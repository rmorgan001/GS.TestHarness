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

/// <summary>The order the check groups run in matters: nothing moves the mount until the checks that don't have run.</summary>
public class GroupOrderTests
{
    private static List<string> Names() => [.. TestRunner.Groups.Select(g => g.Name)];

    [Fact]
    public void Connection_runs_first_and_Disconnected_last()
    {
        var names = Names();
        Assert.Equal("Connection", names[0]);
        Assert.Equal("Disconnected", names[^1]);
    }

    [Fact]
    public void The_groups_that_move_the_mount_run_after_all_the_ones_that_dont()
    {
        var names = Names();
        // Motion, park and home and tracking move the mount; then the diagnoses, the ConformU checks and the summaries.
        string[] movers = ["Motion", "ParkHome", "Tracking", "Diagnose", "ConformU", "Performance", "Disconnected"];
        Assert.All(movers, m => Assert.Contains(m, names));
        // They run in this order, one after another...
        Assert.Equal(movers, names.Where(movers.Contains).ToList());
        // ...and every other group runs before the first of them.
        var first = names.IndexOf("Motion");
        Assert.All(names.Where(n => !movers.Contains(n)), n => Assert.True(names.IndexOf(n) < first, $"{n} runs after Motion"));
    }
}
