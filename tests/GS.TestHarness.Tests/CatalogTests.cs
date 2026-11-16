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
}
