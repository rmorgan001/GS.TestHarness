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

/// <summary>
/// One problem area of one server: the tier 1 checks that found it, and the tier 2 and tier 3 diagnoses of the same member that
/// took it apart. The Recommendations tab lists these, so a problem and what is known about its cause are chosen together.
/// </summary>
public sealed record RecommendationSection(
    Codebase Codebase,
    string Member,
    IReadOnlyList<Recommendation> Tier1,
    IReadOnlyList<Recommendation> Tier2,
    IReadOnlyList<Recommendation> Tier3)
{
    /// <summary>Stable across rebuilds, so a selection survives the list being built again.</summary>
    public string Key => $"{Codebase}|{Member}";

    /// <summary>Every recommendation of the section, tier 1 first.</summary>
    public IEnumerable<Recommendation> All => Tier1.Concat(Tier2).Concat(Tier3);

    /// <summary>The worst outcome of any of them, ranked as the Recommendations tab ranks problems (a failure before an error).</summary>
    public Outcome Worst => All.Select(r => r.Worst).OrderByDescending(Rank).First();

    private static (int, bool) Rank(Outcome o) => (o.Severity(), o == Outcome.Fail);

    /// <summary>The tier of the test that made a recommendation: the first digit of its test number (1 when the test has none).</summary>
    public static int TierOf(Recommendation r) => TestNumbers.TierOf(TestNumbers.Of(r.Id)) is > 0 and var tier ? tier : 1;

    /// <summary>
    /// Groups recommendations by server and member: a tier 1 problem with the tier 2 and 3 diagnoses of the same member. The
    /// worst first, then the most often found.
    /// </summary>
    public static List<RecommendationSection> Group(IEnumerable<Recommendation> recommendations)
        => [.. recommendations
            .GroupBy(r => (r.Codebase, r.Member))
            .Select(g => new RecommendationSection(g.Key.Codebase, g.Key.Member,
                [.. g.Where(r => TierOf(r) == 1)], [.. g.Where(r => TierOf(r) == 2)], [.. g.Where(r => TierOf(r) >= 3)]))
            .OrderBy(s => s.Codebase)
            .ThenByDescending(s => Rank(s.Worst))
            .ThenByDescending(s => s.All.Sum(r => r.Hits.Count))
            .ThenBy(s => s.Member, StringComparer.Ordinal)];
}

