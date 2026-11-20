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
    /// The options for a run of just the problems in <paramref name="results"/>: the groups they came from (with the
    /// connection and capabilities every group needs) narrowed to their members, as a ConformU log's follow-up is.
    /// Null when there are no problems.
    /// </summary>
    public static RunOptions? For(RunOptions options, IEnumerable<CheckResult> results)
    {
        var problems = results.Where(r => r.IsProblem).ToList();
        if (problems.Count == 0) return null;
        var known = TestRunner.Groups.Select(g => g.Name).ToHashSet();
        var groups = problems.Select(r => r.Group).Where(known.Contains).ToHashSet();
        // Every group needs the device connected and its capabilities read.
        groups.Add("Connection");
        groups.Add("Capabilities");
        var members = problems.Select(r => r.Member).Where(m => m.Length > 0).Distinct().ToList();
        // A tier 1 run of just these: the tier and what it follows up aren't carried over.
        return options with { Groups = [.. groups], Members = members, Tier = 1, FollowsUp = null };
    }
}
