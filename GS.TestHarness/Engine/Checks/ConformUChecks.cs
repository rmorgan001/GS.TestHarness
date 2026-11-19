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

namespace GS.TestHarness.Engine.Checks;

/// <summary>
/// Follows up a ConformU log (only in a run started from one). While the device is connected, it probes the problem
/// properties no other check looked at in this run (reading each a few times). At the end of the run,
/// <see cref="Summarise"/> says for each of ConformU's problem areas whether the harness reproduced it.
/// </summary>
public sealed partial class ConformUChecks : ICheckGroup
{
    public string Name => "ConformU";
    public string Description => "Only for a ConformU log: at tier 1 reads the flagged properties no other check reached; at tiers 2 and 3 diagnoses the problems it knows how to take apart (rate offsets, pulse guiding); then says for each problem whether the harness reproduced it.";

    public string? SkipReason(CheckContext ctx) => ctx.ConformU is null ? "no ConformU log for this run" : null;

    private const int ProbeReads = 3;
    private const double SlowMs = 1000;

    public async Task RunAsync(CheckContext ctx)
    {
        var log = ctx.ConformU!;
        ctx.Report("Log", "ConformU", Outcome.Info,
            $"ConformU log {log.FileName}: {log.Count(ConformUSeverity.Error)} errors, {log.Count(ConformUSeverity.Issue)} issues, " +
            $"{log.Count(ConformUSeverity.Alert)} configuration alerts and {log.Count(ConformUSeverity.Warning)} warnings, in {log.Areas.Count} areas.",
            actual: string.Join(", ", log.Areas.Select(a => a.Member)) is { Length: > 0 } members ? $"Areas: {members}" : null,
            reference: TestCatalog.Reference("ConformU").Url);

        // Tiers 2 and 3: the problems the harness knows how to take apart, measured to tell their causes apart, with
        // recommendations. Only the members the run is narrowed to (the ones the tier below found).
        if (ctx.Options.Tier >= 2)
        {
            await Diagnostics.RunAsync(ctx, [.. log.Areas.Where(a => ctx.Wants(a.Member)).Select(a => new DiagnosisProblem(a.Member, Said(a)))]);
            return;
        }

        // Tier 1: a property no check has looked at in this run (its groups were skipped) still gets read.
        var results = ctx.Results;
        foreach (var area in log.Areas.Where(a => a.CanProbe && !results.Any(r => r.Group != "ConformU" && r.Outcome != Outcome.Skipped && About(r, a))))
            await ProbeAsync(ctx, area);
    }

    /// <summary>The check made for a member no group covers: read it a few times and report what it does.</summary>
    private static async Task ProbeAsync(CheckContext ctx, ConformUArea area)
    {
        var property = ConformUMap.Property(area.Member)!;
        var id = $"{IdOf(area.Member)}.Probe";
        var reads = new List<Call<object?>>();
        for (var i = 0; i < ProbeReads; i++)
        {
            var call = await ctx.TryAsync(t => property.GetValue(t));
            reads.Add(call);
            if (!call.Ok) break;
        }
        var conformU = Said(area);
        var last = reads[^1];
        var slowest = reads.Max(r => r.Ms);
        if (last.Kind == AscomErrorKind.NotImplemented)
        {
            ctx.Report(id, area.Member, Outcome.Info, $"Generated check: {area.Member} is not implemented (PropertyNotImplementedException).",
                expected: conformU, actual: last.ErrorText, ms: slowest);
        }
        else if (!last.Ok)
        {
            ctx.Report(id, area.Member, Outcome.Fail, $"Generated check: reading {area.Member} raised an error, as ConformU found.",
                expected: conformU, actual: last.ErrorText, ms: slowest,
                fix: $"{area.Member} should return a value, or throw PropertyNotImplementedException if the driver doesn't support it. " +
                     $"Start with the error above and ConformU's message.");
        }
        else
        {
            var values = string.Join(", ", reads.Select(r => CheckContext.Show(r.Value)).Distinct());
            var slow = slowest > SlowMs;
            ctx.Report(id, area.Member, slow ? Outcome.Warning : Outcome.Info,
                $"Generated check: {area.Member} read {reads.Count} times = {values}" + (slow ? $", slowest {slowest:0} ms." : "."),
                expected: conformU, actual: $"{values} (slowest read {slowest:0} ms)", ms: slowest,
                fix: slow ? $"{area.Member} took over {SlowMs:0} ms to read; look for a blocking call in its getter." : null);
        }
        ctx.Facts[$"ConformU.Probed.{area.Member}"] = true;
    }

