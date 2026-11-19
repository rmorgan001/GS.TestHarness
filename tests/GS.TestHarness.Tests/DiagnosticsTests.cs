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
/// The diagnoses (tiers 2 and 3) tell the causes of a problem apart: each cause, put into the fake mount as a defect,
/// gets its own verdict, and a mount without it isn't diagnosed with anything. Every run starts at tier 1 and goes
/// deeper only as far as each tier offers the next.
/// </summary>
public class DiagnosticsTests(ITestOutputHelper output)
{
    // As in the ConformU 4.6 debug log of GSServer's simulator (TestFiles/ASCOM.ConformU.0138.445020.txt).
    private const string RateLog = """
        01:38:47.746 ASCOM Universal Device Conformance Checker Version 4.6.0.56430, Build time: Mon 26 October 2026 15:12:58
        01:43:28.508 RightAscensionRate Write -9.0       DEBUG    Starting test
        01:44:05.790 RightAscensionRate Write -9.0       ISSUE    Rate:  -0.003 - RightAscensionRate is outside the expected tolerance. Expected:  -0.0033, Actual:  -0.0051, Deviation from expected: 52.91%, Tolerance:5%, Test duration: 10 seconds.
        """;

    private const string PulseLog = """
        01:38:47.746 ASCOM Universal Device Conformance Checker Version 4.6.0.56430, Build time: Mon 26 October 2026 15:12:58
        01:49:13.884 PulseGuide -9.0 South               DEBUG    Test guiding direction: South
        01:49:18.899 PulseGuide -9.0 South               ISSUE    Moved south but outside test tolerance - Declination change (DMS): -00:00:28.8, Expected: -00:00:30.1, Difference: +00:00:01.3, Test tolerance: +00:00:01.0
        01:49:28.949 PulseGuide -9.0 West                ISSUE    Moved west but outside test tolerance - RA change (HMS): -00:00:02.40, Expected: -00:00:02.51, Difference: +00:00:00.11, Test tolerance: +00:00:00.07
        """;

    private static readonly TestTarget GsServer = new() { Codebase = Codebase.GSServer };

    /// <summary>
    /// Follows a ConformU log up as the Run page does: tier 1 first (only the connection and capabilities checks, to
    /// keep it quick), then each deeper tier the run before offers, up to <paramref name="tier"/>.
    /// </summary>
    private async Task<RunReport> DiagnoseAsync(string logText, MountKind mount, int tier, params Defect[] defects)
    {
        var log = await ConformULog.ParseAsync("conformu.txt", logText);
        var fake = new FakeTelescope(defects);
        var options = EngineTests.Everything with { Groups = ["Connection", "Capabilities", "ConformU"], Members = log.Plan().Members, Mount = mount };
        var report = await EngineTests.RunAsync(fake, options, target: GsServer, conformU: log);
        // Tier 1 finds; it never diagnoses.
        Assert.DoesNotContain(report.Results, Tiers.IsDiagnosis);
        report = await DeeperAsync(fake, report, options, tier, log);
        foreach (var r in report.Results.Where(Tiers.IsDiagnosis))
            output.WriteLine($"{r.Outcome,-8} {r.Id}: {r.Message}\n  {r.Actual?.Replace("\n", "\n  ")}\n  FIX: {r.Fix}\n");
        return report;
    }

    /// <summary>Each next tier <paramref name="report"/> offers, in turn, up to <paramref name="tier"/>: as the Run page's button does.</summary>
    internal static async Task<RunReport> DeeperAsync(FakeTelescope fake, RunReport report, RunOptions options, int tier, ConformULog? log = null)
    {
        while (report.Options.Tier < tier)
        {
            var next = Tiers.Next(report);
            Assert.True(next is not null, $"tier {report.Options.Tier + 1} wasn't offered after tier {report.Options.Tier}");
            Assert.Equal(report.Options.Tier + 1, next!.Tier);
            report = await EngineTests.RunAsync(fake, Tiers.Options(options, next), target: GsServer, conformU: log, followsUp: report);
            Assert.Equal(next.Tier, report.Options.Tier);
        }
        return report;
    }

