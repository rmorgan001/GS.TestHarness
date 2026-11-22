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
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A damaged file is kept aside, not overwritten.
            try { File.Move(_path, _path + ".bad", overwrite: true); } catch (IOException) { /* leave it */ }
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