    /// <summary>
    /// For each of the log's problem areas, compares ConformU's problems with the harness's findings for the same
    /// member: reproduced, not reproduced, not checked in this run (a group was skipped), or no check at all.
    /// Needs no device, so it runs after every other group (including the disconnected checks).
    /// </summary>
    public static void Summarise(CheckContext ctx)
    {
        if (ctx.ConformU is not { } log) return;
        var group = ctx.Group;
        ctx.Group = "ConformU";
        var results = ctx.Results.Where(r => r.Group != "ConformU").ToList();
        // A tier 2 or 3 run only says what became of the problems it diagnosed.
        foreach (var area in log.Areas.Where(a => ctx.Options.Tier < 2 || ctx.Wants(a.Member)))
        {
            var id = IdOf(area.Member);
            var conformU = Said(area);
            var related = results.Where(r => r.Outcome != Outcome.Skipped && About(r, area)).ToList();
            var problems = related.Where(r => r.IsProblem).ToList();
            // A diagnosis made for this very problem decides the verdict (even for a probed property); other problems
            // found with the member are listed apart.
            var diagnosis = $"ConformU.Diagnose.{area.Member}";
            var diagnoses = ctx.Results.Where(r => (r.Id == diagnosis || r.Id.StartsWith(diagnosis + ".", StringComparison.Ordinal))
                                                   && r.Outcome is Outcome.Pass or Outcome.Info or Outcome.Warning or Outcome.Fail).ToList();
            // Tier 3's final analysis, where there is one, is the diagnosis's last word.
            if (diagnoses.Where(d => d.Id.EndsWith(".Summary", StringComparison.Ordinal)).ToList() is { Count: > 0 } summaries) diagnoses = summaries;
            if (diagnoses.Count == 0 && ctx.Fact($"ConformU.Probed.{area.Member}")) continue;
            if (diagnoses.Count > 0)
            {
                Diagnosed(ctx, area, id, conformU, diagnoses, problems);
            }
            else if (problems.Count > 0)
            {
                var worst = problems.Any(r => r.Outcome is Outcome.Fail or Outcome.Error) ? Outcome.Fail : Outcome.Warning;
                var first = problems.OrderByDescending(r => r.Outcome.Severity()).First();
                ctx.Report($"{id}.Reproduced", area.Member, worst,
                    $"Reproduced: {problems.Count} of the harness's {related.Count} checks of {area.Member} found a problem, as ConformU did.",
                    expected: conformU,
                    actual: string.Join('\n', problems.Take(6).Select(r => $"{r.Id}: {r.Message}")) + (problems.Count > 6 ? $"\n… and {problems.Count - 6} more" : ""),
                    fix: first.Fix ?? $"See the harness findings for {area.Member} above.",
                    sourceMember: area.Member);
            }
            else if (related.Count > 0)
            {
                ctx.Report($"{id}.NotReproduced", area.Member, Outcome.Info,
                    $"Not reproduced: the harness's {related.Count} checks of {area.Member} all pass. Either it has been fixed since the " +
                    "ConformU run, or ConformU tested something the harness doesn't: compare ConformU's message with the checks listed.",
                    expected: conformU,
                    actual: string.Join('\n', related.Take(6).Select(r => $"{r.Id}: {r.Message}")) + (related.Count > 6 ? $"\n… and {related.Count - 6} more" : ""));
            }
            else if (area.Covered)
            {
                var skipped = results.Where(r => r.Outcome == Outcome.Skipped && r.Id.EndsWith(".Group") && area.Groups.Contains(r.Member)).ToList();
                var why = skipped.Count > 0 ? string.Join(" ", skipped.Select(r => r.Message))
                    : ctx.Lost is not null ? "Communication with the device was lost." : $"The {string.Join(", ", area.Groups)} checks made no finding about it.";
                ctx.Report($"{id}.NotChecked", area.Member, Outcome.Skipped,
                    $"Not checked in this run: {why}", expected: conformU,
                    actual: $"Checked by: {string.Join(", ", area.Groups)}");
            }
            else
            {
                ctx.Report($"{id}.NoCheck", area.Member, Outcome.Warning,
                    $"No harness check covers {area.Member}, and it can't be called safely without knowing what ConformU passed it: reproduce it by hand.",
                    expected: conformU,
                    fix: $"Repeat ConformU's step for {area.Member} by hand (or in ConformU with only this test ticked), and add a check " +
                         "to the harness if it's a real problem.");
            }
        }
        ctx.Group = group;
    }