    /// <summary>
    /// Tier 3's final analysis of RightAscensionRate: it compares itself with the tier 2 diagnosis it follows up (shown,
    /// with that diagnosis's recommendation, under Expected), and the ConformU verdict takes its word.
    /// </summary>
    private static CheckResult Summary(RunReport report, string verdict, Outcome outcome)
    {
        var s = Finding(report, "Diagnose.RightAscensionRate.Summary");
        Assert.Equal(outcome, s.Outcome);
        Assert.StartsWith("Final analysis of RightAscensionRate: tier 3 " + verdict, s.Message);
        Assert.Contains("Tier 2 (ConformU.Diagnose.RightAscensionRate, Fail)", s.Expected);
        Assert.Contains("Tier 2's recommendation:", s.Expected);
        if (outcome != Outcome.Info) Assert.False(string.IsNullOrWhiteSpace(s.Fix), "the final analysis has no recommendation");
        Assert.Contains(report.Results, r => r.Group == "ConformU" && r.Member == "RightAscensionRate"
                                             && r.Message.Contains("Final analysis of RightAscensionRate", StringComparison.Ordinal));
        return s;
    }

    private static CheckResult Finding(RunReport report, string id) => Assert.Single(report.Results, r => r.Id == $"ConformU.{id}");

    [Fact]
    public async Task A_right_rate_offset_is_not_diagnosed()
    {
        var report = await DiagnoseAsync(RateLog, MountKind.Simulator, 2);
        Assert.Equal(Outcome.Info, Finding(report, "Diagnose.RightAscensionRate").Outcome);
        Assert.Equal(Outcome.Info, Finding(report, "Diagnose.Readings").Outcome);
        Assert.Equal(Outcome.Info, Finding(report, "RightAscensionRate.NotReproduced").Outcome);
        // Nothing found at tier 2: no tier 3 to go to.
        Assert.Null(Tiers.Next(report));
    }

    [Fact]
    public async Task Jittery_readings_are_told_from_a_wrong_rate()
    {
        var report = await DiagnoseAsync(RateLog, MountKind.Simulator, 2, Defect.PositionJitter);
        var d = Finding(report, "Diagnose.RightAscensionRate");
        Assert.Equal(Outcome.Warning, d.Outcome);
        Assert.Contains("The rate is right", d.Message);
        // The readings' general scatter, not one stray reading.
        Assert.Contains("the readings wander up to", d.Message);
        // The report shows where ConformU's two readings sat against the line through all of them.
        Assert.Contains("first and last readings", d.Actual);
        // The simulator's code path, not the SkyWatcher's.
        Assert.Contains("GS.Simulator/Controllers.cs", d.Fix);
        Assert.DoesNotContain("Real mount:", d.Fix);
        // The verdict follows the diagnosis: explained by the readings, not reproduced in the rate.
        var verdict = Finding(report, "RightAscensionRate.Explained");
        Assert.Equal(Outcome.Warning, verdict.Outcome);
        Assert.Equal(d.Fix, verdict.Fix);
        Assert.DoesNotContain(report.Results, r => r.Id == "ConformU.RightAscensionRate.Reproduced");
    }

    [Fact]
    public async Task A_stray_reading_is_told_from_the_readings_scatter()
    {
        var report = await DiagnoseAsync(RateLog, MountKind.Simulator, 2, Defect.StrayReadingAfterRateSet);
        var d = Finding(report, "Diagnose.RightAscensionRate");
        Assert.Equal(Outcome.Warning, d.Outcome);
        Assert.Contains("A single stray reading, not the rate", d.Message);
        Assert.DoesNotContain("scatter by 0.000 s with no offset", d.Message);
        Assert.Contains("Run ConformU again", d.Fix);
        Assert.Equal(Outcome.Warning, Finding(report, "RightAscensionRate.Explained").Outcome);
    }

    [Fact]
    public async Task A_scaled_rate_offset_is_diagnosed_as_scaled()
    {
        var report = await DiagnoseAsync(RateLog, MountKind.RealMount, 2, Defect.RaOffsetScaled);
        var d = Finding(report, "Diagnose.RightAscensionRate");
        Assert.Equal(Outcome.Fail, d.Outcome);
        Assert.Contains("scaled wrongly", d.Message);
        Assert.Contains("1.5", d.Message);
        Assert.Contains("SideSec2ArcSec", d.Fix);
        Assert.Contains("Real mount:", d.Fix);
        Assert.DoesNotContain("Simulator:", d.Fix);
        var verdict = Finding(report, "RightAscensionRate.Reproduced");
        Assert.Equal(Outcome.Fail, verdict.Outcome);
        Assert.Contains("diagnosed", verdict.Message);
    }

