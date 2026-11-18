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

using System.Reflection;
using System.Text.RegularExpressions;
using ASCOM.Common.DeviceInterfaces;

namespace GS.TestHarness.Engine;

/// <summary>How bad ConformU said a problem was.</summary>
public enum ConformUSeverity
{
    /// <summary>CONFIGURATION ALERT: the device's configuration, not its code.</summary>
    Alert,

    /// <summary>WARNING.</summary>
    Warning,

    /// <summary>ISSUE: a breach of the standard.</summary>
    Issue,

    /// <summary>ERROR: a test that failed outright.</summary>
    Error
}

/// <summary>One problem line from a ConformU log.</summary>
public sealed record ConformUProblem(int Line, string Time, string Test, ConformUSeverity Severity, string Message)
{
    /// <summary>Lines after the problem line that belong to it (an exception, a value listing).</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// The problems with one interface member (or topic), and the harness check groups that test it. No groups: no
/// existing check covers it, so the run makes one (<see cref="Checks.ConformUChecks"/>).
/// </summary>
public sealed record ConformUArea(string Member, IReadOnlyList<string> Groups, IReadOnlyList<ConformUProblem> Problems)
{
    public ConformUSeverity Severity => Problems.Max(p => p.Severity);

    /// <summary>A readable ITelescopeV4 property: a member with no existing check can still be probed by reading it.</summary>
    public bool CanProbe => ConformUMap.Property(Member) is not null;

    public bool Covered => Groups.Count > 0;
}

/// <summary>
/// The groups a run needs to look at a ConformU log's problems, the members it narrows them to (only the sections
/// and findings about the log's problem members), and the options those groups need.
/// </summary>
public sealed record ConformUPlan(IReadOnlyList<string> Groups, IReadOnlyList<string> Members)
{
    public bool NeedsMotion => Groups.Any(g => g is "Motion" or "ParkHome" or "Tracking");
    public bool NeedsWrites => Groups.Contains("Settings");
    public bool NeedsExtended => Groups.Any(g => g is "Tracking" or "Clients");
    public bool NeedsProtocol => Groups.Contains("Protocol");
    public bool NeedsDisconnected => Groups.Contains("Disconnected");
}

/// <summary>
/// A ConformU log (a normal or debug log): its problem lines (ISSUE, ERROR, CONFIGURATION ALERT, WARNING) grouped by the
/// member they are about. OK, INFO and DEBUG lines are counted but not kept.
/// </summary>
public sealed record ConformULog
{
    public required string FileName { get; init; }
    public string? ConformUVersion { get; init; }

    /// <summary>The device's Name and DriverVersion, as ConformU read them.</summary>
    public string? DeviceName { get; init; }
    public string? DriverVersion { get; init; }

    public int Lines { get; init; }
    public int OkLines { get; init; }
    public int DebugLines { get; init; }
    public IReadOnlyList<ConformUProblem> Problems { get; init; } = [];
    public IReadOnlyList<ConformUArea> Areas { get; init; } = [];

    /// <summary>Whether the file looked like a ConformU log at all (it had at least one result line).</summary>
    public bool Recognised => OkLines + DebugLines + Problems.Count > 0;

    /// <summary>
    /// Whether it's a debug log (ConformU run with debug logging on): only a debug log has DEBUG lines. The ConformU
    /// Debug Log Test takes debug logs only.
    /// </summary>
    public bool IsDebug => DebugLines > 0;

    public int Count(ConformUSeverity s) => Problems.Count(p => p.Severity == s);

    public ConformUPlan Plan()
    {
        var groups = new HashSet<string>(ConformUMap.AlwaysRun);
        foreach (var a in Areas) groups.UnionWith(a.Groups);
        groups.Add("ConformU");
        // In the runner's order, for display. The members: every problem area's (the runner's RunOptions.Members).
        return new ConformUPlan([.. ConformUMap.GroupOrder.Where(groups.Contains)],
            [.. Areas.Select(a => a.Member).Distinct(StringComparer.OrdinalIgnoreCase)]);
    }

