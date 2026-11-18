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

/// <summary>Reading a ConformU log, mapping its problems to check groups, and a run that follows one up.</summary>
public class ConformUTests(ITestOutputHelper output)
{
    // As ConformU writes them: time, test name padded to a column, outcome, message. Its summary at the end repeats
    // the problems; a DEBUG line and an exception's lines that follow a problem are in a debug log.
    private const string Log = """
        ConformU 4.1.0 (Build 12345) - Telescope conformance check
        10:31:22.118 Name                                OK       GS Sky Telescope
        10:31:22.120 DriverVersion                       OK       1.0.9.12
        10:31:22.200 Connected                           DEBUG    Get Connected = True
        10:31:23.441 SiteLatitude Write                  ISSUE    Invalid value 91 accepted, expected InvalidValueException
        10:31:24.512 Declination                         ISSUE    Declination is outside the range -90 to +90
        10:31:25.001 CanMoveAxis:Primary                 OK       True
        10:31:26.734 SlewToCoordinates                   ERROR    Exception: System.Runtime.InteropServices.COMException
                  at ASCOM.GS.Sky.Telescope.SlewToCoordinates(Double RightAscension, Double Declination)
                  at ConformU.TelescopeTester.TelescopeSlewTest()

        10:31:27.002 CommandString                       ERROR    Unexpected exception: timed out
        10:31:28.000 Mount Safety                        CONFIGURATION ALERT The mount's park position is below the horizon

        Conformance test has finished

        Your driver had 2 errors, 2 issues and 1 configuration alert

        Error Summary
        10:31:26.734 SlewToCoordinates                   ERROR    Exception: System.Runtime.InteropServices.COMException
        10:31:27.002 CommandString                       ERROR    Unexpected exception: timed out
        Issue Summary
        10:31:23.441 SiteLatitude Write                  ISSUE    Invalid value 91 accepted, expected InvalidValueException
        10:31:24.512 Declination                         ISSUE    Declination is outside the range -90 to +90
        """;

    [Fact]
    public async Task A_log_is_read_with_its_problems_once_each()
    {
        var log = await ConformULog.ParseAsync("conformu.txt", Log);
        Assert.True(log.Recognised);
        Assert.Equal("4.1.0", log.ConformUVersion);
        Assert.Equal("GS Sky Telescope", log.DeviceName);
        Assert.Equal("1.0.9.12", log.DriverVersion);
        Assert.Equal(1, log.DebugLines);
        Assert.Equal(2, log.Count(ConformUSeverity.Error));
        Assert.Equal(2, log.Count(ConformUSeverity.Issue));
        Assert.Equal(1, log.Count(ConformUSeverity.Alert));

        var slew = Assert.Single(log.Problems, p => p.Test == "SlewToCoordinates");
        Assert.Contains("TelescopeSlewTest", slew.Detail);
        Assert.Equal(["SiteLatitude", "Declination", "SlewToCoordinates", "CommandString", "MountSafety"],
            log.Areas.Select(a => a.Member).OrderBy(m => log.Problems.First(p => ConformUMap.Map(p).Member == m).Line));
    }

    [Fact]
    public async Task A_real_ConformU_4_6_debug_log_is_read()
    {
        // ConformU 4.6.0 against GSServer (COM), debug logging on: 22k lines, 12 ISSUEs each repeated in the summary,
        // test names with the rate or direction after the member ("RightAscensionRate Write -9.0", "PulseGuide +3.0 North").
        var path = Path.Combine(AppContext.BaseDirectory, "TestFiles", "ASCOM.ConformU.0138.445020.txt");
        using var reader = File.OpenText(path);
        var log = await ConformULog.ParseAsync(Path.GetFileName(path), reader);

        Assert.True(log.Recognised);
        Assert.True(log.IsDebug);
        Assert.Equal("4.6.0.56430", log.ConformUVersion);
        Assert.Equal("GSServer", log.DeviceName);
        Assert.Equal("1.2.3.3", log.DriverVersion);
        Assert.Equal(12, log.Count(ConformUSeverity.Issue));
        Assert.Equal(0, log.Count(ConformUSeverity.Error) + log.Count(ConformUSeverity.Warning) + log.Count(ConformUSeverity.Alert));
        // Debug lines between problems are result lines, not a problem's detail.
        Assert.All(log.Problems, p => Assert.Null(p.Detail));

        Assert.Equal(["RightAscensionRate", "PulseGuide"], log.Areas.Select(a => a.Member));
        Assert.All(log.Areas, a => Assert.Equal(6, a.Problems.Count));
        Assert.Equal("Properties,Settings,Motion,Tracking", string.Join(",", log.Areas[0].Groups));
        Assert.Equal("Motion", string.Join(",", log.Areas[1].Groups));

        var plan = log.Plan();
        Assert.Equal(["Connection", "Common", "Capabilities", "Properties", "Methods", "Settings", "Motion", "Tracking", "ConformU", "Performance"], plan.Groups);
        Assert.True(plan.NeedsMotion);
        Assert.True(plan.NeedsExtended);
    }

