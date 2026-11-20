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

using System.Text.Json.Serialization;

namespace GS.TestHarness.Engine;

/// <summary>How the harness reaches the mount.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TargetKind>))]
public enum TargetKind
{
    /// <summary>An Alpaca server, over HTTP.</summary>
    Alpaca,

    /// <summary>An ASCOM driver on this computer, through COM (Windows only).</summary>
    Com
}

/// <summary>
/// The server under test, which is also the source code findings point at. Each is reached one way only:
/// GSServer through its COM driver, GreenSwampAlpaca over Alpaca.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<Codebase>))]
public enum Codebase
{
    /// <summary>GSServer: the ASCOM.GS.Sky.Telescope COM driver.</summary>
    GSServer,

    /// <summary>GreenSwampAlpaca: the Alpaca server.</summary>
    GreenSwampAlpaca
}

/// <summary>The mount to test: one of the servers, reached the one way that server is reached.</summary>
public sealed record TestTarget
{
    /// <summary>Which server: it decides the transport (<see cref="Kind"/>) and where findings point in the source.</summary>
    public Codebase Codebase { get; init; } = Codebase.GreenSwampAlpaca;

    /// <summary>COM for GSServer, Alpaca for GreenSwampAlpaca.</summary>
    [JsonIgnore]
    public TargetKind Kind => KindOf(Codebase);

    public static TargetKind KindOf(Codebase server) => server == Codebase.GSServer ? TargetKind.Com : TargetKind.Alpaca;

    /// <summary>Whether two targets are the same device: same server, and same address and device number (or ProgID).</summary>
    public static bool SameDevice(TestTarget a, TestTarget b) => a.DeviceKey == b.DeviceKey;

    /// <summary>The same for two targets exactly when they're the same device (see <see cref="SameDevice"/>).</summary>
    [JsonIgnore]
    public string DeviceKey => Kind == TargetKind.Com
        ? $"{Codebase}|{ProgId.Trim().ToUpperInvariant()}"
        : $"{Codebase}|{Host.Trim().ToUpperInvariant()}|{Port}|{DeviceNumber}";

    /// <summary>Alpaca: the server's host name or IP address.</summary>
    public string Host { get; init; } = "localhost";

    /// <summary>Alpaca: the server's port.</summary>
    public int Port { get; init; } = 31416;

    /// <summary>Alpaca: the telescope's device number.</summary>
    public int DeviceNumber { get; init; }

    /// <summary>COM: the driver's ProgID.</summary>
    public string ProgId { get; init; } = "ASCOM.GS.Sky.Telescope";

    /// <summary>Alpaca: the user name for a server that asks for one (HTTP basic authentication). Never saved in reports.</summary>
    [JsonIgnore]
    public string? UserName { get; init; }

    /// <summary>Alpaca: the password that goes with <see cref="UserName"/>. Never saved in reports.</summary>
    [JsonIgnore]
    public string? Password { get; init; }

    /// <summary>The Alpaca server's base address (no trailing slash).</summary>
    [JsonIgnore]
    public string BaseUrl => $"http://{(Host.Contains(':') && !Host.StartsWith('[') ? $"[{Host}]" : Host)}:{Port}";

    /// <summary>How the target is shown: what kind of server and its ProgID or address (it needn't be GSServer or GreenSwampAlpaca).</summary>
    [JsonIgnore]
    public string Display => Kind == TargetKind.Com
        ? $"COM driver {ProgId}"
        : $"Alpaca server {BaseUrl} telescope {DeviceNumber}";
}

/// <summary>What a run may do to the mount, and how long it waits.</summary>
public sealed record RunOptions
{
    /// <summary>
    /// Slew, sync, move the axes, pulse guide, park, unpark and find home. Off by default: only use it with a
    /// simulator, or with a real mount that is clear to move.
    /// </summary>
    public bool AllowMotion { get; init; }

    /// <summary>
    /// Write settings back (site, guide rates, tracking rate, settle time, refraction), each with the value it
    /// already has, and try invalid values (which must be refused). Originals are put back afterwards.
    /// </summary>
    public bool AllowSettingsWrites { get; init; } = true;

    /// <summary>Call SetPark, which moves the saved park position to where the mount points now. Off by default.</summary>
    public bool AllowSetPark { get; init; }

