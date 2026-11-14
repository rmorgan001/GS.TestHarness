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

/// <summary>
/// Writing properties: each is written back with the value it has (so nothing changes), refused when the mount
/// can't change it, and invalid values are refused. Anything a faulty server accepted is put back.
/// </summary>
public sealed class SettingsChecks : ICheckGroup
{
    public string Name => "Settings";
    public string Description => "Writes each property back unchanged, tries invalid values (which must be refused) and checks targets, rates and tracking rates.";

    public string? SkipReason(CheckContext ctx) => ctx.Options.AllowSettingsWrites ? null : "settings writes are turned off for this run";

    public async Task RunAsync(CheckContext ctx)
    {
        var c = ctx.Caps;
        var restore = new List<(string What, Action<ITelescopeV4> Undo)>();
        try
        {
            await DoubleSettingAsync(ctx, restore, "SiteLatitude", t => t.SiteLatitude, (t, v) => t.SiteLatitude = v, [-91, 91], "latitude is -90 to 90 degrees");
            await DoubleSettingAsync(ctx, restore, "SiteLongitude", t => t.SiteLongitude, (t, v) => t.SiteLongitude = v, [-181, 181], "longitude is -180 to 180 degrees");
            await DoubleSettingAsync(ctx, restore, "SiteElevation", t => t.SiteElevation, (t, v) => t.SiteElevation = v, [-301, 10001], "elevation is -300 to 10000 m");
            await DoubleSettingAsync(ctx, restore, "SlewSettleTime", t => t.SlewSettleTime, (t, v) => t.SlewSettleTime = (short)v, [-1], "a settle time can't be negative");

            if (c.CanSetGuideRates)
            {
                await DoubleSettingAsync(ctx, restore, "GuideRateRightAscension", t => t.GuideRateRightAscension, (t, v) => t.GuideRateRightAscension = v, [-1], "a guide rate can't be negative");
                await DoubleSettingAsync(ctx, restore, "GuideRateDeclination", t => t.GuideRateDeclination, (t, v) => t.GuideRateDeclination = v, [-1], "a guide rate can't be negative");
            }
            else
            {
                await NotImplementedWriteAsync(ctx, restore, "GuideRateRightAscension", "CanSetGuideRates", t => t.GuideRateRightAscension, (t, v) => t.GuideRateRightAscension = v);
                await NotImplementedWriteAsync(ctx, restore, "GuideRateDeclination", "CanSetGuideRates", t => t.GuideRateDeclination, (t, v) => t.GuideRateDeclination = v);
            }

            // DoesRefraction: written back unchanged.
            var refraction = await ctx.TryAsync(t => t.DoesRefraction);
            if (refraction.Ok)
            {
                var write = await ctx.TryAsync(t => t.DoesRefraction = refraction.Value);
                ctx.Report("DoesRefraction.Write", "DoesRefraction",
                    write.Ok || write.Kind == AscomErrorKind.NotImplemented ? (write.Ok ? Outcome.Pass : Outcome.Info) : Outcome.Fail,
                    write.Ok ? "DoesRefraction written back unchanged." : write.Kind == AscomErrorKind.NotImplemented
                        ? "DoesRefraction can be read but not written (allowed)." : "Writing DoesRefraction raised an error.",
                    actual: write.ErrorText, ms: write.Ms,
                    fix: "Writing DoesRefraction must either work or throw PropertyNotImplementedException.");
            }

            // Rates that offset tracking: written back when they can be set, refused when they can't.
            foreach (var (member, cap, can, read, write) in new (string, string, bool, Func<ITelescopeV4, double>, Action<ITelescopeV4, double>)[]
                     {
                         ("RightAscensionRate", "CanSetRightAscensionRate", c.CanSetRightAscensionRate, t => t.RightAscensionRate, (t, v) => t.RightAscensionRate = v),
                         ("DeclinationRate", "CanSetDeclinationRate", c.CanSetDeclinationRate, t => t.DeclinationRate, (t, v) => t.DeclinationRate = v)
                     })
            {
                if (can)
                {
                    var current = await ctx.TryAsync(read);
                    if (!current.Ok) continue;
                    var call = await ctx.TryAsync(t => write(t, current.Value));
                    ctx.Report($"{member}.Write", member, call.Ok ? Outcome.Pass : Outcome.Fail,
                        call.Ok ? $"{member} written back unchanged." : $"Writing {member} raised an error, though {cap} is true.",
                        actual: call.ErrorText, ms: call.Ms,
                        fix: $"{cap} is true, so {member} must accept a value (at least while TrackingRate is driveSidereal).");
                }
                else
                {
                    await NotImplementedWriteAsync(ctx, restore, member, cap, read, write);
                }
            }

            // Tracking: written back unchanged when it can be set, refused when it can't.
            var tracking = await ctx.TryAsync(t => t.Tracking);
            if (tracking.Ok)
            {
                if (c.CanSetTracking)
                {
                    var call = await ctx.TryAsync(t => t.Tracking = tracking.Value);
                    ctx.Report("Tracking.Write", "Tracking", call.Ok ? Outcome.Pass : Outcome.Fail,
                        call.Ok ? "Tracking written back unchanged." : "Writing Tracking raised an error, though CanSetTracking is true.",
                        actual: call.ErrorText, ms: call.Ms, fix: "CanSetTracking is true, so setting Tracking must work.");
                }
                else
                {
                    await ctx.ExpectNotImplementedAsync("Tracking.NotImplemented", "Tracking", "CanSetTracking",
                        t => t.Tracking = tracking.Value, property: true);
                }
            }

            // SideOfPier: a write would flip the mount, so it is only checked here when it can't be set.
            if (!c.CanSetPierSide)
            {
                var side = await ctx.TryAsync(t => t.SideOfPier);
                if (side.Ok && side.Value != PointingState.Unknown)
                    await ctx.ExpectNotImplementedAsync("SideOfPier.NotImplemented", "SideOfPier", "CanSetPierSide",
                        t => t.SideOfPier = side.Value, property: true);
            }

            await TrackingRateAsync(ctx, restore);
            await UtcDateAsync(ctx);
            await TargetsAsync(ctx);
        }
        finally
        {
            foreach (var (what, undo) in restore)
            {
                var call = await ctx.TryAsync(undo);
                if (!call.Ok)
                    ctx.Report("Restore", what, Outcome.Error, $"Could not put {what} back after an invalid value was accepted.",
                        actual: call.ErrorText, fix: $"Check {what} on the mount and set it back by hand.");
            }
        }
    }

