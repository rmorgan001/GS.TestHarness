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

/// <summary>Reading every property: implemented when it must be, with a value in range.</summary>
public sealed class PropertyChecks : ICheckGroup
{
    public string Name => "Properties";
    public string Description => "Reads every property, checks it is implemented when required and its value is in range and consistent.";
    public string? SkipReason(CheckContext ctx) => null;

    public async Task RunAsync(CheckContext ctx)
    {
        var c = ctx.Caps;

        await ctx.ReadOptionalAsync("AlignmentMode", t => t.AlignmentMode,
            v => Enum.IsDefined(v) ? null : "it isn't one of algAltAz, algPolar, algGermanPolar");
        await ctx.ReadOptionalAsync("Altitude", t => t.Altitude, v => v is < -90 or > 90 ? "altitude is -90 to 90 degrees" : null);
        await ctx.ReadOptionalAsync("Azimuth", t => t.Azimuth, v => v is < 0 or >= 360 ? "azimuth is 0 up to 360 degrees" : null);
        await ctx.ReadOptionalAsync("ApertureArea", t => t.ApertureArea, v => v < 0 ? "an area can't be negative" : null);
        await ctx.ReadOptionalAsync("ApertureDiameter", t => t.ApertureDiameter, v => v < 0 ? "a diameter can't be negative" : null);
        await ctx.ReadOptionalAsync("FocalLength", t => t.FocalLength, v => v < 0 ? "a focal length can't be negative" : null);
        await ctx.ReadMandatoryAsync("AtHome", t => t.AtHome);
        var atPark = await ctx.ReadMandatoryAsync("AtPark", t => t.AtPark);
        ctx.Facts["AtPark"] = atPark is { Ok: true, Value: true };

        var dec = await ctx.ReadMandatoryAsync("Declination", t => t.Declination, v => v is < -90 or > 90 ? "declination is -90 to 90 degrees" : null);
        var ra = await ctx.ReadMandatoryAsync("RightAscension", t => t.RightAscension, v => v is < 0 or >= 24 ? "right ascension is 0 up to 24 hours" : null);
        foreach (var (member, capability, can, read) in new (string, string, bool, Func<ITelescopeV4, double>)[]
                 {
                     ("DeclinationRate", "CanSetDeclinationRate", c.CanSetDeclinationRate, t => t.DeclinationRate),
                     ("RightAscensionRate", "CanSetRightAscensionRate", c.CanSetRightAscensionRate, t => t.RightAscensionRate)
                 })
        {
            var offset = await ctx.ReadMandatoryAsync(member, read);
            // As ConformU: an offset that can't be set can only be 0. (The documentation doesn't say so outright, hence a warning.)
            if (!can && offset is { Ok: true } && offset.Value != 0)
                ctx.Report($"{member}.Zero", member, Outcome.Warning, $"{member} is {CheckContext.Show(offset.Value)}, but {capability} is false.",
                    expected: "0", actual: CheckContext.Show(offset.Value),
                    fix: $"{capability} is false, so no client can have set an offset: {member} should read 0 (ConformU reports " +
                         "anything else as an issue).");
        }
        await ctx.ReadMandatoryAsync("DoesRefraction", t => t.DoesRefraction);
        await ctx.ReadMandatoryAsync("EquatorialSystem", t => t.EquatorialSystem,
            v => Enum.IsDefined(v) ? null : "it isn't one of the EquatorialCoordinateType values");

        await ctx.ReadRequiredWhenAsync("GuideRateDeclination", c.CanSetGuideRates, "CanSetGuideRates is true",
            t => t.GuideRateDeclination, v => v < 0 ? "a rate can't be negative" : null);
        await ctx.ReadRequiredWhenAsync("GuideRateRightAscension", c.CanSetGuideRates, "CanSetGuideRates is true",
            t => t.GuideRateRightAscension, v => v < 0 ? "a rate can't be negative" : null);

        if (c.CanPulseGuide)
            await ctx.ReadMandatoryAsync("IsPulseGuiding", t => t.IsPulseGuiding);
        else
            await ctx.ExpectNotImplementedAsync("IsPulseGuiding.NotImplemented", "IsPulseGuiding", "CanPulseGuide",
                t => _ = t.IsPulseGuiding, property: true);

        var sideAllowed = new[] { PointingState.Normal, PointingState.ThroughThePole, PointingState.Unknown };
        await ctx.ReadRequiredWhenAsync("SideOfPier", c.CanSetPierSide, "CanSetPierSide is true", t => t.SideOfPier,
            v => sideAllowed.Contains(v) ? null : "it isn't pierEast, pierWest or pierUnknown");

        var lst = await ctx.ReadMandatoryAsync("SiderealTime", t => t.SiderealTime, v => v is < 0 or >= 24 ? "sidereal time is 0 up to 24 hours" : null);
        if (lst.Ok && c.SiteLongitude is { } lon)
        {
            var expected = Values.LocalSiderealTime(DateTime.UtcNow, lon);
            var diffSeconds = Math.Abs(Values.HoursDiff(lst.Value, expected)) * 3600;
            // As ConformU: within a second or two is right (this is mean sidereal time; ASCOM asks for apparent, which
            // differs by up to ~1.2 s), up to an hour is worth knowing, more than an hour is wrong.
            var outcome = diffSeconds > 3600 ? Outcome.Fail : diffSeconds > 2 ? Outcome.Info : Outcome.Pass;
            ctx.Report("SiderealTime.Value", "SiderealTime", outcome,
                outcome == Outcome.Pass
                    ? $"SiderealTime agrees with this computer's clock and SiteLongitude (within {diffSeconds:0.0} s)."
                    : $"SiderealTime is {diffSeconds:0} s away from the sidereal time for SiteLongitude {lon:0.####}.",
                expected: $"{CheckContext.Show(expected)} h", actual: $"{CheckContext.Show(lst.Value)} h",
                fix: diffSeconds > 3600
                    ? "SiderealTime should be the local sidereal time at SiteLongitude. An hour or more out usually means the " +
                      "longitude's sign is wrong (east is positive), or a time zone has been applied to UTC."
                    : "SiderealTime is a little out: check the mount's (or simulator's) clock and that the sidereal time " +
                      "formula includes the longitude.");
        }

        await ctx.ReadOptionalAsync("SiteElevation", t => t.SiteElevation, v => v is < -300 or > 10000 ? "elevation is -300 to 10000 m" : null);
        await ctx.ReadOptionalAsync("SiteLatitude", t => t.SiteLatitude, v => v is < -90 or > 90 ? "latitude is -90 to 90 degrees" : null);
        await ctx.ReadOptionalAsync("SiteLongitude", t => t.SiteLongitude, v => v is < -180 or > 180 ? "longitude is -180 to 180 degrees" : null);
        await ctx.ReadMandatoryAsync("Slewing", t => t.Slewing);
        await ctx.ReadOptionalAsync("SlewSettleTime", t => t.SlewSettleTime, v => v < 0 ? "a settle time can't be negative" : null);

        // Before any client sets them, the targets must report that they have no value.
        foreach (var (member, read) in new (string, Func<ITelescopeV4, double>)[]
                 { ("TargetDeclination", t => t.TargetDeclination), ("TargetRightAscension", t => t.TargetRightAscension) })
        {
            var call = await ctx.TryAsync(read);
            var required = c.CanSlew || c.CanSlewAsync || c.CanSync;
            // The ITelescopeV4 documentation names InvalidOperationException; ConformU also accepts ValueNotSetException.
            if (call.Kind is AscomErrorKind.InvalidOperation or AscomErrorKind.ValueNotSet)
                ctx.Report($"{member}.NotSet", member, Outcome.Pass, $"{member} reports {call.Kind} before it has been set.", ms: call.Ms);
            else if (call.Ok)
                ctx.Report($"{member}.NotSet", member, Outcome.Info,
                    $"{member} = {CheckContext.Show(call.Value)}: it was probably set by an earlier client or run, so " +
                    "\"not set\" can't be checked. Restart the server to check it.", ms: call.Ms);
            else if (call.Kind == AscomErrorKind.NotImplemented && !required)
                ctx.Report($"{member}.NotSet", member, Outcome.Info, $"{member} is not implemented (allowed: the mount can't slew or sync).", ms: call.Ms);
            else
                ctx.Report($"{member}.NotSet", member, Outcome.Fail, $"{member} before it is set raised the wrong error.",
                    expected: AscomError.ExpectedName(AscomErrorKind.InvalidOperation) + " (or ValueNotSetException, 0x402)",
                    actual: call.ErrorText, ms: call.Ms,
                    fix: $"Until a client sets {member}, reading it must throw InvalidOperationException (\"If the value is " +
                         "read before being set for the first time\"), not return a default or raise another error.");
        }

        var tracking = await ctx.ReadMandatoryAsync("Tracking", t => t.Tracking);
        ctx.Facts["Tracking"] = tracking.Ok ? tracking.Value : null;
        var rate = await ctx.ReadMandatoryAsync("TrackingRate", t => t.TrackingRate,
            v => Enum.IsDefined(v) ? null : "it isn't one of driveSidereal, driveLunar, driveSolar, driveKing");
        var rates = await ctx.ReadMandatoryAsync("TrackingRates", t => Values.Rates(t.TrackingRates),
            v => v is null ? "it returned null"
                : v.Count == 0 ? "it is empty"
                : !v.Contains(DriveRate.Sidereal) ? "it doesn't include driveSidereal, which every mount must support"
                : v.Distinct().Count() != v.Count ? "it lists a rate more than once" : null);
        if (rate.Ok && rates is { Ok: true, Value: { } list } && !list.Contains(rate.Value))
            ctx.Report("TrackingRates.HasCurrent", "TrackingRates", Outcome.Fail,
                $"TrackingRate is {rate.Value}, which isn't in TrackingRates.", expected: $"TrackingRates includes {rate.Value}",
                actual: CheckContext.Show(list), fix: "TrackingRates must list every rate TrackingRate can be, including the current one.");
        ctx.Facts["TrackingRates"] = rates.Value;

        var utc = await ctx.ReadMandatoryAsync("UTCDate", t => t.UTCDate);
        if (utc.Ok)
        {
            // UTCDate is UTC by definition. A COM DATE arrives with Kind Unspecified, which ToUniversalTime would
            // take for local time; only a value marked Local is converted.
            var value = utc.Value.Kind == DateTimeKind.Local ? utc.Value.ToUniversalTime() : utc.Value;
            var diff = Math.Abs((value - DateTime.UtcNow).TotalSeconds);
            var bigOffset = Math.Abs(Math.Round(diff / 1800) * 1800 - diff) < 60 && diff >= 1800;
            ctx.Report("UTCDate.Value", "UTCDate", diff <= 10 ? Outcome.Pass : Outcome.Warning,
                diff <= 10 ? $"UTCDate is within {diff:0.0} s of this computer's clock." : $"UTCDate is {diff:0} s away from this computer's clock.",
                expected: CheckContext.Show(DateTime.UtcNow), actual: CheckContext.Show(utc.Value),
                fix: bigOffset
                    ? "The difference is a whole number of half hours: UTCDate is probably returning local time. Return " +
                      "DateTime.UtcNow (or the mount's clock converted to UTC)."
                    : "Check the mount's (or simulator's) clock; UTCDate should be the time in UTC.");
        }

        // Consistency between the position properties.
        if (ra.Ok && dec.Ok && lst.Ok)
            ctx.Facts["Position"] = (ra.Value, dec.Value);
        await AltAzAgreesAsync(ctx);
    }

