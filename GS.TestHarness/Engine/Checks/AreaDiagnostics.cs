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
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using ASCOM.Common.DeviceInterfaces;
using Microsoft.Win32;

namespace GS.TestHarness.Engine.Checks;

/// <summary>
/// Tier 2 of the ASCOM Conformance Check for the problems that aren't measurements (<see cref="Tiers"/>): each
/// diagnosis takes one kind of tier 1 problem, tests around it (the other values, the sibling members, the states
/// around it) to find the core issue, and reviews the code involved when the server's source is on this computer
/// (<see cref="SourceLocator.Body"/>, <see cref="SourceLocator.Search"/>): the guards a member calls, lines commented
/// out, where a setting is used. Each finding says what the pattern points to and what to change. The harness only
/// recommends: it never changes the server.
/// </summary>
public static class AreaDiagnostics
{
    public const string Arguments = "Arguments";
    public const string Preconditions = "Preconditions";
    public const string NotConnected = "NotConnected";
    public const string Registration = "Registration";

    /// <summary>The diagnoses made here (by their <see cref="Diagnosis.Member"/>); the measurements are <see cref="Diagnostics"/>'.</summary>
    public static readonly IReadOnlySet<string> Areas = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { Arguments, Preconditions, "SlewSettleTime", "AxisRates", "DriverVersion", Registration, NotConnected };

    /// <summary>
    /// Whether a diagnosis moves the mount (it needs Move the mount): the measurements, the preconditions and the
    /// settle time do; the arguments, the connection, AxisRates, DriverVersion and the registration don't.
    /// </summary>
    public static bool MovesMount(string member) => !Areas.Contains(member) || member is Preconditions or "SlewSettleTime";

    /// <summary>Runs one area's diagnosis, against what the run it follows up found (<see cref="CheckContext.FollowsUp"/>).</summary>
    public static async Task RunAsync(CheckContext ctx, string area)
    {
        var found = ctx.FollowsUp is { } from ? Tiers.ProblemsWith(from, area) : [];
        var said = Tiers.Said(ctx.FollowsUp, area);
        switch (area)
        {
            case Arguments: await ArgumentsAsync(ctx, found, said); break;
            case Preconditions: await PreconditionsAsync(ctx, found, said); break;
            case NotConnected: await NotConnectedAsync(ctx, found, said); break;
            case "SlewSettleTime":
                if (ctx.Options.Tier >= 3) await SettleDeepAsync(ctx, said);
                else await SettleAsync(ctx, said);
                break;
            case "AxisRates": await AxesAsync(ctx, said); break;
            case "DriverVersion": await DriverVersionAsync(ctx, said); break;
            case Registration:
                if (OperatingSystem.IsWindows()) await RegistrationAsync(ctx, said);
                else ctx.Report("Registration", "COM registration", Outcome.Skipped, "The COM registration can only be diagnosed on Windows.", expected: said);
                break;
        }
    }

    // ---------- Reviewing the code ----------

    /// <summary>The member's code, or null (the source isn't here, or the member isn't in it).</summary>
    private static IReadOnlyList<SourceLine>? Code(CheckContext ctx, string member) => ctx.Locator.Body(ctx.Target.Codebase, member);

    private const string NoSource = "Code not reviewed: the server's source isn't on this computer (set it under Options → Source code).";

    /// <summary>The helpers a member's code calls (CheckSerialIO, CheckRange...), in order: the guards it has.</summary>
    internal static List<string> Calls(IReadOnlyList<SourceLine> body)
        => [.. body.Skip(1).Where(l => !l.IsComment)
            .SelectMany(l => Regex.Matches(SourceLocator.StripComment(l.Text), @"\b((?:Check|Validate|Ensure|Need|Throw|Verify)\w*)\s*\(")
                .Select(m => m.Groups[1].Value))
            .Distinct()];

    /// <summary>The member's lines (code or comments) that match <paramref name="pattern"/>.</summary>
    internal static List<SourceLine> Lines(IReadOnlyList<SourceLine> body, string pattern, bool comments)
        => [.. body.Skip(1).Where(l => l.IsComment == comments && Regex.IsMatch(l.Text, pattern))];

    private static string Quote(SourceLine l) => $"{l.Location}: {l.Text.Trim()}";

    private static string Quote(IEnumerable<SourceLine> lines) => string.Join("\n", lines.Select(l => "  " + Quote(l)));

    private static string List(IEnumerable<string> items) => string.Join(", ", items);

    // ---------- Arguments ----------

    /// <summary>A member that takes an axis or a guide direction: its valid values and a call with one.</summary>
    private sealed record ArgumentProbe(string Member, string Parameter, int[] Valid, int[] Invalid, Func<ITelescopeV4, int, string> Call);

    /// <summary>What a call that returned a null collection answers (over COM the client library fails on it instead).</summary>
    private const string ReturnedNull = "returned null";

    private static readonly ArgumentProbe[] ArgumentProbes =
    [
        new("CanMoveAxis", "axis", [0, 1, 2], [-1, 3, 9, 99], (t, v) => CheckContext.Show(t.CanMoveAxis((TelescopeAxis)v))),
        new("AxisRates", "axis", [0, 1, 2], [-1, 3, 9, 99],
            (t, v) => Values.Rates(t.AxisRates((TelescopeAxis)v)) is { } r ? $"{r.Count} rate{(r.Count == 1 ? "" : "s")}" : ReturnedNull),
        // A pulse of no length: nothing moves even if a direction is accepted.
        new("PulseGuide", "direction", [0, 1, 2, 3], [-1, 4, 9, 99], (t, v) => { t.PulseGuide((GuideDirection)v, 0); return ""; })
    ];

    /// <summary>How the member answered one value.</summary>
    private enum Answer { Accepted, Null, InvalidValue, WrongAscomError, FromMountCode, OtherError }

    private static (Answer Answer, string Text) Classify<T>(Call<T> call)
    {
        if (call.Ok)
            return call.Value is ReturnedNull ? (Answer.Null, ReturnedNull)
                : (Answer.Accepted, call.Value is null or "" ? "accepted" : $"accepted ({call.Value})");
        var error = call.Error!;
        // Over COM, a null AxisRates collection makes the ASCOM client library fail as it wraps it.
        if (error is NullReferenceException && error.StackTrace?.Contains("ASCOM.", StringComparison.Ordinal) == true)
            return (Answer.Null, "returned null (the ASCOM client library failed on it: NullReferenceException)");
        if (call.Kind == AscomErrorKind.InvalidValue) return (Answer.InvalidValue, "InvalidValueException");
        // A .NET exception from below the driver, passed on (GSServer wraps them in DriverException).
        var inner = Regex.Match(error.Message, @"System\.(\w+Exception)");
        if (inner.Success && inner.Groups[1].Value is not "NullReferenceException")
            return (Answer.FromMountCode, $"refused with {inner.Groups[1].Value} from the mount code");
        return call.Kind is AscomErrorKind.Other ? (Answer.OtherError, call.ErrorText) : (Answer.WrongAscomError, call.ErrorText);
    }

