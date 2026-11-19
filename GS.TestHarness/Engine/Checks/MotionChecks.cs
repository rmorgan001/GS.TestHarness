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

using System.Diagnostics;
using ASCOM.Common.DeviceInterfaces;

namespace GS.TestHarness.Engine.Checks;

/// <summary>
/// Slewing, syncing, axis moves, pulse guiding, tracking and pier side: everything that moves the mount (only
/// with <see cref="RunOptions.AllowMotion"/>). The mount is unparked first, and its tracking state and parked
/// state are put back afterwards.
/// </summary>
public sealed class MotionChecks : ICheckGroup
{
    public string Name => "Motion";
    public string Description => "Slews (sync, async, to target, alt/az), meridian flips, syncs, MoveAxis, PulseGuide, AbortSlew, tracking and rate offsets. Moves the mount.";

    public string? SkipReason(CheckContext ctx) => ctx.Options.AllowMotion ? null : "motion is turned off for this run";

    // As ConformU: within 10 arc-seconds is right; further is worth knowing (Info); a degree or more is plainly wrong.
    private const double PositionOk = 10.0 / 3600; // degrees
    private const double PositionFail = 1.0;

    public async Task RunAsync(CheckContext ctx)
    {
        var c = ctx.Caps;
        var wasTracking = (await ctx.TryAsync(t => t.Tracking)).Value;
        var wasParked = (await ctx.TryAsync(t => t.AtPark)).Value;
        try
        {
            if (wasParked)
            {
                if (!c.CanUnpark)
                {
                    ctx.Report("Prepare", "AtPark", Outcome.Skipped, "The mount is parked and can't unpark, so it can't be moved.");
                    return;
                }
                var unpark = await ctx.TryAsync(t => t.Unpark(), ctx.SlewTimeout);
                await ctx.WaitUntilAsync(t => !t.AtPark && !t.Slewing, ctx.SlewTimeout);
                if (!unpark.Ok)
                {
                    ctx.Report("Prepare", "Unpark", Outcome.Error, "The mount is parked and wouldn't unpark.", actual: unpark.ErrorText);
                    return;
                }
            }
            if (c.CanSetTracking) await ctx.TryAsync(t => t.Tracking = true);

            // Each section only if the run wants one of its members (a ConformU run wants only its problem members).
            if (ctx.Wants(NotImplementedMembers)) await NotImplementedMethodsAsync(ctx);
            if (ctx.Wants("SlewToCoordinates", "SlewToCoordinatesAsync", "SlewToTarget", "SlewToTargetAsync", "SideOfPier",
                    "DestinationSideOfPier", "Slewing", "SlewSettleTime", "TargetRightAscension", "TargetDeclination"))
                await SlewsAsync(ctx);
            if (ctx.Wants("AbortSlew")) await AbortAsync(ctx);
            if (ctx.Wants("SyncToCoordinates", "SyncToTarget", "TargetRightAscension", "TargetDeclination")) await SyncsAsync(ctx);
            if (ctx.Wants("SlewToAltAz", "SlewToAltAzAsync", "SyncToAltAz")) await AltAzAsync(ctx);
            if (ctx.Wants("MoveAxis", "AxisRates", "CanMoveAxis")) await MoveAxisAsync(ctx);
            if (ctx.Wants("PulseGuide", "IsPulseGuiding", "GuideRateRightAscension", "GuideRateDeclination")) await PulseGuideAsync(ctx);
            if (ctx.Wants("Tracking")) await TrackingAsync(ctx);
            if (ctx.Wants("RightAscensionRate", "DeclinationRate")) await RateOffsetsAsync(ctx);
        }
        finally
        {
            // Leave the mount as it was found: stopped, tracking as before, parked if it was.
            await ctx.TryAsync(t => { if (t.Slewing) t.AbortSlew(); });
            for (var axis = 0; axis < 3; axis++)
                if (c.CanMoveAxis[axis]) await ctx.TryAsync(t => t.MoveAxis((TelescopeAxis)axis, 0));
            if (c.CanSetTracking) await ctx.TryAsync(t => t.Tracking = wasTracking);
            if (wasParked && c.CanPark)
            {
                await ctx.TryAsync(t => t.Park(), ctx.SlewTimeout);
                await ctx.WaitUntilAsync(t => t.AtPark, ctx.SlewTimeout);
            }
        }
    }

    // ---------- Members a false capability says aren't there ----------

    /// <summary>The members <see cref="NotImplementedMethodsAsync"/> calls.</summary>
    private static readonly string[] NotImplementedMembers =
        ["SlewToCoordinates", "SlewToCoordinatesAsync", "SlewToTarget", "SlewToTargetAsync", "SyncToCoordinates", "SyncToTarget",
         "PulseGuide", "FindHome", "Park", "Unpark", "SlewToAltAz", "SlewToAltAzAsync", "SyncToAltAz", "MoveAxis"];

    private static async Task NotImplementedMethodsAsync(CheckContext ctx)
    {
        var c = ctx.Caps;
        var pos = await PositionAsync(ctx);
        if (pos is null) return;
        var (ra, dec) = pos.Value;
        var altaz = await AltAzPositionAsync(ctx);

        async Task Check(string id, string member, bool capability, string capabilityName, Action<ITelescopeV4> call)
        {
            if (capability || !ctx.Wants(member)) return;
            var result = await ctx.ExpectNotImplementedAsync(id, member, capabilityName, call);
            if (!result.Ok) return;
            // It did something it said it couldn't: stop it, and undo a park.
            await StopAsync(ctx);
            if ((await ctx.TryAsync(t => t.AtPark)).Value) await ctx.TryAsync(t => t.Unpark(), ctx.SlewTimeout);
        }

        await Check("SlewToCoordinates.NotImplemented", "SlewToCoordinates", c.CanSlew, "CanSlew", t => t.SlewToCoordinates(ra, dec));
        await Check("SlewToCoordinatesAsync.NotImplemented", "SlewToCoordinatesAsync", c.CanSlewAsync, "CanSlewAsync", t => t.SlewToCoordinatesAsync(ra, dec));
        await Check("SlewToTarget.NotImplemented", "SlewToTarget", c.CanSlew, "CanSlew", t => { t.TargetRightAscension = ra; t.TargetDeclination = dec; t.SlewToTarget(); });
        await Check("SlewToTargetAsync.NotImplemented", "SlewToTargetAsync", c.CanSlewAsync, "CanSlewAsync", t => { t.TargetRightAscension = ra; t.TargetDeclination = dec; t.SlewToTargetAsync(); });
        await Check("SyncToCoordinates.NotImplemented", "SyncToCoordinates", c.CanSync, "CanSync", t => t.SyncToCoordinates(ra, dec));
        await Check("SyncToTarget.NotImplemented", "SyncToTarget", c.CanSync, "CanSync", t => { t.TargetRightAscension = ra; t.TargetDeclination = dec; t.SyncToTarget(); });
        await Check("PulseGuide.NotImplemented", "PulseGuide", c.CanPulseGuide, "CanPulseGuide", t => t.PulseGuide(GuideDirection.North, 100));
        await Check("FindHome.NotImplemented", "FindHome", c.CanFindHome, "CanFindHome", t => t.FindHome());
        await Check("Park.NotImplemented", "Park", c.CanPark, "CanPark", t => t.Park());
        await Check("Unpark.NotImplemented", "Unpark", c.CanUnpark, "CanUnpark", t => t.Unpark());
        if (altaz is { } p)
        {
            await Check("SlewToAltAz.NotImplemented", "SlewToAltAz", c.CanSlewAltAz, "CanSlewAltAz", t => t.SlewToAltAz(p.Az, p.Alt));
            await Check("SlewToAltAzAsync.NotImplemented", "SlewToAltAzAsync", c.CanSlewAltAzAsync, "CanSlewAltAzAsync", t => t.SlewToAltAzAsync(p.Az, p.Alt));
            await Check("SyncToAltAz.NotImplemented", "SyncToAltAz", c.CanSyncAltAz, "CanSyncAltAz", t => t.SyncToAltAz(p.Az, p.Alt));
        }
        for (var axis = 0; axis < 3; axis++)
        {
            var a = (TelescopeAxis)axis;
            if (c.CanMoveAxis[axis] || !ctx.Wants("MoveAxis")) continue;
            var result = await ctx.ExpectNotImplementedAsync($"MoveAxis.{a}.NotImplemented", "MoveAxis", $"CanMoveAxis({a})", t => t.MoveAxis(a, 0.1));
            if (result.Ok) await ctx.TryAsync(t => t.MoveAxis(a, 0));
        }
    }

    // ---------- Slews ----------

    private static async Task SlewsAsync(CheckContext ctx)
    {
        var c = ctx.Caps;
        if (!c.CanSlew && !c.CanSlewAsync) return;
        var lst = await ctx.TryAsync(t => t.SiderealTime);
        if (!lst.Ok) return;
        var dec = MethodChecks.SafeDeclination(ctx);
        // Three hours either side of the meridian, as ConformU's side-of-pier tests use.
        var east = Values.Wrap24(lst.Value + 3);
        var west = Values.Wrap24(lst.Value - 3);
        var gem = c.AlignmentMode == AlignmentMode.GermanPolar;

        if (c.CanSlewAsync)
        {
            await SlewAsync(ctx, "SlewToCoordinatesAsync.West", "SlewToCoordinatesAsync", west, dec, async: true,
                t => t.SlewToCoordinatesAsync(west, dec), expectSide: gem ? PointingState.Normal : null);
            await SlewAsync(ctx, "SlewToCoordinatesAsync.East", "SlewToCoordinatesAsync", east, dec, async: true,
                t => t.SlewToCoordinatesAsync(east, dec), expectSide: gem ? PointingState.ThroughThePole : null);
            var target = await ctx.TryAsync(t => { t.TargetRightAscension = west; t.TargetDeclination = dec; });
            if (target.Ok)
                await SlewAsync(ctx, "SlewToTargetAsync", "SlewToTargetAsync", west, dec, async: true, t => t.SlewToTargetAsync(), null);
        }
        if (ctx.Target.Kind == TargetKind.Alpaca)
        {
            // "Deprecated synchronous methods ... must not be implemented by Alpaca drivers."
            await DeprecatedSyncSlewAsync(ctx, "SlewToCoordinates", t => t.SlewToCoordinates(east, dec));
            await DeprecatedSyncSlewAsync(ctx, "SlewToTarget", t => { t.TargetRightAscension = east; t.TargetDeclination = dec; t.SlewToTarget(); });
            await DeprecatedSyncSlewAsync(ctx, "SlewToAltAz", t => t.SlewToAltAz(c.SiteLatitude is < 0 ? 30 : 150, 50));
        }
        else if (c.CanSlew)
        {
            await SlewAsync(ctx, "SlewToCoordinates", "SlewToCoordinates", east, dec, async: false,
                t => t.SlewToCoordinates(east, dec), null);
            var target = await ctx.TryAsync(t => { t.TargetRightAscension = west; t.TargetDeclination = dec; });
            if (target.Ok)
                await SlewAsync(ctx, "SlewToTarget", "SlewToTarget", west, dec, async: false, t => t.SlewToTarget(), null);
        }

        if (c.CanSlewAsync) await SettleTimeAsync(ctx);
        if (c.CanSlewAsync && gem && Math.Abs(c.SiteLatitude ?? 30) > 10) await FarSidesAsync(ctx, lst.Value);
        if (c.CanSlewAsync && gem) await FlipsAsync(ctx);

        // Invalid coordinates must be refused before the mount moves.
        var member = c.CanSlewAsync ? "SlewToCoordinatesAsync" : "SlewToCoordinates";
        Action<ITelescopeV4, double, double> slew = c.CanSlewAsync ? (t, r, d) => t.SlewToCoordinatesAsync(r, d) : (t, r, d) => t.SlewToCoordinates(r, d);
        foreach (var (id, r, d, rule) in new[]
                 {
                     ("InvalidRaLow", -1.0, dec, "right ascension is 0 up to 24 hours"),
                     ("InvalidRaHigh", 25.0, dec, "right ascension is 0 up to 24 hours"),
                     ("InvalidDecLow", west, -91.0, "declination is -90 to 90 degrees"),
                     ("InvalidDecHigh", west, 91.0, "declination is -90 to 90 degrees")
                 })
        {
            var result = await ctx.ExpectErrorAsync($"{member}.{id}", member, $"{member}({CheckContext.Show(r)}, {CheckContext.Show(d)})",
                t => slew(t, r, d), AscomErrorKind.InvalidValue, rule, timeout: ctx.SlewTimeout);
            if (result.Ok) await StopAsync(ctx);
        }

        // An equatorial slew needs tracking on.
        if (c.CanSetTracking)
        {
            var off = await ctx.TryAsync(t => t.Tracking = false);
            if (off.Ok)
            {
                var result = await ctx.ExpectErrorAsync($"{member}.NotTracking", member, $"{member} with Tracking false",
                    t => slew(t, west, dec), AscomErrorKind.InvalidOperation, "Tracking is false", timeout: ctx.SlewTimeout);
                if (result.Ok) await StopAsync(ctx);
                await ctx.TryAsync(t => t.Tracking = true);
            }
        }
    }

