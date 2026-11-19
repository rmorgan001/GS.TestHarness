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

/// <summary>A deeper run a finished one can be followed up with: its tier, the members it diagnoses, and the run it follows up.</summary>
public sealed record NextTier(int Tier, IReadOnlyList<string> Members, RunReport From);

/// <summary>
/// One diagnosis that can be run: its tier, the member it takes apart, and the catalog entries of the findings it
/// makes (<see cref="Produces"/>, keys as Group.Id). The tier and what it follows up are the catalog's
/// (<see cref="CatalogEntry.Tier"/>, <see cref="CatalogEntry.FollowsUp"/>): the catalog is the one list of every check.
/// </summary>
public sealed record Diagnosis(int Tier, string Member, IReadOnlyList<string> Produces, string What)
{
    /// <summary>The checks of the tier below it follows up: those of the catalog entries it produces.</summary>
    public IReadOnlyList<string> FollowsUp => [.. Produces.SelectMany(k => TestCatalog.Entry(k)?.FollowsUp ?? []).Distinct()];
}

/// <summary>
/// The diagnostic tiers, for both the ASCOM Conformance Check and the ConformU Debug Log Test (<see cref="Purpose"/>):
/// <list type="number">
/// <item>Initial testing: problems with the known ASCOM standards, or the problems a ConformU log already found.</item>
/// <item>Further tests within the problem area, with the source code reviewed, to identify the core issue and possible fixes.</item>
/// <item>Deep dive tests to verify tier 2's recommendations, ending in a final analysis, summary and recommended fixes.</item>
/// </list>
/// Tier 1 always runs first. Tiers 2 and 3 are only offered for what the tier below found, and only where a test of
/// the deeper tier follows it up: every check in <see cref="TestCatalog"/> says its tier and, for tiers 2 and 3, the
/// checks of the tier below it follows up. A tier 2 or 3 run makes only its tests (with the Connection and
/// Capabilities checks they need), for the members the run it follows up found problems with.
/// </summary>
public static class Tiers
{
    public const int Max = 3;

    private static readonly string[] Rate = ["Diagnose.{member}", "ConformU.Diagnose.{member}"];
    private static readonly string[] RateCause =
        ["Diagnose.{member}.Cause", "ConformU.Diagnose.{member}.Cause", "Diagnose.{member}.Summary", "ConformU.Diagnose.{member}.Summary"];
    private static readonly string[] Pulse = ["Diagnose.PulseGuide.{axis}", "ConformU.Diagnose.PulseGuide.{axis}"];

    /// <summary>
    /// Every tier 2 and 3 diagnosis that can be run. A new one goes here (tier, member, the catalog entries of its
    /// findings), with its findings in the catalog (each with its tier and the lower tier checks it follows up) and its
    /// measurement in <see cref="Checks.Diagnostics"/>. CatalogTests checks that they agree.
    /// </summary>
    public static readonly IReadOnlyList<Diagnosis> Diagnoses =
    [
        new(2, "RightAscensionRate", Rate,
            "ConformU's offset and one 800 times larger, each way, against the readings' scatter: a wrong rate, or only the small offset off."),
        new(2, "DeclinationRate", Rate, "As for RightAscensionRate, in Declination."),
        new(2, "PulseGuide", Pulse,
            "Pulses of three lengths each way, read as each ends and a moment later: a stale reading, a wrong guide rate, or time lost per pulse."),
        new(3, "RightAscensionRate", RateCause,
            "The tier 2 offsets again, with no offset at all and the small one for three times as long: a drift, wandering readings, or a rounded small offset."),
        new(3, "DeclinationRate", RateCause, "As for RightAscensionRate, in Declination.")
    ];

    public static string Name(int tier) => tier switch
    {
        1 => "Tier 1: find the problems",
        2 => "Tier 2: identify the core issue",
        3 => "Tier 3: verify and conclude",
        _ => $"Tier {tier}"
    };

