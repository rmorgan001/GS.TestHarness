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

/// <summary>The Recommendations tab's sections (a tier 1 problem with its tier 2 and 3 diagnoses) and the Markdown for an AI agent.</summary>
public class AgentBriefTests
{
    private static readonly TestTarget Target = new() { Codebase = Codebase.GSServer, Host = "localhost", Port = 31416 };

    private static RunReport Run(int tier = 1) => new()
    {
        Target = Target, Options = EngineTests.Everything with { Tier = tier, AllowMotion = true },
        Identity = new DeviceIdentity { Name = "Fake GEM", DriverVersion = "1.2.3" }
    };

    private static Recommendation Rec(string id, string member, Outcome outcome, string fix, string? source = null, string? actual = null, int tier = 1)
    {
        var result = new CheckResult
        {
            Id = id, Group = id[..id.IndexOf('.')], Member = member, Outcome = outcome, Message = $"{id} found something",
            Expected = "settle time added", Actual = actual, Fix = fix, Source = source
        };
        return new Recommendation(Codebase.GSServer, id, result.Group, member, outcome, [fix], source is null ? [] : [source], null,
            [new RecommendationHit(Run(tier), result)]);
    }

    private static readonly Recommendation Tier1 = Rec("Motion.SlewSettleTime.Effect", "SlewSettleTime", Outcome.Fail, "Make SlewSettleTime add its wait to Slewing.", "Driver/Settle.cs:11");
    private static readonly Recommendation Tier2 = Rec("Diagnose.SlewSettleTime", "SlewSettleTime", Outcome.Fail, "Wait after the final stage.", "Driver/Settle.cs:12", tier: 2);
    private static readonly Recommendation Tier3 = Rec("Diagnose.SlewSettleTime.Cause", "SlewSettleTime", Outcome.Fail, "Use .TotalMilliseconds.", "Driver/Settle.cs:11", "line one\nline two", tier: 3);
    private static readonly Recommendation Other = Rec("Motion.SlewToCoordinates.West", "SlewToCoordinates", Outcome.Warning, "Check the target.");

    [Fact]
    public void The_test_numbers_of_the_ids_used_here_give_the_tiers_expected()
    {
        Assert.Equal(1, RecommendationSection.TierOf(Tier1));
        Assert.Equal(2, RecommendationSection.TierOf(Tier2));
        Assert.Equal(3, RecommendationSection.TierOf(Tier3));
    }

    [Fact]
    public void A_tier_1_problem_is_grouped_with_the_tier_2_and_3_diagnoses_of_its_member()
    {
        var sections = RecommendationSection.Group([Other, Tier3, Tier1, Tier2]);
        Assert.Equal(2, sections.Count);
        var settle = Assert.Single(sections, s => s.Member == "SlewSettleTime");
        Assert.Equal([Tier1], settle.Tier1);
        Assert.Equal([Tier2], settle.Tier2);
        Assert.Equal([Tier3], settle.Tier3);
        Assert.Equal(Outcome.Fail, settle.Worst);
        Assert.Equal("GSServer|SlewSettleTime", settle.Key);
        // The worst section first.
        Assert.Equal("SlewSettleTime", sections[0].Member);
    }

    [Fact]
    public void A_diagnosis_with_no_tier_1_problem_still_forms_a_section()
    {
        var section = Assert.Single(RecommendationSection.Group([Tier2]));
        Assert.Empty(section.Tier1);
        Assert.Single(section.Tier2);
    }

    [Fact]
    public void The_brief_says_what_it_is_and_has_each_tier_where_to_change_and_when_done()
    {
        var sections = RecommendationSection.Group([Tier1, Tier2, Tier3]);
        var set = new RecommendationSet([Run()], [Tier1, Tier2, Tier3], []);
        var text = AgentBrief.Build(sections, set);
        Assert.Contains("# Fix list: ASCOM ITelescopeV4 conformance problems", text);
        Assert.Contains("## What this is", text);
        Assert.Contains("## 1. `SlewSettleTime` in GSServer (Fail)", text);
        Assert.Contains("### Problem (tier 1)", text);
        Assert.Contains("### Cause analysis (tier 2)", text);
        Assert.Contains("### Confirmed cause and fix (tier 3)", text);
        Assert.Contains("Make SlewSettleTime add its wait to Slewing.", text);
        Assert.Contains("Use .TotalMilliseconds.", text);
        var where = text.Split("### Where to change the code")[1].Split("### Done when")[0];
        Assert.Single(where.Split('\n'), l => l.Contains("Driver/Settle.cs:11"));
        Assert.Contains("Driver/Settle.cs:12", where);
        Assert.Contains("### Done when", text);
        Assert.Contains("Diagnose.SlewSettleTime.Cause", text.Split("### Done when")[1]);
        // The multi-line "actual" is a code block, not run into the list.
        Assert.Contains("line one", text);
        Assert.Contains("```", text);
    }

    [Fact]
    public void The_brief_tells_the_agent_how_to_check_again_for_each_server()
    {
        // GSServer is a COM driver; a GreenSwampAlpaca server is reached over Alpaca.
        var com = Run();
        Assert.Equal("GS.TestHarness --run COM --progid ASCOM.GS.Sky.Telescope --motion", AgentBrief.CommandFor(com));
        var alpaca = com with { Target = new TestTarget { Codebase = Codebase.GreenSwampAlpaca, Host = "localhost", Port = 31416 } };
        Assert.Equal("GS.TestHarness --run Alpaca --host localhost --port 31416 --device 0 --motion", AgentBrief.CommandFor(alpaca));
        var text = AgentBrief.Build(RecommendationSection.Group([Tier1]), new RecommendationSet([com], [Tier1], []));
        Assert.Contains("Check again with: `GS.TestHarness --run COM", text);
    }

    [Fact]
    public void Only_the_chosen_sections_are_in_the_brief()
    {
        var sections = RecommendationSection.Group([Tier1, Other]);
        var chosen = sections.Where(s => s.Member == "SlewToCoordinates").ToList();
        var text = AgentBrief.Build(chosen, new RecommendationSet([Run()], [Tier1, Other], []));
        Assert.Contains("SlewToCoordinates", text);
        Assert.DoesNotContain("SlewSettleTime", text);
        Assert.Contains("1 problem area ", text);
    }
}
