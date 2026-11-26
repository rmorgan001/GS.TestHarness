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

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ASCOM.Alpaca.Clients;
using ASCOM.Common.Alpaca;
using ASCOM.Common.DeviceInterfaces;

namespace GS.TestHarness.Engine;

/// <summary>
/// The mount under test, as an <see cref="ITelescopeV4"/>, whatever the transport. Every call goes through
/// <see cref="RunAsync{T}"/>, which runs it where the transport needs it (a COM driver's own thread) and stops
/// waiting after a timeout.
/// </summary>
public interface ITelescopeSession : IAsyncDisposable
{
    /// <summary>"Alpaca" or "COM", for the findings.</summary>
    string Transport { get; }

    Task<T> RunAsync<T>(Func<ITelescopeV4, T> call, TimeSpan timeout);
}

public static class TelescopeSessionExtensions
{
    public static Task RunAsync(this ITelescopeSession session, Action<ITelescopeV4> call, TimeSpan timeout)
        => session.RunAsync(t => { call(t); return true; }, timeout);

    public static async Task ConnectClientAsync(this ITelescopeSession session, TimeSpan timeout)
    {
        await session.RunAsync(t =>
        {
            try { t.Connect(); }
            catch (ASCOM.NotImplementedException) { t.Connected = true; }
        }, timeout);
        await WaitForConnectionAsync(session, timeout);
        if (!await session.RunAsync(t => t.Connected, timeout))
            throw new InvalidOperationException("The mount didn't connect.");
    }

    /// <summary>Releases this client's connection; true means the hardware is still connected, possibly by another client.</summary>
    public static async Task<bool> DisconnectClientAsync(this ITelescopeSession session, TimeSpan timeout)
    {
        await session.RunAsync(t =>
        {
            try { t.Disconnect(); }
            catch (ASCOM.NotImplementedException) { t.Connected = false; }
        }, timeout);
        await WaitForConnectionAsync(session, timeout);
        return await session.RunAsync(t => t.Connected, timeout);
    }

    /// <summary>Always attempts both disconnect and disposal, and reports either failure to the owner.</summary>
    public static async Task<bool> ReleaseAsync(this ITelescopeSession session, TimeSpan timeout)
    {
        var errors = new List<Exception>();
        var connected = false;
        try { connected = await session.DisconnectClientAsync(timeout); }
        catch (Exception ex) { errors.Add(ex); }
        try { await session.DisposeAsync(); }
        catch (Exception ex) { errors.Add(ex); }
        if (errors.Count > 0) throw new AggregateException("Connection cleanup failed.", errors);
        return connected;
    }

    private static async Task WaitForConnectionAsync(ITelescopeSession session, TimeSpan timeout)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            var remaining = timeout - watch.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            bool connecting;
            try { connecting = await session.RunAsync(t => t.Connecting, remaining); }
            catch (ASCOM.NotImplementedException) { return; }
            if (!connecting) return;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Connecting stayed true for {timeout.TotalSeconds:0.#} s.");
    }
}

/// <summary>
/// Runs a device call on the thread pool. Whatever the call raises is caught inside the delegate and rethrown from
/// the awaiting method. Device calls are expected to fail (most checks provoke an error on purpose), and an
/// exception that left the delegate would be caught by the thread pool, outside "my code": Visual Studio then
/// stops the whole app with "Exception User-Unhandled" on every one, which looks like a hang.
/// </summary>
internal static class DeviceCall
{
    public static async Task<T> RunAsync<T>(Func<T> call, TimeSpan timeout)
    {
        var work = Task.Run(() =>
        {
            try
            {
                return (Value: call(), Error: (System.Runtime.ExceptionServices.ExceptionDispatchInfo?)null);
            }
            catch (Exception ex)
            {
                return (Value: default(T)!, Error: System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex));
            }
        });
        (T Value, System.Runtime.ExceptionServices.ExceptionDispatchInfo? Error) result;
        try
        {
            result = await work.WaitAsync(timeout);
        }
        catch (TimeoutException) when (!work.IsCompleted)
        {
            throw new CallTimeoutException(timeout);
        }
        result.Error?.Throw();
        return result.Value;
    }
}

/// <summary>Raised when a call doesn't answer in time (the harness stopped waiting).</summary>
public sealed class CallTimeoutException(TimeSpan timeout)
    : TimeoutException($"no answer within {timeout.TotalSeconds:0.#} s");

/// <summary>A telescope on an Alpaca server, through the ASCOM Alpaca client.</summary>
public sealed class AlpacaSession : ITelescopeSession
{
    private readonly AlpacaTelescope _telescope;

