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

/// <summary>
/// The reports that belong together: a tier 1 report with the tier 2 and tier 3 reports that followed it up (directly or through each
/// other), and the re-runs of any of them (the Re-run button). Selecting a run group on the Reports tab makes Reviews and Summary show
/// it. A report whose earlier report has been deleted starts a group of its own.
/// </summary>
public sealed record RunGroup(RunReport Root, IReadOnlyList<RunReport> Runs)
{
    /// <summary>The group's ID: its first report's.</summary>
    public string Key => Root.Id;

    /// <summary>The reports after the first.</summary>
    public IEnumerable<RunReport> Deeper => Runs.Where(r => r.Id != Root.Id);

    /// <summary>
    /// The group's newest tier 1 report: what the group's Reviews and Summary are built on (its tier 2 and 3 reports follow it up). A
    /// group that has none (its tier 1 report was deleted) uses its first report.
    /// </summary>
    public RunReport Latest => Runs.Where(r => r.Options.Tier == 1).OrderByDescending(r => r.Started).FirstOrDefault() ?? Root;
}

public static class RunGroups
{
    /// <summary>The groups of <paramref name="runs"/>, the one with the newest first report first. Within a group by tier, then time.</summary>
    public static List<RunGroup> Build(IEnumerable<RunReport> runs)
    {
        var all = runs.ToList();
        var byId = all.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        return [.. all
            .GroupBy(r => FirstOf(r, byId).Id)
            .Select(g => new RunGroup(byId[g.Key], [.. g.OrderBy(r => r.Options.Tier).ThenBy(r => r.Started)]))
            .OrderByDescending(g => g.Root.Started)];
    }

    /// <summary>
    /// The report a run's group starts from: back through the report it follows up, or the one it re-ran, as far as those reports
    /// still exist.
    /// </summary>
    private static RunReport FirstOf(RunReport run, IReadOnlyDictionary<string, RunReport> byId)
    {
        var seen = new HashSet<string>();
        while ((run.Options.FollowsUp ?? run.Options.RerunOf) is { } before && seen.Add(run.Id) && byId.TryGetValue(before, out var parent))
            run = parent;
        return run;
    }

    /// <summary>The group with this key (its first report's ID), or null.</summary>
    public static RunGroup? Find(IEnumerable<RunGroup> groups, string? key)
        => key is null ? null : groups.FirstOrDefault(g => g.Key == key);
}
