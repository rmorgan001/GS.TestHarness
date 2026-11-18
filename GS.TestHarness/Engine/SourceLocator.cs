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

using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace GS.TestHarness.Engine;

/// <summary>Where a codebase's source is, from the "Codebases" configuration section.</summary>
public sealed class CodebaseOptions
{
    /// <summary>The repository folder: absolute, or a folder name looked for beside this app's folders.</summary>
    public string Root { get; set; } = "";

    /// <summary>The telescope driver's source files (relative to <see cref="Root"/>), where interface members are.</summary>
    public List<string> TelescopeFiles { get; set; } = [];
}

/// <summary>
/// Finds the code a finding is about, in the source of the server under test, so a report can say where to make
/// the fix ("GS.Ascom.SkyTelescope/Telescope.cs:731"). Finds nothing when the source isn't on this computer.
/// Each codebase's folder is the one the person running the harness set (<see cref="SetFolder"/>, the Options tab's
/// Source code), or else the configured default ("Codebases" in appsettings.json, looked for beside this app).
/// </summary>
public sealed class SourceLocator
{
    private readonly ConcurrentDictionary<Codebase, (string Root, string[] TelescopeFiles)> _codebases = new();
    private readonly ConcurrentDictionary<string, string[]> _lines = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Codebase, string[]> _allFiles = new();
    private readonly IReadOnlyDictionary<Codebase, CodebaseOptions> _options;
    private readonly string _searchFrom;
    private readonly ConcurrentDictionary<Codebase, string> _set = new();

    public SourceLocator(IReadOnlyDictionary<Codebase, CodebaseOptions> codebases, string searchFrom)
    {
        _options = codebases;
        _searchFrom = searchFrom;
        foreach (var codebase in codebases.Keys) Resolve(codebase);
    }

    /// <summary>A locator that finds nothing.</summary>
    public static SourceLocator None { get; } = new(new Dictionary<Codebase, CodebaseOptions>(), "");

    /// <summary>The folder or a file changed.</summary>
    public event Action? Changed;

    /// <summary>The codebase's folder on this computer, or null if it isn't here.</summary>
    public string? RootOf(Codebase codebase) => _codebases.TryGetValue(codebase, out var c) ? c.Root : null;

    /// <summary>The folder the person set for the codebase (null: the configured default is used).</summary>
    public string? FolderSetFor(Codebase codebase) => _set.TryGetValue(codebase, out var f) ? f : null;

    /// <summary>The configured default folder, as found on this computer (null if it isn't here).</summary>
    public string? DefaultRootOf(Codebase codebase)
        => _options.TryGetValue(codebase, out var o) ? ResolveRoot(o.Root, _searchFrom) : null;

    /// <summary>The driver files (relative to the folder) that show the folder is the codebase's source.</summary>
    public IReadOnlyList<string> DriverFilesOf(Codebase codebase)
        => _options.TryGetValue(codebase, out var o) ? o.TelescopeFiles : [];

    /// <summary>
    /// Uses this folder for the codebase from now on; null or empty goes back to the configured default. Returns why
    /// it can't be used, or null when it can.
    /// </summary>
    public string? SetFolder(Codebase codebase, string? folder)
    {
        string? problem = null;
        if (string.IsNullOrWhiteSpace(folder)) _set.TryRemove(codebase, out _);
        else
        {
            problem = Check(codebase, folder.Trim());
            _set[codebase] = folder.Trim();
        }
        Resolve(codebase);
        Changed?.Invoke();
        return problem;
    }

    /// <summary>Why a folder isn't usable as the codebase's source, or null when it is (it holds the driver file).</summary>
    public string? Check(Codebase codebase, string folder)
    {
        if (!Path.IsPathRooted(folder)) return "Give the whole path, starting with the drive (C:\\...).";
        if (!Directory.Exists(folder)) return "There's no such folder on this computer.";
        var files = DriverFilesOf(codebase);
        if (files.Count > 0 && !files.Any(f => File.Exists(Path.Combine(folder, f))))
            return $"It doesn't hold {string.Join(" or ", files)}: pick the repository's top folder.";
        return null;
    }

    /// <summary>Works out the codebase's folder (the one set, else the default) and its driver files.</summary>
    private void Resolve(Codebase codebase)
    {
        _allFiles.TryRemove(codebase, out _);
        var root = _set.TryGetValue(codebase, out var set)
            ? (Directory.Exists(set) ? Path.GetFullPath(set) : null)
            : DefaultRootOf(codebase);
        if (root is null)
        {
            _codebases.TryRemove(codebase, out _);
            return;
        }
        _codebases[codebase] = (root, [.. DriverFilesOf(codebase).Select(f => Path.Combine(root, f)).Where(File.Exists)]);
    }

    private static string? ResolveRoot(string root, string searchFrom)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        if (Path.IsPathRooted(root)) return Directory.Exists(root) ? Path.GetFullPath(root) : null;
        for (var dir = string.IsNullOrEmpty(searchFrom) ? null : new DirectoryInfo(searchFrom); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, root);
            if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    /// <summary>
    /// Where an interface member is defined in the codebase's telescope driver, or, failing that, the first
    /// match for any of <paramref name="patterns"/> (regular expressions) in its source. Null if not found.
    /// </summary>
    public string? Find(Codebase codebase, string? member, params string[] patterns)
    {
        if (!_codebases.TryGetValue(codebase, out var c)) return null;

        if (!string.IsNullOrEmpty(member) && Regex.IsMatch(member, @"^\w+$"))
        {
            var definition = new Regex($@"^\s*public\s+(?:override\s+|virtual\s+|static\s+)*[\w<>\[\],.?\s]+?\s+{member}\s*(?:\(|$|\{{|=>)");
            foreach (var file in c.TelescopeFiles)
                if (FindIn(c.Root, file, definition) is { } hit) return hit;
        }

        foreach (var pattern in patterns)
        {
            var regex = new Regex(pattern, RegexOptions.IgnoreCase);
            foreach (var file in c.TelescopeFiles)
                if (FindIn(c.Root, file, regex) is { } hit) return hit;
            foreach (var file in AllFiles(codebase, c.Root))
                if (FindIn(c.Root, file, regex) is { } hit) return hit;
        }
        return null;
    }

    private string[] AllFiles(Codebase codebase, string root)
        => _allFiles.GetOrAdd(codebase, _ =>
        {
            try
            {
                return [.. Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                    .Where(f => !Regex.IsMatch(f, @"[\\/](obj|bin|packages|\.git|\.vs)[\\/]|Tests?[\\/]", RegexOptions.IgnoreCase))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)];
            }
            catch (IOException) { return []; }
            catch (UnauthorizedAccessException) { return []; }
        });

    private string? FindIn(string root, string file, Regex regex)
    {
        var lines = _lines.GetOrAdd(file, f =>
        {
            try { return File.ReadAllLines(f); }
            catch (IOException) { return []; }
            catch (UnauthorizedAccessException) { return []; }
        });
        for (var i = 0; i < lines.Length; i++)
            if (regex.IsMatch(lines[i]))
                return $"{Path.GetRelativePath(root, file).Replace('\\', '/')}:{i + 1}";
        return null;
    }
}