    // ---------- "Right at 40"/s but not at 0.05"/s": three causes the diagnosis must tell apart ----------
    // At 0.05"/s ConformU's offset moves RA by only 0.0033 s each second, so anything adding even 0.0007 s/s, or a reading
    // error of 0.001 s, shows as a 20% error while vanishing at 40"/s. Today all three get the same verdict ("about N × the
    // expected rate ... rounded, held back or ignored"), with N an average of the two signs.

    [Fact]
    public async Task Drift_with_no_offset_is_told_from_a_wrong_small_offset()
    {
        // The offset is right; RA moves 0.0007 s/s with no offset at all: +0.05"/s measures 1.21 ×, -0.05"/s 0.79 ×.
        var report = await DiagnoseAsync(RateLog, MountKind.Simulator, 3, Defect.RaTrackingDrift);
        var d = Finding(report, "Diagnose.RightAscensionRate.Cause");
        Assert.Equal(Outcome.Fail, d.Outcome);
        // Measured with no offset (a control run as long as the offset runs), and named as a drift, not an offset fault.
        Assert.Contains("with no offset", d.Message);
        Assert.Contains("drift", d.Message);
        Assert.Matches(@"0\.000[67]\d* s", d.Message);
        // Not averaged across the signs into a meaningless "1.00 ×", and not blamed on the offset.
        Assert.DoesNotContain("1.00 ×", d.Message);
        Assert.DoesNotContain("held back or ignored", d.Message);
        // Where GSServer adds to the tracking rate, not the offset's conversion.
        Assert.Contains("CurrentTrackingRate", d.Fix);
        Assert.Contains("RaTrackingOffset", d.Fix);
        Assert.DoesNotContain("SideSec2ArcSec", d.Fix);
        Assert.Equal(Outcome.Fail, Finding(report, "RightAscensionRate.Reproduced").Outcome);
        // The final analysis: tier 2's fault confirmed, and its cause (a drift) named.
        var summary = Summary(report, "confirms tier 2's fault in the driver", Outcome.Fail);
        Assert.Contains("drift", summary.Message);
        Assert.Contains("RaTrackingOffset", summary.Fix);
    }

    [Fact]
    public async Task Readings_that_wander_slowly_are_told_from_a_wrong_small_offset()
    {
        // The rate is right; the readings carry a slow, bounded error. Over ConformU's short test it looks like a rate
        // error, but measured for longer it shrinks (a wrong rate's doesn't), and it isn't there with no offset either.
        var report = await DiagnoseAsync(RateLog, MountKind.Simulator, 3, Defect.SlowPositionWander);
        var d = Finding(report, "Diagnose.RightAscensionRate.Cause");
        Assert.Equal(Outcome.Warning, d.Outcome);
        Assert.Contains("The rate is right", d.Message);
        // Tells how it knows: the error shrank as the measurement got longer.
        Assert.Contains("longer", d.Message);
        Assert.Contains("GS.Simulator/Controllers.cs", d.Fix);
        Assert.DoesNotContain("held back or ignored", d.Message);
        Assert.Equal(Outcome.Warning, Finding(report, "RightAscensionRate.Explained").Outcome);
        Assert.DoesNotContain(report.Results, r => r.Id == "ConformU.RightAscensionRate.Reproduced");
        // The final analysis: tier 2 called it a fault; tier 3 overturns that, the readings explain it.
        var summary = Summary(report, "overturns tier 2: the driver's rate is right", Outcome.Warning);
        Assert.Contains("GS.Simulator/Controllers.cs", summary.Fix);
    }

