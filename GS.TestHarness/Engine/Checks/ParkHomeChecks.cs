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

/// <summary>FindHome, Park, Unpark and SetPark, and what a parked mount must refuse. Moves the mount.</summary>
public sealed class ParkHomeChecks : ICheckGroup
{
    public string Name => "ParkHome";
    public string Description => "FindHome and AtHome, Park/Unpark and AtPark, what a parked mount must refuse, and SetPark (only if allowed). Moves the mount.";

    public string? SkipReason(CheckContext ctx) => ctx.Options.AllowMotion ? null : "motion is turned off for this run";

    public async Task RunAsync(CheckContext ctx)
    {
        var c = ctx.Caps;
        var wasParked = (await ctx.TryAsync(t => t.AtPark)).Value;
        var wasTracking = (await ctx.TryAsync(t => t.Tracking)).Value;
        try
        {
            if (wasParked && c.CanUnpark)
            {
                await ctx.TryAsync(t => t.Unpark(), ctx.SlewTimeout);
                await ctx.WaitUntilAsync(t => !t.AtPark && !t.Slewing, ctx.SlewTimeout);
            }
            await AbortParkAndHomeAsync(ctx);
            await FindHomeAsync(ctx);
            await ParkAsync(ctx);
            await SetParkAsync(ctx);
        }
        finally
        {
            await ctx.TryAsync(t => { if (t.Slewing) t.AbortSlew(); });
            var parked = (await ctx.TryAsync(t => t.AtPark)).Value;
            if (wasParked && !parked && c.CanPark)
            {
                await ctx.TryAsync(t => t.Park(), ctx.SlewTimeout);
                await ctx.WaitUntilAsync(t => t.AtPark, ctx.SlewTimeout);
            }
            else if (!wasParked && parked && c.CanUnpark)
            {
                await ctx.TryAsync(t => t.Unpark(), ctx.SlewTimeout);
            }
            if (!wasParked && c.CanSetTracking) await ctx.TryAsync(t => t.Tracking = wasTracking);
        }
    }

    /// <summary>
    /// AbortSlew stops "slewing, parking, find-home, and move-axis": start a Park (then a FindHome), abort it part way,
    /// and the mount must stop without having parked (or reached home).
    /// </summary>
    private static async Task AbortParkAndHomeAsync(CheckContext ctx)
    {
        var c = ctx.Caps;
        async Task AbortAsync(string id, string member, Action<ITelescopeV4> start, Func<ITelescopeV4, bool> arrived, string arrivedName)
        {
            // Move away first, so the operation has somewhere to go.
            if (c.CanSlewAsync && await MotionChecks.PositionAsync(ctx) is { } here && (await ctx.TryAsync(t => t.SiderealTime)) is { Ok: true } lst)
            {
                await ctx.TryAsync(t => t.Tracking = true);
                await ctx.TryAsync(t => t.SlewToCoordinatesAsync(Values.Wrap24(lst.Value - 2), MethodChecks.SafeDeclination(ctx)));
                await ctx.WaitForSlewEndAsync();
            }
            if (!(await ctx.TryAsync(start, ctx.SlewTimeout)).Ok) return;
            await Task.Delay(150, ctx.Token);
            if ((await ctx.TryAsync(t => t.Slewing)) is not { Ok: true, Value: true })
            {
                ctx.Report(id, "AbortSlew", Outcome.Info, $"{member} finished too quickly to stop it, so AbortSlew during {member} wasn't checked.");
                return;
            }
            var abort = await ctx.TryAsync(t => t.AbortSlew(), ctx.SlewTimeout);
            var stopped = await ctx.WaitUntilAsync(t => !t.Slewing, TimeSpan.FromSeconds(10));
            var there = await ctx.TryAsync(arrived);
            var ok = abort.Ok && stopped && there is { Ok: true, Value: false };
            ctx.Report(id, "AbortSlew", ok ? Outcome.Pass : Outcome.Fail,
                ok ? $"AbortSlew stopped {member} part way ({arrivedName} false)."
                    : !abort.Ok ? $"AbortSlew during {member} raised an error." : !stopped ? $"Slewing stayed true after AbortSlew during {member}."
                    : $"After AbortSlew during {member}, {arrivedName} is true: it wasn't stopped.",
                actual: abort.Ok ? null : abort.ErrorText, ms: abort.Ms,
                fix: $"AbortSlew must stop {member} too (\"slewing, parking, find-home, and move-axis\"), leaving {arrivedName} false.");
            if ((await ctx.TryAsync(t => t.AtPark)).Value && c.CanUnpark) await ctx.TryAsync(t => t.Unpark(), ctx.SlewTimeout);
            await ctx.WaitUntilAsync(t => !t.Slewing, ctx.SlewTimeout);
        }
        if (c.CanPark && c.CanUnpark) await AbortAsync("AbortSlew.DuringPark", "Park", t => t.Park(), t => t.AtPark, "AtPark");
        if (c.CanFindHome) await AbortAsync("AbortSlew.DuringFindHome", "FindHome", t => t.FindHome(), t => t.AtHome, "AtHome");
    }

