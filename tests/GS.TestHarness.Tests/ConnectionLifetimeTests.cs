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
using GS.TestHarness.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GS.TestHarness.Tests;

public sealed class ConnectionLifetimeTests : IDisposable
{
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(5);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "GS.TestHarness.ConnectionTests", Guid.NewGuid().ToString("N"));

    private sealed class RecordingSession(ITelescopeSession inner, bool failDispose = false) : ITelescopeSession
    {
        public int Disposals { get; private set; }
        public string Transport => inner.Transport;
        public Task<T> RunAsync<T>(Func<ITelescopeV4, T> call, TimeSpan timeout) => inner.RunAsync(call, timeout);
        public async ValueTask DisposeAsync()
        {
            Disposals++;
            await inner.DisposeAsync();
            if (failDispose) throw new InvalidOperationException("Dispose failed");
        }
    }

    private (RunService Run, ConnectionService Connections, ServerEntry Server, ReportStore Store, List<RecordingSession> Sessions)
        CreateRun(FakeTelescope fake, bool failDispose = false, bool failOpen = false)
    {
        var store = new ReportStore(Path.Combine(_folder, "Reports"));
        var servers = new ServerList(Path.Combine(_folder, "servers.json"), null, NullLogger<ServerList>.Instance);
        var server = servers.Items[0];
        var connections = new ConnectionService(NullLogger<ConnectionService>.Instance);
        var sessions = new List<RecordingSession>();
        var clients = 0;
        var run = new RunService(new TestRunner(SourceLocator.None), store, new HomeService(NullLogger<HomeService>.Instance),
            new SuggestionStore(store), connections, servers, NullLogger<RunService>.Instance)
        {
            OpenSession = (_, _) =>
            {
                if (failOpen) throw new InvalidOperationException("Open failed");
                var session = new RecordingSession(new DirectSession(fake, ++clients), failDispose);
                sessions.Add(session);
                return Task.FromResult<ITelescopeSession>(session);
            }
        };
        return (run, connections, server, store, sessions);
    }

    private static async Task RunToCompletionAsync(RunService run, TestTarget target, RunOptions options, bool chain = true)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed()
        {
            if (!run.IsRunning && run.StartedAt is not null) finished.TrySetResult();
        }
        run.Changed += Changed;
        try
        {
            Assert.True(run.Start(target, options, chain: chain));
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            run.Changed -= Changed;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Default_completion_releases_the_client_even_without_disconnected_checks(bool motion)
    {
        var fake = new FakeTelescope();
        var (run, connections, server, store, sessions) = CreateRun(fake);
        await RunToCompletionAsync(run, server.Target, new RunOptions { Groups = ["Connection"], IncludeDisconnected = false, AllowMotion = motion });

        Assert.Equal(0, fake.ClientCount);
        Assert.Equal(1, Assert.Single(sessions).Disposals);
        Assert.False(connections.IsConnected(server.Id));
        Assert.False(run.IsConnectedTo(server.Target));
        Assert.Contains(run.Results, r => r is { Id: "Run.Connections", Outcome: Outcome.Info });
        var saved = await store.LoadAsync(Assert.IsType<RunReport>(run.LastReport).Id);
        Assert.Contains(Assert.IsType<RunReport>(saved).Results, r => r is { Id: "Run.Connections", Outcome: Outcome.Info });
    }

    [Fact]
    public async Task The_sidebar_client_is_released_before_testing_and_not_replaced_by_a_second_persistent_client()
    {
        var fake = new FakeTelescope();
        var (run, connections, server, _, sessions) = CreateRun(fake);
        var sidebar = new RecordingSession(new DirectSession(fake, 100));
        await sidebar.ConnectClientAsync(Short);
        connections.EndTest(server.Id, sidebar);
        var counts = new List<int>();
        run.Changed += () =>
        {
            if (run.IsConnectedTo(server.Target)) counts.Add(fake.ClientCount);
        };

        await RunToCompletionAsync(run, server.Target, new RunOptions { Groups = ["Connection"] });

        Assert.Equal(1, sidebar.Disposals);
        Assert.NotEmpty(counts);
        Assert.All(counts, count => Assert.Equal(1, count));
        Assert.Single(sessions);
        Assert.Equal(0, fake.ClientCount);
    }

    [Fact]
    public async Task Keeping_one_connection_hands_back_the_run_client_and_sidebar_disconnect_releases_it()
    {
        var fake = new FakeTelescope();
        var (run, connections, server, _, sessions) = CreateRun(fake);
        await RunToCompletionAsync(run, server.Target, new RunOptions { Groups = ["Connection", "Disconnected"], KeepConnection = true });

        Assert.Equal(1, fake.ClientCount);
        Assert.True(connections.IsConnected(server.Id));
        Assert.Equal(0, Assert.Single(sessions).Disposals);
        Assert.Contains(run.Results, r => r.Id == "Run.Connections" && r.Message.Contains("One connection kept"));
        Assert.False(await connections.DisconnectAsync(server.Id));
        Assert.Equal(0, fake.ClientCount);
        Assert.Equal(1, sessions[0].Disposals);
    }

    [Fact]
    public async Task Chained_tiers_use_one_main_client_and_release_it_only_at_the_end()
    {
        var fake = new FakeTelescope(Defect.DriverVersionFull);
        var (run, _, server, _, sessions) = CreateRun(fake);
        await RunToCompletionAsync(run, server.Target, new RunOptions { Groups = ["Connection"], AllowMotion = true });

        Assert.True(Assert.IsType<FinishedInfo>(run.Finished).Reports.Count >= 2);
        Assert.Equal(1, Assert.Single(sessions).Disposals);
        Assert.Equal(0, fake.ClientCount);
    }

    [Fact]
    public async Task Cancellation_releases_the_main_client_even_when_keep_was_selected()
    {
        var fake = new FakeTelescope();
        var (run, connections, server, _, sessions) = CreateRun(fake);
        var stopped = false;
        run.Changed += () =>
        {
            if (!stopped && run.IsRunning && run.Results.Any(r => r.Id == "Connection.Connect"))
            {
                stopped = true;
                run.Stop();
            }
        };
        await RunToCompletionAsync(run, server.Target, new RunOptions { Groups = ["Connection"], KeepConnection = true });

        Assert.True(Assert.IsType<RunReport>(run.LastReport).Cancelled);
        Assert.Equal(0, fake.ClientCount);
        Assert.Equal(1, Assert.Single(sessions).Disposals);
        Assert.False(connections.IsConnected(server.Id));
    }

    [Fact]
    public async Task Completion_does_not_release_another_applications_client()
    {
        var fake = new FakeTelescope(Defect.ConnectedSharedByClients, Defect.RefusesUnconnectedClient);
        var external = new DirectSession(fake, 100);
        await external.ConnectClientAsync(Short);
        var (run, _, server, _, _) = CreateRun(fake);
        await RunToCompletionAsync(run, server.Target, new RunOptions { Groups = ["Connection", "Disconnected"] });

        Assert.Equal(1, fake.ClientCount);
        Assert.True(await external.RunAsync(t => t.Connected, Short));
        Assert.Contains(run.Results, r => r.Id == "Run.Connections" && r.Message.Contains("hardware connected"));
        await external.ReleaseAsync(Short);
        Assert.Equal(0, fake.ClientCount);
    }

    [Fact]
    public async Task Cleanup_failure_is_visible_and_persisted_and_still_attempts_disposal()
    {
        var fake = new FakeTelescope(Defect.DisconnectThrows);
        var (run, _, server, store, sessions) = CreateRun(fake, failDispose: true);
        await RunToCompletionAsync(run, server.Target, new RunOptions { Groups = ["Connection"] });

        Assert.Equal(1, Assert.Single(sessions).Disposals);
        Assert.Contains("cleanup failed", run.Status);
        Assert.Contains(run.Results, r => r is { Id: "Run.Connections", Outcome: Outcome.Error });
        var saved = await store.LoadAsync(Assert.IsType<RunReport>(run.LastReport).Id);
        Assert.Contains(Assert.IsType<RunReport>(saved).Results, r => r is { Id: "Run.Connections", Outcome: Outcome.Error });
    }

    [Fact]
    public async Task A_driver_that_keeps_the_last_client_connected_is_reported_as_a_cleanup_failure()
    {
        var fake = new FakeTelescope(Defect.ConnectedAfterDisconnect);
        var (run, _, server, _, sessions) = CreateRun(fake);
        await RunToCompletionAsync(run, server.Target, new RunOptions { Groups = ["Connection"], IncludeDisconnected = false });
        Assert.Equal(1, fake.ClientCount);
        Assert.Equal(1, Assert.Single(sessions).Disposals);
        Assert.Contains(run.Results, r => r is { Id: "Run.Connections", Outcome: Outcome.Error });
        Assert.Contains("cleanup failed", run.Status);
    }

    [Fact]
    public async Task Auxiliary_clients_are_all_disposed_even_when_their_cleanup_fails()
    {
        var fake = new FakeTelescope();
        var main = new DirectSession(fake);
        var extras = new List<RecordingSession>();
        var clients = 0;
        var report = await new TestRunner(SourceLocator.None).RunAsync(main, new TestTarget(),
            new RunOptions { Groups = ["Connection", "Clients"], Extended = true }, openSession: () =>
            {
                var session = new RecordingSession(new DirectSession(fake, ++clients), failDispose: true);
                extras.Add(session);
                return Task.FromResult<ITelescopeSession>(session);
            });

        Assert.Equal(7, extras.Count);
        Assert.All(extras, s => Assert.Equal(1, s.Disposals));
        Assert.Equal(1, fake.ClientCount);
        Assert.Equal(7, report.Results.Count(r => r is { Id: "Clients.Cleanup", Outcome: Outcome.Error }));
        await main.ReleaseAsync(Short);
        Assert.Equal(0, fake.ClientCount);
    }

    [Fact]
    public async Task Sidebar_connect_is_rejected_while_its_server_is_reserved_for_testing()
    {
        var connections = new ConnectionService(NullLogger<ConnectionService>.Instance);
        var server = new ServerEntry { Name = "Test mount", Target = new TestTarget() };
        await connections.BeginTestAsync(server.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connections.ConnectAsync(server));
        connections.EndTest(server.Id);
        Assert.False(connections.IsBusy(server.Id));
    }

    [Fact]
    public async Task Opening_failure_releases_the_sidebar_reservation_and_reports_the_error()
    {
        var fake = new FakeTelescope();
        var (run, connections, server, _, sessions) = CreateRun(fake, failOpen: true);
        await RunToCompletionAsync(run, server.Target, new RunOptions { Groups = ["Connection"] });
        Assert.Empty(sessions);
        Assert.Equal(0, fake.ClientCount);
        Assert.Contains(run.Results, r => r is { Id: "Run.Stopped", Outcome: Outcome.Error });
        await connections.BeginTestAsync(server.Id);
        connections.EndTest(server.Id);
    }

    [Fact]
    public async Task Sidebar_disposal_failure_does_not_leave_the_server_busy()
    {
        var fake = new FakeTelescope();
        var session = new RecordingSession(new DirectSession(fake), failDispose: true);
        await session.ConnectClientAsync(Short);
        var connections = new ConnectionService(NullLogger<ConnectionService>.Instance);
        connections.EndTest("mount", session);
        await Assert.ThrowsAsync<AggregateException>(() => connections.DisconnectAsync("mount"));
        Assert.Equal(0, fake.ClientCount);
        Assert.False(connections.IsBusy("mount"));
        Assert.False(connections.IsConnected("mount"));
    }

    [Fact]
    public async Task Interrupted_testing_does_not_hand_back_a_connection()
    {
        var fake = new FakeTelescope(Defect.InterfaceVersion2);
        var (run, connections, server, _, sessions) = CreateRun(fake);
        await RunToCompletionAsync(run, server.Target, new RunOptions { Groups = ["Connection"], KeepConnection = true });
        Assert.Contains(run.Results, r => r.Id.EndsWith(".Stopped", StringComparison.Ordinal));
        Assert.Equal(0, fake.ClientCount);
        Assert.False(connections.IsConnected(server.Id));
        Assert.Equal(1, Assert.Single(sessions).Disposals);
    }

    [Fact]
    public async Task Release_reports_disconnect_and_dispose_errors_together()
    {
        var session = new RecordingSession(new DirectSession(new FakeTelescope(Defect.DisconnectThrows)), failDispose: true);
        var error = await Assert.ThrowsAsync<AggregateException>(() => session.ReleaseAsync(Short));
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Equal(1, session.Disposals);
    }

    [Fact]
    public async Task A_stuck_disconnection_reports_a_timeout_and_still_disposes()
    {
        var session = new RecordingSession(new DirectSession(new FakeTelescope(Defect.ConnectingNeverFinishes)));
        var error = await Assert.ThrowsAsync<AggregateException>(() => session.ReleaseAsync(TimeSpan.FromMilliseconds(100)));
        Assert.Contains(error.InnerExceptions, e => e is TimeoutException);
        Assert.Equal(1, session.Disposals);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }
}
