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
    public void A_check_recorded_twice_in_one_run_is_one_hit()
    {
        var run = Run(Codebase.GreenSwampAlpaca, "North", 0,
            Result("A.Twice", Outcome.Warning, message: "first"), Result("A.Twice", Outcome.Fail, message: "second"));

        var r = Assert.Single(RecommendationBuilder.Build([run]).Recommendations);

        Assert.Equal(Outcome.Fail, r.Worst);
        Assert.Equal("second", Assert.Single(r.Hits).Result.Message);
    }

    [Fact]
    public void A_device_last_tested_on_an_older_build_is_left_out()
    {
        var oldPort = Run(Codebase.GreenSwampAlpaca, "North", 0, Result("A.Fixed", Outcome.Fail)) with
        {
            Target = new TestTarget { Codebase = Codebase.GreenSwampAlpaca, Port = 11111 }
        };
        oldPort.Identity = new DeviceIdentity { DriverVersion = "1.0" };
        var current = Run(Codebase.GreenSwampAlpaca, "North", 10, Result("A.Fixed", Outcome.Pass));
        current.Identity = new DeviceIdentity { DriverVersion = "1.1" };
        var otherDevice = Run(Codebase.GreenSwampAlpaca, "North", 5, Result("A.Fixed", Outcome.Pass)) with
        {
            Target = new TestTarget { Codebase = Codebase.GreenSwampAlpaca, DeviceNumber = 1 }
        };
        otherDevice.Identity = new DeviceIdentity { DriverVersion = "1.1" };

        var set = RecommendationBuilder.Build([oldPort, current, otherDevice]);

        Assert.Equal([current, otherDevice], set.Runs);
        Assert.Empty(set.Recommendations);
    }

    [Fact]
    public void A_run_with_no_version_is_kept()
    {
        var unknown = Run(Codebase.GreenSwampAlpaca, "North", 0, Result("A.X", Outcome.Fail)) with
        {
            Target = new TestTarget { Codebase = Codebase.GreenSwampAlpaca, Port = 11111 }
        };
        var current = Run(Codebase.GreenSwampAlpaca, "North", 10, Result("A.X", Outcome.Pass));
        current.Identity = new DeviceIdentity { DriverVersion = "1.1" };

        Assert.Equal([current, unknown], RecommendationBuilder.Build([unknown, current]).Runs);
    }

    [Fact]
    public void Markdown_keeps_each_value_inside_its_list_item()
    {
        var result = Result("A.X", Outcome.Fail, fix: "first line\r\nsecond line", source: "Odd`Name.cs:3") with
        {
            Actual = "Exception: boom\r\n   at Foo()\r\n   at Bar()"
        };

        var md = RecommendationBuilder.ToMarkdown(RecommendationBuilder.Build([Run(Codebase.GreenSwampAlpaca, null, 0, result)]));

        Assert.Contains("- Recommendation: first line second line", md);
        Assert.Contains("- Where: ``Odd`Name.cs:3``", md);
        Assert.Contains("    Exception: boom", md);
        Assert.Contains("       at Bar()", md);
        Assert.DoesNotContain("\r", md.Replace(Environment.NewLine, "\n"));
    }

    [Fact]
    public void A_failure_is_listed_before_an_error()
    {
        var run = Run(Codebase.GreenSwampAlpaca, null, 0, Result("A.Error", Outcome.Error), Result("B.Fail", Outcome.Fail));

        Assert.Equal(["B.Fail", "A.Error"], RecommendationBuilder.Build([run]).Recommendations.Select(r => r.Id));
    }

    [Fact]
    public void Compare_and_recommendations_pick_the_same_run()
    {
        var finished = Run(Codebase.GreenSwampAlpaca, "North", 0, Result("A.X", Outcome.Fail));
        var stopped = Run(Codebase.GreenSwampAlpaca, "North", 10, Result("A.X", Outcome.Pass));
        stopped.Cancelled = true;

        Assert.Same(finished, ReportStore.LatestAt([finished, stopped], finished.Target, "North"));
        Assert.Same(finished, Assert.Single(RecommendationBuilder.LatestRuns([finished, stopped])));
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
