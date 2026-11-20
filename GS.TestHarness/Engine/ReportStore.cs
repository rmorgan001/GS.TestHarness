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

    /// <summary>The reports folder, as a full path (a relative ReportsFolder is taken from the current folder).</summary>
    public string Folder { get; } = Path.GetFullPath(folder);

    /// <summary>
    /// Where the app's settings files (servers.json, last-run.json...) sit: beside the reports folder, or in it when it is a
    /// drive's root. A folder written with a trailing separator ("D:\Data\Reports\") keeps its settings in it, as it
    /// always has, so they are still found after an upgrade.
    /// </summary>
    public string SettingsFolder => Path.GetDirectoryName(Folder) is { Length: > 0 } parent ? parent : Folder;

    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.Create) is { Length: > 0 } docs
            ? docs : AppContext.BaseDirectory, "GS.TestHarness", "Reports");

    private string PathOf(string id)
    {
        if (!Regex.IsMatch(id, @"^[\w-]+$")) throw new ArgumentException("Not a report ID.", nameof(id));
        return Path.Combine(Folder, id + ".json");
    }

    /// <summary>
    /// Saves a run. A run saved for the first time gets the next report number (<see cref="RunReport.Number"/>): the
    /// reports already here are numbered first, oldest first, so the numbers follow the order of the runs.
    /// </summary>
    public async Task SaveAsync(RunReport report)
    {
        Directory.CreateDirectory(Folder);
        if (report.Number == 0)
        {
            var known = await ListAsync();
            report.Number = (await IssueAsync(1, known.Select(r => r.Number).DefaultIfEmpty(0).Max()))[0];
        }
        await WriteAsync(report);
    }

    private async Task WriteAsync(RunReport report)
    {
        var path = PathOf(report.Id);
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(report, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>The file that holds the last report number given, so a deleted report's number isn't given again.</summary>
    private string CounterPath => Path.Combine(Folder, "report-numbers.txt");

    private static readonly SemaphoreSlim Numbering = new(1, 1);

    /// <summary>
    /// The next <paramref name="count"/> report numbers, after the last one given (from the counter file) and after
    /// <paramref name="highest"/> (the highest a report here has, should the counter file be gone). The counter file
    /// is held exclusively while it's updated, so the app and the command line never give out the same number.
    /// </summary>
    private async Task<int[]> IssueAsync(int count, int highest)
    {
        await Numbering.WaitAsync();
        try
        {
            Directory.CreateDirectory(Folder);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await using var file = new FileStream(CounterPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    using var reader = new StreamReader(file, leaveOpen: true);
                    var last = int.TryParse((await reader.ReadToEndAsync()).Trim(), out var n) ? n : 0;
                    last = Math.Max(last, highest);
                    var numbers = Enumerable.Range(last + 1, count).ToArray();
                    file.SetLength(0);
                    await using var writer = new StreamWriter(file);
                    await writer.WriteAsync(numbers[^1].ToString(System.Globalization.CultureInfo.InvariantCulture));
                    return numbers;
                }
                catch (IOException) when (attempt < 50)
                {
                    await Task.Delay(100);
                }
            }
        }
        finally
        {
            Numbering.Release();
        }
    }

    /// <summary>
    /// A saved run by its ID, or by its reference: "RAC0012" (an ASCOM Conformance Check run), "RCU0012" (a ConformU
    /// Debug Log Test run), or, as before the prefixes, "R0012" or just "12" for either. A prefix that names the other
    /// test finds nothing, so a reference can't open a run of the wrong kind.
    /// </summary>
    public async Task<RunReport?> LoadAsync(string id)
    {
        if (Regex.Match(id.Trim(), @"^(RAC|RCU|R)?0*(\d{1,9})$", RegexOptions.IgnoreCase) is { Success: true } reference)
        {
            var prefix = reference.Groups[1].Value.ToUpperInvariant();
            var number = int.Parse(reference.Groups[2].Value);
            return (await ListAsync()).FirstOrDefault(r => r.Number == number
                && (prefix is "" or "R" || (prefix == RunReport.ConformUPrefix) == (r.ConformULog is not null)));
        }
        var path = PathOf(id);
        if (!File.Exists(path)) return null;
        var report = JsonSerializer.Deserialize<RunReport>(await File.ReadAllTextAsync(path), Json);
        // A report saved before reports had numbers gets its number when the reports are listed.
        if (report is { Number: 0 }) return (await ListAsync()).FirstOrDefault(r => r.Id == report.Id) ?? report;
        return report;
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

        // Reports saved before reports had numbers: numbered once, oldest first, and saved with it; a deeper run also
        // gets the number of the run it follows up, for its reference.
        var changed = new HashSet<RunReport>(ReferenceEqualityComparer.Instance);
        var unnumbered = reports.Where(r => r.Number == 0).OrderBy(r => r.Started).ToList();
        if (unnumbered.Count > 0)
        {
            var numbers = await IssueAsync(unnumbered.Count, reports.Select(r => r.Number).DefaultIfEmpty(0).Max());
            for (var i = 0; i < unnumbered.Count; i++)
            {
                unnumbered[i].Number = numbers[i];
                changed.Add(unnumbered[i]);
            }
        }
        var byId = reports.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var r in reports.Where(r => r.FollowsUpNumber is null && r.Options.FollowsUp is not null))
            if (byId.TryGetValue(r.Options.FollowsUp!, out var parent) && parent.Number > 0)
            {
                r.FollowsUpNumber = parent.Number;
                changed.Add(r);
            }
        foreach (var r in changed)
        {
            try { await WriteAsync(r); }
            catch (IOException) { /* numbered in memory; saved next time */ }
        }
        return [.. reports.OrderByDescending(r => r.Started)];
    }

    /// <summary>
    /// The run that stands for one device: its newest finished run, or its newest stopped one when
    /// none finished (a stopped run didn't make every check, so it would hide what a finished one found).
    /// </summary>
    /// A tier 2 or 3 run only diagnoses a few members, so it stands for the device only when there is no tier 1 run.
    public static RunReport? Representative(IEnumerable<RunReport> runsOfOneDevice)
        => runsOfOneDevice.OrderBy(r => r.Options.Tier > 1).ThenBy(r => r.Cancelled || r.CommunicationLost)
            .ThenByDescending(r => r.Started).FirstOrDefault();

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
        sb.AppendLine($"- Report: {r.Reference} ({r.Id})");
        sb.AppendLine($"- Target: {r.Target.Display}");
        sb.AppendLine($"- Device: {r.Identity.Name} — {r.Identity.Description}");
        sb.AppendLine($"- DriverInfo: {r.Identity.DriverInfo}");
        sb.AppendLine($"- DriverVersion: {r.Identity.DriverVersion}, InterfaceVersion: {r.Identity.InterfaceVersion}");
        if (r.Identity.ServerDescription is { } sd) sb.AppendLine($"- Alpaca server: {sd}");
        sb.AppendLine($"- Run: {r.Started:yyyy-MM-dd HH:mm} ({(r.Finished - r.Started)?.TotalSeconds:0} s){(r.Cancelled ? ", stopped early" : "")}{(r.CommunicationLost ? ", stopped: communication with the device was lost" : "")}, GS.TestHarness {r.HarnessVersion}");
        sb.AppendLine($"- Options: motion {(r.Options.AllowMotion ? "on" : "off")}, settings writes {(r.Options.AllowSettingsWrites ? "on" : "off")}, " +
                      $"SetPark {(r.Options.AllowSetPark ? "on" : "off")}, protocol {(r.Options.IncludeProtocol ? "on" : "off")}");
        sb.AppendLine($"- {Tiers.Name(r.Options.Tier)}{(r.Options.FollowsUp is { } up ? $", following up report {r.FollowsUpReference}" : "")}");
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
                sb.AppendLine($"Test {x.Number}: `{x.Id}`");
                sb.AppendLine();
                // Expected and Fix on one line, so each stays one list item; Actual whole, as a code block when it runs to several lines.
                if (x.Expected is not null) sb.AppendLine($"- Expected: {RecommendationBuilder.Inline(x.Expected)}");
                if (x.Actual is not null) RecommendationBuilder.AppendActual(sb, x.Actual, indent: "");
                if (x.Fix is not null) sb.AppendLine($"- Fix: {RecommendationBuilder.Inline(x.Fix)}");
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
            sb.AppendLine("| Outcome | No. | Check | Result |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var x in other) sb.AppendLine($"| {x.Outcome} | {x.Number} | `{x.Id}` | {x.Message.Replace("|", "\\|")} |");
        }
        return sb.ToString();
    }
}