    // "10:31:25.441 SlewToCoordinatesAsync              ISSUE    Slewed 1.2 arc seconds away..."
    private static readonly Regex ResultLine = new(
        @"^\s*(?<time>\d{1,2}:\d{2}:\d{2}(?:[.,]\d{1,4})?)\s+(?<test>\S.*?)\s+(?<outcome>OK|INFO|ISSUE|ERROR|WARNING|DEBUG|CONFIGURATION\s+ALERT|CONFIGALERT|ALERT)(?=\s|$)\s*(?<msg>.*)$",
        RegexOptions.Compiled);

    // "ConformU 4.1.0 (Build ...)", or 4.6's "ASCOM Universal Device Conformance Checker Version 4.6.0.56430, Build time: ..."
    private static readonly Regex Version = new(@"(?:ConformU|Conformance\s+Checker)\s+(?:version\s+)?v?(?<v>\d+\.\d+(?:\.\d+)*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Reads a ConformU log. Problems ConformU repeats in its end-of-run summary are kept once.</summary>
    public static async Task<ConformULog> ParseAsync(string fileName, TextReader reader, CancellationToken token = default)
    {
        var problems = new List<ConformUProblem>();
        var seen = new HashSet<(string, ConformUSeverity, string)>();
        string? version = null, name = null, driverVersion = null;
        int lines = 0, ok = 0, debug = 0;
        ConformUProblem? last = null;
        var detail = new List<string>();

        void Flush()
        {
            if (last is not null && detail.Count > 0)
                problems[^1] = last with { Detail = string.Join('\n', detail) };
            last = null;
            detail.Clear();
        }

        while (await reader.ReadLineAsync(token) is { } line)
        {
            lines++;
            if (version is null && Version.Match(line) is { Success: true } v) version = v.Groups["v"].Value;
            var m = ResultLine.Match(line);
            if (!m.Success)
            {
                // Lines that carry on a problem's message (an exception, a list) until the next result line.
                if (last is not null && line.Trim().Length > 0 && detail.Count < 12) detail.Add(line.TrimEnd());
                else if (line.Trim().Length == 0) Flush();
                continue;
            }
            Flush();
            var test = m.Groups["test"].Value.Trim();
            var message = m.Groups["msg"].Value.Trim();
            var outcome = Regex.Replace(m.Groups["outcome"].Value, @"\s+", " ");
            ConformUSeverity? severity = outcome switch
            {
                "ERROR" => ConformUSeverity.Error,
                "ISSUE" => ConformUSeverity.Issue,
                "WARNING" => ConformUSeverity.Warning,
                "CONFIGURATION ALERT" or "CONFIGALERT" or "ALERT" => ConformUSeverity.Alert,
                _ => null
            };
            if (severity is null)
            {
                if (outcome == "DEBUG") debug++;
                else ok++;
                if (outcome == "OK" && message.Length > 0)
                {
                    if (name is null && test.Equals("Name", StringComparison.OrdinalIgnoreCase)) name = message;
                    if (driverVersion is null && test.Equals("DriverVersion", StringComparison.OrdinalIgnoreCase)) driverVersion = message;
                }
                continue;
            }
            if (!seen.Add((test, severity.Value, message))) continue;
            last = new ConformUProblem(lines, m.Groups["time"].Value, test, severity.Value, message);
            problems.Add(last);
        }
        Flush();

        var areas = problems
            .Select(p => (Problem: p, Map: ConformUMap.Map(p)))
            .GroupBy(x => x.Map.Member, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ConformUArea(g.First().Map.Member,
                [.. ConformUMap.GroupOrder.Where(name => g.Any(x => x.Map.Groups.Contains(name)))],
                [.. g.Select(x => x.Problem)]))
            .OrderByDescending(a => a.Severity).ThenBy(a => a.Problems[0].Line)
            .ToList();

        return new ConformULog
        {
            FileName = fileName, ConformUVersion = version, DeviceName = name, DriverVersion = driverVersion,
            Lines = lines, OkLines = ok, DebugLines = debug, Problems = problems, Areas = areas
        };
    }

    public static Task<ConformULog> ParseAsync(string fileName, string text)
        => ParseAsync(fileName, new StringReader(text));
}

/// <summary>Which ITelescopeV4 member a ConformU test is about, and which harness groups check that member.</summary>
public static class ConformUMap
{
    /// <summary>The runner's group order.</summary>
    public static readonly string[] GroupOrder =
        ["Connection", "Protocol", "Common", "Capabilities", "Properties", "Methods", "Settings", "Motion", "ParkHome",
         "Tracking", "Clients", "Registration", "ConformU", "Performance", "Disconnected"];

