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






    // ---------- ReportStore.Comparable ----------

    private static RunReport Report(int tier = 1, bool conformU = false, bool cancelled = false, IReadOnlyList<string>? groups = null)
        => new()
        {
            Target = new TestTarget(), Cancelled = cancelled, ConformULog = conformU ? "log.txt" : null,
            Options = EngineTests.Everything with { Tier = tier, Groups = groups }
        };

    [Fact]
    public void A_finished_whole_run_of_the_same_kind_is_comparable()
        => Assert.True(ReportStore.Comparable(Report(), new TestTarget(), 1, false));

    [Fact]
    public void A_run_of_another_tier_or_kind_isnt_comparable()
    {
        Assert.False(ReportStore.Comparable(Report(tier: 1), new TestTarget(), 2, false));
        Assert.False(ReportStore.Comparable(Report(conformU: true), new TestTarget(), 1, false));
        Assert.False(ReportStore.Comparable(Report(), new TestTarget(), 1, true));
    }

    [Fact]
    public void A_stopped_run_or_a_rerun_of_some_groups_isnt_a_baseline_for_a_full_check()
    {
        Assert.False(ReportStore.Comparable(Report(cancelled: true), new TestTarget(), 1, false));
        Assert.False(ReportStore.Comparable(Report(groups: ["Connection", "Motion"]), new TestTarget(), 1, false));
    }

    [Fact]
    public void Deeper_tiers_and_ConformU_runs_are_narrowed_by_design_and_stay_comparable()
    {
        Assert.True(ReportStore.Comparable(Report(tier: 2, groups: ["Diagnose"]), new TestTarget(), 2, false));
        Assert.True(ReportStore.Comparable(Report(conformU: true, groups: ["Motion"]), new TestTarget(), 1, true));
    }


    // ---------- Re-running a tier 2 or 3 run ----------

    private static RunReport Tier1WithAnActionProblem()
        => new()
        {
            Target = new TestTarget { Codebase = Codebase.GSServer }, Options = EngineTests.Everything,
            Results = [R("Common.Action.Unknown", Outcome.Fail, "Common", "Action")]
        };

    [Fact]
    public void A_tier_2_run_is_rerun_as_the_same_diagnosis_of_the_run_it_followed_up()
    {
        var tier1 = Tier1WithAnActionProblem();
        var next = Tiers.Next(tier1)!;
        var tier2 = new RunReport { Target = tier1.Target, Options = Tiers.Options(EngineTests.Everything, next) };
        var again = RerunPlan.SameTier(tier2, tier1);
        Assert.NotNull(again);
        Assert.Equal(2, again.Tier);
        Assert.Equal(next.Members, again.Members);
        Assert.Same(tier1, again.From);
    }

    [Fact]
    public void A_tier_1_run_or_another_runs_follow_up_isnt_rerun_as_a_tier()
    {
        var tier1 = Tier1WithAnActionProblem();
        var tier2 = new RunReport { Target = tier1.Target, Options = Tiers.Options(EngineTests.Everything, Tiers.Next(tier1)!) };
        Assert.Null(RerunPlan.SameTier(tier1, tier1));                       // tier 1: its groups are re-run instead
        Assert.Null(RerunPlan.SameTier(tier2, Tier1WithAnActionProblem()));  // a different run than the one it followed
        Assert.Null(RerunPlan.SameTier(tier2, null));                        // the run it followed is gone
    }
}
