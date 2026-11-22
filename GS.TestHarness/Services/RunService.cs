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
public sealed class RunService(TestRunner runner, ReportStore store, HomeService home, ILogger<RunService> log)
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private readonly List<CheckResult> _results = [];

    /// <summary>Raised (on a worker thread) whenever the run's state or findings change.</summary>
    public event Action? Changed;

    public bool IsRunning { get; private set; }
    public TestTarget? Target { get; private set; }
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
    private readonly List<RunReport> _reports = [];

    /// <summary>
    /// Looks at a finished tier for tests that are missing. Tier 1: each problem no tier 2 test follows up (so the only
    /// thing to do about it is read the finding). Tier 2 or 3: a diagnosis that couldn't measure what it was after.
    /// </summary>
    private void EvaluateTests(RunReport report)
    {
        var tier = report.Options.Tier;
        var found = new List<SuggestedTest>();
        if (tier == 1)
        {
            // Only what is worth a test of its own: a Fail or Error, or a Warning of a test that matters (a deprecated method
            // being implemented, say, is a Warning of weight 2 that no diagnosis would add anything to).
            foreach (var r in Tiers.OutlookFor(report).Uncovered
                         .Where(r => r.Outcome is Outcome.Fail or Outcome.Error || (r.Outcome == Outcome.Warning && TestWeights.Of(r) >= MinWarningWeight)))
            {
                // The finding that called for it, then the member's other tier 1 problems: the new test should explain them too.
                var related = new List<string> { Describe(r.Id, r.Number) };
                related.AddRange(report.Results.Where(x => x.IsProblem && x.Member == r.Member && x.Id != r.Id && !Tiers.IsDiagnosis(x))
                    .Select(x => x.Id).Distinct().Take(5).Select(id => Describe(id, TestNumbers.Of(id))));
                found.Add(new SuggestedTest(2,
                    $"Tier 2 diagnosis for {r.Member} ({r.Id})",
                    WithNumbers($"Tier 1 found: {r.Message} No tier 2 test follows it up, so there's nothing that narrows it to a cause in the code.", related),
                    related));
            }
        }
        else
        {
            foreach (var r in report.Results.Where(r => Tiers.IsDiagnosis(r) && r.Outcome == Outcome.Error))
            {
                // The diagnosis that couldn't measure, and the tier below's tests it follows up.
                var related = new List<string> { Describe(r.Id, r.Number) };
                if (TestCatalog.For(r.Id) is { } entry)
                    related.AddRange(entry.FollowsUp.Select(k => Describe(k, TestNumbers.For(k))));
                found.Add(new SuggestedTest(tier,
                    $"A tier {tier} measurement for {r.Member} that works where {r.Id} couldn't",
                    WithNumbers($"{r.Id} couldn't measure it: {r.Message}", related),
                    related));
            }
        }
        lock (_gate)
            foreach (var s in found)
                if (!_suggestions.Any(x => x.Title == s.Title)) _suggestions.Add(s);
    }

    /// <summary>A tier 1 Warning only brings a suggested test when its test weighs at least this (<see cref="TestWeights"/>).</summary>
    private const int MinWarningWeight = 5;

    private static string Describe(string id, int number) => number > 0 ? $"Test {number}: {id}" : id;

    /// <summary>The reason with every test number it refers to (those of <paramref name="related"/>, from "Test 1159: …") listed at the end.</summary>
    private static string WithNumbers(string why, IReadOnlyList<string> related)
    {
        var numbers = related.Select(s => System.Text.RegularExpressions.Regex.Match(s, @"^Test (\d+):"))
            .Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct().ToList();
        return numbers.Count == 0 ? why : $"{why} Test numbers referred to: {string.Join(", ", numbers)}.";
    }

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
        lock (_gate)
        {
            if (IsRunning) return false;
            IsRunning = true;
            Target = target;
            Options = options;
            ConformU = conformU;
            Progress = null;
            LastReport = null;
            Status = $"Connecting to {target.Display}…";
            StartedAt = DateTimeOffset.Now;
            PreviousReport = null;
            Finished = null;
            _suggestions.Clear();
            _reports.Clear();
            _results.Clear();
            _cts = new CancellationTokenSource();
        }
        Changed?.Invoke();
        _ = Task.Run(() => RunOneAsync(target, options, conformU, followsUp, _cts.Token, chain));
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
        try
        {
            report = await RunAsync(target, options, conformU, followsUp, token);
            if (report is not null) Collect(report);
            // Chained tiers: after each tier the mount goes home and the next tier (for what this one found) starts, with no
            // second safety dialog (the user accepted it at tier 1). IsRunning stays true throughout, so Stop stays at hand.
            while (chain && report is { Cancelled: false } && !token.IsCancellationRequested)
            {
                var next = Tiers.Next(report);
                var last = next is null;
                // Too many failures: a mount in this state may not be safe to keep moving, so the deeper tiers aren't run.
                // Each failed test counts for its weight (1 to 10): one that can't fail (the connection, stopping the mount)
                // ends the tiers at once, and the rest add up.
                var score = Tiers.ProblemScore(report);
                var critical = Tiers.CriticalFailures(report);
                var unsafeToGoOn = !last && (critical.Count > 0 || score > Tiers.MaxProblemsToContinue);
                if (unsafeToGoOn)
                {
                    var worst = string.Join("; ", Tiers.WeightedProblems(report).Take(3).Select(x => $"{x.Finding.Id} (weight {x.Weight})"));
                    safetyNote = (critical.Count > 0
                            ? $"Tier {report.Options.Tier} failed {critical.Count} test{(critical.Count == 1 ? "" : "s")} the run can't go on after (weight {TestWeights.Critical}): " +
                              string.Join(", ", critical.Take(5).Select(c => c.Id)) + ". "
                            : $"Tier {report.Options.Tier}'s failed tests add up to {score} (the limit is {Tiers.MaxProblemsToContinue}); the heaviest: {worst}. ") +
                         $"Tier {next!.Tier} was not run for safety concerns: the mount may not be behaving as it should. " +
                         "Fix the problems in the report and run the tests again.";
                }
                if (!await GoHomeAsync(target, options, last ? "All tests finished" : $"Tier {report.Options.Tier} finished", token)) break;
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
                report = await RunAsync(target, options, conformU, next!.From, token);
                if (report is not null) Collect(report);
            }
        }
        finally
        {
            lock (_gate)
            {
                if (chain && _reports.Count > 0)
                    Finished = new FinishedInfo(Status ?? "The run has ended.", !completed, [.. _reports], [.. _suggestions], safetyNote);
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
        EvaluateTests(report);
    }

    /// <summary>
    /// Between chained tiers and at the end: sends the mount home (a run that doesn't move the mount has nothing to send).
    /// False if it couldn't get there, so the chain stops with the reason in <see cref="Status"/> rather than going on from elsewhere.
    /// </summary>
    private async Task<bool> GoHomeAsync(TestTarget target, RunOptions options, string after, CancellationToken token)
    {
        if (!options.AllowMotion) return true;
        lock (_gate) Status = $"{after}. Sending the mount home…";
        Changed?.Invoke();
        await using var session = home.Open(target, options);
        var status = await session.GoHomeAsync();
        if (token.IsCancellationRequested) return false;
        if (status.Error is null && status.Problem is null && status.AtHome) return true;
        var why = status.Error ?? status.Problem ?? "the mount isn't reporting that it is at home.";
        log.LogWarning("Chained run stopped: couldn't send {Target} home: {Why}", target.Display, why);
        lock (_gate) Status = $"{after}, but the mount couldn't be sent home ({why}) so the remaining tiers were not run.";
        return false;
    }

    private async Task<RunReport?> RunAsync(TestTarget target, RunOptions options, ConformULog? conformU, RunReport? followsUp,
        CancellationToken token)
    {
        RunReport? report = null;
        try
        {
            log.LogInformation("Run started: {Target}, tier {Tier}, motion {Motion}", target.Display, options.Tier, options.AllowMotion);
            var previous = await store.LatestForAsync(target);
            // What the summary compares with and the clock estimates from: a like-for-like run, not just the newest one.
            PreviousReport = (await store.ListAsync()).FirstOrDefault(r => ReportStore.Comparable(r, target, options.Tier, conformU is not null));
            Changed?.Invoke();
            await using var session = await TestRunner.OpenAsync(target, options);
            report = await runner.RunAsync(session, target, options,
                r =>
                {
                    lock (_gate) _results.Add(r);
                    Changed?.Invoke();
                },
                p =>
                {
                    Progress = p;
                    Status = (p.Group == "Done" ? "Finishing…" : $"Checking {p.Group} (group {p.GroupIndex + 1} of {p.GroupCount})");
                    Changed?.Invoke();
                },
                token, previous: previous, conformU: conformU, followsUp: followsUp, onGate: warnings => AskGateAsync(warnings, token));
        }
        catch (Exception ex) when (report is not null)
        {
            // The run finished; only closing the session failed. Keep the report.
            log.LogWarning(ex, "Closing the session failed: {Error}", ex.Message);
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

/// <summary>A test the user should add: <see cref="Title"/> says what to add, <see cref="Why"/> what in the run called for it.</summary>
/// <param name="Related">The earlier tests it should be tied to (each "Test 1159: ParkHome.FindHome"): the ones whose findings called for it.</param>
public sealed record SuggestedTest(int Tier, string Title, string Why, IReadOnlyList<string> Related);

/// <summary>What the finished dialog shows at the end of all testing (a new instance each time, so a page can tell it has shown it).</summary>
public sealed record FinishedInfo(string Message, bool Stopped, IReadOnlyList<RunReport> Reports, IReadOnlyList<SuggestedTest> Suggestions, string? SafetyNote = null);

/// <summary>The warnings a run is asking the user about before it moves the mount.</summary>
public sealed record GateRequest(IReadOnlyList<string> Warnings);
