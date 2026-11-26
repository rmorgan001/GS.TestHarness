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
/// it is disconnected, deleted or moved, tested, or the app stops. Shared by every tab, so the status dots agree.
/// Testing releases the sidebar client first; only an explicit Keep one connection hands a run client back.
/// </summary>
public sealed class ConnectionService(ILogger<ConnectionService> log)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, ITelescopeSession> _sessions = [];
    private readonly HashSet<string> _busy = [];
    private readonly HashSet<string> _testing = [];

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

    /// <summary>Reserves the server for testing, waits for a sidebar operation, and releases its existing client.</summary>
    public async Task BeginTestAsync(string id)
    {
        lock (_gate)
        {
            if (!_testing.Add(id)) throw new InvalidOperationException("This server is already being tested.");
        }
        Changed?.Invoke();
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (IsBusy(id))
            {
                if (watch.Elapsed >= Timeout) throw new TimeoutException("The sidebar connection operation did not finish before testing.");
                await Task.Delay(50);
            }
            await DisconnectAsync(id);
        }
        catch
        {
            EndTest(id);
            throw;
        }
    }

    /// <summary>Ends testing, optionally taking ownership of its one connected client.</summary>
    public void EndTest(string id, ITelescopeSession? session = null)
    {
        lock (_gate)
        {
            if (session is not null)
            {
                if (_sessions.ContainsKey(id) || _busy.Contains(id))
                    throw new InvalidOperationException("The sidebar cannot take over the test connection.");
                _sessions[id] = session;
            }
            _testing.Remove(id);
        }
        Changed?.Invoke();
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
            await session.ConnectClientAsync(Timeout);
            lock (_gate) _sessions[server.Id] = session;
            session = null;
            log.LogInformation("Connected {Server} ({Target})", server.Name, server.Target.Display);
            return true;
        }
        catch (Exception ex)
        {
            if (session is not null)
            {
                try { await session.ReleaseAsync(Timeout); }
                catch (Exception cleanup) { throw new AggregateException("Connecting failed and cleanup also failed.", ex, cleanup); }
            }
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
            return await session.ReleaseAsync(Timeout);
        }
        finally
        {
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

    private bool Begin(string id)
    {
        lock (_gate)
        {
            if (_testing.Contains(id)) throw new InvalidOperationException("This server is being tested; stop the run before connecting it.");
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