    /// <summary>Alpaca only: check the HTTP protocol itself (management API, transaction IDs, error replies, discovery).</summary>
    public bool IncludeProtocol { get; init; } = true;

    /// <summary>Disconnect at the end and check that the mount then refuses to answer.</summary>
    public bool IncludeDisconnected { get; init; } = true;

    /// <summary>
    /// The longer checks: tracking drift (tracking on, off, rate offsets), several clients at once, parallel reads
    /// and connect cycles. They take a few minutes; off by default.
    /// </summary>
    public bool Extended { get; init; }

    /// <summary>How long each tracking-drift measurement lasts, in seconds.</summary>
    public int DriftSeconds { get; init; } = 20;

    /// <summary>The longest a slew, park or find-home may take.</summary>
    public int SlewTimeoutSeconds { get; init; } = 120;

    /// <summary>The longest a single call may take.</summary>
    public int CallTimeoutSeconds { get; init; } = 15;

    /// <summary>
    /// Degrees either side of the meridian (0 to 90) where a German mount can point from either side of the pier.
    /// The flip checks flip the mount with SideOfPier inside it, and expect a goto past it to flip the mount. 0 when
    /// it isn't known: there's no zone, so the SideOfPier flip isn't checked.
    /// </summary>
    public int FlipAngle { get; init; }

    /// <summary>
    /// Only these check groups (by name); null runs them all. Set by a run started from a ConformU log, to the groups
    /// that check what the log found wrong.
    /// </summary>
    public IReadOnlyList<string>? Groups { get; init; }

    /// <summary>
    /// Only these members (ConformUMap.Protocol for the Alpaca protocol); null for all. Set by a run started from a
    /// ConformU log, to the members it found problems with: the groups skip the sections that don't touch them, and
    /// only findings about them are kept (with the ConformU verdicts, and anything that explains a skipped check).
    /// </summary>
    public IReadOnlyList<string>? Members { get; init; }

    /// <summary>
    /// What the server drives: GSServer's simulator or a real mount. GSServer takes different code paths for each
    /// (GS.Simulator or GS.SkyWatcher), so the diagnostics and their recommendations depend on it.
    /// </summary>
    public MountKind Mount { get; init; } = MountKind.Unknown;

    /// <summary>
    /// How deep the run goes (<see cref="Tiers"/>): 1 finds problems (the full check, or a ConformU log's follow-up);
    /// 2 measures a problem found at tier 1 to tell its causes apart; 3 measures further to pin the cause down. A tier 2
    /// or 3 run only diagnoses <see cref="Members"/>, each one that has a diagnosis at that tier.
    /// </summary>
    public int Tier { get; init; } = 1;

    /// <summary>The report this run follows up: the tier below's run that found the problems. Null at tier 1.</summary>
    public string? FollowsUp { get; init; }
}

/// <summary>What the server under test drives (the Mount option).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MountKind>))]
public enum MountKind
{
    /// <summary>Not said: recommendations cover both code paths.</summary>
    Unknown,

    /// <summary>GSServer's built-in simulator (GS.Simulator).</summary>
    Simulator,

    /// <summary>A real mount (GSServer's SkyWatcher code, GS.SkyWatcher).</summary>
    RealMount
}

/// <summary>A check's result.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Outcome>))]
public enum Outcome
{
    /// <summary>As ASCOM requires.</summary>
    Pass,

    /// <summary>Allowed, but worth knowing (for example, an optional member that isn't implemented).</summary>
    Info,

    /// <summary>Not a breach of the standard, but likely to trouble clients, or a recommendation not followed.</summary>
    Warning,

    /// <summary>Breaks the ASCOM standard.</summary>
    Fail,

    /// <summary>The check itself couldn't finish (the mount didn't answer, or an earlier step failed).</summary>
    Error,

    /// <summary>Not run (not applicable, or not allowed by the run's options).</summary>
    Skipped
}

public static class OutcomeExtensions
{
    /// <summary>
    /// How bad an outcome is: 2 for Fail or Error, 1 for Warning, 0 for the rest. Fail and Error rank the same, so
    /// a check going from one to the other is neither fixed nor broken.
    /// </summary>
    public static int Severity(this Outcome o) => o switch
    {
        Outcome.Fail or Outcome.Error => 2,
        Outcome.Warning => 1,
        _ => 0
    };
}