    /// <summary>
    /// ConformU's wider side-of-pier model: nine hours either side of the meridian, high in the sky (the mount has to
    /// point through the pole or flip). Recorded for information: what is right there depends on the mount's limits.
    /// </summary>
    private static async Task FarSidesAsync(CheckContext ctx, double lst)
    {
        var dec = Math.Sign(ctx.Caps.SiteLatitude ?? 30) * 75.0;
        var sides = new List<string>();
        foreach (var ha in new[] { -9.0, 9.0 })
        {
            var ra = Values.Wrap24(lst - ha);
            if (!(await ctx.TryAsync(t => t.SlewToCoordinatesAsync(ra, dec))).Ok || !await ctx.WaitForSlewEndAsync()) return;
            var side = await ctx.TryAsync(t => t.SideOfPier);
            sides.Add($"HA {ha:+0;-0} h: {(side.Ok ? side.Value.ToString() : side.ErrorText)}");
        }
        ctx.Report("SideOfPier.FarHourAngles", "SideOfPier", Outcome.Info,
            $"SideOfPier at Dec {dec:0}°, {string.Join("; ", sides)}.",
            actual: string.Join("; ", sides));
    }

    /// <summary>
    /// Meridian flips around the run's flip angle (<see cref="RunOptions.FlipAngle"/>, degrees from the meridian).
    /// Past it on either side only one side of the pier reaches the sky, so a goto across the meridian must flip the
    /// mount (east of the meridian ThroughThePole, west of it Normal). Inside it either side works, so the flip is
    /// made with the SideOfPier command, and the mount must keep pointing at the same place.
    /// </summary>
    private static async Task FlipsAsync(CheckContext ctx)
    {
        var dec = MethodChecks.SafeDeclination(ctx);
        var angle = Math.Clamp(ctx.Options.FlipAngle, 0, 90);

        // Gotos 5° past the flip angle, east to west and back.
        var past = (angle + 5) / 15.0; // hours
        foreach (var (id, fromHa, toHa, expect) in new[]
                 {
                     ("Flip.Goto.EastToWest", -past, past, PointingState.Normal),
                     ("Flip.Goto.WestToEast", past, -past, PointingState.ThroughThePole)
                 })
        {
            var start = expect == PointingState.Normal ? PointingState.ThroughThePole : PointingState.Normal;
            var fromSide = fromHa < 0 ? "east" : "west";
            var toSide = toHa < 0 ? "east" : "west";
            var lst = await ctx.TryAsync(t => t.SiderealTime);
            if (!lst.Ok) return;
            var from = Values.Wrap24(lst.Value - fromHa);
            if (!(await ctx.TryAsync(t => t.SlewToCoordinatesAsync(from, dec), ctx.SlewTimeout)).Ok || !await ctx.WaitForSlewEndAsync())
            {
                await StopAsync(ctx);
                ctx.Report($"{id}.Flipped", "SlewToCoordinatesAsync", Outcome.Skipped,
                    $"The mount couldn't be sent {angle + 5}° {fromSide} of the meridian first, so the flip wasn't checked.");
                continue;
            }
            var before = await ctx.TryAsync(t => t.SideOfPier);
            if (!before.Ok || before.Value == PointingState.Unknown)
            {
                ctx.Report($"{id}.Flipped", "SideOfPier", Outcome.Skipped, "SideOfPier can't be read (or is pierUnknown), so a flip can't be seen.");
                continue;
            }

            // The goto itself (returns, position, targets, DestinationSideOfPier) ...
            var to = Values.Wrap24(lst.Value - toHa);
            await SlewAsync(ctx, id, "SlewToCoordinatesAsync", to, dec, async: true, t => t.SlewToCoordinatesAsync(to, dec), expectSide: null);

            // ... then whether it flipped.
            var after = await ctx.TryAsync(t => t.SideOfPier);
            if (!after.Ok || (await ctx.TryAsync(t => t.Slewing)) is { Ok: true, Value: true }) continue;
            var flipped = before.Value == start && after.Value == expect;
            ctx.Report($"{id}.Flipped", "SideOfPier", flipped ? Outcome.Pass : Outcome.Fail,
                flipped
                    ? $"A goto from {angle + 5}° {fromSide} to {angle + 5}° {toSide} of the meridian (past the {angle}° flip angle) flipped the mount from {before.Value} to {after.Value}."
                    : before.Value != start
                        ? $"{angle + 5}° {fromSide} of the meridian, past the {angle}° flip angle, the mount was on {before.Value}, not {start}."
                        : $"A goto from {angle + 5}° {fromSide} to {angle + 5}° {toSide} of the meridian (past the {angle}° flip angle) didn't flip the mount: it stayed on {after.Value}.",
                expected: $"{start} → {expect}", actual: $"{before.Value} → {after.Value}",
                fix: flipped ? null
                    : $"Past the flip angle a German mount can only reach the sky from one side: east of the meridian ThroughThePole " +
                      "(pierWest), west of it Normal (pierEast). A goto across the meridian there must flip the mount. Check the flip " +
                      "decision against the flip angle (the meridian limit), the hour angle's sign, and that the axis positions are " +
                      "recalculated for the new side.",
                sourcePatterns: ["SideOfPier", "DestinationSideOfPier", "Flip", "Meridian"]);
        }

        // SideOfPier flips halfway inside the flip angle, west then east of the meridian.
        if (!ctx.Caps.CanSetPierSide)
        {
            ctx.Report("Flip.SideOfPier", "SideOfPier", Outcome.Skipped, "CanSetPierSide is false, so flipping with the SideOfPier command wasn't checked.");
            return;
        }
        if (angle < 1)
        {
            ctx.Report("Flip.SideOfPier", "SideOfPier", Outcome.Skipped,
                "The flip angle is 0°: there's no zone where either side reaches the sky, so the SideOfPier command wasn't checked.");
            return;
        }
        foreach (var (id, ha) in new[] { ("Flip.SideOfPier.West", angle / 2.0 / 15), ("Flip.SideOfPier.East", -angle / 2.0 / 15) })
        {
            var lst = await ctx.TryAsync(t => t.SiderealTime);
            if (!lst.Ok) return;
            var ra = Values.Wrap24(lst.Value - ha);
            var where = $"{angle / 2.0:0.#}° {(ha > 0 ? "west" : "east")} of the meridian (inside the {angle}° flip angle)";
            if (!(await ctx.TryAsync(t => t.SlewToCoordinatesAsync(ra, dec), ctx.SlewTimeout)).Ok || !await ctx.WaitForSlewEndAsync())
            {
                await StopAsync(ctx);
                ctx.Report(id, "SideOfPier", Outcome.Skipped, $"The mount couldn't be sent {where}, so the SideOfPier flip wasn't checked.");
                continue;
            }
            var before = await ctx.TryAsync(t => t.SideOfPier);
            if (!before.Ok || before.Value == PointingState.Unknown)
            {
                ctx.Report(id, "SideOfPier", Outcome.Skipped, "SideOfPier can't be read (or is pierUnknown), so a flip can't be seen.");
                continue;
            }
            var other = before.Value == PointingState.Normal ? PointingState.ThroughThePole : PointingState.Normal;

            var flip = await ctx.TryAsync(t => t.SideOfPier = other, ctx.SlewTimeout);
            await ctx.WaitForSlewEndAsync();
            var after = await ctx.TryAsync(t => t.SideOfPier);
            var pos = await PositionAsync(ctx);
            var sideOk = flip.Ok && after is { Ok: true } && after.Value == other;
            var sep = pos is { } p ? Values.Separation(p.Ra, p.Dec, ra, dec) : (double?)null;
            var outcome = !sideOk || sep > PositionFail ? Outcome.Fail : sep > PositionOk ? Outcome.Info : Outcome.Pass;
            ctx.Report(id, "SideOfPier", outcome,
                !sideOk ? $"SideOfPier = {other} {where} didn't flip the mount (it was on {before.Value})."
                    : sep is null ? $"SideOfPier = {other} flipped the mount {where} (its position couldn't be read)."
                    : $"SideOfPier = {other} flipped the mount {where} from {before.Value}, and it ended {sep * 3600:0.0}\" from where it pointed.",
                expected: $"{other}, still at RA {CheckContext.Show(ra)} h, Dec {CheckContext.Show(dec)}°",
                actual: !flip.Ok ? flip.ErrorText
                    : $"{(after.Ok ? after.Value.ToString() : after.ErrorText)}" + (pos is { } q ? $", RA {CheckContext.Show(q.Ra)} h, Dec {CheckContext.Show(q.Dec)}°" : ""),
                ms: flip.Ms,
                fix: outcome == Outcome.Pass ? null
                    : "CanSetPierSide is true, so inside the flip angle setting SideOfPier must move the mount to that side while " +
                      "pointing at the same RA and Dec: the same target reached through the other side of the pier.",
                sourcePatterns: ["SideOfPier", "Flip"]);
            await ctx.TryAsync(t => t.SideOfPier = before.Value, ctx.SlewTimeout);
            await ctx.WaitForSlewEndAsync();
        }
    }

