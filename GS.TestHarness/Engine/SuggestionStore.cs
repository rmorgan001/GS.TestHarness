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

using System.Text.Json;
using System.Text.Json.Serialization;

namespace GS.TestHarness.Engine;

/// <summary>Where a suggested test stands: still to do, added (Done) or not wanted (Dismissed).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SuggestionStatus { Open, Done, Dismissed }

/// <summary>
/// A suggested test kept between runs. <see cref="Chains"/> are the first reports (tier 1) of the chains of tiers that
/// suggested it, so "suggested in 3 runs" counts runs, not tiers.
/// </summary>
public sealed class StoredSuggestion
{
    public required SuggestedTest Suggestion { get; set; }
    public SuggestionStatus Status { get; set; } = SuggestionStatus.Open;
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public List<string> Chains { get; set; } = [];
}

/// <summary>
/// The suggested tests of every run, in suggested-tests.json beside the reports folder: so they can be looked at again after
/// the finished dialog is closed, show how often they have been suggested, and be marked Done or Dismissed (a dismissed one
/// isn't shown again; a Done one that is suggested again is open again, since the test didn't cover it).
/// </summary>
public sealed class SuggestionStore(ReportStore reports)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string PathOf => Path.Combine(reports.SettingsFolder, "suggested-tests.json");

    private async Task<List<StoredSuggestion>> LoadAsync()
    {
        try
        {
            if (!File.Exists(PathOf)) return [];
            return JsonSerializer.Deserialize<List<StoredSuggestion>>(await File.ReadAllTextAsync(PathOf), ReportStore.Json) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A damaged file is kept aside, not overwritten, so nothing is lost for good.
            try { File.Move(PathOf, PathOf + ".bad", overwrite: true); } catch (IOException) { /* leave it */ }
            return [];
        }
    }

    private async Task SaveAsync(List<StoredSuggestion> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathOf)!);
        await File.WriteAllTextAsync(PathOf + ".tmp", JsonSerializer.Serialize(all, ReportStore.Json));
        File.Move(PathOf + ".tmp", PathOf, overwrite: true);
    }

    /// <summary>Every suggestion kept, heaviest first.</summary>
    public async Task<List<StoredSuggestion>> ListAsync()
    {
        await _gate.WaitAsync();
        try { return [.. (await LoadAsync()).OrderByDescending(s => s.Suggestion.Weight).ThenByDescending(s => s.LastSeen)]; }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Keeps what a chain of tiers (whose first report is <paramref name="chain"/>) suggested. Returns the suggestions to show
    /// for it, each with how many earlier runs suggested it, without the dismissed ones (<paramref name="hidden"/> counts those).
    /// </summary>
    public async Task<(List<SuggestedTest> Shown, int Hidden)> RecordAsync(IReadOnlyList<SuggestedTest> found, string chain)
    {
        await _gate.WaitAsync();
        try
        {
            var all = await LoadAsync();
            var shown = new List<SuggestedTest>();
            var hidden = 0;
            var now = DateTimeOffset.Now;
            foreach (var s in found)
            {
                var stored = all.FirstOrDefault(x => x.Suggestion.Key == s.Key);
                if (stored is null)
                {
                    all.Add(new StoredSuggestion { Suggestion = s, FirstSeen = now, LastSeen = now, Chains = [chain] });
                    shown.Add(s);
                    continue;
                }
                var before = stored.Chains.Count(c => c != chain);
                if (!stored.Chains.Contains(chain)) stored.Chains.Add(chain);
                // The latest wording and members, with every member any run found.
                var members = stored.Suggestion.Members.Union(s.Members).Order().ToList();
                stored.Suggestion = s with { Members = members, SeenBefore = before };
                stored.LastSeen = now;
                if (stored.Status == SuggestionStatus.Done) stored.Status = SuggestionStatus.Open;
                if (stored.Status == SuggestionStatus.Dismissed) hidden++;
                else shown.Add(stored.Suggestion);
            }
            await SaveAsync(all);
            return (shown, hidden);
        }
        finally { _gate.Release(); }
    }

    public async Task SetStatusAsync(string key, SuggestionStatus status)
    {
        await _gate.WaitAsync();
        try
        {
            var all = await LoadAsync();
            if (all.FirstOrDefault(x => x.Suggestion.Key == key) is not { } stored) return;
            stored.Status = status;
            await SaveAsync(all);
        }
        finally { _gate.Release(); }
    }
}
