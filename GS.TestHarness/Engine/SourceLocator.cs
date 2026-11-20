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

/// <summary>One line of a codebase's source: where it is (file relative to the codebase's folder, 1-based line) and its text.</summary>
public sealed record SourceLine(string File, int Line, string Text)
{
    public string Location => $"{File}:{Line}";

    /// <summary>Whether the line is a comment (code commented out, or a note).</summary>
    public bool IsComment => Text.TrimStart().StartsWith("//", StringComparison.Ordinal);
}

/// <summary>Where a codebase's source is, from the "Codebases" configuration section.</summary>
public sealed class CodebaseOptions
{
    /// <summary>A default folder for the repository: a whole path (nothing is looked for beside this app); empty for none.</summary>
    public string Root { get; set; } = "";

    /// <summary>The telescope driver's source files (relative to <see cref="Root"/>), where interface members are.</summary>
    public List<string> TelescopeFiles { get; set; } = [];
}

/// <summary>
/// Finds the code a finding is about, in the source of the server under test, so a report can say where to make
/// the fix ("GS.Ascom.SkyTelescope/Telescope.cs:731"). Finds nothing when the source isn't on this computer.
/// Each codebase's folder is the one the person running the harness set (<see cref="SetFolder"/>, the Options tab's
/// Source code); left blank, there is none (no code review), unless appsettings.json's "Codebases" gives a whole path.
/// </summary>
public sealed class SourceLocator
{
    private readonly ConcurrentDictionary<Codebase, (string Root, string[] TelescopeFiles)> _codebases = new();
    /// <summary>Each file's lines, with the write time and size they were read at: an edited or re-downloaded file is read again.</summary>
    private readonly ConcurrentDictionary<string, (DateTime Written, long Size, string[] Lines)> _lines = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>The folder the person set for the codebase (null: none, so no code review).</summary>
    public string? FolderSetFor(Codebase codebase) => _set.TryGetValue(codebase, out var f) ? f : null;

    /// <summary>The configured default folder, as found on this computer (null if it isn't here).</summary>
    public string? DefaultRootOf(Codebase codebase)
        => _options.TryGetValue(codebase, out var o) ? ResolveRoot(o.Root, _searchFrom) : null;

    /// <summary>The driver files (relative to the folder) that show the folder is the codebase's source.</summary>
    public IReadOnlyList<string> DriverFilesOf(Codebase codebase)
        => _options.TryGetValue(codebase, out var o) ? o.TelescopeFiles : [];

    /// <summary>
    /// Uses this folder for the codebase from now on; null or empty means none (no code review). Returns why
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

    /// <summary>
    /// Why a folder (or a GitHub repository's URL, read from its downloaded copy) isn't usable as the codebase's
    /// source, or null when it is (it holds the driver file).
    /// </summary>
    public string? Check(Codebase codebase, string folder)
    {
        if (GitHubSource.Parse(folder) is { } repo)
        {
            if (repo.LocalFolder is not { } local) return $"{repo.Display} hasn't been downloaded from GitHub yet.";
            return HasDriver(codebase, local) ? null
                : $"{repo.Display} has no telescope driver (no class implementing ITelescopeV4 or ITelescopeV3): give the repository's URL, or /tree/branch/folder for the folder with the server's code.";
        }
        if (GitHubSource.LooksLikeUrl(folder)) return "Only GitHub URLs can be used (https://github.com/owner/repo, optionally /tree/branch).";
        if (!Path.IsPathRooted(folder)) return "Give the whole path, starting with the drive (C:\\...), or a GitHub URL.";
        if (!Directory.Exists(folder)) return "There's no such folder on this computer.";
        return HasDriver(codebase, folder) ? null
            : "It has no telescope driver (no class implementing ITelescopeV4 or ITelescopeV3): pick the repository's top folder of the server's code.";
    }

    /// <summary>A class declaration implementing the telescope interface: how any server's driver is found.</summary>
    private static readonly Regex DriverClass = new(@"\bclass\s+\w+[^{;]*:[^{;]*\bITelescopeV[234]\b");

