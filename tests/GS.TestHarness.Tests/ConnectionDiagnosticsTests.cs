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

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GS.TestHarness.Engine;
using GS.TestHarness.Engine.Checks;
using Xunit.Abstractions;

namespace GS.TestHarness.Tests;

/// <summary>
/// Tier 2 for R0011's (the ASCOM Alpaca Simulators 0.5.0) client and Alpaca server problems: one client's Disconnect
/// cutting off the others, and the server's request validation and setup pages, against a small HTTP server that
/// answers as that simulator does (or as it should).
/// </summary>
public class ConnectionDiagnosticsTests(ITestOutputHelper output)
{
    private static CheckResult Found(string id, string member, Outcome outcome = Outcome.Fail)
        => new() { Id = id, Group = id[..id.IndexOf('.')], Member = member, Outcome = outcome, Message = $"tier 1: {id}" };

    private static RunReport Tier1(TestTarget target) => new()
    {
        Target = target,
        Options = EngineTests.Everything,
        Identity = new DeviceIdentity { Name = "Alpaca Telescope Simulator", ServerDescription = "ASCOM Alpaca Simulators 0.5.0 (ASCOM Initiative)" },
        Results =
        [
            Found("Protocol.ClientTransactionID.Invalid", "ClientTransactionID", Outcome.Warning),
            Found("Protocol.Errors.InvalidValue", "ErrorNumber"),
            Found("Protocol.Setup.Server", "GET /setup", Outcome.Warning),
            Found("Protocol.Setup.Device", "GET /setup/v1/telescope/0/setup", Outcome.Warning),
            Found("Clients.OneDisconnects", "Disconnect"),
            Found("Clients.ConnectCycles", "Connect")
        ]
    };

    private async Task<RunReport> Tier2Async(FakeTelescope fake, bool asItShould)
    {
        using var server = new MiniAlpacaServer(asItShould);
        var target = new TestTarget { Codebase = Codebase.GreenSwampAlpaca, Host = "127.0.0.1", Port = server.Port };
        var tier1 = Tier1(target);
        var next = Tiers.Next(tier1);
        Assert.True(next is { Tier: 2 }, "tier 2 wasn't offered");
        Assert.Equal([AreaDiagnostics.AlpacaRequests, AreaDiagnostics.ClientConnections], next!.Members.Order());
        Assert.Empty(Tiers.OutlookFor(tier1).Uncovered);

        var runner = new TestRunner(SourceLocator.None);
        await using var session = new DirectSession(fake);
        var clients = 0;
        var report = await runner.RunAsync(session, target, Tiers.Options(EngineTests.Everything, next),
            openSession: () => Task.FromResult<ITelescopeSession>(new DirectSession(fake, Interlocked.Increment(ref clients))),
            followsUp: tier1);
        Assert.DoesNotContain(report.Results, r => TestCatalog.For(r.Id) is null);
        foreach (var r in report.Results.Where(Tiers.IsDiagnosis))
            output.WriteLine($"{r.Outcome,-8} {r.Number} {r.Id}: {r.Message}\n  {r.Actual?.Replace("\n", "\n  ")}\n  FIX: {r.Fix}\n");
        return report;
    }

    private static CheckResult Finding(RunReport report, string id)
    {
        var found = report.Results.Where(r => r.Id == id).ToList();
        Assert.True(found.Count == 1, $"{id}: {found.Count} findings");
        return found[0];
    }

    [Fact]
    public async Task Tier_2_takes_apart_the_ASCOM_simulators_client_and_server_problems()
    {
        var report = await Tier2Async(new FakeTelescope(Defect.ClientDisconnectDropsAll, Defect.NoConnectedCheck), asItShould: false);

        var clients = Finding(report, "Diagnose.Clients.Connections");
        Assert.Equal(Outcome.Warning, clients.Outcome);
        Assert.Contains("turns the hardware off for every client", clients.Message);
        Assert.Contains("others' calls still answer", clients.Message);
        Assert.Contains("ConnectCycles cut off this run's client for the same reason", clients.Actual);
        Assert.Contains("Count the clients", clients.Fix);

        var binding = Finding(report, "Diagnose.Alpaca.Binding");
        Assert.Equal(Outcome.Fail, binding.Outcome);
        Assert.Contains("bound as an enum", binding.Message);
        Assert.Contains("a number out of range (RA 25) does reach the driver", binding.Message);
        Assert.Contains("ClientTransactionID that isn't a uint32 (abc, -1, 4294967296, 1.5)", binding.Message);
        Assert.Contains("canmoveaxis Axis=3 (enum out of range): HTTP 400", binding.Actual);

        var setup = Finding(report, "Diagnose.Alpaca.Setup");
        Assert.Equal(Outcome.Fail, setup.Outcome);
        Assert.Contains("exist but throw", setup.Message);
        Assert.Contains("System.NullReferenceException", setup.Actual);
    }