/// <summary>
/// The chosen problem areas as Markdown for an AI coding agent to fix: what the agent is looking at, then for each area what the
/// test saw (tier 1), how the cause was narrowed down (tier 2), what was confirmed and the fix (tier 3), where in the code, and how
/// to tell it is done. Written so the file can be given to the agent as it is.
/// </summary>
public static class AgentBrief
{
    public static string Build(IReadOnlyList<RecommendationSection> sections, RecommendationSet set)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Fix list: ASCOM ITelescopeV4 conformance problems");
        sb.AppendLine();
        AppendBrief(sb, sections);
        AppendServers(sb, sections, set);
        var number = 0;
        foreach (var section in sections)
            AppendSection(sb, section, ++number);
        return sb.ToString();
    }

    private static void AppendBrief(StringBuilder sb, IReadOnlyList<RecommendationSection> sections)
    {
        sb.AppendLine("## What this is");
        sb.AppendLine();
        sb.AppendLine("GS Test Harness checked a telescope server against the ASCOM `ITelescopeV4` interface (an Alpaca server or a COM driver) and " +
                      $"found the {sections.Count} problem area{(sections.Count == 1 ? "" : "s")} below. Each area is one interface member that behaves wrongly. " +
                      "Your job is to fix the server's code so the member behaves as the ASCOM standard says, and to leave unrelated behaviour alone.");
        sb.AppendLine();
        sb.AppendLine("Each area has up to four parts, from what was seen to how to fix it:");
        sb.AppendLine();
        sb.AppendLine("1. **Problem (tier 1)**: the test that failed, what it expected and what it got. This is the symptom.");
        sb.AppendLine("2. **Cause analysis (tier 2)**: measurements and checks that narrow the cause down. Treat them as evidence.");
        sb.AppendLine("3. **Confirmed cause and fix (tier 3)**: the deepest analysis, when there is one. Prefer its fix over the tier 1 suggestion.");
        sb.AppendLine("4. **Where to change the code** and **Done when**: files to open (`file:line`) and the checks that must pass afterwards.");
        sb.AppendLine();
        sb.AppendLine("Notes for the fix:");
        sb.AppendLine();
        sb.AppendLine("- A `file:line` location comes from searching the server's source for code that matches the problem. It is a pointer, not proof: " +
                      "open the file, read around the line, and judge whether it is the real cause.");
        sb.AppendLine("- *Expected* and *Actual* are what the test compared. *Fail* breaks the ASCOM standard, *Warning* is allowed but likely to trouble clients, " +
                      "and *Error* means a check could not be completed.");
        sb.AppendLine("- Fix the root cause, not the symptom. Do not change what the test measures to make it pass.");
        sb.AppendLine("- If two areas share a cause, fix it once and say so.");
        sb.AppendLine();
    }

    private static void AppendServers(StringBuilder sb, IReadOnlyList<RecommendationSection> sections, RecommendationSet set)
    {
        var codebases = sections.Select(s => s.Codebase).Distinct().ToList();
        sb.AppendLine("## Servers tested");
        sb.AppendLine();
        foreach (var codebase in codebases)
        {
            foreach (var run in set.Runs.Where(r => r.Target.Codebase == codebase))
            {
                sb.AppendLine($"- **{codebase}**: {RecommendationBuilder.Inline(run.Label)} at {RecommendationBuilder.Inline(run.Target.Display)}, report {run.Reference} ({run.Started:yyyy-MM-dd HH:mm})");
                sb.AppendLine($"  - Check again with: {RecommendationBuilder.Code(CommandFor(run))}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("The fix goes in the code of the server named in each area's heading. Problems in different servers are separate.");
        sb.AppendLine();
    }

    /// <summary>The command line that repeats a run: the same device and the options that matter, for the agent to re-check with.</summary>
    internal static string CommandFor(RunReport run)
    {
        var t = run.Target;
        var sb = new StringBuilder("GS.TestHarness --run ");
        sb.Append(t.Kind == TargetKind.Com ? $"COM --progid {t.ProgId}" : $"Alpaca --host {t.Host} --port {t.Port} --device {t.DeviceNumber}");
        if (run.Options.AllowMotion) sb.Append(" --motion");
        if (run.Options.Extended) sb.Append(" --extended");
        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, RecommendationSection s, int number)
    {
        sb.AppendLine($"## {number}. `{s.Member}` in {s.Codebase} ({s.Worst})");
        sb.AppendLine();
        if (s.Tier1.Count > 0)
        {
            sb.AppendLine("### Problem (tier 1)");
            sb.AppendLine();
            foreach (var r in s.Tier1) AppendCheck(sb, r);
        }
        if (s.Tier2.Count > 0)
        {
            sb.AppendLine("### Cause analysis (tier 2)");
            sb.AppendLine();
            foreach (var r in s.Tier2) AppendCheck(sb, r);
        }
        if (s.Tier3.Count > 0)
        {
            sb.AppendLine("### Confirmed cause and fix (tier 3)");
            sb.AppendLine();
            foreach (var r in s.Tier3) AppendCheck(sb, r);
        }

        var sources = s.All.SelectMany(r => r.Sources).Distinct().ToList();
        sb.AppendLine("### Where to change the code");
        sb.AppendLine();
        if (sources.Count == 0) sb.AppendLine("- No location was found. Search the server's code for how it implements this member.");
        foreach (var source in sources) sb.AppendLine($"- {RecommendationBuilder.Code(source)}");
        sb.AppendLine();

        sb.AppendLine("### Done when");
        sb.AppendLine();
        if (s.Tier1.Count > 0)
        {
            sb.AppendLine("These checks pass on a re-run:");
            sb.AppendLine();
            foreach (var r in s.Tier1) sb.AppendLine($"- {RecommendationBuilder.Code(r.Id)}{TestLabel(r)}");
            sb.AppendLine();
        }
        if (s.Tier2.Count + s.Tier3.Count > 0)
        {
            sb.AppendLine("These diagnoses are evidence, not pass/fail tests: after the fix they should no longer report a fault:");
            sb.AppendLine();
            foreach (var r in s.Tier2.Concat(s.Tier3)) sb.AppendLine($"- {RecommendationBuilder.Code(r.Id)}{TestLabel(r)}");
            sb.AppendLine();
        }
    }

    /// <summary>" (test 1100)", or nothing for a check that has no test number.</summary>
    private static string TestLabel(Recommendation r) => TestNumbers.Of(r.Id) is > 0 and var n ? $" (test {n})" : "";

    /// <summary>One check: what it found, what it expected and got, and what to change; from its worst result.</summary>
    private static void AppendCheck(StringBuilder sb, Recommendation r)
    {
        var worst = r.Hits[0];
        sb.AppendLine($"- {RecommendationBuilder.Code(r.Id)}{TestLabel(r)}, **{worst.Result.Outcome}**: {RecommendationBuilder.Inline(worst.Result.Message)}");
        if (worst.Result.Expected is not null) sb.AppendLine($"  - Expected: {RecommendationBuilder.Inline(worst.Result.Expected)}");
        if (worst.Result.Actual is not null) RecommendationBuilder.AppendActual(sb, worst.Result.Actual);
        if (r.Fixes.Count == 0) sb.AppendLine("  - How to fix: not recorded for this check; work it out from the expected and actual values.");
        foreach (var fix in r.Fixes) sb.AppendLine($"  - How to fix: {RecommendationBuilder.Inline(fix)}");
        if (r.Reference is not null) sb.AppendLine($"  - ASCOM reference: {RecommendationBuilder.Inline(r.Reference)}");
        if (r.Hits.Count > 1) sb.AppendLine($"  - Found in {r.Hits.Count} runs.");
        sb.AppendLine();
    }
}
