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

using GS.TestHarness.Engine.Checks;

namespace GS.TestHarness.Engine;

/// <summary>What the tier after a run would do (<see cref="Tiers.OutlookFor"/>).</summary>
public enum OutlookKind
{
    /// <summary>Offered: it follows up some of what the run found.</summary>
    Offered,

    /// <summary>Not needed: nothing found to take further, or the run already pinned it down.</summary>
    NotNeeded,

    /// <summary>Not available: the run found problems no deeper test follows up.</summary>
    NotAvailable,

    /// <summary>The run was the last tier.</summary>
    Deepest
}

/// <summary>
/// The tier after a run: offered or not and why (<see cref="Why"/>), the run it would make (<see cref="Next"/>, when
/// offered), and the problems no test of that tier follows up (<see cref="Uncovered"/>).
/// </summary>
public sealed record Outlook(int Tier, OutlookKind Kind, string Why, NextTier? Next, IReadOnlyList<CheckResult> Uncovered)
{
    /// <summary>A short label: "Available", "Not needed", "Not available" or "Last tier".</summary>
    public string Label => Kind switch
    {
        OutlookKind.Offered => "Available",
        OutlookKind.NotNeeded => "Not needed",
        OutlookKind.NotAvailable => "Not available",
        _ => "Last tier"
    };
}

/// <summary>A deeper run a finished one can be followed up with: its tier, the members it diagnoses, and the run it follows up.</summary>
public sealed record NextTier(int Tier, IReadOnlyList<string> Members, RunReport From);

/// <summary>
/// One diagnosis that can be run: its tier, the member it takes apart, and the catalog entries of the findings it
/// makes (<see cref="Produces"/>, keys as Group.Id). The tier and what it follows up are the catalog's
/// (<see cref="CatalogEntry.Tier"/>, <see cref="CatalogEntry.FollowsUp"/>): the catalog is the one list of every check.
/// </summary>
/// <param name="Member">The member it diagnoses, or for one that takes apart a kind of problem across members
/// (<paramref name="AnyMember"/>), the area's name ("NotConnected", "Arguments"...): what the next tier offers.</param>
/// <param name="AnyMember">Whether it follows up its checks whatever member they found a problem with (the area
/// diagnoses), rather than only the findings about <paramref name="Member"/>.</param>
public sealed record Diagnosis(int Tier, string Member, IReadOnlyList<string> Produces, string What, bool AnyMember = false)
{
    /// <summary>The checks of the tier below it follows up: those of the catalog entries it produces.</summary>
    public IReadOnlyList<string> FollowsUp => [.. Produces.SelectMany(k => TestCatalog.Entry(k)?.FollowsUp ?? []).Distinct()];

    /// <summary>Whether it diagnoses a ConformU run's problems (it makes ConformU.Diagnose.* findings).</summary>
    public bool ForConformU => Produces.Any(k => k.StartsWith("ConformU.", StringComparison.Ordinal));

    /// <summary>Whether it diagnoses a conformance run's problems (it makes Diagnose.* findings).</summary>
    public bool ForConformance => Produces.Any(k => k.StartsWith("Diagnose.", StringComparison.Ordinal));
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

    /// <summary>
    /// A chained run goes no deeper when a tier's <see cref="ProblemScore"/> is more than this: that many problems may mean
    /// the mount isn't safe to keep testing. (Ten failures of tests weighing 4 would be enough.) The user can change it: see <see cref="WeightStore"/>.
    /// </summary>
    public static int MaxProblemsToContinue => TestWeights.Limit;

    /// <summary>The Fail and Error findings of a run, each with its test's weight (<see cref="TestWeights"/>, 1 to 10), heaviest first.</summary>
    public static List<(CheckResult Finding, int Weight)> WeightedProblems(RunReport report)
        => [.. report.Results.Where(r => r.Outcome is Outcome.Fail or Outcome.Error)
            .Select(r => (r, TestWeights.Of(r))).OrderByDescending(x => x.Item2)];

    /// <summary>How worrying a run's problems are: the weights of its Fail and Error findings added up. Warnings don't count.</summary>
    public static int ProblemScore(RunReport report) => WeightedProblems(report).Sum(x => x.Weight);

    /// <summary>The failed tests so important (<see cref="TestWeights.Critical"/>) that a run can't go on after one.</summary>
    public static List<CheckResult> CriticalFailures(RunReport report)
        => [.. WeightedProblems(report).Where(x => x.Weight >= TestWeights.Critical).Select(x => x.Finding)];

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
        new(3, "DeclinationRate", RateCause, "As for RightAscensionRate, in Declination."),