    /// <summary>Writes the current value back, then each invalid value (which must be refused).</summary>
    private static async Task DoubleSettingAsync(CheckContext ctx, List<(string, Action<ITelescopeV4>)> restore, string member,
        Func<ITelescopeV4, double> read, Action<ITelescopeV4, double> write, double[] invalid, string rule)
    {
        var current = await ctx.TryAsync(read);
        if (current.Kind == AscomErrorKind.NotImplemented)
        {
            // Can't be read: writing must be refused the same way (or work, if it's write-only, which is odd).
            var call = await ctx.TryAsync(t => write(t, 0));
            ctx.Report($"{member}.Write", member, call.Kind == AscomErrorKind.NotImplemented ? Outcome.Pass : Outcome.Warning,
                call.Kind == AscomErrorKind.NotImplemented ? $"{member} is not implemented for reading or writing."
                    : $"{member} can't be read, but writing it gave: {call.ErrorText}.",
                actual: call.ErrorText, ms: call.Ms,
                fix: $"{member} can't be read, so writing it should throw PropertyNotImplementedException too.");
            return;
        }
        if (!current.Ok) return;
        var value = current.Value;

        var back = await ctx.TryAsync(t => write(t, value));
        if (back.Kind == AscomErrorKind.NotImplemented)
        {
            ctx.Report($"{member}.Write", member, Outcome.Info, $"{member} is read-only (allowed).", ms: back.Ms);
            return;
        }
        ctx.Report($"{member}.Write", member, back.Ok ? Outcome.Pass : Outcome.Fail,
            back.Ok ? $"{member} written back unchanged ({CheckContext.Show(value)})." : $"Writing {member} back with its own value failed.",
            expected: "no error", actual: back.ErrorText, ms: back.Ms,
            fix: $"Writing the value {member} already has must be accepted.");

        if (back.Ok)
        {
            // Only now is it known to be writable: put it back at the end, in case an invalid value is accepted.
            restore.Insert(0, (member, t => write(t, value)));
            var reread = await ctx.TryAsync(read);
            if (reread.Ok && Math.Abs(reread.Value - value) > 1e-6)
                ctx.Report($"{member}.ReadBack", member, Outcome.Fail, $"{member} changed when written back with its own value.",
                    expected: CheckContext.Show(value), actual: CheckContext.Show(reread.Value),
                    fix: $"{member} should store exactly what was written (look for rounding or unit conversion in the setter).");
        }

        foreach (var bad in invalid)
            await ctx.ExpectErrorAsync($"{member}.Invalid{(bad < 0 ? "Low" : "High")}", member, $"{member} = {CheckContext.Show(bad)}",
                t => write(t, bad), AscomErrorKind.InvalidValue, rule, property: true);
    }

