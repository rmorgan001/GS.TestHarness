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

/// <summary>Remembers the server last picked for a run and the options used, so the Run page starts with them.</summary>
public sealed class TargetStore(ReportStore reports, ILogger<TargetStore> log)
{
    public sealed record Saved(string? ServerId, RunOptions Options);

    private readonly object _gate = new();
    private string PathOf => Path.Combine(reports.SettingsFolder, "last-run.json");

    public Saved Load()
    {
        try
        {
            lock (_gate)
            {
                if (File.Exists(PathOf) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(PathOf), ReportStore.Json) is { } saved)
                    return saved with { Options = saved.Options ?? new RunOptions() };
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Couldn't read {Path}: {Error}", PathOf, ex.Message);
        }
        return new Saved(null, new RunOptions());
    }

    public void SaveServer(string? serverId) => Write(Load() with { ServerId = serverId });

    public void SaveOptions(RunOptions options) => Write(Load() with { Options = options });

    private void Write(Saved saved)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PathOf)!);
                File.WriteAllText(PathOf, JsonSerializer.Serialize(saved, ReportStore.Json));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Couldn't save {Path}: {Error}", PathOf, ex.Message);
        }
    }
}
