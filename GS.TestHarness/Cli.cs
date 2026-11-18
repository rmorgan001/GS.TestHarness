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

namespace GS.TestHarness;

/// <summary>
/// Runs the checks from the command line instead of starting the web app, for scripts and build pipelines:
/// <code>
/// GS.TestHarness --run GreenSwampAlpaca --host localhost --port 31416 --device 0 [--motion] [--setpark]
///                [--no-writes] [--no-protocol] [--no-disconnect] [--extended] [--drift-seconds 20]
///                [--sites server,north,south] [--mount simulator|real]
///                [--source C:\src\GSServer] [--md report.md]
/// GS.TestHarness --run GSServer [--progid ASCOM.GS.Sky.Telescope] [--motion] ...
/// </code>
/// The report is saved like a run from the web page. Exit code: 0 no failures, 1 failures or errors, 2 bad arguments.
/// </summary>
public static class Cli
{
    public static bool IsCli(string[] args) => args.Contains("--run", StringComparer.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string[] args, SourceLocator locator, ReportStore store, SiteStore sites)
    {
        string? Value(string name)
        {
            var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        bool Flag(string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);

        // The server decides the transport: GSServer is its COM driver, GreenSwampAlpaca its Alpaca server.
        if (!Enum.TryParse<Codebase>(Value("--run"), true, out var server))
        {
            Console.Error.WriteLine("--run must be followed by GSServer (COM) or GreenSwampAlpaca (Alpaca).");
            return 2;
        }

        // --source <folder>: the server's source for this run only (else the folder set in the app, else the default).
        if (Value("--source") is { Length: > 0 } source && locator.SetFolder(server, source) is { } problem)
            Console.Error.WriteLine($"--source {source}: {problem} Findings won't name a file.");

        var target = new TestTarget
        {
            Host = Value("--host") ?? "localhost",
            Port = int.TryParse(Value("--port"), out var port) ? port : 31426,
            DeviceNumber = int.TryParse(Value("--device"), out var device) ? device : 0,
            ProgId = Value("--progid") ?? "ASCOM.GS.Sky.Telescope",
            Codebase = server
        };
        var options = new RunOptions
        {
            AllowMotion = Flag("--motion"),
            AllowSetPark = Flag("--motion") && Flag("--setpark"),
            AllowSettingsWrites = !Flag("--no-writes"),
            IncludeProtocol = !Flag("--no-protocol"),
            IncludeDisconnected = !Flag("--no-disconnect"),
            Extended = Flag("--extended"),
            DriftSeconds = int.TryParse(Value("--drift-seconds"), out var ds) ? Math.Max(5, ds) : 20,
            SlewTimeoutSeconds = int.TryParse(Value("--slew-timeout"), out var st) ? st : 120,
            CallTimeoutSeconds = int.TryParse(Value("--call-timeout"), out var ct) ? ct : 15,
            FlipAngle = int.TryParse(Value("--flip-angle"), out var fa) ? Math.Clamp(fa, 0, 90) : 15,
            // --mount simulator|real: which GSServer code path the mount uses (for the diagnostics' recommendations).
            Mount = Value("--mount")?.ToLowerInvariant() switch
            {
                "simulator" or "sim" => MountKind.Simulator,
                "real" or "mount" => MountKind.RealMount,
                _ => MountKind.Unknown
            }
        };

        // --sites server,north,south (any of them, in that order): one run each; the north and south sites are the
        // ones saved on the Run page (sites.json).
        var siteList = new List<SiteLocation?>();
        foreach (var name in (Value("--sites") ?? "server").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (name.ToLowerInvariant())
            {
                case "server": siteList.Add(null); break;
                case "north": siteList.Add(sites.Current.North); break;
                case "south": siteList.Add(sites.Current.South); break;
                default:
                    Console.Error.WriteLine("--sites takes server, north and/or south, separated by commas.");
                    return 2;
            }
        }

        var worst = 0;
        foreach (var site in siteList)
        {
            var code = await RunOneAsync(target, options, site, locator, store, Value("--md") is { } md && siteList.Count > 1
                ? Path.ChangeExtension(md, null) + "-" + (site?.Hemisphere.ToLowerInvariant() ?? "server") + ".md" : Value("--md"));
            worst = Math.Max(worst, code);
        }
        return worst;
    }

    private static async Task<int> RunOneAsync(TestTarget target, RunOptions options, SiteLocation? site, SourceLocator locator,
        ReportStore store, string? markdownPath)
    {
        Console.WriteLine($"Checking {target.Display} at {site?.Label ?? "the server's own site"} (motion {(options.AllowMotion ? "on" : "off")})…");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        RunReport report;
        try
        {
            var previous = await store.LatestForAsync(target);
            await using var session = await TestRunner.OpenAsync(target, options);
            report = await new TestRunner(locator).RunAsync(session, target, options,
                r =>
                {
                    if (r.IsProblem) Console.WriteLine($"  {r.Outcome,-7} {r.Id}: {r.Message}");
                },
                p =>
                {
                    if (p.Group != "Done") Console.WriteLine($"[{p.GroupIndex + 1}/{p.GroupCount}] {p.Group}");
                },
                cts.Token, previous: previous, site: site);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Couldn't open {target.Display}: {AscomError.Describe(ex)}");
            return 1;
        }

        await store.SaveAsync(report);
        var markdown = ReportStore.ToMarkdown(report);
        if (markdownPath is { } md) await File.WriteAllTextAsync(md, markdown);

        Console.WriteLine();
        Console.WriteLine($"{report.Label}: {report.Count(Outcome.Fail)} failed, {report.Count(Outcome.Error)} errors, " +
                          $"{report.Count(Outcome.Warning)} warnings, {report.Count(Outcome.Pass)} passed.");
        Console.WriteLine($"Report: {Path.Combine(store.Folder, report.Id + ".json")}");
        return report.Count(Outcome.Fail) + report.Count(Outcome.Error) > 0 ? 1 : 0;
    }
}
