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

using System.Text.RegularExpressions;
using GS.TestHarness.Engine;

namespace GS.TestHarness.Services;

/// <summary>
/// Shows popup dialogs for one browser tab (scoped), one at a time, as in GS.Point3DWeb: a caller awaits
/// <see cref="ShowAsync"/>; the DialogHost in the layout renders <see cref="Current"/>.
/// </summary>
public sealed class DialogService
{
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public DialogModel? Current { get; private set; }

    public event Action? Changed;

    /// <summary>Shows the dialog once any open one has closed; returns whether it was accepted.</summary>
    public async Task<bool> ShowAsync(DialogModel dialog)
    {
        await _oneAtATime.WaitAsync();
        try
        {
            Current = dialog;
            Changed?.Invoke();
            return await dialog.Result;
        }
        finally
        {
            Current = null;
            Changed?.Invoke();
            _oneAtATime.Release();
        }
    }

    /// <summary>Tells the user something (one OK button).</summary>
    public Task ShowMessageAsync(string message, string title = "GS Test Harness")
        => ShowAsync(new ConfirmDialogModel(title, message, "OK", null));

    public void Refresh() => Changed?.Invoke();
}

/// <summary>A dialog's state; closing it completes <see cref="Result"/> (true for accept).</summary>
public abstract class DialogModel
{
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<bool> Result => _result.Task;

    public virtual void Close(bool accepted) => _result.TrySetResult(accepted);
}

/// <summary>Asks a yes/no question, or (with no cancel text) just tells the user something.</summary>
public sealed class ConfirmDialogModel(string title, string message, string acceptText = "Yes", string? cancelText = "No", bool danger = false) : DialogModel
{
    public string Title { get; } = title;
    public string Message { get; } = message;
    public string AcceptText { get; } = acceptText;
    public string? CancelText { get; } = cancelText;
    public bool Danger { get; } = danger;
}

/// <summary>
/// The warning shown before every run: the user takes responsibility for watching the mount. For a run that moves the
/// mount it holds a <see cref="HomeSession"/> (the mount is connected while the dialog is open) and, when the mount
/// isn't at the home position, adds the "put the mount in the home position" section with Go home and Check again.
/// Accepting means the user confirms the mount is at home (if it was asked), so the run can start.
/// </summary>
public sealed class StartDialogModel(string title, string message, HomeSession? home, HomeStatus? status, IReadOnlyList<string>? warnings = null) : DialogModel
{
    public string Title { get; } = title;
    public string Message { get; } = message;

    /// <summary>Settings that look wrong for this run (each a ⚠ line), shown with the warning.</summary>
    public IReadOnlyList<string> Warnings { get; } = warnings ?? [];

    /// <summary>The mount's position, once read; null for a run that doesn't move the mount.</summary>
    public HomeStatus? Status { get; private set; } = status;
    public bool HasMount => home is not null;
    public bool Busy { get; private set; }
    public string? Note { get; private set; }

    /// <summary>The note says something went wrong (a Go home that failed), not progress.</summary>
    public bool NoteIsProblem { get; private set; }

    /// <summary>A Go home or check under way can't be cancelled: the mount may be moving, and closing would pull the connection from under it.</summary>
    public override void Close(bool accepted)
    {
        if (Busy) return;
        base.Close(accepted);
    }

    /// <summary>The mount is connected but isn't reporting that it's at home: the home section is shown.</summary>
    public bool NeedsHome => home is not null && Status is { Error: null, AtHome: false };

    /// <summary>The mount is connected and reports that it is at home.</summary>
    public bool IsAtHome => home is not null && Status is { Error: null, AtHome: true };

    /// <summary>The mount couldn't be connected or read: only Check again and Cancel are offered.</summary>
    public bool HasError => home is not null && Status?.Error is not null;

    /// <summary>The mount couldn't be connected (as opposed to connected but not readable).</summary>
    public bool NotConnected => HasError && Status is { NotConnected: true };

    /// <summary>Reads the mount's position again (after the user moved it, or powered it off and on).</summary>
    public Task CheckAsync(Action changed) => RunAsync("Checking the mount…", h => h.CheckAsync(), changed);

    /// <summary>Sends the mount to the home position and waits for it to get there.</summary>
    public Task GoHomeAsync(Action changed) => RunAsync("Sending the mount home…", h => h.GoHomeAsync(), changed);