    private static async Task ArgumentsAsync(CheckContext ctx, List<CheckResult> found, string said)
    {
        var parked = (await ctx.TryAsync(t => t.AtPark)).Value;
        var results = new Dictionary<string, List<(int Value, bool Valid, Answer Answer, string Text)>>();
        foreach (var probe in ArgumentProbes)
        {
            if (probe.Member == "PulseGuide" && (!ctx.Caps.CanPulseGuide || parked)) continue;
            var rows = new List<(int, bool, Answer, string)>();
            foreach (var (value, valid) in probe.Invalid.Select(v => (v, false)).Concat(probe.Valid.Select(v => (v, true))))
            {
                var (answer, text) = Classify(await ctx.TryAsync(t => probe.Call(t, value)));
                rows.Add((value, valid, answer, text));
            }
            results[probe.Member] = rows;
        }
        if (ctx.Caps.CanPulseGuide) await ctx.WaitUntilAsync(t => !t.IsPulseGuiding, TimeSpan.FromSeconds(5), 100);

        // The members whose invalid values aren't all refused with InvalidValueException.
        var wrong = results.Where(r => r.Value.Any(x => !x.Valid && x.Answer != Answer.InvalidValue)).Select(r => r.Key).ToList();
        foreach (var member in found.Select(r => r.Member).Distinct())
        {
            var probe = ArgumentProbes.FirstOrDefault(p => p.Member == member);
            if (probe is null || !results.TryGetValue(member, out var rows))
            {
                ctx.Report($"Arguments.{member}", member, Outcome.Skipped,
                    probe is null ? $"{member}'s arguments have no diagnosis yet." : $"{member} couldn't be called here (the mount is parked, or can't pulse guide).",
                    expected: said);
                continue;
            }
            var invalid = rows.Where(r => !r.Valid).ToList();
            var refusedValid = rows.Where(r => r.Valid && r.Answer != Answer.Accepted).ToList();
            var validText = probe.Parameter == "axis" ? "0, 1 or 2 (TelescopeAxis)" : "0 to 3 (GuideDirection)";
            string pattern;
            if (invalid.All(r => r.Answer == Answer.InvalidValue))
                pattern = $"every invalid {probe.Parameter} is now refused with InvalidValueException";
            else if (invalid.All(r => r.Answer == Answer.Accepted))
                pattern = $"{member} doesn't check the {probe.Parameter} at all: every invalid value is accepted";
            else if (invalid.Any(r => r.Answer == Answer.Null))
                pattern = $"{member} returns null for an invalid {probe.Parameter} instead of refusing it, so a client gets nothing it can use (over COM the client library fails with NullReferenceException)";
            else if (invalid.Any(r => r.Answer == Answer.FromMountCode))
                pattern = $"{member} doesn't check the {probe.Parameter}: the value reaches the mount code, whose own exception comes back ({invalid.First(r => r.Answer == Answer.FromMountCode).Text})";
            else
                pattern = $"{member} refuses invalid values with the wrong error ({List(invalid.Select(r => r.Text).Distinct())})";
            var ok = invalid.All(r => r.Answer == Answer.InvalidValue) && refusedValid.Count == 0;

            var actual = new StringBuilder();
            actual.AppendLine($"{member}({probe.Parameter}):");
            foreach (var r in rows) actual.AppendLine($"  {r.Value,3}{(r.Valid ? " (valid)" : "")}: {r.Text}");
            var others = wrong.Where(w => w != member).ToList();
            if (others.Count > 0) actual.AppendLine($"The same kind of argument is unchecked in: {List(others)}.");

            // The code: what it checks first, and where a null comes from.
            var body = Code(ctx, member);
            var fix = new StringBuilder($"At the top of {member}, before anything uses it: if the {probe.Parameter} isn't {validText}, " +
                                        "throw InvalidValueException (0x401)");
            fix.Append(probe.Parameter == "axis" ? " (for example `if (axis < 0 || axis > 2) throw new InvalidValueException(...)`)." : " (for example `if (!Enum.IsDefined(direction)) throw new InvalidValueException(...)`).");
            if (body is null) actual.AppendLine(NoSource);
            else
            {
                var calls = Calls(body);
                var nulls = Lines(body, @"return\s+null", comments: false);
                var checks = Lines(body, @"InvalidValueException|Enum\.IsDefined|IsDefined\(", comments: false);
                actual.AppendLine($"Code reviewed ({body[0].Location}): {(calls.Count > 0 ? $"it calls {List(calls)}" : "it calls no guard")}" +
                                  $"{(checks.Count == 0 ? $"; nothing checks the {probe.Parameter} (no InvalidValueException or Enum.IsDefined)" : "")}.");
                if (nulls.Count > 0)
                {
                    actual.AppendLine("It returns null:");
                    actual.AppendLine(Quote(nulls));
                    fix.Append($" Replace the `return null` ({nulls[0].Location}) with that throw.");
                }
            }
            if (invalid.Any(r => r.Answer == Answer.FromMountCode))
                fix.Append($" Check it in the driver rather than letting the mount code's exception through: clients get a DriverException they can't tell from a fault.");
            if (others.Count > 0)
                fix.Append($" {List(others)} {(others.Count == 1 ? "needs" : "need")} the same check: one shared helper (as CheckRange is for numbers) keeps them alike.");
            if (refusedValid.Count > 0)
                fix.Append($" It also refuses valid values ({List(refusedValid.Select(r => $"{r.Value}: {r.Text}"))}): those must work.");

            ctx.Report($"Arguments.{member}", member, ok ? Outcome.Info : Outcome.Fail,
                $"{member}: {pattern}.", expected: said + $"\n\nExpected: InvalidValueException for any {probe.Parameter} that isn't {validText}.",
                actual: actual.ToString().TrimEnd(), fix: ok ? null : fix.ToString());
        }
    }

    // ---------- Preconditions ----------

    /// <summary>A member that must refuse while tracking is off (or the mount is slewing), called at the current position.</summary>
    private sealed record GuardedCall(string Member, bool Can, Action<ITelescopeV4, double, double> Call);