    /// <summary>A write the capability says isn't possible: must raise not implemented. Written with the current value.</summary>
    private static async Task NotImplementedWriteAsync(CheckContext ctx, List<(string, Action<ITelescopeV4>)> restore, string member,
        string capability, Func<ITelescopeV4, double> read, Action<ITelescopeV4, double> write)
    {
        var current = await ctx.TryAsync(read);
        // The current value, so a server that wrongly accepts the write changes nothing.
        var value = current.Ok ? current.Value : 0;
        await ctx.ExpectNotImplementedAsync($"{member}.NotImplemented", member, capability, t => write(t, value), property: true);
    }

    private static async Task TrackingRateAsync(CheckContext ctx, List<(string, Action<ITelescopeV4>)> restore)
    {
        var current = await ctx.TryAsync(t => t.TrackingRate);
        if (!current.Ok) return;
        var original = current.Value;
        restore.Insert(0, ("TrackingRate", t => t.TrackingRate = original));

        var rates = ctx.Facts.GetValueOrDefault("TrackingRates") as List<DriveRate> ?? [original];
        foreach (var rate in rates)
        {
            var set = await ctx.TryAsync(t => t.TrackingRate = rate);
            var read = set.Ok ? await ctx.TryAsync(t => t.TrackingRate) : null;
            var ok = set.Ok && read is { Ok: true } && read.Value == rate;
            ctx.Report($"TrackingRate.Set{rate}", "TrackingRate", ok ? Outcome.Pass : Outcome.Fail,
                ok ? $"TrackingRate set to {rate} and read back." : $"TrackingRate = {rate} (listed in TrackingRates) didn't take.",
                expected: rate.ToString(), actual: set.Ok ? read?.Ok == true ? read.Value.ToString() : read?.ErrorText : set.ErrorText,
                ms: set.Ms, fix: "Every rate in TrackingRates must be accepted by TrackingRate and then read back.");
        }
        await ctx.ExpectErrorAsync("TrackingRate.Invalid", "TrackingRate", "TrackingRate = 99", t => t.TrackingRate = (DriveRate)99,
            AscomErrorKind.InvalidValue, "99 isn't a DriveRate", property: true);
        await ctx.TryAsync(t => t.TrackingRate = original);
    }

