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
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace GS.TestHarness.Engine.Checks;

/// <summary>
/// The Alpaca HTTP protocol itself, below the interface: the management API, transaction IDs, the JSON reply's
/// shape, how errors and bad requests are answered, and discovery. Alpaca only.
/// </summary>
public sealed class ProtocolChecks : ICheckGroup
{
    public const string ApiReference = "https://ascom-standards.org/api/";

    public string Name => "Protocol";
    public string Description => "Alpaca only: management API, ClientTransactionID/ServerTransactionID, JSON reply shape, error numbers, HTTP 400 for bad requests, parameter casing and UDP discovery.";

    public string? SkipReason(CheckContext ctx)
        => ctx.Target.Kind != TargetKind.Alpaca ? "it's a COM driver, not an Alpaca server"
            : !ctx.Options.IncludeProtocol ? "protocol checks are turned off for this run" : null;

    private static readonly string[] ReplyFields = ["ClientTransactionID", "ServerTransactionID", "ErrorNumber", "ErrorMessage"];

    /// <summary>An HTTP client for the target's Alpaca server (with its credentials, if any).</summary>
    internal static HttpClient CreateHttp(CheckContext ctx)
    {
        var http = new HttpClient { BaseAddress = new Uri(ctx.Target.BaseUrl), Timeout = ctx.CallTimeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GS.TestHarness/1.0");
        if (!string.IsNullOrEmpty(ctx.Target.UserName))
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ctx.Target.UserName}:{ctx.Target.Password}")));
        return http;
    }

    /// <summary>The device's path on the Alpaca server.</summary>
    internal static string DevicePath(CheckContext ctx) => $"api/v1/telescope/{ctx.Target.DeviceNumber}";

    public async Task RunAsync(CheckContext ctx)
    {
        using var http = CreateHttp(ctx);
        var device = DevicePath(ctx);

        await ManagementAsync(ctx, http);
        await TransactionIdsAsync(ctx, http, device);
        await ReplyShapeAsync(ctx, http, device);
        await ErrorsAsync(ctx, http, device);
        await BadRequestsAsync(ctx, http, device);
        await MoreRequestsAsync(ctx, http, device);
        await DiscoveryAsync(ctx);
        await DiscoveryWideAsync(ctx);
    }

    internal sealed record Reply(HttpStatusCode Status, JsonElement? Json, string Body, double Ms, string? ContentType, Exception? Error)
    {
        public bool IsJson => Json is { ValueKind: JsonValueKind.Object };
        public JsonElement? Field(string name) => IsJson && Json!.Value.TryGetProperty(name, out var v) ? v : null;

        /// <summary>A field matched without regard to case, for telling a wrongly cased name from a missing one.</summary>
        public string? FieldNameIgnoringCase(string name)
            => IsJson ? Json!.Value.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Name : null;

        public string Shown => Error is not null ? $"{Error.GetType().Name}: {Error.Message}" : $"HTTP {(int)Status}: {Trim(Body)}";
        private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;
    }

    internal static async Task<Reply> SendAsync(CheckContext ctx, HttpClient http, HttpMethod method, string path,
        IEnumerable<KeyValuePair<string, string>>? form = null)
    {
        ctx.Token.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (form is not null) request.Content = new FormUrlEncodedContent(form);
            using var response = await http.SendAsync(request, ctx.Token);
            var body = await response.Content.ReadAsStringAsync(ctx.Token);
            JsonElement? json = null;
            try { json = JsonDocument.Parse(body).RootElement.Clone(); } catch (JsonException) { }
            return new Reply(response.StatusCode, json, body, watch.Elapsed.TotalMilliseconds, response.Content.Headers.ContentType?.MediaType, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ctx.Token.IsCancellationRequested)
        {
            return new Reply(0, null, "", watch.Elapsed.TotalMilliseconds, null, ex);
        }
    }

    internal static Task<Reply> GetAsync(CheckContext ctx, HttpClient http, string path) => SendAsync(ctx, http, HttpMethod.Get, path);

    private static Task<Reply> PutAsync(CheckContext ctx, HttpClient http, string path, params (string Key, string Value)[] form)
        => SendAsync(ctx, http, HttpMethod.Put, path, form.Select(f => KeyValuePair.Create(f.Key, f.Value)));

