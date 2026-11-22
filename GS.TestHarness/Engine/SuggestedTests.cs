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

using System.Text.RegularExpressions;

namespace GS.TestHarness.Engine;

/// <summary>
/// A draft of the catalog entry for a suggested test, in the shape of <see cref="CatalogEntry"/>, and where to put it. It is a
/// starting point: the method names what to vary and what to review, and whoever writes the test refines it.
/// </summary>
/// <param name="ProposedKey">The new test's key (Group.Id, placeholders as in the catalog).</param>
/// <param name="Number">The next free number in the tier's range (<see cref="TestNumbers"/>).</param>
/// <param name="FollowsUp">The tier below's checks it follows up, by key.</param>
public sealed record TestSpec(int Tier, string ProposedKey, int Number, string Objective, string Method, string Verification,
    IReadOnlyList<string> FollowsUp)
{
    /// <summary>The files to change to add it.</summary>
    public IReadOnlyList<string> Edits =>
    [
        $"Engine/Tiers.cs: add a Diagnosis({Tier}, <member or area>, [\"{ProposedKey}\"], \"<what it does>\") to Tiers.Diagnoses.",
        $"Engine/TestCatalog.cs: add the E(...) entry for {ProposedKey} with Tier = {Tier} and FollowsUp = [{string.Join(", ", FollowsUp.Select(f => $"\"{f}\""))}].",
        $"Engine/TestNumbers.cs: add ({Number}, \"{ProposedKey}\"). A number is never reused.",
        "Engine/Checks/AreaDiagnostics.cs (or Diagnostics.cs): the measurement, reporting findings with that id.",
        "tests/GS.TestHarness.Tests/CatalogTests.cs: runs the checks and fails if the pieces above disagree."
    ];
}
/// <summary>
/// A test the user should add: what in the run called for it (<see cref="Why"/>, <see cref="Related"/>), the members it is about
/// (one suggestion covers every member that had the same kind of problem), how much it matters (<see cref="Weight"/>, the heaviest
/// related test's, 1 to 10) and a draft of the test (<see cref="Spec"/>).
/// </summary>
/// <param name="Related">The earlier tests it should be tied to (each "Test 1159: ParkHome.FindHome").</param>
/// <param name="SeenBefore">How many earlier runs (of the same chain of tiers) suggested it too; 0 the first time.</param>
public sealed record SuggestedTest(int Tier, string Title, string Why, IReadOnlyList<string> Related,
    IReadOnlyList<string> Members, int Weight, TestSpec Spec, int SeenBefore = 0)
{
    /// <summary>What identifies it from one run to the next: its tier and the key of the test it proposes.</summary>
    public string Key => $"{Tier}|{Spec.ProposedKey}";
}

/// <summary>Works out, from a finished tier's report, which tests are missing (<see cref="Evaluate"/>).</summary>
public static class SuggestedTests
{
    /// <summary>A tier 1 Warning only brings a suggested test when its test weighs at least this (<see cref="TestWeights"/>).</summary>
    public const int MinWarningWeight = 5;

    /// <summary>
    /// Tier 1: the problems no tier 2 test follows up (a Fail or Error, or a Warning of a test that matters), one suggestion for
    /// each kind of problem however many members had it. Tier 2 or 3: the diagnoses that couldn't measure what they were after,
    /// likewise one for each diagnosis.
    /// </summary>
    public static List<SuggestedTest> Evaluate(RunReport report)
    {
        var tier = report.Options.Tier;
        var findings = tier == 1
            ? Tiers.OutlookFor(report).Uncovered
                .Where(r => r.Outcome is Outcome.Fail or Outcome.Error || (r.Outcome == Outcome.Warning && TestWeights.Of(r) >= MinWarningWeight))
                .ToList()
            : [.. report.Results.Where(r => Tiers.IsDiagnosis(r) && r.Outcome == Outcome.Error)];

        // One suggestion for each kind of problem: the catalog entry the finding belongs to (so SlewToCoordinates.Deprecated and
        // SlewToTarget.Deprecated are one), with every member that had it. Uncovered only has one finding of each id, so the
        // members come from the whole report.
        var suggestions = new List<SuggestedTest>();
        foreach (var kind in findings.GroupBy(r => TestCatalog.For(r.Id)?.Key ?? r.Id))
        {
            var entry = TestCatalog.Entry(kind.Key);
            var ids = kind.Select(r => r.Id).ToHashSet();
            var all = report.Results.Where(r => KindOf(r) == kind.Key && (tier == 1 ? r.IsProblem : r.Outcome == Outcome.Error)).ToList();
            var members = all.Select(r => r.Member).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().Order().ToList();
            var first = kind.First();

            // The findings that called for it, then (tier 1) the members' other tier 1 problems: the new test should explain them too.
            var pairs = ids.Select(id => (Id: id, Number: TestNumbers.Of(id))).ToList();
            if (tier == 1)
                pairs.AddRange(report.Results.Where(x => x.IsProblem && members.Contains(x.Member) && !Tiers.IsDiagnosis(x) && !ids.Contains(x.Id))
                    .Select(x => x.Id).Distinct().Take(5).Select(id => (Id: id, Number: TestNumbers.Of(id))));
            else if (entry is not null)
                pairs.AddRange(entry.FollowsUp.Select(k => (Id: k, Number: TestNumbers.For(k))));
            var related = Related(pairs);

            var weight = Math.Max(TestWeights.Min, kind.Max(TestWeights.Of));
            var what = Label(entry?.Key, ids);
            var who = members.Count switch { 0 => "", 1 or 2 or 3 => $" for {string.Join(", ", members)}", _ => $" for {members.Count} members" };
            var title = tier == 1
                ? $"Tier 2 diagnosis of {what}{who}"
                : $"A tier {tier} measurement of {what}{who} that works where it couldn't";
            var why = tier == 1
                ? $"Tier 1 found: {first.Message}{(members.Count > 1 ? $" The same problem was found in {members.Count} members." : "")} " +
                  "No tier 2 test follows it up, so there's nothing that narrows it to a cause in the code."
                : $"{first.Id} couldn't measure it: {first.Message}";
            suggestions.Add(new SuggestedTest(tier == 1 ? 2 : tier, title, WithNumbers(why, related), related, members, weight,
                Draft(tier == 1 ? 2 : tier, entry, first, members)));
        }
        return suggestions;
    }