    /// <summary>
    /// Groups every ConformU run includes: they only read, they're quick, and later groups depend on what they learn
    /// (capabilities, AtPark, tracking rates, axis rates).
    /// </summary>
    public static readonly string[] AlwaysRun = ["Connection", "Common", "Capabilities", "Properties", "Methods", "Performance"];

    /// <summary>The topic for Alpaca protocol problems (ConformU's protocol tests).</summary>
    public const string Protocol = "AlpacaProtocol";

    private static readonly Dictionary<string, string[]> Members = new(StringComparer.OrdinalIgnoreCase)
    {
        // Connection, and the clients and disconnected checks that exercise it.
        ["Connected"] = ["Connection", "Clients", "Disconnected"],
        ["Connecting"] = ["Connection", "Disconnected"],
        ["Connect"] = ["Connection", "Clients"],
        ["Disconnect"] = ["Clients", "Disconnected"],
        ["Name"] = ["Connection"],
        ["Description"] = ["Connection"],
        ["DriverInfo"] = ["Connection"],
        ["DriverVersion"] = ["Connection"],
        ["InterfaceVersion"] = ["Connection"],
        // Common.
        ["SupportedActions"] = ["Common"],
        ["Action"] = ["Common"],
        ["DeviceState"] = ["Common"],
        ["CommandBlind"] = ["Common"],
        ["CommandBool"] = ["Common"],
        ["CommandString"] = ["Common"],
        // Properties that are only read.
        ["AlignmentMode"] = ["Properties"],
        ["Altitude"] = ["Properties"],
        ["ApertureArea"] = ["Properties"],
        ["ApertureDiameter"] = ["Properties"],
        ["Azimuth"] = ["Properties"],
        ["Declination"] = ["Properties"],
        ["EquatorialSystem"] = ["Properties"],
        ["FocalLength"] = ["Properties"],
        ["IsPulseGuiding"] = ["Properties", "Motion"],
        ["RightAscension"] = ["Properties"],
        ["SiderealTime"] = ["Properties"],
        ["Slewing"] = ["Properties", "Motion"],
        ["UTCDate"] = ["Properties", "Settings"],
        ["TrackingRates"] = ["Properties"],
        // Properties that are written too.
        ["DoesRefraction"] = ["Properties", "Settings"],
        ["GuideRateDeclination"] = ["Properties", "Settings"],
        ["GuideRateRightAscension"] = ["Properties", "Settings"],
        ["SiteElevation"] = ["Properties", "Settings"],
        ["SiteLatitude"] = ["Properties", "Settings"],
        ["SiteLongitude"] = ["Properties", "Settings"],
        ["SlewSettleTime"] = ["Properties", "Settings"],
        ["TargetDeclination"] = ["Properties", "Settings", "Motion"],
        ["TargetRightAscension"] = ["Properties", "Settings", "Motion"],
        ["TrackingRate"] = ["Properties", "Settings", "Tracking"],
        ["Tracking"] = ["Properties", "Settings", "Motion", "Tracking"],
        ["RightAscensionRate"] = ["Properties", "Settings", "Motion", "Tracking"],
        ["DeclinationRate"] = ["Properties", "Settings", "Motion", "Tracking"],
        ["SideOfPier"] = ["Properties", "Motion"],
        // Methods.
        ["AxisRates"] = ["Methods", "Motion"],
        ["DestinationSideOfPier"] = ["Methods", "Motion"],
        ["CanMoveAxis"] = ["Capabilities"],
        ["AbortSlew"] = ["Motion"],
        ["MoveAxis"] = ["Motion"],
        ["PulseGuide"] = ["Motion"],
        ["SlewToAltAz"] = ["Motion"],
        ["SlewToAltAzAsync"] = ["Motion"],
        ["SlewToCoordinates"] = ["Motion"],
        ["SlewToCoordinatesAsync"] = ["Motion"],
        ["SlewToTarget"] = ["Motion"],
        ["SlewToTargetAsync"] = ["Motion"],
        ["SyncToAltAz"] = ["Motion"],
        ["SyncToCoordinates"] = ["Motion"],
        ["SyncToTarget"] = ["Motion"],
        // Park and home.
        ["AtHome"] = ["Properties", "ParkHome"],
        ["AtPark"] = ["Properties", "ParkHome"],
        ["FindHome"] = ["ParkHome"],
        ["Park"] = ["ParkHome"],
        ["Unpark"] = ["ParkHome"],
        ["SetPark"] = ["ParkHome"],
    };

