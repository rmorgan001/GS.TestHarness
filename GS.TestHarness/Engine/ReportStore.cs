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

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GS.TestHarness.Engine;

/// <summary>Saved runs: one JSON file each, in a folder (Documents\GS.TestHarness\Reports by default).</summary>
public sealed class ReportStore(string folder)
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Folder { get; } = folder;

    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.Create) is { Length: > 0 } docs
            ? docs : AppContext.BaseDirectory, "GS.TestHarness", "Reports");

    private string PathOf(string id)
    {
        if (!Regex.IsMatch(id, @"^[\w-]+$")) throw new ArgumentException("Not a report ID.", nameof(id));
        return Path.Combine(Folder, id + ".json");
    }

    public async Task SaveAsync(RunReport report)
    {
        Directory.CreateDirectory(Folder);
        var path = PathOf(report.Id);
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(report, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public async Task<RunReport?> LoadAsync(string id)
    {
        var path = PathOf(id);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<RunReport>(await File.ReadAllTextAsync(path), Json);
    }

    /// <summary>Each report file read so far, with the time and size it had then: it's read again only when either changes.</summary>
    private readonly ConcurrentDictionary<string, (DateTime Written, long Length, RunReport Report)> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every saved run, newest first. Files that can't be read are left out. A file is only read again when it has
    /// changed, so listing doesn't get slower as the history grows. The reports are shared: don't change them.
    /// </summary>
    public async Task<List<RunReport>> ListAsync()
    {
        var reports = new List<RunReport>();
        if (!Directory.Exists(Folder))
        {
            _cache.Clear();
            return reports;
        }
        var files = new HashSet<string>(Directory.EnumerateFiles(Folder, "*.json"), StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                if (_cache.TryGetValue(file, out var hit) && hit.Written == info.LastWriteTimeUtc && hit.Length == info.Length)
                {
                    reports.Add(hit.Report);
                    continue;
                }
                if (JsonSerializer.Deserialize<RunReport>(await File.ReadAllTextAsync(file), Json) is { } r)
                {
                    _cache[file] = (info.LastWriteTimeUtc, info.Length, r);
                    reports.Add(r);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException) { _cache.TryRemove(file, out _); }
        }
        foreach (var gone in _cache.Keys.Where(k => !files.Contains(k)).ToList()) _cache.TryRemove(gone, out _);
        return [.. reports.OrderByDescending(r => r.Started)];
    }

    /// <summary>
    /// The run that stands for one device at one site: its newest finished run, or its newest stopped one when
    /// none finished (a stopped run didn't make every check, so it would hide what a finished one found).
    /// </summary>
    /// A tier 2 or 3 run only diagnoses a few members, so it stands for the device only when there is no tier 1 run.
    public static RunReport? Representative(IEnumerable<RunReport> runsOfOneDeviceAtOneSite)
        => runsOfOneDeviceAtOneSite.OrderBy(r => r.Options.Tier > 1).ThenBy(r => r.Cancelled || r.CommunicationLost)
            .ThenByDescending(r => r.Started).FirstOrDefault();

    /// <summary>The run that stands for <paramref name="target"/> at the site with this hemisphere (null: the server's own site).</summary>
    public static RunReport? LatestAt(IEnumerable<RunReport> runs, TestTarget target, string? hemisphere)
        => Representative(runs.Where(r => TestTarget.SameDevice(r.Target, target) && r.Site?.Hemisphere == hemisphere));

    /// <summary>The newest saved run of the same device (by target), other than <paramref name="exceptId"/>.</summary>
    public async Task<RunReport?> LatestForAsync(TestTarget target, string? exceptId = null)
        => (await ListAsync()).FirstOrDefault(r => r.Id != exceptId && TestTarget.SameDevice(r.Target, target));

    public void Delete(string id)
    {
        var path = PathOf(id);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>
    /// The report as Markdown: a summary, then every problem with its expected and actual values, fix and source
    /// location, ready to paste into an issue.
    /// </summary>
    public static string ToMarkdown(RunReport r, bool includePasses = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# ASCOM compliance: {r.Label}");
        sb.AppendLine();
        sb.AppendLine($"- Target: {r.Target.Display}");
        sb.AppendLine($"- Site: {r.SiteLabel}{(r.Site is { } site ? $" ({site.Coordinates})" : "")}");
        sb.AppendLine($"- Device: {r.Identity.Name} — {r.Identity.Description}");
        sb.AppendLine($"- DriverInfo: {r.Identity.DriverInfo}");
        sb.AppendLine($"- DriverVersion: {r.Identity.DriverVersion}, InterfaceVersion: {r.Identity.InterfaceVersion}");
        if (r.Identity.ServerDescription is { } sd) sb.AppendLine($"- Alpaca server: {sd}");
        sb.AppendLine($"- Run: {r.Started:yyyy-MM-dd HH:mm} ({(r.Finished - r.Started)?.TotalSeconds:0} s){(r.Cancelled ? ", stopped early" : "")}{(r.CommunicationLost ? ", stopped: communication with the device was lost" : "")}, GS.TestHarness {r.HarnessVersion}");
        sb.AppendLine($"- Options: motion {(r.Options.AllowMotion ? "on" : "off")}, settings writes {(r.Options.AllowSettingsWrites ? "on" : "off")}, " +
                      $"SetPark {(r.Options.AllowSetPark ? "on" : "off")}, protocol {(r.Options.IncludeProtocol ? "on" : "off")}");
        sb.AppendLine();
        sb.AppendLine($"**{r.Count(Outcome.Fail)} failed, {r.Count(Outcome.Error)} errors, {r.Count(Outcome.Warning)} warnings**, " +
                      $"{r.Count(Outcome.Pass)} passed, {r.Count(Outcome.Info)} info, {r.Count(Outcome.Skipped)} skipped.");

        foreach (var outcome in new[] { Outcome.Fail, Outcome.Error, Outcome.Warning })
        {
            var items = r.Results.Where(x => x.Outcome == outcome).ToList();
            if (items.Count == 0) continue;
            sb.AppendLine();
            sb.AppendLine($"## {outcome switch { Outcome.Fail => "Failures", Outcome.Error => "Errors", _ => "Warnings" }} ({items.Count})");
            foreach (var x in items)
            {
                sb.AppendLine();
                sb.AppendLine($"### {x.Member}: {x.Message}");
                sb.AppendLine($"`{x.Id}`");
                sb.AppendLine();
                if (x.Expected is not null) sb.AppendLine($"- Expected: {x.Expected}");
                if (x.Actual is not null) sb.AppendLine($"- Actual: {x.Actual.Split('\n')[0]}");
                if (x.Fix is not null) sb.AppendLine($"- Fix: {x.Fix}");
                if (x.Source is not null) sb.AppendLine($"- Code to check: `{x.Source}`");
                if (x.Reference is not null) sb.AppendLine($"- Reference: {x.Reference}");
            }
        }

        var other = r.Results.Where(x => includePasses ? x.Outcome is Outcome.Pass or Outcome.Info or Outcome.Skipped : x.Outcome is Outcome.Info or Outcome.Skipped).ToList();
        if (other.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(includePasses ? "## Everything else" : "## Notes");
            sb.AppendLine();
            sb.AppendLine("| Outcome | Check | Result |");
            sb.AppendLine("|---|---|---|");
            foreach (var x in other) sb.AppendLine($"| {x.Outcome} | `{x.Id}` | {x.Message.Replace("|", "\\|")} |");
        }
        return sb.ToString();
    }
}

/// <summary>How one check changed between two runs.</summary>
public enum Change
{
    Fixed, Regressed, StillFailing, Changed, New,

    /// <summary>Only the first run made this check.</summary>
    Gone,

    /// <summary>Only the second run made this check, and it didn't find a problem.</summary>
    Added,

    Same
}

/// <summary>How a check came out across the sites, worst first.</summary>
public enum SiteChange
{
    /// <summary>Another outcome (or, for a problem, another message) at some site.</summary>
    Differs,

    /// <summary>The same problem at every site.</summary>
    FailsEverywhere,

    /// <summary>The same result wherever it was made, but some site's run didn't make it.</summary>
    NotEverywhere,

    /// <summary>No problem, at every site.</summary>
    Same
}

public sealed record SiteComparedCheck(string Id, string Group, string Member, CheckResult?[] Results, SiteChange Change);

public sealed record ComparedCheck(string Id, string Group, string Member, CheckResult? Before, CheckResult? After, Change Change);

/// <summary>
/// Compares two runs of the same program check by check (by ID), e.g. two versions of GSServer. Runs of different
/// programs (see <see cref="AreDifferentPrograms"/>) aren't compared.
/// </summary>
public static class ReportComparer
{
    private static int Severity(Outcome o) => o.Severity();

    public static List<ComparedCheck> Compare(RunReport before, RunReport after)
    {
        // An ID can occur more than once in a run (rarely); compare the worst of each.
        static Dictionary<string, CheckResult> Index(RunReport r) => r.Results
            .GroupBy(x => x.Id)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => Severity(x.Outcome)).First());
        var a = Index(before);
        var b = Index(after);
        var list = new List<ComparedCheck>();
        foreach (var id in a.Keys.Union(b.Keys))
        {
            a.TryGetValue(id, out var x);
            b.TryGetValue(id, out var y);
            var any = y ?? x!;
            Change change;
            if (x is null) change = Severity(y!.Outcome) > 0 ? Change.New : Change.Added;
            else if (y is null) change = Change.Gone;
            else if (Severity(x.Outcome) > Severity(y.Outcome)) change = Change.Fixed;
            else if (Severity(x.Outcome) < Severity(y.Outcome)) change = Change.Regressed;
            else if (Severity(y.Outcome) > 0) change = x.Message == y.Message ? Change.StillFailing : Change.Changed;
            else change = Change.Same;
            list.Add(new ComparedCheck(id, any.Group, any.Member, x, y, change));
        }
        return [.. list.OrderBy(c => c.Change).ThenBy(c => c.Id, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Compares runs of one device at several sites (server's own, north, south) check by check. Each row holds
    /// the check's result in each run, in the runs' order (null: that run didn't make it).
    /// </summary>
    public static List<SiteComparedCheck> CompareSites(IReadOnlyList<RunReport> runs)
    {
        static Dictionary<string, CheckResult> Index(RunReport r) => r.Results
            .GroupBy(x => x.Id)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => Severity(x.Outcome)).First());
        var indexes = runs.Select(Index).ToList();
        var list = new List<SiteComparedCheck>();
        foreach (var id in indexes.SelectMany(i => i.Keys).Distinct())
        {
            var results = indexes.Select(i => i.GetValueOrDefault(id)).ToArray();
            var made = results.OfType<CheckResult>().ToList();
            var any = made[0];
            SiteChange change;
            if (made.Select(r => Severity(r.Outcome)).Distinct().Count() > 1
                || (Severity(any.Outcome) > 0 && made.Select(r => r.Message).Distinct().Count() > 1))
                change = SiteChange.Differs;
            else if (made.Count < results.Length) change = SiteChange.NotEverywhere;
            else change = Severity(any.Outcome) > 0 ? SiteChange.FailsEverywhere : SiteChange.Same;
            list.Add(new SiteComparedCheck(id, any.Group, any.Member, results, change));
        }
        return [.. list.OrderBy(c => c.Change).ThenBy(c => c.Id, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Whether two runs are of different programs (another server, or another device on it) rather than two
    /// versions of the same one.
    /// </summary>
    public static bool AreDifferentPrograms(RunReport a, RunReport b)
        => a.Target.Codebase != b.Target.Codebase
           || !string.Equals(a.Identity.Name, b.Identity.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The two runs of one device to compare by default, for a regression check across driver versions: its newest
    /// run (after), and the newest earlier run at the same site with another DriverVersion (before); failing that,
    /// the newest earlier run at the same site, then at any site. Null with fewer than two runs.
    /// </summary>
    public static (RunReport Before, RunReport After)? DefaultVersionPair(IEnumerable<RunReport> runsOfOneDevice)
    {
        var runs = runsOfOneDevice.OrderByDescending(r => r.Started).ToList();
        if (runs.Count < 2) return null;
        var after = runs[0];
        var earlier = runs.Skip(1).ToList();
        var sameSite = earlier.Where(r => r.Site?.Hemisphere == after.Site?.Hemisphere).ToList();
        var before = sameSite.FirstOrDefault(r => !string.Equals(r.Identity.DriverVersion, after.Identity.DriverVersion, StringComparison.OrdinalIgnoreCase))
                     ?? sameSite.FirstOrDefault()
                     ?? earlier[0];
        return (before, after);
    }

    /// <summary>
    /// What makes two runs not like for like (another site, other options, another program), for a warning above
    /// the comparison: such differences show up as checks gone or added rather than as regressions.
    /// </summary>
    public static List<string> Differences(RunReport before, RunReport after)
    {
        var list = new List<string>();
        if (AreDifferentPrograms(before, after)) list.Add("they are runs of different programs");
        if (before.Site?.Hemisphere != after.Site?.Hemisphere) list.Add($"they were made at different sites ({before.SiteLabel}, {after.SiteLabel})");
        void Option(string name, bool a, bool b) { if (a != b) list.Add($"{name} was {(a ? "on" : "off")} before and {(b ? "on" : "off")} after"); }
        Option("motion", before.Options.AllowMotion, after.Options.AllowMotion);
        Option("settings writes", before.Options.AllowSettingsWrites, after.Options.AllowSettingsWrites);
        Option("SetPark", before.Options.AllowSetPark, after.Options.AllowSetPark);
        Option("the protocol checks", before.Options.IncludeProtocol, after.Options.IncludeProtocol);
        Option("the extended checks", before.Options.Extended, after.Options.Extended);
        Option("the disconnected checks", before.Options.IncludeDisconnected, after.Options.IncludeDisconnected);
        if (before.Cancelled || before.CommunicationLost) list.Add("the earlier run stopped part way");
        if (after.Cancelled || after.CommunicationLost) list.Add("the later run stopped part way");
        return list;
    }
}

/// <summary>Reports and comparisons as CSV (RFC 4180, UTF-8 with a byte order mark so Excel reads it as UTF-8).</summary>
public static class ReportCsv
{
    public static byte[] Report(RunReport r)
    {
        var sb = new StringBuilder();
        Row(sb, "Id", "Group", "Member", "Outcome", "Message", "Expected", "Actual", "Fix", "Source", "Reference", "DurationMs");
        foreach (var x in r.Results)
            Row(sb, x.Id, x.Group, x.Member, x.Outcome.ToString(), x.Message, x.Expected, x.Actual, x.Fix, x.Source, x.Reference,
                x.DurationMs.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
        return Bytes(sb);
    }

    public static byte[] Comparison(RunReport before, RunReport after)
    {
        var sb = new StringBuilder();
        Row(sb, "Change", "Id", "Group", "Member",
            $"Before outcome ({before.Identity.DriverVersion} {before.Started:yyyy-MM-dd HH:mm})", "Before message",
            $"After outcome ({after.Identity.DriverVersion} {after.Started:yyyy-MM-dd HH:mm})", "After message", "Fix");
        foreach (var c in ReportComparer.Compare(before, after))
            Row(sb, c.Change.ToString(), c.Id, c.Group, c.Member, c.Before?.Outcome.ToString(), c.Before?.Message,
                c.After?.Outcome.ToString(), c.After?.Message, (c.After ?? c.Before)?.Fix);
        return Bytes(sb);
    }

    /// <summary>A file name made safe from the run's label.</summary>
    public static string FileName(string label) => Regex.Replace(label, @"[^\w.-]+", "_").Trim('_');

    private static void Row(StringBuilder sb, params string?[] fields)
    {
        sb.AppendJoin(',', fields.Select(Field));
        sb.Append("\r\n");
    }

    private static string Field(string? value)
    {
        value ??= "";
        // A spreadsheet runs a cell that starts like a formula: device messages are untrusted, so make it text.
        if (value.Length > 0 && (value[0] is '=' or '+' or '@' or '\t' or '\r'
                                 || (value[0] == '-' && (value.Length == 1 || !(char.IsDigit(value[1]) || value[1] is '.' or ' ')))))
            value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    private static byte[] Bytes(StringBuilder sb) => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
}