    public AlpacaSession(TestTarget target, TimeSpan callTimeout)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(callTimeout.TotalSeconds));
        _telescope = new AlpacaTelescope(new AlpacaConfiguration
        {
            ServiceType = ServiceType.Http,
            IpAddressString = target.Host,
            PortNumber = target.Port,
            RemoteDeviceNumber = target.DeviceNumber,
            EstablishConnectionTimeout = Math.Min(seconds, 5),
            StandardDeviceResponseTimeout = seconds,
            LongDeviceResponseTimeout = Math.Max(seconds, 120),
            ClientNumber = (uint)Random.Shared.Next(1, 65535),
            // The harness checks the server's JSON casing itself (the protocol checks); the client mustn't
            // hide interface results behind a casing error.
            StrictCasing = false,
            UserName = target.UserName ?? "",
            Password = target.Password ?? "",
            UserAgentProductName = "GS.TestHarness",
            UserAgentProductVersion = typeof(AlpacaSession).Assembly.GetName().Version?.ToString(3) ?? "1.0"
        });
    }

    public string Transport => "Alpaca";

    public Task<T> RunAsync<T>(Func<ITelescopeV4, T> call, TimeSpan timeout)
        => DeviceCall.RunAsync(() => call(_telescope), timeout);

    public ValueTask DisposeAsync()
    {
        _telescope.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// An ASCOM driver on this computer, through COM. The driver is created and called on its own
/// single-threaded-apartment thread that dispatches window messages, as COM drivers expect.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ComSession : ITelescopeSession
{
    private readonly StaThread _thread;
    private ASCOM.Com.DriverAccess.Telescope? _telescope;

    private ComSession(string progId)
    {
        _thread = new StaThread($"ASCOM {progId}");
    }

    /// <summary>Starts the driver (which may start its server program); throws if it can't be created.</summary>
    public static async Task<ComSession> CreateAsync(string progId, TimeSpan timeout)
    {
        var session = new ComSession(progId);
        try
        {
            await session._thread.Run(() => session._telescope = new ASCOM.Com.DriverAccess.Telescope(progId)).WaitAsync(timeout);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    public string Transport => "COM";

    public async Task<T> RunAsync<T>(Func<ITelescopeV4, T> call, TimeSpan timeout)
    {
        var work = _thread.Run(() => call(_telescope ?? throw new ObjectDisposedException("The ASCOM driver was released.")));
        try
        {
            return await work.WaitAsync(timeout);
        }
        catch (TimeoutException) when (!work.IsCompleted)
        {
            throw new CallTimeoutException(timeout);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _thread.Run(() =>
            {
                _telescope?.Dispose();
                _telescope = null;
                return true;
            }).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            _thread.Dispose();
        }
    }

    /// <summary>The ASCOM telescope drivers registered on this computer: (ProgID, name).</summary>
    public static IReadOnlyList<(string ProgId, string Name)> GetDrivers()
        => [.. ASCOM.Com.Profile.GetDrivers(ASCOM.Common.DeviceTypes.Telescope)
            .Where(d => !string.IsNullOrWhiteSpace(d.ProgID))
            .Select(d => (d.ProgID, string.IsNullOrWhiteSpace(d.Name) ? d.ProgID : d.Name))
            .OrderBy(d => d.Item2, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>
    /// A single-threaded-apartment thread that runs queued work one item at a time and, while waiting,
    /// dispatches window messages (as in GS.Point3DWeb's ComTelescopes).
    /// </summary>
    private sealed class StaThread : IDisposable
    {
        private readonly ConcurrentQueue<Action> _work = new();
        private readonly AutoResetEvent _signal = new(false);
        private readonly object _gate = new();
        private bool _stopping;

        public StaThread(string name)
        {
            var thread = new Thread(Loop) { IsBackground = true, Name = name };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private void Loop()
        {
            var handles = new[] { _signal.SafeWaitHandle.DangerousGetHandle() };
            try
            {
                while (true)
                {
                    while (_work.TryDequeue(out var item)) item();
                    lock (_gate)
                    {
                        if (_stopping && _work.IsEmpty) return;
                    }
                    MsgWaitForMultipleObjectsEx(1, handles, 0xFFFFFFFF, 0x04FF, 0x0004);
                    while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
                    {
                        TranslateMessage(in msg);
                        DispatchMessage(in msg);
                    }
                }
            }
            finally
            {
                _signal.Dispose();
            }
        }

        public Task<T> Run<T>(Func<T> work)
        {
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (_stopping)
                {
                    result.SetException(new ObjectDisposedException("The ASCOM driver was released."));
                    return result.Task;
                }
                _work.Enqueue(() =>
                {
                    try { result.SetResult(work()); }
                    catch (Exception ex) { result.SetException(ex); }
                });
                _signal.Set();
            }
            return result.Task;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_stopping) return;
                _stopping = true;
                _signal.Set();
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Msg
        {
            public IntPtr Hwnd;
            public uint Message;
            public IntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public int X;
            public int Y;
            public uint Private;
        }

        [DllImport("user32.dll")]
        private static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr[] handles, uint milliseconds, uint wakeMask, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekMessage(out Msg msg, IntPtr hwnd, uint filterMin, uint filterMax, uint remove);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TranslateMessage(in Msg msg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(in Msg msg);
    }
}

/// <summary>
/// An <see cref="ITelescopeV4"/> object in this process (the tests' fake mount). Each session is a client with its
/// own number, which the object can read from <see cref="CurrentClient"/> during a call.
/// </summary>
public sealed class DirectSession(ITelescopeV4 telescope, int clientId = 0) : ITelescopeSession
{
    /// <summary>The client making the current call.</summary>
    public static readonly AsyncLocal<int> CurrentClient = new();

    public string Transport => "Direct";

    public Task<T> RunAsync<T>(Func<ITelescopeV4, T> call, TimeSpan timeout)
        => DeviceCall.RunAsync(() =>
        {
            CurrentClient.Value = clientId;
            return call(telescope);
        }, timeout);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
