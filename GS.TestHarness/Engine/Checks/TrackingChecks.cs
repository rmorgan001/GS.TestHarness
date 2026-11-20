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
/// Tracking measured over time (extended): with tracking on the position holds still; with it off the mount stays
/// put against the ground, so RA follows sidereal time; and the rate offsets move it at the rate set.
/// </summary>
public sealed class TrackingChecks : ICheckGroup
{
    public string Name => "Tracking";
    public string Description => "Extended: tracking on holds RA/Dec still, tracking off lets RA follow sidereal time, and RightAscensionRate/DeclinationRate move the mount at the rate set.";

    public string? SkipReason(CheckContext ctx)
        => !ctx.Options.Extended ? "extended checks are turned off for this run"
            : ctx.Fact("AtPark") && !ctx.Caps.CanUnpark ? "the mount is parked and can't unpark" : null;

    // RA in seconds of time; Dec in arc-seconds.
    private const double HoldOkSeconds = 1.0, HoldOkArcsec = 15;

    public async Task RunAsync(CheckContext ctx)
    {
        var c = ctx.Caps;
        if ((await ctx.TryAsync(t => t.AtPark)).Value)
        {
            ctx.Report("Prepare", "AtPark", Outcome.Skipped, "The mount is parked, so tracking can't be measured.");
            return;
        }
        var wasTracking = (await ctx.TryAsync(t => t.Tracking)).Value;
        var rate = await ctx.TryAsync(t => t.TrackingRate);
        try
        {
            if (c.CanSetTracking)
            {
                await ctx.TryAsync(t => t.TrackingRate = DriveRate.Sidereal);
                await ctx.TryAsync(t => t.Tracking = true);
            }
            // Drift on and off only when tracking itself is wanted (a ConformU run wants only its problem members).
            if (ctx.Wants("Tracking", "RightAscension", "TrackingRate"))
            {
                if ((await ctx.TryAsync(t => t.Tracking)).Value) await HoldsStillAsync(ctx);
                else ctx.Report("TrackingOn", "Tracking", Outcome.Skipped, "Tracking is off and can't be turned on.");

                if (c.CanSetTracking && (await ctx.TryAsync(t => t.Tracking = false)).Ok)
                {
                    await FollowsSkyAsync(ctx);
                    await ctx.TryAsync(t => t.Tracking = true);
                }
                else
                {
                    ctx.Report("TrackingOff", "Tracking", Outcome.Skipped, "Tracking can't be turned off (CanSetTracking is false).");
                }
            }

            if (!ctx.Wants("RightAscensionRate", "DeclinationRate")) { }
            else if ((c.CanSetRightAscensionRate || c.CanSetDeclinationRate) && ctx.Options.AllowMotion) await OffsetsAsync(ctx);
            else if (c.CanSetRightAscensionRate || c.CanSetDeclinationRate)
                ctx.Report("Offsets", "RightAscensionRate", Outcome.Skipped, "Rate offsets move the mount; motion is turned off for this run.");
        }
        finally
        {
            using var restoring = ctx.Restoring();
            if (c.CanSetRightAscensionRate) await ctx.TryAsync(t => t.RightAscensionRate = 0);
            if (c.CanSetDeclinationRate) await ctx.TryAsync(t => t.DeclinationRate = 0);
            if (rate.Ok) await ctx.TryAsync(t => t.TrackingRate = rate.Value);
            if (c.CanSetTracking) await ctx.TryAsync(t => t.Tracking = wasTracking);
        }
    }

    private static TimeSpan Span(CheckContext ctx) => TimeSpan.FromSeconds(Math.Max(1, ctx.Options.DriftSeconds));

    private static async Task<(double Ra, double Dec, double Lst, long Ticks)?> SampleAsync(CheckContext ctx)
    {
        var s = await ctx.TryAsync(t => (t.RightAscension, t.Declination, t.SiderealTime, Stopwatch.GetTimestamp()));
        return s.Ok ? s.Value : null;
    }

    private static async Task HoldsStillAsync(CheckContext ctx)
    {
        // Tracking was just turned on, perhaps straight after a slew: let the mount settle (reach sidereal rate, finish
        // any final correction) before measuring.
        await MotionChecks.SettledPositionAsync(ctx);
        var a = await SampleAsync(ctx);
        await Task.Delay(Span(ctx), ctx.Token);
        var b = await SampleAsync(ctx);
        if (a is null || b is null) return;
        var dRa = Math.Abs(Values.HoursDiff(b.Value.Ra, a.Value.Ra)) * 3600; // seconds of time
        var dDec = Math.Abs(b.Value.Dec - a.Value.Dec) * 3600;            // arc-seconds
        var outcome = dRa <= HoldOkSeconds && dDec <= HoldOkArcsec ? Outcome.Pass : dRa <= 4 && dDec <= 60 ? Outcome.Warning : Outcome.Fail;
        ctx.Report("TrackingOn", "Tracking", outcome,
            $"With tracking on (sidereal), RA moved {dRa:0.00} s and Dec {dDec:0.0}\" in {ctx.Options.DriftSeconds} s.",
            expected: $"RA within {HoldOkSeconds} s, Dec within {HoldOkArcsec}\"", actual: $"RA {dRa:0.00} s, Dec {dDec:0.0}\"",
            fix: "While tracking at sidereal rate RightAscension and Declination must stay on the target. If RA creeps, the " +
                 "tracking rate or the RA calculation (LST - hour angle) is off; if it moves by about 1 s per second, " +
                 "the reported RA isn't being compensated for tracking.",
            sourceMember: "RightAscension");
    }