    /// <summary>The source files left out of the search: build output, packages, tests.</summary>
    private static readonly Regex Skipped = new(@"[\\/](obj|bin|packages|\.git|\.vs)[\\/]|Tests?[\\/]", RegexOptions.IgnoreCase);

    /// <summary>
    /// A file for another kind of ASCOM device (in a repository of several simulators, say): its path names one and
    /// not the telescope. The code review sticks to the telescope's code and the code every device shares.
    /// </summary>
    private static readonly Regex OtherDevice = new(@"camera|covercalibrator|dome|filterwheel|focuser|observingconditions|rotator|safetymonitor|switch|video", RegexOptions.IgnoreCase);

    private static readonly Regex TelescopeDevice = new(@"telescope", RegexOptions.IgnoreCase);

    /// <summary>Whether a file (its path within the repository) is another device's code, not the telescope's or shared.</summary>
    internal static bool IsOtherDevice(string relativePath) => OtherDevice.IsMatch(relativePath) && !TelescopeDevice.IsMatch(relativePath);

    /// <summary>Whether the folder holds a telescope driver: the codebase's usual driver file, or (any server) a class implementing the interface.</summary>
    private bool HasDriver(Codebase codebase, string folder)
        => DriverFilesOf(codebase).Any(f => File.Exists(Path.Combine(folder, f))) || DriversIn(folder).Length > 0;