    /// <summary>Adds <paramref name="found"/> to <paramref name="into"/>, skipping what is there already; heaviest first.</summary>
    public static void Merge(List<SuggestedTest> into, IEnumerable<SuggestedTest> found)
    {
        foreach (var s in found)
            if (!into.Any(x => x.Title == s.Title)) into.Add(s);
        into.Sort((a, b) => b.Weight.CompareTo(a.Weight));
        // Each gets its own number: the next free ones in the tier, in the order listed.
        var taken = new Dictionary<int, int>();
        for (var i = 0; i < into.Count; i++)
        {
            var tier = into[i].Tier;
            var n = taken.GetValueOrDefault(tier);
            taken[tier] = n + 1;
            into[i] = into[i] with { Spec = into[i].Spec with { Number = NextNumber(tier) + n } };
        }
    }

    /// <summary>A Markdown file for <paramref name="picked"/>: each suggestion with a draft of its test and where to add it.</summary>
    /// <param name="from">What the suggestions came from (the run's server and reports), or null.</param>
    public static string ToMarkdown(IEnumerable<SuggestedTest> picked, string? from)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Suggested new tests").AppendLine();
        if (from is not null) sb.AppendLine(from).AppendLine();
        foreach (var s in picked)
        {
            sb.AppendLine($"## {s.Title}").AppendLine();
            sb.AppendLine($"- Tier: {s.Tier}");
            sb.AppendLine($"- Priority: {s.Weight} of 10 (the heaviest related test's weight)");
            if (s.SeenBefore > 0) sb.AppendLine($"- Suggested before: in {s.SeenBefore} earlier run{(s.SeenBefore == 1 ? "" : "s")}");
            if (s.Members.Count > 0) sb.AppendLine($"- Members: {string.Join(", ", s.Members)}");
            sb.AppendLine($"- Why: {s.Why}");
            if (s.Related.Count > 0)
            {
                sb.AppendLine("- Related earlier tests:");
                foreach (var r in s.Related) sb.AppendLine($"  - {r}");
            }
            sb.AppendLine().AppendLine("### Draft test (refine before adding)").AppendLine();
            var spec = s.Spec;
            sb.AppendLine($"- Key: `{spec.ProposedKey}`");
            sb.AppendLine($"- Number: {spec.Number}");
            sb.AppendLine($"- Tier: {spec.Tier}");
            sb.AppendLine($"- Follows up: {(spec.FollowsUp.Count == 0 ? "none (no catalog entry for the finding)" : string.Join(", ", spec.FollowsUp.Select(f => $"`{f}`")))}");
            sb.AppendLine($"- Objective: {spec.Objective}");
            sb.AppendLine($"- Method: {spec.Method}");
            sb.AppendLine($"- Verification: {spec.Verification}");
            sb.AppendLine().AppendLine("### Where to add it").AppendLine();
            foreach (var edit in spec.Edits) sb.AppendLine($"- [ ] {edit}");
            sb.AppendLine();
        }
        return sb.ToString();
    }
    /// <summary>
    /// The related tests as lines, one for each test number: two findings of the same catalog test (the two directions of a flip,
    /// say) share a number, so they are listed together as "Test 1104: id, id". A finding with no number is a line of its own.
    /// </summary>
    private static List<string> Related(IEnumerable<(string Id, int Number)> pairs)
    {
        var lines = new List<string>();
        var list = pairs.DistinctBy(p => p.Id).ToList();
        foreach (var number in list.Where(p => p.Number > 0).Select(p => p.Number).Distinct())
            lines.Add($"Test {number}: {string.Join(", ", list.Where(p => p.Number == number).Select(p => p.Id))}");
        lines.AddRange(list.Where(p => p.Number <= 0).Select(p => p.Id));
        return lines;
    }

    /// <summary>
    /// What the suggestion is about, without the catalog's placeholders: the finding's own id for one finding, and for several of
    /// one kind the id with each placeholder filled in by the values found ("Flip.Goto.(EastToWest, WestToEast).Flipped"). The group
    /// (first part of the id) is left off. Falls back to the catalog's id when the findings' ids don't line up with it.
    /// </summary>
    private static string Label(string? entryKey, IReadOnlySet<string> ids)
    {
        static string WithoutGroup(string id) => id[(id.IndexOf('.') + 1)..];
        if (ids.Count == 1) return WithoutGroup(ids.First());
        var key = entryKey?.Split('.');
        var parts = ids.Select(id => id.Split('.')).ToList();
        if (key is null || parts.Any(p => p.Length != key.Length))
            return entryKey is null ? WithoutGroup(ids.First()) : WithoutGroup(entryKey.Replace("*}", "}"));
        var segments = key.Select((k, i) =>
        {
            if (!k.StartsWith('{')) return k;
            var values = parts.Select(p => p[i]).Distinct().Order().ToList();
            return values.Count == 1 ? values[0] : values.Count <= 3 ? $"({string.Join(", ", values)})" : $"({values.Count} values)";
        });
        return WithoutGroup(string.Join('.', segments));
    }
    private static string KindOf(CheckResult r) => TestCatalog.For(r.Id)?.Key ?? r.Id;

    /// <summary>The reason with every test number it refers to (those of <paramref name="related"/>, from "Test 1159: …") listed at the end.</summary>
    private static string WithNumbers(string why, IReadOnlyList<string> related)
    {
        var numbers = related.Select(s => Regex.Match(s, @"^Test (\d+):"))
            .Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct().ToList();
        return numbers.Count == 0 ? why : $"{why} Test numbers referred to: {string.Join(", ", numbers)}.";
    }

    /// <summary>The next free number in <paramref name="tier"/>'s range.</summary>
    public static int NextNumber(int tier)
        => TestNumbers.All.Where(a => TestNumbers.TierOf(a.Number) == tier).Select(a => a.Number).DefaultIfEmpty(tier * 1000).Max() + 1;

    /// <summary>A draft of the test for a kind of problem (<paramref name="entry"/>, or null for a finding with no catalog entry).</summary>
    private static TestSpec Draft(int tier, CatalogEntry? entry, CheckResult first, IReadOnlyList<string> members)
    {
        var target = members.Count > 0 ? string.Join(", ", members.Take(5)) + (members.Count > 5 ? " and more" : "") : "the member";
        string key, objective, method, verification;
        IReadOnlyList<string> follows;
        if (first.Group == "Diagnose" || first.Id.StartsWith("ConformU.Diagnose.", StringComparison.Ordinal))
        {
            // A diagnosis that couldn't measure: the same question another way.
            key = (entry?.Key ?? first.Id) + ".Alternative";
            objective = entry?.Objective ?? first.Message;
            method = $"Measure the same thing by another route, one that doesn't depend on what stopped {first.Id} ({first.Message}), " +
                     $"and review the code for {target}. Original method: {entry?.Method ?? "not in the catalog"}";
            verification = entry?.Verification ?? "Fail or Warning naming the cause and the code to change; Info if within tolerance; Error if it couldn't measure.";
            follows = entry?.FollowsUp ?? [];
        }
        else
        {
            key = "Diagnose." + (entry?.Id.Replace("*}", "}") ?? first.Id);
            objective = $"Find the cause of: {entry?.Objective ?? first.Message}";
            method = $"Repeat what failed ({entry?.Method ?? first.Message}) with the values around it (the neighbouring valid and invalid ones, " +
                     $"tracking on and off, before and after a slew), and review the code in the server source that implements {target}, " +
                     "to tell which of the possible causes it is.";
            verification = "Fail or Warning naming the cause and the code to change; Info if it is within tolerance; Error if it couldn't measure. " +
                           $"The tier 1 test is judged: {entry?.Verification ?? "n/a"}";
            follows = entry is null ? [] : [entry.Key];
        }
        return new TestSpec(tier, key, NextNumber(tier), objective, method, verification, follows);
    }
}
