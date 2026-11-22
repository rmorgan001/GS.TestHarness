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
using Xunit;

namespace GS.TestHarness.Tests;

public class ConformUSummaryTests
{
    private static CheckResult V(string member, string verdict, Outcome outcome, string message, string? expected = null)
        => new() { Id = $"ConformU.{member}.{verdict}", Group = "ConformU", Member = member, Outcome = outcome, Message = message, Expected = expected };

    private static RunReport Run(int tier, string? log, string? followsUp, DateTimeOffset at, params CheckResult[] results)
        => new()
        {
            Target = new TestTarget(), Options = EngineTests.Everything with { Tier = tier, FollowsUp = followsUp }, ConformULog = log,
            Started = at, Number = tier, Results = [.. results]
        };

    private const string Rate = "ConformU ISSUE (line 285, DeclinationRate Write -9.0): Rate +0.050 is outside the expected tolerance.\n" +
                                "ConformU ISSUE (line 316, DeclinationRate Write -9.0): Rate -0.050 is outside the expected tolerance.\n… and 6 more";

    [Fact]
    public void A_run_that_didnt_follow_a_ConformU_log_has_no_summary()
        => Assert.Null(ConformUSummary.Build([Run(1, null, null, DateTimeOffset.Now)]));

    [Fact]
    public void The_deepest_tiers_verdict_is_the_last_word_for_each_area_and_ConformUs_problems_are_counted()
    {
        var now = DateTimeOffset.Now;
        var tier1 = Run(1, "log.txt", null, now,
            new CheckResult { Id = "ConformU.Log", Group = "ConformU", Member = "ConformU", Outcome = Outcome.Info, Message = "ConformU log log.txt: 0 errors, 17 issues, 0 configuration alerts and 0 warnings, in 3 areas." },
            V("DeclinationRate", "NotReproduced", Outcome.Info, "Not reproduced: the harness's 11 checks all pass.", Rate),
            V("PulseGuide", "NotReproduced", Outcome.Info, "Not reproduced: the harness's 17 checks all pass.", "ConformU ISSUE (line 2091, PulseGuide +9.0 West): outside test tolerance"));
        var tier2 = Run(2, "log.txt", tier1.Id, now.AddMinutes(2),
            V("DeclinationRate", "Explained", Outcome.Info, "Explained, not a fault: ConformU's two readings can be that far out.", Rate));

        var summary = ConformUSummary.Build([tier2, tier1])!; // in any order
        Assert.Equal("log.txt", summary.FileName);
        Assert.Contains("17 issues", summary.LogLine);
        Assert.Equal(["DeclinationRate", "PulseGuide"], summary.Areas.Select(a => a.Member));

        var rate = summary.Areas[0];
        Assert.Equal(ConformUVerdict.Explained, rate.Verdict); // tier 2 replaced tier 1's "not reproduced"
        Assert.Equal(2, rate.Tier);
        Assert.Equal("Explained: not a fault", rate.Label);
        Assert.False(rate.Remains);
        Assert.Equal(8, rate.Problems); // 2 lines and "and 6 more"
        Assert.Contains("(and 7 more)", rate.ShortSaid);

        var pulse = summary.Areas[1];
        Assert.Equal(ConformUVerdict.NotReproduced, pulse.Verdict);
        Assert.Equal(1, pulse.Tier);
        Assert.Equal(1, pulse.Problems);

        Assert.False(summary.AnyRemain);
        Assert.Equal("2 areas: 1 explained (not a fault), 1 not reproduced", summary.Headline);
    }

    [Fact]
    public void A_reproduced_problem_is_flagged_as_remaining()
    {
        var run = Run(1, "log.txt", null, DateTimeOffset.Now, V("SlewToCoordinates", "Reproduced", Outcome.Fail, "Reproduced: 2 of the harness's 9 checks found a problem.", "ConformU ISSUE (line 5, x): y"));
        var summary = ConformUSummary.Build([run])!;
        Assert.True(summary.AnyRemain);
        Assert.Equal("Reproduced", summary.Areas[0].Label);
        Assert.Equal("1 area: 1 reproduced", summary.Headline);
    }

    private static (RunReport Tier1, ConformUSummary Summary) Real()
    {
        var now = DateTimeOffset.Now;
        var tier1 = Run(1, "log.txt", null, now,
            new CheckResult { Id = "ConformU.Log", Group = "ConformU", Member = "ConformU", Outcome = Outcome.Info, Message = "ConformU log log.txt: 0 errors, 17 issues, 0 configuration alerts and 0 warnings, in 3 areas." },
            V("DeclinationRate", "NotReproduced", Outcome.Info, "Not reproduced: the harness's 11 checks all pass.", Rate));
        var tier2 = Run(2, "log.txt", tier1.Id, now.AddMinutes(2),
            V("DeclinationRate", "Explained", Outcome.Info, "Explained, not a fault: ConformU's two | readings can be that far out.", Rate));
        return (tier1, ConformUSummary.Build([tier1, tier2])!);
    }

    [Fact]
    public void The_summary_as_markdown_is_a_table_of_areas_that_survives_a_pipe_in_a_message()
    {
        var md = Real().Summary.ToMarkdown();
        Assert.Contains("## What became of the problems in the ConformU log", md);
        Assert.Contains("17 issues", md);
        Assert.Contains("| DeclinationRate |", md);
        Assert.Contains("**Explained: not a fault** (tier 2,", md);
        Assert.Contains(@"two \| readings", md); // the pipe is escaped, so it does not split the row
    }

    [Fact]
    public void A_ConformU_reports_markdown_has_the_summary_and_another_reports_hasnt()
    {
        var (tier1, summary) = Real();
        Assert.Contains("What became of the problems in the ConformU log", ReportStore.ToMarkdown(tier1, conformU: summary));
        Assert.DoesNotContain("ConformU log", ReportStore.ToMarkdown(Run(1, null, null, DateTimeOffset.Now)));
    }

    [Fact]
    public void The_fix_list_says_which_of_the_logs_problems_were_ruled_out()
    {
        var text = AgentBrief.Build([], new RecommendationSet([], [], []), Real().Summary);
        Assert.Contains("What became of the problems in the ConformU log", text);
        Assert.Contains("do not change the server", text);
        Assert.DoesNotContain("What became of the problems", AgentBrief.Build([], new RecommendationSet([], [], [])));
    }
}