/// <summary>One finding: what was checked, what happened and, for a problem, how to correct it.</summary>
public sealed record CheckResult
{
    /// <summary>Stable across versions, so runs can be compared: "Group.Member.Rule".</summary>
    public required string Id { get; init; }

    public required string Group { get; init; }

    /// <summary>The interface member, Alpaca endpoint or topic.</summary>
    public required string Member { get; init; }

    public required Outcome Outcome { get; init; }

    /// <summary>What happened, in a sentence.</summary>
    public required string Message { get; init; }

    public string? Expected { get; init; }

    public string? Actual { get; init; }

    /// <summary>For a problem: why it matters and what to change.</summary>
    public string? Fix { get; init; }

    /// <summary>For a problem: where the code to change probably is ("file:line").</summary>
    public string? Source { get; init; }

    /// <summary>The ASCOM reference for the rule.</summary>
    public string? Reference { get; init; }

    public double DurationMs { get; init; }

    [JsonIgnore]
    public bool IsProblem => Outcome is Outcome.Fail or Outcome.Error or Outcome.Warning;

    /// <summary>The number of the test that made this finding (<see cref="TestNumbers"/>): its first digit is the tier.</summary>
    [JsonIgnore]
    public int Number => TestNumbers.Of(Id);
}

/// <summary>What the device says it is, read at the start of a run.</summary>
public sealed record DeviceIdentity
{
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? DriverInfo { get; init; }
    public string? DriverVersion { get; init; }
    public short? InterfaceVersion { get; init; }

    /// <summary>Alpaca: the server's management description (ServerName, Manufacturer, ManufacturerVersion).</summary>
    public string? ServerDescription { get; init; }

    /// <summary>Alpaca: the device's UniqueID from the management API (it must never change).</summary>
    public string? UniqueId { get; init; }
}

/// <summary>A whole run: the target, what the device said it was, the options and every finding.</summary>
public sealed record RunReport
{
    public string Id { get; init; } = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];

    /// <summary>
    /// The report's number, given by <see cref="ReportStore"/> when it is saved: 1, 2, 3... in the order runs were saved,
    /// never reused (0 until saved). Shown as <see cref="Reference"/>.
    /// </summary>
    public int Number { get; set; }

    /// <summary>The report's reference, "R0012": what to quote for it (the <see cref="Id"/> until it has a number).</summary>
    [JsonIgnore]
    public string Reference => Number > 0 ? ReferenceOf(Number) : Id;

    public static string ReferenceOf(int number) => $"R{number:0000}";

    /// <summary>For a tier 2 or 3 run, the number of the run it follows up (<see cref="RunOptions.FollowsUp"/>), for its reference.</summary>
    public int? FollowsUpNumber { get; set; }

    /// <summary>The reference of the run this one follows up ("R0011"), or null for a tier 1 run.</summary>
    [JsonIgnore]
    public string? FollowsUpReference => Options.FollowsUp is { } up ? FollowsUpNumber is > 0 and var n ? ReferenceOf(n) : up : null;
    public required TestTarget Target { get; init; }
    public required RunOptions Options { get; init; }
    public DeviceIdentity Identity { get; set; } = new();
    public DateTimeOffset Started { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? Finished { get; set; }
    public bool Cancelled { get; set; }

    /// <summary>The device stopped answering part way through, so the run stopped there.</summary>
    public bool CommunicationLost { get; set; }
    public string HarnessVersion { get; init; } = typeof(RunReport).Assembly.GetName().Version?.ToString(3) ?? "";

    /// <summary>The ConformU log the run followed up (its file name); null for an ordinary run.</summary>
    public string? ConformULog { get; init; }

    public List<CheckResult> Results { get; init; } = [];

    /// <summary>A short label for the run: device name and version.</summary>
    [JsonIgnore]
    public string Label => $"{Identity.Name ?? Target.Display} {Identity.DriverVersion}".Trim()
                           + (Options.Tier > 1 ? $" (tier {Options.Tier})" : "");

    public int Count(Outcome outcome) => Results.Count(r => r.Outcome == outcome);
}