    /// <summary>The files in <paramref name="folder"/> with a class implementing ITelescopeV4 (or V3, V2), at most a few.</summary>
    private static string[] DriversIn(string folder)
    {
        try
        {
            return [.. Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
                .Where(f => !Skipped.IsMatch(f) && !IsOtherDevice(Path.GetRelativePath(folder, f)))
                .Where(f => File.ReadLines(f).Any(DriverClass.IsMatch))
                .Take(4)];
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    /// <summary>Works out the codebase's folder (the one set, else the default) and its driver files.</summary>
    private void Resolve(Codebase codebase)
    {
        _allFiles.TryRemove(codebase, out _);
        var root = _set.TryGetValue(codebase, out var set)
            ? GitHubSource.Parse(set) is { } repo ? repo.LocalFolder : Directory.Exists(set) ? Path.GetFullPath(set) : null
            : DefaultRootOf(codebase);
        if (root is null)
        {
            _codebases.TryRemove(codebase, out _);
            return;
        }
        // The codebase's usual driver file; else (another server's code) the files with a class implementing the interface.
        string[] drivers = [.. DriverFilesOf(codebase).Select(f => Path.Combine(root, f)).Where(File.Exists)];
        _codebases[codebase] = (root, drivers.Length > 0 ? drivers : DriversIn(root));
    }

    private static string? ResolveRoot(string root, string searchFrom)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        // Only a whole path: nothing is looked for beside this program, so a blank Source Code field means no code review.
        return Path.IsPathRooted(root) && Directory.Exists(root) ? Path.GetFullPath(root) : null;
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

    /// <summary>
    /// The code of an interface member in the codebase's telescope driver: from its definition to its closing brace
    /// (a property's getter and setter both). Null if the source isn't here or the member isn't found. For a
    /// method defined more than once (overloads), the first.
    /// </summary>
    public IReadOnlyList<SourceLine>? Body(Codebase codebase, string member)
    {
        if (!_codebases.TryGetValue(codebase, out var c) || !Regex.IsMatch(member, @"^\w+$")) return null;
        var definition = new Regex($@"^\s*public\s+(?:override\s+|virtual\s+|static\s+)*[\w<>\[\],.?\s]+?\s+{member}\s*(?:\(|$|\{{|=>)");
        foreach (var file in c.TelescopeFiles)
            if (BodyIn(c.Root, file, definition) is { } body) return body;
        return null;
    }

    /// <summary>
    /// The code of a method of any accessibility, anywhere in the codebase's source (the files of
    /// <paramref name="near"/> first, then the driver files, then the rest): from its definition to its closing brace.
    /// Null if the source isn't here or the method isn't found.
    /// </summary>
    public IReadOnlyList<SourceLine>? MethodBody(Codebase codebase, string method, string? near = null)
    {
        if (!_codebases.TryGetValue(codebase, out var c) || !Regex.IsMatch(method, @"^\w+$")) return null;
        var definition = new Regex($@"^\s*(?:public|private|internal|protected)\s+(?:static\s+|async\s+|override\s+|virtual\s+|sealed\s+)*[\w<>\[\],.?\s]+?\s+{method}\s*\(");
        var files = AllFiles(codebase, c.Root);
        var first = near is null ? [] : files.Where(f => Relative(c.Root, f).Equals(near, StringComparison.OrdinalIgnoreCase));
        foreach (var file in first.Concat(c.TelescopeFiles).Concat(files).DistinctBy(Path.GetFullPath, StringComparer.OrdinalIgnoreCase))
            if (BodyIn(c.Root, file, definition) is { } body) return body;
        return null;
    }

    /// <summary>From the first line matching <paramref name="definition"/> to the end of its block (or its semicolon).</summary>
    private List<SourceLine>? BodyIn(string root, string file, Regex definition)
    {
        var lines = Lines(file);
        var start = Array.FindIndex(lines, l => definition.IsMatch(l));
        if (start < 0) return null;
        var body = new List<SourceLine>();
        var depth = 0;
        var opened = false;
        for (var i = start; i < lines.Length; i++)
        {
            body.Add(new SourceLine(Relative(root, file), i + 1, lines[i]));
            var code = StripComment(lines[i]);
            depth += code.Count(ch => ch == '{') - code.Count(ch => ch == '}');
            opened |= code.Contains('{');
            // An expression-bodied member ends at its semicolon; a block at its closing brace.
            if (opened ? depth <= 0 : code.TrimEnd().EndsWith(';')) break;
        }
        return body;
    }

    /// <summary>
    /// Every line in the codebase's source (the driver files first) that matches <paramref name="pattern"/>, up to
    /// <paramref name="max"/>. Empty if the source isn't here.
    /// </summary>
    public IReadOnlyList<SourceLine> Search(Codebase codebase, string pattern, int max = 20)
    {
        if (!_codebases.TryGetValue(codebase, out var c)) return [];
        var regex = new Regex(pattern);
        var found = new List<SourceLine>();
        // The same file can be named with either slash (the configured driver file, and the folder listing).
        foreach (var file in c.TelescopeFiles.Concat(AllFiles(codebase, c.Root)).DistinctBy(Path.GetFullPath, StringComparer.OrdinalIgnoreCase))
        {
            var lines = Lines(file);
            for (var i = 0; i < lines.Length && found.Count < max; i++)
                if (regex.IsMatch(lines[i])) found.Add(new SourceLine(Relative(c.Root, file), i + 1, lines[i]));
            if (found.Count >= max) break;
        }
        return found;
    }

    /// <summary>A line without its // comment (a rough cut: enough to count braces and find calls).</summary>
    public static string StripComment(string line)
    {
        var i = line.IndexOf("//", StringComparison.Ordinal);
        return i < 0 ? line : line[..i];
    }

    private string[] Lines(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists) return [];
            var (written, size) = (info.LastWriteTimeUtc, info.Length);
            if (_lines.TryGetValue(file, out var cached) && cached.Written == written && cached.Size == size) return cached.Lines;
            var lines = File.ReadAllLines(file);
            _lines[file] = (written, size, lines);
            return lines;
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    private string[] AllFiles(Codebase codebase, string root)
        => _allFiles.GetOrAdd(codebase, _ =>
        {
            try
            {
                return [.. Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                    .Where(f => !Skipped.IsMatch(f) && !IsOtherDevice(Path.GetRelativePath(root, f)))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)];
            }
            catch (IOException) { return []; }
            catch (UnauthorizedAccessException) { return []; }
        });

    private string? FindIn(string root, string file, Regex regex)
    {
        var lines = Lines(file);
        for (var i = 0; i < lines.Length; i++)
            if (regex.IsMatch(lines[i]))
                return $"{Path.GetRelativePath(root, file).Replace('\\', '/')}:{i + 1}";
        return null;
    }
}
