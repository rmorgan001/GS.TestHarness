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

using System.Globalization;
using System.Text.Json;

namespace GS.TestHarness.Engine.Checks;

/// <summary>A set of related checks, run in order by the <see cref="TestRunner"/>.</summary>
public interface ICheckGroup
{
    /// <summary>Short and stable: the first part of each finding's ID.</summary>
    string Name { get; }

    /// <summary>What the group checks, for the UI.</summary>
    string Description { get; }

    /// <summary>Whether to run it for this target and these options; the reason it doesn't, if not.</summary>
    string? SkipReason(CheckContext ctx);

    Task RunAsync(CheckContext ctx);
}

/// <summary>Raised by a check when the rest of the run can't go on (the mount wouldn't connect).</summary>
public sealed class StopRunException(string message) : Exception(message);

/// <summary>
/// Raised by <see cref="CheckContext.TryAsync{T}"/> once the device can no longer be reached (its COM server
/// died, the Alpaca server went away): the run stops, keeping the findings so far.
/// </summary>
public sealed class CommunicationLostException(string group, string message, string diagnostics) : Exception(message)
{
    /// <summary>The group that was running when it happened.</summary>
    public string Group { get; } = group;

    /// <summary>The last call's error and the check call's error, for the report.</summary>
    public string Diagnostics { get; } = diagnostics;
}

/// <summary>Conversions for values that arrive untyped (DeviceState values, Alpaca JSON).</summary>
public static class Values
{
    public static double? ToDouble(object? value) => value switch
    {
        null => null,
        double d => d,
        float f => f,
        int i => i,
        long l => l,
        short s => s,
        JsonElement { ValueKind: JsonValueKind.Number } e => e.GetDouble(),
        JsonElement { ValueKind: JsonValueKind.String } e when double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        IConvertible c => TryConvert(c),
        _ => null
    };

    private static double? TryConvert(IConvertible c)
    {
        try { return c.ToDouble(CultureInfo.InvariantCulture); }
        catch { return null; }
    }

    public static bool? ToBool(object? value) => value switch
    {
        null => null,
        bool b => b,
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => false,
        string s when bool.TryParse(s, out var b) => b,
        _ => null
    };

    /// <summary>AxisRates as (Minimum, Maximum) pairs; null for a null collection.</summary>
    public static List<(double Minimum, double Maximum)>? Rates(ASCOM.Common.DeviceInterfaces.IAxisRates? rates)
    {
        if (rates is null) return null;
        var list = new List<(double, double)>();
        foreach (var r in rates)
            if (r is ASCOM.Common.DeviceInterfaces.IRate rate) list.Add((rate.Minimum, rate.Maximum));
        return list;
    }

    /// <summary>TrackingRates as a list; null for a null collection.</summary>
    public static List<ASCOM.Common.DeviceInterfaces.DriveRate>? Rates(ASCOM.Common.DeviceInterfaces.ITrackingRates? rates)
    {
        if (rates is null) return null;
        var list = new List<ASCOM.Common.DeviceInterfaces.DriveRate>();
        foreach (var r in rates)
            if (r is ASCOM.Common.DeviceInterfaces.DriveRate rate) list.Add(rate);
        return list;
    }

    /// <summary>The difference between two angles in hours, wrapped to -12..12.</summary>
    public static double HoursDiff(double a, double b)
    {
        var d = (a - b) % 24;
        if (d > 12) d -= 24;
        if (d < -12) d += 24;
        return d;
    }

    /// <summary>The difference between two angles in degrees, wrapped to -180..180.</summary>
    public static double DegreesDiff(double a, double b)
    {
        var d = (a - b) % 360;
        if (d > 180) d -= 360;
        if (d < -180) d += 360;
        return d;
    }

    public static double Wrap24(double hours) => ((hours % 24) + 24) % 24;

    /// <summary>Local mean sidereal time (hours) at <paramref name="longitude"/> (degrees, east positive).</summary>
    public static double LocalSiderealTime(DateTime utc, double longitude)
    {
        var jd = utc.ToOADate() + 2415018.5;
        var d = jd - 2451545.0;
        var t = d / 36525.0;
        var gmst = 280.46061837 + 360.98564736629 * d + 0.000387933 * t * t - t * t * t / 38710000.0;
        return Wrap24((gmst + longitude) / 15.0);
    }

    /// <summary>The angle in degrees between two equatorial positions (RA in hours, Dec in degrees).</summary>
    public static double Separation(double ra1, double dec1, double ra2, double dec2)
    {
        double R(double deg) => deg * Math.PI / 180;
        var cos = Math.Sin(R(dec1)) * Math.Sin(R(dec2)) + Math.Cos(R(dec1)) * Math.Cos(R(dec2)) * Math.Cos(R((ra1 - ra2) * 15));
        return Math.Acos(Math.Clamp(cos, -1, 1)) * 180 / Math.PI;
    }

    /// <summary>The angle in degrees between two horizontal positions.</summary>
    public static double AltAzSeparation(double alt1, double az1, double alt2, double az2)
        => Separation(az1 / 15, alt1, az2 / 15, alt2);
}
