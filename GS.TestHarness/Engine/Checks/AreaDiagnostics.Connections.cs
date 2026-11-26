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
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GS.TestHarness.Engine.Checks;

/// <summary>
/// Tier 2 for the connection between clients (Clients.OneDisconnects, Clients.ConnectCycles) and for the Alpaca
/// server's own handling of requests (Protocol.ClientTransactionID.Invalid, Protocol.Errors.InvalidValue, the setup
/// pages): what happens, step by step, and the code that does it.
/// </summary>
public static partial class AreaDiagnostics
{
    public const string ClientConnections = "ClientConnections";
    public const string AlpacaRequests = "AlpacaRequests";

    // ---------- Clients: one client's Disconnect ----------

    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(30);

    private static async Task<(T Value, Exception? Error)> Attempt<T>(Func<Task<T>> call)
    {
        try { return (await call(), null); }
        catch (Exception ex) { return (default!, AscomError.Unwrap(ex)); }
    }

    /// <summary>One client's state: whether it reads Connected, and whether a member answers.</summary>
    private sealed record ClientState(bool? Connected, string ConnectedText, bool Answers, string AnswerText)
    {
        public override string ToString() => $"Connected {ConnectedText}, RightAscension {AnswerText}";
    }

    private static async Task<ClientState> StateOf(ITelescopeSession s)
    {
        var connected = await Attempt(() => s.RunAsync(t => t.Connected, ClientTimeout));
        var ra = await Attempt(() => s.RunAsync(t => t.RightAscension, ClientTimeout));
        return new ClientState(connected.Error is null ? connected.Value : null,
            connected.Error is null ? connected.Value.ToString() : AscomError.Describe(connected.Error),
            ra.Error is null, ra.Error is null ? CheckContext.Show(Math.Round(ra.Value, 5)) : AscomError.Describe(ra.Error));
    }

