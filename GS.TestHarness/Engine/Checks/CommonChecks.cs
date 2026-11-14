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

/// <summary>The members every ASCOM device has: SupportedActions, Action and DeviceState.</summary>
public sealed class CommonChecks : ICheckGroup
{
    public string Name => "Common";
    public string Description => "SupportedActions, Action, Connecting and DeviceState (checked against the individual properties).";
    public string? SkipReason(CheckContext ctx) => null;

    /// <summary>The operational properties ITelescopeV4's DeviceState should hold (the ones the device implements).</summary>
    public static readonly string[] OperationalProperties =
    [
        "Altitude", "AtHome", "AtPark", "Azimuth", "Declination", "IsPulseGuiding", "RightAscension",
        "SideOfPier", "SiderealTime", "Slewing", "Tracking", "UTCDate"
    ];

    public async Task RunAsync(CheckContext ctx)
    {
        var actions = await ctx.ReadMandatoryAsync("SupportedActions", t => t.SupportedActions?.ToList(),
            v => v is null ? "it returned null; return an empty list instead"
                : v.Any(string.IsNullOrWhiteSpace) ? "it contains an empty action name" : null);

        var unknown = "GS.TestHarness.NoSuchAction" + Guid.NewGuid().ToString("N")[..6];
        await ctx.ExpectErrorAsync("Action.Unknown", "Action", $"Action(\"{unknown}\")", t => t.Action(unknown, ""),
            AscomErrorKind.ActionNotImplemented, "the action isn't one of SupportedActions",
            alsoAccepted: actions is { Ok: true, Value.Count: 0 } ? [AscomErrorKind.NotImplemented] : null);

        foreach (var (member, call) in new (string, Action<ITelescopeV4>)[]
                 {
                     ("CommandBlind", t => t.CommandBlind("", false)),
                     ("CommandBool", t => _ = t.CommandBool("", false)),
                     ("CommandString", t => _ = t.CommandString("", false))
                 })
        {
            var r = await ctx.TryAsync(call);
            ctx.Report($"{member}.Implemented", member, Outcome.Info,
                r.Kind == AscomErrorKind.NotImplemented ? $"{member} is not implemented (allowed: raw commands are optional)."
                    : r.Ok ? $"{member} is implemented (an empty command was accepted)."
                    : $"{member} is implemented (an empty command gave {r.Kind}).",
                actual: r.ErrorText, ms: r.Ms);
        }

        if (ctx.Identity.InterfaceVersion is < 4)
        {
            ctx.Report("DeviceState.Read", "DeviceState", Outcome.Skipped, "DeviceState is new in ITelescopeV4.");
            return;
        }

        await ctx.ReadMandatoryAsync("Connecting", t => t.Connecting, v => v ? "it is true although connecting has finished" : null,
            expected: "false");

        var state = await ctx.ReadMandatoryAsync("DeviceState", t => t.DeviceState?.ToList(),
            v => v is null ? "it returned null" : null);
        if (state is not { Ok: true, Value: { } list }) return;

        var duplicates = list.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
            ctx.Report("DeviceState.Duplicates", "DeviceState", Outcome.Fail, $"DeviceState names {string.Join(", ", duplicates)} more than once.",
                fix: "Each property may appear only once in DeviceState.");

        var byName = list.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => (object?)g.First().Value, StringComparer.OrdinalIgnoreCase);
        if (!byName.ContainsKey("TimeStamp"))
            ctx.Report("DeviceState.TimeStamp", "DeviceState", Outcome.Warning, "DeviceState has no TimeStamp.",
                expected: "a TimeStamp entry (the time the values were read)",
                fix: "Add a \"TimeStamp\" entry with the time (UTC) the state was read, as ASCOM recommends.");

        var missing = new List<string>();
        foreach (var property in OperationalProperties)
        {
            if (byName.ContainsKey(property)) continue;
            // Only a property the device implements has to be there.
            var read = await ctx.TryAsync<object?>(t => property switch
            {
                "Altitude" => t.Altitude, "AtHome" => t.AtHome, "AtPark" => t.AtPark, "Azimuth" => t.Azimuth,
                "Declination" => t.Declination, "IsPulseGuiding" => t.IsPulseGuiding, "RightAscension" => t.RightAscension,
                "SideOfPier" => t.SideOfPier, "SiderealTime" => t.SiderealTime, "Slewing" => t.Slewing,
                "Tracking" => t.Tracking, "UTCDate" => t.UTCDate, _ => null
            });
            if (read.Ok) missing.Add(property);
        }
        ctx.Report("DeviceState.Contents", "DeviceState", missing.Count == 0 ? Outcome.Pass : Outcome.Fail,
            missing.Count == 0
                ? $"DeviceState has every operational property the device implements ({list.Count} entries)."
                : $"DeviceState leaves out {string.Join(", ", missing)}, which the device implements.",
            expected: string.Join(", ", OperationalProperties), actual: string.Join(", ", byName.Keys),
            fix: "DeviceState must return every operational property the device implements, so a client can read the " +
                 "whole state in one call.");

        // The values must agree with the properties read one at a time (allowing for the time between reads).
        var mismatches = new List<string>();
        await CompareAsync(ctx, byName, "Tracking", t => t.Tracking, mismatches);
        await CompareAsync(ctx, byName, "AtPark", t => t.AtPark, mismatches);
        await CompareAsync(ctx, byName, "AtHome", t => t.AtHome, mismatches);
        await CompareNumberAsync(ctx, byName, "Declination", t => t.Declination, 0.05, false, mismatches);
        await CompareNumberAsync(ctx, byName, "RightAscension", t => t.RightAscension, 0.01, true, mismatches);
        await CompareNumberAsync(ctx, byName, "SiderealTime", t => t.SiderealTime, 0.01, true, mismatches);
        if (mismatches.Count > 0)
            ctx.Report("DeviceState.Values", "DeviceState", Outcome.Fail, "DeviceState disagrees with the properties.",
                actual: string.Join("; ", mismatches),
                fix: "Build DeviceState from the same values the properties return (read them together, at one time).");
        else
            ctx.Report("DeviceState.Values", "DeviceState", Outcome.Pass, "DeviceState agrees with the properties.");
    }

    private static async Task CompareAsync(CheckContext ctx, Dictionary<string, object?> state, string name,
        Func<ITelescopeV4, bool> read, List<string> mismatches)
    {
        if (!state.TryGetValue(name, out var raw)) return;
        var value = await ctx.TryAsync(read);
        if (!value.Ok) return;
        if (Values.ToBool(raw) is not { } b) mismatches.Add($"{name} is {CheckContext.Show(raw)}, not a boolean");
        else if (b != value.Value) mismatches.Add($"{name}: DeviceState {b}, property {value.Value}");
    }

    private static async Task CompareNumberAsync(CheckContext ctx, Dictionary<string, object?> state, string name,
        Func<ITelescopeV4, double> read, double tolerance, bool hours, List<string> mismatches)
    {
        if (!state.TryGetValue(name, out var raw)) return;
        var value = await ctx.TryAsync(read);
        if (!value.Ok) return;
        if (Values.ToDouble(raw) is not { } d) mismatches.Add($"{name} is {CheckContext.Show(raw)}, not a number");
        else if (Math.Abs(hours ? Values.HoursDiff(d, value.Value) : d - value.Value) > tolerance)
            mismatches.Add($"{name}: DeviceState {CheckContext.Show(d)}, property {CheckContext.Show(value.Value)}");
    }
}
