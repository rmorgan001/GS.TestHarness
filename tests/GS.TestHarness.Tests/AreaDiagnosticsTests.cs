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
using Xunit.Abstractions;

namespace GS.TestHarness.Tests;

/// <summary>
/// Tier 2 for the problems that aren't measurements (AreaDiagnostics): from the problems GSServer 1.2.3.3's tier 1
/// run found, each area is offered, finds the core issue the fake mount's defect puts there (and the code review finds
/// it in a small copy of GSServer's code), and says a mount without the defect is now right.
/// </summary>
public class AreaDiagnosticsTests(ITestOutputHelper output)
{
    private static readonly TestTarget GsServer = new() { Codebase = Codebase.GSServer };

    private static CheckResult Found(string id, string member, Outcome outcome = Outcome.Fail)
        => new() { Id = id, Group = id[..id.IndexOf('.')], Member = member, Outcome = outcome, Message = $"tier 1: {id}" };

    /// <summary>The problems GSServer 1.2.3.3's tier 1 run found (report 2026-11-19 06:52), as findings.</summary>
    private static RunReport Tier1() => new()
    {
        Target = GsServer,
        Options = EngineTests.Everything,
        Results =
        [
            Found("Connection.DriverVersion.Format", "DriverVersion", Outcome.Warning),
            Found("Capabilities.CanMoveAxis.InvalidAxis", "CanMoveAxis"),
            Found("Methods.AxisRates.Tertiary", "AxisRates"),
            Found("Methods.AxisRates.InvalidAxis", "AxisRates"),
            Found("Motion.SlewSettleTime.Effect", "SlewSettleTime"),
            Found("Motion.SlewToCoordinatesAsync.NotTracking", "SlewToCoordinatesAsync"),
            Found("Motion.PulseGuide.InvalidDirection", "PulseGuide"),
            Found("Motion.PulseGuide.NotTracking", "PulseGuide"),
            Found("Motion.PulseGuide.WhileSlewing", "PulseGuide"),
            // Connected read true after Disconnect while another client held the hardware: allowed, so only noted.
            Found("Clients.DisconnectedClient.Connected", "Connected", Outcome.Info),
            Found("Registration.Server.64-bit", "COM registration", Outcome.Warning),
            Found("Disconnected.NotConnected.RightAscension", "RightAscension"),
            Found("Disconnected.NotConnected.Tracking", "Tracking"),
            Found("Disconnected.NotConnected.CanSlew", "CanSlew"),
            Found("Disconnected.NotConnected.AxisRates", "AxisRates"),
            Found("Connection.Connected.Read", "Connected", Outcome.Pass)
        ]
    };

    /// <summary>GSServer 1.2.3.3's faults, as the fake mount's defects.</summary>
    private static readonly Defect[] AsGsServer =
    [
        Defect.NoConnectedCheck, Defect.InvalidAxisUnchecked, Defect.TertiaryAxisRates, Defect.PulseGuideDirectionUnchecked,
        Defect.PulseGuideWhileSlewing, Defect.PulseGuideWhileNotTracking, Defect.AsyncSlewTurnsTrackingOn, Defect.DriverVersionFull,
        Defect.SettleTimeIgnored, Defect.ConnectedSharedByClients
    ];

    /// <summary>The tier 2 run the Run page's button starts after <paramref name="tier1"/>.</summary>
    private async Task<RunReport> Tier2Async(FakeTelescope fake, RunReport tier1, SourceLocator? locator = null)
    {
        var next = Tiers.Next(tier1);
        Assert.True(next is { Tier: 2 }, "tier 2 wasn't offered");
        var runner = new TestRunner(locator ?? SourceLocator.None);
        await using var session = new DirectSession(fake);
        var clients = 0;
        var report = await runner.RunAsync(session, GsServer, Tiers.Options(EngineTests.Everything, next!),
            openSession: () => Task.FromResult<ITelescopeSession>(new DirectSession(fake, Interlocked.Increment(ref clients))),
            followsUp: tier1);
        var uncatalogued = report.Results.Select(r => r.Id).Where(id => TestCatalog.For(id) is null).Distinct().ToList();
        Assert.True(uncatalogued.Count == 0, "Not in Engine/TestCatalog.cs: " + string.Join(", ", uncatalogued));
        foreach (var r in report.Results.Where(Tiers.IsDiagnosis))
            output.WriteLine($"{r.Outcome,-8} {r.Number} {r.Id}: {r.Message}\n  {r.Actual?.Replace("\n", "\n  ")}\n  FIX: {r.Fix}\n");
        return report;
    }