    // ---------- Management API ----------

    private static async Task ManagementAsync(CheckContext ctx, HttpClient http)
    {
        var versions = await GetAsync(ctx, http, "management/apiversions?ClientID=1&ClientTransactionID=1");
        var hasV1 = versions.Field("Value") is { ValueKind: JsonValueKind.Array } arr && arr.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.Number && v.GetInt32() == 1);
        ctx.Report("Management.ApiVersions", "/management/apiversions", hasV1 ? Outcome.Pass : Outcome.Fail,
            hasV1 ? "apiversions lists version 1." : "apiversions doesn't list version 1.",
            expected: "{\"Value\":[1], ...}", actual: versions.Shown, ms: versions.Ms, reference: ApiReference,
            fix: "GET /management/apiversions must return a JSON reply whose Value is an array of the supported API versions, [1].",
            sourcePatterns: ["apiversions"]);

        var description = await GetAsync(ctx, http, "management/v1/description?ClientID=1&ClientTransactionID=2");
        var value = description.Field("Value");
        var missing = new[] { "ServerName", "Manufacturer", "ManufacturerVersion", "Location" }
            .Where(f => value is not { ValueKind: JsonValueKind.Object } v || !v.TryGetProperty(f, out _)).ToList();
        ctx.Report("Management.Description", "/management/v1/description", missing.Count == 0 ? Outcome.Pass : Outcome.Fail,
            missing.Count == 0 ? "description has ServerName, Manufacturer, ManufacturerVersion and Location." : $"description is missing {string.Join(", ", missing)}.",
            actual: description.Shown, ms: description.Ms, reference: ApiReference,
            fix: "GET /management/v1/description must return Value = { ServerName, Manufacturer, ManufacturerVersion, Location }.",
            sourcePatterns: ["ManufacturerVersion", "management/v1/description"]);
        if (value is { ValueKind: JsonValueKind.Object } d)
        {
            string S(string n) => d.TryGetProperty(n, out var p) ? p.ToString() : "";
            ctx.Identity = ctx.Identity with { ServerDescription = $"{S("ServerName")} {S("ManufacturerVersion")} ({S("Manufacturer")})".Trim() };
        }

        var devices = await GetAsync(ctx, http, "management/v1/configureddevices?ClientID=1&ClientTransactionID=3");
        var list = devices.Field("Value") is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray().ToList() : null;
        var ours = list?.FirstOrDefault(e => e.ValueKind == JsonValueKind.Object
                                             && e.TryGetProperty("DeviceType", out var t) && string.Equals(t.GetString(), "Telescope", StringComparison.OrdinalIgnoreCase)
                                             && e.TryGetProperty("DeviceNumber", out var n) && n.ValueKind == JsonValueKind.Number && n.GetInt32() == ctx.Target.DeviceNumber);
        string? problem = list is null ? "Value isn't an array"
            : ours is not { ValueKind: JsonValueKind.Object } o ? $"telescope {ctx.Target.DeviceNumber} isn't listed"
            : new[] { "DeviceName", "DeviceType", "DeviceNumber", "UniqueID" }.FirstOrDefault(f => !o.TryGetProperty(f, out _)) is { } f ? $"its entry has no {f}"
            : string.IsNullOrWhiteSpace(o.GetProperty("UniqueID").GetString()) ? "its UniqueID is empty" : null;
        ctx.Report("Management.ConfiguredDevices", "/management/v1/configureddevices", problem is null ? Outcome.Pass : Outcome.Fail,
            problem is null ? $"configureddevices lists telescope {ctx.Target.DeviceNumber}." : $"configureddevices: {problem}.",
            actual: devices.Shown, ms: devices.Ms, reference: ApiReference,
            fix: "GET /management/v1/configureddevices must list every device as { DeviceName, DeviceType, DeviceNumber, UniqueID }, " +
                 "with a UniqueID that never changes (a GUID), so clients can recognise the device.",
            sourcePatterns: ["configureddevices", "UniqueID"]);

