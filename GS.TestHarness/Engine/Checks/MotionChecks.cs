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
    public string Description => "Slews (sync, async, to target, alt/az), syncs, MoveAxis, PulseGuide, AbortSlew, tracking, rate offsets and pier side. Moves the mount.";

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

            await NotImplementedMethodsAsync(ctx);
            await SlewsAsync(ctx);
            await AbortAsync(ctx);
            await SyncsAsync(ctx);
            await AltAzAsync(ctx);
            await MoveAxisAsync(ctx);
            await PulseGuideAsync(ctx);
            await TrackingAsync(ctx);
            await RateOffsetsAsync(ctx);
            await SideOfPierAsync(ctx);
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

    private static async Task NotImplementedMethodsAsync(CheckContext ctx)
    {
        var c = ctx.Caps;
        var pos = await PositionAsync(ctx);
        if (pos is null) return;
        var (ra, dec) = pos.Value;
        var altaz = await AltAzPositionAsync(ctx);

        async Task Check(string id, string member, bool capability, string capabilityName, Action<ITelescopeV4> call)
        {
            if (capability) return;
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
            if (c.CanMoveAxis[axis]) continue;
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
        await Task.Delay(500, ctx.Token);
        var wasSlewing = (await ctx.TryAsync(t => t.Slewing)).Value;
        var abort = await ctx.TryAsync(t => t.AbortSlew(), ctx.SlewTimeout);
        var stopped = await ctx.WaitUntilAsync(t => !t.Slewing, TimeSpan.FromSeconds(10));
        if (!wasSlewing)
        {
            ctx.Report("AbortSlew.DuringSlew", "AbortSlew", Outcome.Info, "The slew finished too quickly to stop it, so AbortSlew during a slew wasn't checked.");
            return;
        }
        ctx.Report("AbortSlew.DuringSlew", "AbortSlew", abort.Ok && stopped ? Outcome.Pass : Outcome.Fail,
            abort.Ok && stopped ? "AbortSlew stopped a slew." : abort.Ok ? "Slewing stayed true for 10 s after AbortSlew." : "AbortSlew during a slew raised an error.",
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

        // Sync to where the mount already is: a valid sync that changes the pointing model as little as possible.
        var sync = await ctx.TryAsync(t => t.SyncToCoordinates(ra, dec));
        await CheckPositionAfterSync(ctx, "SyncToCoordinates", sync, ra, dec);

        var target = await ctx.TryAsync(t => { t.TargetRightAscension = ra; t.TargetDeclination = dec; });
        if (target.Ok)
        {
            var toTarget = await ctx.TryAsync(t => t.SyncToTarget());
            await CheckPositionAfterSync(ctx, "SyncToTarget", toTarget, ra, dec);
        }

        await ctx.ExpectErrorAsync("SyncToCoordinates.InvalidRa", "SyncToCoordinates", "SyncToCoordinates(25, dec)",
            t => t.SyncToCoordinates(25, dec), AscomErrorKind.InvalidValue, "right ascension is 0 up to 24 hours");
        await ctx.ExpectErrorAsync("SyncToCoordinates.InvalidDec", "SyncToCoordinates", "SyncToCoordinates(ra, 91)",
            t => t.SyncToCoordinates(ra, 91), AscomErrorKind.InvalidValue, "declination is -90 to 90 degrees");

        // Not checked: a sync with Tracking false. The documentation only says it "may throw DriverException", and
        // ConformU doesn't test it.
    }

    private static async Task CheckPositionAfterSync(CheckContext ctx, string member, Call<bool> sync, double ra, double dec)
    {
        if (!sync.Ok)
        {
            ctx.Report(member, member, Outcome.Fail, $"{member} raised an error.", actual: sync.ErrorText, ms: sync.Ms,
                fix: $"CanSync is true, so {member} to the mount's own position must work.");
            return;
        }
        var after = await PositionAsync(ctx);
        if (after is null) return;
        var sep = Values.Separation(after.Value.Ra, after.Value.Dec, ra, dec);
        ctx.Report(member, member, sep > PositionFail ? Outcome.Fail : sep > PositionOk ? Outcome.Info : Outcome.Pass,
            $"{member}: position afterwards is {sep * 3600:0.0}\" from the sync coordinates.",
            expected: $"RA {CheckContext.Show(ra)} h, Dec {CheckContext.Show(dec)}°",
            actual: $"RA {CheckContext.Show(after.Value.Ra)} h, Dec {CheckContext.Show(after.Value.Dec)}°", ms: sync.Ms,
            fix: $"After {member}, RightAscension and Declination must read the coordinates synced to.");
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
                    var sync = await ctx.TryAsync(t => t.SyncToAltAz(h.Az, h.Alt));
                    ctx.Report("SyncToAltAz", "SyncToAltAz", sync.Ok ? Outcome.Pass : Outcome.Fail,
                        sync.Ok ? "SyncToAltAz to the mount's own position worked." : "SyncToAltAz raised an error.",
                        actual: sync.ErrorText, ms: sync.Ms, fix: "CanSyncAltAz is true, so SyncToAltAz (with tracking off) must work.");
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
            var move = await ctx.TryAsync(t => t.MoveAxis(axis, rate));
            if (!move.Ok)
            {
                ctx.Report(id, "MoveAxis", Outcome.Fail, $"MoveAxis({axis}, {CheckContext.Show(rate)}) raised an error.",
                    actual: move.ErrorText, ms: move.Ms, fix: "A rate inside AxisRates must be accepted.");
                continue;
            }
            await Task.Delay(1500, ctx.Token);
            var slewing = await ctx.TryAsync(t => t.Slewing);
            ctx.Report($"{id}.Slewing", "MoveAxis", slewing is { Ok: true, Value: true } ? Outcome.Pass : Outcome.Fail,
                slewing is { Ok: true, Value: true } ? "Slewing is true while an axis moves." : "Slewing is false while MoveAxis is moving an axis.",
                expected: "Slewing true", actual: slewing.Ok ? slewing.Value.ToString() : slewing.ErrorText,
                fix: "Slewing must be true while any axis is moving under MoveAxis, so clients know the mount is moving.");
            var stop = await ctx.TryAsync(t => t.MoveAxis(axis, 0));
            var stopped = await ctx.WaitUntilAsync(t => !t.Slewing, TimeSpan.FromSeconds(15));
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

            var reverse = await ctx.TryAsync(t => t.MoveAxis(axis, -rate));
            await Task.Delay(500, ctx.Token);
            await ctx.TryAsync(t => t.MoveAxis(axis, 0));
            ctx.Report($"{id}.Negative", "MoveAxis", reverse.Ok ? Outcome.Pass : Outcome.Warning,
                reverse.Ok ? "A negative rate (the other direction) was accepted." : "A negative rate was refused.",
                actual: reverse.ErrorText, fix: "MoveAxis takes the direction from the rate's sign: -rate must be accepted wherever +rate is.");
            await ctx.WaitUntilAsync(t => !t.Slewing, TimeSpan.FromSeconds(15));

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
        const int duration = 1000;
        foreach (var direction in new[] { GuideDirection.North, GuideDirection.South, GuideDirection.East, GuideDirection.West })
        {
            var id = $"PulseGuide.{direction}";
            var watch = Stopwatch.StartNew();
            var call = await ctx.TryAsync(t => t.PulseGuide(direction, duration), TimeSpan.FromSeconds(ctx.Options.CallTimeoutSeconds + 2));
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
        }
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

    // ---------- Tracking, rate offsets, pier side ----------

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
            foreach (var (member, can, set, get, value) in new (string, bool, Action<ITelescopeV4, double>, Func<ITelescopeV4, double>, double)[]
                     {
                         ("RightAscensionRate", c.CanSetRightAscensionRate, (t, v) => t.RightAscensionRate = v, t => t.RightAscensionRate, 0.05),
                         ("DeclinationRate", c.CanSetDeclinationRate, (t, v) => t.DeclinationRate = v, t => t.DeclinationRate, 0.5)
                     })
            {
                if (!can) continue;
                var write = await ctx.TryAsync(t => set(t, value));
                var read = write.Ok ? await ctx.TryAsync(get) : null;
                await ctx.TryAsync(t => set(t, 0));
                var ok = write.Ok && read is { Ok: true } && Math.Abs(read.Value - value) < 1e-6;
                ctx.Report($"{member}.Offset", member, ok ? Outcome.Pass : Outcome.Fail,
                    ok ? $"{member} set to {value} and read back (then put back to 0)." : $"{member} = {value} didn't read back.",
                    expected: CheckContext.Show(value), actual: !write.Ok ? write.ErrorText : read!.Ok ? CheckContext.Show(read.Value) : read.ErrorText,
                    ms: write.Ms, fix: $"With TrackingRate driveSidereal, {member} must accept an offset and read it back unchanged " +
                                       "(RightAscensionRate is in seconds of RA per sidereal second, DeclinationRate in arc-seconds per SI second).");
            }
        }
        finally
        {
            if (originalRate.Ok) await ctx.TryAsync(t => t.TrackingRate = originalRate.Value);
        }

        // Rate offsets only apply at sidereal rate: at any other rate, writing one is an invalid operation.
        var other = (ctx.Facts.GetValueOrDefault("TrackingRates") as List<DriveRate>)?.FirstOrDefault(r => r != DriveRate.Sidereal);
        if (other is { } rate && rate != DriveRate.Sidereal && c.CanSetRightAscensionRate && (await ctx.TryAsync(t => t.TrackingRate = rate)).Ok)
        {
            try
            {
                var r = await ctx.ExpectErrorAsync("RightAscensionRate.NotSidereal", "RightAscensionRate",
                    $"RightAscensionRate = 0.05 with TrackingRate {rate}", t => t.RightAscensionRate = 0.05,
                    AscomErrorKind.InvalidOperation, "TrackingRate isn't driveSidereal", property: true);
                if (r.Ok) await ctx.TryAsync(t => t.RightAscensionRate = 0);
            }
            finally
            {
                if (originalRate.Ok) await ctx.TryAsync(t => t.TrackingRate = originalRate.Value);
            }
        }
    }

    private static async Task SideOfPierAsync(CheckContext ctx)
    {
        var c = ctx.Caps;
        if (!c.CanSetPierSide || !c.CanSlewAsync) return;
        // Near the meridian, where either side can reach the sky without passing a flip limit.
        var lst = await ctx.TryAsync(t => t.SiderealTime);
        if (!lst.Ok) return;
        var dec = MethodChecks.SafeDeclination(ctx);
        var ra = Values.Wrap24(lst.Value - 0.1);
        await ctx.TryAsync(t => t.SlewToCoordinatesAsync(ra, dec));
        await ctx.WaitForSlewEndAsync();
        var before = await ctx.TryAsync(t => t.SideOfPier);
        if (!before.Ok || before.Value == PointingState.Unknown) return;
        var other = before.Value == PointingState.Normal ? PointingState.ThroughThePole : PointingState.Normal;

        var flip = await ctx.TryAsync(t => t.SideOfPier = other, ctx.SlewTimeout);
        await ctx.WaitForSlewEndAsync();
        var after = await ctx.TryAsync(t => t.SideOfPier);
        var ok = flip.Ok && after is { Ok: true } && after.Value == other;
        ctx.Report("SideOfPier.Set", "SideOfPier", ok ? Outcome.Pass : Outcome.Fail,
            ok ? $"SideOfPier = {other} flipped the mount (from {before.Value})." : $"SideOfPier = {other} didn't flip the mount.",
            expected: other.ToString(), actual: !flip.Ok ? flip.ErrorText : after.Ok ? after.Value.ToString() : after.ErrorText, ms: flip.Ms,
            fix: "CanSetPierSide is true, so setting SideOfPier near the meridian must move the mount to that side, pointing at the same place.");
        await ctx.TryAsync(t => t.SideOfPier = before.Value, ctx.SlewTimeout);
        await ctx.WaitForSlewEndAsync();
    }

    // ---------- Helpers ----------

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
