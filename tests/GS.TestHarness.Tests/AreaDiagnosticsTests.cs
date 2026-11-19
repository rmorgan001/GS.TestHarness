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

using ASCOM.Common.DeviceInterfaces;
using GS.TestHarness.Engine;
using GS.TestHarness.Engine.Checks;
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
    // ---------- Arguments and rate offsets (R0010, the ASCOM Alpaca Simulators 0.5.0) ----------

    private static readonly TestTarget Alpaca = new() { Codebase = Codebase.GreenSwampAlpaca, Host = "127.0.0.1", Port = 32323 };

    /// <summary>The argument and rate offset problems R0010 found, as findings, from the server <paramref name="server"/>.</summary>
    private static RunReport SimulatorTier1(string server = "ASCOM Alpaca Simulators 0.5.0+1c01cfc6 (ASCOM Initiative)") => new()
    {
        Target = Alpaca,
        Options = EngineTests.Everything,
        Identity = new DeviceIdentity { Name = "Alpaca Telescope Simulator", ServerDescription = server },
        Results =
        [
            Found("Methods.DestinationSideOfPier.InvalidRa", "DestinationSideOfPier"),
            Found("Methods.DestinationSideOfPier.InvalidDec", "DestinationSideOfPier"),
            Found("Settings.GuideRateRightAscension.InvalidLow", "GuideRateRightAscension"),
            Found("Settings.GuideRateDeclination.InvalidLow", "GuideRateDeclination"),
            Found("Motion.RightAscensionRate.ZeroAtLunar", "RightAscensionRate"),
            Found("Motion.DeclinationRate.ZeroAtLunar", "DeclinationRate"),
            Found("Motion.RightAscensionRate.ZeroAtSolar", "RightAscensionRate"),
            Found("Motion.DeclinationRate.ZeroAtSolar", "DeclinationRate")
        ]
    };

    private async Task<RunReport> SimulatorTier2Async(FakeTelescope fake, RunReport tier1, SourceLocator? locator = null)
    {
        var next = Tiers.Next(tier1);
        Assert.True(next is { Tier: 2 }, "tier 2 wasn't offered");
        Assert.Equal([AreaDiagnostics.Arguments, AreaDiagnostics.RateOffsets], next!.Members.Order());
        Assert.Empty(Tiers.OutlookFor(tier1).Uncovered);
        var runner = new TestRunner(locator ?? SourceLocator.None);
        await using var session = new DirectSession(fake);
        var report = await runner.RunAsync(session, Alpaca, Tiers.Options(EngineTests.Everything, next), followsUp: tier1);
        Assert.DoesNotContain(report.Results, r => TestCatalog.For(r.Id) is null);
        foreach (var r in report.Results.Where(Tiers.IsDiagnosis))
            output.WriteLine($"{r.Outcome,-8} {r.Number} {r.Id}: {r.Message}\n  {r.Actual?.Replace("\n", "\n  ")}\n  FIX: {r.Fix}\n");
        return report;
    }

    [Fact]
    public async Task Tier_2_takes_apart_unchecked_ranges_and_offsets_that_carry_on_at_other_rates()
    {
        var root = Path.Combine(Path.GetTempPath(), "gs-harness-sim-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fake = new FakeTelescope(Defect.DestinationSideOfPierUnchecked, Defect.GuideRateNegativeAccepted,
                Defect.RateOffsetKeptAtOtherRates, Defect.RateOffsetAppliedAtOtherRates);
            // The source is this server's (it names itself as the server answering does).
            var report = await SimulatorTier2Async(fake, SimulatorTier1("Simulator Like Server 1.0 (Somebody)"), SimulatorLikeSource(root, "Simulator Like Server"));

            var side = Finding(report, "Diagnose.Arguments.DestinationSideOfPier");
            Assert.Equal(Outcome.Fail, side.Outcome);
            Assert.Contains("doesn't check the coordinates at all", side.Message);
            Assert.Contains("ra 25: accepted", side.Actual);
            Assert.Contains("It checks raDec, worked out from the arguments", side.Actual);
            Assert.Contains("Check the arguments themselves first", side.Fix);
            Assert.Contains("names SlewToCoordinatesAsync", side.Fix);

            var guide = Finding(report, "Diagnose.Arguments.GuideRateRightAscension");
            Assert.Equal(Outcome.Fail, guide.Outcome);
            Assert.Contains("doesn't check the value at all", guide.Message);
            Assert.Contains("nothing checks the value", guide.Actual);
            Assert.Contains("GuideRateDeclination", guide.Actual); // the same kind of argument unchecked there too
            Assert.Equal(Outcome.Fail, Finding(report, "Diagnose.Arguments.GuideRateDeclination").Outcome);

            var ra = Finding(report, "Diagnose.RateOffsets.RightAscensionRate");
            Assert.Equal(Outcome.Fail, ra.Outcome);
            Assert.Contains("carries on at Lunar, Solar", ra.Message);
            Assert.Contains("(still applied)", ra.Actual);
            Assert.Contains("its getter looks at TrackingRate", ra.Actual);
            Assert.Contains("TrackingRate's setter", ra.Fix);
            var dec = Finding(report, "Diagnose.RateOffsets.DeclinationRate");
            Assert.Equal(Outcome.Fail, dec.Outcome);
            Assert.Contains("its getter doesn't look at TrackingRate", dec.Actual);
            Assert.Contains("RightAscensionRate does the same", dec.Actual);

            // Everything put back.
            await using var session = new DirectSession(fake);
            var after = await session.RunAsync(t => { t.Connected = true; return (t.GuideRateRightAscension, t.TrackingRate, t.RightAscensionRate); }, TimeSpan.FromSeconds(5));
            Assert.Equal((0.004, DriveRate.Sidereal, 0.0), after);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(Defect.RateOffsetKeptAtOtherRates, "only RightAscensionRate's reading is wrong", "(not applied)")]
    [InlineData(Defect.RateOffsetAppliedAtOtherRates, "reads 0 at Lunar, but the mount still moves", "(still applied)")]
    public async Task Tier_2_tells_a_wrong_reading_from_an_offset_still_applied(Defect defect, string pattern, string measured)
    {
        var report = await SimulatorTier2Async(new FakeTelescope(defect), SimulatorTier1());
        var ra = Finding(report, "Diagnose.RateOffsets.RightAscensionRate");
        Assert.Equal(Outcome.Fail, ra.Outcome);
        Assert.Contains(pattern, ra.Message);
        Assert.Contains(measured, ra.Actual);
    }

    [Fact]
    public async Task Tier_2_finds_the_arguments_and_offsets_right_and_reviews_no_code_of_another_server()
    {
        var root = Path.Combine(Path.GetTempPath(), "gs-harness-sim-" + Guid.NewGuid().ToString("N"));
        try
        {
            // The source on this computer is another server's: R0010's case (the ASCOM simulator answering, GreenSwampAlpaca's source here).
            var report = await SimulatorTier2Async(new FakeTelescope(), SimulatorTier1(), SimulatorLikeSource(root, "Green Swamp Alpaca Server"));
            foreach (var id in new[]
                     {
                         "Diagnose.Arguments.DestinationSideOfPier", "Diagnose.Arguments.GuideRateRightAscension", "Diagnose.Arguments.GuideRateDeclination",
                         "Diagnose.RateOffsets.RightAscensionRate", "Diagnose.RateOffsets.DeclinationRate"
                     })
            {
                var r = Finding(report, id);
                Assert.True(r.Outcome == Outcome.Info, $"{id}: {r.Outcome}: {r.Message}");
                Assert.Contains("the server answering is ASCOM Alpaca Simulators, not GreenSwampAlpaca", r.Actual);
            }
            Assert.Contains("(not applied)", Finding(report, "Diagnose.RateOffsets.DeclinationRate").Actual);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("ASCOM Alpaca Simulators 0.5.0+1c01cfc6 (ASCOM Initiative)", "ASCOM Alpaca Simulators")]
    [InlineData("Green Swamp Alpaca Server 1.0.0 (Green Swamp Software)", "Green Swamp Alpaca Server")]
    [InlineData("Some Server (Maker)", "Some Server")]
    [InlineData("", null)]
    public void The_server_name_is_the_words_before_its_version(string description, string? name)
        => Assert.Equal(name, CheckContext.ServerNameOf(description));

    /// <summary>A small server like GreenSwampAlpaca's, naming itself <paramref name="serverName"/>.</summary>
    private static SourceLocator SimulatorLikeSource(string root, string serverName)
    {
        Directory.CreateDirectory(Path.Combine(root, "Driver"));
        File.WriteAllText(Path.Combine(root, "Program.cs"), $$"""
            internal static class Program
            {
                internal const string ServerName = "{{serverName}}";
            }
            """);
        File.WriteAllText(Path.Combine(root, "Driver", "Telescope.cs"), """
            public class Telescope
            {
                public double RightAscensionRate
                {
                    get
                    {
                        var r = (TrackingRate == DriveRate.Sidereal) ? _mount.RateRaCurrent : 0.0;
                        return r;
                    }
                    set
                    {
                        CheckRate(value);
                        _mount.RateRaCurrent = value;
                    }
                }

                public double DeclinationRate
                {
                    get
                    {
                        return _mount.RateDecCurrent;
                    }
                    set
                    {
                        CheckRate(value);
                        _mount.RateDecCurrent = value;
                    }
                }

                public DriveRate TrackingRate
                {
                    get
                    {
                        return _mount.Settings.TrackingRate;
                    }
                    set
                    {
                        CheckTrackingRate("TrackingRate", value);
                        _mount.Settings.TrackingRate = value;
                    }
                }

                public double GuideRateRightAscension
                {
                    get
                    {
                        return _mount.GuideRateRa;
                    }
                    set
                    {
                        _mount.GuideRateRa = value;
                    }
                }

                public double GuideRateDeclination
                {
                    get
                    {
                        return _mount.GuideRateDec;
                    }
                    set
                    {
                        _mount.GuideRateDec = value;
                    }
                }

                public PointingState DestinationSideOfPier(double RightAscension, double Declination)
                {
                    var raDec = Transforms.CoordTypeToInternal(RightAscension, Declination);
                    CheckRange(raDec.X, 0, 24, "SlewToCoordinatesAsync", "RightAscension");
                    CheckRange(raDec.Y, -90, 90, "SlewToCoordinatesAsync", "Declination");
                    return _mount.DetermineSideOfPier(raDec.X, raDec.Y);
                }
            }
            """);
        return new SourceLocator(new Dictionary<Codebase, CodebaseOptions>
        {
            [Codebase.GreenSwampAlpaca] = new() { Root = root, TelescopeFiles = ["Driver/Telescope.cs"] }
        }, "");
    }

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