    private static async Task FindHomeAsync(CheckContext ctx)
    {
        if (!ctx.Caps.CanFindHome) return;
        var call = await ctx.TryAsync(t => t.FindHome(), ctx.SlewTimeout);
        if (!call.Ok)
        {
            ctx.Report("FindHome", "FindHome", Outcome.Fail, "FindHome raised an error.", actual: call.ErrorText, ms: call.Ms,
                fix: "CanFindHome is true, so FindHome must work on an unparked mount.");
            await MotionChecks.StopAsync(ctx);
            return;
        }
        if (call.Ms > 5000)
            ctx.Report("FindHome.Returns", "FindHome", Outcome.Warning, $"FindHome took {call.Ms / 1000:0.0} s to return.",
                expected: "returns at once (ITelescopeV4), with Slewing true until home is reached",
                fix: "In ITelescopeV4 FindHome is asynchronous: start the move and return; report it through Slewing and AtHome.");
        var home = await ctx.WaitUntilAsync(t => t.AtHome && !t.Slewing, ctx.SlewTimeout);
        ctx.Report("FindHome", "FindHome", home ? Outcome.Pass : Outcome.Fail,
            home ? "FindHome reached home: AtHome true." : $"AtHome wasn't true {ctx.Options.SlewTimeoutSeconds} s after FindHome.",
            expected: "AtHome true, Slewing false", ms: call.Ms,
            fix: "AtHome must become true when FindHome has reached the home position (and Slewing false).");
        if (!home) return;

        // Leaving home must clear AtHome.
        if (ctx.Caps.CanSlewAsync && (await MotionChecks.PositionAsync(ctx)) is { } pos)
        {
            var dec = Math.Clamp(pos.Dec + (pos.Dec > 0 ? -10 : 10), -80, 80);
            await ctx.TryAsync(t => t.Tracking = true);
            await ctx.TryAsync(t => t.SlewToCoordinatesAsync(pos.Ra, dec));
            await ctx.WaitForSlewEndAsync();
            var atHome = await ctx.TryAsync(t => t.AtHome);
            ctx.Report("AtHome.AfterSlew", "AtHome", atHome is { Ok: true, Value: false } ? Outcome.Pass : Outcome.Fail,
                atHome is { Ok: true, Value: false } ? "AtHome went false when the mount left home." : "AtHome is still true after slewing away from home.",
                expected: "false", actual: atHome.Ok ? atHome.Value.ToString() : atHome.ErrorText,
                fix: "AtHome must be true only while the mount is at its home position: clear it when the mount moves.");
        }
    }

