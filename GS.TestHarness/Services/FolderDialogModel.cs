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

namespace GS.TestHarness.Services;

/// <summary>
/// Picks a folder on the computer running the harness (a browser can't give a page a folder's path, so the server
/// lists the folders itself). Folders that hold any of <see cref="Marks"/> (a server's driver file) are marked, so the
/// repository's top folder stands out. <see cref="Selected"/> is the folder open when Select is pressed.
/// </summary>
public sealed class FolderDialogModel : DialogModel
{
    public FolderDialogModel(string title, string? start, IReadOnlyList<string> marks, string markText)
    {
        Title = title;
        Marks = marks;
        MarkText = markText;
        Open(start is { Length: > 0 } && Directory.Exists(start) ? start : StartFolder());
    }

    public string Title { get; }

    /// <summary>Files (relative to a folder) that mark the folder wanted.</summary>
    public IReadOnlyList<string> Marks { get; }

    /// <summary>What a marked folder is ("GSServer source").</summary>
    public string MarkText { get; }

    /// <summary>The folder open, or "" for the list of drives.</summary>
    public string Current { get; private set; } = "";

    /// <summary>What's in it: its folders, each marked when it holds a mark file.</summary>
    public IReadOnlyList<(string Name, string Path, bool Marked)> Folders { get; private set; } = [];

    /// <summary>Why the folder couldn't be listed, or null.</summary>
    public string? Error { get; private set; }

    /// <summary>Whether the open folder is itself the one wanted.</summary>
    public bool CurrentMarked => Current.Length > 0 && IsMarked(Current);

    public string? Selected => Current.Length > 0 ? Current : null;

    /// <summary>Places to start from: the drives, and the usual places for repositories.</summary>
    public IReadOnlyList<(string Label, string Path)> Places { get; } = BuildPlaces();

    public void Open(string path)
    {
        Error = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            Current = "";
            Folders = [.. DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => (d.Name.TrimEnd('\\', '/'), d.RootDirectory.FullName, false))];
            return;
        }
        path = path.Trim().Trim('"');
        if (!Path.IsPathRooted(path) || !Directory.Exists(path))
        {
            Error = $"There's no folder {path}.";
            return;
        }
        try
        {
            var dir = new DirectoryInfo(path);
            Folders = [.. dir.EnumerateDirectories()
                .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0 && !d.Name.StartsWith('.'))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => (d.Name, d.FullName, IsMarked(d.FullName)))];
            Current = dir.FullName;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Error = $"{path} can't be opened: {ex.Message}";
        }
    }

    /// <summary>Up one folder; from a drive's top, the list of drives.</summary>
    public void Up() => Open(Current.Length == 0 ? "" : Directory.GetParent(Current)?.FullName ?? "");

    private bool IsMarked(string folder)
    {
        try { return Marks.Any(m => File.Exists(Path.Combine(folder, m))); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return false; }
    }

    /// <summary>Where repositories usually are (source\repos), else the user's folder.</summary>
    private static string StartFolder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var repos = Path.Combine(home, "source", "repos");
        return Directory.Exists(repos) ? repos : home;
    }

    private static List<(string, string)> BuildPlaces()
    {
        var places = new List<(string, string)>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var (label, path) in new[]
                 {
                     ("source\\repos", Path.Combine(home, "source", "repos")),
                     ("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
                     ("Home", home)
                 })
            if (path.Length > 0 && Directory.Exists(path)) places.Add((label, path));
        places.Add(("This PC", ""));
        return places;
    }
}
