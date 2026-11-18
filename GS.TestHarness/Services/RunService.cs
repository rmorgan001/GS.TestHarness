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

    /// <summary>Every report of the last batch (one per site).</summary>
    public IReadOnlyList<RunReport> LastReports { get; private set; } = [];

    /// <summary>The site of the run under way (null: the server's own), and its place in the batch.</summary>
    public SiteLocation? Site { get; private set; }

    /// <summary>The ConformU log the run under way (or the last run) follows up; null for an ordinary run.</summary>
    public ConformULog? ConformU { get; private set; }
    public int SiteIndex { get; private set; }
    public int SiteCount { get; private set; } = 1;

    public IReadOnlyList<CheckResult> Results
    {
        get { lock (_gate) return [.. _results]; }
    }

    /// <summary>Starts a run at the server's own site; false if one is already running.</summary>
    public bool Start(TestTarget target, RunOptions options) => Start(target, options, [null]);

    /// <summary>
    /// Starts one run per site, in turn (null: the server's own site); false if a run is already under way.
    /// </summary>
    public bool Start(TestTarget target, RunOptions options, IReadOnlyList<SiteLocation?> sites, ConformULog? conformU = null)
    {
        if (sites.Count == 0) return false;
        lock (_gate)
        {
            if (IsRunning) return false;
            IsRunning = true;
            Target = target;
            Options = options;
            ConformU = conformU;
            Progress = null;
            LastReport = null;
            LastReports = [];
            SiteCount = sites.Count;
            SiteIndex = 0;
            Site = sites[0];
            Status = $"Connecting to {target.Display}…" + SiteSuffix(sites[0]);
            _results.Clear();
            _cts = new CancellationTokenSource();
        }
        Changed?.Invoke();
        _ = Task.Run(() => RunAllAsync(target, options, sites, conformU, _cts.Token));
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

    private async Task RunAllAsync(TestTarget target, RunOptions options, IReadOnlyList<SiteLocation?> sites, ConformULog? conformU,
        CancellationToken token)
    {
        var reports = new List<RunReport>();
        try
        {
            for (var i = 0; i < sites.Count && !token.IsCancellationRequested; i++)
            {
                lock (_gate)
                {
                    Site = sites[i];
                    SiteIndex = i;
                    if (i > 0) _results.Clear();
                }
                if (await RunAsync(target, options, sites[i], conformU, token) is { } report)
                {
                    reports.Add(report);
                    lock (_gate) LastReports = [.. reports];
                }
            }
            if (sites.Count > 1)
                Status = token.IsCancellationRequested ? $"Stopped after {reports.Count} of {sites.Count} sites; the reports so far are saved."
                    : $"Finished all {sites.Count} sites; each report is saved.";
        }
        finally
        {
            lock (_gate)
            {
                LastReport = reports.LastOrDefault();
                LastReports = [.. reports];
                IsRunning = false;
                _cts?.Dispose();
                _cts = null;
            }
            Changed?.Invoke();
        }
    }

    /// <summary>" · North: Mauna Kea · site 2 of 3" for the status line: where the run is, and how far through the sites.</summary>
    private string SiteSuffix(SiteLocation? site)
    {
        if (SiteCount <= 1 && site is null) return "";
        var text = $" · {site?.Label ?? "Server's site"}";
        return SiteCount > 1 ? $"{text} · site {SiteIndex + 1} of {SiteCount}" : text;
    }

    private async Task<RunReport?> RunAsync(TestTarget target, RunOptions options, SiteLocation? site, ConformULog? conformU,
        CancellationToken token)
    {
        RunReport? report = null;
        var where = site is null ? "" : $" at {site.Label}";
        try
        {
            log.LogInformation("Run started: {Target}, motion {Motion}", target.Display, options.AllowMotion);
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
                    Status = (p.Group == "Done" ? "Finishing…" : $"Checking {p.Group} (group {p.GroupIndex + 1} of {p.GroupCount})") + SiteSuffix(site);
                    Changed?.Invoke();
                },
                token, previous: previous, site: site, conformU: conformU);
            await store.SaveAsync(report);
            Status = report.Cancelled ? $"Stopped{where}; the findings so far are saved." : $"Finished{where} and saved.";
            log.LogInformation("Run finished: {Label}: {Fail} failed, {Warn} warnings", report.Label, report.Count(Outcome.Fail), report.Count(Outcome.Warning));
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
        Changed?.Invoke();
        return report;
    }
}