    /// <summary>
    /// Altitude and Azimuth must be the same place as RightAscension and Declination, converted with SiderealTime and
    /// SiteLatitude. Allows half a degree (refraction near the horizon, J2000 against topocentric).
    /// </summary>
    private static async Task AltAzAgreesAsync(CheckContext ctx)
    {
        var read = await ctx.TryAsync(t => (t.RightAscension, t.Declination, t.SiderealTime, t.SiteLatitude, t.Altitude, t.Azimuth));
        if (!read.Ok) return; // Altitude/Azimuth (or the site) not implemented: nothing to compare
        var (ra, dec, lst, lat, alt, az) = read.Value;
        double R(double deg) => deg * Math.PI / 180;
        double D(double rad) => rad * 180 / Math.PI;
        var ha = R(Values.HoursDiff(lst, ra) * 15);
        var sinAlt = Math.Sin(R(dec)) * Math.Sin(R(lat)) + Math.Cos(R(dec)) * Math.Cos(R(lat)) * Math.Cos(ha);
        var calcAlt = D(Math.Asin(Math.Clamp(sinAlt, -1, 1)));
        var calcAz = (D(Math.Atan2(-Math.Sin(ha) * Math.Cos(R(dec)),
            Math.Sin(R(dec)) * Math.Cos(R(lat)) - Math.Cos(R(dec)) * Math.Sin(R(lat)) * Math.Cos(ha))) + 360) % 360;
        var sep = Values.AltAzSeparation(alt, az, calcAlt, calcAz);
        var outcome = sep <= 0.5 ? Outcome.Pass : sep <= 2 ? Outcome.Warning : Outcome.Fail;
        var flipped = Values.AltAzSeparation(alt, (az + 180) % 360, calcAlt, calcAz) <= 0.5;
        ctx.Report("AltAz.Consistent", "Azimuth", outcome,
            outcome == Outcome.Pass ? $"Altitude/Azimuth agree with RA/Dec, sidereal time and latitude (within {sep * 60:0}')."
                : $"Altitude/Azimuth are {sep:0.00}° from the position RA/Dec give.",
            expected: $"alt {calcAlt:0.00}°, az {calcAz:0.00}°", actual: $"alt {alt:0.00}°, az {az:0.00}°",
            fix: flipped ? "Azimuth is 180° out: ASCOM azimuth is measured from north through east (north = 0, east = 90)."
                : "Altitude/Azimuth and RA/Dec must describe the same place. Check the conversion's latitude sign, the hour " +
                  "angle (LST - RA), and that both come from the same moment.",
            sourceMember: "Azimuth");
    }
}