    private static async Task FollowsSkyAsync(CheckContext ctx)
    {
        var a = await SampleAsync(ctx);
        await Task.Delay(Span(ctx), ctx.Token);
        var b = await SampleAsync(ctx);
        if (a is null || b is null) return;
        // Not tracking, the mount is fixed against the ground: hour angle (LST - RA) stays the same.
        var dRa = Values.HoursDiff(b.Value.Ra, a.Value.Ra) * 3600;
        var dLst = Values.HoursDiff(b.Value.Lst, a.Value.Lst) * 3600;
        var error = Math.Abs(dRa - dLst);
        var outcome = error <= 1.5 ? Outcome.Pass : error <= 5 ? Outcome.Warning : Outcome.Fail;
        ctx.Report("TrackingOff", "Tracking", outcome,
            $"With tracking off, RA changed {dRa:0.0} s while sidereal time advanced {dLst:0.0} s.",
            expected: $"RA follows sidereal time ({dLst:0.0} s)", actual: $"{dRa:0.0} s",
            fix: "With tracking off the mount points at a fixed hour angle, so RightAscension must increase with " +
                 "SiderealTime. A constant RA means the driver reports the last target instead of the mount's real position.",
            sourceMember: "RightAscension");
    }

    private static async Task OffsetsAsync(CheckContext ctx)
    {
        var c = ctx.Caps;
        const double raRate = 0.05;  // seconds of RA per sidereal second
        // Arc-seconds per SI second, towards the equator: at a pole a Dec offset has nowhere to go.
        var decRate = (await ctx.TryAsync(t => t.Declination)) is { Ok: true, Value: > 0 } ? -1.0 : 1.0;
        var raSet = c.CanSetRightAscensionRate && (await ctx.TryAsync(t => t.RightAscensionRate = raRate)).Ok;
        var decSet = c.CanSetDeclinationRate && (await ctx.TryAsync(t => t.DeclinationRate = decRate)).Ok;
        if (!raSet && !decSet) return;
        await Task.Delay(1000, ctx.Token); // as ConformU: let the mount reach the new rate before measuring
        var a = await SampleAsync(ctx);
        await Task.Delay(Span(ctx), ctx.Token);
        var b = await SampleAsync(ctx);
        if (c.CanSetRightAscensionRate) await ctx.TryAsync(t => t.RightAscensionRate = 0);
        if (c.CanSetDeclinationRate) await ctx.TryAsync(t => t.DeclinationRate = 0);
        if (a is null || b is null) return;
        var seconds = (double)(b.Value.Ticks - a.Value.Ticks) / Stopwatch.Frequency;

        if (raSet)
        {
            var expected = raRate * seconds * 1.0027379; // per sidereal second -> per SI second
            var actual = Values.HoursDiff(b.Value.Ra, a.Value.Ra) * 3600;
            Judge(ctx, "RightAscensionRate.Moves", "RightAscensionRate", expected, actual, "s of RA",
                "RightAscensionRate is in seconds of RA per sidereal second: setting 0.05 must move RA by about 0.05 s every second. " +
                "A result about 15 times too big or small usually means seconds of time and arc-seconds were mixed up.");
        }
        if (decSet)
        {
            var expected = decRate * seconds;
            var actual = (b.Value.Dec - a.Value.Dec) * 3600;
            Judge(ctx, "DeclinationRate.Moves", "DeclinationRate", expected, actual, "\"",
                $"DeclinationRate is in arc-seconds per SI second: setting {decRate:+0;-0} must move Declination by {decRate:+0;-0}\" every second.");
        }
    }

    private static void Judge(CheckContext ctx, string id, string member, double expected, double actual, string unit, string fix)
    {
        var ratio = Math.Abs(expected) < 1e-9 ? 0 : actual / expected;
        var outcome = ratio is >= 0.8 and <= 1.2 ? Outcome.Pass : ratio is >= 0.5 and <= 1.5 ? Outcome.Warning : Outcome.Fail;
        ctx.Report(id, member, outcome, $"{member}: the mount moved {actual:0.0}{unit} where {expected:0.0}{unit} was expected.",
            expected: $"{expected:0.0}{unit}", actual: $"{actual:0.0}{unit}", fix: fix);
    }
}
