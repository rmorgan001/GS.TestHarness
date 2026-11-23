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

using GS.TestHarness.Engine;

namespace GS.TestHarness.Services;

/// <summary>
/// The sidebar's Connect button, as in GS.Point3DWeb: connects a server from the list and keeps it connected until
/// it is disconnected, deleted or moved, or the app stops. Shared by every tab, so the status dots agree. A run
/// opens its own session; a server connected here simply counts as another client of it.
/// </summary>
public sealed class ConnectionService(ILogger<ConnectionService> log)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, ITelescopeSession> _sessions = [];
    private readonly HashSet<string> _busy = [];

    /// <summary>Raised (on any thread) when a server connects, disconnects or starts doing either.</summary>
    public event Action? Changed;

    public bool IsConnected(string id)
    {
        lock (_gate) return _sessions.ContainsKey(id);
    }

    public bool IsBusy(string id)
    {
        lock (_gate) return _busy.Contains(id);
    }

    /// <summary>
    /// Takes over a connection made elsewhere (the start dialog's home check), so the server shows as connected and the
    /// sidebar's Disconnect closes it. False if the server is already connected here, or is being connected or disconnected (the caller then disconnects what it made).
    /// </summary>
    public bool Adopt(string id, ITelescopeSession session)
    {
        lock (_gate)
        {
            if (_sessions.ContainsKey(id) || _busy.Contains(id)) return false;
            _sessions[id] = session;
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Connects (V4 Connect, falling back to Connected = true); returns whether the device reports connected.</summary>
    public async Task<bool> ConnectAsync(ServerEntry server)
    {
        if (!Begin(server.Id)) return IsConnected(server.Id);
        ITelescopeSession? session = null;
        try
        {
            var target = server.RunTarget;
            session = await TestRunner.OpenAsync(target, new RunOptions { CallTimeoutSeconds = 15 });
            await session.RunAsync(t =>
            {
                try { t.Connect(); }
                catch (ASCOM.NotImplementedException) { t.Connected = true; }
            }, Timeout);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.Elapsed < Timeout && await IsConnectingAsync(session)) await Task.Delay(100);
            var connected = await session.RunAsync(t => t.Connected, Timeout);
            if (!connected)
            {
                await session.DisposeAsync();
                return false;
            }
            lock (_gate) _sessions[server.Id] = session;
            session = null;
            log.LogInformation("Connected {Server} ({Target})", server.Name, server.Target.Display);
            return true;
        }
        catch
        {
            if (session is not null) await session.DisposeAsync();
            throw;
        }
        finally
        {
            End(server.Id);
        }
    }

    /// <summary>Disconnects; returns whether the device still reports connected (another client holds it).</summary>
    public async Task<bool> DisconnectAsync(string id)
    {
        ITelescopeSession? session;
        lock (_gate)
        {
            if (!_sessions.Remove(id, out session)) return false;
            _busy.Add(id);
        }
        Changed?.Invoke();
        try
        {
            await session.RunAsync(t =>
            {
                try { t.Disconnect(); }
                catch (ASCOM.NotImplementedException) { t.Connected = false; }
            }, Timeout);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.Elapsed < Timeout && await IsConnectingAsync(session)) await Task.Delay(100);
            return await session.RunAsync(t => t.Connected, Timeout);
        }
        finally
        {
            await session.DisposeAsync();
            End(id);
        }
    }

    /// <summary>Disconnects a server without waiting or reporting failures (it was deleted or moved).</summary>
    public void Release(string id) => _ = Task.Run(async () =>
    {
        try { await DisconnectAsync(id); }
        catch (Exception ex) { log.LogInformation("Releasing {Id}: {Error}", id, ex.Message); }
    });

    /// <summary>Disconnects everything (the app is stopping); waits a few seconds at most.</summary>
    public void DisconnectAll()
    {
        string[] ids;
        lock (_gate) ids = [.. _sessions.Keys];
        if (ids.Length == 0) return;
        var all = Task.WhenAll(ids.Select(async id =>
        {
            try { await DisconnectAsync(id); }
            catch { /* stopping: nothing more to do */ }
        }));
        all.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Whether a connect or disconnect is still under way; a driver without Connecting (before V4) has none to wait for.</summary>
    private static async Task<bool> IsConnectingAsync(ITelescopeSession session)
    {
        try { return await session.RunAsync(t => t.Connecting, Timeout); }
        catch (ASCOM.NotImplementedException) { return false; }
    }

    private bool Begin(string id)
    {
        lock (_gate)
        {
            if (_sessions.ContainsKey(id) || !_busy.Add(id)) return false;
        }
        Changed?.Invoke();
        return true;
    }

    private void End(string id)
    {
        lock (_gate) _busy.Remove(id);
        Changed?.Invoke();
    }
}