    private static async Task PreconditionsAsync(CheckContext ctx, List<CheckResult> found, string said)
    {
        var c = ctx.Caps;
        if (!ctx.Options.AllowMotion || (await ctx.TryAsync(t => t.AtPark)).Value)
        {
            foreach (var member in found.Select(r => r.Member).Distinct())
                ctx.Report($"Preconditions.{member}", member, Outcome.Skipped,
                    ctx.Options.AllowMotion ? "The mount is parked, so the conditions weren't tested." : "Testing the conditions moves the mount: turn on Move the mount under Options and run again.",
                    expected: said);
            return;
        }
        var notTracking = found.Any(r => r.Id.EndsWith(".NotTracking", StringComparison.Ordinal));
        var whileSlewing = found.Any(r => r.Id.EndsWith(".WhileSlewing", StringComparison.Ordinal));
        GuardedCall[] family =
        [
            new("SlewToCoordinates", c.CanSlew, (t, ra, dec) => t.SlewToCoordinates(ra, dec)),
            new("SlewToCoordinatesAsync", c.CanSlewAsync, (t, ra, dec) => t.SlewToCoordinatesAsync(ra, dec)),
            new("SlewToTarget", c.CanSlew, (t, ra, dec) => { t.TargetRightAscension = ra; t.TargetDeclination = dec; t.SlewToTarget(); }),
            new("SlewToTargetAsync", c.CanSlewAsync, (t, ra, dec) => { t.TargetRightAscension = ra; t.TargetDeclination = dec; t.SlewToTargetAsync(); }),
            new("SyncToCoordinates", c.CanSync, (t, ra, dec) => t.SyncToCoordinates(ra, dec)),
            new("SyncToTarget", c.CanSync, (t, ra, dec) => { t.TargetRightAscension = ra; t.TargetDeclination = dec; t.SyncToTarget(); }),
            new("PulseGuide", c.CanPulseGuide, (t, _, _) => t.PulseGuide(GuideDirection.North, 300))
        ];

        // Tracking off: each member of the family, at the current position (so one that wrongly slews barely moves).
        var offRows = new Dictionary<string, (bool Refused, string Text)>();
        var offSummary = new Dictionary<string, string>(); // what an accepted call did, for the finding's sentence
        var wasTracking = (await ctx.TryAsync(t => t.Tracking)).Value;
        if (notTracking && c.CanSetTracking)
        {
            try
            {
                foreach (var g in family.Where(g => g.Can))
                {
                    if (!(await ctx.TryAsync(t => t.Tracking = false)).Ok || (await ctx.TryAsync(t => t.Tracking)).Value)
                    {
                        offRows[g.Member] = (false, "not tested: tracking couldn't be turned off");
                        continue;
                    }
                    if (await MotionChecks.PositionAsync(ctx) is not { } here) continue;
                    var call = await ctx.TryAsync(t => g.Call(t, here.Ra, here.Dec), ctx.SlewTimeout);
                    if (call.Kind == AscomErrorKind.NotImplemented) { offRows[g.Member] = (true, "not implemented here"); continue; }
                    if (!call.Ok)
                    {
                        offRows[g.Member] = (call.Kind == AscomErrorKind.InvalidOperation,
                            call.Kind == AscomErrorKind.InvalidOperation ? "refused (InvalidOperationException)" : $"refused with the wrong error: {call.ErrorText}");
                        continue;
                    }
                    await ctx.WaitForSlewEndAsync();
                    await ctx.WaitUntilAsync(t => !t.IsPulseGuiding, TimeSpan.FromSeconds(5), 100);
                    var after = await MotionChecks.PositionAsync(ctx);
                    var trackingNow = (await ctx.TryAsync(t => t.Tracking)).Value;
                    var moved = after is { } a ? Math.Abs(a.Dec - here.Dec) * 3600 : double.NaN;
                    offRows[g.Member] = (false, "accepted" + (trackingNow ? "; it turned tracking on" : "; tracking stayed off")
                        + (double.IsNaN(moved) ? "" : FormattableString.Invariant($"; Dec moved {moved:0.0}\"")));
                    offSummary[g.Member] = "accepted" + (trackingNow ? " and turned tracking on" : "")
                        + (moved > 1 ? FormattableString.Invariant($", moving the mount {moved:0.0}\"") : "");
                }
            }
            finally
            {
                await MotionChecks.StopAsync(ctx);
                await ctx.TryAsync(t => t.Tracking = wasTracking);
            }
        }

        // During a slew: a pulse, and whether the slew still ends where it was sent.
        string? slewingRow = null;
        var slewingRefused = true;
        if (whileSlewing && c.CanSlewAsync && c.CanPulseGuide && (await ctx.TryAsync(t => t.SiderealTime)) is { Ok: true } lst
            && await MotionChecks.PositionAsync(ctx) is { } start)
        {
            var far = Values.Wrap24(lst.Value + (Values.HoursDiff(start.Ra, lst.Value) > 0 ? -2 : 2));
            var dec = MethodChecks.SafeDeclination(ctx);
            if ((await ctx.TryAsync(t => t.SlewToCoordinatesAsync(far, dec))).Ok && (await ctx.TryAsync(t => t.Slewing)).Value)
            {
                var pulse = await ctx.TryAsync(t => t.PulseGuide(GuideDirection.North, 300));
                slewingRefused = pulse.Kind == AscomErrorKind.InvalidOperation;
                await ctx.WaitForSlewEndAsync();
                var end = await MotionChecks.PositionAsync(ctx);
                var off = end is { } e ? Math.Abs(e.Dec - dec) * 3600 : double.NaN;
                slewingRow = (pulse.Ok ? "accepted" : slewingRefused ? "refused (InvalidOperationException)" : $"refused with the wrong error: {pulse.ErrorText}")
                             + (double.IsNaN(off) ? "" : FormattableString.Invariant($"; the slew ended {off:0.0}\" from its target Dec"));
            }
            else slewingRow = "not tested: the slew ended before a pulse could be sent";
            await MotionChecks.StopAsync(ctx);
        }

        foreach (var member in found.Select(r => r.Member).Distinct())
        {
            var actual = new StringBuilder();
            var problems = new List<string>();
            var fix = new StringBuilder();
            var body = Code(ctx, member);
            var memberNotTracking = notTracking && found.Any(r => r.Member == member && r.Id.EndsWith(".NotTracking", StringComparison.Ordinal));
            var memberSlewing = whileSlewing && found.Any(r => r.Member == member && r.Id.EndsWith(".WhileSlewing", StringComparison.Ordinal));

            if (memberNotTracking && offRows.Count > 0)
            {
                actual.AppendLine("With tracking off, at the current position:");
                foreach (var (m, row) in offRows) actual.AppendLine($"  {m}: {row.Text}");
                var refusing = offRows.Where(r => r.Value.Refused && !r.Value.Text.StartsWith("not implemented")).Select(r => r.Key).ToList();
                var accepting = offRows.Where(r => !r.Value.Refused && !r.Value.Text.StartsWith("not tested")).Select(r => r.Key).ToList();
                if (offRows.TryGetValue(member, out var mine) && !mine.Refused)
                {
                    var did = offSummary.GetValueOrDefault(member, "accepted");
                    problems.Add(refusing.Count > 0
                        ? $"with tracking off it is {did}, while {List(refusing)} refuse" +
                          (accepting.Count == 1 ? $": only {member} lacks the guard" : $": {List(accepting)} lack the guard")
                        : $"with tracking off it is {did}, and so is every sibling: no member checks tracking");
                    fix.Append($"At the top of {member}, throw InvalidOperationException (0x40B) when Tracking is false");
                    // The guard the refusing siblings call and this one doesn't.
                    var siblingCalls = refusing.Where(r => r != member).Select(r => Code(ctx, r)).OfType<IReadOnlyList<SourceLine>>().Select(Calls).ToList();
                    if (body is not null && siblingCalls.Count > 0)
                    {
                        // The tracking guard they share (not every helper they happen to call).
                        var shared = siblingCalls.Aggregate((a, b) => [.. a.Intersect(b)]);
                        var missing = shared.Except(Calls(body)).Where(n => n.Contains("Track", StringComparison.OrdinalIgnoreCase)).ToList();
                        if (missing.Count > 0)
                        {
                            fix.Append($": call {List(missing)} as {List(refusing.Where(r => r != member))} do");
                            actual.AppendLine($"Code reviewed: {List(refusing.Where(r => r != member))} call {List(missing)}; {member} ({body[0].Location}) doesn't.");
                        }
                        var tracking = Lines(body, @"Tracking", comments: false);
                        if (tracking.Count > 0)
                        {
                            actual.AppendLine($"{member}'s own code about tracking:");
                            actual.AppendLine(Quote(tracking));
                        }
                    }
                    if (did.Contains("turned tracking on"))
                        fix.Append($". It turns tracking on instead: a client that turned tracking off doesn't expect a slew to change it, and the standard says to refuse");
                    fix.Append(". ");
                }
            }
            if (memberSlewing && slewingRow is not null)
            {
                actual.AppendLine($"PulseGuide during a slew: {slewingRow}.");
                if (!slewingRefused)
                {
                    problems.Add($"during a slew it is {slewingRow.Split(';')[0]}");
                    fix.Append("At the top of PulseGuide, throw InvalidOperationException (0x40B) while Slewing is true. ");
                }
            }
            if (body is not null && member == "PulseGuide")
            {
                var commented = Lines(body, @"Slewing|IsSlewing|Tracking", comments: true);
                if (commented.Count > 0)
                {
                    actual.AppendLine("Code reviewed: the guards are there but commented out:");
                    actual.AppendLine(Quote(commented));
                    fix.Append("The guards are already written but commented out" +
                               (commented.Any(l => l.Text.Contains("InvalidValueException")) ? " (and they throw InvalidValueException: use InvalidOperationException, the error for the wrong state)" : "") +
                               $": put them back ({commented[0].Location}). ");
                }
            }
            if (body is null) actual.AppendLine(NoSource);
            var untested = (memberNotTracking && !offRows.ContainsKey(member))
                           || (memberSlewing && (slewingRow is null || slewingRow.StartsWith("not tested", StringComparison.Ordinal)));
            var ok = problems.Count == 0;
            var outcome = !ok ? Outcome.Fail : untested ? Outcome.Skipped : Outcome.Info;
            ctx.Report($"Preconditions.{member}", member, outcome,
                !ok ? $"{member}: {string.Join("; ", problems)}."
                    : untested ? $"{member} couldn't be tested in the state tier 1 found the problem in (tracking can't be set, or the slew ended too soon)."
                    : $"{member} now refuses in the states tier 1 tested.",
                expected: said + "\n\nExpected: InvalidOperationException (0x40B) when Tracking is false (equatorial slews, syncs and PulseGuide) or while Slewing (PulseGuide).",
                actual: actual.ToString().TrimEnd(), fix: ok ? null : fix.ToString().Trim());
        }
    }

    // ---------- NotConnected ----------

    private static readonly (string Member, Func<ITelescopeV4, object?> Call)[] Operational =
    [
        ("RightAscension", t => t.RightAscension), ("Declination", t => t.Declination), ("Altitude", t => t.Altitude),
        ("Azimuth", t => t.Azimuth), ("SiderealTime", t => t.SiderealTime), ("UTCDate", t => t.UTCDate),
        ("Tracking", t => t.Tracking), ("TrackingRate", t => t.TrackingRate), ("Slewing", t => t.Slewing),
        ("AtPark", t => t.AtPark), ("AtHome", t => t.AtHome), ("IsPulseGuiding", t => t.IsPulseGuiding),
        ("SideOfPier", t => t.SideOfPier), ("SiteLatitude", t => t.SiteLatitude), ("SlewSettleTime", t => t.SlewSettleTime),
        ("GuideRateRightAscension", t => t.GuideRateRightAscension), ("AlignmentMode", t => t.AlignmentMode),
        ("EquatorialSystem", t => t.EquatorialSystem), ("CanSlew", t => t.CanSlew), ("CanPark", t => t.CanPark),
        ("CanPulseGuide", t => t.CanPulseGuide), ("CanSetTracking", t => t.CanSetTracking),
        ("CanMoveAxis", t => t.CanMoveAxis(TelescopeAxis.Primary)), ("AxisRates", t => Values.Rates(t.AxisRates(TelescopeAxis.Primary))?.Count),
        ("TrackingRates", t => Values.Rates(t.TrackingRates)?.Count)
    ];

