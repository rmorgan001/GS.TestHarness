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

namespace GS.TestHarness.Engine;

/// <summary>
/// What changed since the last run of the same device: the problems that are new, and the ones that are fixed (the check
/// ran again and no longer finds it). Findings are matched by their ID, which is stable across runs.
/// </summary>
public sealed record RunComparison(IReadOnlyList<CheckResult> New, IReadOnlyList<CheckResult> Fixed, int Unchanged)
{
    public static RunComparison Compare(IEnumerable<CheckResult> current, IEnumerable<CheckResult> previous)
    {
        var now = current.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.ToList());
        var before = previous.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.ToList());

        bool WasProblem(string id) => before.TryGetValue(id, out var was) && was.Any(r => r.IsProblem);
        bool IsProblem(string id) => now.TryGetValue(id, out var is_) && is_.Any(r => r.IsProblem);

        var added = now.Where(p => IsProblem(p.Key) && !WasProblem(p.Key))
            .Select(p => p.Value.First(r => r.IsProblem)).ToList();
        // Fixed only if the check ran this time: a problem whose check didn't run again isn't fixed, just not looked at.
        var gone = before.Where(p => WasProblem(p.Key) && now.ContainsKey(p.Key) && !IsProblem(p.Key))
            .Select(p => p.Value.First(r => r.IsProblem)).ToList();
        var same = now.Keys.Count(id => IsProblem(id) && WasProblem(id));
        return new RunComparison(added, gone, same);
    }

    /// <summary>Nothing new, nothing fixed, nothing carried over.</summary>
    public bool IsEmpty => New.Count == 0 && Fixed.Count == 0 && Unchanged == 0;
}
