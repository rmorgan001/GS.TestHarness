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

/// <summary>The checks of SiteLatitude's sign: against the Site hemisphere setting, and against where home is.</summary>
public class HemisphereTests
{
    // The fake mount is at latitude 28.5° (north).
    private static Task<RunReport> RunAsync(Hemisphere hemisphere)
        => EngineTests.RunAsync(new FakeTelescope(), EngineTests.Everything with { SiteHemisphere = hemisphere });

    [Fact]
    public async Task A_matching_hemisphere_passes()
    {
        var report = await RunAsync(Hemisphere.North);
        Assert.Contains(report.Results, r => r.Id == "Properties.SiteHemisphere" && r.Outcome == Outcome.Pass);
    }

    [Fact]
    public async Task A_latitude_with_the_wrong_sign_is_a_warning()
    {
        var report = await RunAsync(Hemisphere.South);
        var result = Assert.Single(report.Results, r => r.Id == "Properties.SiteHemisphere");
        Assert.Equal(Outcome.Warning, result.Outcome);
        Assert.Contains("southern", result.Message);
    }

    [Fact]
    public async Task Nothing_is_checked_when_the_hemisphere_isnt_said()
    {
        var report = await RunAsync(Hemisphere.Unknown);
        Assert.DoesNotContain(report.Results, r => r.Id == "Properties.SiteHemisphere");
    }

    [Fact]
    public async Task Home_at_the_pole_of_the_latitudes_hemisphere_passes()
    {
        var report = await RunAsync(Hemisphere.Unknown);
        Assert.Contains(report.Results, r => r.Id == "ParkHome.HomePole" && r.Outcome == Outcome.Pass);
    }

    [Fact]
    public async Task Home_at_the_other_pole_than_the_hemisphere_you_said_is_a_warning()
    {
        // The fake homes at the north pole; saying it is southern puts home at the wrong pole.
        var report = await RunAsync(Hemisphere.South);
        var result = Assert.Single(report.Results, r => r.Id == "ParkHome.HomePole");
        Assert.Equal(Outcome.Warning, result.Outcome);
        Assert.Contains("southern", result.Message);
    }

    [Fact]
    public async Task Home_at_the_pole_of_the_hemisphere_you_said_passes()
    {
        var report = await RunAsync(Hemisphere.North);
        Assert.Contains(report.Results, r => r.Id == "ParkHome.HomePole" && r.Outcome == Outcome.Pass);
    }
}