    private static CheckResult Finding(RunReport report, string id)
    {
        var found = report.Results.Where(r => r.Id == id).ToList();
        Assert.True(found.Count == 1, $"{id}: {found.Count} findings");
        return found[0];
    }

    [Fact]
    public void A_run_says_whether_the_next_tier_is_available_not_needed_or_not_available()
    {
        // Tier 1 with no problems: tier 2 isn't needed.
        var clean = Tier1() with { Results = [Found("Connection.Connected.Read", "Connected", Outcome.Pass)] };
        var none = Tiers.OutlookFor(clean);
        Assert.Equal((2, OutlookKind.NotNeeded), (none.Tier, none.Kind));
        Assert.Empty(none.Uncovered);

        // Tier 1 problems no tier 2 test follows up: not available, and which ones.
        var unfollowed = Tier1() with { Results = [Found("Properties.SiderealTime.Value", "SiderealTime")] };
        var missing = Tiers.OutlookFor(unfollowed);
        Assert.Equal(OutlookKind.NotAvailable, missing.Kind);
        Assert.Equal(["Properties.SiderealTime.Value"], missing.Uncovered.Select(r => r.Id));

        // Offered for some problems: the others are listed as having no tier 2 test.
        var mixed = Tiers.OutlookFor(Tier1() with { Results = [.. Tier1().Results, Found("Properties.SiderealTime.Value", "SiderealTime")] });
        Assert.Equal(OutlookKind.Offered, mixed.Kind);
        Assert.NotNull(mixed.Next);
        Assert.Equal(["Properties.SiderealTime.Value"], mixed.Uncovered.Select(r => r.Id));

        // Tier 2 that pinned its problems down, with no tier 3 test for them: tier 3 isn't needed.
        var tier2 = new RunReport
        {
            Target = GsServer, Options = EngineTests.Everything with { Tier = 2, FollowsUp = "x" },
            Results = [Found("Diagnose.Arguments.CanMoveAxis", "CanMoveAxis"), Found("Diagnose.DriverVersion", "DriverVersion", Outcome.Warning)]
        };
        var pinned = Tiers.OutlookFor(tier2);
        Assert.Equal((3, OutlookKind.NotNeeded), (pinned.Tier, pinned.Kind));
        Assert.Contains("pinned these down", pinned.Why);
        Assert.Equal(2, pinned.Uncovered.Count);

        // With SlewSettleTime too, tier 3 is offered for it, and the others shown as pinned down.
        var settle = Tiers.OutlookFor(tier2 with { Results = [.. tier2.Results, Found("Diagnose.SlewSettleTime", "SlewSettleTime")] });
        Assert.Equal(OutlookKind.Offered, settle.Kind);
        Assert.Equal(["SlewSettleTime"], settle.Next!.Members);
        Assert.Equal(2, settle.Uncovered.Count);

        // Tier 2 that found everything right: tier 3 isn't needed.
        Assert.Equal(OutlookKind.NotNeeded, Tiers.OutlookFor(tier2 with { Results = [Found("Diagnose.DriverVersion", "DriverVersion", Outcome.Info)] }).Kind);

        // Tier 3 is the last.
        Assert.Equal(OutlookKind.Deepest, Tiers.OutlookFor(tier2 with { Options = tier2.Options with { Tier = 3 } }).Kind);
    }

