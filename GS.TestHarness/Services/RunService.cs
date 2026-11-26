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

using GS.TestHarness.Engine;

namespace GS.TestHarness.Services;

/// <summary>
/// The run under way, shared by every browser tab: one run at a time (two runs would fight over the mount),
/// and a tab opened or refreshed mid-run sees its progress. Each finished run is saved to the
/// <see cref="ReportStore"/>.
/// </summary>
public sealed class RunService(TestRunner runner, ReportStore store, HomeService home, SuggestionStore suggestionStore,
    ConnectionService connections, ServerList servers, ILogger<RunService> log)
{
    internal Func<TestTarget, RunOptions, Task<ITelescopeSession>> OpenSession { get; init; } = TestRunner.OpenAsync;

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private readonly List<CheckResult> _results = [];

    /// <summary>Raised (on a worker thread) whenever the run's state or findings change.</summary>
    public event Action? Changed;

    public bool IsRunning { get; private set; }
    public TestTarget? Target { get; private set; }
    private bool _connected;
    private bool? _wasConnected;
    public bool IsTesting(TestTarget target) => IsRunning && Target is { } current && TestTarget.SameDevice(current, target);
    public bool IsConnectedTo(TestTarget target) => IsTesting(target) && _connected;
    public RunOptions? Options { get; private set; }
    public RunProgress? Progress { get; private set; }
    public string? Status { get; private set; }

    /// <summary>When the run under way (or the last run) started.</summary>
    public DateTimeOffset? StartedAt { get; private set; }

    /// <summary>The last saved run of the same device before this one, to compare with and to estimate how long this will take.</summary>
    public RunReport? PreviousReport { get; private set; }

    /// <summary>What the run is waiting on the user for before it moves the mount (null when it isn't waiting).</summary>
    public GateRequest? PendingGate { get; private set; }

    /// <summary>Set when a chained run has ended (all tiers done, stopped, or couldn't go on); cleared when the next run starts.</summary>
    public FinishedInfo? Finished { get; private set; }

    private readonly List<SuggestedTest> _suggestions = [];

    /// <summary>The run under way carries on through the tiers (and so stops itself when too many important tests fail).</summary>
    public bool IsChained { get; private set; }

    /// <summary>The weights of the failed tests (Fail or Error) of the tier under way so far: what is compared with <see cref="ScoreLimit"/>.</summary>
    public int LiveScore
    {
        get { lock (_gate) return ScoreOf(_results); }
    }

    /// <summary>How many failed tests of the tier under way are ones the run can't go on after (weight <see cref="TestWeights.Critical"/>).</summary>
    public int LiveCritical
    {
        get { lock (_gate) return _results.Count(r => r.Outcome is Outcome.Fail or Outcome.Error && TestWeights.Of(r) >= TestWeights.Critical); }
    }

    /// <summary>What the failed tests' weights may add up to in a tier before it is stopped.</summary>
    public int ScoreLimit => TestWeights.Limit;

    /// <summary>How many weights have been changed from the built-in ones (for the report section).</summary>
    public int AdjustedCount => TestWeights.AdjustedSnapshot()?.Count ?? 0;

    /// <summary>Why the tier under way was stopped for safety (null while it hasn't been).</summary>
    private string? _earlyStop;

    private CancellationTokenSource? _tierCts;

    private static int ScoreOf(IEnumerable<CheckResult> results)
        => results.Where(r => r.Outcome is Outcome.Fail or Outcome.Error).Sum(TestWeights.Of);

    /// <summary>
    /// Called with each finding of a chained run: stops the tier under way (the mount is put home afterwards, as for Stop) as soon as a
    /// test of weight <see cref="TestWeights.Critical"/> fails or the failed weights pass <see cref="ScoreLimit"/>, rather than leaving
    /// it to the end of the tier.
    /// </summary>
    private void CheckSafety(CheckResult r)
    {
        if (!IsChained || r.Outcome is not (Outcome.Fail or Outcome.Error)) return;
        lock (_gate)
        {
            if (_earlyStop is not null) return;
            var why = Tiers.SafetyReason(_results, r);
            if (why is null) return;
            _earlyStop = why;
            Status = "Stopping for safety: " + why;
            _tierCts?.Cancel();
        }
    }
    private readonly List<RunReport> _reports = [];

    private TaskCompletionSource<GateDecision>? _gateTcs;

    /// <summary>
    /// Called from the run before the first group that moves the mount: shows the warnings to whoever has the page open
    /// and waits for Proceed, Skip or Stop. Stopping the run answers Stop.
    /// </summary>
    private async Task<GateDecision> AskGateAsync(IReadOnlyList<string> warnings, CancellationToken token)
    {
        var tcs = new TaskCompletionSource<GateDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new GateRequest(warnings);
        lock (_gate)
        {
            _gateTcs = tcs;
            PendingGate = request;
            Status = "Waiting for you: Proceed, Skip or Stop before the mount moves.";
        }
        Changed?.Invoke();
        await using var _ = token.Register(() => tcs.TrySetResult(GateDecision.Stop));
        try
        {
            var decision = await tcs.Task;
            // Not "Waiting for you" any more: the next progress report replaces this.
            Status = decision == GateDecision.Stop ? "Stopped before the mount moved; making the summary…" : "Going on…";
            return decision;
        }
        finally
        {
            lock (_gate)
            {
                _gateTcs = null;
                PendingGate = null;
            }
            Changed?.Invoke();
        }
    }

    /// <summary>The user's answer to <paramref name="request"/> (ignored if it has already been answered).</summary>
    public void ResolveGate(GateRequest request, GateDecision decision)
    {
        TaskCompletionSource<GateDecision>? tcs;
        lock (_gate) tcs = ReferenceEquals(PendingGate, request) ? _gateTcs : null;
        tcs?.TrySetResult(decision);
    }

    /// <summary>The last run's report, once it has finished.</summary>
    public RunReport? LastReport { get; private set; }

    /// <summary>The ConformU log the run under way (or the last run) follows up; null for an ordinary run.</summary>
    public ConformULog? ConformU { get; private set; }

    public IReadOnlyList<CheckResult> Results
    {
        get { lock (_gate) return [.. _results]; }
    }

    /// <summary>Starts a run; false if one is already under way.</summary>
    /// <param name="followsUp">For a tier 2 or 3 run, the run it follows up (<see cref="Tiers.Options"/> made the options from it).</param>
    /// <param name="chain">Carry on through the deeper tiers for what each finds: the mount goes home after every tier, the next
    /// tier starts by itself, and the mount goes home again at the end.</param>
    public bool Start(TestTarget target, RunOptions options, ConformULog? conformU = null, RunReport? followsUp = null, bool chain = false)
    {
        // Tier 1 always first: a deeper run only follows up the tier below's findings.
        if (Tiers.Problem(options, followsUp, conformU is not null) is { } problem)
        {
            Status = $"Can't run tier {options.Tier}: {problem}";
            Changed?.Invoke();
            return false;
        }
        CancellationToken runToken;
        lock (_gate)
        {
            if (IsRunning) return false;
            IsRunning = true;
            _connected = false;
            _wasConnected = null;
            Target = target;
            Options = options;
            ConformU = conformU;
            Progress = null;
            LastReport = null;
            Status = $"Connecting to {target.Display}…";
            StartedAt = DateTimeOffset.Now;
            PreviousReport = null;
            Finished = null;
            IsChained = chain;
            _earlyStop = null;
            _suggestions.Clear();
            _reports.Clear();
            _results.Clear();
            _cts = new CancellationTokenSource();
            runToken = _cts.Token;
        }
        Changed?.Invoke();
        _ = Task.Run(() => RunOneAsync(target, options, conformU, followsUp, runToken, chain));
        return true;
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!IsRunning) return;
            Status = "Stopping…";
            _cts?.Cancel();
        }
        Changed?.Invoke();
    }

    private async Task RunOneAsync(TestTarget target, RunOptions options, ConformULog? conformU, RunReport? followsUp,
        CancellationToken token, bool chain)
    {
        RunReport? report = null;
        var completed = false;
        string? safetyNote = null;
        ITelescopeSession? session = null;
        var server = servers.FindDevice(target);
        var reserved = false;
        Exception? setupError = null;
        try
        {
            if (server is not null)
            {
                await connections.BeginTestAsync(server.Id);
                reserved = true;
            }
            token.ThrowIfCancellationRequested();
            session = await OpenSession(target, options);
            report = await RunAsync(session, target, options, conformU, followsUp, token);
            completed = !chain && report is { Cancelled: false, CommunicationLost: false };
            if (report is not null) Collect(report);
            // Chained tiers: after each tier the mount goes home and the next tier (for what this one found) starts, with no
            // second safety dialog (the user accepted it at tier 1). IsRunning stays true throughout, so Stop stays at hand.
            while (chain && report is { CommunicationLost: false } && (!report.Cancelled || _earlyStop is not null) && !token.IsCancellationRequested)
            {
                var stoppedEarly = _earlyStop;
                var next = stoppedEarly is null ? Tiers.Next(report) : null;
                var last = next is null && stoppedEarly is null;
                // Too many failures: a mount in this state may not be safe to keep moving, so the deeper tiers aren't run.
                // Each failed test counts for its weight (1 to 10): one that can't fail (the connection, stopping the mount)
                // ends the tiers at once, and the rest add up.
                var score = Tiers.ProblemScore(report);
                var critical = Tiers.CriticalFailures(report);
                var unsafeToGoOn = stoppedEarly is not null || (!last && (critical.Count > 0 || score > Tiers.MaxProblemsToContinue));
                if (stoppedEarly is not null)
                {
                    safetyNote = $"Tier {report.Options.Tier} was stopped part way for safety concerns: {stoppedEarly}. Its report holds what was found up to then, " +
                                 "no deeper tier was run and the mount was sent home. Fix the problems in the report and run the tests again.";
                }
                else if (unsafeToGoOn)
                {
                    var worst = string.Join("; ", Tiers.WeightedProblems(report).Take(3).Select(x => $"{x.Finding.Id} (weight {x.Weight})"));
                    safetyNote = (critical.Count > 0
                            ? $"Tier {report.Options.Tier} failed {critical.Count} test{(critical.Count == 1 ? "" : "s")} the run can't go on after (weight {TestWeights.Critical}): " +
                              string.Join(", ", critical.Take(5).Select(c => c.Id)) + ". "
                            : $"Tier {report.Options.Tier}'s failed tests add up to {score} (the limit is {Tiers.MaxProblemsToContinue}); the heaviest: {worst}. ") +
                         $"Tier {next!.Tier} was not run for safety concerns: the mount may not be behaving as it should. " +
                         "Fix the problems in the report and run the tests again.";
                }
                if (!await GoHomeAsync(session, target, options, last ? "All tests finished" : stoppedEarly is not null ? $"Tier {report.Options.Tier} stopped for safety" : $"Tier {report.Options.Tier} finished", token)) break;
                if (unsafeToGoOn)
                {
                    Status = "Testing stopped for safety concerns" + (options.AllowMotion ? "; the mount is at the home position." : ".");
                    break;
                }
                if (last)
                {
                    completed = true;
                    Status = options.AllowMotion
                        ? "All tests are finished. The mount is at the home position."
                        : "All tests are finished.";
                    break;
                }
                lock (_gate)
                {
                    LastReport = report;
                    options = Tiers.Options(options, next!);
                    Options = options;
                    Progress = null;
                    Status = $"Starting tier {next!.Tier}…";
                    StartedAt = DateTimeOffset.Now;
                    _results.Clear();
                }
                Changed?.Invoke();
                report = await RunAsync(session, target, options, conformU, next!.From, token);
                if (report is not null) Collect(report);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Status = "Stopped before testing started.";
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Run failed for {Target}", target.Display);
            Status = $"Couldn't run: {AscomError.Describe(ex)}";
            if (session is null) setupError = ex;
            lock (_gate) _results.Add(new CheckResult { Id = "Run.Stopped", Group = "Run", Member = target.Display, Outcome = Outcome.Error, Message = Status, Actual = ex.ToString() });
            completed = false;
        }
        finally
        {
            CheckResult connectionResult;
            try
            {
                var retained = false;
                var hardwareConnected = false;
                try
                {
                    if (session is not null && options.KeepConnection && completed && !token.IsCancellationRequested && server is not null
                        && !_reports.Any(r => r.Cancelled || r.CommunicationLost || r.Results.Any(f => f.Outcome == Outcome.Error
                            && (f.Id.EndsWith(".Cleanup", StringComparison.Ordinal) || f.Id.EndsWith(".Stopped", StringComparison.Ordinal)))))
                    {
                        await session.ConnectClientAsync(TimeSpan.FromSeconds(30));
                        connections.EndTest(server.Id, session);
                        reserved = false;
                        retained = true;
                    }
                }
                finally
                {
                    if (session is not null && !retained)
                        hardwareConnected = await session.ReleaseAsync(TimeSpan.FromSeconds(30));
                    _connected = false;
                    if (reserved && server is not null) connections.EndTest(server.Id);
                }
                if (hardwareConnected && _wasConnected == false)
                    throw new InvalidOperationException("The driver still reports Connected true after the last harness client was released, although no client held the hardware when testing started.");
                if (setupError is not null) throw new InvalidOperationException("Connection setup or sidebar release failed; cleanup could not be verified.", setupError);
                if (_reports.Any(r => r.Results.Any(f => f.Outcome == Outcome.Error && f.Id.EndsWith(".Cleanup", StringComparison.Ordinal))))
                    throw new InvalidOperationException("An extra test client could not be released; see the Cleanup findings.");
                var message = retained ? "One connection kept in the sidebar (Keep one connection was selected)."
                    : hardwareConnected ? "Harness connections released. The driver still reports the hardware connected; another client or the server may hold it."
                    : "Harness connections released.";
                connectionResult = new CheckResult { Id = "Run.Connections", Group = "Run", Member = "Disconnect", Outcome = Outcome.Info, Message = message };
                Status = $"{Status} {message}";
            }
            catch (Exception ex)
            {
                _connected = false;
                if (reserved && server is not null) connections.EndTest(server.Id);
                log.LogWarning(ex, "Connection cleanup failed for {Target}", target.Display);
                completed = false;
                var message = $"Connection cleanup failed: {AscomError.Describe(ex)}. Connections may still be active; check the driver.";
                connectionResult = new CheckResult { Id = "Run.Connections", Group = "Run", Member = "Disconnect", Outcome = Outcome.Error, Message = message, Actual = ex.ToString() };
                Status = $"{Status} {message}";
            }
            lock (_gate) _results.Add(connectionResult);
            if (report is not null)
            {
                lock (report.Results) report.Results.Add(connectionResult);
                try { await store.SaveAsync(report); }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Connection cleanup result not saved for {Target}", target.Display);
                    Status = $"{Status} Cleanup result could not be saved: {ex.Message}";
                }
            }
            // Kept for the Suggested Tests page, and marked with how often they were suggested before; dismissed ones aren't shown again.
            List<SuggestedTest> shown;
            var hidden = 0;
            lock (_gate) shown = [.. _suggestions];
            if (chain && shown.Count > 0 && _reports.Count > 0)
            {
                try { (shown, hidden) = await suggestionStore.RecordAsync(shown, _reports[0].Id); }
                catch (Exception ex) { log.LogWarning(ex, "Suggested tests not saved: {Error}", ex.Message); }
            }
            lock (_gate)
            {
                if (chain && _reports.Count > 0)
                    Finished = new FinishedInfo(Status ?? "The run has ended.", !completed, [.. _reports], shown, safetyNote, hidden);
                LastReport = report;
                IsRunning = false;
                _cts?.Dispose();
                _cts = null;
            }
            Changed?.Invoke();
        }
    }

    private void Collect(RunReport report)
    {
        lock (_gate) _reports.Add(report);
        var found = SuggestedTests.Evaluate(report);
        lock (_gate) SuggestedTests.Merge(_suggestions, found);
    }

    /// <summary>
    /// Between chained tiers and at the end: sends the mount home (a run that doesn't move the mount has nothing to send).
    /// False if it couldn't get there, so the chain stops with the reason in <see cref="Status"/> rather than going on from elsewhere.
    /// </summary>
    private async Task<bool> GoHomeAsync(ITelescopeSession telescope, TestTarget target, RunOptions options, string after, CancellationToken token)
    {
        if (!options.AllowMotion) return true;
        lock (_gate) Status = $"{after}. Sending the mount home…";
        Changed?.Invoke();
        HomeStatus status;
        try
        {
            await using var session = home.Open(target, options, telescope);
            status = await session.GoHomeAsync();
            _connected = status.Error is null;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Chained run stopped: couldn't send {Target} home: {Error}", target.Display, ex.Message);
            lock (_gate) Status = $"{after}, but the mount couldn't be sent home ({AscomError.Describe(ex)}) so the remaining tiers were not run.";
            return false;
        }
        if (token.IsCancellationRequested) return false;
        if (status.Error is null && status.Problem is null && status.AtHome) return true;
        var why = status.Error ?? status.Problem ?? "the mount isn't reporting that it is at home.";
        log.LogWarning("Chained run stopped: couldn't send {Target} home: {Why}", target.Display, why);
        lock (_gate) Status = $"{after}, but the mount couldn't be sent home ({why}) so the remaining tiers were not run.";
        return false;
    }

    private async Task<RunReport?> RunAsync(ITelescopeSession session, TestTarget target, RunOptions options, ConformULog? conformU, RunReport? followsUp,
        CancellationToken outer)
    {
        RunReport? report = null;
        // This tier's own token: the user's Stop cancels it (it is linked to the run's), and so does the safety check (CheckSafety)
        // without ending the whole run, so the mount can still be sent home afterwards.
        using var tierCts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        lock (_gate) _tierCts = tierCts;
        var token = tierCts.Token;
        try
        {
            log.LogInformation("Run started: {Target}, tier {Tier}, motion {Motion}", target.Display, options.Tier, options.AllowMotion);
            var previous = await store.LatestForAsync(target);
            // What the summary compares with and the clock estimates from: a like-for-like run, not just the newest one.
            PreviousReport = (await store.ListAsync()).FirstOrDefault(r => ReportStore.Comparable(r, target, options.Tier, conformU is not null));
            Changed?.Invoke();
            report = await runner.RunAsync(session, target, options,
                r =>
                {
                    if (_wasConnected is null && r.Id == "Connection.Connected.Read" && bool.TryParse(r.Actual, out var before)) _wasConnected = before;
                    if (r.Id is "Connection.Connect" or "Connection.Connected.SetTrueWhenConnected" && r.Outcome is Outcome.Pass or Outcome.Warning) _connected = true;
                    if (r.Id == "Disconnected.Disconnect" && r.Outcome is Outcome.Pass or Outcome.Info) _connected = false;
                    lock (_gate) _results.Add(r);
                    CheckSafety(r);
                    Changed?.Invoke();
                },
                p =>
                {
                    Progress = p;
                    Status = (p.Group == "Done" ? "Finishing…" : $"Checking {p.Group} (group {p.GroupIndex + 1} of {p.GroupCount})");
                    Changed?.Invoke();
                },
                token, openSession: () => OpenSession(target, options), previous: previous, conformU: conformU, followsUp: followsUp, onGate: warnings => AskGateAsync(warnings, token));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Run failed: {Error}", ex.Message);
            Status = $"Couldn't run: {AscomError.Describe(ex)}";
            lock (_gate)
            {
                _results.Add(new CheckResult
                {
                    Id = "Run.Open", Group = "Run", Member = target.Display, Outcome = Outcome.Error,
                    Message = $"Couldn't open the device: {AscomError.Describe(ex)}",
                    Fix = target.Kind == TargetKind.Com
                        ? "Check that the driver is registered (ASCOM Profile Explorer) and that its server program can start."
                        : "Check that the Alpaca server is running and the host, port and device number are right."
                });
            }
        }
        lock (_gate) _tierCts = null; // the tier is over: nothing more to stop (tierCts is disposed on leaving)
        if (report is not null)
        {
            // Saved apart from the run: a failed save keeps the findings on screen and says so, not "couldn't open".
            try
            {
                await store.SaveAsync(report);
                Status = report.Cancelled ? $"Stopped; the findings so far are saved." : $"Finished and saved.";
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Run report not saved: {Error}", ex.Message);
                Status = $"{(report.Cancelled ? "Stopped" : "Finished")}, but the report couldn't be saved to {store.Folder}: {ex.Message}";
            }
            log.LogInformation("Run finished: {Label}: {Fail} failed, {Warn} warnings", report.Label, report.Count(Outcome.Fail), report.Count(Outcome.Warning));
        }
        Changed?.Invoke();
        return report;
    }
}

/// <summary>What the finished dialog shows at the end of all testing (a new instance each time, so a page can tell it has shown it).</summary>
public sealed record FinishedInfo(string Message, bool Stopped, IReadOnlyList<RunReport> Reports, IReadOnlyList<SuggestedTest> Suggestions, string? SafetyNote = null, int HiddenDismissed = 0);

/// <summary>The warnings a run is asking the user about before it moves the mount.</summary>
public sealed record GateRequest(IReadOnlyList<string> Warnings);