    [Fact]
    public async Task A_small_offset_rounded_up_is_still_diagnosed()
    {
        // The real fault the current message describes: 1.20 × at both signs, at every length of measurement, no drift.
        var report = await DiagnoseAsync(RateLog, MountKind.Simulator, 3, Defect.SmallRaOffsetRounded);
        var d = Finding(report, "Diagnose.RightAscensionRate.Cause");
        Assert.Equal(Outcome.Fail, d.Outcome);
        Assert.Contains("1.20", d.Message);
        // Faster than asked: rounded up, so not "held back or ignored".
        Assert.Contains("rounded", d.Message);
        Assert.DoesNotContain("held back", d.Message);
        Assert.DoesNotContain("drift", d.Message);
        Assert.Equal(Outcome.Fail, Finding(report, "RightAscensionRate.Reproduced").Outcome);
        // The final analysis: tier 2's fault confirmed, as a small offset rounded up.
        var summary = Summary(report, "confirms tier 2's fault in the driver", Outcome.Fail);
        Assert.Contains("rounded", summary.Message);
        Assert.DoesNotContain("held back", summary.Message);
    }

    [Fact]
    public async Task Pulses_that_move_right_are_not_diagnosed()
    {
        var report = await DiagnoseAsync(PulseLog, MountKind.Simulator, 2);
        Assert.Equal(Outcome.Info, Finding(report, "Diagnose.PulseGuide.Dec").Outcome);
        Assert.Equal(Outcome.Info, Finding(report, "Diagnose.PulseGuide.RA").Outcome);
        Assert.Equal(Outcome.Info, Finding(report, "PulseGuide.NotReproduced").Outcome);
    }

    [Fact]
    public async Task A_stale_position_is_told_from_a_short_pulse()
    {
        var report = await DiagnoseAsync(PulseLog, MountKind.Simulator, 2, Defect.StalePosition);
        foreach (var axis in new[] { "Dec", "RA" })
        {
            var d = Finding(report, $"Diagnose.PulseGuide.{axis}");
            Assert.Equal(Outcome.Warning, d.Outcome);
            Assert.Contains("stale", d.Message);
            Assert.Contains("DisplayInterval", d.Fix);
            Assert.Contains("GS.Simulator/Actions.cs", d.Fix);
        }
        var verdict = Finding(report, "PulseGuide.Explained");
        Assert.Equal(Outcome.Warning, verdict.Outcome);
        Assert.Contains("stale", verdict.Message);
    }

    [Fact]
    public async Task Time_lost_in_every_pulse_is_measured()
    {
        var report = await DiagnoseAsync(PulseLog, MountKind.Unknown, 2, Defect.PulseLosesTime);
        foreach (var axis in new[] { "Dec", "RA" })
        {
            var d = Finding(report, $"Diagnose.PulseGuide.{axis}");
            Assert.Equal(Outcome.Fail, d.Outcome);
            Assert.Contains("loses about", d.Message);
            // 100 ms lost, give or take the measurement.
            var ms = int.Parse(System.Text.RegularExpressions.Regex.Match(d.Message, @"loses about (\d+) ms").Groups[1].Value);
            Assert.InRange(ms, 70, 130);
            // The Mount option isn't set: both code paths are listed.
            Assert.Contains("Simulator:", d.Fix);
            Assert.Contains("Real mount:", d.Fix);
        }
        Assert.Equal(Outcome.Fail, Finding(report, "PulseGuide.Reproduced").Outcome);
        // PulseGuide has no tier 3 diagnosis.
        Assert.Null(Tiers.Next(report));
    }

    [Fact]
    public async Task Without_motion_the_diagnoses_say_what_they_need()
    {
        var log = await ConformULog.ParseAsync("conformu.txt", PulseLog);
        var options = EngineTests.Everything with { AllowMotion = false, Groups = ["Connection", "Capabilities", "ConformU"], Members = log.Plan().Members };
        var fake = new FakeTelescope();
        // Tier 1 offers tier 2 even with motion off; tier 2 then says what it needs.
        var report = await DeeperAsync(fake, await EngineTests.RunAsync(fake, options, target: GsServer, conformU: log), options, 2, log);
        var d = Finding(report, "Diagnose.PulseGuide");
        Assert.Equal(Outcome.Skipped, d.Outcome);
        Assert.Contains("Move the mount", d.Message);
    }

    // ---------- Tier 1 first, then each deeper tier only as offered ----------