    [Theory]
    [InlineData("CanMoveAxis:Primary", "", "CanMoveAxis", "Capabilities")]
    [InlineData("CanSlewAsync", "", "CanSlewAsync", "Capabilities")]
    [InlineData("SideOfPier Model Tests", "", "SideOfPier", "Properties,Motion")]
    [InlineData("TargetDeclination Write", "", "TargetDeclination", "Properties,Settings,Motion")]
    [InlineData("AxisRate:Primary", "", "AxisRates", "Methods,Motion")]
    [InlineData("Parked:SlewToCoordinates", "", "SlewToCoordinates", "Motion,ParkHome")]
    [InlineData("GET Connected", "", ConformUMap.Protocol, "Protocol")]
    [InlineData("Post-run Checks", "Tracking was left on", "Tracking", "Properties,Settings,Motion,Tracking")]
    [InlineData("CommandString", "", "CommandString", "Common")]
    [InlineData("Mount Safety", "below the horizon", "MountSafety", "")]
    public void A_test_maps_to_its_member_and_groups(string test, string message, string member, string groups)
    {
        var (m, g) = ConformUMap.Map(new ConformUProblem(1, "00:00:00.000", test, ConformUSeverity.Issue, message));
        Assert.Equal(member, m);
        Assert.Equal(groups, string.Join(",", g));
    }

    [Fact]
    public async Task The_plan_runs_the_read_only_groups_and_the_ones_the_problems_need()
    {
        var plan = (await ConformULog.ParseAsync("conformu.txt", Log)).Plan();
        Assert.Equal(["Connection", "Common", "Capabilities", "Properties", "Methods", "Settings", "Motion", "ConformU", "Performance"], plan.Groups);
        Assert.True(plan.NeedsMotion);
        Assert.True(plan.NeedsWrites);
        Assert.False(plan.NeedsExtended);
    }

    [Fact]
    public async Task A_run_says_for_each_problem_whether_it_was_reproduced()
    {
        var log = await ConformULog.ParseAsync("conformu.txt", Log);
        var options = EngineTests.Everything with { AllowMotion = false, Groups = log.Plan().Groups };
        var report = await EngineTests.RunAsync(new FakeTelescope(Defect.AcceptsInvalidLatitude), options, conformU: log);
        foreach (var r in report.Results.Where(r => r.Group == "ConformU"))
            output.WriteLine($"{r.Outcome,-8} {r.Id}: {r.Message}");

        Assert.Equal("conformu.txt", report.ConformULog);
        // Only the planned groups ran: none of the others is even mentioned.
        Assert.DoesNotContain(report.Results, r => r.Group is "Disconnected" or "ParkHome" or "Clients" or "Tracking");
        Assert.Contains(report.Results, r => r.Id == "ConformU.Log");
        Assert.Equal(Outcome.Fail, Assert.Single(report.Results, r => r.Id == "ConformU.SiteLatitude.Reproduced").Outcome);
        Assert.Equal(Outcome.Info, Assert.Single(report.Results, r => r.Id == "ConformU.Declination.NotReproduced").Outcome);
        var slew = Assert.Single(report.Results, r => r.Id == "ConformU.SlewToCoordinates.NotChecked");
        Assert.Contains("motion is turned off", slew.Message);
        Assert.Single(report.Results, r => r.Id == "ConformU.CommandString.NotReproduced");
        Assert.Equal(Outcome.Warning, Assert.Single(report.Results, r => r.Id == "ConformU.MountSafety.NoCheck").Outcome);
    }