    /// <summary>
    /// Clients B and C connect beside the run's own client A; B disconnects; then B connects again, and C and B
    /// disconnect. After each step every client's Connected and a RightAscension read show whether the device counts
    /// its clients (one Disconnect leaves the others connected) or one Disconnect turns the hardware off for all.
    /// </summary>
    private static async Task ClientConnectionsAsync(CheckContext ctx, List<CheckResult> found, string said)
    {
        const string Id = "Clients.Connections";
        if (ctx.OpenSession is null)
        {
            ctx.Report(Id, "Disconnect", Outcome.Skipped, "Another client can't be opened here.", expected: said);
            return;
        }
        var a = ctx.Session;
        ITelescopeSession? b = null, c = null;
        var steps = new StringBuilder();
        ClientState? cAfterB = null, aAfterB = null, cAfterBack = null, aAfterAll = null;
        try
        {
            b = await ctx.OpenSession();
            c = await ctx.OpenSession();
            await ClientsChecks.ConnectAsync(b);
            await ClientsChecks.ConnectAsync(c);
            steps.AppendLine($"A (this run), B and C connected: A {await StateOf(a)}; B {await StateOf(b)}; C {await StateOf(c)}");

            await ClientsChecks.DisconnectAsync(b);
            aAfterB = await StateOf(a);
            cAfterB = await StateOf(c);
            steps.AppendLine($"B disconnected: A {aAfterB}; B {await StateOf(b)}; C {cAfterB}");

            await ClientsChecks.ConnectAsync(b);
            cAfterBack = await StateOf(c);
            steps.AppendLine($"B connected again: C {cAfterBack}");

            await ClientsChecks.DisconnectAsync(c);
            await ClientsChecks.DisconnectAsync(b);
            aAfterAll = await StateOf(a);
            steps.AppendLine($"C and B disconnected (A never did): A {aAfterAll}");
        }
        catch (Exception ex)
        {
            steps.AppendLine($"Stopped: {AscomError.Describe(ex)}");
        }
        finally
        {
            if (c is not null) await ClientsChecks.ReleaseClientAsync(ctx, c);
            if (b is not null) await ClientsChecks.ReleaseClientAsync(ctx, b);
            // Put this run's client back, whatever the others did to it.
            if ((await Attempt(() => a.RunAsync(t => t.Connected, ClientTimeout))) is not (true, null))
            {
                await Attempt(async () => { await ClientsChecks.ConnectAsync(a); return 0; });
                steps.AppendLine("This run's client was connected again.");
            }
        }

        if (cAfterB is null || aAfterB is null)
        {
            ctx.Report(Id, "Disconnect", Outcome.Error, "The clients couldn't be connected and disconnected in turn.",
                expected: said, actual: steps.ToString().TrimEnd());
            return;
        }

        var counts = cAfterB.Connected == true && aAfterB.Connected == true;
        var shared = cAfterB.Connected == false;
        string pattern, fix = "";
        Outcome outcome;
        if (counts && cAfterB.Answers && aAfterAll?.Connected != false)
        {
            pattern = "the device now counts its clients: one client's Disconnect leaves the others connected and answering";
            outcome = Outcome.Info;
        }
        else if (counts)
        {
            pattern = "the other clients still read Connected true after one disconnected, but stopped answering";
            outcome = Outcome.Fail;
            fix = "Connected and the members must agree: while Connected reads true, members must answer.";
        }
        else if (shared)
        {
            pattern = "one client's Disconnect turns the hardware off for every client: there's no count of clients" +
                      (cAfterB.Answers ? ", and the others' calls still answer (members don't check the connection)" : "");
            outcome = Outcome.Warning;
            fix = "Connected reports the hardware state, so another client reading false after one disconnected follows from it; " +
                  "but every client sharing the device (a planetarium and a guider, say) then loses it when any one of them closes. " +
                  "Count the clients: Connect adds one, Disconnect removes one, and only the last Disconnect disconnects the hardware " +
                  "(as hubs do). Connected can still report the hardware state.";
        }
        else
        {
            pattern = $"after one client disconnected, another client's Connected couldn't be read ({cAfterB.ConnectedText})";
            outcome = Outcome.Fail;
            fix = "Connected must always be readable, by every client.";
        }
        if (shared && cAfterBack?.Connected == true) steps.AppendLine("Any client's Connect turns it back on for all: one shared state.");
        if (found.Any(r => r.Id == "Clients.ConnectCycles") && shared)
            steps.AppendLine("ConnectCycles cut off this run's client for the same reason: each cycle's Disconnect turned the hardware off.");

        // The code: whether Connect/Disconnect/Connected keep a count of clients.
        var bodies = new[] { "Connected", "Connect", "Disconnect" }.Select(m => Code(ctx, m)).OfType<IReadOnlyList<SourceLine>>().ToList();
        if (bodies.Count == 0) steps.AppendLine(NoSource(ctx));
        else
        {
            var counting = bodies.SelectMany(body => Lines(body, @"\b(Count|count|[Cc]lients?|ConnectStates|[Cc]onnections|Interlocked)\b|\+\+|--", comments: false)).Distinct().ToList();
            if (counting.Count > 0)
            {
                steps.AppendLine("Code reviewed: it keeps a count of clients:");
                steps.AppendLine(Quote(counting.Take(4)));
            }
            else
            {
                var sets = bodies.SelectMany(body => Lines(body, @"=\s*(true|false|value)\b|Connect\w*\s*\(", comments: false)).Distinct().Take(4).ToList();
                steps.AppendLine($"Code reviewed ({bodies[0][0].Location}): no count of clients{(sets.Count > 0 ? "; it sets the state directly:" : ".")}");
                if (sets.Count > 0) steps.AppendLine(Quote(sets));
                if (shared) fix += $" Add the count in {bodies[0][0].Location.Split(':')[0]}.";
            }
        }

        ctx.Report(Id, "Disconnect", outcome, $"Clients: {pattern}.",
            expected: said + "\n\nExpected: one client's Disconnect leaves the other clients connected and answering.",
            actual: steps.ToString().TrimEnd(), fix: fix.Length == 0 ? null : fix);
    }

