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
    /// <summary>
    /// The order to list problems in: <see cref="OutcomeExtensions.Severity"/> (shared with Compare), then a failure
    /// before an error, since a failure is a breach of the standard and an error is a check that couldn't finish.
    /// </summary>
    private static (int, bool) Rank(Outcome o) => (o.Severity(), o == Outcome.Fail);

    /// <summary>
    /// The newest run of each device at each site (server's own, north, south), newest first; see
    /// <see cref="ReportStore.Representative"/> for how a run stopped early counts. A device's run of an older
    /// build of the server is left out too: when the newest run of that server at that site reports a different
    /// version, the older one tested code that has since changed (for example a port tested once and not since).
    /// </summary>
    public static List<RunReport> LatestRuns(IEnumerable<RunReport> runs)
    {
        var all = runs.ToList();
        var latest = all
            .GroupBy(r => (r.Target.DeviceKey, r.Site?.Hemisphere))
            .Select(g => ReportStore.Representative(g)!)
            .ToList();

        // The version of each server at each site now: the one its newest run reported.
        var current = all
            .Where(r => !string.IsNullOrWhiteSpace(r.Identity.DriverVersion))
            .GroupBy(r => (r.Target.Codebase, r.Site?.Hemisphere))
            .ToDictionary(g => g.Key, g => g.MaxBy(r => r.Started)!.Identity.DriverVersion!.Trim());
        latest.RemoveAll(run => !string.IsNullOrWhiteSpace(run.Identity.DriverVersion)
                                && current.TryGetValue((run.Target.Codebase, run.Site?.Hemisphere), out var now)
                                && !string.Equals(run.Identity.DriverVersion.Trim(), now, StringComparison.OrdinalIgnoreCase));
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

            // One hit per run: a run can record the same check more than once, and only its worst result counts.
            var ordered = problems
                .GroupBy(x => x.Run)
                .Select(run => run.OrderByDescending(x => Rank(x.Result.Outcome)).First())
                .OrderByDescending(x => Rank(x.Result.Outcome)).ThenByDescending(x => x.Run.Started).ToList();
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
                .ThenByDescending(r => Rank(r.Worst))
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
                sb.AppendLine($"### {r.Worst}: {Inline(r.Member)}");
                sb.AppendLine(Code(r.Id));
                sb.AppendLine();
                if (r.Fixes.Count == 0) sb.AppendLine("- Recommendation: none recorded; see the expected and actual values below.");
                foreach (var fix in r.Fixes) sb.AppendLine($"- Recommendation: {Inline(fix)}");
                foreach (var source in r.Sources) sb.AppendLine($"- Code to check: {Code(source)}");
                if (r.Reference is not null) sb.AppendLine($"- Reference: {Inline(r.Reference)}");
                foreach (var hit in r.Hits)
                {
                    sb.AppendLine($"- {hit.Result.Outcome} at {hit.Run.SiteLabel} ({hit.Run.Label}, {hit.Run.Started:yyyy-MM-dd HH:mm}): {Inline(hit.Result.Message)}");
                    if (hit.Result.Expected is not null) sb.AppendLine($"  - Expected: {Inline(hit.Result.Expected)}");
                    if (hit.Result.Actual is not null) AppendActual(sb, hit.Result.Actual);
                }
            }
        }
        return sb.ToString();
    }

    private static string[] Lines(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    /// <summary>Text on one line, so it stays inside its list item.</summary>
    private static string Inline(string text)
        => string.Join(" ", Lines(text).Select(l => l.Trim()).Where(l => l.Length > 0));

    /// <summary>A code span that holds any text: the fence is one backtick longer than the longest run inside.</summary>
    private static string Code(string text)
    {
        text = Inline(text);
        var longest = 0;
        for (int i = 0, run = 0; i < text.Length; i++)
            longest = Math.Max(longest, run = text[i] == '`' ? run + 1 : 0);
        var fence = new string('`', longest + 1);
        var pad = text.StartsWith('`') || text.EndsWith('`') ? " " : "";
        return $"{fence}{pad}{text}{pad}{fence}";
    }

    /// <summary>The actual value: on the line itself when it's one line, otherwise all of it in a code block under it.</summary>
    private static void AppendActual(StringBuilder sb, string actual)
    {
        var lines = Lines(actual.TrimEnd());
        if (lines.Length == 1)
        {
            sb.AppendLine($"  - Actual: {lines[0]}");
            return;
        }
        var longest = 0;
        foreach (var line in lines)
            if (line.TrimStart().StartsWith("```")) longest = Math.Max(longest, line.TrimStart().TakeWhile(c => c == '`').Count());
        var fence = new string('`', Math.Max(3, longest + 1));
        sb.AppendLine("  - Actual:");
        sb.AppendLine();
        sb.AppendLine($"    {fence}");
        foreach (var line in lines) sb.AppendLine($"    {line}");
        sb.AppendLine($"    {fence}");
    }
}
