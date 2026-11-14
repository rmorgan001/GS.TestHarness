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

namespace GS.TestHarness.Engine.Checks;

/// <summary>
/// A summary of the run so far: how quickly the properties answered, and any call that hung (never answered).
/// Reads nothing from the mount.
/// </summary>
public sealed class PerformanceChecks : ICheckGroup
{
    public string Name => "Performance";
    public string Description => "From the run so far: property read times (slow reads flagged) and any call that never answered.";
    public string? SkipReason(CheckContext ctx) => null;

    /// <summary>A property read slower than this is flagged: clients poll properties several times a second.</summary>
    public const double SlowReadMs = 1000;

    public Task RunAsync(CheckContext ctx)
    {
        var results = ctx.Results;

        var reads = results.Where(r => r.Id.EndsWith(".Read", StringComparison.Ordinal) && r.DurationMs > 0
                                       && r.Group is "Properties" or "Capabilities" or "Connection" or "Common").ToList();
        if (reads.Count > 0)
        {
            var times = reads.Select(r => r.DurationMs).Order().ToList();
            var median = times[times.Count / 2];
            var p95 = times[Math.Min(times.Count - 1, (int)Math.Ceiling(times.Count * 0.95) - 1)];
            var slow = reads.Where(r => r.DurationMs > SlowReadMs).OrderByDescending(r => r.DurationMs).ToList();
            ctx.Report("ReadTimes", "property reads", slow.Count == 0 ? Outcome.Pass : Outcome.Warning,
                slow.Count == 0
                    ? $"{reads.Count} property reads: median {median:0} ms, 95% within {p95:0} ms, slowest {times[^1]:0} ms."
                    : $"{slow.Count} of {reads.Count} property reads took over {SlowReadMs:0} ms.",
                expected: $"every property read within {SlowReadMs:0} ms",
                actual: slow.Count == 0 ? $"median {median:0} ms, slowest {times[^1]:0} ms"
                    : string.Join(", ", slow.Take(8).Select(r => $"{r.Member} {r.DurationMs:0} ms")),
                fix: "Clients read properties many times a second. A slow property usually waits on a serial round trip to " +
                     "the mount: answer from a cached state updated in the background instead.",
                sourceMember: slow.FirstOrDefault()?.Member);
        }

        // Calls that never answered (the harness gave up waiting): a hang, whatever the call.
        var hung = results.Where(r => r.Actual?.Contains("no answer within", StringComparison.OrdinalIgnoreCase) == true
                                      || r.Message.Contains("did not answer", StringComparison.OrdinalIgnoreCase)
                                      || r.Message.Contains("no answer", StringComparison.OrdinalIgnoreCase)).ToList();
        ctx.Report("Hangs", "all members", hung.Count == 0 ? Outcome.Pass : Outcome.Fail,
            hung.Count == 0 ? "No call hung: every one answered or failed within the call timeout."
                : $"{hung.Count} call(s) never answered within {ctx.Options.CallTimeoutSeconds} s.",
            actual: hung.Count == 0 ? null : string.Join(", ", hung.Select(r => r.Member).Distinct().Take(10)),
            fix: "A call must always come back, with a value or an error: put a timeout on every serial command and turn a " +
                 "lost mount into NotConnectedException or DriverException instead of waiting for ever. (A mount that is " +
                 "switched off or unplugged mid-run is the real test: try it by hand with the simulator stopped.)",
            sourceMember: hung.FirstOrDefault()?.Member);
        return Task.CompletedTask;
    }
}