    // ---------- Alpaca: the server's handling of requests ----------

    /// <summary>One request sent, what kind of value it carries, and the reply.</summary>
    private sealed record Probe(string Label, string Kind, ProtocolChecks.Reply Reply)
    {
        public bool Refused400 => Reply.Status == HttpStatusCode.BadRequest;

        public bool ReachedDriver => Reply.Status == HttpStatusCode.OK;

        public string Shown
        {
            get
            {
                if (Reply.Error is not null) return Reply.Shown;
                if (Reply.Status != HttpStatusCode.OK)
                {
                    var text = Reply.Body.Length > 140 ? Reply.Body[..140] + "…" : Reply.Body;
                    return $"HTTP {(int)Reply.Status}: {text}";
                }
                // Read leniently: a number that isn't a whole int32 (1025.0) is shown as sent, not a reason to stop the group.
                var errorNumber = Reply.Field("ErrorNumber");
                var number = errorNumber is { ValueKind: JsonValueKind.Number } n ? n.TryGetInt32(out var i) ? i : (int?)null : 0;
                var txid = Reply.Field("ClientTransactionID")?.ToString();
                if (number is null) return $"HTTP 200, ErrorNumber {errorNumber}";
                return number != 0 ? $"HTTP 200, ErrorNumber 0x{number:X}" : $"HTTP 200, Value {Reply.Field("Value")?.ToString() ?? "-"}, ClientTransactionID {txid}";
            }
        }
    }

    private static async Task AlpacaRequestsAsync(CheckContext ctx, List<CheckResult> found, string said)
    {
        if (ctx.Target.Kind != TargetKind.Alpaca)
        {
            foreach (var id in new[] { "Alpaca.Binding", "Alpaca.Setup" })
                ctx.Report(id, "Alpaca server", Outcome.Skipped, "It's a COM driver, not an Alpaca server.", expected: said);
            return;
        }
        using var http = ProtocolChecks.CreateHttp(ctx);
        if (found.Any(r => r.Id is "Protocol.ClientTransactionID.Invalid" or "Protocol.Errors.InvalidValue"))
            await BindingAsync(ctx, http, said);
        if (found.Any(r => r.Id.StartsWith("Protocol.Setup.", StringComparison.Ordinal)))
            await SetupPagesAsync(ctx, http, said);
    }

