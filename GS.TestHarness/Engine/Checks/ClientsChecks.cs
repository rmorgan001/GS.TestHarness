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

using System.Diagnostics;
using ASCOM.Common.DeviceInterfaces;

namespace GS.TestHarness.Engine.Checks;

/// <summary>
/// Several clients at once (extended): one disconnecting mustn't cut off another; reads from several clients in
/// parallel all answer correctly; and connecting and disconnecting quickly, over and over, leaves no trouble.
/// The run's own client stays connected throughout.
/// </summary>
public sealed class ClientsChecks : ICheckGroup
{
    public string Name => "Clients";
    public string Description => "Extended: two more clients connect and one disconnects (the other must keep working), parallel reads from four clients, and ten quick connect/disconnect cycles.";

    public string? SkipReason(CheckContext ctx)
        => !ctx.Options.Extended ? "extended checks are turned off for this run"
            : ctx.OpenSession is null ? "another client can't be opened here" : null;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task RunAsync(CheckContext ctx)
    {
        await TwoClientsAsync(ctx);
        await ParallelReadsAsync(ctx);
        await ConnectCyclesAsync(ctx);
    }

    private static async Task ConnectAsync(ITelescopeSession s)
    {
        await s.RunAsync(t => t.Connect(), Timeout);
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < Timeout && await s.RunAsync(t => t.Connecting, Timeout)) await Task.Delay(50);
    }

    private static async Task DisconnectAsync(ITelescopeSession s)
    {
        await s.RunAsync(t => t.Disconnect(), Timeout);
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < Timeout && await s.RunAsync(t => t.Connecting, Timeout)) await Task.Delay(50);
    }

    /// <summary>Client B and client C connect; B disconnects; C must still be connected and answering.</summary>
    private static async Task TwoClientsAsync(CheckContext ctx)
    {
        ITelescopeSession? b = null, c = null;
        try
        {
            b = await ctx.OpenSession!();
            c = await ctx.OpenSession!();
            await ConnectAsync(b);
            await ConnectAsync(c);
            await DisconnectAsync(b);
            // B has disconnected while C still holds the device. Connected reports the hardware state: in a hub it stays
            // true until every client has disconnected, and the ASCOM notes say a client that set it false and still reads
            // true is "not an error". So true is allowed here, and false (B's own state) is fine too. ConformU still reports
            // "The device disconnected without error but Connected Get returned True" when another client is connected.
            var own = await Try(() => b.RunAsync(t => t.Connected, Timeout));
            ctx.Report("DisconnectedClient.Connected", "Connected", own.Error is not null ? Outcome.Fail : own.Value ? Outcome.Info : Outcome.Pass,
                own.Error is not null ? "Reading Connected on a client that disconnected raised an error."
                    : own.Value ? "A client that disconnected reads Connected true while another client keeps the hardware connected: " +
                                  "allowed, since Connected reports the hardware state (in a hub it stays true until every client has " +
                                  "disconnected). ConformU reports this as an issue whenever another client is connected during its run: " +
                                  "close the other clients before running ConformU."
                    : "A client that disconnected reads Connected false while another client is still connected.",
                expected: "false (this client's state) or true (the hardware's, still connected by the other client): both are allowed",
                actual: own.Error is null ? own.Value.ToString() : AscomError.Describe(own.Error),
                fix: "Connected must stay readable after Disconnect(): it reports whether the hardware is connected.",
                sourceMember: "Connected");
            var connected = await Try(() => c.RunAsync(t => t.Connected, Timeout));
            var ra = await Try(() => c.RunAsync(t => t.RightAscension, Timeout));
            var ok = connected is (true, null) && ra.Error is null;
            ctx.Report("OneDisconnects", "Disconnect", ok ? Outcome.Pass : Outcome.Fail,
                ok ? "One client disconnecting left the other connected and answering."
                    : "When one client disconnected, another client was cut off too.",
                expected: "the other client still Connected and answering",
                actual: $"Connected: {(connected.Error is null ? connected.Value.ToString() : AscomError.Describe(connected.Error))}; " +
                        $"RightAscension: {(ra.Error is null ? CheckContext.Show(ra.Value) : AscomError.Describe(ra.Error))}",
                fix: "A device used by several clients must count its connections: Disconnect from one client may only " +
                     "disconnect the hardware when no other client is still connected.",
                sourceMember: "Disconnect", sourcePatterns: ["SetConnected", "clientKey"]);
        }
        catch (Exception ex)
        {
            ctx.Report("OneDisconnects", "Connect", Outcome.Error, $"Couldn't set up two more clients: {AscomError.Describe(ex)}");
        }
        finally
        {
            if (c is not null) { await Quietly(() => DisconnectAsync(c)); await c.DisposeAsync(); }
            if (b is not null) await b.DisposeAsync();
        }
    }

    /// <summary>Four clients read at the same time; every read must answer, quickly, and the clients must agree.</summary>
    private static async Task ParallelReadsAsync(CheckContext ctx)
    {
        var sessions = new List<ITelescopeSession>();
        try
        {
            for (var i = 0; i < 4; i++)
            {
                var s = await ctx.OpenSession!();
                sessions.Add(s);
                await ConnectAsync(s);
            }
            var errors = new List<string>();
            var times = new List<double>();
            var ras = new List<double>();
            await Task.WhenAll(sessions.Select(async s =>
            {
                for (var n = 0; n < 10; n++)
                {
                    var watch = Stopwatch.StartNew();
                    try
                    {
                        var (ra, _, _, _) = await s.RunAsync(t => (t.RightAscension, t.Declination, t.Tracking, t.Slewing), Timeout);
                        lock (times) { times.Add(watch.Elapsed.TotalMilliseconds); ras.Add(ra); }
                    }
                    catch (Exception ex)
                    {
                        lock (errors) errors.Add(AscomError.Describe(ex));
                    }
                }
            }));
            var slowest = times.Count > 0 ? times.Max() : 0;
            var spread = ras.Count > 1 ? Math.Abs(Values.HoursDiff(ras.Max(), ras.Min())) * 3600 : 0;
            var outcome = errors.Count > 0 ? Outcome.Fail : slowest > 2000 || spread > 60 ? Outcome.Warning : Outcome.Pass;
            ctx.Report("ParallelReads", "RightAscension", outcome,
                errors.Count > 0 ? $"{errors.Count} of 40 parallel reads failed."
                    : $"40 parallel reads (4 clients) all answered; slowest {slowest:0} ms, RA spread {spread:0.0} s.",
                expected: "every read answers, within 2 s, with consistent values",
                actual: errors.Count > 0 ? string.Join("; ", errors.Distinct().Take(3)) : $"slowest {slowest:0} ms, average {times.Average():0} ms",
                fix: errors.Count > 0
                    ? "Reads from several clients at once must all succeed: protect shared state with a lock, and don't let one " +
                      "client's serial command interleave with another's."
                    : "Parallel reads are slow or disagree: look for a lock held across a serial round trip.",
                sourceMember: "RightAscension");
        }
        catch (Exception ex)
        {
            ctx.Report("ParallelReads", "RightAscension", Outcome.Error, $"Couldn't open four clients: {AscomError.Describe(ex)}");
        }
        finally
        {
            foreach (var s in sessions)
            {
                await Quietly(() => DisconnectAsync(s));
                await s.DisposeAsync();
            }
        }
    }

    /// <summary>Ten quick connect/disconnect cycles from one more client: no errors, Connected true each time.</summary>
    private static async Task ConnectCyclesAsync(CheckContext ctx)
    {
        ITelescopeSession? s = null;
        var problems = new List<string>();
        var watch = Stopwatch.StartNew();
        try
        {
            s = await ctx.OpenSession!();
            for (var i = 1; i <= 10; i++)
            {
                try
                {
                    await ConnectAsync(s);
                    if (!await s.RunAsync(t => t.Connected, Timeout)) problems.Add($"cycle {i}: Connected false after Connect");
                    await DisconnectAsync(s);
                }
                catch (Exception ex)
                {
                    problems.Add($"cycle {i}: {AscomError.Describe(ex)}");
                }
            }
            // The run's own client must have been unaffected.
            var main = await ctx.TryAsync(t => t.Connected);
            if (main is not { Ok: true, Value: true }) problems.Add("the run's own client was disconnected by the cycles");
        }
        catch (Exception ex)
        {
            problems.Add(AscomError.Describe(ex));
        }
        finally
        {
            if (s is not null) await s.DisposeAsync();
        }
        ctx.Report("ConnectCycles", "Connect", problems.Count == 0 ? Outcome.Pass : Outcome.Fail,
            problems.Count == 0 ? $"10 connect/disconnect cycles worked ({watch.Elapsed.TotalSeconds:0.0} s)." : $"Connect/disconnect cycles had {problems.Count} problem(s).",
            actual: problems.Count == 0 ? null : string.Join("; ", problems.Take(4)),
            fix: "Connecting and disconnecting repeatedly must work every time and must not affect other clients. Check that " +
                 "Disconnect releases everything Connect set up (ports, timers, threads) and that the client count stays right.",
            sourceMember: "Connect");
    }

    private static async Task<(T Value, Exception? Error)> Try<T>(Func<Task<T>> call)
    {
        try { return (await call(), null); }
        catch (Exception ex) { return (default!, AscomError.Unwrap(ex)); }
    }

    private static async Task Quietly(Func<Task> call)
    {
        try { await call(); }
        catch { /* tidying up */ }
    }
}