        // The UniqueID must never change: compare with the last saved run of this device.
        if (ours is { ValueKind: JsonValueKind.Object } me && me.TryGetProperty("UniqueID", out var uid) && uid.GetString() is { Length: > 0 } uniqueId)
        {
            ctx.Identity = ctx.Identity with { UniqueId = uniqueId };
            if (ctx.Previous?.Identity.UniqueId is { Length: > 0 } before)
                ctx.Report("Management.UniqueIdStable", "/management/v1/configureddevices",
                    string.Equals(before, uniqueId, StringComparison.OrdinalIgnoreCase) ? Outcome.Pass : Outcome.Fail,
                    string.Equals(before, uniqueId, StringComparison.OrdinalIgnoreCase)
                        ? $"UniqueID is the same as in the run of {ctx.Previous.Started:yyyy-MM-dd HH:mm}."
                        : $"UniqueID changed since the run of {ctx.Previous.Started:yyyy-MM-dd HH:mm}.",
                    expected: before, actual: uniqueId, reference: ApiReference,
                    fix: "A device's UniqueID must stay the same for ever (across restarts, reinstalls and upgrades): clients " +
                         "use it to recognise the device. Generate it once and save it in the settings.",
                    sourcePatterns: ["UniqueID", "UniqueId"]);
            else
                ctx.Report("Management.UniqueIdStable", "/management/v1/configureddevices", Outcome.Info,
                    "No earlier run of this device to compare the UniqueID with; the next run will.", actual: uniqueId);
        }
        if (list is not null)
        {
            var ids = list.Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("UniqueID", out _)).Select(e => e.GetProperty("UniqueID").GetString()).ToList();
            if (ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Count)
                ctx.Report("Management.UniqueIds", "/management/v1/configureddevices", Outcome.Fail, "Two devices share a UniqueID.",
                    actual: string.Join(", ", ids), reference: ApiReference,
                    fix: "Each device needs its own UniqueID.", sourcePatterns: ["UniqueID"]);
        }
    }

    // ---------- Transaction IDs ----------

    private static async Task TransactionIdsAsync(CheckContext ctx, HttpClient http, string device)
    {
        var first = await GetAsync(ctx, http, $"{device}/name?ClientID=7&ClientTransactionID=4242");
        if (!first.IsJson)
        {
            ctx.Report("Reply.Json", $"GET {device}/name", Outcome.Fail, "The reply isn't a JSON object.", actual: first.Shown,
                ms: first.Ms, reference: ApiReference, fix: "Every Alpaca device reply must be a JSON object.");
            return;
        }
        var echoed = first.Field("ClientTransactionID");
        ctx.Report("ClientTransactionID.Echo", "ClientTransactionID",
            echoed is { ValueKind: JsonValueKind.Number } e && e.GetInt64() == 4242 ? Outcome.Pass : Outcome.Fail,
            "The reply's ClientTransactionID " + (echoed is { ValueKind: JsonValueKind.Number } e2 && e2.GetInt64() == 4242 ? "matches the request's." : "doesn't match the request's."),
            expected: "4242", actual: echoed?.ToString() ?? "(missing)", ms: first.Ms, reference: ApiReference,
            fix: "Return the request's ClientTransactionID unchanged in every reply, so clients can match replies to requests.",
            sourcePatterns: ["ClientTransactionID"]);

        var second = await GetAsync(ctx, http, $"{device}/name?ClientID=7&ClientTransactionID=4243");
        var s1 = first.Field("ServerTransactionID");
        var s2 = second.Field("ServerTransactionID");
        var ok = s1 is { ValueKind: JsonValueKind.Number } a && s2 is { ValueKind: JsonValueKind.Number } b && a.GetInt64() > 0 && b.GetInt64() > a.GetInt64();
        ctx.Report("ServerTransactionID.Increases", "ServerTransactionID", ok ? Outcome.Pass : Outcome.Fail,
            ok ? "ServerTransactionID is positive and increases." : "ServerTransactionID is missing, zero or doesn't increase.",
            expected: "a positive number that increases with each reply", actual: $"{s1?.ToString() ?? "(missing)"}, then {s2?.ToString() ?? "(missing)"}",
            reference: ApiReference, fix: "Give every reply a ServerTransactionID from a counter that starts at 1 and increases.",
            sourcePatterns: ["ServerTransactionID"]);

        // Query parameter names are case-insensitive.
        var lower = await GetAsync(ctx, http, $"{device}/name?clientid=7&clienttransactionid=4244");
        var lowerOk = lower.Status == HttpStatusCode.OK && lower.Field("ClientTransactionID") is { ValueKind: JsonValueKind.Number } l && l.GetInt64() == 4244;
        ctx.Report("Query.CaseInsensitive", "query parameters", lowerOk ? Outcome.Pass : Outcome.Fail,
            lowerOk ? "Lower-case query parameter names (clienttransactionid) are accepted." : "Lower-case query parameter names aren't recognised.",
            expected: "ClientTransactionID 4244", actual: lower.Shown, reference: ApiReference,
            fix: "In GET requests, parameter names are case-insensitive: match ClientID and ClientTransactionID (and any " +
                 "other query parameter) ignoring case.", sourcePatterns: ["ClientTransactionID"]);

        // A missing or malformed ClientTransactionID is not an error.
        var none = await GetAsync(ctx, http, $"{device}/name");
        var noneOk = none.Status == HttpStatusCode.OK && none.Field("ClientTransactionID") is { ValueKind: JsonValueKind.Number } n && n.GetInt64() == 0;
        ctx.Report("ClientTransactionID.Missing", "ClientTransactionID", noneOk ? Outcome.Pass : Outcome.Warning,
            noneOk ? "Without a ClientTransactionID the reply has 0." : "Without a ClientTransactionID the reply doesn't have 0.",
            expected: "HTTP 200 with ClientTransactionID 0", actual: none.Shown, reference: ApiReference,
            fix: "When the request has no ClientTransactionID, reply normally with ClientTransactionID 0.", sourcePatterns: ["ClientTransactionID"]);

        var bad = await GetAsync(ctx, http, $"{device}/name?ClientID=7&ClientTransactionID=abc");
        var badOk = bad.Status == HttpStatusCode.OK && bad.Field("ClientTransactionID") is { ValueKind: JsonValueKind.Number } z && z.GetInt64() == 0;
        ctx.Report("ClientTransactionID.Invalid", "ClientTransactionID", badOk ? Outcome.Pass : Outcome.Warning,
            badOk ? "A non-numeric ClientTransactionID is answered with 0." : "A non-numeric ClientTransactionID isn't answered with 0.",
            expected: "HTTP 200 with ClientTransactionID 0", actual: bad.Shown, reference: ApiReference,
            fix: "A ClientTransactionID that isn't an unsigned 32-bit number should be treated as 0, not refused.",
            sourcePatterns: bad.Status == HttpStatusCode.BadRequest ? ["data validation error", "ClientTransactionID"] : ["ClientTransactionID"]);
    }

    // ---------- JSON reply shape ----------

    private static async Task ReplyShapeAsync(CheckContext ctx, HttpClient http, string device)
    {
        var reply = await GetAsync(ctx, http, $"{device}/rightascension?ClientID=7&ClientTransactionID=5");
        if (!reply.IsJson) return;
        var problems = new List<string>();
        foreach (var field in ReplyFields.Append("Value"))
        {
            if (reply.Field(field) is not null) continue;
            problems.Add(reply.FieldNameIgnoringCase(field) is { Length: > 0 } wrong ? $"{field} is spelled \"{wrong}\"" : $"{field} is missing");
        }
        if (reply.Field("ErrorNumber") is { ValueKind: JsonValueKind.Number } en && en.GetInt32() == 0
            && reply.Field("ErrorMessage") is { } em && em.ToString().Length > 0)
            problems.Add("ErrorMessage isn't empty although ErrorNumber is 0");
        if (reply.Field("Value") is { } v && v.ValueKind != JsonValueKind.Number)
            problems.Add($"Value is a {v.ValueKind}, not a number");
        if (reply.ContentType is { } ct && !ct.Contains("json", StringComparison.OrdinalIgnoreCase))
            problems.Add($"Content-Type is {ct}");
        ctx.Report("Reply.Shape", "JSON reply", problems.Count == 0 ? Outcome.Pass : Outcome.Fail,
            problems.Count == 0 ? "The reply has Value, ClientTransactionID, ServerTransactionID, ErrorNumber and ErrorMessage, cased exactly." : "The reply's JSON isn't as Alpaca requires.",
            actual: problems.Count == 0 ? reply.Shown : string.Join("; ", problems), reference: ApiReference,
            fix: "Alpaca replies use exactly these (case-sensitive) names: Value, ClientTransactionID, ServerTransactionID, " +
                 "ErrorNumber, ErrorMessage, with Content-Type application/json and an empty ErrorMessage on success. " +
                 "Strict clients (ConformU, the ASCOM library with StrictCasing) reject other casings.",
            sourcePatterns: ["ErrorMessage"]);

        foreach (var (path, member) in new[] { ($"{device}/devicestate", "devicestate"), ($"{device}/connecting", "connecting") })
        {
            var r = await GetAsync(ctx, http, $"{path}?ClientID=7&ClientTransactionID=6");
            var ok = r.Status == HttpStatusCode.OK && r.Field("ErrorNumber") is { ValueKind: JsonValueKind.Number } n && n.GetInt32() == 0;
            ctx.Report($"Endpoint.{member}", $"GET {member}", ok ? Outcome.Pass : Outcome.Fail,
                ok ? $"GET {member} answers." : $"GET {member} doesn't answer as ITelescopeV4 requires.",
                actual: r.Shown, reference: ApiReference,
                fix: $"Interface version 4 adds the {member} endpoint: implement GET /api/v1/telescope/{{n}}/{member}.",
                sourcePatterns: [member]);
        }
    }

    // ---------- Errors are replies with an ErrorNumber, not HTTP errors ----------

    private static async Task ErrorsAsync(CheckContext ctx, HttpClient http, string device)
    {
        var r = await GetAsync(ctx, http, $"{device}/canmoveaxis?Axis=9&ClientID=7&ClientTransactionID=7");
        var number = r.Field("ErrorNumber") is { ValueKind: JsonValueKind.Number } n ? n.GetInt32() : (int?)null;
        var ok = r.Status == HttpStatusCode.OK && number == 0x401;
        ctx.Report("Errors.InvalidValue", "ErrorNumber", ok ? Outcome.Pass : Outcome.Fail,
            ok ? "An invalid value is answered with HTTP 200 and ErrorNumber 0x401." : "An invalid value isn't answered with HTTP 200 and ErrorNumber 0x401.",
            expected: "HTTP 200, ErrorNumber 1025 (0x401), a non-empty ErrorMessage", actual: r.Shown, reference: ApiReference,
            fix: r.Status != HttpStatusCode.OK
                ? "A device error (here, an invalid axis) is a normal reply: HTTP 200 with ErrorNumber and ErrorMessage set. " +
                  "HTTP 4xx is only for requests the server can't understand, and 5xx for server faults. Catch the driver's " +
                  "exception and turn it into ErrorNumber/ErrorMessage."
                : "Map InvalidValueException to ErrorNumber 0x401 (1025).",
            sourcePatterns: r.Status == HttpStatusCode.BadRequest ? ["data validation error", "InvalidModelStateResponseFactory"] : ["InvalidValueException", "ErrorNumber"]);
        if (ok && r.Field("ErrorMessage") is { } m && string.IsNullOrWhiteSpace(m.GetString()))
            ctx.Report("Errors.Message", "ErrorMessage", Outcome.Warning, "An error reply has an empty ErrorMessage.",
                reference: ApiReference, fix: "Put a description of the error in ErrorMessage whenever ErrorNumber isn't 0.",
                sourcePatterns: ["ErrorMessage"]);
    }

    // ---------- Requests the server must refuse with HTTP 400 ----------

    private static async Task BadRequestsAsync(CheckContext ctx, HttpClient http, string device)
    {
        async Task Expect400(string id, string member, string what, Reply reply, string fix, Outcome otherwise = Outcome.Fail, params string[] patterns)
        {
            var ok = reply.Status == HttpStatusCode.BadRequest;
            ctx.Report(id, member, ok ? Outcome.Pass : otherwise, ok ? $"{what}: HTTP 400, as required." : $"{what}: not refused with HTTP 400.",
                expected: "HTTP 400 Bad Request", actual: reply.Shown, ms: reply.Ms, reference: ApiReference, fix: fix, sourcePatterns: patterns);
        }

        await Expect400("BadRequest.DeviceNumber", "device number", "GET for telescope 99 (not configured)",
            await GetAsync(ctx, http, "api/v1/telescope/99/name?ClientID=7&ClientTransactionID=8"),
            "A request for a device number that isn't configured must get HTTP 400.", patterns: ["DeviceNumber"]);

        await Expect400("BadRequest.MissingParameter", "PUT parameters", "PUT targetrightascension without TargetRightAscension",
            await PutAsync(ctx, http, $"{device}/targetrightascension", ("ClientID", "7"), ("ClientTransactionID", "9")),
            "A PUT that lacks a required parameter must get HTTP 400 (with the problem in the body), before the driver is called.",
            patterns: ["TargetRightAscension", "BadRequest"]);

        await Expect400("BadRequest.BadNumber", "PUT parameters", "PUT TargetRightAscension=abc",
            await PutAsync(ctx, http, $"{device}/targetrightascension", ("TargetRightAscension", "abc"), ("ClientID", "7"), ("ClientTransactionID", "10")),
            "A parameter that can't be parsed (a number that isn't one) must get HTTP 400.", patterns: ["TargetRightAscension", "BadRequest"]);

        await Expect400("BadRequest.BadBoolean", "PUT parameters", "PUT Tracking=maybe",
            await PutAsync(ctx, http, $"{device}/tracking", ("Tracking", "maybe"), ("ClientID", "7"), ("ClientTransactionID", "11")),
            "A boolean parameter that isn't True or False must get HTTP 400.", patterns: ["Tracking", "BadRequest"]);

        // PUT body parameter names are case-sensitive in Alpaca; a wrongly cased name is a missing parameter.
        await Expect400("BadRequest.PutCasing", "PUT parameters", "PUT targetrightascension=12 (wrongly cased name)",
            await PutAsync(ctx, http, $"{device}/targetrightascension", ("targetrightascension", "12"), ("ClientID", "7"), ("ClientTransactionID", "12")),
            "PUT form parameter names are case-sensitive in the Alpaca API: a wrongly cased name should be treated as missing " +
            "(HTTP 400). Accepting it hides client bugs that other servers will reject.", Outcome.Warning, "BadRequest");

        var unknown = await GetAsync(ctx, http, $"{device}/nosuchmember?ClientID=7&ClientTransactionID=13");
        var unknownOk = unknown.Status is HttpStatusCode.BadRequest or HttpStatusCode.NotFound;
        ctx.Report("BadRequest.UnknownMember", "unknown member", unknownOk ? Outcome.Pass : Outcome.Warning,
            unknownOk ? $"An unknown member gets HTTP {(int)unknown.Status}." : "An unknown member isn't refused with HTTP 400 or 404.",
            expected: "HTTP 400 (or 404)", actual: unknown.Shown, reference: ApiReference,
            fix: "A request for a member the device type doesn't have should get HTTP 400.");
    }

    // ---------- Discovery ----------

    // ---------- UTCDate format, locale, setup pages, HTTP methods ----------

    private static async Task MoreRequestsAsync(CheckContext ctx, HttpClient http, string device)
    {
        // UTCDate in JSON: "ISO 8601 format with 'Z' suffix to indicate UTC time".
        var utc = await GetAsync(ctx, http, $"{device}/utcdate?ClientID=7&ClientTransactionID=20");
        if (utc.Field("Value") is { ValueKind: JsonValueKind.String } v && v.GetString() is { } text)
        {
            var iso = text.EndsWith('Z') && DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) && parsed.Kind == DateTimeKind.Utc;
            ctx.Report("UTCDate.Format", "UTCDate", iso ? Outcome.Pass : Outcome.Fail,
                iso ? $"UTCDate is sent as ISO 8601 UTC (\"{text}\")." : $"UTCDate is sent as \"{text}\", not ISO 8601 with a Z.",
                expected: "e.g. \"2026-11-14T20:15:30.1234567Z\"", actual: text, reference: CheckContext.DocsBase + "UTCDate",
                fix: "Over Alpaca, \"the DateTime value must be in ISO 8601 format with 'Z' suffix to indicate UTC time\": format it " +
                     "with DateTime.UtcNow.ToString(\"o\") (a DateTime of Kind Utc), not local time or a culture format.",
                sourcePatterns: ["UTCDate"]);
        }

        // Numbers are invariant culture: a decimal comma is not a number.
        var comma = await PutAsync(ctx, http, $"{device}/targetrightascension", ("TargetRightAscension", "12,5"), ("ClientID", "7"), ("ClientTransactionID", "21"));
        var commaOk = comma.Status == HttpStatusCode.BadRequest
                      || comma.Field("ErrorNumber") is { ValueKind: JsonValueKind.Number } en && en.GetInt32() == 0x401;
        ctx.Report("BadRequest.DecimalComma", "PUT parameters", commaOk ? Outcome.Pass : Outcome.Fail,
            commaOk ? "\"12,5\" (a decimal comma) is refused." : "\"12,5\" (a decimal comma) was accepted as a number.",
            expected: "HTTP 400 (or ErrorNumber 0x401)", actual: comma.Shown, reference: ApiReference,
            fix: "Alpaca numbers always use a decimal point. Parse with CultureInfo.InvariantCulture: parsing with the PC's culture " +
                 "makes \"12,5\" 125 (or 12.5) on some systems and \"12.5\" fail on others (German, French...).",
            sourcePatterns: ["CultureInfo", "double.Parse", "Convert.ToDouble"]);

        // Setup pages.
        foreach (var (path, id) in new[] { ("setup", "Setup.Server"), ($"setup/v1/telescope/{ctx.Target.DeviceNumber}/setup", "Setup.Device") })
        {
            var r = await GetAsync(ctx, http, path);
            var html = r.Status == HttpStatusCode.OK && (r.ContentType?.Contains("html", StringComparison.OrdinalIgnoreCase) ?? false);
            ctx.Report(id, $"GET /{path}", html ? Outcome.Pass : Outcome.Warning,
                html ? $"GET /{path} returns an HTML page." : $"GET /{path} doesn't return an HTML page.",
                expected: "HTTP 200, text/html", actual: r.Error is null ? $"HTTP {(int)r.Status} {r.ContentType}" : r.Shown, reference: ApiReference,
                fix: "Alpaca servers provide HTML setup pages: /setup for the server and /setup/v1/{type}/{n}/setup for each " +
                     "device, so a user can configure it from a browser.",
                sourcePatterns: ["/setup", "setup/v1"]);
        }

        // Methods other than GET/PUT.
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Delete })
        {
            var r = await SendAsync(ctx, http, method, $"{device}/name?ClientID=7&ClientTransactionID=22");
            var ok = r.Status is HttpStatusCode.MethodNotAllowed or HttpStatusCode.BadRequest or HttpStatusCode.NotFound;
            ctx.Report($"BadRequest.Method{method.Method}", $"{method.Method} request", ok ? Outcome.Pass : (int)r.Status >= 500 ? Outcome.Fail : Outcome.Warning,
                ok ? $"{method.Method} to a device member is refused (HTTP {(int)r.Status})." : $"{method.Method} to a device member got HTTP {(int)r.Status}.",
                expected: "HTTP 405 (or 400/404)", actual: r.Shown, reference: ApiReference,
                fix: (int)r.Status >= 500 ? "A request with the wrong HTTP method crashed the server (HTTP 5xx): route only GET and PUT to device members."
                    : "Device members are GET (read) or PUT (write); anything else should be refused with 405 Method Not Allowed.");
        }
    }

    /// <summary>Discovery by broadcast (IPv4) and by the IPv6 multicast group, as clients on the network send it.</summary>
    private static async Task DiscoveryWideAsync(CheckContext ctx)
    {
        async Task<List<string>> AskAsync(IPEndPoint to, AddressFamily family, bool broadcast)
        {
            var replies = new List<string>();
            try
            {
                using var udp = new UdpClient(family) { EnableBroadcast = broadcast };
                if (family == AddressFamily.InterNetworkV6) udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
                await udp.SendAsync(Encoding.ASCII.GetBytes("alpacadiscovery1"), to, ctx.Token);
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
        bool Ours(string reply) => reply.Contains($"\"AlpacaPort\":{ctx.Target.Port}") || reply.Contains($"\"AlpacaPort\": {ctx.Target.Port}");

        var v4 = await AskAsync(new IPEndPoint(IPAddress.Broadcast, 32227), AddressFamily.InterNetwork, true);
        ctx.Report("Discovery.Broadcast", "UDP discovery", v4.Any(Ours) ? Outcome.Pass : Outcome.Warning,
            v4.Any(Ours) ? $"Broadcast discovery found this server ({v4.Count} repl{(v4.Count == 1 ? "y" : "ies")} in all)."
                : $"Broadcast discovery got {v4.Count} repl{(v4.Count == 1 ? "y" : "ies")}, none with port {ctx.Target.Port}.",
            actual: string.Join(" ", v4.Take(5)), reference: ApiReference,
            fix: "Clients find servers by broadcasting \"alpacadiscovery1\" to UDP 32227: listen on all addresses (not just " +
                 "localhost) and let UDP 32227 through the firewall.",
            sourcePatterns: ["alpacadiscovery", "32227"]);

        var v6 = await AskAsync(new IPEndPoint(IPAddress.Parse("ff12::a1:9aca"), 32227), AddressFamily.InterNetworkV6, false);
        ctx.Report("Discovery.IPv6", "UDP discovery", v6.Any(Ours) ? Outcome.Pass : Outcome.Info,
            v6.Any(Ours) ? "IPv6 discovery (multicast ff12::a1:9aca) found this server." : "No answer to IPv6 discovery (multicast ff12::a1:9aca).",
            actual: string.Join(" ", v6.Take(5)), reference: ApiReference);
    }

    private static async Task DiscoveryAsync(CheckContext ctx)
    {
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(ctx.Target.Host, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(ctx.Target.Host, ctx.Token);
        }
        catch (SocketException ex)
        {
            ctx.Report("Discovery", "UDP discovery", Outcome.Error, $"Couldn't resolve {ctx.Target.Host}: {ex.Message}");
            return;
        }
        var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
        if (address is null) return;
        if (address.Equals(IPAddress.IPv6Loopback)) address = IPAddress.Loopback;

        using var udp = new UdpClient(address.AddressFamily);
        var request = Encoding.ASCII.GetBytes("alpacadiscovery1");
        string? reply = null;
        var watch = Stopwatch.StartNew();
        try
        {
            await udp.SendAsync(request, new IPEndPoint(address, 32227), ctx.Token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var result = await udp.ReceiveAsync(timeout.Token);
            reply = Encoding.UTF8.GetString(result.Buffer);
        }
        catch (OperationCanceledException) when (!ctx.Token.IsCancellationRequested) { }
        catch (SocketException) { }

        if (reply is null)
        {
            ctx.Report("Discovery", "UDP discovery", Outcome.Warning, $"No answer to an Alpaca discovery request sent to {address}:32227.",
                expected: "{\"AlpacaPort\": " + ctx.Target.Port + "}", actual: "no reply in 3 s", reference: ApiReference,
                fix: "Alpaca servers should answer \"alpacadiscovery1\" on UDP port 32227 with {\"AlpacaPort\": <port>}, so " +
                     "clients can find them. Check that discovery is enabled and the firewall lets UDP 32227 in.",
                sourcePatterns: ["alpacadiscovery", "32227"]);
            return;
        }
        int? port = null;
        try
        {
            using var doc = JsonDocument.Parse(reply);
            if (doc.RootElement.TryGetProperty("AlpacaPort", out var p) && p.ValueKind == JsonValueKind.Number) port = p.GetInt32();
        }
        catch (JsonException) { }
        ctx.Report("Discovery", "UDP discovery", port == ctx.Target.Port ? Outcome.Pass : port is null ? Outcome.Fail : Outcome.Warning,
            port == ctx.Target.Port ? $"Discovery answered with AlpacaPort {port} in {watch.ElapsedMilliseconds} ms."
                : port is null ? "The discovery reply isn't {\"AlpacaPort\": n}." : $"Discovery answered with port {port}, not {ctx.Target.Port}.",
            expected: "{\"AlpacaPort\": " + ctx.Target.Port + "}", actual: reply, reference: ApiReference,
            fix: port is null ? "Reply to discovery with exactly {\"AlpacaPort\": <port>} (the name is case-sensitive)."
                : "Another Alpaca server on this computer may have answered, or the server reports the wrong port.",
            sourcePatterns: ["AlpacaPort", "alpacadiscovery"]);
    }
}