    /// <summary>
    /// Values the driver must judge (an out-of-range axis, an RA past 24 h) against values no server can parse, and
    /// ClientTransactionIDs that aren't a uint32: which ones the server's request validation refuses with HTTP 400
    /// before the driver is called.
    /// </summary>
    private static async Task BindingAsync(CheckContext ctx, HttpClient http, string said)
    {
        var device = ProtocolChecks.DevicePath(ctx);
        var probes = new List<Probe>();
        async Task Send(string label, string kind, string path)
            => probes.Add(new Probe(label, kind, await ProtocolChecks.GetAsync(ctx, http, $"{device}/{path}")));
        foreach (var axis in new[] { "0", "2" }) await Send($"canmoveaxis Axis={axis}", "valid", $"canmoveaxis?Axis={axis}&ClientID=7&ClientTransactionID=31");
        foreach (var axis in new[] { "3", "-1", "9" }) await Send($"canmoveaxis Axis={axis}", "enum out of range", $"canmoveaxis?Axis={axis}&ClientID=7&ClientTransactionID=32");
        await Send("canmoveaxis Axis=abc", "unparseable", "canmoveaxis?Axis=abc&ClientID=7&ClientTransactionID=33");
        await Send("destinationsideofpier RightAscension=25", "number out of range", "destinationsideofpier?RightAscension=25&Declination=45&ClientID=7&ClientTransactionID=34");
        await Send("destinationsideofpier RightAscension=abc", "unparseable", "destinationsideofpier?RightAscension=abc&Declination=45&ClientID=7&ClientTransactionID=35");
        await Send("name ClientTransactionID=5", "valid", "name?ClientID=7&ClientTransactionID=5");
        foreach (var txid in new[] { "abc", "-1", "4294967296", "1.5" })
            await Send($"name ClientTransactionID={txid}", "transaction id", $"name?ClientID=7&ClientTransactionID={Uri.EscapeDataString(txid)}");

        var actual = new StringBuilder("Requests sent:\n");
        foreach (var p in probes) actual.AppendLine($"  {p.Label} ({p.Kind}): {p.Shown}");

        var enumRefused = probes.Where(p => p.Kind == "enum out of range").ToList() is { Count: > 0 } er && er.All(p => p.Refused400);
        var numberReaches = probes.FirstOrDefault(p => p.Kind == "number out of range")?.ReachedDriver == true;
        var txidRefused = probes.Where(p => p.Kind == "transaction id" && p.Refused400).Select(p => p.Label.Split('=')[1]).ToList();
        var problems = new List<string>();
        if (enumRefused)
            problems.Add("an axis out of range is refused with HTTP 400 by the server's request validation (the parameter is bound as an enum), so the driver never sees it" +
                         (numberReaches ? "; a number out of range (RA 25) does reach the driver" : ""));
        if (txidRefused.Count > 0)
            problems.Add($"a ClientTransactionID that isn't a uint32 ({string.Join(", ", txidRefused)}) is refused with HTTP 400 instead of being taken as 0");
        var outcome = enumRefused ? Outcome.Fail : txidRefused.Count > 0 ? Outcome.Warning : Outcome.Info;

        // The code: the controller's enum parameters and its ClientTransactionID binding, and any model state handling.
        var fix = new StringBuilder();
        if (ctx.Locator.RootOf(ctx.Target.Codebase) is null) actual.AppendLine(NoSource(ctx));
        else
        {
            var enums = ctx.Locator.Search(ctx.Target.Codebase, @"\]\s*(TelescopeAxis|GuideDirection|DriveRate|PierSide|PointingState|AlignmentMode)\s+\w+\s*[,)=]", 200);
            var txids = ctx.Locator.Search(ctx.Target.Codebase, @"\b(uint|int|long)\s+ClientTransactionID\b", 10000);
            var filters = ctx.Locator.Search(ctx.Target.Codebase, @"SuppressModelStateInvalidFilter|InvalidModelStateResponseFactory", 4);
            if (enums.Count > 0)
            {
                actual.AppendLine($"Code reviewed: {enums.Count} action parameter(s) bound as an enum, e.g.:");
                actual.AppendLine(Quote(enums.Take(3)));
                if (enumRefused)
                    fix.Append($"Take each enum parameter as an int and cast it in the call ({enums.Count} of them, e.g. {enums[0].Location}), so the driver judges the value " +
                               "and its InvalidValueException comes back as HTTP 200 with ErrorNumber 0x401. ");
            }
            if (txids.Count > 0)
            {
                actual.AppendLine($"ClientTransactionID is bound as a number in {txids.Count} place(s), e.g.:");
                actual.AppendLine(Quote(txids.Take(2)));
                if (txidRefused.Count > 0)
                    fix.Append("Take ClientTransactionID (and ClientID) as a string and parse it with uint.TryParse, using 0 when it isn't a uint32 " +
                               $"(e.g. {txids[0].Location}). ");
            }
            if (filters.Count > 0)
            {
                actual.AppendLine("Model state handling:");
                actual.AppendLine(Quote(filters));
            }
        }
        if (fix.Length == 0 && problems.Count > 0)
        {
            if (enumRefused) fix.Append("Let a value that parses through to the driver (bind it as a number, not an enum or with [Range]) so the driver's InvalidValueException comes back as HTTP 200 with ErrorNumber 0x401. ");
            if (txidRefused.Count > 0) fix.Append("Treat a ClientTransactionID that isn't a uint32 as 0 instead of refusing the request. ");
        }
        if (problems.Count > 0) fix.Append("Keep HTTP 400 for values that can't be parsed at all (Axis=abc).");

        ctx.Report("Alpaca.Binding", "Alpaca server", outcome,
            problems.Count == 0 ? "The server lets values that parse through to the driver, and takes a bad ClientTransactionID as 0."
                : "The Alpaca server: " + string.Join("; and ", problems) + ".",
            expected: said + "\n\nExpected: HTTP 200 with ErrorNumber 0x401 for a value that parses but is out of range; HTTP 400 only for one that can't be parsed; a bad ClientTransactionID answered with 0.",
            actual: actual.ToString().TrimEnd(), fix: problems.Count == 0 ? null : fix.ToString().Trim());
    }

