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

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace GS.TestHarness.Engine.Checks;

/// <summary>
/// Tier 2 for Common.Action.Unknown (which error an unknown action gets, and where the code throws it) and for
/// Protocol.Discovery (which addresses the server answers discovery on: a server listening on localhost only, a
/// broadcast that doesn't reach it, or one that never runs).
/// </summary>
public static partial class AreaDiagnostics
{
    public const string Actions = "Actions";
    public const string Discovery = "Discovery";

    // ---------- Actions ----------

    /// <summary>
    /// Action with a made-up name, an empty one and a blank one (never a supported one: it would do something), the
    /// errors each gets, against SupportedActions; and the code: where Action throws, and the names it handles.
    /// </summary>
    private static async Task ActionsAsync(CheckContext ctx, string said)
    {
        var supported = await ctx.TryAsync(t => t.SupportedActions?.ToList());
        var listed = supported is { Ok: true, Value: { } l } ? l : [];
        var unknown = "GS.TestHarness.NoSuchAction" + Guid.NewGuid().ToString("N")[..6];
        var rows = new List<(string Label, Call<string> Call)>();
        foreach (var (label, name) in new[] { ($"\"{unknown}\" (made up)", unknown), ("\"\" (empty)", ""), ("\" \" (blank)", " ") })
            rows.Add((label, await ctx.TryAsync(t => t.Action(name, ""))));
        var made = rows[0].Call;
        string Answer(Call<string> c) => c.Ok ? "accepted" : c.Kind == AscomErrorKind.ActionNotImplemented ? "ActionNotImplementedException (right)" : c.ErrorText ?? "error";

        var actual = new StringBuilder($"SupportedActions: {(supported.Ok ? listed.Count == 0 ? "none" : string.Join(", ", listed) : supported.ErrorText)}\n");
        foreach (var (label, call) in rows) actual.AppendLine($"Action({label}): {Answer(call)}");

        var right = made.Kind == AscomErrorKind.ActionNotImplemented;
        var pattern = right ? "an unknown action now gets ActionNotImplementedException"
            : made.Ok ? "an unknown action is accepted: Action doesn't check the name against SupportedActions"
            : $"an unknown action gets {(made.Kind is AscomErrorKind.Other ? made.ErrorText : AscomError.ExpectedName(made.Kind))} instead of ActionNotImplementedException";

        SourceLine? at = null;
        string? listNote = null;
        var body = Code(ctx, "Action");
        if (body is null) actual.AppendLine(NoSource(ctx));
        else
        {
            var throws = Lines(body, @"throw\s+new\s+\w*Exception", comments: false);
            actual.AppendLine($"Code reviewed ({body[0].Location}): {(throws.Count == 0 ? "it throws nothing itself" : "it throws:")}");
            if (throws.Count > 0) actual.AppendLine(Quote(throws.Take(4)));

            // Where the error the client got comes from: its message, without the made-up name, in the code.
            if (!right && made.Error is { } error && MessageFragment(error.Message, unknown) is { } fragment)
            {
                var from = ctx.Locator.Search(ctx.Target.Codebase, Regex.Escape(fragment), 3);
                if (from.Count > 0)
                {
                    actual.AppendLine($"The message \"{fragment}\" comes from:");
                    actual.AppendLine(Quote(from));
                }
                at = from.FirstOrDefault() ?? throws.FirstOrDefault();
            }

            // The names Action handles, against SupportedActions.
            var handled = body.Skip(1).Where(x => !x.IsComment && Regex.IsMatch(x.Text, @"\bcase\b|==|Equals\(|=>"))
                .SelectMany(x => Regex.Matches(x.Text, @"""([A-Za-z][\w.:-]{1,60})""").Select(m => m.Groups[1].Value))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (handled.Count > 0 && supported.Ok)
            {
                var notListed = handled.Where(h => !listed.Contains(h, StringComparer.OrdinalIgnoreCase)).ToList();
                var notHandled = listed.Where(s => !handled.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
                actual.AppendLine($"Names Action handles: {List(handled)}.");
                if (notListed.Count > 0) actual.AppendLine($"Handled but not in SupportedActions: {List(notListed)}.");
                if (notHandled.Count > 0) actual.AppendLine($"In SupportedActions but not handled here: {List(notHandled)}.");
                if (notListed.Count > 0) listNote = $" Also list {List(notListed)} in SupportedActions, or clients can't know {(notListed.Count == 1 ? "it exists" : "they exist")}.";
            }
        }
        var fix = right ? null
            : "Throw ActionNotImplementedException (0x40C) when the name isn't one of SupportedActions" +
              (at is null ? "" : $" (at {at.Location}, which throws {made.Kind} now)") +
              ", comparing ignoring case (action names are case-insensitive). Clients tell an action the driver doesn't have from one that failed by this error." +
              listNote;

        ctx.Report("Actions", "Action", right ? Outcome.Info : Outcome.Fail, $"Action: {pattern}.",
            expected: said + "\n\nExpected: ActionNotImplementedException (0x40C) for any name that isn't one of SupportedActions.",
            actual: actual.ToString().TrimEnd(), fix: fix);
    }

    /// <summary>The longest run of the error's own words (without the action's name and its quotes), to find it in the code.</summary>
    internal static string? MessageFragment(string message, string name)
    {
        // Split at the name, quotes and the ends of sentences (a driver may append its action list after them).
        var parts = Regex.Split(message.Replace(name, "\n"), @"[\n'""|]|\.\s+")
            .Select(p => Regex.Replace(p.Trim(), @"^[^A-Za-z]+|[^A-Za-z]+$", ""))
            .Where(p => p.Length >= 12 && !Regex.IsMatch(p, @"^(InvalidOperation|ASCOM|DriverException)\b"))
            .OrderByDescending(p => p.Length).ToList();
        var best = parts.FirstOrDefault();
        return best is null ? null : best.Length > 60 ? best[..60] : best;
    }

    // ---------- Discovery ----------

    /// <summary>One discovery request: where it went, what kind of address, and whether this server answered.</summary>
    internal sealed record DiscoveryProbe(string Label, string Kind, bool Answered, int Replies);

    /// <summary>
    /// "alpacadiscovery1" to the server's address, to each of this PC's network addresses (when the server is on this
    /// PC), to each network's broadcast address and to 255.255.255.255: which ones this server answers.
    /// </summary>
    private static async Task DiscoveryDiagnosisAsync(CheckContext ctx, string said)
    {
        if (ctx.Target.Kind != TargetKind.Alpaca)
        {
            ctx.Report("Discovery", "UDP discovery", Outcome.Skipped, "It's a COM driver: there's no discovery.", expected: said);
            return;
        }
        IPAddress? target;
        try
        {
            target = IPAddress.TryParse(ctx.Target.Host, out var ip) ? ip
                : (await Dns.GetHostAddressesAsync(ctx.Target.Host, ctx.Token)).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        }
        catch (SocketException) { target = null; }
        if (target is null || target.AddressFamily != AddressFamily.InterNetwork)
        {
            ctx.Report("Discovery", "UDP discovery", Outcome.Skipped, $"{ctx.Target.Host} has no IPv4 address to send discovery to.", expected: said);
            return;
        }

        var networks = LocalNetworks();
        var local = IPAddress.IsLoopback(target) || networks.Any(n => n.Address.Equals(target));
        var probes = new List<DiscoveryProbe>();
        async Task Ask(string label, string kind, IPAddress to, bool broadcast)
        {
            var replies = await DiscoverAsync(ctx, to, broadcast);
            probes.Add(new DiscoveryProbe(label, kind, replies.Any(r => Regex.IsMatch(r, $@"""AlpacaPort""\s*:\s*{ctx.Target.Port}\b")), replies.Count));
        }
        await Ask($"{target} (the server's address)", "server", target, false);
        foreach (var n in networks)
        {
            if (local && !n.Address.Equals(target)) await Ask($"{n.Address} (this PC on {n.Name})", "this PC", n.Address, false);
            await Ask($"{n.Broadcast} (the broadcast address of {n.Name})", "broadcast", n.Broadcast, true);
        }
        await Ask("255.255.255.255 (broadcast)", "broadcast", IPAddress.Broadcast, true);

        var (outcome, message, fix) = DiscoveryVerdict(probes, local);
        var actual = new StringBuilder("Discovery requests (\"alpacadiscovery1\" to UDP 32227):\n");
        foreach (var p in probes)
            actual.AppendLine($"  {p.Label}: {(p.Answered ? "answered" : p.Replies > 0 ? $"{p.Replies} repl{(p.Replies == 1 ? "y" : "ies")}, none from this server" : "no answer")}");

        // The code: how the server's discovery listener is set up.
        if (ctx.Locator.RootOf(ctx.Target.Codebase) is null) actual.AppendLine(NoSource(ctx));
        else
        {
            var (review, better) = ReviewDiscovery(ctx.Locator, ctx.Target.Codebase, message);
            actual.AppendLine(review);
            if (better is not null) fix = better + (better.EndsWith("instead. ", StringComparison.Ordinal) ? fix : "");
        }

        ctx.Report("Discovery", "UDP discovery", outcome, message,
            expected: said + "\n\nExpected: an answer {\"AlpacaPort\": " + ctx.Target.Port + "} to a broadcast, as clients on the network send it.",
            actual: actual.ToString().TrimEnd(), fix: fix);
    }

    /// <summary>What the discovery answers show: the server not answering at all, listening on localhost only, or a broadcast that doesn't reach it.</summary>
    internal static (Outcome Outcome, string Message, string? Fix) DiscoveryVerdict(IReadOnlyList<DiscoveryProbe> probes, bool local)
    {
        var server = probes.Any(p => p.Kind == "server" && p.Answered);
        var thisPc = probes.Where(p => p.Kind == "this PC").ToList();
        var broadcast = probes.Any(p => p.Kind == "broadcast" && p.Answered);
        if (broadcast)
            return (Outcome.Info, "Discovery: a broadcast now finds this server.", null);
        if (!server)
            return (Outcome.Fail, "Discovery: no answer even when sent straight to the server's address: discovery isn't running, or UDP 32227 is blocked.",
                "Turn discovery on in the server, and let UDP port 32227 in through the firewall of the PC it runs on.");
        if (local && thisPc.Count > 0 && !thisPc.Any(p => p.Answered))
            return (Outcome.Fail, "Discovery: the server listens on localhost only: it answers at 127.0.0.1 but not at this PC's network addresses, so no other PC can find it.",
                "Listen for discovery on every address (IPAddress.Any), not the loopback address.");
        if (local && thisPc.Any(p => p.Answered))
            return (Outcome.Info,
                "Discovery: the server listens on every address (it answers at this PC's network address), but no broadcast reached it. " +
                "A broadcast sent from the same PC isn't always delivered back to a listener on it (Windows), so this may not be the server's fault.",
                "Check from another PC on the network (ConformU's discovery, or NINA's equipment chooser). If that finds nothing either, the firewall " +
                "on this PC is blocking broadcast UDP 32227 (a rule for the Public profile, say).");
        if (local)
            return (Outcome.Info,
                "Discovery: the server answers at its own address, but no broadcast reached it; this PC has no network address to tell more " +
                "(a broadcast sent from the same PC isn't always delivered back to it).",
                "Check from another PC on the network.");
        return (Outcome.Warning,
            "Discovery: the server answers when asked directly, but not to a broadcast: its PC's firewall or the network blocks broadcasts, " +
            "or it's on another subnet than this PC.",
            "Let broadcast UDP 32227 in through the firewall of the server's PC, and check that both PCs are on the same subnet (routers don't pass broadcasts).");
    }

    /// <summary>
    /// The discovery listener's code (lines in a discovery file, or about discovery, before any other: an HTTP
    /// listener's address isn't the discovery listener's) and, for a server answering on localhost only, the better
    /// fix: a remote-access setting that decides whether other PCs get answers, or a listener bound to loopback.
    /// </summary>
    internal static (string Review, string? Fix) ReviewDiscovery(SourceLocator locator, Codebase codebase, string message)
    {
        static bool AboutDiscovery(SourceLine l) => Regex.IsMatch(l.File + " " + l.Text, @"[Dd]iscover|\b32227\b|Responder");
        var found = locator.Search(codebase,
                @"new\s+UdpClient\s*\(|\.Bind\s*\(|IPAddress\.(Any|Loopback|IPv6Any|IPv6Loopback)\b|\b32227\b|alpacadiscovery|new\s+Responder\s*\(|AllowRemoteAccess|OnlyToLocalHost", 80)
            .Where(l => !l.IsComment).ToList();
        var discovery = found.Where(AboutDiscovery).Take(6).ToList();
        if (discovery.Count == 0) return ("Code reviewed: no discovery listener found (no UdpClient or responder on port 32227).", null);
        var review = "Code reviewed: the discovery listener:\n" + Quote(discovery);
        if (!message.Contains("localhost only", StringComparison.Ordinal)) return (review, null);

        var remote = discovery.FirstOrDefault(l => Regex.IsMatch(l.Text, @"AllowRemoteAccess|OnlyToLocalHost"));
        var loopback = discovery.FirstOrDefault(l => Regex.IsMatch(l.Text, @"IPAddress\.(Loopback|IPv6Loopback)\b"));
        var http = found.FirstOrDefault(l => !AboutDiscovery(l) && Regex.IsMatch(l.Text, @"Listen\s*\(\s*IPAddress\.Loopback"));
        if (remote is not null)
            return (review, $"Discovery answers other PCs only when the server allows remote access ({remote.Location}): turn on its remote access setting " +
                            "(\"Allow remote access\" in its settings)" +
                            (http is null ? "" : $", which also makes its API listen on every address rather than 127.0.0.1 only ({http.Location}), so other PCs can then reach it") +
                            ". Without it, the server can only be used from this PC.");
        if (loopback is not null)
            return (review, $"The discovery listener binds to the loopback address ({loopback.Location}): bind to IPAddress.Any (and IPv6Any) instead. ");
        return (review, null);
    }

    /// <summary>This PC's IPv4 networks that are up: name, address and broadcast address (at most four).</summary>
    private static List<(string Name, IPAddress Address, IPAddress Broadcast)> LocalNetworks()
    {
        var list = new List<(string, IPAddress, IPAddress)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var u in nic.GetIPProperties().UnicastAddresses)
                {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.IPv4Mask is null || IPAddress.IsLoopback(u.Address)) continue;
                    var a = u.Address.GetAddressBytes();
                    var m = u.IPv4Mask.GetAddressBytes();
                    var b = new byte[4];
                    for (var i = 0; i < 4; i++) b[i] = (byte)(a[i] | ~m[i]);
                    list.Add((nic.Name, u.Address, new IPAddress(b)));
                }
            }
        }
        catch (NetworkInformationException) { }
        return [.. list.Take(4)];
    }

    /// <summary>Sends "alpacadiscovery1" to UDP 32227 at <paramref name="to"/> and collects the replies for 2 s.</summary>
    private static async Task<List<string>> DiscoverAsync(CheckContext ctx, IPAddress to, bool broadcast)
    {
        var replies = new List<string>();
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = broadcast };
            await udp.SendAsync(Encoding.ASCII.GetBytes("alpacadiscovery1"), new IPEndPoint(to, 32227), ctx.Token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            while (true)
            {
                var result = await udp.ReceiveAsync(timeout.Token);
                replies.Add(Encoding.UTF8.GetString(result.Buffer));
            }
        }
        catch (OperationCanceledException) when (!ctx.Token.IsCancellationRequested) { }
        catch (SocketException) { }
        return replies;
    }
}
