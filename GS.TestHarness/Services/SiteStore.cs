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
using GS.TestHarness.Engine;

namespace GS.TestHarness.Services;

/// <summary>
/// The two sites a run can be moved to (one north, one south) and which sites the next run uses, saved to
/// sites.json beside the reports folder and shared by every tab.
/// </summary>
public sealed class SiteStore
{
    public sealed record Saved
    {
        public SiteLocation North { get; init; } = SiteLocation.DefaultNorth;
        public SiteLocation South { get; init; } = SiteLocation.DefaultSouth;
        public bool UseServer { get; init; } = true;
        public bool UseNorth { get; init; }
        public bool UseSouth { get; init; }
    }

    private readonly object _gate = new();
    private readonly string _path;
    private readonly ILogger<SiteStore>? _log;
    private Saved _saved;

    /// <summary>
    /// Whether the Northern and Southern Hemisphere sites are offered. Off for now: the Sites card shows only the
    /// server's current settings, and saved North/South ticks are ignored. Set true to bring them back.
    /// </summary>
    public static readonly bool HemisphereSites = false;

    public event Action? Changed;

    public SiteStore(ReportStore reports, ILogger<SiteStore> log) : this(Path.Combine(Path.GetDirectoryName(reports.Folder)!, "sites.json"), log)
    {
    }

    internal SiteStore(string path, ILogger<SiteStore>? log)
    {
        _path = path;
        _log = log;
        _saved = Load();
    }

    public Saved Current
    {
        get { lock (_gate) return _saved; }
    }

    public void Save(Saved saved)
    {
        lock (_gate)
        {
            _saved = saved with
            {
                North = saved.North with { Hemisphere = "North" },
                South = saved.South with { Hemisphere = "South" }
            };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(_saved, ReportStore.Json));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.LogWarning("Couldn't save {Path}: {Error}", _path, ex.Message);
            }
        }
        Changed?.Invoke();
    }

    /// <summary>The sites the next run uses, in order (null: the server's own site).</summary>
    public IReadOnlyList<SiteLocation?> Selected()
    {
        var s = Current;
        var list = new List<SiteLocation?>();
        if (s.UseServer) list.Add(null);
        if (HemisphereSites && s.UseNorth) list.Add(s.North);
        if (HemisphereSites && s.UseSouth) list.Add(s.South);
        return list;
    }

    private Saved Load()
    {
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(_path), ReportStore.Json) is { } saved)
                return saved;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _log?.LogWarning("Couldn't read {Path}: {Error}", _path, ex.Message);
        }
        return new Saved();
    }
}