    private static async Task NotConnectedAsync(CheckContext ctx, List<CheckResult> found, string said)
    {
        const string id = "NotConnected";
        var rows = new List<(string Member, bool Refused, string Text)>();
        var disconnect = await ctx.TryAsync(t => t.Disconnect(), TimeSpan.FromSeconds(30));
        try
        {
            if (!disconnect.Ok)
            {
                ctx.Report(id, "Connected", Outcome.Error, "This client couldn't disconnect, so the members weren't tried disconnected.",
                    expected: said, actual: disconnect.ErrorText);
                return;
            }
            await ctx.WaitUntilAsync(t => !t.Connecting, TimeSpan.FromSeconds(30), 100);
            if ((await ctx.TryAsync(t => t.Connected)) is { Ok: true, Value: true })
            {
                ctx.Report(id, "Connected", Outcome.Skipped,
                    "Connected is still true after this client disconnected: another client holds the hardware connection (Connected reports the hardware state), " +
                    "so its disconnected state can't be tried. Close the other clients (or restart the server) and run again.",
                    expected: said);
                return;
            }
            foreach (var (member, call) in Operational)
            {
                var r = await ctx.TryAsync(call);
                rows.Add((member, r.Kind == AscomErrorKind.NotConnected,
                    r.Ok ? $"answered {CheckContext.Show(r.Value)}" : r.Kind == AscomErrorKind.NotConnected ? "refused (NotConnectedException)" : $"refused with {r.ErrorText}"));
            }
        }
        finally
        {
            await ctx.TryAsync(t => t.Connect(), TimeSpan.FromSeconds(30));
            await ctx.WaitUntilAsync(t => !t.Connecting, TimeSpan.FromSeconds(30), 100);
        }

        var answered = rows.Where(r => !r.Refused).ToList();
        var refused = rows.Where(r => r.Refused).ToList();
        var actual = new StringBuilder();
        actual.AppendLine($"While this client was disconnected, {answered.Count} of {rows.Count} members answered:");
        foreach (var r in rows) actual.AppendLine($"  {r.Member}: {r.Text}");
        var pattern = answered.Count == 0 ? "every operational member now refuses while disconnected"
            : refused.Count == 0 ? "no member checks the connection: the missing check is common to all of them, not a few members"
            : $"{refused.Count} member{(refused.Count == 1 ? "" : "s")} refuse ({List(refused.Select(r => r.Member))}); the other {answered.Count} have no connection check";

        var fix = new StringBuilder("Add one guard, called first in every operational member (everything but Connected, Connecting, Connect, " +
                                    "Disconnect, Name, Description, DriverInfo, DriverVersion, InterfaceVersion and SupportedActions): " +
                                    "throw NotConnectedException (0x407) when the device isn't connected, that is whenever Connected reads false.");
        var bodies = found.Select(r => r.Member).Distinct().Select(m => (Member: m, Body: Code(ctx, m))).Where(x => x.Body is not null).ToList();
        if (bodies.Count == 0) actual.AppendLine(NoSource);
        else
        {
            actual.AppendLine("Code reviewed:");
            foreach (var (member, body) in bodies)
            {
                var calls = Calls(body!);
                var mentions = Lines(body!, @"Connected|NotConnected", comments: false);
                actual.AppendLine($"  {member} ({body![0].Location}): {(calls.Count > 0 ? $"calls {List(calls)}" : "calls no guard")}; " +
                                  (mentions.Count == 0 ? "nothing checks the connection." : $"mentions the connection at {List(mentions.Select(l => l.Location))}."));
            }
            var thrown = ctx.Locator.Search(ctx.Target.Codebase, @"new\s+NotConnectedException", 10);
            if (thrown.Count > 0)
            {
                actual.AppendLine($"NotConnectedException is thrown in {thrown.Count} place{(thrown.Count == 1 ? "" : "s")}, none of them a check at the top of the members above:");
                actual.AppendLine(Quote(thrown));
            }
            var first = bodies.Select(b => Calls(b.Body!).FirstOrDefault()).OfType<string>().GroupBy(x => x).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
            if (first is not null) fix.Append($" The members already start with {first}: a CheckConnected beside it, called the same way, fits the code.");
        }
        ctx.Report(id, "Connected", answered.Count == 0 ? Outcome.Info : Outcome.Fail, $"Disconnected: {pattern}.",
            expected: said + "\n\nExpected: NotConnectedException (0x407) from every operational member while this client is disconnected.",
            actual: actual.ToString().TrimEnd(), fix: answered.Count == 0 ? null : fix.ToString(),
            reference: CheckContext.DocsBase + "Connected");
    }

    // ---------- SlewSettleTime ----------

    private static async Task SettleAsync(CheckContext ctx, string said)
    {
        const string id = "SlewSettleTime";
        if (!ctx.Options.AllowMotion || !ctx.Caps.CanSlewAsync || (await ctx.TryAsync(t => t.AtPark)).Value)
        {
            ctx.Report(id, id, Outcome.Skipped,
                !ctx.Options.AllowMotion ? "Timing the settle moves the mount: turn on Move the mount under Options and run again."
                    : !ctx.Caps.CanSlewAsync ? "The mount can't slew asynchronously, so the settle couldn't be timed." : "The mount is parked, so the settle wasn't timed.",
                expected: said);
            return;
        }
        var original = await ctx.TryAsync(t => t.SlewSettleTime);
        var rows = new List<(short Set, string ReadBack, double? Settle, double? Total)>();
        try
        {
            foreach (short seconds in new short[] { 0, 2, 5 })
            {
                var set = await ctx.TryAsync(t => t.SlewSettleTime = seconds);
                var back = await ctx.TryAsync(t => t.SlewSettleTime);
                var readBack = !set.Ok ? $"couldn't be set: {set.ErrorText}" : back.Ok ? back.Value.ToString(CultureInfo.InvariantCulture) : back.ErrorText;
                var (settle, total) = await TimeSettleAsync(ctx);
                rows.Add((seconds, readBack, settle, total));
            }
        }
        finally
        {
            if (original.Ok) await ctx.TryAsync(t => t.SlewSettleTime = original.Value);
        }

        var actual = new StringBuilder();
        actual.AppendLine("Short slews (1° in Dec), Slewing's end against when the position stopped changing:");
        foreach (var r in rows)
            actual.AppendLine(FormattableString.Invariant($"  set {r.Set} s, reads back {r.ReadBack}: Slewing ended {(r.Settle is { } s ? $"{s:0.0} s" : "?")} after the mount stopped (slew {(r.Total is { } t ? $"{t:0.0} s" : "?")})"));
        // What each setting adds to the wait with none: the server may have its own wait at the end of every slew
        // (GSServer's is about 2 s), which isn't the setting's.
        var baseline = rows.FirstOrDefault(r => r.Set == 0).Settle;
        var timed = rows.Where(r => r.Settle is not null && r.Set > 0).ToList();
        double Extra((short Set, string ReadBack, double? Settle, double? Total) r) => r.Settle!.Value - (baseline ?? 0);
        var notKept = rows.Where(r => r.ReadBack != r.Set.ToString(CultureInfo.InvariantCulture)).ToList();
        string pattern;
        var ok = false;
        var own = baseline is > 0.6 ? FormattableString.Invariant($" (Slewing always ends {baseline:0.0} s after the mount stops, even with 0: that's the server's own end of a slew, not the setting)") : "";
        if (notKept.Count > 0) pattern = $"the setting isn't kept ({List(notKept.Select(r => $"{r.Set} reads back {r.ReadBack}"))})";
        else if (timed.Count == 0 || baseline is null) pattern = "the settle couldn't be timed (the slews didn't finish)";
        else if (timed.All(r => Extra(r) < 0.6)) pattern = "the setting is kept but never used: it adds nothing to when Slewing ends, whatever its value" + own;
        else if (timed.All(r => Math.Abs(Extra(r) - r.Set) <= 1)) { pattern = "Slewing now stays true for the settle time" + own; ok = true; }
        else pattern = $"the settle time is used, but not as seconds ({List(timed.Select(r => FormattableString.Invariant($"{r.Set} s added {Extra(r):0.0} s")))})" + own;

        var fix = new StringBuilder("After the mount stops, keep Slewing true for SlewSettleTime seconds before clearing it.");
        var uses = ctx.Locator.Search(ctx.Target.Codebase, @"SlewSettleTime", 20)
            .Where(l => !l.IsComment && !Regex.IsMatch(l.Text, @"\b(?:public|private|internal)\b.*\bSlewSettleTime\b\s*(?:$|\{|=>)")).ToList();
        if (uses.Count == 0 && ctx.Locator.RootOf(ctx.Target.Codebase) is null) actual.AppendLine(NoSource);
        else if (uses.Count > 0)
        {
            actual.AppendLine("Code reviewed: where the settle time is used:");
            actual.AppendLine(Quote(uses));
            // TimeSpan.Milliseconds is the milliseconds part (0 to 999), not the length: 0 for any whole number of seconds.
            var part = uses.Where(l => Regex.IsMatch(l.Text, @"FromSeconds\([^)]*SlewSettleTime[^)]*\)\.Milliseconds\b")).ToList();
            if (part.Count > 0)
            {
                actual.AppendLine("These wait TimeSpan.FromSeconds(SlewSettleTime).Milliseconds: the milliseconds part of the time span, which is 0 for any whole number of seconds (SlewSettleTime is a whole number), so the wait is always 0:");
                actual.AppendLine(Quote(part));
                fix.Append($" The delay is there ({List(part.Select(l => l.Location))}) but uses `.Milliseconds` (the 0-999 part of the time span): use `.TotalMilliseconds` (cast to int), or wait on the TimeSpan itself.");
            }
        }
        ctx.Report(id, id, ok ? Outcome.Info : Outcome.Fail, $"SlewSettleTime: {pattern}.",
            expected: said + "\n\nExpected: Slewing stays true for about the settle time after the mount stops.",
            actual: actual.ToString().TrimEnd(), fix: ok ? null : fix.ToString());
    }

