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

using ASCOM.Common.DeviceInterfaces;

namespace GS.TestHarness.Engine.Checks;

/// <summary>Methods that don't move the mount: AxisRates and DestinationSideOfPier.</summary>
public sealed class MethodChecks : ICheckGroup
{
    public string Name => "Methods";
    public string Description => "AxisRates for each axis (and an invalid one) and DestinationSideOfPier: methods that don't move the mount.";
    public string? SkipReason(CheckContext ctx) => null;

    public async Task RunAsync(CheckContext ctx)
    {
        foreach (var axis in new[] { TelescopeAxis.Primary, TelescopeAxis.Secondary, TelescopeAxis.Tertiary })
        {
            var canMove = ctx.Caps.CanMoveAxis[(int)axis];
            var call = await ctx.TryAsync(t => Values.Rates(t.AxisRates(axis)));
            var id = $"AxisRates.{axis}";
            if (!call.Ok)
            {
                ctx.Report(id, "AxisRates", Outcome.Fail, $"AxisRates({axis}) raised an error.",
                    expected: canMove ? "the axis's rate ranges" : "an empty collection", actual: call.ErrorText, ms: call.Ms,
                    fix: "AxisRates is mandatory for all three axes: return an empty collection for an axis that can't be moved.");
                continue;
            }
            var rates = call.Value ?? [];
            ctx.Facts[id] = rates;
            string? problem = null;
            if (call.Value is null) problem = "it returned null instead of a collection";
            else if (!canMove && rates.Count > 0) problem = $"CanMoveAxis({axis}) is false, so the collection must be empty";
            else if (canMove && rates.Count == 0) problem = $"CanMoveAxis({axis}) is true, so at least one rate range is needed";
            else if (rates.Any(r => r.Minimum < 0 || r.Maximum < 0)) problem = "rates must be positive (MoveAxis takes the direction from the sign)";
            else if (rates.Any(r => r.Minimum > r.Maximum)) problem = "a range has its Minimum above its Maximum";
            else
            {
                var sorted = rates.OrderBy(r => r.Minimum).ToList();
                for (var i = 1; i < sorted.Count && problem is null; i++)
                    if (sorted[i].Minimum < sorted[i - 1].Maximum) problem = "the ranges overlap";
            }
            var shown = string.Join(", ", rates.Select(r => $"{CheckContext.Show(r.Minimum)}..{CheckContext.Show(r.Maximum)}"));
            ctx.Report(id, "AxisRates", problem is null ? Outcome.Pass : Outcome.Fail,
                problem is null ? $"AxisRates({axis}) = [{shown}] °/s" : $"AxisRates({axis}): {problem}.",
                actual: $"[{shown}]", ms: call.Ms, fix: problem is null ? null : $"Correct AxisRates: {problem}.");
        }
        await ctx.ExpectErrorAsync("AxisRates.InvalidAxis", "AxisRates", "AxisRates(3)",
            t => _ = t.AxisRates((TelescopeAxis)3), AscomErrorKind.InvalidValue, "the axis isn't 0, 1 or 2");

        await DestinationSideOfPierAsync(ctx);
    }

    private static async Task DestinationSideOfPierAsync(CheckContext ctx)
    {
        var lst = await ctx.TryAsync(t => t.SiderealTime);
        if (!lst.Ok) return;
        var dec = SafeDeclination(ctx);
        // As ConformU: two hours either side of the meridian, clear of any tracking past it.
        var east = Values.Wrap24(lst.Value + 2); // hour angle -2: east of the meridian
        var west = Values.Wrap24(lst.Value - 2); // hour angle +2: west of the meridian

        var toEast = await ctx.TryAsync(t => t.DestinationSideOfPier(east, dec));
        if (toEast.Kind == AscomErrorKind.NotImplemented)
        {
            ctx.Report("DestinationSideOfPier.Read", "DestinationSideOfPier",
                ctx.Caps.AlignmentMode == AlignmentMode.GermanPolar ? Outcome.Warning : Outcome.Info,
                "DestinationSideOfPier is not implemented.", ms: toEast.Ms,
                fix: "Optional, but a German equatorial mount should implement it: imaging programs use it to plan meridian flips.");
            return;
        }
        var toWest = await ctx.TryAsync(t => t.DestinationSideOfPier(west, dec));
        if (!toEast.Ok || !toWest.Ok)
        {
            ctx.Report("DestinationSideOfPier.Read", "DestinationSideOfPier", Outcome.Fail, "DestinationSideOfPier raised an error.",
                actual: toEast.Ok ? toWest.ErrorText : toEast.ErrorText,
                fix: "DestinationSideOfPier must return the side of pier the mount would be on after slewing to the coordinates, " +
                     "or throw MethodNotImplementedException.");
            return;
        }
        ctx.Facts["DestinationSideOfPier"] = true;
        if (ctx.Caps.AlignmentMode == AlignmentMode.GermanPolar)
        {
            var normal = toEast.Value == PointingState.ThroughThePole && toWest.Value == PointingState.Normal;
            var problem = toEast.Value == PointingState.Unknown || toWest.Value == PointingState.Unknown
                    ? "it returned pierUnknown"
                : toEast.Value == toWest.Value ? "it gives the same side on both sides of the meridian"
                : !normal ? "the sides are the wrong way round (physical pier side rather than pointing state?)" : null;
            ctx.Report("DestinationSideOfPier.Sides", "DestinationSideOfPier", problem is null ? Outcome.Pass : Outcome.Fail,
                problem is null
                    ? "DestinationSideOfPier: pierWest (through the pole) east of the meridian, pierEast (normal) west of it."
                    : $"DestinationSideOfPier: {problem}.",
                expected: "2 h east of the meridian: pierWest (ThroughThePole); 2 h west: pierEast (Normal)",
                actual: $"east: {toEast.Value}; west: {toWest.Value}",
                fix: "DestinationSideOfPier is \"the pointing state in which the mount will be if slewed to the given " +
                     "coordinates\": pierEast is the normal pointing state (west of the meridian, positive hour angle), pierWest " +
                     "through the pole. ConformU treats the same answer on both sides, or reversed sides, as an issue. Work it " +
                     "out from the target's hour angle (LST - RA) and the flip limits, not from the current side.");
        }
        else
        {
            ctx.Report("DestinationSideOfPier.Read", "DestinationSideOfPier", Outcome.Pass,
                $"DestinationSideOfPier = {toEast.Value} (east) / {toWest.Value} (west).");
        }

        await ctx.ExpectErrorAsync("DestinationSideOfPier.InvalidRa", "DestinationSideOfPier", "DestinationSideOfPier(25, dec)",
            t => _ = t.DestinationSideOfPier(25, dec), AscomErrorKind.InvalidValue, "right ascension is 0 up to 24 hours");
        await ctx.ExpectErrorAsync("DestinationSideOfPier.InvalidDec", "DestinationSideOfPier", "DestinationSideOfPier(ra, 91)",
            t => _ = t.DestinationSideOfPier(west, 91), AscomErrorKind.InvalidValue, "declination is -90 to 90 degrees");
    }

    /// <summary>A declination that is high in the sky an hour either side of the meridian, at the site's latitude.</summary>
    public static double SafeDeclination(CheckContext ctx)
    {
        var lat = ctx.Caps.SiteLatitude ?? 30;
        return Math.Abs(lat) >= 20 ? Math.Sign(lat) * 45 : 0;
    }
}
