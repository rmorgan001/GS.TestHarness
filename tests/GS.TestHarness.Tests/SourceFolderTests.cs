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

namespace GS.TestHarness.Tests;

/// <summary>Each person's own source folder: checked, saved, used for "where to check the code", and cleared back to the default.</summary>
public sealed class SourceFolderTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "gs-source-" + Guid.NewGuid().ToString("N"));

    private static readonly Dictionary<Codebase, CodebaseOptions> Config = new()
    {
        [Codebase.GSServer] = new CodebaseOptions { Root = "NoSuchFolderBesideTheTests", TelescopeFiles = ["GS.Ascom.SkyTelescope/Telescope.cs"] }
    };

    /// <summary>A GSServer-like repository with its driver file defining PulseGuide on line 3.</summary>
    private string Repository()
    {
        var root = Path.Combine(_temp, "MyGSServer");
        Directory.CreateDirectory(Path.Combine(root, "GS.Ascom.SkyTelescope"));
        File.WriteAllLines(Path.Combine(root, "GS.Ascom.SkyTelescope", "Telescope.cs"),
            ["namespace X;", "class Telescope {", "    public void PulseGuide(int d, int ms) { }", "}"]);
        return root;
    }

    [Fact]
    public async Task A_folder_set_is_checked_saved_and_used()
    {
        var locator = new SourceLocator(Config, AppContext.BaseDirectory);
        var file = Path.Combine(_temp, "source-folders.json");
        var store = new SourceStore(locator, file, null);
        Assert.Null(locator.RootOf(Codebase.GSServer));

        Assert.Contains("whole path", await store.SetAsync(Codebase.GSServer, "relative\\folder"));
        Assert.Contains("no such folder", await store.SetAsync(Codebase.GSServer, Path.Combine(_temp, "missing")));
        Directory.CreateDirectory(Path.Combine(_temp, "empty"));
        Assert.Contains("no telescope driver", await store.SetAsync(Codebase.GSServer, Path.Combine(_temp, "empty")));

        var repo = Repository();
        Assert.Null(await store.SetAsync(Codebase.GSServer, $"\"{repo}\"")); // pasted with quotes, as Explorer's "Copy as path" gives it
        Assert.Equal(repo, locator.RootOf(Codebase.GSServer));
        Assert.Equal("GS.Ascom.SkyTelescope/Telescope.cs:3", locator.Find(Codebase.GSServer, "PulseGuide"));

        // Saved: a new start of the app uses it again.
        var again = new SourceLocator(Config, AppContext.BaseDirectory);
        _ = new SourceStore(again, file, null);
        Assert.Equal(repo, again.RootOf(Codebase.GSServer));

        // Cleared: back to the default (not found here).
        Assert.Null(await store.SetAsync(Codebase.GSServer, ""));
        Assert.Null(locator.RootOf(Codebase.GSServer));
        Assert.Equal("", store.FolderOf(Codebase.GSServer));
    }

    [Fact]
    public void The_folder_popup_marks_the_source_folder()
    {
        var repo = Repository();
        Directory.CreateDirectory(Path.Combine(_temp, "Other"));
        var dialog = new FolderDialogModel("GSServer source code", _temp, ["GS.Ascom.SkyTelescope/Telescope.cs"], "GSServer source");

        Assert.Equal(_temp, dialog.Current.TrimEnd(Path.DirectorySeparatorChar));
        Assert.Equal([("MyGSServer", true), ("Other", false)], dialog.Folders.Select(f => (f.Name, f.Marked)));
        Assert.False(dialog.CurrentMarked);

        dialog.Open(repo);
        Assert.True(dialog.CurrentMarked);
        Assert.Equal(repo, dialog.Selected);

        dialog.Up();
        Assert.Equal(_temp, dialog.Current.TrimEnd(Path.DirectorySeparatorChar));

        dialog.Open(Path.Combine(_temp, "missing"));
        Assert.NotNull(dialog.Error);
        Assert.Equal(_temp, dialog.Current.TrimEnd(Path.DirectorySeparatorChar)); // stays where it was
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { }
    }
}
