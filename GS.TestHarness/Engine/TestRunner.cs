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

using GS.TestHarness.Engine.Checks;

namespace GS.TestHarness.Engine;

/// <summary>Progress of a run, for the UI.</summary>
public sealed record RunProgress(int GroupIndex, int GroupCount, string Group, int Findings);

/// <summary>Runs the check groups, in order, against one mount, and gathers the findings into a <see cref="RunReport"/>.</summary>
public sealed class TestRunner(SourceLocator locator)
{
    /// <summary>
    /// The groups, in the order they run: connection first, the groups that don't move the mount, then the ones that do
    /// (motion, park and home, tracking rates, the diagnoses), and the disconnected checks last.
    /// </summary>
    public static IReadOnlyList<ICheckGroup> Groups { get; } =
    [
        new ConnectionChecks(),
        new ProtocolChecks(),
        new CommonChecks(),
        new CapabilityChecks(),
        new PropertyChecks(),
        new MethodChecks(),
        new SettingsChecks(),
        new ClientsChecks(),
        new RegistrationChecks(),
        new MotionChecks(),
        new ParkHomeChecks(),
        new TrackingChecks(),
        new DiagnoseChecks(),
        new ConformUChecks(),
        new PerformanceChecks(),
        new DisconnectedChecks()
    ];

    /// <summary>Opens the session for a target (starting a COM driver, or creating the Alpaca client).</summary>
    public static async Task<ITelescopeSession> OpenAsync(TestTarget target, RunOptions options)
    {
        var timeout = TimeSpan.FromSeconds(options.CallTimeoutSeconds);
        if (target.Kind == TargetKind.Com)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("COM drivers can only be tested on Windows.");
            return await ComSession.CreateAsync(target.ProgId, TimeSpan.FromSeconds(Math.Max(30, options.CallTimeoutSeconds)));
        }
        return new AlpacaSession(target, timeout);
    }

    /// <summary>
    /// Runs every group that applies. Each finding is passed to <paramref name="onResult"/> as it is made.
    /// Stopping (<paramref name="token"/>) finishes the report with what was found so far.
    /// </summary>
    /// <param name="openSession">Opens another client of the device (the Clients checks); by default a new session of the target.</param>
    /// <param name="previous">The last saved run of the same device, if any.</param>
    /// <param name="conformU">A ConformU log to follow up: the ConformU checks run, and say for each of its problems
    /// whether the harness reproduced it. Combine it with <see cref="RunOptions.Groups"/> from its plan.</param>
    /// <param name="followsUp">For a tier 2 or 3 run (<see cref="RunOptions.Tier"/>), the run it follows up: what it found
    /// is what the diagnoses take apart (see <see cref="Tiers.Options"/>).</param>
    public async Task<RunReport> RunAsync(ITelescopeSession session, TestTarget target, RunOptions options,
        Action<CheckResult>? onResult = null, Action<RunProgress>? onProgress = null, CancellationToken token = default,
        Func<Task<ITelescopeSession>>? openSession = null, RunReport? previous = null,
        ConformULog? conformU = null, RunReport? followsUp = null)
    {
        // Tier 1 always first: a deeper run only follows up the tier below's findings.
        if (Tiers.Problem(options, followsUp, conformU is not null) is { } tierProblem)
            throw new InvalidOperationException($"Can't run tier {options.Tier}: {tierProblem}");
        var report = new RunReport
        {
            Target = target, Options = options, ConformULog = conformU?.FileName,
            FollowsUpNumber = followsUp is { Number: > 0 } up ? up.Number : null
        };
        void Add(CheckResult r)
        {
            lock (report.Results) report.Results.Add(r);
            onResult?.Invoke(r);
        }
        var ctx = new CheckContext(session, target, options, locator, Add, token, report.Results,
            openSession ?? (() => OpenAsync(target, options)), previous) { ConformU = conformU, FollowsUp = followsUp };

        try
        {
            for (var i = 0; i < Groups.Count; i++)
            {
                var group = Groups[i];
                // Groups left out of this run (a ConformU run's plan, or the ConformU checks without a log) aren't mentioned.
                if (options.Groups is { } only && !only.Contains(group.Name)) continue;
                if (group is ConformUChecks && conformU is null) continue;
                // The diagnoses are tiers 2 and 3 only, and a ConformU run's are made in the ConformU group (its verdicts follow them).
                if (group is DiagnoseChecks && (options.Tier < 2 || conformU is not null)) continue;
                ctx.Group = group.Name;
                onProgress?.Invoke(new RunProgress(i, Groups.Count, group.Name, report.Results.Count));
                // Once the device has gone, only the summary of what was found can still run.
                if (ctx.Lost is not null && group is not PerformanceChecks)
                {
                    ctx.Report("Group", group.Name, Outcome.Skipped, $"{group.Name} not run: communication with the device was lost.");
                    continue;
                }
                if (group.SkipReason(ctx) is { } reason)
                {
                    ctx.Report("Group", group.Name, Outcome.Skipped, $"{group.Name} not run: {reason}.");
                    continue;
                }
                try
                {
                    await group.RunAsync(ctx);
                    // Connected: from now on a device that stops answering stops the run.
                    if (group is ConnectionChecks) ctx.WatchForLostConnection = true;
                }
                catch (CommunicationLostException ex)
                {
                    ctx.Group = ex.Group;
                    ctx.Report("CommunicationLost", ex.Group, Outcome.Error, $"Run stopped during {ex.Group}: {ex.Message}",
                        actual: ex.Diagnostics,
                        fix: "The driver's server program (COM) or the Alpaca server stopped answering. Look at the server's log " +
                             "for a crash or an exception at this time, restart it, and check the mount before running again. " +
                             "The findings up to this point are kept.");
                    report.CommunicationLost = true;
                }
                catch (StopRunException ex)
                {
                    ctx.Report("Stopped", group.Name, Outcome.Error, $"Run stopped: {ex.Message}");
                    break;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // A fault in a check (or a mount that stopped answering): note it and go on with the next group.
                    ctx.Report("Unexpected", group.Name, Outcome.Error, $"The {group.Name} checks stopped early: {AscomError.Describe(ex)}",
                        actual: ex.ToString());
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            report.Cancelled = true;
            // Don't leave the mount moving.
            if (options.AllowMotion)
            {
                try { await session.RunAsync(t => { t.AbortSlew(); return true; }, TimeSpan.FromSeconds(10)); }
                catch { /* nothing more to do */ }
            }
        }
        // ConformU's problems against everything the run found, the disconnected checks included.
        ConformUChecks.Summarise(ctx);
        report.Identity = ctx.Identity;
        report.Finished = DateTimeOffset.Now;
        onProgress?.Invoke(new RunProgress(Groups.Count, Groups.Count, "Done", report.Results.Count));
        return report;
    }
}