    private async Task RunAsync(string note, Func<HomeSession, Task<HomeStatus>> work, Action changed)
    {
        if (Busy || home is null) return;
        Busy = true;
        (Note, NoteIsProblem) = (note, false);
        changed();
        try
        {
            Status = await work(home);
            NoteIsProblem = Status?.Problem is not null;
            Note = Status?.Problem
                ?? (IsAtHome ? "The mount is at the home position. It's okay to click \"Start run\"."
                : NeedsHome ? "The mount still isn't reporting that it's at home." : null);
        }
        finally
        {
            Busy = false;
            changed();
        }
    }
}

/// <summary>
/// The warnings before the mount moves: Proceed, Skip (the groups that move the mount) or Stop. Closing it any other way
/// (Escape) is Stop.
/// </summary>
public sealed class GateDialogModel(GateRequest request) : DialogModel
{
    public IReadOnlyList<string> Warnings { get; } = request.Warnings;
    public GateRequest Request { get; } = request;
    public GateDecision? Decision { get; private set; }

    /// <summary>Closed without an answer (the page was left, or another tab answered): the run keeps waiting.</summary>
    public bool Abandoned { get; private set; }

    public void Abandon()
    {
        Abandoned = true;
        Close(false);
    }

    public void Choose(GateDecision decision)
    {
        Decision = decision;
        Close(true);
    }
}

/// <summary>Picks an ASCOM telescope driver registered on this computer (as GS.Point3DWeb's ASCOM Drivers dialog).</summary>
public sealed class ComDriversDialogModel : DialogModel
{
    public string Message { get; private set; } = "Loading…";
    public IReadOnlyList<(string ProgId, string Name)> Drivers { get; private set; } = [];
    public string? SelectedProgId { get; set; }
    public string? SelectedName => Drivers.FirstOrDefault(d => d.ProgId == SelectedProgId).Name;

    public async Task LoadAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            Message = "ASCOM COM drivers can only be used on Windows.";
            return;
        }
        try
        {
            Drivers = await Task.Run(() => OperatingSystem.IsWindows() ? ComSession.GetDrivers() : []);
            // GSServer's driver first picked, as it is the one the harness is for.
            SelectedProgId = Drivers.FirstOrDefault(d => string.Equals(d.ProgId, "ASCOM.GS.Sky.Telescope", StringComparison.OrdinalIgnoreCase)).ProgId;
            Message = Drivers.Count > 0
                ? "Select an ASCOM telescope driver on the computer running this app. Each is tested as GSServer (COM)."
                : "No ASCOM telescope drivers are installed on the computer running this app.";
        }
        catch (Exception e)
        {
            Message = $"Could not read the ASCOM drivers: {e.Message}";
        }
    }
}

/// <summary>Adds or edits a server: its name, which server it is, and where (host, port, device; or ProgID).</summary>
public sealed partial class ServerDialogModel : DialogModel
{
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$")]
    private static partial Regex ProgIdPattern();

    private readonly ServerList _servers;

    public ServerDialogModel(ServerList servers, ServerEntry? entry)
    {
        _servers = servers;
        IsNew = entry is null;
        Id = entry?.Id ?? Guid.NewGuid().ToString("N");
        var target = entry?.Target ?? new TestTarget();
        Name = entry?.Name ?? "";
        Server = OperatingSystem.IsWindows() ? target.Codebase : Codebase.GreenSwampAlpaca;
        Host = target.Host;
        Port = target.Port;
        DeviceNumber = target.DeviceNumber;
        ProgId = target.ProgId;
        UserName = entry?.UserName;
        Password = entry?.Password;
        _unreadable = entry?.PasswordUnreadable ?? false;
        _savedPassword = entry?.ProtectedPassword;
        PasswordEdited = false; // setting Password above isn't an edit
    }

    // A saved password that couldn't be decrypted is kept until the user types a new one (or clears it).
    private readonly bool _unreadable;
    private readonly string? _savedPassword;

    public string? UserName { get; set; }

    public string? Password
    {
        get;
        set
        {
            if (field != value) PasswordEdited = true;
            field = value;
        }
    }

    private bool PasswordEdited { get; set; }

    public bool PasswordUnreadable => _unreadable && !PasswordEdited;

