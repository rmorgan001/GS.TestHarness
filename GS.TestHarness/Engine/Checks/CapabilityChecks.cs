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

/// <summary>The Can* properties and CanMoveAxis: all mandatory. Their answers decide what later groups expect.</summary>
public sealed class CapabilityChecks : ICheckGroup
{
    public string Name => "Capabilities";
    public string Description => "Every Can* property and CanMoveAxis, which later checks rely on.";
    public string? SkipReason(CheckContext ctx) => null;

    public async Task RunAsync(CheckContext ctx)
    {
        var c = ctx.Caps;
        c.CanFindHome = await Can(ctx, "CanFindHome", t => t.CanFindHome);
        c.CanPark = await Can(ctx, "CanPark", t => t.CanPark);
        c.CanPulseGuide = await Can(ctx, "CanPulseGuide", t => t.CanPulseGuide);
        c.CanSetDeclinationRate = await Can(ctx, "CanSetDeclinationRate", t => t.CanSetDeclinationRate);
        c.CanSetGuideRates = await Can(ctx, "CanSetGuideRates", t => t.CanSetGuideRates);
        c.CanSetPark = await Can(ctx, "CanSetPark", t => t.CanSetPark);
        c.CanSetPierSide = await Can(ctx, "CanSetPierSide", t => t.CanSetPierSide);
        c.CanSetRightAscensionRate = await Can(ctx, "CanSetRightAscensionRate", t => t.CanSetRightAscensionRate);
        c.CanSetTracking = await Can(ctx, "CanSetTracking", t => t.CanSetTracking);
        c.CanSlew = await Can(ctx, "CanSlew", t => t.CanSlew);
        c.CanSlewAltAz = await Can(ctx, "CanSlewAltAz", t => t.CanSlewAltAz);
        c.CanSlewAltAzAsync = await Can(ctx, "CanSlewAltAzAsync", t => t.CanSlewAltAzAsync);
        c.CanSlewAsync = await Can(ctx, "CanSlewAsync", t => t.CanSlewAsync);
        c.CanSync = await Can(ctx, "CanSync", t => t.CanSync);
        c.CanSyncAltAz = await Can(ctx, "CanSyncAltAz", t => t.CanSyncAltAz);
        c.CanUnpark = await Can(ctx, "CanUnpark", t => t.CanUnpark);

        foreach (var axis in new[] { TelescopeAxis.Primary, TelescopeAxis.Secondary, TelescopeAxis.Tertiary })
        {
            var call = await ctx.TryAsync(t => t.CanMoveAxis(axis));
            var id = $"CanMoveAxis.{axis}";
            if (call.Ok)
            {
                c.CanMoveAxis[(int)axis] = call.Value;
                ctx.Report(id, "CanMoveAxis", Outcome.Pass, $"CanMoveAxis({axis}) = {call.Value}", ms: call.Ms);
            }
            else
            {
                ctx.Report(id, "CanMoveAxis", Outcome.Fail, $"CanMoveAxis({axis}) raised an error.", expected: "true or false",
                    actual: call.ErrorText, ms: call.Ms,
                    fix: "CanMoveAxis is mandatory for the three axes: return false for an axis the mount can't move.");
            }
        }
        await ctx.ExpectErrorAsync("CanMoveAxis.InvalidAxis", "CanMoveAxis", "CanMoveAxis(3)",
            t => t.CanMoveAxis((TelescopeAxis)3), AscomErrorKind.InvalidValue, "the axis isn't 0, 1 or 2");

        // Combinations that make no sense to a client.
        if (c.CanUnpark && !c.CanPark)
            ctx.Report("Consistency.Unpark", "CanUnpark", Outcome.Warning, "CanUnpark is true but CanPark is false.",
                fix: "A mount that can't park has nothing to unpark from: report CanUnpark false, or implement Park.");
        if (c.CanSetPark && !c.CanPark)
            ctx.Report("Consistency.SetPark", "CanSetPark", Outcome.Warning, "CanSetPark is true but CanPark is false.",
                fix: "Setting a park position is only useful to a mount that can park.");
        // COM drivers must implement both the synchronous and asynchronous slews or neither (ConformU checks this for
        // COM only: Alpaca devices are expected to implement only the asynchronous ones).
        if (ctx.Target.Kind == TargetKind.Com && (c.CanSlew != c.CanSlewAsync || c.CanSlewAltAz != c.CanSlewAltAzAsync))
            ctx.Report("Consistency.SlewPairs", "CanSlew", Outcome.Fail,
                "The synchronous and asynchronous slew capabilities don't match.",
                actual: $"CanSlew {c.CanSlew}, CanSlewAsync {c.CanSlewAsync}, CanSlewAltAz {c.CanSlewAltAz}, CanSlewAltAzAsync {c.CanSlewAltAzAsync}",
                fix: "A COM driver whose mount can slew must implement both SlewToCoordinates and SlewToCoordinatesAsync " +
                     "(and the same for alt/az): report CanSlew and CanSlewAsync alike.");
        if (c.CanSyncAltAz && !c.CanSync)
            ctx.Report("Consistency.SyncAltAz", "CanSyncAltAz", Outcome.Info, "CanSyncAltAz is true but CanSync is false (unusual).");

        // Mount facts the motion checks need.
        var mode = await ctx.TryAsync(t => t.AlignmentMode);
        c.AlignmentMode = mode.Ok ? mode.Value : null;
        var lat = await ctx.TryAsync(t => t.SiteLatitude);
        c.SiteLatitude = lat.Ok ? lat.Value : null;
        var lon = await ctx.TryAsync(t => t.SiteLongitude);
        c.SiteLongitude = lon.Ok ? lon.Value : null;
    }

    private static async Task<bool> Can(CheckContext ctx, string member, Func<ITelescopeV4, bool> read)
    {
        var call = await ctx.ReadMandatoryAsync(member, read);
        return call is { Ok: true, Value: true };
    }
}