    [Fact]
    public async Task A_run_narrowed_to_the_problem_members_reports_only_them()
    {
        var log = await ConformULog.ParseAsync("conformu.txt", Log);
        var plan = log.Plan();
        Assert.Equal(["CommandString", "Declination", "MountSafety", "SiteLatitude", "SlewToCoordinates"], plan.Members.Order());

        var all = await EngineTests.RunAsync(new FakeTelescope(Defect.AcceptsInvalidLatitude),
            EngineTests.Everything with { AllowMotion = false, Groups = plan.Groups }, conformU: log);
        var narrowed = await EngineTests.RunAsync(new FakeTelescope(Defect.AcceptsInvalidLatitude),
            EngineTests.Everything with { AllowMotion = false, Groups = plan.Groups, Members = plan.Members }, conformU: log);
        output.WriteLine($"{all.Results.Count} findings in the whole groups, {narrowed.Results.Count} narrowed");

        Assert.True(narrowed.Results.Count < all.Results.Count / 2);
        // Every finding is about a problem member, a ConformU verdict, or says why a check wasn't made.
        string[] explains = ["Group", "Prepare", "CommunicationLost", "Stopped", "Unexpected"];
        Assert.All(narrowed.Results.Where(r => r.Group != "ConformU"), r => Assert.True(
            plan.Members.Contains(r.Member) || r.Id.Split('.').Skip(1).Any(plan.Members.Contains) || explains.Contains(r.Id.Split('.')[^1]),
            $"{r.Id} ({r.Member}) isn't about a problem member"));
        // The same verdicts as with the whole groups.
        foreach (var id in new[] { "ConformU.SiteLatitude.Reproduced", "ConformU.Declination.NotReproduced", "ConformU.SlewToCoordinates.NotChecked",
                     "ConformU.CommandString.NotReproduced", "ConformU.MountSafety.NoCheck" })
            Assert.Equal(Assert.Single(all.Results, r => r.Id == id).Outcome, Assert.Single(narrowed.Results, r => r.Id == id).Outcome);
        // Other settings weren't written: only SiteLatitude's checks ran in Settings.
        Assert.DoesNotContain(narrowed.Results, r => r.Group == "Settings" && r.Member != "SiteLatitude");
    }

    [Fact]
    public async Task A_property_no_check_reached_is_probed()
    {
        var log = await ConformULog.ParseAsync("conformu.txt", Log);
        // Without the property checks, nothing else reads Declination.
        var options = EngineTests.Everything with { AllowMotion = false, Groups = ["Connection", "Capabilities", "ConformU"] };
        var report = await EngineTests.RunAsync(new FakeTelescope(), options, conformU: log);
        var probe = Assert.Single(report.Results, r => r.Id == "ConformU.Declination.Probe");
        Assert.Equal(Outcome.Info, probe.Outcome);
        Assert.DoesNotContain(report.Results, r => r.Id.StartsWith("ConformU.Declination.Not"));
    }

    [Fact]
    public async Task An_ordinary_run_has_no_ConformU_findings()
    {
        var report = await EngineTests.RunAsync(new FakeTelescope(), EngineTests.Everything with { AllowMotion = false });
        Assert.DoesNotContain(report.Results, r => r.Group == "ConformU");
    }

    [Fact]
    public async Task Only_a_log_with_DEBUG_lines_is_a_debug_log()
    {
        Assert.True((await ConformULog.ParseAsync("debug.txt", Log)).IsDebug);
        // The same log without its DEBUG line: what ConformU writes with debug logging off.
        var normal = await ConformULog.ParseAsync("normal.txt", string.Join('\n', Log.Split('\n').Where(l => !l.Contains(" DEBUG "))));
        Assert.True(normal.Recognised);
        Assert.False(normal.IsDebug);
    }

    [Fact]
    public async Task A_file_that_isnt_a_ConformU_log_is_not_recognised()
    {
        var log = await ConformULog.ParseAsync("notes.txt", "Just some notes.\nNothing to see.");
        Assert.False(log.Recognised);
        Assert.Empty(log.Areas);
    }
}
