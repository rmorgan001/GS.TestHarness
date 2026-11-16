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
    /// <summary>The groups, in the order they run: connection first, the disconnected checks last.</summary>
    public static IReadOnlyList<ICheckGroup> Groups { get; } =
    [
        new ConnectionChecks(),
        new ProtocolChecks(),
        new CommonChecks(),
        new CapabilityChecks(),
        new PropertyChecks(),
        new MethodChecks(),
        new SettingsChecks(),
        new MotionChecks(),
        new ParkHomeChecks(),
        new TrackingChecks(),
        new ClientsChecks(),
        new RegistrationChecks(),
        new PerformanceChecks(),
        new DisconnectedChecks()
    ];

    /// <summary>
    /// Tells the mount it is at <paramref name="site"/> (elevation, latitude, longitude) and checks it took. Returns the
    /// server's own site, to put back afterwards. Stops the run if the site can't be read or set.
    /// </summary>
    private static async Task<(double, double, double)> ApplySiteAsync(CheckContext ctx, SiteLocation site)
    {
        ctx.Group = "Site";
        var own = await ctx.TryAsync(t => (t.SiteLatitude, t.SiteLongitude, t.SiteElevation));
        if (!own.Ok)
        {
            ctx.Report("Apply", "SiteLatitude", Outcome.Error, $"The server's own site can't be read, so the run can't move it to {site.Label}.",
                actual: own.ErrorText);
            throw new StopRunException($"the site can't be read: {own.ErrorText}");
        }
        var set = await ctx.TryAsync(t =>
        {
            t.SiteElevation = site.Elevation;
            t.SiteLatitude = site.Latitude;
            t.SiteLongitude = site.Longitude;
        });
        var back = set.Ok ? await ctx.TryAsync(t => (t.SiteLatitude, t.SiteLongitude, t.SiteElevation)) : null;
        var took = back is { Ok: true } && Math.Abs(back.Value.SiteLatitude - site.Latitude) < 1e-4
                   && Math.Abs(back.Value.SiteLongitude - site.Longitude) < 1e-4 && Math.Abs(back.Value.SiteElevation - site.Elevation) < 0.5;
        if (!took)
        {
            ctx.Report("Apply", "SiteLatitude", Outcome.Error, $"The mount couldn't be set to {site.Label}.",
                expected: site.Coordinates,
                actual: !set.Ok ? set.ErrorText : back is { Ok: true } ? FormattableString.Invariant($"{back.Value.SiteLatitude:0.####}°, {back.Value.SiteLongitude:0.####}°, {back.Value.SiteElevation:0} m") : back?.ErrorText,
                fix: "Running at another site needs SiteLatitude, SiteLongitude and SiteElevation to be writable.");
            await RestoreSiteAsync(ctx, own.Value);
            throw new StopRunException($"the mount couldn't be set to {site.Label}");
        }
        ctx.Report("Apply", "SiteLatitude", Outcome.Info, $"Site set to {site.Label} ({site.Coordinates}) for this run.",
            actual: FormattableString.Invariant($"was {own.Value.SiteLatitude:0.####}°, {own.Value.SiteLongitude:0.####}°, {own.Value.SiteElevation:0} m"));
        // Later groups read the site from the mount (the capability checks keep latitude and longitude).
        return own.Value;
    }

    /// <summary>Puts the server's own site back.</summary>
    private static async Task RestoreSiteAsync(CheckContext ctx, (double Latitude, double Longitude, double Elevation) own)
    {
        var group = ctx.Group;
        ctx.Group = "Site";
        if (ctx.Lost is not null)
        {
            ctx.Report("Restore", "SiteLatitude", Outcome.Error, "The server's own site couldn't be put back: communication with the device was lost.",
                fix: FormattableString.Invariant($"Set the site back to latitude {own.Latitude:0.####}, longitude {own.Longitude:0.####}, elevation {own.Elevation:0} m in the server's settings."));
            ctx.Group = group;
            return;
        }
        var r = await ctx.TryAsync(t =>
        {
            t.SiteElevation = own.Elevation;
            t.SiteLatitude = own.Latitude;
            t.SiteLongitude = own.Longitude;
        });
        ctx.Report("Restore", "SiteLatitude", r.Ok ? Outcome.Info : Outcome.Error,
            r.Ok ? FormattableString.Invariant($"The server's own site was put back ({own.Latitude:0.####}°, {own.Longitude:0.####}°, {own.Elevation:0} m).")
                : "The server's own site couldn't be put back: set it by hand.",
            actual: r.ErrorText,
            fix: FormattableString.Invariant($"Set the site back to latitude {own.Latitude:0.####}, longitude {own.Longitude:0.####}, elevation {own.Elevation:0} m in the server's settings."));
        ctx.Group = group;
    }

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
    public async Task<RunReport> RunAsync(ITelescopeSession session, TestTarget target, RunOptions options,
        Action<CheckResult>? onResult = null, Action<RunProgress>? onProgress = null, CancellationToken token = default,
        Func<Task<ITelescopeSession>>? openSession = null, RunReport? previous = null, SiteLocation? site = null)
    {
        var report = new RunReport { Target = target, Options = options, Site = site };
        void Add(CheckResult r)
        {
            lock (report.Results) report.Results.Add(r);
            onResult?.Invoke(r);
        }
        var ctx = new CheckContext(session, target, options, locator, Add, token, report.Results,
            openSession ?? (() => OpenAsync(target, options)), previous);
        (double Latitude, double Longitude, double Elevation)? original = null;

        try
        {
            for (var i = 0; i < Groups.Count; i++)
            {
                var group = Groups[i];
                // The site goes back before the disconnected checks: a disconnected mount can't be written to.
                if (group is DisconnectedChecks && original is { } back)
                {
                    original = null;
                    await RestoreSiteAsync(ctx, back);
                }
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
                    if (group is ConnectionChecks)
                    {
                        // Connected: from now on a device that stops answering stops the run.
                        ctx.WatchForLostConnection = true;
                        if (site is not null) original = await ApplySiteAsync(ctx, site);
                    }
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
            if (original is { } back)
            {
                original = null;
                try { await RestoreSiteAsync(new CheckContext(session, target, options, locator, Add, CancellationToken.None), back); }
                catch { /* reported by RestoreSiteAsync where it can be */ }
            }
            // Don't leave the mount moving.
            if (options.AllowMotion)
            {
                try { await session.RunAsync(t => { t.AbortSlew(); return true; }, TimeSpan.FromSeconds(10)); }
                catch { /* nothing more to do */ }
            }
        }
        if (original is { } stillSet) await RestoreSiteAsync(ctx, stillSet);
        report.Identity = ctx.Identity;
        report.Finished = DateTimeOffset.Now;
        onProgress?.Invoke(new RunProgress(Groups.Count, Groups.Count, "Done", report.Results.Count));
        return report;
    }
}