    /// <summary>
    /// A short slew, sampled every 100 ms until Slewing goes false and a second after: how long after the position's
    /// last change Slewing ended, and the slew's length. Null when the slew couldn't be made or didn't end.
    /// </summary>
    private static async Task<(double? Settle, double? Total)> TimeSettleAsync(CheckContext ctx)
    {
        if (await MotionChecks.PositionAsync(ctx) is not { } here) return (null, null);
        var dec = Math.Clamp(here.Dec + (here.Dec > 0 ? -1 : 1), -89, 89);
        var watch = Stopwatch.StartNew();
        if (!(await ctx.TryAsync(t => t.SlewToCoordinatesAsync(here.Ra, dec))).Ok) return (null, null);
        var samples = new List<(double T, double Dec)>();
        double? ended = null;
        while (watch.Elapsed < ctx.SlewTimeout && (ended is null || watch.Elapsed.TotalSeconds < ended + 1))
        {
            var d = await ctx.TryAsync(t => t.Declination);
            var slewing = await ctx.TryAsync(t => t.Slewing);
            if (d.Ok) samples.Add((watch.Elapsed.TotalSeconds, d.Value));
            if (ended is null && slewing is { Ok: true, Value: false }) ended = watch.Elapsed.TotalSeconds;
            await Task.Delay(100);
        }
        if (ended is null || samples.Count == 0) return (null, null);
        var final = samples[^1].Dec;
        var moving = samples.Where(s => Math.Abs(s.Dec - final) * 3600 > 1).ToList();
        var lastMove = moving.Count > 0 ? moving[^1].T : samples[0].T;
        return (Math.Max(0, ended.Value - lastMove), ended);
    }

    // ---------- SlewSettleTime, tier 3 ----------

    /// <summary>One position reading during a slew: when (s since the slew started), where, and whether Slewing was still true.</summary>
    private readonly record struct Sample(double T, double Ra, double Dec, bool Slewing);

    /// <summary>
    /// How a slew ended, from its readings: when it got within 1" of where it stopped (the main slew over), when the
    /// position last moved at all (a final correction creeps by fractions of an arc-second), and when Slewing went
    /// false. <see cref="Fine"/> is the creeping after the main slew, <see cref="Idle"/> the time Slewing stayed true
    /// with the mount still, <see cref="Wait"/> both: Slewing's end after the main slew.
    /// </summary>
    private sealed record SlewEnd(double Total, double MainEnd, double LastMove, double SlewingEnd, double LargestFineStep)
    {
        public double Fine => Math.Max(0, Math.Min(LastMove, SlewingEnd) - MainEnd);
        public double Idle => Math.Max(0, SlewingEnd - Math.Max(LastMove, MainEnd));
        public double Wait => Math.Max(0, SlewingEnd - MainEnd);

        /// <summary>How long the position still moved after Slewing went false (it should be 0).</summary>
        public double MovedAfter => Math.Max(0, LastMove - SlewingEnd);
    }

    /// <summary>
    /// How much the readings jump with the mount still (tracking): the largest step between readings, in arc-seconds,
    /// for RA (scaled by cos Dec) and Dec. GSServer's RA reading jitters by a fraction of an arc-second, which mustn't
    /// be taken for movement.
    /// </summary>
    private static async Task<(double Ra, double Dec)> ReadingNoiseAsync(CheckContext ctx)
    {
        var samples = new List<Sample>();
        for (var i = 0; i < 20; i++)
        {
            var ra = await ctx.TryAsync(t => t.RightAscension);
            var dec = await ctx.TryAsync(t => t.Declination);
            if (ra.Ok && dec.Ok) samples.Add(new Sample(0, ra.Value, dec.Value, false));
            await Task.Delay(50);
        }
        double raMax = 0, decMax = 0;
        for (var i = 1; i < samples.Count; i++)
        {
            var (r, d) = Steps(samples[i], samples[i - 1]);
            raMax = Math.Max(raMax, r);
            decMax = Math.Max(decMax, d);
        }
        return (raMax, decMax);
    }

    /// <summary>The step between two readings, in arc-seconds: RA (scaled by cos Dec) and Dec apart.</summary>
    private static (double Ra, double Dec) Steps(Sample a, Sample b)
        => (Math.Abs(Values.HoursDiff(a.Ra, b.Ra) * 15 * Math.Cos(b.Dec * Math.PI / 180) * 3600), Math.Abs(a.Dec - b.Dec) * 3600);

    /// <summary>Angular distance in arc-seconds between two readings (RA hours scaled by cos Dec).</summary>
    private static double Arcsec(Sample a, Sample b)
    {
        var dRa = Values.HoursDiff(a.Ra, b.Ra) * 15 * Math.Cos(b.Dec * Math.PI / 180);
        return Math.Sqrt(dRa * dRa + (a.Dec - b.Dec) * (a.Dec - b.Dec)) * 3600;
    }

    /// <summary>
    /// Starts <paramref name="start"/> (a slew) and reads the position and Slewing as fast as the mount answers until
    /// Slewing has been false for 1.5 s; null when the slew couldn't be started or didn't end. (Tracking is on, so RA
    /// holds still once the mount does.) <paramref name="during"/>, if given, is called a second into the slew.
    /// </summary>
    private static async Task<SlewEnd?> TrackSlewAsync(CheckContext ctx, Action<ITelescopeV4> start, (double Ra, double Dec) noise, Func<Task>? during = null)
    {
        var watch = Stopwatch.StartNew();
        if (!(await ctx.TryAsync(start)).Ok) return null;
        var samples = new List<Sample>();
        double? ended = null;
        var duringDone = during is null;
        while (watch.Elapsed < ctx.SlewTimeout && (ended is null || watch.Elapsed.TotalSeconds < ended + 1.5))
        {
            if (!duringDone && watch.Elapsed.TotalSeconds > 1) { duringDone = true; await during!(); }
            var ra = await ctx.TryAsync(t => t.RightAscension);
            var dec = await ctx.TryAsync(t => t.Declination);
            var slewing = await ctx.TryAsync(t => t.Slewing);
            var now = watch.Elapsed.TotalSeconds;
            if (ra.Ok && dec.Ok && slewing.Ok) samples.Add(new Sample(now, ra.Value, dec.Value, slewing.Value));
            if (ended is null && slewing is { Ok: true, Value: false }) ended = now;
            await Task.Delay(50);
        }
        if (ended is null || samples.Count < 3) return null;
        var final = samples[^1];
        var mainEnd = samples.LastOrDefault(s => Arcsec(s, final) > 1).T;
        // After the main slew, the last reading still away from where the mount stopped by more than the readings'
        // own jumps with the mount still (twice them, at least 0.05"), axis by axis. Measured from where it stopped
        // rather than step by step, so it doesn't depend on how fast the mount answers.
        var raLimit = Math.Max(0.05, 2 * noise.Ra);
        var decLimit = Math.Max(0.05, 2 * noise.Dec);
        var lastMove = mainEnd;
        var largest = 0.0;
        foreach (var s in samples.Where(s => s.T > mainEnd))
        {
            var (ra, dec) = Steps(s, final);
            if (ra > raLimit || dec > decLimit) { lastMove = s.T; largest = Math.Max(largest, Math.Max(ra, dec)); }
        }
        return new SlewEnd(ended.Value, mainEnd, lastMove, ended.Value, largest);
    }

