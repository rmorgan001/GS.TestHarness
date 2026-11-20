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

/// <summary>Where the mount is, as far as the home check can tell. <see cref="Error"/> is set when it couldn't be read.</summary>
public sealed record HomeStatus(bool AtHome, bool AtPark, bool CanFindHome, string? Error = null)
{
    public static HomeStatus Failed(string error) => new(false, false, false, error);
}

/// <summary>
/// Checks, before a run that moves the mount, that it is at the home position, and can send it there (FindHome).
/// Each call opens its own short session and puts the connection back as it found it, so it works whether or not
/// the server is connected from the sidebar, and still works after the user has powered the mount off and on.
/// </summary>
public sealed class HomeService(ILogger<HomeService> log)
{
    /// <summary>Reads AtHome (and AtPark, CanFindHome).</summary>
    public Task<HomeStatus> CheckAsync(TestTarget target, RunOptions options)
        => WithMountAsync(target, options, async (session, timeout) => await ReadAsync(session, timeout));

    /// <summary>
    /// Sends the mount home: unparks it if parked, calls FindHome and waits (up to the slew timeout) for it to arrive.
    /// Returns the status afterwards.
    /// </summary>
    public Task<HomeStatus> GoHomeAsync(TestTarget target, RunOptions options)
        => WithMountAsync(target, options, async (session, timeout) =>
        {
            var slew = TimeSpan.FromSeconds(Math.Max(10, options.SlewTimeoutSeconds));
            var status = await ReadAsync(session, timeout);
            if (!status.CanFindHome) return status with { Error = "This mount can't find home itself (CanFindHome is false). Put it in the home position by hand." };
            if (status.AtPark)
            {
                await session.RunAsync(t => t.Unpark(), slew);
                await WaitAsync(session, t => !t.AtPark, slew);
            }
            await session.RunAsync(t => t.FindHome(), slew);
            await WaitAsync(session, t => t.AtHome && !t.Slewing, slew);
            return await ReadAsync(session, timeout);
        });

    private static async Task<HomeStatus> ReadAsync(ITelescopeSession session, TimeSpan timeout)
    {
        var atHome = await session.RunAsync(t => t.AtHome, timeout);
        var atPark = await session.RunAsync(t => t.AtPark, timeout);
        var canFindHome = await session.RunAsync(t => t.CanFindHome, timeout);
        return new HomeStatus(atHome, atPark, canFindHome);
    }

    private static async Task WaitAsync(ITelescopeSession session, Func<ASCOM.Common.DeviceInterfaces.ITelescopeV4, bool> done, TimeSpan limit)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < limit)
        {
            if (await session.RunAsync(done, TimeSpan.FromSeconds(15))) return;
            await Task.Delay(500);
        }
    }

    private async Task<HomeStatus> WithMountAsync(TestTarget target, RunOptions options,
        Func<ITelescopeSession, TimeSpan, Task<HomeStatus>> work)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(2, options.CallTimeoutSeconds));
        ITelescopeSession? session = null;
        var connectedHere = false;
        try
        {
            session = await TestRunner.OpenAsync(target, options);
            if (!await session.RunAsync(t => t.Connected, timeout))
            {
                await session.RunAsync(t =>
                {
                    try { t.Connect(); }
                    catch (ASCOM.NotImplementedException) { t.Connected = true; }
                }, timeout);
                connectedHere = true;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (watch.Elapsed < TimeSpan.FromSeconds(30) && await session.RunAsync(t => t.Connecting, timeout)) await Task.Delay(100);
                if (!await session.RunAsync(t => t.Connected, timeout)) return HomeStatus.Failed("The mount didn't connect.");
            }
            return await work(session, timeout);
        }
        catch (Exception ex)
        {
            log.LogInformation("Home check for {Target}: {Error}", target.Display, ex.Message);
            return HomeStatus.Failed($"Couldn't read the mount's position: {AscomError.Describe(ex)}");
        }
        finally
        {
            if (session is not null)
            {
                if (connectedHere)
                {
                    try
                    {
                        await session.RunAsync(t =>
                        {
                            try { t.Disconnect(); }
                            catch (ASCOM.NotImplementedException) { t.Connected = false; }
                        }, timeout);
                    }
                    catch { /* the run connects for itself */ }
                }
                await session.DisposeAsync();
            }
        }
    }
}