    /// <summary>
    /// SlewSettleTime keeps Slewing true after the mount stops: a short slew with a 3 s settle time must take at least
    /// about 2.5 s longer than the same slew with none.
    /// </summary>
    private static async Task SettleTimeAsync(CheckContext ctx)
    {
        var original = await ctx.TryAsync(t => t.SlewSettleTime);
        if (!original.Ok || !(await ctx.TryAsync(t => t.SlewSettleTime = 0)).Ok)
        {
            ctx.Report("SlewSettleTime.Effect", "SlewSettleTime", Outcome.Skipped, "SlewSettleTime can't be set, so its effect wasn't checked.");
            return;
        }
        try
        {
            async Task<double?> TimeShortSlewAsync()
            {
                if (await PositionAsync(ctx) is not { } here) return null;
                var dec = Math.Clamp(here.Dec + (here.Dec > 0 ? -1 : 1), -89, 89);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                if (!(await ctx.TryAsync(t => t.SlewToCoordinatesAsync(here.Ra, dec))).Ok) return null;
                return await ctx.WaitUntilAsync(t => !t.Slewing, ctx.SlewTimeout, 50) ? watch.Elapsed.TotalSeconds : null;
            }
            var without = await TimeShortSlewAsync();
            await ctx.TryAsync(t => t.SlewSettleTime = 3);
            var with = await TimeShortSlewAsync();
            if (without is null || with is null) return;
            var extra = with.Value - without.Value;
            ctx.Report("SlewSettleTime.Effect", "SlewSettleTime", extra >= 2.5 ? Outcome.Pass : Outcome.Fail,
                $"With SlewSettleTime 3 the slew reported Slewing {extra:0.0} s longer than with 0.",
                expected: "about 3 s longer", actual: $"{with:0.0} s vs {without:0.0} s",
                fix: "SlewSettleTime: Slewing must stay true for that many seconds after the mount stops, so clients don't start " +
                     "an exposure while it is still settling. Add the settle delay before clearing Slewing.");
        }
        finally
        {
            await ctx.TryAsync(t => t.SlewSettleTime = original.Value);
        }
    }

    /// <summary>
    /// An Alpaca driver must not implement a deprecated synchronous slew: it should raise MethodNotImplementedException.
    /// Anything else (it slews, or refuses for another reason) means it is implemented.
    /// </summary>
    private static async Task DeprecatedSyncSlewAsync(CheckContext ctx, string member, Action<ITelescopeV4> call)
    {
        var result = await ctx.TryAsync(call, ctx.SlewTimeout);
        if (result.Kind != AscomErrorKind.NotImplemented) await StopAsync(ctx);
        ctx.Report($"{member}.Deprecated", member, result.Kind == AscomErrorKind.NotImplemented ? Outcome.Pass : Outcome.Warning,
            result.Kind == AscomErrorKind.NotImplemented
                ? $"{member} (deprecated synchronous slew) isn't implemented over Alpaca, as required."
                : $"{member} (deprecated synchronous slew) is implemented over Alpaca.",
            expected: "MethodNotImplementedException (0x400)", actual: result.Ok ? "the mount slewed" : result.ErrorText, ms: result.Ms,
            fix: $"ITelescopeV4: the deprecated synchronous slews \"must not be implemented by Alpaca drivers\". Make {member} " +
                 "throw MethodNotImplementedException over Alpaca; clients use the Async variant.");
    }

    /// <summary>
    /// Runs a slew and checks it: an async one returns at once and reports Slewing until it ends; a synchronous one
    /// returns only when it has ended. Then the position, the targets and (on a GEM) the side of pier are checked.
    /// </summary>
    private static async Task SlewAsync(CheckContext ctx, string id, string member, double ra, double dec, bool async,
        Action<ITelescopeV4> slew, PointingState? expectSide)
    {
        PointingState? predicted = null;
        if (ctx.Fact("DestinationSideOfPier"))
        {
            var p = await ctx.TryAsync(t => t.DestinationSideOfPier(ra, dec));
            if (p.Ok) predicted = p.Value;
        }

        var call = await ctx.TryAsync(slew, ctx.SlewTimeout);
        if (!call.Ok)
        {
            ctx.Report(id, member, Outcome.Fail, $"{member} raised an error.", actual: call.ErrorText, ms: call.Ms,
                fix: $"{member} to RA {CheckContext.Hours(ra)}, Dec {dec:0}° (an hour from the meridian, high in the sky) should work.");
            await StopAsync(ctx);
            return;
        }
        var slewingAfter = await ctx.TryAsync(t => t.Slewing);
        if (async)
        {
            if (slewingAfter is { Ok: true, Value: false } && call.Ms < 5000)
                ctx.Report($"{id}.SlewingOnReturn", member, Outcome.Fail, $"Slewing was false straight after {member} returned.",
                    expected: "Slewing true from the moment the call returns until the slew has finished", actual: "Slewing false",
                    fix: "\"Slewing must be True immediately upon returning from any of these calls\": set it before returning, " +
                         "not when the mount first reports movement.", sourceMember: "Slewing");
            if (call.Ms > 5000)
                ctx.Report($"{id}.Returns", member, Outcome.Fail, $"{member} took {call.Ms / 1000:0.0} s to return.",
                    expected: "returns at once, while the mount slews", actual: $"{call.Ms:0} ms",
                    fix: $"{member} must start the slew and return straight away; clients follow it through Slewing.");
        }
        else if (slewingAfter is { Ok: true, Value: true })
        {
            ctx.Report($"{id}.Returns", member, Outcome.Fail, $"{member} returned while the mount was still slewing.",
                expected: "Slewing false when the synchronous slew returns", actual: "Slewing true",
                fix: $"{member} is the synchronous slew: it must not return until the slew (and settle time) has finished. " +
                     "Use the Async variant for a slew that returns at once.");
        }

        var ended = await ctx.WaitForSlewEndAsync();
        if (!ended)
        {
            ctx.Report(id, member, Outcome.Fail, $"Slewing was still true {ctx.Options.SlewTimeoutSeconds} s after {member}.",
                expected: "Slewing false when the slew ends", actual: "Slewing true",
                fix: "Make sure Slewing goes back to false when the slew ends (and after any settle time).");
            await StopAsync(ctx);
            return;
        }

        var pos = await PositionAsync(ctx);
        if (pos is null) return;
        var sep = Values.Separation(pos.Value.Ra, pos.Value.Dec, ra, dec);
        ctx.Report(id, member, sep > PositionFail ? Outcome.Fail : sep > PositionOk ? Outcome.Info : Outcome.Pass,
            $"{member}: ended {sep * 3600:0.0}\" from the requested position.",
            expected: $"RA {CheckContext.Show(ra)} h, Dec {CheckContext.Show(dec)}°",
            actual: $"RA {CheckContext.Show(pos.Value.Ra)} h, Dec {CheckContext.Show(pos.Value.Dec)}°", ms: call.Ms,
            fix: "The mount should end within a few arc-minutes of where it was sent. Check the coordinate conversion " +
                 "(EquatorialSystem: J2000 vs topocentric), refraction, and that RA isn't confused with hour angle.");

        // A slew sets the targets to where it went.
        var tra = await ctx.TryAsync(t => t.TargetRightAscension);
        var tdec = await ctx.TryAsync(t => t.TargetDeclination);
        if (tra.Ok && tdec.Ok && (Math.Abs(Values.HoursDiff(tra.Value, ra)) > 1e-4 || Math.Abs(tdec.Value - dec) > 1e-3))
            ctx.Report($"{id}.Targets", member, Outcome.Fail, $"After {member}, the targets aren't the slew's coordinates.",
                expected: $"{CheckContext.Show(ra)} h, {CheckContext.Show(dec)}°", actual: $"{CheckContext.Show(tra.Value)} h, {CheckContext.Show(tdec.Value)}°",
                fix: "SlewToCoordinates(Async) must set TargetRightAscension and TargetDeclination to the coordinates given.");

        if (expectSide is not null || predicted is not null)
        {
            var side = await ctx.TryAsync(t => t.SideOfPier);
            if (side.Ok)
            {
                if (predicted is { } pr && side.Value != pr)
                    ctx.Report($"{id}.DestinationSideOfPier", "DestinationSideOfPier", Outcome.Fail,
                        $"DestinationSideOfPier said {pr}, but the mount ended on {side.Value}.", expected: pr.ToString(), actual: side.Value.ToString(),
                        fix: "DestinationSideOfPier must predict the side of pier the slew really ends on: use the same " +
                             "calculation (and flip limits) as the slew itself.");
                if (expectSide is { } ex)
                {
                    var reversed = side.Value != ex && side.Value != PointingState.Unknown;
                    ctx.Report($"{id}.SideOfPier", "SideOfPier", side.Value == ex ? Outcome.Pass : reversed ? Outcome.Fail : Outcome.Warning,
                        side.Value == ex ? $"SideOfPier after the slew: {side.Value}, as expected."
                            : reversed ? $"SideOfPier after the slew is {side.Value}: the pointing state is reversed."
                            : "SideOfPier after the slew is pierUnknown.",
                        expected: ex.ToString(), actual: side.Value.ToString(),
                        fix: "SideOfPier is the pointing state, not the physical side: three hours west of the meridian a German mount " +
                             "is pierEast (Normal), three hours east pierWest (ThroughThePole). ConformU reports the reverse as " +
                             "\"SideOfPier reports physical pier side rather than pointing state\". Check the sign of the hour angle.");
                }
            }
        }
    }

    private static async Task AbortAsync(CheckContext ctx)
    {
        var idle = await ctx.TryAsync(t => t.AbortSlew());
        ctx.Report("AbortSlew.Idle", "AbortSlew", idle.Ok ? Outcome.Pass : Outcome.Fail,
            idle.Ok ? "AbortSlew while not slewing: no error." : "AbortSlew while not slewing raised an error.",
            actual: idle.ErrorText, ms: idle.Ms, fix: "AbortSlew when nothing is moving must do nothing, without an error.");

        if (!ctx.Caps.CanSlewAsync) return;
        var lst = await ctx.TryAsync(t => t.SiderealTime);
        var pos = await PositionAsync(ctx);
        if (!lst.Ok || pos is null) return;
        // Somewhere far enough away that the slew is still going when it is stopped.
        var far = Values.Wrap24(lst.Value + (Values.HoursDiff(pos.Value.Ra, lst.Value) > 0 ? -2 : 2));
        var dec = MethodChecks.SafeDeclination(ctx);
        var start = await ctx.TryAsync(t => t.SlewToCoordinatesAsync(far, dec));
        if (!start.Ok) return;
        // A real mount may take a moment (a serial round trip) to report the slew: wait for it, up to 3 s.
        var wasSlewing = await ctx.WaitUntilAsync(t => t.Slewing, TimeSpan.FromSeconds(3), 100);
        var abort = await ctx.TryAsync(t => t.AbortSlew(), ctx.SlewTimeout);
        var stopped = await ctx.WaitUntilAsync(t => !t.Slewing, TimeSpan.FromSeconds(15));
        if (!wasSlewing)
        {
            ctx.Report("AbortSlew.DuringSlew", "AbortSlew", Outcome.Info, "The slew finished too quickly to stop it, so AbortSlew during a slew wasn't checked.");
            return;
        }
        ctx.Report("AbortSlew.DuringSlew", "AbortSlew", abort.Ok && stopped ? Outcome.Pass : Outcome.Fail,
            abort.Ok && stopped ? "AbortSlew stopped a slew." : abort.Ok ? "Slewing stayed true for 15 s after AbortSlew." : "AbortSlew during a slew raised an error.",
            actual: abort.ErrorText, ms: abort.Ms,
            fix: "AbortSlew must stop the slew straight away and Slewing must then go false.");
        await ctx.WaitForSlewEndAsync();
    }

    // ---------- Syncs ----------