    /// <summary>
    /// Tier 3: why Slewing stays true for a while after a slew whatever SlewSettleTime is. Short and long slews in each
    /// axis with no settle time, one with 3 s, and a slew stopped by AbortSlew, each read finely enough to see a final
    /// correction creeping by fractions of an arc-second; then where the code waits, and the final analysis.
    /// </summary>
    private static async Task SettleDeepAsync(CheckContext ctx, string said)
    {
        const string id = "SlewSettleTime.Cause";
        if (!ctx.Options.AllowMotion || !ctx.Caps.CanSlewAsync || (await ctx.TryAsync(t => t.AtPark)).Value)
        {
            ctx.Report(id, "SlewSettleTime", Outcome.Skipped,
                !ctx.Options.AllowMotion ? "The tier 3 slews move the mount: turn on Move the mount under Options and run again."
                    : !ctx.Caps.CanSlewAsync ? "The mount can't slew asynchronously, so the end of a slew couldn't be timed." : "The mount is parked, so nothing was timed.",
                expected: said);
            return;
        }
        var original = await ctx.TryAsync(t => t.SlewSettleTime);
        var rows = new List<(string Name, short Settle, SlewEnd? End)>();
        double? abortEnd = null;
        // The readings' own jumps with the mount still, so they aren't taken for a slew still creeping.
        var noise = await ReadingNoiseAsync(ctx);
        try
        {
            async Task Slew(string name, short settle, double raHours, double decDegrees)
            {
                await ctx.TryAsync(t => t.SlewSettleTime = settle);
                if (await MotionChecks.PositionAsync(ctx) is not { } here) { rows.Add((name, settle, null)); return; }
                // Towards the equator, so a long slew stays clear of the pole.
                var dec = Math.Clamp(here.Dec + (here.Dec > 0 ? -decDegrees : decDegrees), -85, 85);
                var ra = Values.Wrap24(here.Ra + raHours);
                rows.Add((name, settle, await TrackSlewAsync(ctx, t => t.SlewToCoordinatesAsync(ra, dec), noise)));
            }
            await Slew("Dec 0.5°", 0, 0, 0.5);
            await Slew("Dec 2°", 0, 0, 2);
            await Slew("Dec 8°", 0, 0, 8);
            await Slew("RA 2° (8 min)", 0, 8.0 / 60, 0);
            await Slew("Dec 2°, SlewSettleTime 3", 3, 0, 2);

            // A slew stopped part way: does Slewing end at once, or after the same wait?
            await ctx.TryAsync(t => t.SlewSettleTime = 0);
            if (await MotionChecks.PositionAsync(ctx) is { } from)
            {
                var dec = Math.Clamp(from.Dec + (from.Dec > 0 ? -15 : 15), -85, 85);
                var abortedAt = 0.0;
                var watch = Stopwatch.StartNew();
                var end = await TrackSlewAsync(ctx, t => t.SlewToCoordinatesAsync(from.Ra, dec), noise,
                    async () => { abortedAt = watch.Elapsed.TotalSeconds; await ctx.TryAsync(t => t.AbortSlew()); });
                if (end is not null && abortedAt > 0) abortEnd = Math.Max(0, end.SlewingEnd - abortedAt);
            }
        }
        finally
        {
            if (original.Ok) await ctx.TryAsync(t => t.SlewSettleTime = original.Value);
            await ctx.WaitForSlewEndAsync();
        }

        var actual = new StringBuilder();
        actual.AppendLine(FormattableString.Invariant($"With the mount still, the readings jump up to {noise.Ra:0.00}\" in RA and {noise.Dec:0.00}\" in Dec: smaller steps aren't counted as movement."));
        actual.AppendLine("Each slew, read every ~0.1 s: the main slew over (within 1\" of where it stopped), the last movement, and Slewing's end:");
        foreach (var (name, settle, end) in rows)
            actual.AppendLine(end is null ? $"  {name}: couldn't be timed"
                : FormattableString.Invariant($"  {name}: slew {end.MainEnd:0.0} s; then creeping {end.Fine:0.0} s (up to {end.LargestFineStep:0.00}\" from where it stopped); then still {end.Idle:0.0} s; Slewing ended {end.Wait:0.0} s after the main slew")
                  + (end.MovedAfter > 0.2 ? FormattableString.Invariant($"; the position still moved {end.MovedAfter:0.0} s after Slewing ended") : ""));
        if (abortEnd is { } a) actual.AppendLine(FormattableString.Invariant($"  AbortSlew part way through a 15° slew: Slewing ended {a:0.0} s after the abort"));

        var plain = rows.Where(r => r.Settle == 0 && r.End is not null).Select(r => r.End!).ToList();
        var two = rows.FirstOrDefault(r => r.Name == "Dec 2°").End;
        var withSettle = rows.FirstOrDefault(r => r.Settle == 3).End;
        if (plain.Count == 0 || two is null || withSettle is null)
        {
            ctx.Report(id, "SlewSettleTime", Outcome.Error, "The slews couldn't be timed, so the end of a slew wasn't taken apart.", expected: said,
                actual: actual.ToString().TrimEnd());
            return;
        }
        var wait = plain.Average(e => e.Wait);
        var fine = plain.Average(e => e.Fine);
        var idle = plain.Average(e => e.Idle);
        var spread = plain.Max(e => e.Wait) - plain.Min(e => e.Wait);
        var movedAfter = plain.Max(e => e.MovedAfter);
        var extra = withSettle.Wait - two.Wait;
        if (Math.Abs(extra) < 0.05) extra = 0; // no "-0.0 s"
        var used = Math.Abs(extra - 3) <= 1;
        var correcting = fine >= 0.5 && fine >= idle;

        var findings = new List<string>
        {
            used ? FormattableString.Invariant($"SlewSettleTime now works: 3 s added {extra:0.0} s")
                 : FormattableString.Invariant($"SlewSettleTime is ignored: 3 s added {extra:0.0} s to when Slewing ends")
        };
        if (wait >= 0.6)
            findings.Add(FormattableString.Invariant($"the {wait:0.0} s Slewing stays true after every slew, with no settle time, is ") +
                         (correcting
                             ? FormattableString.Invariant($"a final correction, not a wait: the position still creeps (up to {plain.Max(e => e.LargestFineStep):0.00}\" from where it stops) for {fine:0.0} s after the main slew")
                             : FormattableString.Invariant($"a wait with the mount still ({idle:0.0} s after the last movement)")) +
                         (spread <= 1 ? ", the same for every length and axis" : FormattableString.Invariant($", varying by {spread:0.0} s with the slew")));
        if (movedAfter > 0.3)
            findings.Add(FormattableString.Invariant($"Slewing goes false while the mount is still moving (for up to {movedAfter:0.0} s after)"));
        if (abortEnd is { } ab)
            findings.Add(ab < 0.6 ? FormattableString.Invariant($"an aborted slew ends at once ({ab:0.0} s), so the wait belongs to finishing a slew, not to Slewing itself")
                                  : FormattableString.Invariant($"even an aborted slew keeps Slewing true {ab:0.0} s"));

        // The code: where the settle wait is, and whether a later stage of the slew comes after it.
        var fix = new StringBuilder();
        var delays = ctx.Locator.Search(ctx.Target.Codebase, @"Delay\w*\(.*SlewSettleTime|SlewSettleTime.*(Delay|Sleep|Wait)", 10).Where(l => !l.IsComment).ToList();
        var stages = ctx.Locator.Search(ctx.Target.Codebase, @"(?i)precision\s*(slew|goto)|PrecisionGoto\(", 20);
        if (delays.Count == 0 && ctx.Locator.RootOf(ctx.Target.Codebase) is null) actual.AppendLine(NoSource);
        var before = new List<(SourceLine Delay, SourceLine Stage)>();
        if (delays.Count > 0)
        {
            actual.AppendLine("Code reviewed: the settle waits:");
            actual.AppendLine(Quote(delays));
            foreach (var d in delays)
                if (stages.FirstOrDefault(s => s.File == d.File && s.Line > d.Line && s.Line - d.Line <= 150) is { } stage) before.Add((d, stage));
            if (before.Count > 0)
            {
                actual.AppendLine("A later stage of the slew comes after the settle wait:");
                actual.AppendLine(Quote(before.Select(b => b.Stage)));
            }
        }
        if (!used)
        {
            var part = delays.Where(l => Regex.IsMatch(l.Text, @"\.Milliseconds\b")).ToList();
            fix.Append(part.Count > 0
                ? $"1. Make the settle wait real: it waits TimeSpan.FromSeconds(SlewSettleTime).Milliseconds, the 0-999 ms part, so 0 for whole seconds; use .TotalMilliseconds ({List(part.Select(l => l.Location))}). "
                : "1. Wait SlewSettleTime seconds after the mount stops, before Slewing goes false. ");
        }
        if (before.Count > 0)
            fix.Append($"{(used ? 1 : 2)}. Move the settle wait after the final stage of the slew ({List(before.Select(b => $"{b.Stage.Location}, after {b.Delay.Location}"))}): " +
                       "where it is, it runs between the main slew and the precision correction, so the mount isn't still yet when it starts, " +
                       "and the correction then adds its own time. After the last stage, the settle time starts when the mount is truly still. ");
        if (movedAfter > 0.3)
            fix.Append(FormattableString.Invariant($"Slewing went false up to {movedAfter:0.0} s before the position stopped changing: clear it only after the slew's last stage (and the settle time). "));
        if (correcting)
            fix.Append(FormattableString.Invariant($"The {wait:0.0} s itself is the slew's final correction: Slewing true during it is right (the mount is still moving), and clients see the settle time on top of it."));
        else if (wait >= 0.6)
            fix.Append(FormattableString.Invariant($"Find what keeps Slewing true {idle:0.0} s after the mount is still: a fixed wait at the end of every slew would be better as part of SlewSettleTime."));

        var outcome = used && !(wait >= 0.6 && !correcting) && movedAfter <= 0.3 ? Outcome.Info : Outcome.Fail;
        ctx.Report(id, "SlewSettleTime", outcome, $"Tier 3: {string.Join("; ", findings)}.",
            expected: said, actual: actual.ToString().TrimEnd(), fix: fix.Length > 0 ? fix.ToString().Trim() : null);
        SettleFinalAnalysis(ctx, outcome, string.Join("; ", findings), fix.ToString().Trim(), used);
    }