    [Fact]
    public void Every_kind_of_problem_GSServer_had_is_offered_a_tier_2_diagnosis()
    {
        var next = Tiers.Next(Tier1());
        Assert.NotNull(next);
        Assert.Equal(2, next.Tier);
        Assert.Equal(
            ["Arguments", "AxisRates", "DriverVersion", "NotConnected", "Preconditions", "Registration", "SlewSettleTime"],
            next.Members.Order());
        // The pulse length diagnosis follows up pulses that moved wrongly, not these.
        Assert.DoesNotContain("PulseGuide", next.Members);
        // Each area follows up only its own kind of finding.
        Assert.Equal(["Capabilities.CanMoveAxis.InvalidAxis", "Methods.AxisRates.InvalidAxis", "Motion.PulseGuide.InvalidDirection"],
            Tiers.ProblemsWith(Tier1(), "Arguments").Select(r => r.Id));
        Assert.Equal(4, Tiers.ProblemsWith(Tier1(), "NotConnected").Count);
        Assert.Equal(["Methods.AxisRates.Tertiary"], Tiers.ProblemsWith(Tier1(), "AxisRates").Select(r => r.Id));
        // A ConformU run isn't offered them: they diagnose a conformance run's findings.
        Assert.Null(Tiers.Next(Tier1() with { ConformULog = "conformu.txt" }));
    }

