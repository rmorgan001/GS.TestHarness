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

/// <summary>What the user chooses at the pre-motion gate.</summary>
public enum GateDecision
{
    /// <summary>Run the groups that move the mount anyway.</summary>
    Proceed,

    /// <summary>Skip the groups that move the mount; the rest of the run goes on.</summary>
    Skip,

    /// <summary>End the run here, keeping the findings so far.</summary>
    Stop
}

/// <summary>
/// The gate before the first group that moves the mount: gathers what looks unsafe (a position, the time or the site
/// can't be trusted; the mount is already slewing, parked or away from home) as warnings, which the user answers with
/// Proceed, Skip or Stop. Nothing here stops anything by itself: a warning only asks.
/// </summary>
public static class MotionGate
{
    /// <summary>The groups that move the mount (or may, at tiers 2 and 3 or with a ConformU log).</summary>
    public static readonly IReadOnlySet<string> MovingGroups = new HashSet<string> { "Motion", "ParkHome", "Tracking", "Diagnose", "ConformU" };

    /// <summary>
    /// The members whose problems matter before the mount moves: where it is, what the sky and the site are, whether it
    /// is moving. The slews' targets come from them.
    /// </summary>
    private static readonly HashSet<string> SafetyMembers = new(StringComparer.Ordinal)
    {
        "RightAscension", "Declination", "Altitude", "Azimuth", "SiderealTime", "SiteLatitude", "SiteLongitude", "UTCDate",
        "Slewing", "AtPark", "AtHome", "SideOfPier", "Tracking", "Connected"
    };

    private const int MostListed = 8;

    /// <summary>
    /// The warnings for the run so far and the mount now; empty when there is nothing to ask. <paramref name="unattended"/>
    /// (the command line, which has no start dialog to put the mount at home): a mount that is parked or away from home
    /// is left to the start-position finding, not a warning that stops the run.
    /// </summary>
    public static async Task<IReadOnlyList<string>> EvaluateAsync(CheckContext ctx, bool unattended = false)
    {
        var warnings = new List<string>();

        // Findings already made about what the slews depend on.
        foreach (var r in ctx.Results.Where(r => r.IsProblem && (SafetyMembers.Contains(r.Member) || r.Id.StartsWith("Properties.AltAz", StringComparison.Ordinal))))
            warnings.Add($"{TestNumbers.Label(r.Id)}: {r.Message}");
        if (warnings.Count > MostListed)
        {
            var more = warnings.Count - MostListed;
            warnings = [.. warnings.Take(MostListed), $"…and {more} more finding{(more == 1 ? "" : "s")} like these (see the results)."];
        }

        // The mount now.
        if ((await ctx.TryAsync(t => t.Slewing)).Value)
            warnings.Add("The mount is already slewing.");
        var parked = (await ctx.TryAsync(t => t.AtPark)).Value;
        if (unattended) return warnings;
        if (parked)
            warnings.Add("The mount is parked: the motion checks will unpark it first.");
        else if (await ctx.TryAsync(t => t.AtHome) is { Ok: true, Value: false })
            warnings.Add("The mount isn't at the home position.");
        return warnings;
    }
}
