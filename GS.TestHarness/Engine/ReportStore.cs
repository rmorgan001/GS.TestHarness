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

    /// <summary>Every saved run, newest first. Files that can't be read are left out.</summary>
    public async Task<List<RunReport>> ListAsync()
    {
        var reports = new List<RunReport>();
        if (!Directory.Exists(Folder)) return reports;
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<RunReport>(await File.ReadAllTextAsync(file), Json) is { } r) reports.Add(r);
            }
            catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException) { }
        }
        return [.. reports.OrderByDescending(r => r.Started)];
    }

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
        sb.AppendLine($"- Run: {r.Started:yyyy-MM-dd HH:mm} ({(r.Finished - r.Started)?.TotalSeconds:0} s){(r.Cancelled ? ", stopped early" : "")}, GS.TestHarness {r.HarnessVersion}");
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
                if (x.Source is not null) sb.AppendLine($"- Where: `{x.Source}`");
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

public sealed record ComparedCheck(string Id, string Group, string Member, CheckResult? Before, CheckResult? After, Change Change);

/// <summary>
/// Compares two runs of the same program check by check (by ID), e.g. two versions of GSServer. Runs of different
/// programs (see <see cref="AreDifferentPrograms"/>) aren't compared.
/// </summary>
public static class ReportComparer
{
    private static int Severity(Outcome o) => o switch
    {
        Outcome.Fail or Outcome.Error => 2,
        Outcome.Warning => 1,
        _ => 0
    };

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
    /// Whether two runs are of different programs (another server, or another device on it) rather than two
    /// versions of the same one.
    /// </summary>
    public static bool AreDifferentPrograms(RunReport a, RunReport b)
        => a.Target.Codebase != b.Target.Codebase
           || !string.Equals(a.Identity.Name, b.Identity.Name, StringComparison.OrdinalIgnoreCase);
}
