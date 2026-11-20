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

/// <summary>The top tabs: the conformance test (with Run, Reports and Recommendations under it),
/// the ConformU Debug Log Test and the options.</summary>
public enum RunPageTab
{
    /// <summary>A full compliance check.</summary>
    Conformance,

    /// <summary>Following up a ConformU log.</summary>
    ConformU,

    /// <summary>What a run may do, and how long it waits.</summary>
    Options
}

/// <summary>
/// The Run tab's state for the browser tab's session: the sub-tab shown and the ConformU log dropped on it, so both
/// are still there after looking at a report and coming back.
/// </summary>
public sealed class RunPageState
{
    /// <summary>The largest ConformU log accepted: a full run's debug log is a few MB; this leaves plenty of room.</summary>
    public const long MaxBytes = 200L * 1024 * 1024;

    /// <summary>The tab or the log changed: the layout's top tabs (and the ConformU count on them) follow it.</summary>
    public event Action? Changed;

    public RunPageTab Tab
    {
        get;
        set { if (field == value) return; field = value; Changed?.Invoke(); }
    } = RunPageTab.Conformance;

    public ConformULog? Log
    {
        get;
        set { field = value; Changed?.Invoke(); }
    }

    /// <summary>
    /// Where the ConformU Debug Log Test's own pages live: conformu/reports and conformu/recommendations. The address
    /// says which test's runs a page shows, so a reload, a reconnect or a link opened on its own can't mix them.
    /// </summary>
    public const string ConformUPrefix = "conformu/";

    /// <summary>Whether the page at this base-relative address is one of the ConformU Debug Log Test's own.</summary>
    public static bool IsConformUPage(string relativePath)
        => relativePath.StartsWith(ConformUPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The runs Reports and Recommendations show: each test's own. Under the ConformU Debug Log Test only ConformU
    /// runs (the ones that followed up a log); under the ASCOM Conformance Check only its runs.
    /// </summary>
    public static List<RunReport> Shown(IEnumerable<RunReport> runs, bool conformU)
        => [.. runs.Where(r => IsConformU(r) == conformU)];

    /// <summary>A ConformU Debug Log Test run (it followed up a ConformU log), not a full ASCOM Conformance Check.</summary>
    public static bool IsConformU(RunReport r) => r.ConformULog is not null;

    /// <summary>Why the last ConformU file couldn't be read, or null.</summary>
    public string? Error { get; set; }
}
