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

namespace GS.TestHarness.Tests;

/// <summary>Each server keeps its own Settings section in servers.json.</summary>
public class ServerSettingsTests
{
    [Fact]
    public void A_servers_settings_are_saved_with_it_and_read_back()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gs-harness-servers-" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = Path.Combine(folder, "servers.json");
            var list = new ServerList(file, null, NullLogger<ServerList>.Instance);
            var sim = new ServerEntry
            {
                Name = "ASCOM simulator", Target = new TestTarget { Codebase = Codebase.GreenSwampAlpaca, Host = "127.0.0.1", Port = 32323 },
                Settings = new ServerSettings
                {
                    SourcePath = "https://github.com/owner/repo/tree/main", Mount = MountKind.Simulator, FlipAngle = 10,
                    SlewTimeoutSeconds = 90, CallTimeoutSeconds = 8
                }
            };
            list.Add(sim);
            Assert.Null(list.Items.First(s => s.Name == "GSServer").Settings); // none until first used

            var again = new ServerList(file, null, NullLogger<ServerList>.Instance);
            Assert.Equal(sim.Settings, again.Find(sim.Id)!.Settings);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