    private static async Task ParkAsync(CheckContext ctx)
    {
        var c = ctx.Caps;
        if (!c.CanPark) return;
        if (!c.CanUnpark)
        {
            ctx.Report("Park", "Park", Outcome.Skipped, "CanUnpark is false: parking would leave the mount stuck, so Park isn't checked.");
            return;
        }

        var call = await ctx.TryAsync(t => t.Park(), ctx.SlewTimeout);
        if (!call.Ok)
        {
            ctx.Report("Park", "Park", Outcome.Fail, "Park raised an error.", actual: call.ErrorText, ms: call.Ms,
                fix: "CanPark is true, so Park must work.");
            await MotionChecks.StopAsync(ctx);
            return;
        }
        var parked = await ctx.WaitUntilAsync(t => t.AtPark && !t.Slewing, ctx.SlewTimeout);
        ctx.Report("Park", "Park", parked ? Outcome.Pass : Outcome.Fail,
            parked ? $"Parked (Park returned in {call.Ms / 1000:0.0} s)." : $"AtPark wasn't true {ctx.Options.SlewTimeoutSeconds} s after Park.",
            expected: "AtPark true, Slewing false", ms: call.Ms,
            fix: "AtPark must become true when the mount reaches its park position, and Slewing go false.");
        if (!parked) return;

        var tracking = await ctx.TryAsync(t => t.Tracking);
        if (tracking is { Ok: true, Value: true })
            ctx.Report("Park.Tracking", "Park", Outcome.Warning, "Tracking is still true while parked.",
                fix: "A parked mount should stop tracking (Tracking false), or it drifts away from the park position.");

        var again = await ctx.TryAsync(t => t.Park(), ctx.SlewTimeout);
        ctx.Report("Park.WhenParked", "Park", again.Ok ? Outcome.Pass : Outcome.Fail,
            again.Ok ? "Park while parked: no error." : "Park while parked raised an error.", actual: again.ErrorText,
            fix: "Park on a parked mount must do nothing, without an error.");

        // A parked mount must refuse to move.
        var pos = await MotionChecks.PositionAsync(ctx);
        var (ra, dec) = pos ?? (12, 0);
        async Task Refuses(string id, string member, string what, Action<ITelescopeV4> call)
        {
            var r = await ctx.ExpectErrorAsync($"Parked.{id}", member, what, call, AscomErrorKind.Parked, "the mount is parked",
                timeout: ctx.SlewTimeout);
            // ConformU: a refused command must not unpark the mount either.
            if (!r.Ok && (await ctx.TryAsync(t => t.AtPark)) is { Ok: true, Value: false })
                ctx.Report($"Parked.{id}.StillParked", member, Outcome.Fail, $"{what} unparked the mount.",
                    expected: "AtPark stays true", actual: "AtPark false",
                    fix: $"{member} must check AtPark first and throw ParkedException without touching the mount.");
            if (r.Ok || (await ctx.TryAsync(t => t.AtPark)) is { Ok: true, Value: false })
            {
                // It moved off the park position: park again for the rest.
                await MotionChecks.StopAsync(ctx);
                await ctx.TryAsync(t => t.Park(), ctx.SlewTimeout);
                await ctx.WaitUntilAsync(t => t.AtPark && !t.Slewing, ctx.SlewTimeout);
            }
        }
        if (c.CanSlewAsync) await Refuses("SlewToCoordinatesAsync", "SlewToCoordinatesAsync", "SlewToCoordinatesAsync while parked", t => t.SlewToCoordinatesAsync(ra, dec));
        if (c.CanSlew && ctx.Target.Kind == TargetKind.Com) await Refuses("SlewToCoordinates", "SlewToCoordinates", "SlewToCoordinates while parked", t => t.SlewToCoordinates(ra, dec));
        if (c.CanSlewAsync) await Refuses("SlewToTargetAsync", "SlewToTargetAsync", "SlewToTargetAsync while parked", t => { t.TargetRightAscension = ra; t.TargetDeclination = dec; t.SlewToTargetAsync(); });
        if (c.CanSync) await Refuses("SyncToCoordinates", "SyncToCoordinates", "SyncToCoordinates while parked", t => t.SyncToCoordinates(ra, dec));
        if (c.CanPulseGuide) await Refuses("PulseGuide", "PulseGuide", "PulseGuide while parked", t => t.PulseGuide(GuideDirection.North, 100));
        for (var i = 0; i < 3; i++)
        {
            var axis = (TelescopeAxis)i;
            // Rate 0, as ConformU: refused all the same, and harmless if it isn't.
            if (c.CanMoveAxis[i]) await Refuses($"MoveAxis.{axis}", "MoveAxis", $"MoveAxis({axis}, 0) while parked", t => t.MoveAxis(axis, 0));
        }
        if (c.CanSlewAltAzAsync) await Refuses("SlewToAltAzAsync", "SlewToAltAzAsync", "SlewToAltAzAsync while parked", t => t.SlewToAltAzAsync(180, 45));
        if (c.CanFindHome) await Refuses("FindHome", "FindHome", "FindHome while parked", t => t.FindHome());
        await Refuses("AbortSlew", "AbortSlew", "AbortSlew while parked", t => t.AbortSlew());
        if (c.CanSetTracking)
        {
            // "Attempts to slew, move or enable tracking while parked must raise a ParkedException"; turning tracking
            // off must still work (ConformU).
            var off = await ctx.TryAsync(t => t.Tracking = false);
            ctx.Report("Parked.TrackingOff", "Tracking", off.Ok ? Outcome.Pass : Outcome.Fail,
                off.Ok ? "Tracking = false while parked: no error." : "Tracking = false while parked raised an error.",
                actual: off.ErrorText, ms: off.Ms, fix: "Turning tracking off on a parked mount must work.");
            var on = await ctx.ExpectErrorAsync("Parked.TrackingOn", "Tracking", "Tracking = true while parked",
                t => t.Tracking = true, AscomErrorKind.Parked, "the mount is parked", property: true);
            if (on.Ok) await ctx.TryAsync(t => t.Tracking = false);
        }

        var unpark = await ctx.TryAsync(t => t.Unpark(), ctx.SlewTimeout);
        var unparked = unpark.Ok && await ctx.WaitUntilAsync(t => !t.AtPark, ctx.SlewTimeout);
        ctx.Report("Unpark", "Unpark", unparked ? Outcome.Pass : Outcome.Fail,
            unparked ? "Unparked: AtPark false." : unpark.Ok ? "AtPark stayed true after Unpark." : "Unpark raised an error.",
            actual: unpark.ErrorText, ms: unpark.Ms, fix: "CanUnpark is true, so Unpark must work and AtPark go false.");
        var unparkAgain = await ctx.TryAsync(t => t.Unpark(), ctx.SlewTimeout);
        ctx.Report("Unpark.WhenUnparked", "Unpark", unparkAgain.Ok ? Outcome.Pass : Outcome.Fail,
            unparkAgain.Ok ? "Unpark while not parked: no error." : "Unpark while not parked raised an error.",
            actual: unparkAgain.ErrorText, fix: "Unpark on a mount that isn't parked must do nothing, without an error.");
    }

    private static async Task SetParkAsync(CheckContext ctx)
    {
        if (!ctx.Options.AllowSetPark)
        {
            ctx.Report("SetPark", "SetPark", Outcome.Skipped, "SetPark changes the saved park position; it is turned off for this run.");
            return;
        }
        if (!ctx.Caps.CanSetPark)
        {
            await ctx.ExpectNotImplementedAsync("SetPark.NotImplemented", "SetPark", "CanSetPark", t => t.SetPark());
            return;
        }
        var call = await ctx.TryAsync(t => t.SetPark());
        ctx.Report("SetPark", "SetPark", call.Ok ? Outcome.Pass : Outcome.Fail,
            call.Ok ? "SetPark worked (the park position is now where the mount points)." : "SetPark raised an error.",
            actual: call.ErrorText, ms: call.Ms, fix: "CanSetPark is true, so SetPark must work.");
    }
}
