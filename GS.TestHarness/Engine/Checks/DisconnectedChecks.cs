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

namespace GS.TestHarness.Engine.Checks;

/// <summary>Disconnecting, and what a disconnected device must refuse. Run last.</summary>
public sealed class DisconnectedChecks : ICheckGroup
{
    public string Name => "Disconnected";
    public string Description => "Disconnect and Connecting, then that a disconnected device refuses with NotConnected. Run last.";

    public string? SkipReason(CheckContext ctx) => ctx.Options.IncludeDisconnected ? null : "disconnected checks are turned off for this run";

    public async Task RunAsync(CheckContext ctx)
    {
        var disconnect = await ctx.TryAsync(t => t.Disconnect(), TimeSpan.FromSeconds(30));
        if (disconnect.Kind == AscomErrorKind.NotImplemented)
        {
            ctx.Report("Disconnect", "Disconnect", Outcome.Fail, "Disconnect() is not implemented.", actual: disconnect.ErrorText,
                fix: "ITelescopeV4 requires Disconnect(): start disconnecting and return, with Connecting true until it has finished.");
            disconnect = await ctx.TryAsync(t => t.Connected = false, TimeSpan.FromSeconds(30));
        }
        if (!disconnect.Ok)
        {
            ctx.Report("Disconnect", "Disconnect", Outcome.Fail, "Disconnecting raised an error.", actual: disconnect.ErrorText,
                ms: disconnect.Ms, fix: "Disconnect() must not fail on a connected device.");
            return;
        }
        var finished = await ctx.WaitUntilAsync(t => !t.Connecting, TimeSpan.FromSeconds(30), 100);
        if (!finished)
            ctx.Report("Connecting.AfterDisconnect", "Connecting", Outcome.Fail, "Connecting stayed true for 30 s after Disconnect().",
                fix: "Set Connecting back to false when disconnecting has finished.");

        var connected = await ctx.TryAsync(t => t.Connected);
        if (connected is { Ok: true, Value: true })
        {
            // GSServer keeps the mount connected while any other client holds it, and an Alpaca device is one for
            // every client: then "disconnected" can't be observed from here.
            ctx.Report("Disconnect", "Disconnect", Outcome.Info,
                "Connected is still true after Disconnect(): the device is presumably held by another client, so the " +
                "disconnected-state checks can't run. Disconnect the other clients (or restart the server) to run them.",
                ms: disconnect.Ms);
            return;
        }
        ctx.Report("Disconnect", "Disconnect", connected.Ok ? Outcome.Pass : Outcome.Fail,
            connected.Ok ? "Disconnected: Connected is false." : "Connected can't be read after disconnecting.",
            actual: connected.ErrorText, ms: disconnect.Ms, fix: "Connected must stay readable when the device is disconnected.");

        await ctx.ExpectErrorAsync("NotConnected.RightAscension", "RightAscension", "RightAscension while disconnected",
            t => _ = t.RightAscension, AscomErrorKind.NotConnected, "the device isn't connected", property: true);
        await ctx.ExpectErrorAsync("NotConnected.Tracking", "Tracking", "Tracking while disconnected",
            t => _ = t.Tracking, AscomErrorKind.NotConnected, "the device isn't connected", property: true);
        await ctx.ExpectErrorAsync("NotConnected.CanSlew", "CanSlew", "CanSlew while disconnected",
            t => _ = t.CanSlew, AscomErrorKind.NotConnected, "the device isn't connected", property: true);
        await ctx.ExpectErrorAsync("NotConnected.AxisRates", "AxisRates", "AxisRates while disconnected",
            t => _ = t.AxisRates(ASCOM.Common.DeviceInterfaces.TelescopeAxis.Primary), AscomErrorKind.NotConnected, "the device isn't connected");

        // These must work whether or not the device is connected: the documentation lists no NotConnectedException for
        // them (Description does list one, so it isn't checked here).
        foreach (var (member, read) in new (string, Func<ASCOM.Common.DeviceInterfaces.ITelescopeV4, object?>)[]
                 {
                     ("Name", t => t.Name), ("DriverInfo", t => t.DriverInfo),
                     ("DriverVersion", t => t.DriverVersion), ("InterfaceVersion", t => t.InterfaceVersion),
                     ("SupportedActions", t => t.SupportedActions)
                 })
        {
            var call = await ctx.TryAsync(read);
            ctx.Report($"WhenDisconnected.{member}", member, call.Ok ? Outcome.Pass : Outcome.Fail,
                call.Ok ? $"{member} answers while disconnected." : $"{member} refuses while disconnected.",
                actual: call.ErrorText, ms: call.Ms,
                fix: $"{member} must work whether or not the device is connected (clients read it before connecting).");
        }

        var again = await ctx.TryAsync(t => t.Disconnect(), TimeSpan.FromSeconds(30));
        ctx.Report("Disconnect.WhenDisconnected", "Disconnect", again.Ok ? Outcome.Pass : Outcome.Fail,
            again.Ok ? "Disconnect() while disconnected: no error." : "Disconnect() while disconnected raised an error.",
            actual: again.ErrorText, fix: "Disconnect() on a disconnected device must do nothing, without an error.");

        // Put back a connection another client had made before the run.
        if (ctx.Fact("WasConnected"))
        {
            await ctx.TryAsync(t => t.Connect(), TimeSpan.FromSeconds(30));
            await ctx.WaitUntilAsync(t => !t.Connecting, TimeSpan.FromSeconds(30), 100);
        }
    }
}