    private static async Task SyncsAsync(CheckContext ctx)
    {
        if (!ctx.Caps.CanSync) return;
        var pos = await PositionAsync(ctx);
        if (pos is null) return;
        var (ra, dec) = pos.Value;

        // As ConformU: sync to a position a degree away (so a sync that does nothing shows), check the position and
        // the targets, then sync back by the same amount so the pointing model is left as it was.
        var (syncRa, syncDec) = SyncOffset(ra, dec);
        var sync = await ctx.TryAsync(t => t.SyncToCoordinates(syncRa, syncDec));
        await CheckPositionAfterSync(ctx, "SyncToCoordinates", sync, syncRa, syncDec, ra, dec);
        if (sync.Ok)
        {
            await CheckTargetsAfterSync(ctx, "SyncToCoordinates", syncRa, syncDec);
            await UndoSyncAsync(ctx, "SyncToCoordinates", ra - syncRa, dec - syncDec);
        }

        if (await PositionAsync(ctx) is { } here)
        {
            var (targetRa, targetDec) = SyncOffset(here.Ra, here.Dec);
            var target = await ctx.TryAsync(t => { t.TargetRightAscension = targetRa; t.TargetDeclination = targetDec; });
            if (target.Ok)
            {
                var toTarget = await ctx.TryAsync(t => t.SyncToTarget());
                await CheckPositionAfterSync(ctx, "SyncToTarget", toTarget, targetRa, targetDec, here.Ra, here.Dec);
                if (toTarget.Ok) await UndoSyncAsync(ctx, "SyncToTarget", here.Ra - targetRa, here.Dec - targetDec);
            }
        }

        await ctx.ExpectErrorAsync("SyncToCoordinates.InvalidRa", "SyncToCoordinates", "SyncToCoordinates(25, dec)",
            t => t.SyncToCoordinates(25, dec), AscomErrorKind.InvalidValue, "right ascension is 0 up to 24 hours");
        await ctx.ExpectErrorAsync("SyncToCoordinates.InvalidDec", "SyncToCoordinates", "SyncToCoordinates(ra, 91)",
            t => t.SyncToCoordinates(ra, 91), AscomErrorKind.InvalidValue, "declination is -90 to 90 degrees");

        // Not checked: a sync with Tracking false. The documentation only says it "may throw DriverException", and
        // ConformU doesn't test it.
    }

    /// <summary>How far a sync check tells the mount it is out (ConformU's SYNC_SIMULATED_ERROR), in degrees.</summary>
    private const double SyncError = 1.0;

    /// <summary>A position <see cref="SyncError"/> west and south of (<paramref name="ra"/>, <paramref name="dec"/>), clear of the poles.</summary>
    private static (double Ra, double Dec) SyncOffset(double ra, double dec)
        => (Values.Wrap24(ra - SyncError / 15), dec - SyncError < -89 ? dec + SyncError : dec - SyncError);

    private static async Task CheckPositionAfterSync(CheckContext ctx, string member, Call<bool> sync, double ra, double dec,
        double beforeRa, double beforeDec)
    {
        if (!sync.Ok)
        {
            ctx.Report(member, member, Outcome.Fail, $"{member} raised an error.", actual: sync.ErrorText, ms: sync.Ms,
                fix: $"CanSync is true, so {member} to a position a degree from where the mount points must work.");
            return;
        }
        var after = await PositionAsync(ctx);
        if (after is null) return;
        var sep = Values.Separation(after.Value.Ra, after.Value.Dec, ra, dec);
        // Still reading (about) the position before the sync: the sync was ignored.
        var ignored = sep > PositionOk && Values.Separation(after.Value.Ra, after.Value.Dec, beforeRa, beforeDec) < sep;
        ctx.Report(member, member, ignored || sep > PositionFail ? Outcome.Fail : sep > PositionOk ? Outcome.Info : Outcome.Pass,
            ignored ? $"{member} didn't change the position: it still reads where the mount was before the sync."
                : $"{member}: position afterwards is {sep * 3600:0.0}\" from the sync coordinates.",
            expected: $"RA {CheckContext.Show(ra)} h, Dec {CheckContext.Show(dec)}°",
            actual: $"RA {CheckContext.Show(after.Value.Ra)} h, Dec {CheckContext.Show(after.Value.Dec)}°", ms: sync.Ms,
            fix: $"After {member}, RightAscension and Declination must read the coordinates synced to: the sync tells the " +
                 "mount where it really points, so the position (or the pointing model's offset) must change to match.");
    }

    /// <summary>As ConformU: a sync sets the targets to the coordinates synced to.</summary>
    private static async Task CheckTargetsAfterSync(CheckContext ctx, string member, double ra, double dec)
    {
        var tra = await ctx.TryAsync(t => t.TargetRightAscension);
        var tdec = await ctx.TryAsync(t => t.TargetDeclination);
        var ok = tra.Ok && tdec.Ok && Math.Abs(Values.HoursDiff(tra.Value, ra)) * 15 * 3600 <= 10 && Math.Abs(tdec.Value - dec) * 3600 <= 10;
        ctx.Report($"{member}.Targets", member, ok ? Outcome.Pass : Outcome.Fail,
            ok ? $"After {member}, the targets are the coordinates synced to." : $"After {member}, the targets aren't the coordinates synced to.",
            expected: $"{CheckContext.Show(ra)} h, {CheckContext.Show(dec)}°",
            actual: !tra.Ok ? tra.ErrorText : !tdec.Ok ? tdec.ErrorText : $"{CheckContext.Show(tra.Value)} h, {CheckContext.Show(tdec.Value)}°",
            fix: $"{member} must set TargetRightAscension and TargetDeclination to the coordinates given (ConformU reports this as an issue).");
    }

    /// <summary>Syncs back by the opposite of the test sync, so the mount's pointing is as it was before it.</summary>
    private static async Task UndoSyncAsync(CheckContext ctx, string member, double dRa, double dDec)
    {
        if (await PositionAsync(ctx) is not { } now) return;
        var undo = await ctx.TryAsync(t => t.SyncToCoordinates(Values.Wrap24(now.Ra + dRa), Math.Clamp(now.Dec + dDec, -90, 90)));
        if (!undo.Ok)
            ctx.Report($"{member}.Restore", "SyncToCoordinates", Outcome.Error,
                $"Couldn't sync back after the {member} check: the mount's pointing may be {SyncError}° out.", actual: undo.ErrorText,
                fix: "Sync the mount on a known star (or restart the simulator) before using it.");
    }

    // ---------- Alt/az ----------

    private static async Task AltAzAsync(CheckContext ctx)
    {
        var c = ctx.Caps;
        if (!c.CanSlewAltAz && !c.CanSlewAltAzAsync && !c.CanSyncAltAz) return;
        var lat = c.SiteLatitude ?? 30;
        double alt = 50, az = lat >= 0 ? 150 : 30;

        if (c.CanSetTracking && (await ctx.TryAsync(t => t.Tracking = true)).Ok)
        {
            if (c.CanSlewAltAzAsync)
            {
                var r = await ctx.ExpectErrorAsync("SlewToAltAzAsync.Tracking", "SlewToAltAzAsync", "SlewToAltAzAsync with Tracking true",
                    t => t.SlewToAltAzAsync(az, alt), AscomErrorKind.InvalidOperation, "Tracking is true (alt/az slews need tracking off)");
                if (r.Ok) await StopAsync(ctx);
            }
        }
        if (c.CanSetTracking) await ctx.TryAsync(t => t.Tracking = false);
        var trackingOff = (await ctx.TryAsync(t => t.Tracking)) is { Ok: true, Value: false };
        if (!trackingOff)
        {
            ctx.Report("AltAz.Prepare", "Tracking", Outcome.Skipped, "Alt/az slews need tracking off, and it couldn't be turned off.");
            return;
        }
        try
        {
            if (c.CanSlewAltAzAsync)
                await AltAzSlewAsync(ctx, "SlewToAltAzAsync", alt, az, t => t.SlewToAltAzAsync(az, alt), async: true);
            if (c.CanSlewAltAz && ctx.Target.Kind == TargetKind.Com)
                await AltAzSlewAsync(ctx, "SlewToAltAz", alt + 5, az + 10, t => t.SlewToAltAz(az + 10, alt + 5), async: false);

            var member = c.CanSlewAltAzAsync ? "SlewToAltAzAsync" : c.CanSlewAltAz ? "SlewToAltAz" : null;
            if (member is not null)
            {
                Action<ITelescopeV4, double, double> slew = c.CanSlewAltAzAsync ? (t, a, z) => t.SlewToAltAzAsync(z, a) : (t, a, z) => t.SlewToAltAz(z, a);
                foreach (var (id, a, z, rule) in new[]
                         {
                             // ConformU's bad values: altitude -100 / 100, azimuth -10 / 370.
                             ("InvalidAltLow", -100.0, az, "an altitude of -100 degrees is invalid"),
                             ("InvalidAltHigh", 100.0, az, "an altitude of 100 degrees is invalid"),
                             ("InvalidAzLow", alt, -10.0, "an azimuth of -10 degrees is invalid"),
                             ("InvalidAzHigh", alt, 370.0, "an azimuth of 370 degrees is invalid")
                         })
                {
                    var r = await ctx.ExpectErrorAsync($"{member}.{id}", member, $"{member}(az {z}, alt {a})", t => slew(t, a, z),
                        AscomErrorKind.InvalidValue, rule, timeout: ctx.SlewTimeout);
                    if (r.Ok) await StopAsync(ctx);
                }
            }

            if (c.CanSyncAltAz)
            {
                var here = await AltAzPositionAsync(ctx);
                if (here is { } h)
                {
                    // As ConformU: sync a degree off in altitude and azimuth, check Altitude/Azimuth read it, then sync back.
                    var (syncAlt, syncAz) = (h.Alt > 80 ? h.Alt - SyncError : h.Alt + SyncError, (h.Az + SyncError) % 360);
                    var sync = await ctx.TryAsync(t => t.SyncToAltAz(syncAz, syncAlt));
                    if (!sync.Ok)
                    {
                        ctx.Report("SyncToAltAz", "SyncToAltAz", Outcome.Fail, "SyncToAltAz raised an error.",
                            actual: sync.ErrorText, ms: sync.Ms, fix: "CanSyncAltAz is true, so SyncToAltAz (with tracking off) must work.");
                    }
                    else if (await AltAzPositionAsync(ctx) is { } after)
                    {
                        var sep = Values.AltAzSeparation(after.Alt, after.Az, syncAlt, syncAz);
                        var ignored = sep > PositionOk && Values.AltAzSeparation(after.Alt, after.Az, h.Alt, h.Az) < sep;
                        ctx.Report("SyncToAltAz", "SyncToAltAz", ignored || sep > PositionFail ? Outcome.Fail : sep > PositionOk ? Outcome.Info : Outcome.Pass,
                            ignored ? "SyncToAltAz didn't change the position: Altitude/Azimuth still read where the mount was."
                                : $"SyncToAltAz: Altitude/Azimuth afterwards are {sep * 3600:0.0}\" from the sync coordinates.",
                            expected: $"alt {CheckContext.Show(syncAlt)}°, az {CheckContext.Show(syncAz)}°",
                            actual: $"alt {CheckContext.Show(after.Alt)}°, az {CheckContext.Show(after.Az)}°", ms: sync.Ms,
                            fix: "After SyncToAltAz, Altitude and Azimuth must read the coordinates synced to (the parameters are " +
                                 "azimuth, then altitude).");
                        var undo = await ctx.TryAsync(t => t.SyncToAltAz(
                            ((after.Az + h.Az - syncAz) % 360 + 360) % 360, Math.Clamp(after.Alt + h.Alt - syncAlt, -90, 90)));
                        if (!undo.Ok)
                            ctx.Report("SyncToAltAz.Restore", "SyncToAltAz", Outcome.Error,
                                $"Couldn't sync back after the SyncToAltAz check: the mount's pointing may be {SyncError}° out.",
                                actual: undo.ErrorText, fix: "Sync the mount on a known star (or restart the simulator) before using it.");
                    }
                    else
                    {
                        ctx.Report("SyncToAltAz", "SyncToAltAz", Outcome.Pass, "SyncToAltAz worked (Altitude/Azimuth can't be read, so the position wasn't checked).", ms: sync.Ms);
                    }
                    await ctx.ExpectErrorAsync("SyncToAltAz.InvalidAlt", "SyncToAltAz", "SyncToAltAz(az, 100)",
                        t => t.SyncToAltAz(h.Az, 100), AscomErrorKind.InvalidValue, "an altitude of 100 degrees is invalid");
                }
            }
        }
        finally
        {
            if (c.CanSetTracking) await ctx.TryAsync(t => t.Tracking = true);
        }
    }

