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
using GS.TestHarness.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GS.TestHarness.Tests;

public class HomeSessionTests
{
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(5);

    /// <summary>A mount another client (the user's own app) keeps connected, behaving as GS Server's COM driver does.</summary>
    private static async Task<FakeTelescope> ConnectedByAnotherClientAsync()
    {
        var fake = new FakeTelescope(Defect.ConnectedSharedByClients, Defect.RefusesUnconnectedClient);
        await new DirectSession(fake, 100).RunAsync(t => t.Connect(), Short);
        return fake;
    }

    [Fact]
    public async Task The_fake_refuses_a_client_that_reads_Connected_true_but_never_connected()
    {
        var fake = await ConnectedByAnotherClientAsync();
        var fresh = new DirectSession(fake, 101);
        Assert.True(await fresh.RunAsync(t => t.Connected, Short));
        await Assert.ThrowsAnyAsync<Exception>(() => fresh.RunAsync(t => t.AtHome, Short));
    }

    [Fact]
    public async Task The_home_check_connects_its_own_client_when_the_driver_refuses_it_although_Connected_reads_true()
    {
        var fake = await ConnectedByAnotherClientAsync();
        var clients = 100;
        await using var home = new HomeSession(NullLogger.Instance, new TestTarget(), new RunOptions { CallTimeoutSeconds = 5 },
            () => Task.FromResult<ITelescopeSession>(new DirectSession(fake, Interlocked.Increment(ref clients))));

        var status = await home.CheckAsync();

        Assert.Null(status.Error);
        Assert.True(status.AtHome); // the fake mount starts at home
    }

    [Fact]
    public async Task Going_home_works_the_same_way()
    {
        var fake = await ConnectedByAnotherClientAsync();
        var clients = 100;
        await using var home = new HomeSession(NullLogger.Instance, new TestTarget(), new RunOptions { CallTimeoutSeconds = 5, SlewTimeoutSeconds = 20 },
            () => Task.FromResult<ITelescopeSession>(new DirectSession(fake, Interlocked.Increment(ref clients))));

        var status = await home.GoHomeAsync();

        Assert.Null(status.Error);
        Assert.Null(status.Problem);
        Assert.True(status.AtHome);
    }
}
