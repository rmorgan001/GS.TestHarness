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

/// <summary>
/// The Re-run button. A tier 1 run is run again whole (a full check, or the same ConformU follow-up); a tier 2 or 3 run is its own
/// diagnosis again, which is what <see cref="SameTier"/> works out.
/// </summary>
public static class RerunPlan
{
    /// <summary>
    /// Re-running a tier 2 or 3 run: the same diagnosis again, of what the run below it found (<paramref name="followed"/>).
    /// Null if that run isn't this run's, or has nothing at this tier to diagnose any more. A tier 1 run isn't re-run this way.
    /// </summary>
    public static NextTier? SameTier(RunReport last, RunReport? followed)
        => last.Options.Tier > 1 && followed is not null && followed.Id == last.Options.FollowsUp && Tiers.Next(followed) is { } next && next.Tier == last.Options.Tier
            ? next : null;
}
