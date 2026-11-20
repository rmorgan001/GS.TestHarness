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

using GS.TestHarness.Engine.Checks;

namespace GS.TestHarness.Engine;

/// <summary>What a run would do, without connecting to anything (the command line's --dry-run).</summary>
public static class RunPlan
{
    /// <summary>The groups the run would go through, in order, and for each the reason it would be skipped (null: it runs).</summary>
    public static IReadOnlyList<(string Group, string? SkipReason)> For(TestTarget target, RunOptions options, bool hasConformULog = false)
    {
        var stub = new PlanSession(target.Kind == TargetKind.Com ? "COM" : "Alpaca");
        var ctx = new CheckContext(stub, target, options, SourceLocator.None, _ => { }, CancellationToken.None,
            openSession: () => Task.FromResult<ITelescopeSession>(stub));
        var plan = new List<(string, string?)>();
        foreach (var group in TestRunner.Groups)
        {
            // Groups left out of the run aren't mentioned (as in TestRunner.RunAsync).
            if (options.Groups is { } only && !only.Contains(group.Name)) continue;
            var reason = group is ConformUChecks && !hasConformULog ? "no ConformU log for this run"
                : group is DiagnoseChecks && (options.Tier < 2 || hasConformULog) ? "tiers 2 and 3 only"
                : group.SkipReason(ctx);
            plan.Add((group.Name, reason));
        }
        return plan;
    }

    /// <summary>A session that is never used: the groups' skip rules only look at the options and the target.</summary>
    private sealed class PlanSession(string transport) : ITelescopeSession
    {
        public string Transport { get; } = transport;

        public Task<T> RunAsync<T>(Func<ASCOM.Common.DeviceInterfaces.ITelescopeV4, T> call, TimeSpan timeout)
            => throw new NotSupportedException("A dry run doesn't talk to the mount.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