    /// <summary>ConformU's names that differ from the member's.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AxisRate"] = "AxisRates",
        ["TrackingRatesSimple"] = "TrackingRates",
    };

    private static readonly Regex Words = new(@"[A-Za-z][A-Za-z0-9]*", RegexOptions.Compiled);

    /// <summary>The member a problem is about, and the groups that check it (none: no existing check).</summary>
    public static (string Member, string[] Groups) Map(ConformUProblem p)
    {
        var test = p.Test;
        if (IsProtocol(test)) return (Protocol, ["Protocol"]);

        var words = Words.Matches(test).Select(m => m.Value).ToList();
        // The member is the first word that names one (CanMoveAxis:Primary, SideOfPier Model Tests, TargetDeclination Write).
        foreach (var w in words)
        {
            var name = Aliases.GetValueOrDefault(w, w);
            if (Members.TryGetValue(name, out var groups)) return (Canonical(name), WithParked(test, p.Message, groups));
            if (name.StartsWith("Can", StringComparison.Ordinal) && name.Length > 3 && char.IsUpper(name[3]))
                return (name, ["Capabilities"]);
        }
        // A test named after something else (Mount Safety, Post-run checks): look for a member in what it says.
        foreach (var w in Words.Matches(p.Message).Select(m => m.Value))
            if (w.Length > 4 && Members.TryGetValue(w, out var groups) && groups.Length > 0)
                return (Canonical(w), WithParked(test, p.Message, groups));

        var member = string.Concat(words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        return (member.Length > 0 ? member : "Unknown", []);
    }

    /// <summary>A problem found with the mount parked is also the park checks' business.</summary>
    private static string[] WithParked(string test, string message, string[] groups)
        => (test.Contains("Park", StringComparison.OrdinalIgnoreCase) || message.Contains("parked", StringComparison.OrdinalIgnoreCase))
           && !groups.Contains("ParkHome") ? [.. groups, "ParkHome"] : groups;

    private static string Canonical(string name) => Members.Keys.First(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool IsProtocol(string test)
        => test.StartsWith("GET ", StringComparison.OrdinalIgnoreCase) || test.StartsWith("PUT ", StringComparison.OrdinalIgnoreCase)
           || test.Contains("HTTP", StringComparison.OrdinalIgnoreCase) || test.Contains("Alpaca", StringComparison.OrdinalIgnoreCase)
           || test.Contains("Management", StringComparison.OrdinalIgnoreCase) || test.Contains("Discovery", StringComparison.OrdinalIgnoreCase)
           || test.Contains("TransactionID", StringComparison.OrdinalIgnoreCase);

    private static readonly Dictionary<string, PropertyInfo> Properties =
        new[] { typeof(ITelescopeV4) }.Concat(typeof(ITelescopeV4).GetInterfaces())
            .SelectMany(t => t.GetProperties())
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    /// <summary>The readable ITelescopeV4 property of that name, or null.</summary>
    public static PropertyInfo? Property(string member) => Properties.GetValueOrDefault(member);
}
