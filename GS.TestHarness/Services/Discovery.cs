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

using System.Text.Json;
using ASCOM.Alpaca.Discovery;

namespace GS.TestHarness.Services;

/// <summary>An Alpaca telescope found on the network.</summary>
public sealed record FoundTelescope(string Name, string Host, int Port, int DeviceNumber);

/// <summary>Finds Alpaca servers (UDP discovery) and the telescopes they serve.</summary>
public static class Discovery
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static async Task<List<FoundTelescope>> FindTelescopesAsync()
    {
        using var discovery = new AlpacaDiscovery();
        discovery.StartDiscovery(numberOfPolls: 2, pollInterval: 100, discoveryPort: 32227, discoveryDuration: 2.0,
            resolveDnsName: false, useIpV4: true, useIpV6: false);
        while (!discovery.DiscoveryComplete) await Task.Delay(50);

        var perServer = await Task.WhenAll(discovery.GetAlpacaDevices().Select(async server =>
        {
            var found = new List<FoundTelescope>();
            try
            {
                var json = await Http.GetStringAsync($"http://{server.HostName}:{server.Port}/management/v1/configureddevices");
                using var doc = JsonDocument.Parse(json);
                foreach (var device in doc.RootElement.GetProperty("Value").EnumerateArray())
                {
                    if (!string.Equals(device.GetProperty("DeviceType").GetString(), "Telescope", StringComparison.OrdinalIgnoreCase)) continue;
                    found.Add(new FoundTelescope(device.GetProperty("DeviceName").GetString() ?? "Telescope",
                        server.HostName, server.Port, device.GetProperty("DeviceNumber").GetInt32()));
                }
            }
            catch
            {
                // a server that doesn't answer is left out
            }
            return found;
        }));
        return [.. perServer.SelectMany(f => f).DistinctBy(f => (f.Host, f.Port, f.DeviceNumber))];
    }
}