    /// <summary>The setup pages: whether they're missing or throw, and the error they throw.</summary>
    private static async Task SetupPagesAsync(CheckContext ctx, HttpClient http, string said)
    {
        var actual = new StringBuilder();
        var throws = new List<string>();
        var missing = new List<string>();
        foreach (var path in new[] { "setup", $"setup/v1/telescope/{ctx.Target.DeviceNumber}/setup" })
        {
            var r = await ProtocolChecks.GetAsync(ctx, http, path);
            var html = r.Status == HttpStatusCode.OK && (r.ContentType?.Contains("html", StringComparison.OrdinalIgnoreCase) ?? false);
            var error = (int)r.Status >= 500 ? ErrorIn(r.Body) : null;
            actual.AppendLine($"GET /{path}: {(r.Error is not null ? r.Shown : $"HTTP {(int)r.Status} {r.ContentType}")}{(error is null ? "" : $": {error}")}");
            if ((int)r.Status >= 500) throws.Add($"/{path}");
            else if (!html) missing.Add($"/{path}");
        }

        // The server's page and the telescope's (a shared setup controller holds every device's routes).
        var routes = ctx.Locator.Search(ctx.Target.Codebase, @"[""']/?setup(/v1/telescope/[^""']*)?/?[""']", 6);
        if (ctx.Locator.RootOf(ctx.Target.Codebase) is null) actual.AppendLine(NoSource(ctx));
        else if (routes.Count > 0)
        {
            actual.AppendLine("Code reviewed: the setup routes:");
            actual.AppendLine(Quote(routes));
        }
        else actual.AppendLine("Code reviewed: no setup route found.");

        var outcome = throws.Count > 0 ? Outcome.Fail : missing.Count > 0 ? Outcome.Warning : Outcome.Info;
        var message = throws.Count > 0 ? $"The setup page{(throws.Count == 1 ? "" : "s")} {string.Join(" and ", throws)} exist{(throws.Count == 1 ? "s" : "")} but throw{(throws.Count == 1 ? "s" : "")} (HTTP 500)."
            : missing.Count > 0 ? $"There's no setup page at {string.Join(" and ", missing)}."
            : "The setup pages now return HTML.";
        var fix = throws.Count > 0
            ? "The page is there but fails as it's built: fix the error it shows (run the server with ASPNETCORE_ENVIRONMENT=Development to see the whole stack trace)."
            : missing.Count > 0 ? "Add the setup pages: /setup for the server and /setup/v1/telescope/{n}/setup for the device, each returning HTML." : null;
        ctx.Report("Alpaca.Setup", "Alpaca server", outcome, message,
            expected: said + "\n\nExpected: HTTP 200 with an HTML page at /setup and /setup/v1/telescope/{n}/setup.",
            actual: actual.ToString().TrimEnd(), fix: fix);
    }

    /// <summary>The error an HTTP 500 page shows: the first line naming an exception, or the start of its text.</summary>
    private static string? ErrorIn(string body)
    {
        var text = WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(body, @"<(script|style)[\s\S]*?</\1>", " ", RegexOptions.IgnoreCase), "<[^>]+>", "\n"));
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var line = lines.FirstOrDefault(l => l.Contains("Exception", StringComparison.Ordinal)) ?? lines.FirstOrDefault();
        return line is null ? null : line.Length > 200 ? line[..200] + "…" : line;
    }
}