    public bool IsNew { get; }
    public string Id { get; }
    public string Title => IsNew ? "Add Server" : "Edit Server";

    public string Name { get; set; }
    public Codebase Server { get; set; }
    public string Host { get; set; }
    public int Port { get; set; }
    public int DeviceNumber { get; set; }
    public string ProgId { get; set; }

    public string? NameError { get; private set; }
    public string? HostError { get; private set; }
    public string? PortError { get; private set; }
    public string? DeviceError { get; private set; }
    public string? ProgIdError { get; private set; }

    /// <summary>The same device is already in the list.</summary>
    public string? DuplicateError { get; private set; }

    public TestTarget Target => new()
    {
        Codebase = Server,
        Host = Host.Trim(),
        Port = Port,
        DeviceNumber = DeviceNumber,
        ProgId = ProgId.Trim()
    };

    public ServerEntry Entry => Server == Codebase.GreenSwampAlpaca
        ? new()
        {
            Id = Id, Name = Name.Trim(), Target = Target,
            UserName = string.IsNullOrWhiteSpace(UserName) ? null : UserName.Trim(),
            Password = string.IsNullOrEmpty(Password) ? null : Password,
            PasswordUnreadable = PasswordUnreadable,
            ProtectedPassword = PasswordUnreadable ? _savedPassword : null
        }
        // A COM driver has no credentials.
        : new() { Id = Id, Name = Name.Trim(), Target = Target };

    /// <summary>Checks the fields; true when the server can be saved.</summary>
    public bool Validate()
    {
        NameError = string.IsNullOrWhiteSpace(Name) ? "Required" : null;
        HostError = PortError = DeviceError = ProgIdError = null;
        if (Server == Codebase.GreenSwampAlpaca)
        {
            HostError = string.IsNullOrWhiteSpace(Host) ? "Required"
                : Uri.CheckHostName(Host.Trim().Trim('[', ']')) == UriHostNameType.Unknown ? "Not a host name or IP address" : null;
            PortError = Port is < 1 or > 65535 ? "1 to 65535" : null;
            DeviceError = DeviceNumber < 0 ? "0 or more" : null;
        }
        else
        {
            ProgIdError = !OperatingSystem.IsWindows() ? "COM drivers need Windows"
                : string.IsNullOrWhiteSpace(ProgId) ? "Required"
                : !ProgIdPattern().IsMatch(ProgId.Trim()) ? "Not a ProgID (e.g. ASCOM.GS.Sky.Telescope)" : null;
        }
        var duplicate = _servers.FindDevice(Target, Id);
        DuplicateError = duplicate is null ? null : $"Already in the list as \"{duplicate.Name}\".";
        return NameError is null && HostError is null && PortError is null && DeviceError is null && ProgIdError is null && DuplicateError is null;
    }
}

/// <summary>Finds GreenSwampAlpaca telescopes on the network (Alpaca discovery) and adds the ones picked.</summary>
public sealed class DiscoveryDialogModel(ServerList servers) : DialogModel
{
    public bool Searching { get; private set; }
    public List<(FoundTelescope Found, bool InList, bool Picked)> Found { get; } = [];

    public async Task SearchAsync(Action changed)
    {
        Searching = true;
        Found.Clear();
        changed();
        try
        {
            foreach (var f in await Discovery.FindTelescopesAsync())
            {
                var target = ToTarget(f);
                Found.Add((f, servers.FindDevice(target) is not null, false));
            }
        }
        finally
        {
            Searching = false;
            changed();
        }
    }

    public void Toggle(int index)
    {
        var (f, inList, picked) = Found[index];
        if (!inList) Found[index] = (f, inList, !picked);
    }

    /// <summary>Adds the picked telescopes to the list.</summary>
    public int AddPicked()
    {
        var added = 0;
        foreach (var (f, inList, picked) in Found)
        {
            if (!picked || inList || servers.FindDevice(ToTarget(f)) is not null) continue;
            servers.Add(new ServerEntry { Name = f.Name, Target = ToTarget(f) });
            added++;
        }
        return added;
    }

    private static TestTarget ToTarget(FoundTelescope f)
        => new() { Codebase = Codebase.GreenSwampAlpaca, Host = f.Host, Port = f.Port, DeviceNumber = f.DeviceNumber };
}