    private static async Task AltAzSlewAsync(CheckContext ctx, string member, double alt, double az, Action<ITelescopeV4> slew, bool async)
    {
        var call = await ctx.TryAsync(slew, ctx.SlewTimeout);
        if (!call.Ok)
        {
            ctx.Report(member, member, Outcome.Fail, $"{member} raised an error.", actual: call.ErrorText, ms: call.Ms,
                fix: $"{member} to altitude {alt}°, azimuth {az}° with tracking off should work.");
            await StopAsync(ctx);
            return;
        }
        if (async && call.Ms > 5000)
            ctx.Report($"{member}.Returns", member, Outcome.Fail, $"{member} took {call.Ms / 1000:0.0} s to return.",
                expected: "returns at once", fix: $"{member} must start the slew and return straight away.");
        if (!async && (await ctx.TryAsync(t => t.Slewing)) is { Ok: true, Value: true })
            ctx.Report($"{member}.Returns", member, Outcome.Fail, $"{member} returned while still slewing.",
                fix: $"{member} is synchronous: it must return only when the slew has ended.");
        await ctx.WaitForSlewEndAsync();
        var pos = await AltAzPositionAsync(ctx);
        if (pos is null)
        {
            ctx.Report(member, member, Outcome.Pass, $"{member} finished (Altitude/Azimuth aren't implemented, so the position wasn't checked).");
            return;
        }
        var sep = Values.AltAzSeparation(pos.Value.Alt, pos.Value.Az, alt, az);
        ctx.Report(member, member, sep > PositionFail ? Outcome.Fail : sep > PositionOk ? Outcome.Info : Outcome.Pass,
            $"{member}: ended {sep * 60:0.0}' from the requested position.",
            expected: $"alt {alt}°, az {az}°", actual: $"alt {CheckContext.Show(pos.Value.Alt)}°, az {CheckContext.Show(pos.Value.Az)}°", ms: call.Ms,
            fix: "Check the alt/az conversion: azimuth is measured from north through east, and the method's parameters are (azimuth, altitude).");
    }

    // ---------- MoveAxis ----------

    private static async Task MoveAxisAsync(CheckContext ctx)
    {
        for (var i = 0; i < 3; i++)
        {
            if (!ctx.Caps.CanMoveAxis[i]) continue;
            var axis = (TelescopeAxis)i;
            var rates = ctx.Facts.GetValueOrDefault($"AxisRates.{axis}") as List<(double Minimum, double Maximum)>;
            if (rates is not { Count: > 0 }) continue;
            var max = rates.Max(r => r.Maximum);
            var range = rates.OrderBy(r => r.Maximum).First(r => r.Maximum > 0 || r.Minimum > 0);
            var rate = Math.Clamp(1.0, range.Minimum, range.Maximum);
            if (rate == 0) rate = range.Maximum;
            var id = $"MoveAxis.{axis}";

            var trackingBefore = await ctx.TryAsync(t => t.Tracking);
            var start = await AxisAngleAsync(ctx, axis);
            var move = await ctx.TryAsync(t => t.MoveAxis(axis, rate));
            if (!move.Ok)
            {
                ctx.Report(id, "MoveAxis", Outcome.Fail, $"MoveAxis({axis}, {CheckContext.Show(rate)}) raised an error.",
                    actual: move.ErrorText, ms: move.Ms, fix: "A rate inside AxisRates must be accepted.");
                continue;
            }
            var moving = Stopwatch.StartNew();
            await Task.Delay(1500, ctx.Token);
            var slewing = await ctx.TryAsync(t => t.Slewing);
            var forwardSeconds = moving.Elapsed.TotalSeconds;
            ctx.Report($"{id}.Slewing", "MoveAxis", slewing is { Ok: true, Value: true } ? Outcome.Pass : Outcome.Fail,
                slewing is { Ok: true, Value: true } ? "Slewing is true while an axis moves." : "Slewing is false while MoveAxis is moving an axis.",
                expected: "Slewing true", actual: slewing.Ok ? slewing.Value.ToString() : slewing.ErrorText,
                fix: "Slewing must be true while any axis is moving under MoveAxis, so clients know the mount is moving.");
            var stop = await ctx.TryAsync(t => t.MoveAxis(axis, 0));
            var stopped = await ctx.WaitUntilAsync(t => !t.Slewing, TimeSpan.FromSeconds(15));
            // Slewing false isn't proof the axis has stopped (some mounts drop it and then decelerate): wait for the
            // position itself to hold still before reading it.
            var forwardStill = await WaitForAxisStillAsync(ctx, axis);
            var end = forwardStill.Angle;
            var forward = start is { } s0 && end is { } e0 ? AxisDiff(axis, e0, s0) : (double?)null;
            ReportAxisSpeed(ctx, axis, rate, forwardSeconds, forward);
            ctx.Report($"{id}.Stop", "MoveAxis", stop.Ok && stopped ? Outcome.Pass : Outcome.Fail,
                stop.Ok && stopped ? $"MoveAxis({axis}, 0) stopped the axis." : stop.Ok ? "Slewing stayed true after MoveAxis(axis, 0)." : "MoveAxis(axis, 0) raised an error.",
                actual: stop.ErrorText, ms: stop.Ms,
                fix: "MoveAxis with rate 0 must stop the axis (and restore tracking if it was on), and Slewing must then go false.");
            var trackingAfter = await ctx.TryAsync(t => t.Tracking);
            if (trackingBefore.Ok && trackingAfter.Ok)
                ctx.Report($"{id}.TrackingRestored", "MoveAxis", trackingAfter.Value == trackingBefore.Value ? Outcome.Pass : Outcome.Fail,
                    trackingAfter.Value == trackingBefore.Value
                        ? $"After MoveAxis({axis}, 0) Tracking is {trackingAfter.Value}, as before."
                        : $"After MoveAxis({axis}, 0) Tracking changed from {trackingBefore.Value} to {trackingAfter.Value}.",
                    expected: trackingBefore.Value.ToString(), actual: trackingAfter.Value.ToString(),
                    fix: "\"A call with Rate = 0 is required to stop motion and return to the previous tracking state of that " +
                         "axis\": remember Tracking when MoveAxis starts and put it back when it stops.");

            // The other way, for as long as the forward move (a real mount needs time to ramp up), from the axis at rest.
            var backStart = end;
            var reverse = await ctx.TryAsync(t => t.MoveAxis(axis, -rate));
            var reversing = Stopwatch.StartNew();
            await Task.Delay(1500, ctx.Token);
            var backSeconds = reversing.Elapsed.TotalSeconds;
            await ctx.TryAsync(t => t.MoveAxis(axis, 0));
            ctx.Report($"{id}.Negative", "MoveAxis", reverse.Ok ? Outcome.Pass : Outcome.Warning,
                reverse.Ok ? "A negative rate (the other direction) was accepted." : "A negative rate was refused.",
                actual: reverse.ErrorText, fix: "MoveAxis takes the direction from the rate's sign: -rate must be accepted wherever +rate is.");
            await ctx.WaitUntilAsync(t => !t.Slewing, TimeSpan.FromSeconds(15));
            var backStill = await WaitForAxisStillAsync(ctx, axis);
            var backEnd = backStill.Angle;
            if (reverse.Ok && forward is { } f && backStart is { } s1 && backEnd is { } e1)
                ReportAxisDirection(ctx, axis, rate, forwardSeconds, f, backSeconds, AxisDiff(axis, e1, s1));
            ReportSlewingUntilStopped(ctx, axis, forwardStill, reverse.Ok ? backStill : null);

            foreach (var (suffix, bad) in new[] { ("TooFast", max + 1), ("TooFastNegative", -(max + 1)) })
            {
                var r = await ctx.ExpectErrorAsync($"{id}.{suffix}", "MoveAxis", $"MoveAxis({axis}, {CheckContext.Show(bad)})",
                    t => t.MoveAxis(axis, bad), AscomErrorKind.InvalidValue, "the rate is outside AxisRates");
                if (r.Ok) await ctx.TryAsync(t => t.MoveAxis(axis, 0));
            }
            await ctx.WaitUntilAsync(t => !t.Slewing, TimeSpan.FromSeconds(15));

            // A rate in a gap between two AxisRates ranges is outside them, so invalid too.
            var ordered = rates.OrderBy(r => r.Minimum).ToList();
            for (var g = 1; g < ordered.Count; g++)
            {
                if (ordered[g].Minimum - ordered[g - 1].Maximum <= 1e-6) continue;
                var gap = (ordered[g].Minimum + ordered[g - 1].Maximum) / 2;
                var r = await ctx.ExpectErrorAsync($"{id}.InGap", "MoveAxis", $"MoveAxis({axis}, {CheckContext.Show(gap)}) (between two AxisRates ranges)",
                    t => t.MoveAxis(axis, gap), AscomErrorKind.InvalidValue, "the rate lies between the AxisRates ranges");
                if (r.Ok) await ctx.TryAsync(t => t.MoveAxis(axis, 0));
                break;
            }
            await ctx.WaitUntilAsync(t => !t.Slewing, TimeSpan.FromSeconds(15));
        }

        // Both axes at once.
        if (ctx.Caps.CanMoveAxis[0] && ctx.Caps.CanMoveAxis[1]
            && ctx.Facts.GetValueOrDefault("AxisRates.Primary") is List<(double Minimum, double Maximum)> { Count: > 0 } p
            && ctx.Facts.GetValueOrDefault("AxisRates.Secondary") is List<(double Minimum, double Maximum)> { Count: > 0 } s)
        {
            var rp = Math.Clamp(1.0, p.Min(r => r.Minimum), p.Max(r => r.Maximum));
            var rs = Math.Clamp(1.0, s.Min(r => r.Minimum), s.Max(r => r.Maximum));
            var a = await ctx.TryAsync(t => t.MoveAxis(TelescopeAxis.Primary, rp));
            var b = await ctx.TryAsync(t => t.MoveAxis(TelescopeAxis.Secondary, rs));
            await Task.Delay(1000, ctx.Token);
            var slewing = await ctx.TryAsync(t => t.Slewing);
            await ctx.TryAsync(t => t.MoveAxis(TelescopeAxis.Primary, 0));
            await ctx.TryAsync(t => t.MoveAxis(TelescopeAxis.Secondary, 0));
            var stopped = await ctx.WaitUntilAsync(t => !t.Slewing, TimeSpan.FromSeconds(15));
            var ok = a.Ok && b.Ok && slewing is { Ok: true, Value: true } && stopped;
            ctx.Report("MoveAxis.BothAxes", "MoveAxis", ok ? Outcome.Pass : Outcome.Fail,
                ok ? "Both axes moved together, and both stopped." : "Moving both axes at once didn't work.",
                actual: !a.Ok ? a.ErrorText : !b.Ok ? b.ErrorText : !stopped ? "Slewing stayed true" : "Slewing false while both moved",
                fix: "MoveAxis on the primary and secondary axes at the same time must both work (hand controllers and " +
                     "planetarium programs move diagonally), with Slewing true until both have stopped.");
        }
    }

    // ---------- PulseGuide ----------

