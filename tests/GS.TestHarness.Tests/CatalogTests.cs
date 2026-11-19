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

/// <summary>
/// The Tests page's catalog. That every finding a run makes is in it is checked by every run in EngineTests
/// (<see cref="EngineTests.RunAsync"/>); these check the catalog itself.
/// </summary>
public class CatalogTests
{
    [Fact]
    public void Every_entry_is_complete_and_cites_known_references()
    {
        var keys = TestCatalog.References.Select(r => r.Key).ToHashSet();
        foreach (var e in TestCatalog.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Objective), $"{e.Group}.{e.Id}: no objective");
            Assert.False(string.IsNullOrWhiteSpace(e.Method), $"{e.Group}.{e.Id}: no method");
            Assert.False(string.IsNullOrWhiteSpace(e.Verification), $"{e.Group}.{e.Id}: no verification");
            Assert.NotEmpty(e.Refs);
            Assert.All(e.Refs, r => Assert.Contains(r, keys));
        }
    }

    [Fact]
    public void Every_entry_is_shown_under_a_listed_group_and_no_id_is_listed_twice()
    {
        var groups = TestCatalog.Groups.Select(g => g.Name).ToHashSet();
        Assert.All(TestCatalog.Entries, e => Assert.Contains(TestCatalog.DisplayGroup(e), groups));
        var duplicates = TestCatalog.Entries.GroupBy(e => $"{e.Group}.{e.Id}").Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(duplicates);
    }

    [Theory]
    [InlineData("Motion.SlewToCoordinatesAsync.InvalidRaLow", "{slew}.InvalidRaLow")]
    [InlineData("Motion.SlewToCoordinatesAsync.West", "SlewToCoordinatesAsync.West")]
    [InlineData("Motion.MoveAxis.Primary.Speed", "MoveAxis.{axis}.Speed")]
    [InlineData("Motion.Flip.Goto.EastToWest.Flipped", "Flip.Goto.{direction}.Flipped")]
    [InlineData("ParkHome.Parked.TrackingOn", "Parked.TrackingOn")]
    [InlineData("Settings.TrackingRate.UnsupportedKing", "TrackingRate.Unsupported{rate}")]
    [InlineData("Motion.Group", "Group")]
    public void A_finding_finds_its_most_specific_entry(string findingId, string entryId)
        => Assert.Equal(entryId, TestCatalog.For(findingId)?.Id);

    [Fact]
    public void Every_check_has_a_tier_and_a_deeper_one_follows_up_checks_one_tier_below()
    {
        foreach (var e in TestCatalog.Entries)
        {
            Assert.InRange(e.Tier, 1, Tiers.Max);
            if (e.Tier == 1)
            {
                Assert.True(e.FollowsUp.Count == 0, $"{e.Key}: a tier 1 check follows up nothing");
                continue;
            }
            Assert.True(e.FollowsUp.Count > 0, $"{e.Key}: a tier {e.Tier} check must say which tier {e.Tier - 1} checks it follows up");
            foreach (var key in e.FollowsUp)
            {
                var below = TestCatalog.Entry(key);
                Assert.True(below is not null, $"{e.Key} follows up {key}, which isn't in the catalog");
                Assert.True(below!.Tier == e.Tier - 1, $"{e.Key} (tier {e.Tier}) follows up {key}, which is tier {below.Tier}, not {e.Tier - 1}");
            }
        }
    }

    [Fact]
    public void Every_test_has_its_own_number_and_the_number_starts_with_its_tier()
    {
        foreach (var e in TestCatalog.Entries)
        {
            Assert.True(e.Number > 0, $"{e.Key} has no number: add it to TestNumbers.All with the next number of tier {e.Tier} ({e.Tier}001 on)");
            Assert.True(TestNumbers.TierOf(e.Number) == e.Tier, $"{e.Key} is tier {e.Tier}, but its number {e.Number} starts with {TestNumbers.TierOf(e.Number)}");
        }
        // Numbers and keys are given once; a retired test keeps its number, and every other key is a test.
        Assert.DoesNotContain(TestNumbers.All.GroupBy(t => t.Number), g => g.Count() > 1);
        Assert.DoesNotContain(TestNumbers.All.GroupBy(t => t.Key), g => g.Count() > 1);
        Assert.All(TestNumbers.All, t => Assert.InRange(t.Number % 1000, 1, 999));
        Assert.All(TestNumbers.All.Where(t => !t.Key.StartsWith(TestNumbers.Retired)),
            t => Assert.True(TestCatalog.Entry(t.Key) is not null, $"{t.Number} is {t.Key}, which isn't in the catalog: mark it {TestNumbers.Retired} if it was removed"));
    }

    [Fact]
    public void The_rate_and_pulse_guide_tests_lead_to_their_deeper_tests_by_number()
    {
        var chains = TestCatalog.Chains();
        var ra = Assert.Single(chains, c => c.Test.Key == "Tracking.RightAscensionRate.Moves");
        Assert.Equal([2001, 2003], ra.Tier2.Select(e => e.Number));
        Assert.Equal([3001, 3002], ra.Tier3.Select(e => e.Number));
        var pulse = Assert.Single(chains, c => c.Test.Key == "Motion.PulseGuide.{direction}.Moves");
        Assert.Equal([2001, 2002], pulse.Tier2.Select(e => e.Number));
        Assert.Empty(pulse.Tier3);
        // The other way round: each test lists every deeper test that follows it up, through all the tiers.
        Assert.Equal([2001, 2003, 3001, 3002], TestCatalog.FollowedBy(ra.Test).Select(e => e.Number));
        Assert.Equal([3001, 3002], TestCatalog.FollowedBy(TestCatalog.Entry("Diagnose.{member}")!).Select(e => e.Number));
        Assert.Contains(TestCatalog.FollowedUp(1), e => e.Key == "Tracking.RightAscensionRate.Moves");
        Assert.Empty(TestCatalog.FollowedUp(Tiers.Max));
        Assert.All(chains.Where(c => c.ConformU), c => Assert.All(c.Tier2.Concat(c.Tier3), e => Assert.Equal("ConformU", e.Group)));
        Assert.All(chains.Where(c => !c.ConformU), c => Assert.All(c.Tier2.Concat(c.Tier3), e => Assert.Equal("Diagnose", e.Group)));
    }

    [Fact]
    public void Every_diagnosis_makes_findings_of_its_own_tier_in_the_catalog()
    {
        foreach (var d in Tiers.Diagnoses)
        {
            Assert.InRange(d.Tier, 2, Tiers.Max);
            Assert.False(string.IsNullOrWhiteSpace(d.What), $"tier {d.Tier} {d.Member}: no description");
            Assert.NotEmpty(d.Produces);
            foreach (var key in d.Produces)
            {
                var entry = TestCatalog.Entry(key);
                Assert.True(entry is not null, $"tier {d.Tier} {d.Member}: {key} isn't in the catalog");
                Assert.True(entry!.Tier == d.Tier, $"tier {d.Tier} {d.Member}: {key} is a tier {entry.Tier} check");
            }
            Assert.NotEmpty(d.FollowsUp);
        }
        // One diagnosis per member and tier; a tier 3 one only where the member has a tier 2 one to follow up.
        Assert.DoesNotContain(Tiers.Diagnoses.GroupBy(d => (d.Tier, d.Member)), g => g.Count() > 1);
        Assert.All(Tiers.Diagnoses.Where(d => d.Tier > 2), d => Assert.True(Tiers.Has(d.Member, d.Tier - 1), $"tier {d.Tier} {d.Member} has no tier {d.Tier - 1} diagnosis"));
    }
}