    /// <summary>Tier 3's last word on SlewSettleTime: tier 2's finding and recommendation against what tier 3 measured.</summary>
    private static void SettleFinalAnalysis(CheckContext ctx, Outcome outcome, string found, string fix, bool used)
    {
        var tier2 = ctx.FollowsUp?.Results.FirstOrDefault(r => r.Id == "Diagnose.SlewSettleTime");
        var expected = tier2 is null ? "Tier 2's SlewSettleTime diagnosis isn't in the run this one follows up."
            : $"Tier 2 (Diagnose.SlewSettleTime, {tier2.Outcome}): {tier2.Message}" + (tier2.Fix is { } f ? $"\nTier 2's recommendation: {f}" : "");
        var tier2SaidIgnored = tier2?.Message.Contains("never used", StringComparison.Ordinal) == true
                               || tier2?.Message.Contains("adds nothing", StringComparison.Ordinal) == true;
        var verdict = tier2 is null ? "stands alone"
            : !used && tier2.Outcome == Outcome.Fail ? (tier2SaidIgnored ? "confirms tier 2" : "corrects tier 2 (the setting isn't used at all; the wait tier 2 measured is the slew's own)")
            : used && tier2.Outcome == Outcome.Fail ? "finds SlewSettleTime working now (fixed since tier 2)"
            : "agrees with tier 2";
        ctx.Report("SlewSettleTime.Summary", "SlewSettleTime", outcome,
            $"Final analysis of SlewSettleTime: tier 3 {verdict}. {char.ToUpperInvariant(found[0])}{found[1..]}.",
            expected: expected, actual: found, fix: string.IsNullOrWhiteSpace(fix) ? null : fix);
    }

    // ---------- AxisRates ----------