    /// <summary>As ConformU: write UTCDate an hour ahead, read it back, then set it to the real time again.</summary>
    private static async Task UtcDateAsync(CheckContext ctx)
    {
        var ahead = DateTime.UtcNow.AddHours(1);
        var write = await ctx.TryAsync(t => t.UTCDate = ahead);
        if (write.Kind == AscomErrorKind.NotImplemented)
        {
            ctx.Report("UTCDate.Write", "UTCDate", Outcome.Info, "UTCDate can be read but not written (allowed).", ms: write.Ms);
            return;
        }
        try
        {
            var read = write.Ok ? await ctx.TryAsync(t => t.UTCDate) : null;
            var diff = read is { Ok: true } ? Math.Abs((read.Value - ahead).TotalSeconds) : double.NaN;
            var ok = write.Ok && diff <= 5;
            ctx.Report("UTCDate.Write", "UTCDate", ok ? Outcome.Pass : Outcome.Fail,
                ok ? "UTCDate set an hour ahead and read back." : write.Ok ? "UTCDate didn't read back what was written." : "Writing UTCDate raised an error.",
                expected: CheckContext.Show(ahead), actual: !write.Ok ? write.ErrorText : read is { Ok: true } ? CheckContext.Show(read.Value) : read?.ErrorText,
                ms: write.Ms,
                fix: "Writing UTCDate must either work (and read back, as UTC) or throw PropertyNotImplementedException.");
        }
        finally
        {
            await ctx.TryAsync(t => t.UTCDate = DateTime.UtcNow);
        }
    }

    private static async Task TargetsAsync(CheckContext ctx)
    {
        var position = ctx.Facts.GetValueOrDefault("Position") as (double Ra, double Dec)?;
        var (ra, dec) = position ?? (12.0, 0.0);

        foreach (var (member, value, set, get) in new (string, double, Action<ITelescopeV4, double>, Func<ITelescopeV4, double>)[]
                 {
                     ("TargetRightAscension", ra, (t, v) => t.TargetRightAscension = v, t => t.TargetRightAscension),
                     ("TargetDeclination", dec, (t, v) => t.TargetDeclination = v, t => t.TargetDeclination)
                 })
        {
            var write = await ctx.TryAsync(t => set(t, value));
            if (write.Kind == AscomErrorKind.NotImplemented && !(ctx.Caps.CanSlew || ctx.Caps.CanSlewAsync || ctx.Caps.CanSync))
            {
                ctx.Report($"{member}.Write", member, Outcome.Info, $"{member} is not implemented (allowed: the mount can't slew or sync).");
                continue;
            }
            var read = write.Ok ? await ctx.TryAsync(get) : null;
            var ok = write.Ok && read is { Ok: true } && Math.Abs(read.Value - value) < 1e-5;
            ctx.Report($"{member}.Write", member, ok ? Outcome.Pass : Outcome.Fail,
                ok ? $"{member} set to {CheckContext.Show(value)} and read back." : $"{member} didn't read back what was written.",
                expected: CheckContext.Show(value),
                actual: !write.Ok ? write.ErrorText : read is { Ok: true } ? CheckContext.Show(read.Value) : read?.ErrorText,
                ms: write.Ms, fix: $"{member} must store the value written and return it unchanged.");
        }

        await ctx.ExpectErrorAsync("TargetRightAscension.InvalidLow", "TargetRightAscension", "TargetRightAscension = -1",
            t => t.TargetRightAscension = -1, AscomErrorKind.InvalidValue, "right ascension is 0 up to 24 hours", property: true);
        await ctx.ExpectErrorAsync("TargetRightAscension.InvalidHigh", "TargetRightAscension", "TargetRightAscension = 25",
            t => t.TargetRightAscension = 25, AscomErrorKind.InvalidValue, "right ascension is 0 up to 24 hours", property: true);
        await ctx.ExpectErrorAsync("TargetDeclination.InvalidLow", "TargetDeclination", "TargetDeclination = -91",
            t => t.TargetDeclination = -91, AscomErrorKind.InvalidValue, "declination is -90 to 90 degrees", property: true);
        await ctx.ExpectErrorAsync("TargetDeclination.InvalidHigh", "TargetDeclination", "TargetDeclination = 91",
            t => t.TargetDeclination = 91, AscomErrorKind.InvalidValue, "declination is -90 to 90 degrees", property: true);
    }
}