    [Fact]
    public async Task A_ConformU_run_starts_at_tier_1_and_offers_tier_2_for_what_it_can_diagnose()
    {
        var log = await ConformULog.ParseAsync("conformu.txt", RateLog);
        var options = EngineTests.Everything with { Groups = ["Connection", "Capabilities", "ConformU"], Members = log.Plan().Members };
        var report = await EngineTests.RunAsync(new FakeTelescope(), options, target: GsServer, conformU: log);
        Assert.Equal(1, report.Options.Tier);
        Assert.DoesNotContain(report.Results, Tiers.IsDiagnosis);
        // ConformU found the problem, so tier 2 is offered even though tier 1 didn't reproduce it.
        var next = Assert.IsType<NextTier>(Tiers.Next(report));
        Assert.Equal(2, next.Tier);
        Assert.Equal(["RightAscensionRate"], next.Members);
        var deeper = Tiers.Options(options, next);
        Assert.Equal(report.Id, deeper.FollowsUp);
        Assert.Equal(["Connection", "Capabilities", "ConformU"], deeper.Groups);
    }

    [Fact]
    public async Task A_conformance_run_offers_tier_2_for_the_tier_1_checks_a_diagnosis_follows_up()
    {
        // Tier 1: the tracking checks find RightAscensionRate moving the mount 1.5 times too fast.
        var fake = new FakeTelescope(Defect.RaOffsetScaled);
        var options = EngineTests.WithExtended with { Groups = ["Connection", "Capabilities", "Tracking"], Mount = MountKind.Simulator };
        var tier1 = await EngineTests.RunAsync(fake, options, target: GsServer);
        Assert.Contains(tier1.Results, r => r.Id == "Tracking.RightAscensionRate.Moves" && r.IsProblem);
        // Tier 2 diagnoses it in its own group, and says which tier 1 check it follows up.
        var tier2 = await DeeperAsync(fake, tier1, options, 2);
        var d = Assert.Single(tier2.Results, r => r.Id == "Diagnose.RightAscensionRate");
        Assert.Equal(Outcome.Fail, d.Outcome);
        Assert.Contains("scaled wrongly", d.Message);
        Assert.Contains("Tracking.RightAscensionRate.Moves", d.Expected);
        // Only the diagnosis and what it needs ran.
        Assert.DoesNotContain(tier2.Results, r => r.Group is "Tracking" or "Motion" or "ConformU");
        // It found a fault, so tier 3 is offered next.
        Assert.Equal(3, Tiers.Next(tier2)?.Tier);
    }

    [Fact]
    public async Task A_conformance_run_with_no_problem_offers_no_deeper_tier()
    {
        var options = EngineTests.WithExtended with { Groups = ["Connection", "Capabilities", "Tracking"] };
        Assert.Null(Tiers.Next(await EngineTests.RunAsync(new FakeTelescope(), options, target: GsServer)));
    }

    [Fact]
    public async Task A_deeper_tier_only_follows_up_the_tier_below()
    {
        var log = await ConformULog.ParseAsync("conformu.txt", RateLog);
        var fake = new FakeTelescope();
        var options = EngineTests.Everything with { Groups = ["Connection", "Capabilities", "ConformU"], Members = ["RightAscensionRate"] };
        // Tier 2 with no tier 1 run to follow up.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EngineTests.RunAsync(fake, options with { Tier = 2 }, target: GsServer, conformU: log));
        Assert.Contains("run tier 1 first", ex.Message);
        // Tier 3 straight after tier 1.
        var tier1 = await EngineTests.RunAsync(fake, options, target: GsServer, conformU: log);
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EngineTests.RunAsync(fake, options with { Tier = 3, FollowsUp = tier1.Id }, target: GsServer, conformU: log, followsUp: tier1));
        Assert.Contains("follows up a tier 2 run", ex.Message);
        // Tier 2 for a member tier 1 found nothing about.
        Assert.NotNull(Tiers.Problem(options with { Tier = 2, FollowsUp = tier1.Id, Members = ["PulseGuide"] }, tier1, conformU: true));
        // A ConformU run's tier 2 needs its log.
        Assert.NotNull(Tiers.Problem(options with { Tier = 2, FollowsUp = tier1.Id }, tier1, conformU: false));
    }
}
