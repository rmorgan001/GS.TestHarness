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

namespace GS.TestHarness.Engine;

/// <summary>
/// The test weights the user has changed, and the limit a tier's failed weights may add up to, in test-weights.json beside the
/// reports folder. Only what differs from the built-in weights (<see cref="TestWeights.Default"/>) is kept, so a weight the
/// user hasn't touched follows the built-in table if a later version changes it. Every change is applied to
/// <see cref="TestWeights"/> and saved at once.
/// </summary>
public sealed class WeightStore
{
    private sealed class Saved
    {
        public int? Limit { get; set; }
        public Dictionary<string, int> Weights { get; set; } = [];
    }

    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, int> _weights = [];
    private int _limit = TestWeights.DefaultLimit;

    /// <summary>Raised when a weight or the limit changes.</summary>
    public event Action? Changed;

    public WeightStore(ReportStore reports)
    {
        _path = Path.Combine(reports.SettingsFolder, "test-weights.json");
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(_path)) is { } saved)
            {
                foreach (var (key, weight) in saved.Weights)
                    if (TestCatalog.Entry(key) is not null && weight is >= TestWeights.Min and <= TestWeights.Max && weight != TestWeights.Default(key))
                        _weights[key] = weight; // a test since removed from the catalog, or a weight out of range, is dropped
                if (saved.Limit is { } limit) _limit = Math.Clamp(limit, 1, 10_000);
            }
        }
        catch (JsonException)
        {
            // A damaged file is kept aside, not overwritten. (One that is only locked or unreadable is left where it is.)
            try { File.Move(_path, _path + ".bad", overwrite: true); } catch (IOException) { /* leave it */ } catch (UnauthorizedAccessException) { /* leave it */ }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Built-in weights for now.
        }
        TestWeights.Apply(_weights, _limit);
    }

    public int Limit
    {
        get { lock (_gate) return _limit; }
    }

    /// <summary>Whether anything is different from the built-in weights and limit.</summary>
    public bool HasChanges
    {
        get { lock (_gate) return _weights.Count > 0 || _limit != TestWeights.DefaultLimit; }
    }

    /// <summary>Sets a test's weight (1 to 10); the built-in weight clears the change.</summary>
    public void SetWeight(string key, int weight)
    {
        if (TestCatalog.Entry(key) is null) throw new ArgumentException("Not a test.", nameof(key));
        weight = Math.Clamp(weight, TestWeights.Min, TestWeights.Max);
        lock (_gate)
        {
            if (weight == TestWeights.Default(key)) _weights.Remove(key);
            else _weights[key] = weight;
            Commit();
        }
        Changed?.Invoke();
    }

    /// <summary>Sets the limit a tier's failed weights may add up to.</summary>
    public void SetLimit(int limit)
    {
        lock (_gate)
        {
            _limit = Math.Clamp(limit, 1, 10_000);
            Commit();
        }
        Changed?.Invoke();
    }

    /// <summary>Back to the built-in weight for one test.</summary>
    public void Reset(string key)
    {
        lock (_gate)
        {
            if (!_weights.Remove(key)) return;
            Commit();
        }
        Changed?.Invoke();
    }

    /// <summary>Back to the built-in weights and limit.</summary>
    public void ResetAll()
    {
        lock (_gate)
        {
            _weights.Clear();
            _limit = TestWeights.DefaultLimit;
            Commit();
        }
        Changed?.Invoke();
    }

    /// <summary>The format name written into an export, so an import can tell a weights file from another JSON file.</summary>
    public const string Format = "gs-testharness-weights";

    /// <summary>
    /// Every test's weight as it is now (not just the changed ones) and the limit, as JSON that <see cref="Import"/> reads back: for
    /// a backup, or to give the same weights to another person or machine. Each test is keyed by its catalog key (Group.Id).
    /// </summary>
    public string Export()
    {
        lock (_gate)
        {
            var weights = new Dictionary<string, int>();
            foreach (var e in TestCatalog.Entries.OrderBy(e => e.Number))
                weights[e.Key] = TestWeights.Of(e.Key);
            return JsonSerializer.Serialize(new { format = Format, version = 1, limit = _limit, weights }, ReportStore.Json);
        }
    }

    /// <summary>What an <see cref="Import"/> did: how many weights it set, what it left out and why, and the limit it set (if the file had one).</summary>
    public sealed record ImportResult(int Applied, IReadOnlyList<string> Skipped, int? Limit, string? Error)
    {
        public bool Ok => Error is null;
    }

    /// <summary>
    /// Replaces the weights and limit with those in <paramref name="json"/> (an <see cref="Export"/>, or a file with the same
    /// "limit" and "weights"): a test the file doesn't list goes back to its built-in weight. A test that isn't in the catalog,
    /// or a weight that isn't a whole number from 1 to 10, is left out and listed in <see cref="ImportResult.Skipped"/>. Nothing
    /// changes if the file can't be read at all.
    /// </summary>
    public ImportResult Import(string json)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return new ImportResult(0, [], null, $"That isn't JSON: {ex.Message}");
        }
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("weights", out var list) || list.ValueKind != JsonValueKind.Object)
            return new ImportResult(0, [], null, "That isn't a weights file: it has no \"weights\" object.");
        if (root.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String && format.GetString() != Format)
            return new ImportResult(0, [], null, $"That is a \"{format.GetString()}\" file, not a weights file.");

        int? limit = null;
        var skipped = new List<string>();
        if (root.TryGetProperty("limit", out var l))
        {
            if (l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out var n) && n is >= 1 and <= 10_000) limit = n;
            else skipped.Add("limit: not a whole number from 1 to 10000");
        }

        var imported = new Dictionary<string, int>();
        foreach (var item in list.EnumerateObject())
        {
            if (TestCatalog.Entry(item.Name) is null) skipped.Add($"{item.Name}: not a test");
            else if (item.Value.ValueKind != JsonValueKind.Number || !item.Value.TryGetInt32(out var w) || w is < TestWeights.Min or > TestWeights.Max)
                skipped.Add($"{item.Name}: not a whole number from {TestWeights.Min} to {TestWeights.Max}");
            else if (w != TestWeights.Default(item.Name)) imported[item.Name] = w;
            else imported.Remove(item.Name);
        }

        lock (_gate)
        {
            _weights.Clear();
            foreach (var (key, weight) in imported) _weights[key] = weight;
            _limit = limit ?? TestWeights.DefaultLimit;
            Commit();
        }
        Changed?.Invoke();
        return new ImportResult(list.EnumerateObject().Count() - skipped.Count(s => !s.StartsWith("limit:")), skipped, limit, null);
    }

    /// <summary>Applies and saves (called with the lock held).</summary>
    private void Commit()
    {
        TestWeights.Apply(_weights, _limit);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var saved = new Saved { Limit = _limit == TestWeights.DefaultLimit ? null : _limit, Weights = new(_weights.OrderBy(w => w.Key, StringComparer.Ordinal)) };
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(saved, ReportStore.Json));
        File.Move(_path + ".tmp", _path, overwrite: true);
    }
}
