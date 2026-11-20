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

namespace GS.TestHarness.Tests;

/// <summary>The gate before the groups that move the mount: it asks (Proceed, Skip, Stop) only when something looks unsafe.</summary>
public class GateTests
{
    // The fake mount is at latitude 28.5° (north), so saying it is southern gives a warning to ask about.
    private static readonly RunOptions Unsafe = EngineTests.Everything with { SiteHemisphere = Hemisphere.South };

    private static async Task<(RunReport Report, List<IReadOnlyList<string>> Asked)> RunAsync(RunOptions options, GateDecision? answer,
        bool unattended = false, bool leaveHome = false)
    {
        var asked = new List<IReadOnlyList<string>>();
        var fake = new FakeTelescope();
        if (leaveHome)
        {
            // The mount has been used: it is pointing somewhere, not at home.
            fake.Connect();
            fake.SlewToCoordinatesAsync(1, 10);
            await Task.Delay(1500);
        }
        await using var session = new DirectSession(fake);
        var clients = 0;
        var report = await new TestRunner(SourceLocator.None).RunAsync(session, new TestTarget(), options,
            openSession: () => Task.FromResult<ITelescopeSession>(new DirectSession(fake, Interlocked.Increment(ref clients))),
            unattended: unattended,
            onGate: answer is null ? null : warnings =>
            {
                asked.Add(warnings);
                return Task.FromResult(answer.Value);
            });
        return (report, asked);
    }

    [Fact]
    public async Task Nothing_is_asked_when_nothing_looks_unsafe()
    {
        var (report, asked) = await RunAsync(EngineTests.Everything, GateDecision.Stop);
        Assert.Empty(asked);
        Assert.DoesNotContain(report.Results, r => r.Id.EndsWith(".Gate"));
        Assert.Contains(report.Results, r => r.Id.StartsWith("Motion.Slew"));
    }

    [Fact]
    public async Task Stop_ends_the_run_before_the_mount_moves()
    {
        var (report, asked) = await RunAsync(Unsafe, GateDecision.Stop);
        var warnings = Assert.Single(asked);
        Assert.Contains(warnings, w => w.Contains("southern"));
        Assert.True(report.Cancelled);
        Assert.Contains(report.Results, r => r.Id == "Motion.Gate" && r.Outcome == Outcome.Info);
        Assert.DoesNotContain(report.Results, r => r.Id.StartsWith("Motion.Slew"));
        Assert.DoesNotContain(report.Results, r => r.Id.StartsWith("ParkHome."));
        // Stop ends the run: the groups after the moving ones (the disconnected checks) don't run either.
        Assert.DoesNotContain(report.Results, r => r.Id.StartsWith("Disconnected."));
    }

    [Fact]
    public async Task Skip_leaves_out_the_groups_that_move_the_mount_and_runs_the_rest()
    {
        var (report, asked) = await RunAsync(Unsafe, GateDecision.Skip);
        Assert.Single(asked);
        Assert.False(report.Cancelled);
        Assert.DoesNotContain(report.Results, r => r.Id.StartsWith("Motion.Slew"));
        Assert.Contains(report.Results, r => r.Id == "Motion.Group" && r.Outcome == Outcome.Skipped);
        Assert.Contains(report.Results, r => r.Id == "ParkHome.Group" && r.Outcome == Outcome.Skipped);
        // The groups after them still ran.
        Assert.Contains(report.Results, r => r.Id.StartsWith("Disconnected."));
    }

    [Fact]
    public async Task Proceed_runs_the_mount_moving_groups_and_records_the_warnings()
    {
        var (report, asked) = await RunAsync(Unsafe, GateDecision.Proceed);
        Assert.Single(asked);
        Assert.Contains(report.Results, r => r.Id == "Motion.Gate" && r.Outcome == Outcome.Warning && r.Message.Contains("chose Proceed"));
        Assert.Contains(report.Results, r => r.Id.StartsWith("Motion.Slew"));
    }

    [Fact]
    public async Task Turned_off_in_the_options_nothing_is_asked_or_recorded()
    {
        var (report, asked) = await RunAsync(Unsafe with { SafetyWarnings = false }, GateDecision.Stop);
        Assert.Empty(asked);
        Assert.DoesNotContain(report.Results, r => r.Id.EndsWith(".Gate"));
        Assert.False(report.Cancelled);
        Assert.Contains(report.Results, r => r.Id.StartsWith("Motion.Slew"));
    }

    [Fact]
    public async Task A_mount_away_from_home_is_a_warning_when_someone_can_be_asked()
    {
        var (_, asked) = await RunAsync(EngineTests.Everything, GateDecision.Proceed, leaveHome: true);
        Assert.Contains(Assert.Single(asked), w => w.Contains("home position"));
    }

    [Fact]
    public async Task A_mount_away_from_home_is_only_informational_on_the_command_line()
    {
        var (report, asked) = await RunAsync(EngineTests.Everything, GateDecision.Stop, unattended: true, leaveHome: true);
        Assert.Empty(asked);
        Assert.False(report.Cancelled);
        // The start-position finding still records it.
        Assert.Contains(report.Results, r => r.Id == "Motion.StartPosition" && r.Outcome == Outcome.Warning);
    }

    [Fact]
    public async Task With_nobody_to_ask_the_run_goes_on_and_says_so()
    {
        var (report, asked) = await RunAsync(Unsafe, null);
        Assert.Empty(asked);
        Assert.Contains(report.Results, r => r.Id == "Motion.Gate" && r.Outcome == Outcome.Info && r.Message.Contains("nobody was asked"));
        Assert.Contains(report.Results, r => r.Id.StartsWith("Motion.Slew"));
    }

    [Fact]
    public void The_gates_finding_in_a_tier_2_or_3_run_is_not_a_diagnosis()
    {
        var gate = new CheckResult { Id = "Diagnose.Gate", Group = "Diagnose", Member = "Motion", Outcome = Outcome.Warning, Message = "x" };
        var real = gate with { Id = "Diagnose.SlewSettleTime.Cause" };
        Assert.False(Tiers.IsDiagnosis(gate));
        Assert.True(Tiers.IsDiagnosis(real));
    }
}