    /// <summary>
    /// The verdict for a problem the harness diagnosed (<see cref="Diagnostics"/>, at tier 2 or 3): the diagnosis found the cause
    /// in the driver (Fail: Reproduced), explained it as something else, such as a stale or coarse position reading
    /// (Warning: Explained), or found the member within tolerance (Info: NotReproduced). Problems the harness's own
    /// checks found with the same member are other problems, listed as "also found", not ConformU's.
    /// </summary>
    private static void Diagnosed(CheckContext ctx, ConformUArea area, string id, string conformU, List<CheckResult> diagnoses,
        List<CheckResult> others)
    {
        var worst = diagnoses.MaxBy(d => d.Outcome.Severity())!;
        var causes = string.Join(" ", diagnoses.Where(d => d.Outcome == worst.Outcome).Select(d => d.Message));
        var actual = string.Join('\n', diagnoses.Select(d => $"{d.Id}: {d.Message}"));
        if (others.Count > 0)
            actual += $"\nAlso found, not ConformU's problem: {others.Count} other problem{(others.Count == 1 ? "" : "s")} with {area.Member}:\n" +
                      string.Join('\n', others.Take(6).Select(r => $"{r.Id}: {r.Message}")) + (others.Count > 6 ? $"\n… and {others.Count - 6} more" : "");
        var also = others.Count > 0 ? $" The harness also found {others.Count} other problem{(others.Count == 1 ? "" : "s")} with {area.Member} (listed below)." : "";
        var fix = worst.Fix ?? diagnoses.Select(d => d.Fix).FirstOrDefault(f => f is not null);

        switch (worst.Outcome)
        {
            case Outcome.Fail:
                ctx.Report($"{id}.Reproduced", area.Member, Outcome.Fail,
                    $"Reproduced, and diagnosed: {causes}{also}", expected: conformU, actual: actual, fix: fix, sourceMember: area.Member);
                break;
            case Outcome.Warning:
                ctx.Report($"{id}.Explained", area.Member, Outcome.Warning,
                    $"Explained: {causes}{also}", expected: conformU, actual: actual, fix: fix, sourceMember: area.Member);
                break;
            default:
                ctx.Report($"{id}.NotReproduced", area.Member, Outcome.Info,
                    $"Not reproduced: the {area.Member} diagnosis found it within tolerance. Either it has been fixed since the ConformU run, " +
                    $"or it only happens in conditions this run didn't meet.{also}", expected: conformU, actual: actual);
                break;
        }
    }

    /// <summary>Whether a harness finding is about the area's member (its Member, or a part of its ID).</summary>
    private static bool About(CheckResult r, ConformUArea area)
    {
        if (area.Member == ConformUMap.Protocol) return r.Group == "Protocol";
        // The member, or one that goes with it (Connected with Connect, Disconnect and Connecting).
        var members = ConformUMap.Related(area.Member);
        if (members.Contains(r.Member, StringComparer.OrdinalIgnoreCase)) return true;
        var parts = r.Id.Split('.');
        return parts.Skip(1).Any(p => members.Contains(p, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>What ConformU reported for the area, for a finding's Expected.</summary>
    internal static string Said(ConformUArea area)
        => string.Join('\n', area.Problems.Take(5).Select(p => $"ConformU {p.Severity.ToString().ToUpperInvariant()} (line {p.Line}, {p.Test}): {p.Message}"))
           + (area.Problems.Count > 5 ? $"\n… and {area.Problems.Count - 5} more" : "");

    /// <summary>A member as one word, for a finding ID.</summary>
    internal static string IdOf(string member) => NotWord().Replace(member, "") is { Length: > 0 } id ? id : "Unknown";

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NotWord();
}