    /// <summary>What a tier does, for the ASCOM Conformance Check or (<paramref name="conformU"/>) the ConformU Debug Log Test.</summary>
    public static string Purpose(int tier, bool conformU) => (tier, conformU) switch
    {
        (1, false) => "Initial testing to identify problems with the known ASCOM standards.",
        (1, true) => "Initial testing to verify the problems already found in the ConformU log.",
        (2, false) => "Further tests within the problem area, with the source code reviewed, to identify the core issue and possible fixes.",
        (2, true) => "Further tests (run or created) within the problem area, with the source code reviewed, to identify the core issue and possible fixes.",
        (3, false) => "Deep dive tests to verify tier 2's recommendations, with the source code and other resources, for a final analysis, summary and recommended fixes.",
        (3, true) => "Deep dive tests (run or created) to verify tier 2's recommendations, with the source code and other resources, for a final analysis, summary and recommended fixes.",
        _ => ""
    };

    /// <summary>The members that have a diagnosis at <paramref name="tier"/> (none at tier 1: it finds, it doesn't diagnose).</summary>
    /// <summary>The members that have a diagnosis at <paramref name="tier"/> (none at tier 1: it finds, it doesn't diagnose).</summary>
    public static IReadOnlyList<string> MembersAt(int tier) => [.. Diagnoses.Where(d => d.Tier == tier).Select(d => d.Member).Distinct()];

    public static bool Has(string member, int tier) => Diagnosis(member, tier) is not null;

