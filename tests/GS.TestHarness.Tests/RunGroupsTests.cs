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

/// <summary>Run groups: a tier 1 report with the deeper reports that followed it up; Reviews and Summary can show one.</summary>
public class RunGroupsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private static CheckResult Problem(string id, string member)
        => new() { Id = id, Group = id[..id.IndexOf('.')], Member = member, Outcome = Outcome.Fail, Message = $"{id} failed", Fix = $"fix {member}" };

    private static RunReport Run(int tier, int minutes, string? followsUp = null, params CheckResult[] results) => new()
    {
        Target = new TestTarget { Codebase = Codebase.GSServer }, Started = T0.AddMinutes(minutes), Results = [.. results],
        Options = EngineTests.Everything with { Tier = tier, FollowsUp = followsUp }, Identity = new DeviceIdentity { Name = "Fake GEM" }
    };

    [Fact]
    public void A_tier_1_report_is_grouped_with_the_reports_that_follow_it_up_directly_or_through_each_other()
    {
        var t1 = Run(1, 0);
        var t2 = Run(2, 5, t1.Id);
        var t3 = Run(3, 10, t2.Id);
        var other = Run(1, 20);
        var groups = RunGroups.Build([t3, other, t1, t2]);
        Assert.Equal(2, groups.Count);
        // The newest first report first.
        Assert.Equal(other.Id, groups[0].Key);
        Assert.Equal([t1.Id, t2.Id, t3.Id], groups[1].Runs.Select(r => r.Id));
        Assert.Equal([t2.Id, t3.Id], groups[1].Deeper.Select(r => r.Id));
    }

    [Fact]
    public void A_deeper_report_whose_tier_1_report_was_deleted_forms_its_own_group()
    {
        var gone = Run(1, 0);
        var orphan = Run(2, 5, gone.Id);
        var group = Assert.Single(RunGroups.Build([orphan]));
        Assert.Equal(orphan.Id, group.Key);
    }

    [Fact]
    public void Find_returns_the_group_with_a_key_or_nothing()
    {
        var t1 = Run(1, 0);
        var groups = RunGroups.Build([t1]);
        Assert.Same(groups[0], RunGroups.Find(groups, t1.Id));
        Assert.Null(RunGroups.Find(groups, "nope"));
        Assert.Null(RunGroups.Find(groups, null));
    }

    [Fact]
    public void A_picked_group_shows_its_own_problems_where_the_default_shows_the_newest_run()
    {
        var older = Run(1, 0, null, Problem("Motion.SlewToCoordinates.West", "SlewToCoordinates"));
        var newer = Run(1, 60, null, Problem("Properties.Azimuth.Range", "Azimuth"));
        var all = new List<RunReport> { older, newer };

        var latest = RecommendationBuilder.Build(all);
        Assert.Equal(["Azimuth"], latest.Recommendations.Select(r => r.Member));

        var group = RunGroups.Find(RunGroups.Build(all), older.Id)!;
        var picked = RecommendationBuilder.BuildFrom([group.Root], all);
        Assert.Equal(["SlewToCoordinates"], picked.Recommendations.Select(r => r.Member));
        Assert.Equal([older.Id], picked.Runs.Select(r => r.Id));
    }

    [Fact]
    public void A_re_run_joins_the_group_of_the_report_it_re_ran_and_the_group_is_built_on_its_newest_tier_1_report()
    {
        var first = Run(1, 0, null, Problem("Motion.SlewToCoordinates.West", "SlewToCoordinates"));
        var again = Run(1, 30, null, Problem("Properties.Azimuth.Range", "Azimuth"));
        again = again with { Options = again.Options with { RerunOf = first.Id } };
        var t2 = Run(2, 40, again.Id);
        var all = new List<RunReport> { first, again, t2 };

        var group = Assert.Single(RunGroups.Build(all));
        Assert.Equal(first.Id, group.Key);
        Assert.Equal([first.Id, again.Id, t2.Id], group.Runs.Select(r => r.Id));
        Assert.Equal(again.Id, group.Latest.Id);

        // Reviews and Summary for the group are built on the re-run, not the first run.
        var set = RecommendationBuilder.BuildFrom([group.Latest], all);
        Assert.Equal(["Azimuth"], set.Recommendations.Select(r => r.Member));
    }

    [Fact]
    public void A_re_run_of_a_report_that_was_deleted_starts_a_group_of_its_own()
    {
        var gone = Run(1, 0);
        var again = Run(1, 30);
        again = again with { Options = again.Options with { RerunOf = gone.Id } };
        var group = Assert.Single(RunGroups.Build([again]));
        Assert.Equal(again.Id, group.Key);
    }

    [Fact]
    public void Two_tier_1_runs_nothing_links_stay_separate_groups()
        => Assert.Equal(2, RunGroups.Build([Run(1, 0), Run(1, 30)]).Count);
}
