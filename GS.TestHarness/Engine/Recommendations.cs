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

using System.Text;

namespace GS.TestHarness.Engine;

/// <summary>One run's result for a check that has a recommendation.</summary>
public sealed record RecommendationHit(RunReport Run, CheckResult Result);

/// <summary>
/// A check, in one server's code, that found a problem in some of the latest runs: what to change (each distinct
/// fix the runs gave), where (each distinct source location) and the runs that found it.
/// </summary>
public sealed record Recommendation(
    Codebase Codebase,
    string Id,
    string Group,
    string Member,
    Outcome Worst,
    IReadOnlyList<string> Fixes,
    IReadOnlyList<string> Sources,
    string? Reference,
    IReadOnlyList<RecommendationHit> Hits);

/// <summary>A check no latest run found a problem with, and at least one passed (<see cref="Runs"/>: how many passed).</summary>
public sealed record CleanCheck(Codebase Codebase, string Id, string Group, string Member, int Runs);

/// <summary>What the Recommendations tab shows: the runs it's built from, the problems to fix and the checks that are fine.</summary>
public sealed record RecommendationSet(
    IReadOnlyList<RunReport> Runs,
    IReadOnlyList<Recommendation> Recommendations,
    IReadOnlyList<CleanCheck> Clean);

/// <summary>
/// Turns the saved runs into a list of recommendations. Only the newest run of each device at each site counts,
/// so a problem fixed since an older run doesn't come back.
/// </summary>
public static class RecommendationBuilder
{
    private static int Severity(Outcome o) => o switch
    {
        Outcome.Fail => 3,
        Outcome.Error => 2,
        Outcome.Warning => 1,
        _ => 0
    };

    /// <summary>
    /// The newest run of each device at each site (server's own, north, south), newest first. A run that was
    /// stopped early only counts when there's no finished one: it didn't make every check, so it would hide
    /// problems an older finished run found.
    /// </summary>
    public static List<RunReport> LatestRuns(IEnumerable<RunReport> runs)
    {
        var latest = new List<RunReport>();
        foreach (var run in runs.OrderBy(r => r.Cancelled).ThenByDescending(r => r.Started))
            if (!latest.Any(l => TestTarget.SameDevice(l.Target, run.Target) && l.Site?.Hemisphere == run.Site?.Hemisphere))
                latest.Add(run);
        return [.. latest.OrderByDescending(r => r.Started)];
    }

    public static RecommendationSet Build(IEnumerable<RunReport> runs)
    {
        var latest = LatestRuns(runs);
        var recommendations = new List<Recommendation>();
        var clean = new List<CleanCheck>();

        var byCheck = latest
            .SelectMany(run => run.Results.Select(result => (Run: run, Result: result)))
            .GroupBy(x => (x.Run.Target.Codebase, x.Result.Id));
        foreach (var check in byCheck)
        {
            var any = check.First().Result;
            var problems = check.Where(x => x.Result.IsProblem).ToList();
            if (problems.Count == 0)
            {
                if (check.Any(x => x.Result.Outcome == Outcome.Pass))
                    clean.Add(new CleanCheck(check.Key.Codebase, check.Key.Id, any.Group, any.Member,
                        check.Where(x => x.Result.Outcome == Outcome.Pass).Select(x => x.Run).Distinct().Count()));
                continue;
            }

            var ordered = problems.OrderByDescending(x => Severity(x.Result.Outcome)).ThenByDescending(x => x.Run.Started).ToList();
            recommendations.Add(new Recommendation(
                check.Key.Codebase,
                check.Key.Id,
                any.Group,
                any.Member,
                ordered[0].Result.Outcome,
                [.. ordered.Select(x => x.Result.Fix).OfType<string>().Distinct()],
                [.. ordered.Select(x => x.Result.Source).OfType<string>().Distinct()],
                ordered.Select(x => x.Result.Reference).FirstOrDefault(r => r is not null),
                [.. ordered.Select(x => new RecommendationHit(x.Run, x.Result))]));
        }

        return new RecommendationSet(
            latest,
            [.. recommendations
                .OrderBy(r => r.Codebase)
                .ThenByDescending(r => Severity(r.Worst))
                .ThenByDescending(r => r.Hits.Count)
                .ThenBy(r => r.Id, StringComparer.Ordinal)],
            [.. clean.OrderBy(c => c.Codebase).ThenBy(c => c.Id, StringComparer.Ordinal)]);
    }

    /// <summary>The recommendations as Markdown, one section per server, ready to paste into an issue.</summary>
    public static string ToMarkdown(RecommendationSet set)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ASCOM compliance: recommendations");
        sb.AppendLine();
        sb.AppendLine($"From the latest run of each device at each site ({set.Runs.Count} run{(set.Runs.Count == 1 ? "" : "s")}):");
        sb.AppendLine();
        foreach (var run in set.Runs)
            sb.AppendLine($"- {run.Label} · {run.SiteLabel} · {run.Started:yyyy-MM-dd HH:mm} · {run.Target.Display}{(run.Cancelled ? " · stopped early" : "")}");

        if (set.Recommendations.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("No problems found.");
        }
        foreach (var server in set.Recommendations.GroupBy(r => r.Codebase))
        {
            sb.AppendLine();
            sb.AppendLine($"## {server.Key} ({server.Count()})");
            foreach (var r in server)
            {
                sb.AppendLine();
                sb.AppendLine($"### {r.Worst}: {r.Member}");
                sb.AppendLine($"`{r.Id}`");
                sb.AppendLine();
                if (r.Fixes.Count == 0) sb.AppendLine("- Recommendation: none recorded; see the expected and actual values below.");
                foreach (var fix in r.Fixes) sb.AppendLine($"- Recommendation: {fix}");
                foreach (var source in r.Sources) sb.AppendLine($"- Where: `{source}`");
                if (r.Reference is not null) sb.AppendLine($"- Reference: {r.Reference}");
                foreach (var hit in r.Hits)
                {
                    sb.AppendLine($"- {hit.Result.Outcome} at {hit.Run.SiteLabel} ({hit.Run.Label}, {hit.Run.Started:yyyy-MM-dd HH:mm}): {hit.Result.Message}");
                    if (hit.Result.Expected is not null) sb.AppendLine($"  - Expected: {hit.Result.Expected}");
                    if (hit.Result.Actual is not null) sb.AppendLine($"  - Actual: {hit.Result.Actual.Split('\n')[0]}");
                }
            }
        }
        return sb.ToString();
    }
}