    public static Diagnosis? Diagnosis(string member, int tier)
        => Diagnoses.FirstOrDefault(d => d.Tier == tier && string.Equals(d.Member, member, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The findings of <paramref name="report"/> that the tier above's diagnosis of <paramref name="member"/> follows
    /// up: findings about the member, of a check it lists (<see cref="Diagnosis.FollowsUp"/>), that found something. A
    /// ConformU verdict counts whatever it says (ConformU found the problem); a tier 1 check counts when it's a problem;
    /// a diagnosis when it found a fault or explained it (Fail or Warning): one that couldn't measure (Error) or found
    /// it within tolerance (Info) has nothing more to dig into.
    /// </summary>
    public static List<CheckResult> ProblemsWith(RunReport report, string member)
    {
        if (Diagnosis(member, report.Options.Tier + 1) is not { } diagnosis) return [];
        return [.. report.Results.Where(r =>
            string.Equals(r.Member, member, StringComparison.OrdinalIgnoreCase)
            && TestCatalog.For(r.Id) is { } entry && diagnosis.FollowsUp.Contains($"{entry.Group}.{entry.Id}")
            && (r.Group == "ConformU" && report.Options.Tier == 1 && !IsDiagnosis(r)
                || (report.Options.Tier == 1 ? r.IsProblem : r.Outcome is Outcome.Fail or Outcome.Warning)))];
    }

    /// <summary>A diagnosis finding: Diagnose.* (a conformance run's) or ConformU.Diagnose.* (a ConformU run's).</summary>
    public static bool IsDiagnosis(CheckResult r) => r.Id.StartsWith("Diagnose.", StringComparison.Ordinal) || r.Id.StartsWith("ConformU.Diagnose.", StringComparison.Ordinal);

    /// <summary>The deeper run <paramref name="report"/> can be followed up with, or null: no tier left, or nothing it found has a diagnosis there.</summary>
    public static NextTier? Next(RunReport report)
    {
        var tier = report.Options.Tier + 1;
        if (tier > Max) return null;
        var members = MembersAt(tier).Where(m => ProblemsWith(report, m).Count > 0).ToList();
        return members.Count == 0 ? null : new NextTier(tier, members, report);
    }

    /// <summary>
    /// The options for the next tier's run: the user's options now, narrowed to the members to diagnose, with only the
    /// checks the diagnoses need (connection and capabilities) and the diagnoses: the ConformU group's for a ConformU
    /// run (so its verdicts follow), the Diagnose group's for a conformance run.
    /// </summary>
    public static RunOptions Options(RunOptions current, NextTier next) => current with
    {
        Tier = next.Tier,
        FollowsUp = next.From.Id,
        Members = next.Members,
        Groups = ["Connection", "Capabilities", next.From.ConformULog is null ? "Diagnose" : "ConformU"]
    };

    /// <summary>
    /// Why a run can't be made at its tier, or null. Tier 1 always comes first: a tier 2 run must follow up a tier 1 run
    /// of the same kind (conformance or ConformU), and a tier 3 run a tier 2 run, and it may only diagnose members that
    /// run found problems with and that have a diagnosis at the tier.
    /// </summary>
    public static string? Problem(RunOptions options, RunReport? followsUp, bool conformU)
    {
        var tier = options.Tier;
        if (tier is < 1 or > Max) return $"there is no tier {tier}: tiers go from 1 to {Max}.";
        if (tier == 1) return null;
        if (followsUp is null)
            return $"a tier {tier} run follows up a tier {tier - 1} run: run tier 1 first, then go deeper from what it found.";
        if (followsUp.Options.Tier != tier - 1)
            return $"a tier {tier} run follows up a tier {tier - 1} run, but run {followsUp.Id} is tier {followsUp.Options.Tier}.";
        if ((followsUp.ConformULog is not null) != conformU)
            return conformU ? $"run {followsUp.Id} wasn't a ConformU run: follow up a ConformU log's tier {tier - 1} run."
                : $"run {followsUp.Id} followed up a ConformU log: give the same log to go deeper.";
        var members = options.Members ?? [];
        var allowed = Next(followsUp)?.Members ?? [];
        var stray = members.Where(m => !allowed.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
        if (members.Count == 0 || stray.Count > 0)
            return allowed.Count == 0
                ? $"run {followsUp.Id} found nothing tier {tier} can diagnose."
                : $"tier {tier} can only diagnose what run {followsUp.Id} found: {string.Join(", ", allowed)}.";
        return null;
    }

    /// <summary>What the run being followed up found wrong with <paramref name="member"/>, for a diagnosis's Expected.</summary>
    public static string Said(RunReport? from, string member)
    {
        if (from is null) return $"A tier 1 problem with {member} (no report given to follow up).";
        var problems = ProblemsWith(from, member);
        if (problems.Count == 0) return $"Tier {from.Options.Tier} run {from.Id} found no problem with {member}.";
        return $"Tier {from.Options.Tier} ({from.Id}) found:\n" +
               string.Join('\n', problems.Take(5).Select(r => $"{r.Id} ({r.Outcome}): {r.Message}")) +
               (problems.Count > 5 ? $"\n… and {problems.Count - 5} more" : "");
    }

    /// <summary>
    /// The runs linked with <paramref name="run"/> through its tiers: the tier 1 run its chain starts from, and every
    /// run that follows that one up (directly or through another), <paramref name="run"/> included. By tier, then time.
    /// </summary>
    public static List<RunReport> Chain(RunReport run, IEnumerable<RunReport> all)
    {
        var runs = all.ToList();
        var byId = runs.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        var root = Root(run, byId);
        var chain = new List<RunReport> { root };
        for (var i = 0; i < chain.Count; i++)
            chain.AddRange(runs.Where(r => r.Options.FollowsUp == chain[i].Id && chain.All(c => c.Id != r.Id)));
        if (chain.All(c => c.Id != run.Id)) chain.Add(run);
        return [.. chain.OrderBy(r => r.Options.Tier).ThenBy(r => r.Started)];
    }

    /// <summary>The numbers of the tests of a run's own tier it made (a tier 2 run's 2001…), in order.</summary>
    public static List<int> OwnTests(RunReport run)
        => [.. run.Results.Select(r => r.Number).Where(n => n > 0 && TestNumbers.TierOf(n) == run.Options.Tier).Distinct().Order()];

    /// <summary>The tier 1 run a run's chain of follow-ups starts from (itself at tier 1), among <paramref name="byId"/>.</summary>
    public static RunReport Root(RunReport run, IReadOnlyDictionary<string, RunReport> byId)
    {
        var seen = new HashSet<string>();
        while (run.Options.FollowsUp is { } up && seen.Add(run.Id) && byId.TryGetValue(up, out var parent)) run = parent;
        return run;
    }
}