    private static async Task PulseGuideAsync(CheckContext ctx)
    {
        if (!ctx.Caps.CanPulseGuide) return;
        if (ctx.Caps.CanSetTracking) await ctx.TryAsync(t => t.Tracking = true);
        var (rateRa, rateDec) = await GuideRatesAsync(ctx);
        // Long enough for the slower axis to move about 20 arc-seconds, so the movement can be measured.
        var duration = (int)Math.Clamp(Math.Round(20.0 / 3600 / Math.Min(rateRa ?? AssumedGuideRate, rateDec ?? AssumedGuideRate) * 10) * 100, 1000, 4000);

        // The pulses are measured west of the meridian and, on a German mount, again east of it (through the pole),
        // where the Dec motor has to turn the other way for the same direction on the sky.
        var gem = ctx.Caps.AlignmentMode == AlignmentMode.GermanPolar;
        var placed = await PlaceForGuidingAsync(ctx, hourAngle: 3);
        var side = placed && gem ? nameof(PointingState.Normal) : null;

        foreach (var direction in GuideDirections)
        {
            var id = $"PulseGuide.{direction}";
            var before = await PositionAsync(ctx);
            var watch = Stopwatch.StartNew();
            var call = await ctx.TryAsync(t => t.PulseGuide(direction, duration), TimeSpan.FromSeconds(ctx.Options.CallTimeoutSeconds) + TimeSpan.FromMilliseconds(duration));
            if (!call.Ok)
            {
                ctx.Report(id, "PulseGuide", Outcome.Fail, $"PulseGuide({direction}, {duration}) raised an error.", actual: call.ErrorText,
                    ms: call.Ms, fix: "CanPulseGuide is true, so PulseGuide must work in all four directions while tracking.");
                continue;
            }
            var guiding = await ctx.TryAsync(t => t.IsPulseGuiding);
            var slewing = await ctx.TryAsync(t => t.Slewing);
            var returnedEarly = call.Ms < duration * 0.75;
            if (!returnedEarly && ctx.Identity.InterfaceVersion >= 4)
                ctx.Report($"{id}.Asynchronous", "PulseGuide", Outcome.Fail,
                    $"PulseGuide({direction}, {duration}) blocked for {call.Ms:0} ms: it is synchronous.",
                    expected: "returns as soon as the pulse has started, with IsPulseGuiding true until it ends",
                    actual: $"returned after {call.Ms:0} ms", ms: call.Ms,
                    fix: "In ITelescopeV4 PulseGuide is asynchronous (ConformU reports a synchronous pulse guide as an issue): " +
                         "start the pulse, return, and report it through IsPulseGuiding.");
            if (returnedEarly && slewing is { Ok: true, Value: true })
                ctx.Report($"{id}.Slewing", "Slewing", Outcome.Fail, "Slewing is true during a pulse guide.",
                    expected: "Slewing false", actual: "true",
                    fix: "Slewing \"must not be True\" while pulse guiding: guiding corrections aren't slews, and clients wait " +
                         "for Slewing to go false before exposing.");
            if (returnedEarly && guiding is { Ok: true, Value: false } && watch.ElapsedMilliseconds < duration * 0.75)
                ctx.Report($"{id}.IsPulseGuiding", "IsPulseGuiding", Outcome.Fail,
                    "PulseGuide returned before the pulse ended, but IsPulseGuiding is false.",
                    expected: "IsPulseGuiding true during the pulse", actual: "false",
                    fix: "When PulseGuide returns at once (asynchronous guiding), IsPulseGuiding must be true until the pulse ends.");
            var ended = await ctx.WaitUntilAsync(t => !t.IsPulseGuiding, TimeSpan.FromMilliseconds(duration + 3000), 100);
            ctx.Report(id, "PulseGuide", ended ? Outcome.Pass : Outcome.Fail,
                ended ? $"PulseGuide({direction}) {(returnedEarly ? "returned at once and" : "blocked for the pulse and")} ended in {watch.ElapsedMilliseconds} ms."
                    : "IsPulseGuiding stayed true well after the pulse should have ended.",
                actual: $"returned after {call.Ms:0} ms", ms: call.Ms,
                fix: "IsPulseGuiding must go false when the pulse's duration has passed.");
            // IsPulseGuiding false isn't proof the mount has stopped, or that the position reading has caught up.
            if (ended && before is { } b && await SettledPositionAsync(ctx) is { } a)
                ReportGuideMove(ctx, direction, side, b, a, direction is GuideDirection.North or GuideDirection.South ? rateDec : rateRa, duration);
        }

        if (side is not null && await PlaceForGuidingAsync(ctx, hourAngle: -3))
            await GuideMovesAsync(ctx, nameof(PointingState.ThroughThePole), rateRa, rateDec, duration);
        await ctx.ExpectErrorAsync("PulseGuide.InvalidDirection", "PulseGuide", "PulseGuide(9, 100)",
            t => t.PulseGuide((GuideDirection)9, 100), AscomErrorKind.InvalidValue, "9 isn't a GuideDirection");
        await ctx.ExpectErrorAsync("PulseGuide.NegativeDuration", "PulseGuide", "PulseGuide(North, -1)",
            t => t.PulseGuide(GuideDirection.North, -1), AscomErrorKind.InvalidValue, "the duration is negative");
        await ctx.WaitUntilAsync(t => !t.IsPulseGuiding, TimeSpan.FromSeconds(5), 100);

        // Not while tracking is off, and not while slewing ("InvalidOperationException if Slewing is True or Tracking is False").
        if (ctx.Caps.CanSetTracking && (await ctx.TryAsync(t => t.Tracking = false)).Ok)
        {
            await ctx.ExpectErrorAsync("PulseGuide.NotTracking", "PulseGuide", "PulseGuide with Tracking false",
                t => t.PulseGuide(GuideDirection.North, 300), AscomErrorKind.InvalidOperation, "Tracking is false");
            await ctx.TryAsync(t => t.Tracking = true);
            await ctx.WaitUntilAsync(t => !t.IsPulseGuiding, TimeSpan.FromSeconds(5), 100);
        }
        if (ctx.Caps.CanSlewAsync && (await PositionAsync(ctx)) is { } here && (await ctx.TryAsync(t => t.SiderealTime)) is { Ok: true } lst)
        {
            var far = Values.Wrap24(lst.Value + (Values.HoursDiff(here.Ra, lst.Value) > 0 ? -2 : 2));
            if ((await ctx.TryAsync(t => t.SlewToCoordinatesAsync(far, MethodChecks.SafeDeclination(ctx)))).Ok
                && (await ctx.TryAsync(t => t.Slewing)) is { Ok: true, Value: true })
            {
                await ctx.ExpectErrorAsync("PulseGuide.WhileSlewing", "PulseGuide", "PulseGuide while slewing",
                    t => t.PulseGuide(GuideDirection.North, 300), AscomErrorKind.InvalidOperation, "the mount is slewing");
            }
            else
            {
                ctx.Report("PulseGuide.WhileSlewing", "PulseGuide", Outcome.Info, "The slew finished too quickly to pulse guide during it, so this wasn't checked.");
            }
            await StopAsync(ctx);
            await ctx.WaitUntilAsync(t => !t.IsPulseGuiding, TimeSpan.FromSeconds(5), 100);
        }

        // Two directions at once (ConformU): both run, or the second is refused with InvalidOperationException.
        var east = await ctx.TryAsync(t => t.PulseGuide(GuideDirection.East, 2000));
        var north = await ctx.TryAsync(t => t.PulseGuide(GuideDirection.North, 2000));
        var together = east.Ok && (north.Ok || north.Kind == AscomErrorKind.InvalidOperation);
        ctx.Report("PulseGuide.Concurrent", "PulseGuide", together ? Outcome.Pass : Outcome.Fail,
            !together ? "Pulse guiding East and North at once failed."
                : north.Ok ? "Pulse guiding East and North at the same time: both accepted."
                : "Pulse guiding North during an East pulse: refused with InvalidOperationException (allowed).",
            expected: "both accepted, or the second refused with InvalidOperationException",
            actual: !east.Ok ? east.ErrorText : north.Ok ? "both accepted" : north.ErrorText,
            fix: "An RA and a Dec pulse at the same time should both run. A mount that can't do that must refuse the second " +
                 "with InvalidOperationException (\"Please make this limitation clear in your error message!\").");
        await ctx.WaitUntilAsync(t => !t.IsPulseGuiding, TimeSpan.FromSeconds(6), 100);
    }

    // ---------- Tracking and rate offsets ----------

    private static async Task TrackingAsync(CheckContext ctx)
    {
        if (!ctx.Caps.CanSetTracking) return;
        foreach (var value in new[] { false, true })
        {
            var set = await ctx.TryAsync(t => t.Tracking = value);
            var read = await ctx.TryAsync(t => t.Tracking);
            var ok = set.Ok && read is { Ok: true } && read.Value == value;
            ctx.Report($"Tracking.Set{value}", "Tracking", ok ? Outcome.Pass : Outcome.Fail,
                ok ? $"Tracking set to {value} and read back." : $"Tracking = {value} didn't take.",
                expected: value.ToString(), actual: set.Ok ? read.Ok ? read.Value.ToString() : read.ErrorText : set.ErrorText,
                ms: set.Ms, fix: "Setting Tracking must start or stop tracking, and Tracking must then read the new value.");
        }
    }

    private static async Task RateOffsetsAsync(CheckContext ctx)
    {
        var c = ctx.Caps;
        if (!c.CanSetRightAscensionRate && !c.CanSetDeclinationRate) return;
        var originalRate = await ctx.TryAsync(t => t.TrackingRate);
        await ctx.TryAsync(t => t.TrackingRate = DriveRate.Sidereal);
        try
        {
            foreach (var o in Offsets(c))
                await OffsetWritesAsync(ctx, o);
        }
        finally
        {
            foreach (var o in Offsets(c)) await ctx.TryAsync(t => o.Set(t, 0));
            if (originalRate.Ok) await ctx.TryAsync(t => t.TrackingRate = originalRate.Value);
        }

        // Rate offsets only apply at sidereal rate, at every other rate the mount lists.
        var others = (ctx.Facts.GetValueOrDefault("TrackingRates") as List<DriveRate>)?.Where(r => r != DriveRate.Sidereal).ToList() ?? [];
        foreach (var rate in others)
        {
            try
            {
                await OffsetsAtOtherRateAsync(ctx, rate);
            }
            finally
            {
                await ctx.TryAsync(t => t.TrackingRate = DriveRate.Sidereal);
                foreach (var o in Offsets(c)) await ctx.TryAsync(t => o.Set(t, 0));
                if (originalRate.Ok) await ctx.TryAsync(t => t.TrackingRate = originalRate.Value);
            }
        }
    }

    /// <summary>A rate offset property, and the test values for it (ConformU's 0.05 and 40 arc-seconds per second, each way).</summary>
    private sealed record RateOffset(string Member, string Capability, Func<ITelescopeV4, double> Get, Action<ITelescopeV4, double> Set,
        double Low, double High, string Unit);

    /// <summary>The rate offsets the mount says can be set. RightAscensionRate is in seconds of RA per sidereal second.</summary>
    private static IEnumerable<RateOffset> Offsets(Capabilities c)
    {
        if (c.CanSetRightAscensionRate)
            yield return new RateOffset("RightAscensionRate", "CanSetRightAscensionRate", t => t.RightAscensionRate, (t, v) => t.RightAscensionRate = v,
                0.05 / 15, 40.0 / 15, "s of RA per sidereal second");
        if (c.CanSetDeclinationRate)
            yield return new RateOffset("DeclinationRate", "CanSetDeclinationRate", t => t.DeclinationRate, (t, v) => t.DeclinationRate = v,
                0.05, 40.0, "arc-seconds per second");
    }

