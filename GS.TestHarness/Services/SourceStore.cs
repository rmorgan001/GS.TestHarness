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
/// Where each server's source code is on this computer, as the person running the harness set it (the Options tab's
/// Source code): saved to source-folders.json beside the reports folder, so it's their own, not the app's. Applied to
/// the <see cref="SourceLocator"/> when the app starts; an empty folder means the configured default.
/// </summary>
public sealed class SourceStore
{
    private readonly SourceLocator _locator;
    private readonly ILogger<SourceStore>? _log;
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<Codebase, string> _folders;

    public SourceStore(SourceLocator locator, ReportStore reports, ILogger<SourceStore> log)
        : this(locator, Path.Combine(Path.GetDirectoryName(reports.Folder)!, "source-folders.json"), log)
    {
    }

    internal SourceStore(SourceLocator locator, string path, ILogger<SourceStore>? log)
    {
        _locator = locator;
        _path = path;
        _log = log;
        _folders = Load();
        foreach (var (codebase, folder) in _folders) _locator.SetFolder(codebase, folder);
    }

    /// <summary>The folder set for the codebase, or "" for the default.</summary>
    public string FolderOf(Codebase codebase)
    {
        lock (_gate) return _folders.GetValueOrDefault(codebase, "");
    }

    /// <summary>Sets (or, empty, clears) the codebase's folder and saves it. Returns why it can't be used, or null.</summary>
    public string? Set(Codebase codebase, string? folder)
    {
        folder = folder?.Trim().Trim('"') ?? "";
        lock (_gate)
        {
            if (folder.Length == 0) _folders.Remove(codebase);
            else _folders[codebase] = folder;
            Save();
        }
        return _locator.SetFolder(codebase, folder);
    }

    private Dictionary<Codebase, string> Load()
    {
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<Dictionary<Codebase, string>>(File.ReadAllText(_path), ReportStore.Json) is { } saved)
                return saved.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).ToDictionary();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _log?.LogWarning("Couldn't read {Path}: {Error}", _path, ex.Message);
        }
        return [];
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_folders, ReportStore.Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.LogWarning("Couldn't save {Path}: {Error}", _path, ex.Message);
        }
    }
}
