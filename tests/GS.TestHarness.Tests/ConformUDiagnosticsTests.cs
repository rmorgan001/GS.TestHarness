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
/// The ConformU diagnoses tell the causes of a problem apart: each cause, put into the fake mount as a defect, gets
/// its own verdict, and a mount without it isn't diagnosed with anything.
/// </summary>
public class ConformUDiagnosticsTests(ITestOutputHelper output)
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

    private async Task<RunReport> DiagnoseAsync(string logText, MountKind mount, params Defect[] defects)
    {
        var log = await ConformULog.ParseAsync("conformu.txt", logText);
        var plan = log.Plan();
        // Only what the diagnoses need: the connection, the capabilities, and the ConformU group.
        var options = EngineTests.Everything with { Groups = ["Connection", "Capabilities", "ConformU"], Members = plan.Members, Mount = mount };
        var report = await EngineTests.RunAsync(new FakeTelescope(defects), options, target: new TestTarget { Codebase = Codebase.GSServer }, conformU: log);
        foreach (var r in report.Results.Where(r => r.Id.StartsWith("ConformU.Diagnose")))
            output.WriteLine($"{r.Outcome,-8} {r.Id}: {r.Message}\n  {r.Actual?.Replace("\n", "\n  ")}\n  FIX: {r.Fix}\n");
        return report;
    }

    private static CheckResult Finding(RunReport report, string id) => Assert.Single(report.Results, r => r.Id == $"ConformU.{id}");

    [Fact]
    public async Task A_right_rate_offset_is_not_diagnosed()
    {
        var report = await DiagnoseAsync(RateLog, MountKind.Simulator);
        Assert.Equal(Outcome.Info, Finding(report, "Diagnose.RightAscensionRate").Outcome);
        Assert.Equal(Outcome.Info, Finding(report, "Diagnose.Readings").Outcome);
    }

    [Fact]
    public async Task Jittery_readings_are_told_from_a_wrong_rate()
    {
        var report = await DiagnoseAsync(RateLog, MountKind.Simulator, Defect.PositionJitter);
        var d = Finding(report, "Diagnose.RightAscensionRate");
        Assert.Equal(Outcome.Warning, d.Outcome);
        Assert.Contains("The rate is right", d.Message);
        // The simulator's code path, not the SkyWatcher's.
        Assert.Contains("GS.Simulator/Controllers.cs", d.Fix);
        Assert.DoesNotContain("Real mount:", d.Fix);
    }

    [Fact]
    public async Task A_scaled_rate_offset_is_diagnosed_as_scaled()
    {
        var report = await DiagnoseAsync(RateLog, MountKind.RealMount, Defect.RaOffsetScaled);
        var d = Finding(report, "Diagnose.RightAscensionRate");
        Assert.Equal(Outcome.Fail, d.Outcome);
        Assert.Contains("scaled wrongly", d.Message);
        Assert.Contains("1.5", d.Message);
        Assert.Contains("SideSec2ArcSec", d.Fix);
        Assert.Contains("Real mount:", d.Fix);
        Assert.DoesNotContain("Simulator:", d.Fix);
    }

    [Fact]
    public async Task Pulses_that_move_right_are_not_diagnosed()
    {
        var report = await DiagnoseAsync(PulseLog, MountKind.Simulator);
        Assert.Equal(Outcome.Info, Finding(report, "Diagnose.PulseGuide.Dec").Outcome);
        Assert.Equal(Outcome.Info, Finding(report, "Diagnose.PulseGuide.RA").Outcome);
    }

    [Fact]
    public async Task A_stale_position_is_told_from_a_short_pulse()
    {
        var report = await DiagnoseAsync(PulseLog, MountKind.Simulator, Defect.StalePosition);
        foreach (var axis in new[] { "Dec", "RA" })
        {
            var d = Finding(report, $"Diagnose.PulseGuide.{axis}");
            Assert.Equal(Outcome.Warning, d.Outcome);
            Assert.Contains("stale", d.Message);
            Assert.Contains("DisplayInterval", d.Fix);
            Assert.Contains("GS.Simulator/Actions.cs", d.Fix);
        }
    }

    [Fact]
    public async Task Time_lost_in_every_pulse_is_measured()
    {
        var report = await DiagnoseAsync(PulseLog, MountKind.Unknown, Defect.PulseLosesTime);
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
    }

    [Fact]
    public async Task Without_motion_the_diagnoses_say_what_they_need()
    {
        var log = await ConformULog.ParseAsync("conformu.txt", PulseLog);
        var options = EngineTests.Everything with { AllowMotion = false, Groups = ["Connection", "Capabilities", "ConformU"], Members = log.Plan().Members };
        var report = await EngineTests.RunAsync(new FakeTelescope(), options, conformU: log);
        var d = Finding(report, "Diagnose.PulseGuide");
        Assert.Equal(Outcome.Skipped, d.Outcome);
        Assert.Contains("Move the mount", d.Message);
    }
}
