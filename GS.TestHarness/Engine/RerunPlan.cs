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

namespace GS.TestHarness.Engine;

/// <summary>Re-running only the checks a run had problems with.</summary>
public static class RerunPlan
{
    /// <summary>
    /// The options for a run of the groups that had problems in <paramref name="results"/> (with the connection and capabilities
    /// every group needs). The groups run whole: narrowing them to the problems' members would leave out the checks that have no
    /// member (where the mount started, the protocol checks, the pre-motion gate), and a problem whose check never ran again would
    /// look fixed. Null when there are no problems.
    /// </summary>
    /// <summary>
    /// Re-running a tier 2 or 3 run: the same diagnosis again, of what the run below it found (<paramref name="followed"/>).
    /// Null if that run isn't this run's, or has nothing at this tier to diagnose any more. A tier 1 run isn't re-run this way.
    /// </summary>
    public static NextTier? SameTier(RunReport last, RunReport? followed)
        => last.Options.Tier > 1 && followed is not null && followed.Id == last.Options.FollowsUp && Tiers.Next(followed) is { } next && next.Tier == last.Options.Tier
            ? next : null;

    public static RunOptions? For(RunOptions options, IEnumerable<CheckResult> results)
    {
        var problems = results.Where(r => r.IsProblem).ToList();
        if (problems.Count == 0) return null;
        var known = TestRunner.Groups.Select(g => g.Name).ToHashSet();
        // The diagnoses (tiers 2 and 3) and the ConformU checks aren't a tier 1 run's groups: nothing to re-run for them.
        var groups = problems.Select(r => r.Group).Where(g => known.Contains(g) && g is not ("Diagnose" or "ConformU")).ToHashSet();
        if (groups.Count == 0) return null;
        // Every group needs the device connected and its capabilities read.
        groups.Add("Connection");
        groups.Add("Capabilities");
        // A tier 1 run of just these groups: the tier and what it follows up aren't carried over, and no member narrowing.
        return options with { Groups = [.. groups], Members = null, Tier = 1, FollowsUp = null };
    }
}
