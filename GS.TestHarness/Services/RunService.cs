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
public sealed class RunService(TestRunner runner, ReportStore store, ILogger<RunService> log)
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
    public bool Start(TestTarget target, RunOptions options, ConformULog? conformU = null, RunReport? followsUp = null)
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
            _results.Clear();
            _cts = new CancellationTokenSource();
        }
        Changed?.Invoke();
        _ = Task.Run(() => RunOneAsync(target, options, conformU, followsUp, _cts.Token));
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
        CancellationToken token)
    {
        RunReport? report = null;
        try
        {
            report = await RunAsync(target, options, conformU, followsUp, token);
        }
        finally
        {
            lock (_gate)
            {
                LastReport = report;
                IsRunning = false;
                _cts?.Dispose();
                _cts = null;
            }
            Changed?.Invoke();
        }
    }

    private async Task<RunReport?> RunAsync(TestTarget target, RunOptions options, ConformULog? conformU, RunReport? followsUp,
        CancellationToken token)
    {
        RunReport? report = null;
        try
        {
            log.LogInformation("Run started: {Target}, tier {Tier}, motion {Motion}", target.Display, options.Tier, options.AllowMotion);
            var previous = await store.LatestForAsync(target);
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
                token, previous: previous, conformU: conformU, followsUp: followsUp);
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
