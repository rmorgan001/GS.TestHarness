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
using Xunit;

namespace GS.TestHarness.Tests;

public class SuggestedTestsTests
{
    private static CheckResult R(string id, Outcome outcome, string group, string member)
        => new() { Id = id, Group = group, Member = member, Outcome = outcome, Message = $"{member} went wrong" };

    private static RunReport Tier1(params CheckResult[] results)
        => new() { Target = new TestTarget(), Options = EngineTests.Everything, Results = [.. results] };

    [Fact]
    public void The_same_kind_of_problem_in_several_members_is_one_suggestion()
    {
        // Invalid-value checks of two slews are one kind of test (Motion.{slew}.InvalidRaLow) with two members.
        var report = Tier1(
            R("Motion.SlewToCoordinates.InvalidRaLow", Outcome.Fail, "Motion", "SlewToCoordinates"),
            R("Motion.SlewToTarget.InvalidRaLow", Outcome.Fail, "Motion", "SlewToTarget"));
        var found = SuggestedTests.Evaluate(report).Where(s => s.Spec.ProposedKey.Contains("InvalidRaLow")).ToList();
        Assert.Single(found);
        Assert.Equal(2, found[0].Members.Count);
    }

    [Fact]
    public void A_draft_has_a_key_a_free_number_and_files_to_change()
    {
        var report = Tier1(R("Motion.SlewToCoordinates.InvalidRaLow", Outcome.Fail, "Motion", "SlewToCoordinates"));
        var s = SuggestedTests.Evaluate(report).Single(x => x.Spec.ProposedKey.Contains("InvalidRaLow"));
        Assert.StartsWith("Diagnose.", s.Spec.ProposedKey);
        Assert.Equal(2, s.Spec.Tier);
        Assert.InRange(s.Spec.Number, 2001, 2999);
        Assert.DoesNotContain(TestNumbers.All, a => a.Number == s.Spec.Number);
        Assert.Contains(s.Spec.Edits, e => e.Contains("TestNumbers.cs") && e.Contains(s.Spec.Number.ToString()));
    }

    [Fact]
    public void Merging_gives_each_suggestion_its_own_number_and_lists_the_heaviest_first()
    {
        var report = Tier1(
            R("Motion.SlewToCoordinates.InvalidRaLow", Outcome.Fail, "Motion", "SlewToCoordinates"),
            R("ParkHome.FindHome", Outcome.Fail, "ParkHome", "FindHome"));
        var all = new List<SuggestedTest>();
        SuggestedTests.Merge(all, SuggestedTests.Evaluate(report));
        Assert.True(all.Count >= 2);
        Assert.Equal(all.Select(s => s.Weight).OrderByDescending(w => w), all.Select(s => s.Weight));
        Assert.Equal(all.Count, all.Select(s => s.Spec.Number).Distinct().Count());
    }

    [Fact]
    public async Task A_stored_suggestion_counts_runs_and_a_dismissed_one_stays_hidden()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gs-suggest-" + Guid.NewGuid().ToString("N"), "Reports");
        try
        {
            var store = new SuggestionStore(new ReportStore(folder));
            var report = Tier1(R("Motion.SlewToCoordinates.InvalidRaLow", Outcome.Fail, "Motion", "SlewToCoordinates"));
            var found = SuggestedTests.Evaluate(report).Where(s => s.Spec.ProposedKey.Contains("InvalidRaLow")).ToList();

            var (first, _) = await store.RecordAsync(found, "run-1");
            Assert.Equal(0, first.Single().SeenBefore);
            var (second, _) = await store.RecordAsync(found, "run-2");
            Assert.Equal(1, second.Single().SeenBefore);

            await store.SetStatusAsync(found[0].Key, SuggestionStatus.Dismissed);
            var (third, hidden) = await store.RecordAsync(found, "run-3");
            Assert.Empty(third);
            Assert.Equal(1, hidden);

            await store.SetStatusAsync(found[0].Key, SuggestionStatus.Done);
            await store.RecordAsync(found, "run-4"); // suggested again: the test didn't cover it, so it is open again
            Assert.Equal(SuggestionStatus.Open, (await store.ListAsync()).Single().Status);
        }
        finally
        {
            if (Directory.GetParent(folder) is { } parent && parent.Exists) parent.Delete(recursive: true);
        }
    }
}
