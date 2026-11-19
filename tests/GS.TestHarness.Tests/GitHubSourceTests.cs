/* Copyright(C) 2026  Rob Morgan

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

namespace GS.TestHarness.Tests;

/// <summary>A GitHub URL as the source code path: what it names, and the downloaded copy read as a folder (no network here).</summary>
public class GitHubSourceTests
{
    [Theory]
    [InlineData("https://github.com/owner/repo", "owner", "repo", null, null)]
    [InlineData("https://github.com/owner/repo.git", "owner", "repo", null, null)]
    [InlineData("github.com/owner/repo/", "owner", "repo", null, null)]
    [InlineData("https://github.com/owner/Green.Swamp-Alpaca/tree/develop", "owner", "Green.Swamp-Alpaca", "develop", null)]
    [InlineData("https://github.com/owner/repo/tree/main/src/Server", "owner", "repo", "main", "src/Server")]
    [InlineData("https://www.github.com/owner/repo/blob/v1.2/README.md", "owner", "repo", "v1.2", "README.md")]
    public void A_GitHub_URL_names_the_repository_branch_and_folder(string url, string owner, string repo, string? branch, string? folder)
        => Assert.Equal(new GitHubSource(owner, repo, branch, folder), GitHubSource.Parse(url));

    [Theory]
    [InlineData(@"C:\src\GSServer")]
    [InlineData("https://gitlab.com/owner/repo")]
    [InlineData("https://github.com/owner")]
    [InlineData("")]
    public void Anything_else_isnt_a_GitHub_repository(string text) => Assert.Null(GitHubSource.Parse(text));

    [Theory]
    [InlineData("Camera.Simulator/Camera.cs", true)]
    [InlineData("ASCOM.Alpaca.Razor/Controllers/CameraController.cs", true)]
    [InlineData("CoverCalibratorSimulator/CoverCalibratorSimulator.cs", true)]
    [InlineData("SwitchSimulator/Switch.cs", true)]
    [InlineData("TelescopeSimulator/Telescope.cs", false)]
    [InlineData("ASCOM.Alpaca.Razor/Controllers/TelescopeController.cs", false)]
    [InlineData("ASCOM.Alpaca.Razor/Controllers/AlpacaController.cs", false)] // shared by every device
    [InlineData("GS.Ascom.SkyTelescope/Telescope.cs", false)]
    [InlineData("GS.Server/SkyTelescope/SkyServer.cs", false)]
    public void The_code_review_sticks_to_the_telescopes_code(string path, bool otherDevice)
        => Assert.Equal(otherDevice, SourceLocator.IsOtherDevice(path));

    [Fact]
    public void Another_devices_code_isnt_searched()
    {
        var root = Path.Combine(Path.GetTempPath(), "gs-harness-devices-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (file, text) in new[]
                     {
                         ("Controllers/TelescopeController.cs", "[Route(\"{DeviceNumber}/pulseguide\")]\n"),
                         ("Controllers/CameraController.cs", "[Route(\"{DeviceNumber}/pulseguide\")]\n"),
                         ("TelescopeSimulator/Telescope.cs", "public class Telescope : ITelescopeV4\n{\n}\n")
                     })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, file))!);
                File.WriteAllText(Path.Combine(root, file), text);
            }
            var locator = new SourceLocator(new Dictionary<Codebase, CodebaseOptions>
            {
                [Codebase.GreenSwampAlpaca] = new() { Root = root, TelescopeFiles = [] }
            }, "");
            var found = locator.Search(Codebase.GreenSwampAlpaca, "pulseguide");
            Assert.Equal(["Controllers/TelescopeController.cs"], found.Select(l => l.File.Replace('\\', '/')));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Any_servers_code_is_found_by_its_telescope_driver_class()
    {
        var root = Path.Combine(Path.GetTempPath(), "gs-harness-any-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Laid out nothing like the codebase's usual driver file.
            Directory.CreateDirectory(Path.Combine(root, "src", "Devices"));
            File.WriteAllText(Path.Combine(root, "src", "Devices", "MountDevice.cs"),
                "public sealed class MountDevice : DeviceBase, ITelescopeV4, IDisposable\n{\n    public double SiteLatitude\n    {\n        get { return 0; }\n    }\n}\n");
            File.WriteAllText(Path.Combine(root, "src", "Other.cs"), "public class Other\n{\n}\n");
            var locator = new SourceLocator(new Dictionary<Codebase, CodebaseOptions>
            {
                [Codebase.GreenSwampAlpaca] = new() { Root = "", TelescopeFiles = ["GreenSwamp.Alpaca.Server/TelescopeDriver/Telescope.cs"] }
            }, "");

            Assert.Null(locator.SetFolder(Codebase.GreenSwampAlpaca, root));
            Assert.Equal("src/Devices/MountDevice.cs:3", locator.Find(Codebase.GreenSwampAlpaca, "SiteLatitude")?.Replace('\\', '/'));
            Assert.NotNull(locator.Body(Codebase.GreenSwampAlpaca, "SiteLatitude"));

            // A folder with no telescope driver at all.
            var docs = Directory.CreateDirectory(Path.Combine(root, "docs")).FullName;
            File.WriteAllText(Path.Combine(docs, "Notes.cs"), "public class Notes\n{\n}\n");
            Assert.Contains("no telescope driver", locator.Check(Codebase.GreenSwampAlpaca, docs));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void The_downloaded_copy_is_read_as_the_source_folder()
    {
        var cache = Path.Combine(Path.GetTempPath(), "gs-harness-github-" + Guid.NewGuid().ToString("N"));
        var saved = GitHubSource.CacheRoot;
        GitHubSource.CacheRoot = cache;
        try
        {
            const string url = "https://github.com/owner/repo/tree/main";
            var options = new Dictionary<Codebase, CodebaseOptions>
            {
                [Codebase.GreenSwampAlpaca] = new() { Root = "", TelescopeFiles = ["Driver/Telescope.cs"] }
            };
            var locator = new SourceLocator(options, "");

            // Not downloaded yet: nothing to read, and it says so.
            Assert.Contains("hasn't been downloaded", locator.SetFolder(Codebase.GreenSwampAlpaca, url));
            Assert.Null(locator.RootOf(Codebase.GreenSwampAlpaca));

            // As DownloadAsync leaves it: the repository under src, and the marker.
            var copy = Path.Combine(cache, "owner", "repo", "main");
            Directory.CreateDirectory(Path.Combine(copy, "src", "Driver"));
            File.WriteAllText(Path.Combine(copy, "src", "Driver", "Telescope.cs"), "public class Telescope\n{\n    public double SiteLatitude\n    {\n        get { return 0; }\n    }\n}\n");
            File.WriteAllText(Path.Combine(copy, ".gs-harness-download"), "test");

            Assert.Null(locator.SetFolder(Codebase.GreenSwampAlpaca, url));
            Assert.Equal(Path.Combine(copy, "src"), locator.RootOf(Codebase.GreenSwampAlpaca));
            Assert.NotNull(locator.Body(Codebase.GreenSwampAlpaca, "SiteLatitude"));

            // A folder in the repository that doesn't hold the driver.
            Directory.CreateDirectory(Path.Combine(copy, "src", "docs"));
            Assert.Contains("has no telescope driver", locator.Check(Codebase.GreenSwampAlpaca, url + "/docs"));

            // Another site's URL.
            Assert.Contains("Only GitHub URLs", locator.Check(Codebase.GreenSwampAlpaca, "https://gitlab.com/owner/repo"));
        }
        finally
        {
            GitHubSource.CacheRoot = saved;
            if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
        }
    }
}