    /// <summary>
    /// As ConformU: with TrackingRate driveSidereal, each offset (small and large, both signs) is accepted and read back,
    /// and Slewing stays false while it is applied. A large value may be refused (the mount's range isn't known).
    /// </summary>
    private static async Task OffsetWritesAsync(CheckContext ctx, RateOffset o)
    {
        var problems = new List<string>();
        var refusedLarge = new List<string>();
        var slewingAt = new List<string>();
        var accepted = 0;
        foreach (var value in new[] { o.Low, -o.Low, o.High, -o.High })
        {
            var write = await ctx.TryAsync(t => o.Set(t, value));
            if (!write.Ok)
            {
                if (Math.Abs(value) == o.High && write.Kind is AscomErrorKind.InvalidValue or AscomErrorKind.InvalidOperation)
                    refusedLarge.Add(CheckContext.Show(value));
                else
                    problems.Add($"{CheckContext.Show(value)} was refused ({write.ErrorText})");
                continue;
            }
            accepted++;
            await Task.Delay(1000, ctx.Token); // as ConformU: let the mount settle at the new rate
            var read = await ctx.TryAsync(o.Get);
            if (!read.Ok) problems.Add($"reading back {CheckContext.Show(value)} failed ({read.ErrorText})");
            else if (Math.Abs(read.Value - value) > 1e-6) problems.Add($"{CheckContext.Show(value)} read back as {CheckContext.Show(read.Value)}");
            if ((await ctx.TryAsync(t => t.Slewing)) is { Ok: true, Value: true }) slewingAt.Add(CheckContext.Show(value));
        }
        await ctx.TryAsync(t => o.Set(t, 0));

        var note = refusedLarge.Count > 0 ? $" {string.Join(" and ", refusedLarge)} refused (allowed: may be beyond the mount's range)." : "";
        ctx.Report($"{o.Member}.Offset", o.Member, problems.Count == 0 ? Outcome.Pass : Outcome.Fail,
            problems.Count == 0 ? $"{o.Member} accepted ±{CheckContext.Show(o.Low)} and read each back.{note}"
                : $"{o.Member}: {string.Join("; ", problems)}.",
            expected: $"±{CheckContext.Show(o.Low)} and ±{CheckContext.Show(o.High)} {o.Unit} read back unchanged",
            fix: $"With TrackingRate driveSidereal, {o.Member} must accept an offset of either sign and read it back unchanged " +
                 "(RightAscensionRate is in seconds of RA per sidereal second, DeclinationRate in arc-seconds per SI second). A negative " +
                 "offset moves the other way; it isn't invalid.");
        if (accepted > 0)
            ctx.Report($"{o.Member}.Offset.Slewing", "Slewing", slewingAt.Count == 0 ? Outcome.Pass : Outcome.Fail,
                slewingAt.Count == 0 ? $"Slewing stayed false with a {o.Member} offset applied."
                    : $"Slewing is true while {o.Member} = {string.Join(", ", slewingAt)}.",
                expected: "Slewing false", actual: slewingAt.Count == 0 ? "false" : "true",
                fix: $"A {o.Member} offset is tracking, not slewing: Slewing must stay false while it is applied (ConformU " +
                     "reports this as an issue), or clients wait forever for the mount to settle.", sourceMember: "Slewing");
    }

    /// <summary>
    /// "Reading this property must return a value of zero if TrackingRate is not driveSidereal", and writing one is an
    /// invalid operation. An offset is set at sidereal rate first, so a mount that just keeps it shows.
    /// </summary>
    private static async Task OffsetsAtOtherRateAsync(CheckContext ctx, DriveRate rate)
    {
        var c = ctx.Caps;
        foreach (var o in Offsets(c)) await ctx.TryAsync(t => o.Set(t, o.Low));
        var switched = await ctx.TryAsync(t => t.TrackingRate = rate);
        if (!switched.Ok)
        {
            // Not a failure of the offsets (Settings checks TrackingRate itself), but say why they weren't checked here.
            foreach (var member in new[] { "RightAscensionRate", "DeclinationRate" })
                ctx.Report($"{member}.ZeroAt{rate}", member, Outcome.Info,
                    $"With an offset set, TrackingRate = {rate} was refused, so {member} at that rate wasn't checked.",
                    actual: switched.ErrorText, ms: switched.Ms);
            return;
        }

        foreach (var (member, get) in new (string, Func<ITelescopeV4, double>)[]
                 { ("RightAscensionRate", t => t.RightAscensionRate), ("DeclinationRate", t => t.DeclinationRate) })
        {
            var read = await ctx.TryAsync(get);
            var outcome = read.Ok ? (read.Value == 0 ? Outcome.Pass : Outcome.Fail)
                : read.Kind == AscomErrorKind.InvalidOperation ? Outcome.Warning : Outcome.Fail;
            ctx.Report($"{member}.ZeroAt{rate}", member, outcome,
                outcome == Outcome.Pass ? $"{member} reads 0 with TrackingRate {rate}."
                    : read.Ok ? $"{member} reads {CheckContext.Show(read.Value)} with TrackingRate {rate}."
                    : $"Reading {member} with TrackingRate {rate} raised an error.",
                expected: "0", actual: read.Ok ? CheckContext.Show(read.Value) : read.ErrorText, ms: read.Ms,
                fix: read.Kind == AscomErrorKind.InvalidOperation
                    ? $"The ITelescopeV4 notes say \"Reading this property must return a value of zero if TrackingRate is not " +
                      $"driveSidereal\" (its exception list also names InvalidOperationException, but clients and ConformU expect 0). Return 0."
                    : $"{member} must read 0 while TrackingRate isn't driveSidereal: offsets only apply at sidereal rate, so " +
                      "report 0 (and stop applying any offset set earlier) when the rate changes.");
        }

        foreach (var o in Offsets(c))
        {
            var r = await ctx.ExpectErrorAsync($"{o.Member}.NotSidereal{rate}", o.Member,
                $"{o.Member} = {CheckContext.Show(o.Low)} with TrackingRate {rate}", t => o.Set(t, o.Low),
                AscomErrorKind.InvalidOperation, $"TrackingRate is {rate}, not driveSidereal", property: true);
            if (r.Ok) await ctx.TryAsync(t => o.Set(t, 0));
        }
    }

    // ---------- Pulse guide and MoveAxis movement ----------

    private static readonly GuideDirection[] GuideDirections = [GuideDirection.North, GuideDirection.South, GuideDirection.East, GuideDirection.West];

    /// <summary>Half the sidereal rate, in degrees per second: the usual guide rate, used when the mount doesn't say.</summary>
    internal const double AssumedGuideRate = 0.5 * 15.041 / 3600;

    /// <summary>The guide rates in degrees per second, or null where one can't be read (or isn't above 0).</summary>
    internal static async Task<(double? Ra, double? Dec)> GuideRatesAsync(CheckContext ctx)
    {
        var ra = await ctx.TryAsync(t => t.GuideRateRightAscension);
        var dec = await ctx.TryAsync(t => t.GuideRateDeclination);
        return (ra is { Ok: true, Value: > 0 } ? ra.Value : null, dec is { Ok: true, Value: > 0 } ? dec.Value : null);
    }

    /// <summary>
    /// Slews to this hour angle (positive: west of the meridian) at a safe declination and waits for the slew to end;
    /// false when the mount can't slew there, so the pulses are measured where it is.
    /// </summary>
    internal static async Task<bool> PlaceForGuidingAsync(CheckContext ctx, double hourAngle)
    {
        if (!ctx.Caps.CanSlewAsync) return false;
        var lst = await ctx.TryAsync(t => t.SiderealTime);
        if (!lst.Ok) return false;
        var ra = Values.Wrap24(lst.Value - hourAngle);
        var dec = MethodChecks.SafeDeclination(ctx);
        if (!(await ctx.TryAsync(t => t.SlewToCoordinatesAsync(ra, dec), ctx.SlewTimeout)).Ok) return false;
        await ctx.WaitForSlewEndAsync();
        await ctx.WaitUntilAsync(t => !t.IsPulseGuiding, TimeSpan.FromSeconds(5), 100);
        return true;
    }

    /// <summary>Pulses in each direction where the mount is, and checks how far and which way each one moved it.</summary>
    private static async Task GuideMovesAsync(CheckContext ctx, string side, double? rateRa, double? rateDec, int duration)
    {
        foreach (var direction in GuideDirections)
        {
            var before = await PositionAsync(ctx);
            var call = await ctx.TryAsync(t => t.PulseGuide(direction, duration), TimeSpan.FromSeconds(ctx.Options.CallTimeoutSeconds) + TimeSpan.FromMilliseconds(duration));
            if (!call.Ok || before is not { } b) continue;
            if (!await ctx.WaitUntilAsync(t => !t.IsPulseGuiding, TimeSpan.FromMilliseconds(duration + 3000), 100)) continue;
            if (await SettledPositionAsync(ctx) is { } a)
                ReportGuideMove(ctx, direction, side, b, a, direction is GuideDirection.North or GuideDirection.South ? rateDec : rateRa, duration);
        }
    }

    /// <summary>
    /// Whether a pulse moved the mount the right way and about the right distance (the guide rate times the
    /// duration): guideNorth raises Declination, guideSouth lowers it, guideEast raises Right Ascension and
    /// guideWest lowers it, on either side of the pier and in either hemisphere.
    /// </summary>
    private static void ReportGuideMove(CheckContext ctx, GuideDirection direction, string? side,
        (double Ra, double Dec) before, (double Ra, double Dec) after, double? rate, int duration)
    {
        var id = $"PulseGuide.{direction}.Moves" + (side is null ? "" : $".{side}");
        var inDec = direction is GuideDirection.North or GuideDirection.South;
        var sign = direction is GuideDirection.North or GuideDirection.East ? 1 : -1;
        var coordinate = inDec ? "Declination" : "Right Ascension";
        // Degrees: of declination, or of the RA axis (15 per hour of RA).
        var moved = inDec ? after.Dec - before.Dec : Values.HoursDiff(after.Ra, before.Ra) * 15;
        var expected = (rate ?? AssumedGuideRate) * duration / 1000;
        var where = side is null ? "" : $" ({(side == nameof(PointingState.Normal) ? "west of the meridian, pier side Normal" : "east of the meridian, pier side ThroughThePole")})";
        var expectedText = $"{coordinate} {(sign > 0 ? "up" : "down")} about {Arcsec(expected)}" + (rate is null ? " (guide rate unknown: half sidereal assumed)" : "");
        var actualText = $"{coordinate} {(moved >= 0 ? "up" : "down")} {Arcsec(Math.Abs(moved))}";
        const string fix =
            "guideNorth must raise Declination and guideSouth lower it; guideEast must raise Right Ascension and guideWest " +
            "lower it, by about the guide rate times the duration. That holds on both sides of the pier and in both " +
            "hemispheres: on a German mount the Dec motor turns the other way after a meridian flip, and in the southern " +
            "hemisphere the RA motor does, so the driver has to reverse the motor direction in those cases. Autoguiders " +
            "calibrate once and rely on this.";
        string[] patterns = ["PulseGuide", "GuideDirection"];

        if (Math.Abs(moved) < Math.Max(0.2 * expected, 1.0 / 3600))
        {
            ctx.Report(id, "PulseGuide", Outcome.Fail, $"PulseGuide({direction}, {duration}) didn't move the mount{where}.",
                expected: expectedText, actual: actualText, fix: fix, sourcePatterns: patterns);
            return;
        }
        if (Math.Sign(moved) != sign)
        {
            ctx.Report(id, "PulseGuide", Outcome.Fail, $"PulseGuide({direction}, {duration}) moved the mount the wrong way{where}.",
                expected: expectedText, actual: actualText, fix: fix, sourcePatterns: patterns);
            return;
        }
        // An RA guide rate may be meant on the sky rather than on the axis: on the axis that's 1/cos(Dec) more.
        var ratio = Math.Abs(moved) / expected;
        var skyRatio = ratio * Math.Cos(before.Dec * Math.PI / 180);
        var distanceOk = ratio is >= 0.5 and <= 1.5 || (!inDec && skyRatio is >= 0.5 and <= 1.5);
        ctx.Report(id, "PulseGuide", distanceOk ? Outcome.Pass : rate is null ? Outcome.Info : Outcome.Warning,
            distanceOk
                ? $"PulseGuide({direction}, {duration}) moved {coordinate} the right way by {Arcsec(Math.Abs(moved))}{where}."
                : $"PulseGuide({direction}, {duration}) moved {coordinate} the right way{where}, but {Arcsec(Math.Abs(moved))} rather than about {Arcsec(expected)}.",
            expected: expectedText, actual: actualText,
            fix: distanceOk ? null : "A pulse should move the mount by about its guide rate (GuideRateRightAscension, GuideRateDeclination, " +
                                     "in degrees per second) times its duration; otherwise autoguiders over- or under-correct.",
            sourcePatterns: patterns);
    }