    private static async Task AxesAsync(CheckContext ctx, string said)
    {
        const string id = "AxisRates.Axes";
        var rows = new List<(TelescopeAxis Axis, string Can, List<(double Minimum, double Maximum)>? Rates, string Text)>();
        foreach (var axis in new[] { TelescopeAxis.Primary, TelescopeAxis.Secondary, TelescopeAxis.Tertiary })
        {
            var can = await ctx.TryAsync(t => t.CanMoveAxis(axis));
            var rates = await ctx.TryAsync(t => Values.Rates(t.AxisRates(axis)));
            rows.Add((axis, can.Ok ? CheckContext.Show(can.Value) : can.ErrorText, rates.Ok ? rates.Value : null,
                rates.Ok ? rates.Value is { } r ? $"[{string.Join(", ", r.Select(x => FormattableString.Invariant($"{x.Minimum:0.####}..{x.Maximum:0.####}")))}]" : "null" : rates.ErrorText));
        }
        var wrong = rows.Where(r => r.Can == "False" && r.Rates is { Count: > 0 }).ToList();
        var sameForAll = rows.All(r => r.Rates is { Count: > 0 }) && rows.Select(r => string.Join(";", r.Rates!)).Distinct().Count() == 1;
        var actual = new StringBuilder();
        foreach (var r in rows) actual.AppendLine($"  {r.Axis}: CanMoveAxis {r.Can}, AxisRates {r.Text}");
        var pattern = wrong.Count == 0 ? "AxisRates now matches CanMoveAxis for every axis"
            : $"AxisRates gives rates for {List(wrong.Select(r => r.Axis.ToString()))}, which CanMoveAxis says can't move" +
              (sameForAll ? ": the same rates for every axis, whatever CanMoveAxis says" : "");
        var fix = new StringBuilder("At the top of AxisRates, return an empty collection when CanMoveAxis(axis) is false (the collection must " +
                                    "be empty for an axis that can't be moved, not left out or null).");
        var body = Code(ctx, "AxisRates");
        if (body is null) actual.AppendLine(NoSource);
        else
        {
            var returns = Lines(body, @"return\b", comments: false);
            actual.AppendLine($"Code reviewed ({body[0].Location}): {(Lines(body, @"CanMoveAxis", comments: false).Count == 0 ? "AxisRates never asks CanMoveAxis" : "AxisRates asks CanMoveAxis")}; it returns:");
            actual.AppendLine(Quote(returns));
            var build = ctx.Locator.Search(ctx.Target.Codebase, @"class\s+AxisRates\b", 2);
            if (build.Count > 0)
            {
                actual.AppendLine($"The collection is built by {Quote(build[0])}.");
                fix.Append($" Or have the AxisRates class ({build[0].Location}) leave out the rates of an axis that can't move.");
            }
        }
        ctx.Report(id, "AxisRates", wrong.Count == 0 ? Outcome.Info : Outcome.Fail, $"AxisRates: {pattern}.",
            expected: said + "\n\nExpected: an empty AxisRates collection for each axis CanMoveAxis says can't move.",
            actual: actual.ToString().TrimEnd(), fix: wrong.Count == 0 ? null : fix.ToString());
    }

    // ---------- DriverVersion ----------

    private static async Task DriverVersionAsync(CheckContext ctx, string said)
    {
        const string id = "DriverVersion";
        var version = await ctx.TryAsync(t => t.DriverVersion);
        var info = await ctx.TryAsync(t => t.DriverInfo);
        if (!version.Ok)
        {
            ctx.Report(id, id, Outcome.Error, "DriverVersion couldn't be read.", expected: said, actual: version.ErrorText);
            return;
        }
        var v = version.Value?.Trim() ?? "";
        var ok = Regex.IsMatch(v, @"^\d+\.\d+$");
        var parts = v.Split('.');
        var majorMinor = parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : v;
        var actual = new StringBuilder();
        actual.AppendLine($"DriverVersion: {v}");
        actual.AppendLine($"DriverInfo: {(info.Ok ? info.Value : info.ErrorText)}");
        var inInfo = info.Ok && info.Value?.Contains(v, StringComparison.Ordinal) == true;
        string? fileVersion = null;
        if (OperatingSystem.IsWindows() && ctx.Target.Kind == TargetKind.Com && ServerProgram(ctx.Target.ProgId) is { } exe && File.Exists(exe))
        {
            var fv = FileVersionInfo.GetVersionInfo(exe);
            fileVersion = fv.FileVersion;
            actual.AppendLine($"The driver program ({Path.GetFileName(exe)}): file version {fv.FileVersion}, product version {fv.ProductVersion}");
        }
        var source = fileVersion is not null && v == fileVersion?.Trim() ? "the program's whole file (assembly) version" : "a full build version";
        var fix = new StringBuilder($"Return only the major and minor version from DriverVersion (\"{majorMinor}\").");
        var body = Code(ctx, "DriverVersion");
        if (body is null) actual.AppendLine(NoSource);
        else
        {
            var lines = Lines(body, @"Version|return", comments: false).Where(l => !Regex.IsMatch(l.Text, @"Monitor|CheckVersion")).ToList();
            actual.AppendLine($"Code reviewed ({body[0].Location}):");
            actual.AppendLine(Quote(lines));
            var toString = lines.FirstOrDefault(l => Regex.IsMatch(l.Text, @"Version\.ToString\(\s*\)"));
            if (toString is not null) fix.Append($" At {toString.Location}, `Version.ToString()` gives all four parts: `Version.ToString(2)` gives major.minor.");
        }
        fix.Append(inInfo ? $" DriverInfo already carries {v}." : $" Put the full {v} in DriverInfo, where clients and support can still see it.");
        ctx.Report(id, id, ok ? Outcome.Info : Outcome.Warning,
            ok ? $"DriverVersion is now major.minor ({v})." : $"DriverVersion returns {source}, {v}, not major.minor.",
            expected: said + "\n\nExpected: \"n.n\" (the ITelescopeV4 documentation: \"must be in the form 'n.n'\").",
            actual: actual.ToString().TrimEnd(), fix: ok ? null : fix.ToString());
    }

    // ---------- Registration ----------

    /// <summary>The COM server's program for a ProgID, from the registry (either view), or null.</summary>
    [SupportedOSPlatform("windows")]
    private static string? ServerProgram(string progId)
    {
        if (Clsid(progId) is not { } clsid) return null;
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            if (ServerIn(clsid, view) is { } server && ExecutableOf(server.Command) is { } exe) return exe;
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static string? Clsid(string progId)
    {
        using var key = Registry.ClassesRoot.OpenSubKey($@"{progId}\CLSID");
        return key?.GetValue(null) as string;
    }

    [SupportedOSPlatform("windows")]
    private static (string Kind, string Command)? ServerIn(string clsid, RegistryView view)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            foreach (var type in new[] { "LocalServer32", "InprocServer32" })
            {
                using var k = root.OpenSubKey($@"Software\Classes\CLSID\{clsid}\{type}");
                if (k?.GetValue(null) is string s && s.Length > 0) return (type, s);
            }
        }
        return null;
    }

    private static string? ExecutableOf(string command) => RegistrationChecks.ExecutableOf(command);

    /// <summary>
    /// What a program runs as, from its PE header: "x86 (32-bit)", "x64 (64-bit)", or for .NET "AnyCPU", "AnyCPU, 32-bit
    /// preferred" or "x86 (.NET, 32-bit required)". Null if it can't be read.
    /// </summary>
    internal static string? Bitness(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var r = new BinaryReader(stream);
            stream.Position = 0x3C;
            var pe = r.ReadInt32();
            stream.Position = pe;
            if (r.ReadUInt32() != 0x00004550) return null; // "PE\0\0"
            var machine = r.ReadUInt16();
            var sections = r.ReadUInt16();
            stream.Position = pe + 20;
            var optionalSize = r.ReadUInt16();
            var optional = pe + 24;
            stream.Position = optional;
            var magic = r.ReadUInt16(); // 0x10B PE32, 0x20B PE32+
            var directories = optional + (magic == 0x20B ? 112 : 96);
            stream.Position = directories + 14 * 8; // the CLR runtime header
            var clrRva = r.ReadUInt32();
            if (clrRva == 0) return machine switch { 0x14C => "x86 (32-bit)", 0x8664 => "x64 (64-bit)", 0xAA64 => "ARM64", _ => $"machine 0x{machine:X}" };
            // Find the section holding the CLR header, to read its flags.
            var table = optional + optionalSize;
            for (var i = 0; i < sections; i++)
            {
                stream.Position = table + i * 40 + 8;
                var virtualSize = r.ReadUInt32();
                var virtualAddress = r.ReadUInt32();
                r.ReadUInt32();
                var rawPointer = r.ReadUInt32();
                if (clrRva < virtualAddress || clrRva >= virtualAddress + virtualSize) continue;
                stream.Position = rawPointer + (clrRva - virtualAddress) + 16;
                var flags = r.ReadUInt32();
                var required = (flags & 0x2) != 0;
                var preferred = (flags & 0x20000) != 0;
                if (magic == 0x20B || machine == 0x8664) return "x64 (.NET, 64-bit)";
                return required && preferred ? "AnyCPU, 32-bit preferred (.NET: runs as 32-bit)"
                    : required ? "x86 (.NET, 32-bit required)" : "AnyCPU (.NET: 64-bit on 64-bit Windows)";
            }
            return ".NET (flags unreadable)";
        }
        catch (Exception) { return null; }
    }

    /// <summary>Creates the driver (without connecting it) in a process of the other bitness, through PowerShell; null on success, else why not.</summary>
    [SupportedOSPlatform("windows")]
    private static async Task<string?> CreateInOtherBitnessAsync(string progId)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var shell = Environment.Is64BitProcess
            ? Path.Combine(windows, "SysWOW64", "WindowsPowerShell", "v1.0", "powershell.exe")
            : Path.Combine(windows, "Sysnative", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(shell)) return $"no {(Environment.Is64BitProcess ? "32" : "64")}-bit PowerShell to try it with";
        var start = new ProcessStartInfo(shell,
            $"-NoProfile -NonInteractive -Command \"$o = New-Object -ComObject '{progId}'; [void][Runtime.InteropServices.Marshal]::ReleaseComObject($o); 'created'\"")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var process = Process.Start(start);
        if (process is null) return "PowerShell didn't start";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch { /* gone */ }
            return "no answer in 60 s";
        }
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        return output.Contains("created") ? null : (error.Trim().Split('\n').FirstOrDefault()?.Trim() is { Length: > 0 } e ? e : $"exit code {process.ExitCode}");
    }

    /// <summary>Creates the driver (without connecting it) in this process; null on success, else why not.</summary>
    [SupportedOSPlatform("windows")]
    private static async Task<string?> CreateHereAsync(string progId)
    {
        try
        {
            return await Task.Run(() =>
            {
                var type = Type.GetTypeFromProgID(progId, throwOnError: true)!;
                var o = Activator.CreateInstance(type);
                if (o is not null && System.Runtime.InteropServices.Marshal.IsComObject(o)) System.Runtime.InteropServices.Marshal.ReleaseComObject(o);
                return (string?)null;
            }).WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (Exception ex) { return AscomError.Describe(ex); }
    }

    [SupportedOSPlatform("windows")]
    private static async Task RegistrationAsync(CheckContext ctx, string said)
    {
        const string id = "Registration";
        if (ctx.Target.Kind != TargetKind.Com || ctx.Session.Transport != "COM")
        {
            ctx.Report(id, "COM registration", Outcome.Skipped, "Not a COM driver here, so there's no COM registration to diagnose.", expected: said);
            return;
        }
        var progId = ctx.Target.ProgId;
        if (!Regex.IsMatch(progId, @"^[\w.]+$") || Clsid(progId) is not { } clsid)
        {
            ctx.Report(id, "COM registration", Outcome.Error, $"The ProgID {progId} has no CLSID, so its registration can't be followed.", expected: said);
            return;
        }
        var actual = new StringBuilder();
        actual.AppendLine($"{progId} → {clsid}");
        var views = new Dictionary<string, (string Kind, string Command)?>();
        foreach (var (view, label) in new[] { (RegistryView.Registry64, "64-bit"), (RegistryView.Registry32, "32-bit") })
        {
            views[label] = ServerIn(clsid, view);
            actual.AppendLine($"  {label} registry view: {(views[label] is { } s ? $"{s.Kind} {s.Command}" : "no server")}");
        }
        using (var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid}"))
            if (key?.GetValue("AppID") is string appId) actual.AppendLine($"  AppID {appId}");
        var exe = views.Values.OfType<(string Kind, string Command)>().Select(s => ExecutableOf(s.Command)).FirstOrDefault(e => e is not null);
        var bitness = exe is not null && File.Exists(exe) ? Bitness(exe) : null;
        if (exe is not null) actual.AppendLine($"  the program, {Path.GetFileName(exe)}: {bitness ?? "bitness unreadable"}");

        var here = Environment.Is64BitProcess ? "64-bit" : "32-bit";
        var there = Environment.Is64BitProcess ? "32-bit" : "64-bit";
        var created = new Dictionary<string, string?>
        {
            [here] = await CreateHereAsync(progId),
            [there] = await CreateInOtherBitnessAsync(progId)
        };
        foreach (var label in new[] { "64-bit", "32-bit" })
            actual.AppendLine($"  created from a {label} process: {created[label] ?? "yes"}");

        var missing = views.Where(v => v.Value is null).Select(v => v.Key).ToList();
        var failed = created.Where(c => c.Value is not null).Select(c => c.Key).ToList();
        var outcome = failed.Count > 0 ? Outcome.Fail : missing.Count > 0 ? Outcome.Warning : Outcome.Info;
        var message = failed.Count > 0
            ? $"{List(failed)} clients can't create the driver{(missing.Count > 0 ? $": the server isn't registered in the {List(missing)} registry view" : "")}."
            : missing.Count > 0
                ? $"Clients of both bitnesses can create the driver: COM finds the out-of-process server through the {List(views.Where(v => v.Value is not null).Select(v => v.Key))} registration, " +
                  $"so the missing {List(missing)} view doesn't stop them. Only tools that read the {List(missing)} registry view themselves see no server."
                : "The server is registered in both views and both bitnesses can create the driver.";

        var fix = new StringBuilder();
        if (missing.Count > 0)
            fix.Append($"Write the server's CLSID registration in the {List(missing)} registry view too" +
                       (bitness?.Contains("32") == true ? ": the program runs as 32-bit, so its own registry writes land in the 32-bit view only; open the 64-bit view explicitly (RegistryKey.OpenBaseKey(..., RegistryView.Registry64)) when registering" : "") + ".");
        if (failed.Count > 0) fix.Append($" {List(failed)} clients failing to create it is the real problem: fix the registration (and AppID) so COM can find and start the server for them.");
        var code = ctx.Locator.Search(ctx.Target.Codebase, @"LocalServer32|RegistryView\.Registry(32|64)", 10);
        if (code.Count > 0)
        {
            actual.AppendLine("Code reviewed: the registration code:");
            actual.AppendLine(Quote(code));
            if (missing.Contains("64-bit") && !code.Any(l => l.Text.Contains("Registry64")))
                fix.Append(" The registration code never opens RegistryView.Registry64.");
        }
        else if (ctx.Locator.RootOf(ctx.Target.Codebase) is null) actual.AppendLine(NoSource);
        ctx.Report(id, "COM registration", outcome, message,
            expected: said + "\n\nExpected: a server registered for 64-bit and 32-bit clients, and both able to create the driver.",
            actual: actual.ToString().TrimEnd(), fix: outcome == Outcome.Info ? null : fix.ToString().Trim(),
            reference: "https://learn.microsoft.com/windows/win32/com/localserver32");
    }
}
