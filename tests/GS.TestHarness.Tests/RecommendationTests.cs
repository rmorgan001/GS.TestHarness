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

public class RecommendationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 20, 0, 0, TimeSpan.Zero);

    private static CheckResult Result(string id, Outcome outcome, string? fix = "do this", string? source = null, string message = "m")
        => new() { Id = id, Group = id.Split('.')[0], Member = id.Split('.')[1], Outcome = outcome, Message = message, Fix = fix, Source = source };

    private static RunReport Run(Codebase codebase, string? hemisphere, int minutes, params CheckResult[] results)
        => new()
        {
            Target = new TestTarget { Codebase = codebase },
            Options = new RunOptions(),
            Started = T0.AddMinutes(minutes),
            Site = hemisphere is null ? null : hemisphere == "North" ? SiteLocation.DefaultNorth : SiteLocation.DefaultSouth,
            Identity = new DeviceIdentity { Name = codebase.ToString() },
            Results = [.. results]
        };

    [Fact]
    public void Only_the_latest_run_of_each_device_at_each_site_counts()
    {
        var old = Run(Codebase.GreenSwampAlpaca, "North", 0, Result("A.Fixed", Outcome.Fail));
        var latest = Run(Codebase.GreenSwampAlpaca, "North", 10, Result("A.Fixed", Outcome.Pass));
        var south = Run(Codebase.GreenSwampAlpaca, "South", 5, Result("A.Fixed", Outcome.Pass));

        var set = RecommendationBuilder.Build([old, latest, south]);

        Assert.Equal([latest, south], set.Runs);
        Assert.Empty(set.Recommendations);
        Assert.Equal(2, Assert.Single(set.Clean).Runs);
    }

    [Fact]
    public void A_check_is_listed_once_per_server_with_its_fixes_sources_and_runs()
    {
        var north = Run(Codebase.GreenSwampAlpaca, "North", 0,
            Result("P.SideOfPier", Outcome.Warning, "report the pointing state", "Telescope.cs:10", "north"));
        var south = Run(Codebase.GreenSwampAlpaca, "South", 1,
            Result("P.SideOfPier", Outcome.Fail, "report the pointing state", "Telescope.cs:10", "south"));
        var gs = Run(Codebase.GSServer, null, 2,
            Result("P.SideOfPier", Outcome.Fail, "flip the sign", "SkyServer.cs:99"));

        var set = RecommendationBuilder.Build([north, south, gs]);

        Assert.Equal(2, set.Recommendations.Count);
        var alpaca = set.Recommendations.Single(r => r.Codebase == Codebase.GreenSwampAlpaca);
        Assert.Equal(Outcome.Fail, alpaca.Worst);
        Assert.Equal(["report the pointing state"], alpaca.Fixes);
        Assert.Equal(["Telescope.cs:10"], alpaca.Sources);
        Assert.Equal(["south", "north"], alpaca.Hits.Select(h => h.Result.Message));
        var com = set.Recommendations.Single(r => r.Codebase == Codebase.GSServer);
        Assert.Equal(["flip the sign"], com.Fixes);
        Assert.Equal(["SkyServer.cs:99"], com.Sources);
    }

    [Fact]
    public void Worst_problems_come_first_and_info_or_skipped_are_not_recommendations()
    {
        var run = Run(Codebase.GreenSwampAlpaca, null, 0,
            Result("A.Warn", Outcome.Warning), Result("A.Error", Outcome.Error), Result("A.Fail", Outcome.Fail),
            Result("A.Info", Outcome.Info), Result("A.Skip", Outcome.Skipped), Result("A.Pass", Outcome.Pass));

        var set = RecommendationBuilder.Build([run]);

        Assert.Equal(["A.Fail", "A.Error", "A.Warn"], set.Recommendations.Select(r => r.Id));
        Assert.Equal(["A.Pass"], set.Clean.Select(c => c.Id));
    }

    [Fact]
    public void Different_fixes_are_all_kept_and_a_missing_fix_is_said_in_the_markdown()
    {
        var a = Run(Codebase.GreenSwampAlpaca, "North", 0, Result("A.X", Outcome.Fail, "one"), Result("A.NoFix", Outcome.Fail, fix: null));
        var b = Run(Codebase.GreenSwampAlpaca, "South", 1, Result("A.X", Outcome.Fail, "two"));

        var set = RecommendationBuilder.Build([a, b]);
        Assert.Equal(["two", "one"], set.Recommendations.Single(r => r.Id == "A.X").Fixes);

        var md = RecommendationBuilder.ToMarkdown(set);
        Assert.Contains("## GreenSwampAlpaca (2)", md);
        Assert.Contains("- Recommendation: one", md);
        Assert.Contains("- Recommendation: two", md);
        Assert.Contains("- Recommendation: none recorded", md);
    }

    [Fact]
    public void A_run_stopped_early_doesnt_hide_a_finished_one()
    {
        var finished = Run(Codebase.GreenSwampAlpaca, "North", 0, Result("A.Late", Outcome.Fail));
        var stopped = Run(Codebase.GreenSwampAlpaca, "North", 10, Result("A.Early", Outcome.Pass));
        stopped.Cancelled = true;
        var onlyStopped = Run(Codebase.GreenSwampAlpaca, "South", 5, Result("A.Early", Outcome.Pass));
        onlyStopped.Cancelled = true;

        var set = RecommendationBuilder.Build([finished, stopped, onlyStopped]);

        Assert.Equal([onlyStopped, finished], set.Runs);
        Assert.Equal("A.Late", Assert.Single(set.Recommendations).Id);
    }

    [Fact]
    public void Passed_in_counts_only_the_runs_where_the_check_passed()
    {
        var a = Run(Codebase.GreenSwampAlpaca, "North", 0, Result("A.X", Outcome.Pass));
        var b = Run(Codebase.GreenSwampAlpaca, "South", 1, Result("A.X", Outcome.Skipped));

        Assert.Equal(1, Assert.Single(RecommendationBuilder.Build([a, b]).Clean).Runs);
    }

    [Fact]
    public void No_runs_give_an_empty_set()
    {
        var set = RecommendationBuilder.Build([]);
        Assert.Empty(set.Runs);
        Assert.Empty(set.Recommendations);
        Assert.Contains("No problems found.", RecommendationBuilder.ToMarkdown(set));
    }
}