    private static string Arcsec(double degrees) => $"{degrees * 3600:0.#} arcsec";

    /// <summary>
    /// Where an axis points, in degrees: on an alt/az mount the primary axis is azimuth and the secondary altitude;
    /// on an equatorial mount, RA (15 degrees an hour) and Dec. Null for the tertiary axis or when it can't be read.
    /// </summary>
    private static async Task<double?> AxisAngleAsync(CheckContext ctx, TelescopeAxis axis)
    {
        var altAz = ctx.Caps.AlignmentMode == AlignmentMode.AltAz;
        var read = axis switch
        {
            TelescopeAxis.Primary => altAz ? await ctx.TryAsync(t => t.Azimuth) : await ctx.TryAsync(t => t.RightAscension * 15),
            TelescopeAxis.Secondary => altAz ? await ctx.TryAsync(t => t.Altitude) : await ctx.TryAsync(t => t.Declination),
            _ => null
        };
        return read is { Ok: true } ? read.Value : null;
    }

    /// <summary>How far an axis turned from <paramref name="from"/> to <paramref name="to"/>, in degrees (the primary wrapped to ±180).</summary>
    private static double AxisDiff(TelescopeAxis axis, double to, double from)
        => axis == TelescopeAxis.Primary ? ((to - from) % 360 + 540) % 360 - 180 : to - from;

    /// <summary>Movements under this (30 arc-seconds) are too small to tell from tracking and reading noise.</summary>
    private const double MeasurableMove = 30.0 / 3600;

    /// <summary>MoveAxis at a rate in degrees per second must turn the axis at about that rate.</summary>
    private static void ReportAxisSpeed(CheckContext ctx, TelescopeAxis axis, double rate, double seconds, double? moved)
    {
        var id = $"MoveAxis.{axis}.Speed";
        var expected = Math.Abs(rate) * seconds;
        if (moved is null || expected < MeasurableMove) return;
        var unit = ctx.Caps.AlignmentMode == AlignmentMode.AltAz ? (axis == TelescopeAxis.Primary ? "azimuth" : "altitude")
            : axis == TelescopeAxis.Primary ? "right ascension (15° an hour)" : "declination";
        var expectedText = $"about {expected:0.###}° of {unit} in {seconds:0.0} s";
        var actualText = $"{Math.Abs(moved.Value):0.###}°";
        if (Math.Abs(moved.Value) < 0.1 * expected)
        {
            ctx.Report(id, "MoveAxis", Outcome.Fail, $"MoveAxis({axis}, {CheckContext.Show(rate)}) didn't turn the axis.",
                expected: expectedText, actual: actualText,
                fix: "MoveAxis must turn the axis at the given rate, in degrees per second, until it's stopped with rate 0.",
                sourcePatterns: ["MoveAxis"]);
            return;
        }
        var ratio = Math.Abs(moved.Value) / expected;
        var ok = ratio is >= 0.5 and <= 1.5;
        ctx.Report(id, "MoveAxis", ok ? Outcome.Pass : Outcome.Warning,
            ok ? $"MoveAxis({axis}, {CheckContext.Show(rate)}) turned the axis at about the rate asked ({Math.Abs(moved.Value) / seconds:0.###}°/s)."
               : $"MoveAxis({axis}, {CheckContext.Show(rate)}) turned the axis at {Math.Abs(moved.Value) / seconds:0.###}°/s, not about {Math.Abs(rate):0.###}°/s.",
            expected: expectedText, actual: actualText,
            fix: ok ? null : "MoveAxis's rate is in degrees per second (and AxisRates lists the rates in the same unit): check the conversion to motor steps.",
            sourcePatterns: ["MoveAxis", "AxisRates"]);
    }

    /// <summary>A negative rate must turn the axis the other way from a positive one.</summary>
    private static void ReportAxisDirection(CheckContext ctx, TelescopeAxis axis, double rate,
        double forwardSeconds, double forward, double backSeconds, double back)
    {
        var id = $"MoveAxis.{axis}.Direction";
        if (Math.Abs(rate) * forwardSeconds < MeasurableMove || Math.Abs(forward) < 0.1 * Math.Abs(rate) * forwardSeconds) return;
        var backExpected = Math.Abs(rate) * backSeconds;
        var fix = "MoveAxis takes its direction from the rate's sign: a negative rate must turn the axis the opposite way to a positive one.";
        var actual = $"+{CheckContext.Show(rate)}: {forward:+0.###;-0.###}°, -{CheckContext.Show(rate)}: {back:+0.###;-0.###}°";
        if (Math.Abs(back) < 0.1 * backExpected)
            ctx.Report(id, "MoveAxis", Outcome.Fail, $"MoveAxis({axis}, {CheckContext.Show(-rate)}) didn't turn the axis.",
                expected: "the axis turns back", actual: actual, fix: fix, sourcePatterns: ["MoveAxis"]);
        else if (Math.Sign(back) == Math.Sign(forward))
            ctx.Report(id, "MoveAxis", Outcome.Fail, $"MoveAxis({axis}) turned the same way for a negative rate as for a positive one.",
                expected: "opposite directions", actual: actual, fix: fix, sourcePatterns: ["MoveAxis"]);
        else
            ctx.Report(id, "MoveAxis", Outcome.Pass, $"MoveAxis({axis}) turned one way for a positive rate and back for a negative one.", actual: actual);
    }

    /// <summary>How far an axis may move between two readings 0.3 s apart and still count as at rest (RA drifts 4.5" in that time with tracking off).</summary>
    private const double StillTolerance = 10.0 / 3600;

    /// <summary>
    /// After Slewing has gone false: the axis's angle once it holds still (two readings 0.3 s apart within
    /// <see cref="StillTolerance"/>, for up to 10 s), and how far and how long it still moved after Slewing went false.
    /// </summary>
    private sealed record AxisRest(double? Angle, double MovedAfter, double SecondsAfter);

    private static async Task<AxisRest> WaitForAxisStillAsync(CheckContext ctx, TelescopeAxis axis)
    {
        var first = await AxisAngleAsync(ctx, axis);
        if (first is null) return new AxisRest(null, 0, 0);
        var clock = Stopwatch.StartNew();
        var last = first.Value;
        var stillAt = 0.0;
        while (clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(300, ctx.Token);
            var now = await AxisAngleAsync(ctx, axis);
            if (now is null) break;
            var step = Math.Abs(AxisDiff(axis, now.Value, last));
            last = now.Value;
            if (step <= StillTolerance) break;
            stillAt = clock.Elapsed.TotalSeconds;
        }
        return new AxisRest(last, Math.Abs(AxisDiff(axis, last, first.Value)), stillAt);
    }

    /// <summary>
    /// "Slewing must also be true during any MoveAxis() operations": an axis still turning after Slewing went false
    /// (it was stopped with rate 0, but decelerates or coasts on) tells clients it has stopped when it hasn't.
    /// </summary>
    private static void ReportSlewingUntilStopped(CheckContext ctx, TelescopeAxis axis, AxisRest forward, AxisRest? back)
    {
        if (forward.Angle is null) return;
        var worst = back is { Angle: not null } b && b.MovedAfter > forward.MovedAfter ? b : forward;
        var moved = worst.MovedAfter > 3 * StillTolerance;
        ctx.Report($"MoveAxis.{axis}.SlewingUntilStopped", "Slewing", moved ? Outcome.Fail : Outcome.Pass,
            moved ? $"After MoveAxis({axis}, 0), Slewing went false while the axis was still moving: it turned another {worst.MovedAfter * 3600:0}\" over {worst.SecondsAfter:0.0} s."
                  : $"After MoveAxis({axis}, 0), the axis had stopped when Slewing went false.",
            expected: "the axis at rest when Slewing goes false",
            actual: $"after Slewing went false: {forward.MovedAfter * 3600:0.0}\" (forward stop){(back is { Angle: not null } bb ? $", {bb.MovedAfter * 3600:0.0}\" (reverse stop)" : "")}",
            fix: moved ? "Keep Slewing true until the axis has actually stopped (its deceleration included), not only until the stop command is sent: clients wait for Slewing false before they take an exposure or the next move." : null,
            sourceMember: "Slewing", sourcePatterns: ["MoveAxis"]);
    }

    // ---------- Helpers ----------

    /// <summary>
    /// The position once it has stopped changing: three readings 0.4 s apart within 2" of each other in RA (on the
    /// sky) and Dec, waiting up to <paramref name="maxSeconds"/>. A real mount may still be finishing a move when
    /// it reports it done, and a driver that refreshes its position every few hundred ms reads stale until then.
    /// </summary>
    public static async Task<(double Ra, double Dec)?> SettledPositionAsync(CheckContext ctx, double maxSeconds = 5)
    {
        var last = await PositionAsync(ctx);
        if (last is null) return null;
        var clock = Stopwatch.StartNew();
        var stillSteps = 0;
        while (clock.Elapsed.TotalSeconds < maxSeconds && stillSteps < 2)
        {
            await Task.Delay(400, ctx.Token);
            if (await PositionAsync(ctx) is not { } now) break;
            var dRa = Math.Abs(Values.HoursDiff(now.Ra, last.Value.Ra)) * 15 * 3600 * Math.Cos(now.Dec * Math.PI / 180);
            var dDec = Math.Abs(now.Dec - last.Value.Dec) * 3600;
            stillSteps = dRa <= 2 && dDec <= 2 ? stillSteps + 1 : 0;
            last = now;
        }
        return last;
    }

    public static async Task<(double Ra, double Dec)?> PositionAsync(CheckContext ctx)
    {
        var ra = await ctx.TryAsync(t => t.RightAscension);
        var dec = await ctx.TryAsync(t => t.Declination);
        return ra.Ok && dec.Ok ? (ra.Value, dec.Value) : null;
    }

    private static async Task<(double Alt, double Az)?> AltAzPositionAsync(CheckContext ctx)
    {
        var alt = await ctx.TryAsync(t => t.Altitude);
        var az = await ctx.TryAsync(t => t.Azimuth);
        return alt.Ok && az.Ok ? (alt.Value, az.Value) : null;
    }

    /// <summary>Stops any movement a faulty call started and waits for it to end.</summary>
    public static async Task StopAsync(CheckContext ctx)
    {
        await ctx.TryAsync(t => t.AbortSlew());
        await ctx.WaitForSlewEndAsync();
    }
}