    [Fact]
    public async Task Tier_2_finds_a_server_that_counts_clients_and_lets_values_through_right()
    {
        var report = await Tier2Async(new FakeTelescope(), asItShould: true);
        foreach (var id in new[] { "Diagnose.Clients.Connections", "Diagnose.Alpaca.Binding", "Diagnose.Alpaca.Setup" })
        {
            var r = Finding(report, id);
            Assert.True(r.Outcome == Outcome.Info, $"{id}: {r.Outcome}: {r.Message}");
        }
    }

    /// <summary>
    /// A tiny HTTP server answering the few Alpaca requests the diagnosis sends: as the ASCOM Alpaca Simulators 0.5.0
    /// do (enum and ClientTransactionID binding refusing with HTTP 400, setup pages throwing), or as they should.
    /// </summary>
    private sealed class MiniAlpacaServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly bool _right;

        public MiniAlpacaServer(bool right)
        {
            _right = right;
            _listener.Start();
            _ = Task.Run(AcceptAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[8192];
                var request = new StringBuilder();
                while (!request.ToString().Contains("\r\n\r\n"))
                {
                    var n = await stream.ReadAsync(buffer);
                    if (n == 0) return;
                    request.Append(Encoding.ASCII.GetString(buffer, 0, n));
                }
                var target = request.ToString().Split(' ')[1];
                var (status, type, body) = Answer(target);
                var bytes = Encoding.UTF8.GetBytes(body);
                var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : status == 400 ? "Bad Request" : "Internal Server Error")}\r\n" +
                           $"Content-Type: {type}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                await stream.WriteAsync(bytes);
            }
        }

        private (int Status, string Type, string Body) Answer(string target)
        {
            var path = target.Split('?')[0].TrimEnd('/');
            var query = (target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "").Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "", StringComparer.OrdinalIgnoreCase);
            string Q(string name) => query.GetValueOrDefault(name, "");
            const string Json = "application/json";
            static (int, string, string) Invalid(string path, string value)
                => (400, "text/plain", $"A data validation error occurred for request {path} with error message <The value '{value}' is invalid.>.");

            // ClientTransactionID: a uint32, or (as it should be) 0 when it isn't one.
            var txidText = Q("ClientTransactionID");
            var txidOk = txidText.Length == 0 || uint.TryParse(txidText, NumberStyles.None, CultureInfo.InvariantCulture, out _);
            if (!txidOk && !_right) return Invalid(path, txidText);
            var txid = txidOk && txidText.Length > 0 ? txidText : "0";
            string Reply(string value, int error = 0)
                => $"{{\"Value\":{value},\"ClientTransactionID\":{txid},\"ServerTransactionID\":1,\"ErrorNumber\":{error},\"ErrorMessage\":\"{(error == 0 ? "" : "invalid value")}\"}}";

            if (path.StartsWith("/setup", StringComparison.Ordinal))
                return _right ? (200, "text/html", "<html><body><h1>Setup</h1></body></html>")
                    : (500, "text/html", "<html><head><style>h1{}</style></head><body><h1>An unhandled exception occurred while processing the request.</h1>" +
                                         "<p>System.NullReferenceException: Object reference not set to an instance of an object.</p></body></html>");
            if (path.EndsWith("/canmoveaxis", StringComparison.Ordinal))
            {
                if (!int.TryParse(Q("Axis"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var axis)) return Invalid(path, Q("Axis"));
                if (axis is < 0 or > 2) return _right ? (200, Json, Reply("false", 0x401)) : Invalid(path, Q("Axis"));
                return (200, Json, Reply(axis < 2 ? "true" : "false"));
            }
            if (path.EndsWith("/destinationsideofpier", StringComparison.Ordinal))
            {
                if (!double.TryParse(Q("RightAscension"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ra)) return Invalid(path, Q("RightAscension"));
                return (200, Json, ra is < 0 or >= 24 && _right ? Reply("0", 0x401) : Reply("0"));
            }
            if (path.EndsWith("/name", StringComparison.Ordinal)) return (200, Json, Reply("\"Simulator\""));
            return (400, "text/plain", "unknown request");
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }
}
