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

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using GS.TestHarness.Engine;
using Microsoft.AspNetCore.DataProtection;

namespace GS.TestHarness.Services;

/// <summary>A server in the sidebar list: a name and the target it stands for.</summary>
public sealed record ServerEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Name { get; init; }
    public required TestTarget Target { get; init; }

    /// <summary>Alpaca: user name, for a server that asks for one.</summary>
    public string? UserName { get; init; }

    /// <summary>Alpaca: password, in memory only; saved encrypted as <see cref="ProtectedPassword"/>.</summary>
    [JsonIgnore]
    public string? Password { get; init; }

    /// <summary>The password as saved: encrypted with Data Protection (DPAPI on Windows).</summary>
    public string? ProtectedPassword { get; init; }

    /// <summary>A saved password couldn't be decrypted (the keys changed): it has to be typed again.</summary>
    [JsonIgnore]
    public bool PasswordUnreadable { get; init; }

    /// <summary>The target with its credentials, for a run or a connection.</summary>
    [JsonIgnore]
    public TestTarget RunTarget => Target.Kind == TargetKind.Alpaca ? Target with { UserName = UserName, Password = Password } : Target;

    /// <summary>Where the server is, for the list: host:port and device, or the ProgID.</summary>
    public string Address => Target.Kind == TargetKind.Com
        ? Target.ProgId
        : $"{(Target.Host.Contains(':') && !Target.Host.StartsWith('[') ? $"[{Target.Host}]" : Target.Host)}:{Target.Port} · telescope {Target.DeviceNumber}";

    /// <summary>The logo for the server's connection: ASCOM for GSServer's COM driver, Alpaca for GreenSwampAlpaca.</summary>
    public string Image => Target.Kind == TargetKind.Com ? "com.svg" : "alpaca.png";

    /// <summary>What kind of server it is: a COM driver or an Alpaca server (it needn't be GSServer or GreenSwampAlpaca).</summary>
    public string KindText => Target.Kind == TargetKind.Com ? "COM driver" : "Alpaca server";

    /// <summary>Whether two targets are the same device (so the list holds each once).</summary>
    public static bool SameDevice(TestTarget a, TestTarget b) => TestTarget.SameDevice(a, b);

    /// <summary>
    /// The server's own settings (the Settings section under it on the Run page), saved with it and recalled when it's
    /// dragged onto the Server card. Null until they're first used.
    /// </summary>
    public ServerSettings? Settings { get; init; }
}

/// <summary>A server's settings: where its code is, what it drives, its flip zone and its timeouts.</summary>
public sealed record ServerSettings
{
    /// <summary>The source code's folder or GitHub URL; empty for no code review.</summary>
    public string SourcePath { get; init; } = "";

    public MountKind Mount { get; init; } = MountKind.Unknown;

    /// <summary>The hemisphere the mount is in (Unknown: not checked).</summary>
    public Hemisphere SiteHemisphere { get; init; } = Hemisphere.Unknown;

    /// <summary>Degrees either side of the meridian (0: not known).</summary>
    public int FlipAngle { get; init; }

    public int SlewTimeoutSeconds { get; init; } = 120;

    public int CallTimeoutSeconds { get; init; } = 15;
}