/// <summary>Reports as CSV (RFC 4180, UTF-8 with a byte order mark so Excel reads it as UTF-8).</summary>
public static class ReportCsv
{
    public static byte[] Report(RunReport r)
    {
        var sb = new StringBuilder();
        Row(sb, "No", "Id", "Group", "Member", "Outcome", "Message", "Expected", "Actual", "Fix", "Source", "Reference", "DurationMs");
        foreach (var x in r.Results)
            Row(sb, No(x.Id), x.Id, x.Group, x.Member, x.Outcome.ToString(), x.Message, x.Expected, x.Actual, x.Fix, x.Source, x.Reference,
                x.DurationMs.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
        return Bytes(sb);
    }

    /// <summary>The test's number (TestNumbers), or blank for a finding with none.</summary>
    private static string No(string id) => TestNumbers.Of(id) is > 0 and var n ? n.ToString() : "";

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
        // A leading '-' is only safe on a plain, finite number: "-2+3+cmd|..." starts with a digit too, and Excel reads
        // "-Infinity" as a formula.
        if (value.Length > 0 && (value[0] is '=' or '+' or '@' or '\t' or '\r'
                                 || (value[0] == '-' && !(double.TryParse(value, System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)))))
            value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    private static byte[] Bytes(StringBuilder sb) => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
}
