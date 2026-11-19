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

using System.Runtime.Versioning;
using Microsoft.Win32;

namespace GS.TestHarness.Engine.Checks;

/// <summary>
/// COM only: how the driver is registered, read from the registry (the driver isn't called): the ASCOM Profile
/// entry, the ProgID and its CLSID, and the server for 64-bit and 32-bit clients.
/// </summary>
public sealed class RegistrationChecks : ICheckGroup
{
    public string Name => "Registration";
    public string Description => "COM only, from the registry: the ASCOM Profile entry, ProgID → CLSID, and a LocalServer32 (or InprocServer32) for both 64-bit and 32-bit clients whose program exists.";

    public string? SkipReason(CheckContext ctx)
        => ctx.Target.Kind != TargetKind.Com ? $"{ctx.Target.Codebase} isn't a COM driver"
            : ctx.Session.Transport != "COM" ? "the device isn't reached through COM here"
            : !OperatingSystem.IsWindows() ? "the registry is only on Windows" : null;

    public Task RunAsync(CheckContext ctx)
    {
        if (OperatingSystem.IsWindows()) Check(ctx);
        return Task.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    private static void Check(CheckContext ctx)
    {
        var progId = ctx.Target.ProgId;
        const string reference = "https://ascom-standards.org/Help/Developer/html/T_ASCOM_Utilities_Profile.htm";

        // ASCOM Profile: what the Chooser and clients list.
        string? profileName = null;
        try
        {
            profileName = ASCOM.Com.Profile.GetDrivers(ASCOM.Common.DeviceTypes.Telescope)
                .FirstOrDefault(d => string.Equals(d.ProgID, progId, StringComparison.OrdinalIgnoreCase))?.Name;
        }
        catch (Exception ex)
        {
            ctx.Report("Profile", "ASCOM Profile", Outcome.Error, $"Couldn't read the ASCOM Profile: {ex.Message}");
        }
        ctx.Report("Profile", "ASCOM Profile", profileName is null ? Outcome.Fail : string.IsNullOrWhiteSpace(profileName) ? Outcome.Warning : Outcome.Pass,
            profileName is null ? $"{progId} isn't registered in the ASCOM Profile as a telescope driver."
                : string.IsNullOrWhiteSpace(profileName) ? $"{progId} is in the ASCOM Profile but has no name." : $"ASCOM Profile: \"{profileName}\" ({progId}).",
            fix: "Register the driver with the ASCOM Profile (Profile.Register plus a description) in the installer or the " +
                 "server's /register switch, so the Chooser and clients list it.",
            reference: reference, sourcePatterns: ["Profile.*Register", "RegisterASCOM"]);

        // ProgID -> CLSID.
        string? clsid = null;
        using (var key = Registry.ClassesRoot.OpenSubKey($@"{progId}\CLSID")) clsid = key?.GetValue(null) as string;
        ctx.Report("ProgId", "COM registration", clsid is null ? Outcome.Fail : Outcome.Pass,
            clsid is null ? $"The ProgID {progId} has no CLSID in the registry." : $"{progId} → {clsid}.",
            fix: "The ProgID key needs a CLSID subkey naming the driver's class, written when the COM server registers itself.",
            sourcePatterns: ["ProgId", "RegisterObjects"]);
        if (clsid is null) return;

        // The server, as each bitness of client sees it.
        var found = new List<string>();
        foreach (var (view, label) in new[] { (RegistryView.Registry64, "64-bit"), (RegistryView.Registry32, "32-bit") })
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            string? server = null, kind = null;
            foreach (var root in new[] { hkcu, hklm })
            foreach (var type in new[] { "LocalServer32", "InprocServer32" })
            {
                if (server is not null) break;
                using var k = root.OpenSubKey($@"Software\Classes\CLSID\{clsid}\{type}");
                if (k?.GetValue(null) is string s && s.Length > 0) (server, kind) = (s, type);
            }
            if (server is null)
            {
                ctx.Report($"Server.{label}", "COM registration", Outcome.Warning,
                    $"No server is registered for {label} clients.",
                    expected: "LocalServer32 (or InprocServer32) under the CLSID in the " + label + " registry view",
                    fix: $"{label} clients (some imaging programs are still 32-bit) won't be able to create the driver. Register " +
                         "the COM server in both registry views (the installer, or regasm/registration code for each bitness).",
                    sourcePatterns: ["LocalServer32", "RegisterObjects"]);
                continue;
            }
            found.Add(label);
            var exe = ExecutableOf(server);
            var exists = exe is null || File.Exists(exe);
            ctx.Report($"Server.{label}", "COM registration", exists ? Outcome.Pass : Outcome.Fail,
                exists ? $"{label} clients: {kind} {server}" : $"{label} clients: {kind} points at a program that isn't there.",
                actual: server,
                fix: "The registered server path is stale (the program was moved or uninstalled): re-register it from where it is installed now.",
                sourcePatterns: ["LocalServer32"]);
        }
    }

    /// <summary>The program in a LocalServer32 value: quoted, or up to ".exe" (arguments may follow).</summary>
    internal static string? ExecutableOf(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"')) return command.Split('"', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var i = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (i >= 0) return command[..(i + 4)];
        return command.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? command : null;
    }
}