        // The ASCOM Conformance Check's tier 2 for its other problems: each takes apart one kind of problem, for every
        // member tier 1 found it with, by testing around it and reviewing the member's source (AreaDiagnostics).
        new(2, AreaDiagnostics.Arguments, ["Diagnose.Arguments.{member}"],
            "Every invalid value (and the valid ones) of a member that took an invalid argument (an axis, a direction, coordinates, a setting's value): no check, a partial one, the wrong error, a value that reaches the mount code, or HTTP 400 from the Alpaca server; with the member's code reviewed.", AnyMember: true),
        new(2, AreaDiagnostics.ClientConnections, ["Diagnose.Clients.Connections"],
            "Two more clients connect beside the run's own; one disconnects, connects again, and they all disconnect, every client's Connected and a read after each step: a count of clients, or one Disconnect turning the hardware off for all; the connection code reviewed.", AnyMember: true),
        new(2, AreaDiagnostics.AlpacaRequests, ["Diagnose.Alpaca.Binding", "Diagnose.Alpaca.Setup"],
            "Requests straight to the Alpaca server: values the driver must judge against ones nobody can parse, bad ClientTransactionIDs, and the setup pages: what the server's own request validation refuses with HTTP 400, and what a setup page throws; the controller code reviewed.", AnyMember: true),
        new(2, AreaDiagnostics.Actions, ["Diagnose.Actions"],
            "Action with a made-up, an empty and a blank name against SupportedActions: the error an unknown action gets, where the code throws it, and the names Action handles against those it lists.", AnyMember: true),
        new(2, AreaDiagnostics.Discovery, ["Diagnose.Discovery"],
            "Discovery sent to the server's address, to this PC's own network addresses and to each broadcast address: a server listening on localhost only, a broadcast that doesn't reach it, or no discovery at all; the listener's code reviewed.", AnyMember: true),
        new(2, AreaDiagnostics.RateOffsets, ["Diagnose.RateOffsets.{member}"],
            "An offset set at sidereal rate, read at each other tracking rate and its movement measured at one: the mount still applying it, or only the reading wrong; with the getter and TrackingRate's setter reviewed.", AnyMember: true),
        new(2, AreaDiagnostics.Preconditions, ["Diagnose.Preconditions.{member}"],
            "The member and its siblings with tracking off, or during a slew: whether only this one lacks the guard, and what it does instead; the guards the siblings' code has and this one's lacks.", AnyMember: true),
        new(2, AreaDiagnostics.NotConnected, ["Diagnose.NotConnected"],
            "Every operational member read while this client is disconnected: one missing connection check shared by all, or a few members; with the members' code reviewed.", AnyMember: true),
        new(3, "SlewSettleTime", ["Diagnose.SlewSettleTime.Cause", "Diagnose.SlewSettleTime.Summary"],
            "Slews of 0.5°, 2° and 8° in Dec and 2° in RA, one with a 3 s settle time and one aborted, read finely enough to see a final correction creep: a wait with the mount still, or the slew's own last stage; where the settle wait sits in the code; and the final analysis."),
        new(2, "SlewSettleTime", ["Diagnose.SlewSettleTime"],
            "Short slews with settle times of 0, 2 and 5 s, timing when the mount stops and when Slewing ends: the value not kept, kept but not used, or used in the wrong units; where the code uses it."),
        new(2, "AxisRates", ["Diagnose.AxisRates.Axes"],
            "CanMoveAxis and AxisRates for every axis: rates for an axis that can't move, or the same rates for every axis; the member's code reviewed."),
        new(2, "DriverVersion", ["Diagnose.DriverVersion"],
            "DriverVersion against DriverInfo and the driver program's own version: where the version comes from, and the code that formats it."),
        new(2, AreaDiagnostics.Registration, ["Diagnose.Registration"],
            "The COM server's registration in both registry views, the program's bitness, and starting the driver from a 64-bit and a 32-bit process: whether clients are really affected; the registration code reviewed.", AnyMember: true)
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
        // A diagnosis follows up its own kind of run: a ConformU log's problems, or a conformance run's.
        if (report.ConformULog is not null ? !diagnosis.ForConformU : !diagnosis.ForConformance) return [];
        return [.. report.Results.Where(r =>
            (diagnosis.AnyMember || string.Equals(r.Member, member, StringComparison.OrdinalIgnoreCase))
            && TestCatalog.For(r.Id) is { } entry && diagnosis.FollowsUp.Contains($"{entry.Group}.{entry.Id}")
            && (r.Group == "ConformU" && report.Options.Tier == 1 && !IsDiagnosis(r)
                || (report.Options.Tier == 1 ? r.IsProblem : r.Outcome is Outcome.Fail or Outcome.Warning)))];
    }

    /// <summary>
    /// A diagnosis finding: Diagnose.* (a conformance run's) or ConformU.Diagnose.* (a ConformU run's). The pre-motion gate's
    /// finding (Diagnose.Gate, in a tier 2 or 3 run) is not one: it is about the run, not a member.
    /// </summary>
    public static bool IsDiagnosis(CheckResult r)
        => (r.Id.StartsWith("Diagnose.", StringComparison.Ordinal) || r.Id.StartsWith("ConformU.Diagnose.", StringComparison.Ordinal))
           && !r.Id.EndsWith(".Gate", StringComparison.Ordinal);

    /// <summary>
    /// What the tier after <paramref name="report"/> would do, and why: <see cref="Outlook.Kind"/> says whether it's
    /// offered, not needed (nothing found to dig into, or tier 2 already pinned it down), not available (problems no
    /// deeper test follows up) or past the last tier; <see cref="Outlook.Uncovered"/> lists the problems no deeper test
    /// follows up, even when the tier is offered for others.
    /// </summary>
    public static Outlook OutlookFor(RunReport report)
    {
        var from = report.Options.Tier;
        var tier = from + 1;
        if (from >= Max)
            return new Outlook(tier, OutlookKind.Deepest, "Tier 3 is the last tier: its Summary findings give the final analysis and recommended fixes.", null, []);
        var next = Next(report);
        // What the next tier could take further: tier 1's problems (and a ConformU run's verdicts, whatever they say),
        // a deeper tier's diagnoses that found a fault or explained it.
        var candidates = report.Results.Where(r =>
                from == 1 ? !IsDiagnosis(r) && (r.IsProblem || r.Group == "ConformU" && r.Id.EndsWith(".Reproduced", StringComparison.Ordinal))
                    : IsDiagnosis(r) && r.Outcome is Outcome.Fail or Outcome.Warning)
            .ToList();
        var covered = next is null ? [] : next.Members.SelectMany(m => ProblemsWith(report, m)).ToHashSet();
        var uncovered = candidates.Where(r => !covered.Contains(r))
            .GroupBy(r => r.Id).Select(g => g.First()).ToList();
        if (next is not null)
            return new Outlook(tier, OutlookKind.Offered, $"Available for what tier {from} found: {string.Join(", ", next.Members)}.", next, uncovered);
        if (candidates.Count == 0)
            return new Outlook(tier, OutlookKind.NotNeeded,
                from == 1 ? "Not needed: tier 1 found no problems." : $"Not needed: tier {from}'s diagnoses found nothing wrong to take further.", null, []);
        return from == 1
            ? new Outlook(tier, OutlookKind.NotAvailable,
                $"Not available: no tier 2 test follows up what tier 1 found ({uncovered.Count} problem{(uncovered.Count == 1 ? "" : "s")}). Each finding's fix is what to do.", null, uncovered)
            : new Outlook(tier, OutlookKind.NotNeeded,
                $"Not needed: tier {from} pinned these down in the code (each finding names the cause and the change); no tier {tier} test goes further.", null, uncovered);
    }

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
            return $"a tier {tier} run follows up a tier {tier - 1} run, but report {followsUp.Reference} is tier {followsUp.Options.Tier}.";
        if ((followsUp.ConformULog is not null) != conformU)
            return conformU ? $"report {followsUp.Reference} wasn't a ConformU run: follow up a ConformU log's tier {tier - 1} run."
                : $"report {followsUp.Reference} followed up a ConformU log: give the same log to go deeper.";
        var members = options.Members ?? [];
        var allowed = Next(followsUp)?.Members ?? [];
        var stray = members.Where(m => !allowed.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
        if (members.Count == 0 || stray.Count > 0)
            return allowed.Count == 0
                ? $"report {followsUp.Reference} found nothing tier {tier} can diagnose."
                : $"tier {tier} can only diagnose what report {followsUp.Reference} found: {string.Join(", ", allowed)}.";
        return null;
    }

    /// <summary>What the run being followed up found wrong with <paramref name="member"/>, for a diagnosis's Expected.</summary>
    public static string Said(RunReport? from, string member)
    {
        if (from is null) return $"A tier 1 problem with {member} (no report given to follow up).";
        var problems = ProblemsWith(from, member);
        if (problems.Count == 0) return $"Tier {from.Options.Tier} report {from.Reference} found no problem with {member}.";
        return $"Tier {from.Options.Tier} ({from.Reference}) found:\n" +
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
