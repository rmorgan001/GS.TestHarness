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

using ASCOM.Common.DeviceInterfaces;
using GS.TestHarness.Engine;

namespace GS.TestHarness.Services;

/// <summary>
/// Where the mount is, as far as the home check can tell. <see cref="Error"/> is set when the mount couldn't be
/// connected or read; <see cref="Problem"/> is a message about a Go home that didn't work out (the mount is still connected).
/// </summary>
public sealed record HomeStatus(bool AtHome, bool AtPark, bool CanFindHome, string? Error = null, string? Problem = null, bool NotConnected = false)
{
    /// <summary>The mount couldn't be read; <paramref name="notConnected"/> when it couldn't even be connected.</summary>
    public static HomeStatus Failed(string error, bool notConnected = false) => new(false, false, false, error, NotConnected: notConnected);
}

/// <summary>Makes the <see cref="HomeSession"/> a run's home check works through.</summary>
public sealed class HomeService(ILogger<HomeService> log)
{
    public HomeSession Open(TestTarget target, RunOptions options) => new(log, target, options);
}

/// <summary>
/// Checks, before a run that moves the mount, that it is at the home position, and can send it there (FindHome).
/// One connection is kept for the whole check, the dialog's Check for home and Go home included, so the mount isn't
/// dropped and picked up again at every button. Reading is retried once after reconnecting (the mount was powered off
/// and on, so the link the server held is dead); a motion command (Unpark, FindHome) never is, so a slow or failed
/// Go home isn't interrupted and sent again. Calls are one at a time, and disposing waits for the one under way.
/// Disposing disconnects if this connected it, so the run finds the mount as it was.
/// </summary>
/// <param name="open">Opens the session to the device (the real transport unless a test gives another).</param>
public sealed class HomeSession(ILogger log, TestTarget target, RunOptions options, Func<Task<ITelescopeSession>>? open = null) : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(Math.Max(2, options.CallTimeoutSeconds));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ITelescopeSession? _session;
    private bool _connectedHere, _disposed;

    /// <summary>Reads AtHome (and AtPark, CanFindHome).</summary>
    public async Task<HomeStatus> CheckAsync()
    {
        await _gate.WaitAsync();
        try
        {
            return await ReadWithRetryAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Sends the mount home: unparks it if parked, calls FindHome and waits (up to the slew timeout) for it to arrive.
    /// Returns the status afterwards; <see cref="HomeStatus.Problem"/> says what went wrong if it didn't get there.
    /// </summary>
    public async Task<HomeStatus> GoHomeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var status = await ReadWithRetryAsync();
            if (status.Error is not null) return status;
            if (!status.CanFindHome)
                return status with { Problem = "This mount can't find home itself (CanFindHome is false). Put it in the home position by hand." };

            // From here on nothing is retried: the mount may already be moving.
            var slew = TimeSpan.FromSeconds(Math.Max(10, options.SlewTimeoutSeconds));
            string? problem = null;
            try
            {
                if (status.AtPark)
                {
                    await _session!.RunAsync(t => t.Unpark(), slew);
                    if (!await WaitAsync(t => !t.AtPark, slew)) problem = $"The mount didn't unpark within {slew.TotalSeconds:0} s.";
                }
                if (problem is null)
                {
                    await _session!.RunAsync(t => t.FindHome(), slew);
                    if (!await WaitAsync(t => t.AtHome && !t.Slewing, slew)) problem = $"The mount didn't reach the home position within {slew.TotalSeconds:0} s.";
                }
            }
            catch (Exception ex)
            {
                log.LogInformation("Go home for {Target}: {Error}", target.Display, ex.Message);
                problem = $"Go home failed: {AscomError.Describe(ex)}";
            }
            try
            {
                return await ReadAsync() with { Problem = problem };
            }
            catch (Exception ex)
            {
                return HomeStatus.Failed($"Couldn't read the mount's position: {AscomError.Describe(ex)}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HomeStatus> ReadAsync()
    {
        var session = _session!;
        var atHome = await session.RunAsync(t => t.AtHome, _timeout);
        var atPark = await session.RunAsync(t => t.AtPark, _timeout);
        var canFindHome = await session.RunAsync(t => t.CanFindHome, _timeout);
        return new HomeStatus(atHome, atPark, canFindHome);
    }

    /// <summary>Waits until <paramref name="done"/> holds; false if it still doesn't after <paramref name="limit"/>.</summary>
    private async Task<bool> WaitAsync(Func<ITelescopeV4, bool> done, TimeSpan limit)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < limit)
        {
            if (await _session!.RunAsync(done, TimeSpan.FromSeconds(15))) return true;
            await Task.Delay(500);
        }
        return false;
    }

    /// <summary>Connects and reads; if that fails, drops the connection, connects again and reads once more.</summary>
    private async Task<HomeStatus> ReadWithRetryAsync()
    {
        if (_disposed) return HomeStatus.Failed("The home check has been closed.");
        try
        {
            return await ConnectAndReadAsync();
        }
        catch (Exception first)
        {
            // A driver such as GS Server's reads Connected true for any client while its hardware is connected, but refuses the
            // commands of a client that hasn't connected itself (NotConnected, 0x407). This client read Connected true, so it never
            // called Connect: do it now, and read again, before dropping the session and starting over (which would do the same).
            if (_session is not null && AscomError.Classify(first).Kind == AscomErrorKind.NotConnected)
            {
                log.LogInformation("Home check for {Target}: {Error}; this client isn't connected to the driver, connecting it", target.Display, first.Message);
                try
                {
                    await ConnectThisClientAsync();
                    return await ReadAsync();
                }
                catch (Exception second)
                {
                    log.LogInformation("Home check for {Target}: still {Error}", target.Display, second.Message);
                }
            }
            log.LogInformation("Home check for {Target}: {Error}; connecting again", target.Display, first.Message);
            try
            {
                await DropAsync();
                return await ConnectAndReadAsync();
            }
            catch (NotConnectedException ex)
            {
                log.LogInformation("Home check for {Target}: {Error}", target.Display, ex.Message);
                return HomeStatus.Failed($"{AscomError.Describe(ex.InnerException ?? ex)}", notConnected: true);
            }
            catch (Exception ex)
            {
                log.LogInformation("Home check for {Target}: {Error}", target.Display, ex.Message);
                return HomeStatus.Failed($"Couldn't read the mount's position: {AscomError.Describe(ex)}");
            }
        }
    }

    /// <summary>
    /// Connects this client to the driver although Connected already reads true (the hardware is connected, but not by this client).
    /// Harmless on a driver that is connected (Connect while connected raises no error). It is left connected: the mount was connected
    /// before, and this client didn't make that connection, so <see cref="DropAsync"/> doesn't undo it.
    /// </summary>
    private async Task ConnectThisClientAsync()
    {
        var session = _session!;
        await session.RunAsync(t =>
        {
            try { t.Connect(); }
            catch (ASCOM.NotImplementedException) { t.Connected = true; }
        }, _timeout);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < ConnectTimeout && await session.RunAsync(t => t.Connecting, _timeout)) await Task.Delay(100);
    }

    /// <summary>The mount couldn't be connected (as opposed to connected but not readable).</summary>
    private sealed class NotConnectedException(Exception inner) : Exception(inner.Message, inner);

    private async Task<HomeStatus> ConnectAndReadAsync()
    {
        try { await EnsureConnectedAsync(); }
        catch (Exception ex) { throw new NotConnectedException(ex); }
        return await ReadAsync();
    }

    private async Task EnsureConnectedAsync()
    {
        if (_session is not null) return;
        var session = await (open?.Invoke() ?? TestRunner.OpenAsync(target, options));
        try
        {
            if (!await session.RunAsync(t => t.Connected, _timeout))
            {
                await session.RunAsync(t =>
                {
                    try { t.Connect(); }
                    catch (ASCOM.NotImplementedException) { t.Connected = true; }
                }, _timeout);
                _connectedHere = true;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (watch.Elapsed < ConnectTimeout && await session.RunAsync(t => t.Connecting, _timeout)) await Task.Delay(100);
                if (!await session.RunAsync(t => t.Connected, _timeout)) throw new InvalidOperationException("The mount didn't connect.");
            }
            _session = session;
        }
        catch
        {
            _session = session;
            await DropAsync();
            throw;
        }
    }

    /// <summary>Disconnects (only if this connected it) and closes the session; the next call opens a new one.</summary>
    private async Task DropAsync()
    {
        var session = _session;
        _session = null;
        if (session is null) return;
        if (_connectedHere)
        {
            try
            {
                await session.RunAsync(t =>
                {
                    try { t.Disconnect(); }
                    catch (ASCOM.NotImplementedException) { t.Connected = false; }
                }, _timeout);
            }
            catch { /* the link may already be dead */ }
            _connectedHere = false;
        }
        try { await session.DisposeAsync(); }
        catch { /* nothing more to do */ }
    }

    /// <summary>
    /// Hands the connection this made to <paramref name="adopt"/> (the sidebar's connection list), so the mount stays
    /// connected after the dialog closes. False if there was nothing to hand over (the mount was already connected by
    /// another client, or the adopter refused): disposing then leaves it as it was, or disconnects what this made.
    /// </summary>
    public bool TryHandOver(Func<ITelescopeSession, bool> adopt)
    {
        if (_session is null || !_connectedHere || !adopt(_session)) return false;
        _session = null;
        _connectedHere = false;
        return true;
    }

    /// <summary>Waits for a call under way, then disconnects (if this connected it); nothing reconnects afterwards.</summary>
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _gate.WaitAsync();
        try
        {
            await DropAsync();
        }
        finally
        {
            _gate.Release();
        }
    }
}
