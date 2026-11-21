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
using System.Text.RegularExpressions;
using ASCOM.Common.DeviceInterfaces;

namespace GS.TestHarness.Engine.Checks;

/// <summary>Connecting (Connect, Connecting, Connected) and what the device says it is.</summary>
public sealed class ConnectionChecks : ICheckGroup
{
    public string Name => "Connection";
    public string Description => "Connect, Connecting and Connected, and the identity members (Name, DriverVersion, InterfaceVersion...).";
    public string? SkipReason(CheckContext ctx) => null;

    public async Task RunAsync(CheckContext ctx)
    {
        var before = await ctx.TryAsync(t => t.Connected);
        if (!before.Ok)
        {
            ctx.Report("Connected.Read", "Connected", before.Kind == AscomErrorKind.Timeout ? Outcome.Error : Outcome.Fail,
                "Connected could not be read before connecting.", expected: "true or false", actual: before.ErrorText,
                ms: before.Ms,
                fix: before.Kind is AscomErrorKind.Timeout or AscomErrorKind.Other
                    ? "Check that the server is running and the address, port and device number (or ProgID) are right."
                    : "Connected must always be readable, whether or not the device is connected.");
            if (before.Kind is AscomErrorKind.Timeout or AscomErrorKind.Other)
                throw new StopRunException($"The device can't be reached: {before.ErrorText}");
        }
        else
        {
            ctx.Report("Connected.Read", "Connected", Outcome.Pass, $"Connected = {before.Value} before connecting.", ms: before.Ms);
        }
        ctx.Facts["WasConnected"] = before is { Ok: true, Value: true };

        // ITelescopeV4: Connect() starts connecting and returns; Connecting is true until it has finished.
        var watch = Stopwatch.StartNew();
        var connect = await ctx.TryAsync(t => t.Connect(), TimeSpan.FromSeconds(Math.Max(30, ctx.Options.CallTimeoutSeconds)));
        if (connect.Kind == AscomErrorKind.NotImplemented)
        {
            ctx.Report("Connect", "Connect", Outcome.Fail, "Connect() is not implemented.", expected: "Connect() starts connecting",
                actual: connect.ErrorText, ms: connect.Ms,
                fix: "ITelescopeV4 (Platform 7) requires Connect(), Disconnect() and Connecting. Connect() should start the " +
                     "connection and return at once, with Connecting true until it has finished.");
            var legacy = await ctx.TryAsync(t => t.Connected = true, TimeSpan.FromSeconds(30));
            if (!legacy.Ok) throw new StopRunException($"The device would not connect: {legacy.ErrorText}");
        }
        else if (!connect.Ok)
        {
            ctx.Report("Connect", "Connect", Outcome.Error, "Connect() failed.", actual: connect.ErrorText, ms: connect.Ms,
                fix: "Look at the server's log for why it couldn't connect to the mount (port, simulator settings).");
            throw new StopRunException($"The device would not connect: {connect.ErrorText}");
        }
        else
        {
            // "On return, Connecting must be True unless already connected."
            var onReturn = await ctx.TryAsync(t => (t.Connecting, t.Connected));
            if (onReturn is { Ok: true, Value: (false, false) })
                ctx.Report("Connect.ConnectingOnReturn", "Connect", Outcome.Fail,
                    "When Connect() returned, neither Connecting nor Connected was true.",
                    expected: "Connecting true (or Connected true if it was already connected)", actual: "Connecting false, Connected false",
                    fix: "Set Connecting to true before Connect() returns, and keep it true until the connection attempt has ended.");
            var finished = await ctx.WaitUntilAsync(t => !t.Connecting, TimeSpan.FromSeconds(30), 100);
            var connected = await ctx.TryAsync(t => t.Connected);
            if (!finished)
                ctx.Report("Connecting.AfterConnect", "Connecting", Outcome.Fail, "Connecting stayed true for 30 s after Connect().",
                    expected: "false once connected", actual: "true",
                    fix: "Set Connecting back to false when the connection attempt ends, whether it succeeded or failed.");
            else
                ctx.Report("Connecting.AfterConnect", "Connecting", Outcome.Pass,
                    $"Connecting went false {watch.Elapsed.TotalSeconds:0.0} s after Connect().");

            if (connected is not { Ok: true, Value: true })
            {
                ctx.Report("Connect", "Connect", Outcome.Fail, "Connected is not true after Connect() finished.",
                    expected: "Connected = true", actual: connected.Ok ? "false" : connected.ErrorText,
                    fix: "When Connect() finishes without error, Connected must be true. If the connection failed, the " +
                         "error must be reported (Connecting false, then Connected false and the reason in the log).");
                throw new StopRunException("The device did not connect.");
            }
            ctx.Report("Connect", "Connect", connect.Ms > 5000 ? Outcome.Warning : Outcome.Pass,
                connect.Ms > 5000
                    ? $"Connected, but Connect() itself took {connect.Ms / 1000:0.0} s."
                    : $"Connected (Connect() returned in {connect.Ms:0} ms).",
                expected: "Connect() returns at once and the work goes on in the background",
                actual: $"{connect.Ms:0} ms", ms: connect.Ms,
                fix: "Connect() is meant to be asynchronous: start the connection, return, and report progress through " +
                     "Connecting. Blocking makes clients' user interfaces freeze.");
        }

        // Connecting again while connected must be harmless.
        var again = await ctx.TryAsync(t => t.Connect(), TimeSpan.FromSeconds(30));
        ctx.Report("Connect.WhenConnected", "Connect", again.Ok ? Outcome.Pass : Outcome.Fail,
            again.Ok ? "Connect() while connected: no error." : "Connect() while connected raised an error.",
            expected: "no error", actual: again.ErrorText, ms: again.Ms,
            fix: "Connect() on a connected device must do nothing and not raise an error.");
        await ctx.WaitUntilAsync(t => !t.Connecting, TimeSpan.FromSeconds(30), 100);

        var setTrue = await ctx.TryAsync(t => t.Connected = true, TimeSpan.FromSeconds(30));
        ctx.Report("Connected.SetTrueWhenConnected", "Connected", setTrue.Ok ? Outcome.Pass : Outcome.Fail,
            setTrue.Ok ? "Connected = true while connected: no error." : "Connected = true while connected raised an error.",
            expected: "no error", actual: setTrue.ErrorText, ms: setTrue.Ms,
            fix: "Setting Connected to true on a connected device must do nothing (older clients still use it).");

        await IdentityAsync(ctx);
    }