    [Fact]
    public async Task Tier_2_finds_each_core_issue_and_the_code_review_shows_where()
    {
        var root = Path.Combine(Path.GetTempPath(), "gs-area-" + Guid.NewGuid().ToString("N"));
        try
        {
            var locator = GsServerLikeSource(root);
            var report = await Tier2Async(new FakeTelescope(AsGsServer) { SyncSlews = true }, Tier1(), locator);

            var canMove = Finding(report, "Diagnose.Arguments.CanMoveAxis");
            Assert.Equal(Outcome.Fail, canMove.Outcome);
            Assert.Contains("doesn't check the axis at all", canMove.Message);
            Assert.Contains("AxisRates", canMove.Actual); // the same argument unchecked in its sibling
            var axisRates = Finding(report, "Diagnose.Arguments.AxisRates");
            Assert.Contains("returns null", axisRates.Message);
            Assert.Contains("default: return null;", axisRates.Actual);
            Assert.Contains("Replace the `return null`", axisRates.Fix);
            var pulseDirection = Finding(report, "Diagnose.Arguments.PulseGuide");
            Assert.Contains("reaches the mount code", pulseDirection.Message);
            Assert.Contains("ArgumentOutOfRangeException", pulseDirection.Message);

            var async = Finding(report, "Diagnose.Preconditions.SlewToCoordinatesAsync");
            Assert.Equal(Outcome.Fail, async.Outcome);
            Assert.Contains("turned tracking on", async.Message);
            Assert.Contains("SlewToCoordinates, SlewToTarget", async.Message); // the siblings that refuse
            Assert.Contains("call CheckTracking", async.Fix);
            Assert.Contains("CycleOnTracking", async.Actual);
            var pulse = Finding(report, "Diagnose.Preconditions.PulseGuide");
            Assert.Contains("during a slew it is accepted", pulse.Message);
            Assert.Contains("commented out", pulse.Actual);
            Assert.Contains("use InvalidOperationException", pulse.Fix);

            var notConnected = Finding(report, "Diagnose.NotConnected");
            Assert.Equal(Outcome.Fail, notConnected.Outcome);
            Assert.Contains("no member checks the connection", notConnected.Message);
            Assert.Contains("start with CheckSerialIO", notConnected.Fix);
            Assert.Contains("whenever Connected reads false", notConnected.Fix);
            Assert.DoesNotContain("Diagnose.Connected.PerClient", report.Results.Select(r => r.Id));

            var settle = Finding(report, "Diagnose.SlewSettleTime");
            Assert.Contains("never used", settle.Message);
            Assert.Contains(".TotalMilliseconds", settle.Fix);

            var axes = Finding(report, "Diagnose.AxisRates.Axes");
            Assert.Contains("Tertiary", axes.Message);
            Assert.Contains("same rates for every axis", axes.Message);

            var version = Finding(report, "Diagnose.DriverVersion");
            Assert.Equal(Outcome.Warning, version.Outcome);
            Assert.Contains("\"1.2\"", version.Fix);
            Assert.Contains("Version.ToString(2)", version.Fix);

            // Not a COM driver here: nothing to read in the registry.
            Assert.Equal(Outcome.Skipped, Finding(report, "Diagnose.Registration").Outcome);
            // The diagnoses are numbered tier 2 tests.
            Assert.All(report.Results.Where(Tiers.IsDiagnosis), r => Assert.Equal(2, TestNumbers.TierOf(r.Number)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task A_mount_without_the_faults_is_found_right_now()
    {
        var report = await Tier2Async(new FakeTelescope { SyncSlews = true }, Tier1());
        foreach (var id in new[]
                 {
                     "Diagnose.Arguments.CanMoveAxis", "Diagnose.Arguments.AxisRates", "Diagnose.Arguments.PulseGuide",
                     "Diagnose.Preconditions.SlewToCoordinatesAsync", "Diagnose.Preconditions.PulseGuide", "Diagnose.NotConnected",
                     "Diagnose.SlewSettleTime", "Diagnose.AxisRates.Axes", "Diagnose.DriverVersion"
                 })
            Assert.True(Finding(report, id).Outcome == Outcome.Info, $"{id}: {Finding(report, id).Outcome}: {Finding(report, id).Message}");
        Assert.Contains("Code not reviewed", Finding(report, "Diagnose.Arguments.AxisRates").Actual);
    }

    /// <summary>Tier 1 with only the settle time wrong, then the tier 2 and tier 3 runs the Run page offers after it.</summary>
    private async Task<(RunReport Tier2, RunReport Tier3)> SettleTiersAsync(FakeTelescope fake, SourceLocator? locator = null)
    {
        var tier1 = Tier1() with { Results = [Found("Motion.SlewSettleTime.Effect", "SlewSettleTime")] };
        var tier2 = await Tier2Async(fake, tier1, locator);
        var next = Tiers.Next(tier2);
        Assert.True(next is { Tier: 3 }, "tier 3 wasn't offered for SlewSettleTime");
        Assert.Equal(["SlewSettleTime"], next!.Members);
        var runner = new TestRunner(locator ?? SourceLocator.None);
        await using var session = new DirectSession(fake);
        var tier3 = await runner.RunAsync(session, GsServer, Tiers.Options(EngineTests.Everything, next), followsUp: tier2);
        Assert.DoesNotContain(tier3.Results, r => TestCatalog.For(r.Id) is null);
        foreach (var r in tier3.Results.Where(Tiers.IsDiagnosis))
            output.WriteLine($"{r.Outcome,-8} {r.Number} {r.Id}: {r.Message}\n  {r.Actual?.Replace("\n", "\n  ")}\n  FIX: {r.Fix}\n");
        return (tier2, tier3);
    }

    [Fact]
    public async Task Tier_3_finds_the_settle_time_ignored_and_the_wait_after_a_slew_is_its_final_correction()
    {
        var root = Path.Combine(Path.GetTempPath(), "gs-area-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (tier2, tier3) = await SettleTiersAsync(
                // With GSServer's RA jitter too: the readings' own jumps mustn't be taken for movement.
                new FakeTelescope(Defect.SettleTimeIgnored, Defect.SlewEndsWithPrecisionCorrection, Defect.PositionJitter), GsServerLikeSource(root));
            // Tier 2 sees the wait after every slew, the same with any setting: the setting adds nothing.
            Assert.Contains("adds nothing", Finding(tier2, "Diagnose.SlewSettleTime").Message);

            var cause = Finding(tier3, "Diagnose.SlewSettleTime.Cause");
            Assert.Equal(Outcome.Fail, cause.Outcome);
            Assert.Contains("SlewSettleTime is ignored", cause.Message);
            Assert.Contains("a final correction, not a wait", cause.Message);
            Assert.Contains("the same for every length and axis", cause.Message);
            Assert.Contains("aborted slew ends at once", cause.Message);
            Assert.DoesNotContain("still moving", cause.Message);
            Assert.Contains("the readings jump up to", cause.Actual);
            Assert.Contains(".TotalMilliseconds", cause.Fix);
            Assert.Contains("Move the settle wait after the final stage", cause.Fix);
            Assert.Contains("#region Final precision slew", cause.Actual);
            // The stage after the settle wait, followed into its method: its tries, waits and a .Milliseconds timing.
            Assert.Contains("The stage after it runs SimPrecisionGoto", cause.Actual);
            Assert.Contains("up to 6 tries, each waiting up to 3000 ms for the axes to stop, polling every 20 ms", cause.Actual);
            Assert.Contains("deltaTime = stopwatch1.Elapsed.Milliseconds", cause.Actual);
            Assert.Contains("SimPrecisionGoto also times itself with .Milliseconds", cause.Fix);

            var summary = Finding(tier3, "Diagnose.SlewSettleTime.Summary");
            Assert.Equal(Outcome.Fail, summary.Outcome);
            Assert.StartsWith("Final analysis of SlewSettleTime: tier 3 confirms tier 2.", summary.Message);
            Assert.Contains("Tier 2's recommendation:", summary.Expected);
            Assert.Equal([3005, 3006], tier3.Results.Where(Tiers.IsDiagnosis).Select(r => r.Number).Order());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Tier_3_puts_a_wait_with_the_mount_still_down_to_the_stage_after_the_settle_wait()
    {
        var root = Path.Combine(Path.GetTempPath(), "gs-area-" + Guid.NewGuid().ToString("N"));
        try
        {
            // GSServer as R0008 measured it: the setting ignored, then 1.5 s of Slewing with the mount still.
            var (_, tier3) = await SettleTiersAsync(
                new FakeTelescope(Defect.SettleTimeIgnored, Defect.SlewEndsWithStillWait, Defect.PositionJitter), GsServerLikeSource(root));
            var cause = Finding(tier3, "Diagnose.SlewSettleTime.Cause");
            Assert.Equal(Outcome.Fail, cause.Outcome);
            Assert.Contains("SlewSettleTime is ignored", cause.Message);
            Assert.Contains("a wait with the mount still", cause.Message);
            Assert.DoesNotContain("still moving", cause.Message);
            // The code review names the stage that is the wait, and what it does.
            Assert.Contains("is SimPrecisionGoto (Driver/SkySystem.cs:", cause.Fix);
            Assert.Contains("up to 6 tries", cause.Fix);
            Assert.Contains("Slewing true during it is right", cause.Fix);
            Assert.Contains("SimPrecisionGoto also times itself with .Milliseconds", cause.Fix);
            Assert.StartsWith("Final analysis of SlewSettleTime: tier 3 confirms tier 2.", Finding(tier3, "Diagnose.SlewSettleTime.Summary").Message);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Tier_3_finds_a_settle_time_that_works()
    {
        // Tier 1 said the settle time was wrong; the mount does it right now: tier 2 says so, and isn't followed up.
        var tier1 = Tier1() with { Results = [Found("Motion.SlewSettleTime.Effect", "SlewSettleTime")] };
        var tier2 = await Tier2Async(new FakeTelescope(), tier1);
        Assert.Equal(Outcome.Info, Finding(tier2, "Diagnose.SlewSettleTime").Outcome);
        Assert.Null(Tiers.Next(tier2));
    }

    /// <summary>A small copy of the GSServer 1.2.3.3 code the diagnoses review (the driver, and where it keeps its state).</summary>
    private static SourceLocator GsServerLikeSource(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "Driver"));
        File.WriteAllText(Path.Combine(root, "Driver", "Telescope.cs"), """
            public class Telescope
            {
                public bool Connected
                {
                    get
                    {
                        var r = SkySystem.Connected && !SkySystem.Connecting;
                        return r;
                    }
                    set
                    {
                        SkySystem.SetConnected(InstanceId, value);
                    }
                }

                public double RightAscension
                {
                    get
                    {
                        CheckSerialIO("RightAscension");
                        return SkyServer.RightAscension;
                    }
                }

                public bool Tracking
                {
                    get
                    {
                        CheckSerialIO("Tracking");
                        return SkyServer.Tracking;
                    }
                }

                public bool CanSlew
                {
                    get
                    {
                        return SkySettings.CanSlew;
                    }
                }

                public string DriverVersion
                {
                    get
                    {
                        var asm = Assembly.GetExecutingAssembly();
                        var r = asm.GetName().Version.ToString();
                        return r;
                    }
                }

                public IAxisRates AxisRates(TelescopeAxes axis)
                {
                    switch (axis)
                    {
                        case TelescopeAxes.axisPrimary:
                            return new AxisRates(TelescopeAxes.axisPrimary);
                        case TelescopeAxes.axisTertiary:
                            return new AxisRates(TelescopeAxes.axisTertiary);
                        default: return null;
                    }
                }

                public bool CanMoveAxis(TelescopeAxes axis)
                {
                    var r = SkyServer.CanMoveAxis(axis);
                    return r;
                }

                public void PulseGuide(GuideDirections direction, int duration)
                {
                    CheckSerialIO("PulseGuide");
                    //if (SkyServer.IsSlewing) { throw new InvalidValueException("Pulse rejected when slewing"); }
                    //if (!SkyServer.Tracking) { throw new InvalidValueException("Pulse rejected when tracking is off"); }
                    CheckRange(duration, 0, 30000, "PulseGuide", "Duration");
                    SkyServer.PulseGuide(direction, duration);
                }

                public void SlewToCoordinates(double ra, double dec)
                {
                    CheckSerialIO("SlewToCoordinates");
                    CheckTracking(true, "SlewToCoordinates");
                    SkyServer.SlewRaDec(ra, dec);
                }

                public void SlewToCoordinatesAsync(double ra, double dec)
                {
                    CheckSerialIO("SlewToCoordinatesAsync");
                    SkyServer.CycleOnTracking(true);
                    SkyServer.SlewRaDec(ra, dec);
                }

                public void SlewToTarget()
                {
                    CheckSerialIO("SlewToTarget");
                    CheckTracking(true, "SlewToTarget");
                }

                public void SlewToTargetAsync()
                {
                    CheckSerialIO("SlewToTargetAsync");
                    CheckTracking(true, "SlewToTargetAsync");
                }

                public void SyncToCoordinates(double ra, double dec)
                {
                    CheckSerialIO("SyncToCoordinates");
                    CheckTracking(true, "SyncToCoordinates");
                }

                public void SyncToTarget()
                {
                    CheckSerialIO("SyncToTarget");
                    CheckTracking(true, "SyncToTarget");
                }
            }
            """);
        File.WriteAllText(Path.Combine(root, "Driver", "SkySystem.cs"), """
            public static class SkySystem
            {
                public static bool Connected => ConnectStates.Count > 0;
                public static void SetConnected(long id, bool value) { if (value) ConnectStates.TryAdd(id, true); else ConnectStates.TryRemove(id, out _); }
            }
            public static class SkyServer
            {
                public static double SlewSettleTime { get; set; }
                private static void SlewEnded()
                {
                    if (SlewSettleTime > 0) Tasks.DelayHandler(TimeSpan.FromSeconds(SlewSettleTime).Milliseconds); // post-slew settling time
                    #region Final precision slew
                    SimPrecisionGoto();
                    #endregion
                }
                private static void Fault() => throw new NotConnectedException("Mount Not Connected");
                private static int SimPrecisionGoto()
                {
                    var maxTries = 0;
                    var deltaTime = 0.075;
                    while (true)
                    {
                        if (maxTries > 5) { break; }
                        maxTries++;
                        var stopwatch1 = Stopwatch.StartNew();
                        while (stopwatch1.Elapsed.TotalMilliseconds < 3000)
                        {
                            Thread.Sleep(20);
                        }
                        deltaTime = stopwatch1.Elapsed.Milliseconds * 0.001;
                    }
                    return 0;
                }
            }
            """);
        return new SourceLocator(new Dictionary<Codebase, CodebaseOptions>
        {
            [Codebase.GSServer] = new() { Root = root, TelescopeFiles = ["Driver/Telescope.cs"] }
        }, "");
    }
}