/// <summary>
/// The servers in the sidebar, shared by every browser tab and saved to servers.json beside the reports folder.
/// The first time, the list starts with GSServer's COM driver and a GreenSwampAlpaca server on this computer.
/// </summary>
public sealed class ServerList
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly ILogger<ServerList> _log;
    private List<ServerEntry> _items;

    /// <summary>Raised (on any thread) when an entry is added, changed or removed.</summary>
    public event Action? Changed;

    private readonly IDataProtector? _protector;

    public ServerList(ReportStore reports, IDataProtectionProvider protection, ILogger<ServerList> log)
        : this(Path.Combine(reports.SettingsFolder, "servers.json"), protection.CreateProtector("GS.TestHarness.ServerPasswords"), log)
    {
    }

    /// <summary>Starts from this file (the tests); without a protector, passwords aren't saved.</summary>
    internal ServerList(string path, IDataProtector? protector, ILogger<ServerList> log)
    {
        _log = log;
        _path = path;
        _protector = protector;
        _items = Load() ?? Defaults();
    }

    public static List<ServerEntry> Defaults() =>
    [
        new() { Name = "GSServer", Target = new TestTarget { Codebase = Codebase.GSServer } },
        new() { Name = "GS Simulator (GEM)", Target = new TestTarget { Codebase = Codebase.GreenSwampAlpaca, Host = "localhost", Port = 31416, DeviceNumber = 0 } }
    ];

    public IReadOnlyList<ServerEntry> Items
    {
        get { lock (_gate) return [.. _items]; }
    }

    public ServerEntry? Find(string? id)
    {
        if (id is null) return null;
        lock (_gate) return _items.FirstOrDefault(s => s.Id == id);
    }

    /// <summary>The entry already in the list for the same device, other than <paramref name="exceptId"/>.</summary>
    public ServerEntry? FindDevice(TestTarget target, string? exceptId = null)
    {
        lock (_gate) return _items.FirstOrDefault(s => s.Id != exceptId && ServerEntry.SameDevice(s.Target, target));
    }

    public void Add(ServerEntry entry) => Change(list => list.Add(entry));

    /// <summary>Replaces the entry with the same ID.</summary>
    public void Update(ServerEntry entry) => Change(list =>
    {
        var i = list.FindIndex(s => s.Id == entry.Id);
        if (i >= 0) list[i] = entry;
    });

    public void Remove(string id) => Change(list => list.RemoveAll(s => s.Id == id));

    /// <summary>Writes the list to disk now (the Save button); throws if it can't.</summary>
    public void SaveNow()
    {
        lock (_gate) Write(_items);
    }

    private void Change(Action<List<ServerEntry>> change)
    {
        lock (_gate)
        {
            var list = new List<ServerEntry>(_items);
            change(list);
            _items = list;
            Save(list);
        }
        Changed?.Invoke();
    }

    private List<ServerEntry>? Load()
    {
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<List<ServerEntry>>(File.ReadAllText(_path), ReportStore.Json) is { } list)
                return [.. list.Select(Unprotect)];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Couldn't read the server list {Path}: {Error}", _path, ex.Message);
            // A damaged file is kept aside: the defaults that replace it would be saved over it at the next change.
            if (ex is JsonException)
            {
                try { File.Move(_path, _path + ".bad", overwrite: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        return null;
    }

    private ServerEntry Protect(ServerEntry entry)
    {
        // A password that couldn't be read is kept as saved, so it isn't lost by saving other changes.
        if (entry.PasswordUnreadable) return entry;
        return entry with { ProtectedPassword = string.IsNullOrEmpty(entry.Password) || _protector is null ? null : _protector.Protect(entry.Password) };
    }

    private ServerEntry Unprotect(ServerEntry entry)
    {
        if (entry.ProtectedPassword is not { Length: > 0 } saved || _protector is null) return entry;
        try
        {
            return entry with { Password = _protector.Unprotect(saved) };
        }
        catch (CryptographicException)
        {
            _log.LogWarning("The saved password for {Server} can't be decrypted; enter it again", entry.Name);
            return entry with { PasswordUnreadable = true };
        }
    }

    private void Write(List<ServerEntry> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(list.Select(Protect), ReportStore.Json));
        File.Move(_path + ".tmp", _path, overwrite: true);
    }

    private void Save(List<ServerEntry> list)
    {
        try
        {
            Write(list);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _log.LogWarning("Couldn't save the server list {Path}: {Error}", _path, ex.Message);
        }
    }
}

/// <summary>
/// One browser tab's choices: the server picked for the run (shown in the Run page's Server box) and the one
/// being dragged from the sidebar. Scoped: one per tab.
/// </summary>
public sealed class Workbench(TargetStore saved, ServerList servers)
{
    private string? _selectedId = saved.Load().ServerId;

    public event Action? Changed;

    /// <summary>The server picked for the run, if it is still in the list.</summary>
    public ServerEntry? Selected => servers.Find(_selectedId);

    /// <summary>The entry highlighted in the sidebar (the one Edit and Delete act on).</summary>
    public string? HighlightedId { get; private set; }

    /// <summary>The entry being dragged, while a drag is under way.</summary>
    public ServerEntry? Dragging { get; private set; }

    public void Select(ServerEntry? entry)
    {
        _selectedId = entry?.Id;
        saved.SaveServer(_selectedId);
        Changed?.Invoke();
    }

    public void Highlight(ServerEntry? entry)
    {
        HighlightedId = entry?.Id;
        Changed?.Invoke();
    }

    public void StartDrag(ServerEntry entry)
    {
        Dragging = entry;
        HighlightedId = entry.Id;
        Changed?.Invoke();
    }

    public void EndDrag()
    {
        if (Dragging is null) return;
        Dragging = null;
        Changed?.Invoke();
    }

    /// <summary>Drops the dragged entry on the Server box: it becomes the run's server.</summary>
    public void Drop()
    {
        var entry = Dragging;
        Dragging = null;
        if (entry is not null) Select(entry);
        else Changed?.Invoke();
    }
}
