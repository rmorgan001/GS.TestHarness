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

/// <summary>What the harness made of one of a ConformU log's problem areas (the ConformU.{member}.* findings).</summary>
public enum ConformUVerdict
{
    /// <summary>The harness found the problem too.</summary>
    Reproduced,

    /// <summary>The harness's deeper tests explained it (as something else, such as readings too coarse for ConformU's method).</summary>
    Explained,

    /// <summary>The harness's checks of the member all pass (or its diagnosis found it within tolerance).</summary>
    NotReproduced,

    /// <summary>The checks that would look at it didn't run.</summary>
    NotChecked,

    /// <summary>No harness check covers it: reproduce it by hand.</summary>
    NoCheck
}

/// <summary>One problem area of the ConformU log, with what became of it in the deepest tier that looked at it.</summary>
/// <param name="Said">What ConformU said (its ISSUE, ERROR… lines for the area, as the verdict finding quotes them).</param>
/// <param name="Problems">How many problem lines ConformU gave for the area.</param>
/// <param name="Tier">The tier whose verdict this is (the deepest that gave one).</param>
/// <param name="Message">The verdict: the harness's own conclusion.</param>
public sealed record ConformUAreaSummary(string Member, ConformUVerdict Verdict, Outcome Outcome, int Tier, string Reference, string ReportId,
    string Message, string Said, int Problems, string? Fix)
{
    /// <summary>Whether a problem remains: reproduced, or explained but still a problem (a Warning).</summary>
    public bool Remains => Verdict == ConformUVerdict.Reproduced || (Verdict == ConformUVerdict.Explained && Outcome is Outcome.Warning or Outcome.Fail);

    public string Label => Verdict switch
    {
        ConformUVerdict.Reproduced => "Reproduced",
        ConformUVerdict.Explained => Remains ? "Explained" : "Explained: not a fault",
        ConformUVerdict.NotReproduced => "Not reproduced",
        ConformUVerdict.NotChecked => "Not checked",
        _ => "No check: reproduce by hand"
    };

    /// <summary>The first line of what ConformU said, with how many more there were.</summary>
    public string ShortSaid
    {
        get
        {
            var first = Said.Split('\n', 2)[0].Trim();
            if (first.Length > 220) first = first[..220].TrimEnd() + "…";
            return Problems > 1 ? $"{first} (and {Problems - 1} more)" : first;
        }
    }
}

/// <summary>
/// What became of every problem the ConformU log reported, across the tiers of one chain: for each area, the harness's last word
/// (the deepest tier's verdict). Built from the verdict findings, so it needs nothing but the saved reports.
/// </summary>
/// <param name="LogLine">The log's own totals ("0 errors, 17 issues, 0 configuration alerts and 0 warnings, in 3 areas").</param>
public sealed record ConformUSummary(string FileName, string? LogLine, IReadOnlyList<ConformUAreaSummary> Areas)
{
    private static readonly Regex VerdictId = new(@"^ConformU\.(?<m>[^.]+)\.(?<v>Reproduced|Explained|NotReproduced|NotChecked|NoCheck)$", RegexOptions.Compiled);

    private static readonly Regex More = new(@"… and (?<n>\d+) more\s*$", RegexOptions.Compiled);

    /// <summary>"3 areas: 2 explained (not a fault), 1 not reproduced".</summary>
    public string Headline
    {
        get
        {
            var counts = Areas.GroupBy(a => a.Label).OrderBy(g => Areas.ToList().FindIndex(a => a.Label == g.Key))
                .Select(g => $"{g.Count()} {g.Key.ToLowerInvariant().Replace(": not a fault", " (not a fault)").Replace(": reproduce by hand", " (reproduce by hand)")}");
            return $"{Areas.Count} area{(Areas.Count == 1 ? "" : "s")}: {string.Join(", ", counts)}";
        }
    }

    /// <summary>
    /// The summary as Markdown (a heading, the log's totals, and a table with one row for each area): for the report's and the fix
    /// list's downloads. <paramref name="heading"/> is the Markdown heading marker ("##" by default).
    /// </summary>
    public string ToMarkdown(string heading = "##")
    {
        static string Cell(string s) => s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{heading} What became of the problems in the ConformU log").AppendLine();
        var totals = LogLine is { } line ? $" {line[(line.IndexOf(':') + 1)..].Trim()}" : "";
        sb.AppendLine($"Log `{FileName}`:{totals} The harness's verdict: {Headline}.").AppendLine();
        sb.AppendLine("| Area | What ConformU said | The harness's last word |");
        sb.AppendLine("|---|---|---|");
        foreach (var a in Areas)
            sb.AppendLine($"| {a.Member} | {Cell(a.ShortSaid)} | **{a.Label}** (tier {a.Tier}, {a.Reference}): {Cell(a.Message)} |");
        return sb.ToString();
    }

    /// <summary>Whether any area still has a problem.</summary>
    public bool AnyRemain => Areas.Any(a => a.Remains);

    /// <summary>The summary of the ConformU log the chain <paramref name="chain"/> followed up, or null when it didn't follow up one.</summary>
    public static ConformUSummary? Build(IEnumerable<RunReport> chain)
    {
        var runs = chain.Where(r => r.ConformULog is not null).OrderBy(r => r.Options.Tier).ThenBy(r => r.Started).ToList();
        if (runs.Count == 0) return null;

        var order = new List<string>();
        var last = new Dictionary<string, (RunReport Run, CheckResult Result, ConformUVerdict Verdict)>();
        var said = new Dictionary<string, string>();
        string? logLine = null;
        foreach (var run in runs)
        {
            foreach (var r in run.Results.Where(r => r.Group == "ConformU"))
            {
                if (r.Id == "ConformU.Log") { logLine ??= r.Message; continue; }
                if (VerdictId.Match(r.Id) is not { Success: true } m || !Enum.TryParse<ConformUVerdict>(m.Groups["v"].Value, out var verdict)) continue;
                var member = m.Groups["m"].Value;
                if (!order.Contains(member)) order.Add(member);
                last[member] = (run, r, verdict); // a deeper tier's verdict replaces the one below it
                if (!said.ContainsKey(member) && !string.IsNullOrWhiteSpace(r.Expected)) said[member] = r.Expected;
            }
        }

        var areas = order.Select(member =>
        {
            var (run, r, verdict) = last[member];
            var text = said.GetValueOrDefault(member, "");
            return new ConformUAreaSummary(member, verdict, r.Outcome, run.Options.Tier, run.Reference, run.Id, r.Message, text, ProblemCount(text), r.Fix);
        }).ToList();
        return areas.Count == 0 ? null : new ConformUSummary(runs[0].ConformULog!, logLine, areas);
    }

    /// <summary>How many ISSUE, ERROR, WARNING or ALERT lines a verdict quotes: its lines, and the "… and n more" it ends with.</summary>
    private static int ProblemCount(string said)
    {
        if (said.Length == 0) return 0;
        var lines = said.Split('\n').Count(l => l.StartsWith("ConformU ", StringComparison.Ordinal));
        var more = More.Match(said.TrimEnd());
        return Math.Max(1, lines + (more.Success ? int.Parse(more.Groups["n"].Value) : 0));
    }
}
