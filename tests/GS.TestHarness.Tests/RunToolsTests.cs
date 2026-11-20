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

/// <summary>The dry-run plan, the comparison with the last run, and re-running only the problems.</summary>
public class RunToolsTests
{
    private static CheckResult R(string id, Outcome outcome, string group = "Motion", string member = "Slew")
        => new() { Id = id, Group = group, Member = member, Outcome = outcome, Message = id };

    // ---------- RunPlan (--dry-run) ----------

    [Fact]
    public void The_plan_skips_the_groups_that_move_the_mount_when_motion_is_off()
    {
        var plan = RunPlan.For(new TestTarget(), EngineTests.Everything with { AllowMotion = false });
        Assert.Contains(plan, p => p.Group == "Motion" && p.SkipReason is not null);
        Assert.Contains(plan, p => p.Group == "ParkHome" && p.SkipReason is not null);
        Assert.Contains(plan, p => p.Group == "Connection" && p.SkipReason is null);
    }

    [Fact]
    public void The_plan_runs_the_groups_that_move_the_mount_when_motion_is_on()
    {
        var plan = RunPlan.For(new TestTarget(), EngineTests.Everything);
        Assert.Contains(plan, p => p.Group == "Motion" && p.SkipReason is null);
        Assert.Contains(plan, p => p.Group == "ConformU" && p.SkipReason is not null); // no log
        Assert.Contains(plan, p => p.Group == "Diagnose" && p.SkipReason is not null); // tier 1
    }

    [Fact]
    public void The_plan_leaves_out_groups_a_narrowed_run_doesnt_name()
    {
        var plan = RunPlan.For(new TestTarget(), EngineTests.Everything with { Groups = ["Connection", "Motion"] });
        Assert.Equal(["Connection", "Motion"], plan.Select(p => p.Group));
    }

    // ---------- RunComparison ----------

    [Fact]
    public void A_problem_that_wasnt_there_before_is_new()
    {
        var c = RunComparison.Compare([R("A", Outcome.Fail), R("B", Outcome.Pass)], [R("A", Outcome.Pass), R("B", Outcome.Pass)]);
        Assert.Equal(["A"], c.New.Select(r => r.Id));
        Assert.Empty(c.Fixed);
    }

    [Fact]
    public void A_problem_that_now_passes_is_fixed()
    {
        var c = RunComparison.Compare([R("A", Outcome.Pass)], [R("A", Outcome.Warning)]);
        Assert.Equal(["A"], c.Fixed.Select(r => r.Id));
        Assert.Empty(c.New);
    }

    [Fact]
    public void A_problem_whose_check_didnt_run_again_isnt_fixed()
    {
        var c = RunComparison.Compare([R("B", Outcome.Pass)], [R("A", Outcome.Fail)]);
        Assert.Empty(c.Fixed);
    }

    [Fact]
    public void A_problem_in_both_is_unchanged()
    {
        var c = RunComparison.Compare([R("A", Outcome.Fail)], [R("A", Outcome.Warning)]);
        Assert.Equal(1, c.Unchanged);
        Assert.Empty(c.New);
        Assert.Empty(c.Fixed);
    }

    // ---------- RerunPlan ----------

    [Fact]
    public void Nothing_is_rerun_when_there_are_no_problems()
        => Assert.Null(RerunPlan.For(EngineTests.Everything, [R("A", Outcome.Pass)]));

    [Fact]
    public void The_rerun_covers_the_problems_groups_and_members_with_the_connection()
    {
        var options = RerunPlan.For(EngineTests.Everything, [R("A", Outcome.Fail, "Motion", "SlewToCoordinates"), R("B", Outcome.Pass, "Settings", "X")])!;
        Assert.Contains("Motion", options.Groups!);
        Assert.Contains("Connection", options.Groups!);
        Assert.Contains("Capabilities", options.Groups!);
        Assert.DoesNotContain("Settings", options.Groups!);
        Assert.Equal(["SlewToCoordinates"], options.Members);
        Assert.Equal(1, options.Tier);
    }

    [Fact]
    public async Task A_rerun_finds_the_same_problem_without_running_the_other_groups()
    {
        // Saying the mount is southern gives a problem at latitude 28.5° north; re-run just that.
        var first = await EngineTests.RunAsync(new FakeTelescope(), EngineTests.Everything with { SiteHemisphere = Hemisphere.South });
        var again = RerunPlan.For(EngineTests.Everything with { SiteHemisphere = Hemisphere.South }, first.Results)!;
        var second = await EngineTests.RunAsync(new FakeTelescope(), again);
        Assert.Contains(second.Results, r => r.Id == "Properties.SiteHemisphere" && r.Outcome == Outcome.Warning);
        Assert.DoesNotContain(second.Results, r => r.Group == "Tracking");
        Assert.True(second.Results.Count < first.Results.Count / 2, $"{second.Results.Count} findings against {first.Results.Count}");
    }
}
