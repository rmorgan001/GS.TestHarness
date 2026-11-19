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

namespace GS.TestHarness.Engine.Checks;

/// <summary>
/// The ASCOM Conformance Check's tier 2 and 3 (<see cref="Tiers"/>): diagnoses the members the run it follows up
/// (<see cref="CheckContext.FollowsUp"/>) found problems with. A ConformU run's deeper tiers diagnose in the ConformU
/// group instead, so its verdicts follow.
/// </summary>
public sealed class DiagnoseChecks : ICheckGroup
{
    public const string GroupName = "Diagnose";

    public string Name => GroupName;

    public string Description => "Tiers 2 and 3 of the ASCOM Conformance Check: measures the problems a lower tier found (rate offsets, pulse guiding), and tests around the others (arguments, preconditions, the connection, settle time, axis rates, version, registration) with the code reviewed, to find the core issue and say what to change.";

    public string? SkipReason(CheckContext ctx)
        => ctx.Options.Tier < 2 ? "it runs at tiers 2 and 3 only"
            : ctx.Options.Members is not { Count: > 0 } ? "no members to diagnose (follow up a run that found a problem)"
            : null;

    public async Task RunAsync(CheckContext ctx)
    {
        var members = ctx.Options.Members!;
        // The measurements (rate offsets, pulse guiding) first; then the areas, the one that disconnects last.
        var problems = members.Where(m => !AreaDiagnostics.Areas.Contains(m))
            .Select(m => new DiagnosisProblem(m, Tiers.Said(ctx.FollowsUp, m)))
            .ToList();
        await Diagnostics.RunAsync(ctx, problems);
        foreach (var area in members.Where(AreaDiagnostics.Areas.Contains).OrderBy(m => m == AreaDiagnostics.NotConnected))
            await AreaDiagnostics.RunAsync(ctx, area);
    }
}