    private static async Task IdentityAsync(CheckContext ctx)
    {
        var name = await ctx.ReadMandatoryAsync("Name", t => t.Name, v => string.IsNullOrWhiteSpace(v) ? "it is empty" : null);
        var description = await ctx.ReadMandatoryAsync("Description", t => t.Description,
            v => string.IsNullOrWhiteSpace(v) ? "it is empty" : null);
        if (description is { Ok: true, Value.Length: > 64 })
            ctx.Report("Description.Length", "Description", Outcome.Warning,
                $"Description is {description.Value.Length} characters long.",
                expected: "64 characters or fewer", actual: $"{description.Value.Length}",
                fix: "The ITelescopeV4 documentation limits Description to 64 characters, so it fits in FITS image headers.");
        var info = await ctx.ReadMandatoryAsync("DriverInfo", t => t.DriverInfo, v => string.IsNullOrWhiteSpace(v) ? "it is empty" : null);
        var version = await ctx.ReadMandatoryAsync("DriverVersion", t => t.DriverVersion,
            v => string.IsNullOrWhiteSpace(v) ? "it is empty" : null);
        if (version is { Ok: true, Value: { } v } && !string.IsNullOrWhiteSpace(v) && !Regex.IsMatch(v.Trim(), @"^\d+\.\d+$"))
            ctx.Report("DriverVersion.Format", "DriverVersion", Outcome.Warning,
                $"DriverVersion is \"{v}\", not \"major.minor\".", expected: "\"n.n\", e.g. \"1.0\"", actual: v,
                fix: "The ITelescopeV4 documentation says DriverVersion \"must be in the form 'n.n'\" (only the major and " +
                     "minor version). Put the full build number in DriverInfo instead.");
        var iface = await ctx.ReadMandatoryAsync("InterfaceVersion", t => t.InterfaceVersion,
            v => v is < 1 or > 4 ? "a telescope's InterfaceVersion is 1 to 4" : null, expected: "4");
        // Anything but 3 or 4 isn't an ITelescopeV3 or V4 driver: the checks assume members it may not have, so the run stops here.
        var unsupported = iface is { Ok: true, Value: not (3 or 4) };
        if (unsupported)
            ctx.Report("InterfaceVersion.Current", "InterfaceVersion", Outcome.Error,
                $"InterfaceVersion is {iface.Value}: the harness tests ITelescopeV3 and ITelescopeV4 (3 or 4), so the run is stopped.",
                expected: "3 or 4 (ITelescopeV3 or ITelescopeV4)", actual: $"{iface.Value}",
                fix: "Implement ITelescopeV4 and return 4 from InterfaceVersion (3 for ITelescopeV3). Below 3 the interface lacks members the checks rely on " +
                     "(for example the async slew methods and tracking rates), and no interface version above 4 exists yet.");
        else if (iface is { Ok: true, Value: < 4 })
            ctx.Report("InterfaceVersion.Current", "InterfaceVersion", Outcome.Warning,
                $"InterfaceVersion is {iface.Value}: Platform 7 members (Connect, DeviceState...) may be missing.",
                expected: "4 (ITelescopeV4)", actual: $"{iface.Value}",
                fix: "Implement ITelescopeV4 and return 4. Clients only use Connect, Disconnect, Connecting and " +
                     "DeviceState when InterfaceVersion is 4.");

        ctx.Identity = ctx.Identity with
        {
            Name = name.Value,
            Description = description.Value,
            DriverInfo = info.Value,
            DriverVersion = version.Value,
            InterfaceVersion = iface.Ok ? iface.Value : null
        };
        if (unsupported) throw new StopRunException($"InterfaceVersion is {iface.Value}, not 3 or 4: the harness tests ITelescopeV3 and ITelescopeV4.");
    }
}
