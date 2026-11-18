/* Copyright(C) 2026  Rob Morgan (robert.morgan.e@gmail.com)

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published
    by the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System.Diagnostics;
using System.Globalization;
using ASCOM.Common.DeviceInterfaces;
using GS.TestHarness.Engine.Checks;

namespace GS.TestHarness.Engine;

/// <summary>The outcome of one call to the mount: its value, or what it raised, and how long it took.</summary>
public sealed record Call<T>(T? Value, Exception? Error, double Ms)
{
    public bool Ok => Error is null;
    public AscomErrorKind Kind => AscomError.Classify(Error).Kind;
    public string ErrorText => AscomError.Describe(Error);
}

/// <summary>The mount's Can* answers, read once by the capability checks and used by the later groups.</summary>
public sealed class Capabilities
{
    public bool CanFindHome, CanPark, CanPulseGuide, CanSetDeclinationRate, CanSetGuideRates, CanSetPark,
        CanSetPierSide, CanSetRightAscensionRate, CanSetTracking, CanSlew, CanSlewAltAz, CanSlewAltAzAsync,
        CanSlewAsync, CanSync, CanSyncAltAz, CanUnpark;

    /// <summary>CanMoveAxis for the primary, secondary and tertiary axes.</summary>
    public bool[] CanMoveAxis = new bool[3];

    public AlignmentMode? AlignmentMode;
    public double? SiteLatitude, SiteLongitude;
}

/// <summary>
/// What a check group works with: the mount, the run's options, and the helpers that call the mount and report
/// findings. Findings about a problem get where to check the code from the <see cref="SourceLocator"/>.
/// </summary>
public sealed class CheckContext(
    ITelescopeSession session,
    TestTarget target,
    RunOptions options,
    SourceLocator locator,
    Action<CheckResult> report,
    CancellationToken token,
    IReadOnlyList<CheckResult>? results = null,
    Func<Task<ITelescopeSession>>? openSession = null,
    RunReport? previous = null)
{
    /// <summary>The findings made so far in this run (for the summary checks).</summary>
    public IReadOnlyList<CheckResult> Results
    {
        get
        {
            if (results is null) return [];
            lock (results) return [.. results];
        }
    }

    /// <summary>Opens another client of the same device (the Clients checks); null if not possible here.</summary>
    public Func<Task<ITelescopeSession>>? OpenSession { get; } = openSession;

    /// <summary>The last saved run of the same device, if any (to compare what must not change, like UniqueID).</summary>
    public RunReport? Previous { get; } = previous;

    /// <summary>The ConformU log the run follows up, if it was started from one (the ConformU checks).</summary>
    public ConformULog? ConformU { get; init; }

    public const string DocsBase = "https://ascom-standards.org/newdocs/telescope.html#Telescope.";

    public ITelescopeSession Session { get; } = session;
    public TestTarget Target { get; } = target;
    public RunOptions Options { get; } = options;
    public SourceLocator Locator { get; } = locator;
    public CancellationToken Token { get; } = token;
    public Capabilities Caps { get; } = new();
    public DeviceIdentity Identity { get; set; } = new();

    /// <summary>The group being run, used as each finding's group.</summary>
    public string Group { get; set; } = "";

    /// <summary>What one group learned that a later one needs ("WasConnected", "TrackingRates"...).</summary>
    public Dictionary<string, object?> Facts { get; } = [];

    public bool Fact(string name) => Facts.TryGetValue(name, out var v) && v is true;

    public TimeSpan CallTimeout => TimeSpan.FromSeconds(Options.CallTimeoutSeconds);
    public TimeSpan SlewTimeout => TimeSpan.FromSeconds(Options.SlewTimeoutSeconds);

    /// <summary>
    /// Whether to stop the run when the device can no longer be reached (set once it has connected: before that
    /// the Connection checks report an unreachable device themselves).
    /// </summary>
    public bool WatchForLostConnection { get; set; }

    /// <summary>Why the device was found to be unreachable, once it has been; every later call then throws.</summary>
    public CommunicationLostException? Lost { get; private set; }

    /// <summary>Timeouts in a row: one slow call isn't a lost device, several are worth a look.</summary>
    private int _timeoutsInARow;

    private const int TimeoutsBeforeProbe = 3;

    /// <summary>
    /// Calls the mount, catching whatever it raises. When the device has gone (its COM server died, the Alpaca
    /// server stopped answering), throws <see cref="CommunicationLostException"/> so the run stops with one clear
    /// finding instead of every remaining check failing on its own.
    /// </summary>
    public async Task<Call<T>> TryAsync<T>(Func<ITelescopeV4, T> call, TimeSpan? timeout = null)
    {
        Token.ThrowIfCancellationRequested();
        if (Lost is not null) throw Lost;
        var watch = Stopwatch.StartNew();
        try
        {
            var value = await Session.RunAsync(call, timeout ?? CallTimeout);
            _timeoutsInARow = 0;
            return new Call<T>(value, null, watch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            var error = AscomError.Unwrap(ex);
            var result = new Call<T>(default, error, watch.Elapsed.TotalMilliseconds);
            var timedOut = error is TimeoutException;
            _timeoutsInARow = timedOut ? _timeoutsInARow + 1 : 0;
            if (WatchForLostConnection && (AscomError.IsConnectionLost(error) || _timeoutsInARow >= TimeoutsBeforeProbe)
                && await ProbeAsync() is { } probeError)
            {
                Lost = new CommunicationLostException(Group,
                    timedOut ? $"{_timeoutsInARow} calls in a row got no answer, and the device no longer answers at all."
                        : "The device can no longer be reached.",
                    $"Last call: {AscomError.Describe(error)}\nCheck call (Connected): {AscomError.Describe(probeError)}");
                throw Lost;
            }
            return result;
        }
    }

    /// <summary>Reads Connected with a short timeout: null if the device answered (whatever the value), else the error.</summary>
    private async Task<Exception?> ProbeAsync()
    {
        try
        {
            await Session.RunAsync(t => t.Connected, TimeSpan.FromSeconds(Math.Min(10, Math.Max(3, Options.CallTimeoutSeconds))));
            _timeoutsInARow = 0;
            return null;
        }
        catch (Exception ex)
        {
            var error = AscomError.Unwrap(ex);
            return AscomError.IsConnectionLost(error) || error is TimeoutException ? error : null;
        }
    }

    public Task<Call<bool>> TryAsync(Action<ITelescopeV4> call, TimeSpan? timeout = null)
        => TryAsync(t => { call(t); return true; }, timeout);

    // ---------- A run narrowed to some members (a ConformU run) ----------

    /// <summary>
    /// Whether the run wants checks of any of these members: always, unless it's narrowed to some
    /// (<see cref="RunOptions.Members"/>). A group skips a section none of whose members is wanted.
    /// </summary>
    public bool Wants(params string[] members)
        => Options.Members is not { } only || members.Any(m => only.Contains(m, StringComparer.OrdinalIgnoreCase));

    /// <summary>The findings that say why checks didn't run or the run stopped: kept in a narrowed run, too.</summary>
    private static readonly HashSet<string> Explains = ["Group", "Prepare", "CommunicationLost", "Stopped", "Unexpected"];

    /// <summary>
    /// Whether a finding belongs in the run: every one, unless the run is narrowed to some members. Then only the
    /// ones about them (by Member, or a part of the ID, as the ConformU verdicts match them), the ConformU checks'
    /// own, the Alpaca protocol's when that's wanted, and the skips and errors that explain a missing check.
    /// </summary>
    private bool Keeps(CheckResult r)
    {
        if (Options.Members is not { } only || r.Group == "ConformU") return true;
        var last = r.Id[(r.Id.LastIndexOf('.') + 1)..];
        if (r.Outcome is Outcome.Skipped or Outcome.Error && Explains.Contains(last)) return true;
        if (r.Group == "Protocol" && only.Contains(ConformUMap.Protocol)) return true;
        return only.Contains(r.Member, StringComparer.OrdinalIgnoreCase)
               || r.Id.Split('.').Skip(1).Any(p => only.Contains(p, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Records a finding. A problem gets the source location of <paramref name="member"/> or of the patterns.</summary>
    public void Report(string id, string member, Outcome outcome, string message,
        string? expected = null, string? actual = null, string? fix = null, string? reference = null,
        double ms = 0, string[]? sourcePatterns = null, string? sourceMember = null)
    {
        var problem = outcome is Outcome.Fail or Outcome.Warning or Outcome.Error;
        var result = new CheckResult
        {
            Id = $"{Group}.{id}",
            Group = Group,
            Member = member,
            Outcome = outcome,
            Message = message,
            Expected = expected,
            Actual = actual,
            Fix = problem ? fix : null,
            Source = problem ? Locator.Find(Target.Codebase, sourceMember ?? member, sourcePatterns ?? []) : null,
            Reference = reference ?? (IsInterfaceMember(member) ? DocsBase + member : null),
            DurationMs = Math.Round(ms, 1)
        };
        if (Keeps(result)) report(result);
    }

    private static bool IsInterfaceMember(string member) => member.Length > 0 && char.IsUpper(member[0]) && member.All(char.IsLetterOrDigit);

    // ---------- Reading properties ----------

    /// <summary>
    /// Reads a property every ITelescopeV4 must implement: passes if it answers (and <paramref name="validate"/>
    /// finds nothing wrong), fails otherwise.
    /// </summary>
    public async Task<Call<T>> ReadMandatoryAsync<T>(string member, Func<ITelescopeV4, T> get, Func<T, string?>? validate = null,
        string? expected = null)
    {
        var call = await TryAsync(get);
        if (!call.Ok)
        {
            ReportReadError(member, call, mandatory: true);
            return call;
        }
        ReportValue(member, call, validate, expected);
        return call;
    }

    /// <summary>
    /// Reads a property a driver may leave out: not implemented is fine (Info), as long as it is reported with
    /// the right error; any other error fails.
    /// </summary>
    public async Task<Call<T>> ReadOptionalAsync<T>(string member, Func<ITelescopeV4, T> get, Func<T, string?>? validate = null,
        string? expected = null)
    {
        var call = await TryAsync(get);
        if (call.Kind == AscomErrorKind.NotImplemented)
        {
            Report($"{member}.Read", member, Outcome.Info, $"{member} is not implemented (allowed).", ms: call.Ms);
            return call;
        }
        if (!call.Ok)
        {
            ReportReadError(member, call, mandatory: false);
            return call;
        }
        ReportValue(member, call, validate, expected);
        return call;
    }

    /// <summary>
    /// Reads a property that must be implemented when <paramref name="required"/> (a capability says so) and may
    /// throw PropertyNotImplementedException otherwise.
    /// </summary>
    public Task<Call<T>> ReadRequiredWhenAsync<T>(string member, bool required, string because, Func<ITelescopeV4, T> get,
        Func<T, string?>? validate = null)
        => required ? ReadMandatoryWithReasonAsync(member, because, get, validate) : ReadOptionalAsync(member, get, validate);

    private async Task<Call<T>> ReadMandatoryWithReasonAsync<T>(string member, string because, Func<ITelescopeV4, T> get,
        Func<T, string?>? validate)
    {
        var call = await TryAsync(get);
        if (call.Kind == AscomErrorKind.NotImplemented)
        {
            Report($"{member}.Read", member, Outcome.Fail, $"{member} is not implemented, but {because}.",
                expected: "a value", actual: call.ErrorText,
                fix: $"Implement {member}: {because}, so clients will read it. Otherwise report the capability as false.",
                ms: call.Ms);
            return call;
        }
        if (!call.Ok)
        {
            ReportReadError(member, call, mandatory: true);
            return call;
        }
        ReportValue(member, call, validate, null);
        return call;
    }

    private void ReportValue<T>(string member, Call<T> call, Func<T, string?>? validate, string? expected)
    {
        var problem = validate?.Invoke(call.Value!);
        if (problem is null)
            Report($"{member}.Read", member, Outcome.Pass, $"{member} = {Show(call.Value)}", expected: expected,
                actual: Show(call.Value), ms: call.Ms);
        else
            Report($"{member}.Read", member, Outcome.Fail, $"{member} = {Show(call.Value)}: {problem}",
                expected: expected, actual: Show(call.Value), ms: call.Ms,
                fix: $"Correct the value {member} returns: {problem}.");
    }

    private void ReportReadError<T>(string member, Call<T> call, bool mandatory)
    {
        if (call.Kind == AscomErrorKind.Timeout)
        {
            Report($"{member}.Read", member, Outcome.Fail, $"{member} did not answer.", expected: "an answer within " +
                $"{Options.CallTimeoutSeconds} s", actual: call.ErrorText, ms: call.Ms,
                fix: $"{member} must answer quickly: a property read should not wait for the mount to finish anything. " +
                     "Look for a lock or a blocking serial command in its getter.");
            return;
        }
        if (call.Kind == AscomErrorKind.NotImplemented && mandatory)
        {
            Report($"{member}.Read", member, Outcome.Fail, $"{member} is not implemented, but ITelescopeV4 requires it.",
                expected: "a value", actual: call.ErrorText, ms: call.Ms,
                fix: $"Implement {member}; it is mandatory and must not throw PropertyNotImplementedException.");
            return;
        }
        Report($"{member}.Read", member, Outcome.Fail, $"{member} raised an error.",
            expected: mandatory ? "a value" : "a value, or PropertyNotImplementedException (0x400)",
            actual: call.ErrorText, ms: call.Ms,
            fix: call.Kind == AscomErrorKind.Other
                ? $"{member} raised a non-ASCOM error ({call.Error!.GetType().Name}). Find the fault; if the property " +
                  "really can't answer, throw an ASCOM exception so clients can tell what went wrong."
                : $"Find why {member} raises {call.Kind}: a connected device should return a value here.");
    }

    // ---------- Expecting errors ----------

    /// <summary>
    /// Calls something that must raise <paramref name="expected"/> (or one of <paramref name="alsoAccepted"/>):
    /// fails if it raises nothing or something else. Returns the call, so a check can tidy up after a call that
    /// wrongly succeeded.
    /// </summary>
    public async Task<Call<bool>> ExpectErrorAsync(string id, string member, string what, Action<ITelescopeV4> call,
        AscomErrorKind expected, string why, AscomErrorKind[]? alsoAccepted = null, bool property = false,
        TimeSpan? timeout = null)
    {
        var result = await TryAsync(call, timeout);
        var accepted = result.Kind == expected || (alsoAccepted?.Contains(result.Kind) ?? false);
        var expectedText = AscomError.ExpectedName(expected, property)
                           + (alsoAccepted is { Length: > 0 } ? " or " + string.Join(" or ", alsoAccepted.Select(k => AscomError.ExpectedName(k, property))) : "");
        if (accepted)
        {
            Report(id, member, Outcome.Pass, $"{what}: refused with {result.Kind}, as expected.",
                expected: expectedText, actual: result.ErrorText, ms: result.Ms);
        }
        else if (result.Ok)
        {
            Report(id, member, Outcome.Fail, $"{what}: accepted, but should have been refused ({why}).",
                expected: expectedText, actual: "no error", ms: result.Ms,
                fix: $"{member} must throw {AscomError.ExpectedName(expected, property)} when {why}. " +
                     $"Add the check at the top of {member}, before anything is sent to the mount.");
        }
        else if (result.Kind == AscomErrorKind.Timeout)
        {
            Report(id, member, Outcome.Fail, $"{what}: no answer.", expected: expectedText, actual: result.ErrorText,
                ms: result.Ms, fix: $"{member} must refuse at once when {why}, not wait.");
        }
        else if (IsHttpBadRequest(result.Error))
        {
            // The Alpaca server's request validation refused the value (HTTP 400) before the driver saw it.
            Report(id, member, Outcome.Fail, $"{what}: refused by the Alpaca server with HTTP 400 instead of {expected}.",
                expected: expectedText, actual: result.ErrorText, ms: result.Ms,
                fix: $"The value never reached the driver: the server's request validation (model binding of the parameter) " +
                     $"answered HTTP 400. A parameter that parses but is out of range is the driver's to judge: let it through " +
                     $"and return HTTP 200 with ErrorNumber 0x{CodeOf(expected):X3}. " +
                     "Keep HTTP 400 for parameters that are missing or can't be parsed.",
                sourcePatterns: ["data validation error", "InvalidModelStateResponseFactory"], sourceMember: "-");
        }
        else
        {
            Report(id, member, Outcome.Fail, $"{what}: refused, but with the wrong error.",
                expected: expectedText, actual: result.ErrorText, ms: result.Ms,
                fix: $"Throw {AscomError.ExpectedName(expected, property)} when {why}, not {result.Kind}. Clients " +
                     $"tell errors apart by their kind (Alpaca ErrorNumber; COM exception or HRESULT), so the wrong one " +
                     "makes them behave wrongly." +
                     (result.Kind == AscomErrorKind.Other ? " A non-ASCOM exception usually means the value reached the " +
                         "mount code unchecked." : ""));
        }
        return result;
    }

    /// <summary>Whether the ASCOM Alpaca client reported an HTTP 400 reply (rather than an ASCOM error).</summary>
    private static bool IsHttpBadRequest(Exception? ex)
        => ex?.Message.Contains("HTTP Completion Status: BadRequest", StringComparison.OrdinalIgnoreCase) == true;

    private static int CodeOf(AscomErrorKind kind) => kind switch
    {
        AscomErrorKind.NotImplemented => 0x400,
        AscomErrorKind.InvalidValue => 0x401,
        AscomErrorKind.ValueNotSet => 0x402,
        AscomErrorKind.NotConnected => 0x407,
        AscomErrorKind.Parked => 0x408,
        AscomErrorKind.Slaved => 0x409,
        AscomErrorKind.InvalidOperation => 0x40B,
        AscomErrorKind.ActionNotImplemented => 0x40C,
        _ => 0x4FF
    };

    /// <summary>
    /// Checks a member that a false capability says isn't there: it must raise not implemented. When the
    /// capability is true, the caller tests the member itself.
    /// </summary>
    public Task<Call<bool>> ExpectNotImplementedAsync(string id, string member, string capability, Action<ITelescopeV4> call,
        bool property = false)
        => ExpectErrorAsync(id, member, $"{member} with {capability} false", call, AscomErrorKind.NotImplemented,
            $"{capability} is false", property: property);

    /// <summary>Polls until <paramref name="done"/> or the timeout; returns whether it got there.</summary>
    public async Task<bool> WaitUntilAsync(Func<ITelescopeV4, bool> done, TimeSpan timeout, int pollMs = 250)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            var call = await TryAsync(done);
            if (call is { Ok: true, Value: true }) return true;
            await Task.Delay(pollMs, Token);
        }
        return false;
    }

    /// <summary>Waits for a slew (or park, find home, axis move) to end.</summary>
    public Task<bool> WaitForSlewEndAsync() => WaitUntilAsync(t => !t.Slewing, SlewTimeout);

    public static string Show(object? value) => value switch
    {
        null => "null",
        double d => d.ToString("0.######", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + (dt.Kind == DateTimeKind.Utc ? "Z" : $" ({dt.Kind})"),
        string s => $"\"{s}\"",
        StateValue sv => $"{sv.Name}={Show(sv.Value)}",
        System.Collections.IEnumerable e => "[" + string.Join(", ", e.Cast<object?>().Select(Show)) + "]",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
    };

    public static string Hours(double hours)
    {
        var ts = TimeSpan.FromHours(hours);
        return $"{(int)Math.Floor(hours):00}h{ts.Minutes:00}m{ts.Seconds:00}s";
    }
}
