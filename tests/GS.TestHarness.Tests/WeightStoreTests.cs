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

public class WeightStoreTests
{
    // A test nothing else here depends on, since the weights are shared by every test running at once.
    private const string Key = "Performance.ReadTimes";

    private static (WeightStore Store, string Root) Open(string? root = null)
    {
        root ??= Path.Combine(Path.GetTempPath(), "gs-weights-" + Guid.NewGuid().ToString("N"));
        return (new WeightStore(new ReportStore(Path.Combine(root, "Reports"))), root);
    }

    private static void Cleanup(string root)
    {
        TestWeights.Apply(new Dictionary<string, int>(), TestWeights.DefaultLimit);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void A_changed_weight_is_used_saved_and_found_again_after_a_restart()
    {
        var (store, root) = Open();
        try
        {
            var builtIn = TestWeights.Default(Key);
            var other = builtIn == 9 ? 8 : 9;
            store.SetWeight(Key, other);
            Assert.Equal(other, TestWeights.Of(Key));
            Assert.True(TestWeights.IsAdjusted(Key));

            TestWeights.Apply(new Dictionary<string, int>(), TestWeights.DefaultLimit); // as if the app had stopped
            Assert.Equal(builtIn, TestWeights.Of(Key));
            Open(root);
            Assert.Equal(other, TestWeights.Of(Key));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void Setting_the_built_in_weight_or_resetting_clears_the_change()
    {
        var (store, root) = Open();
        try
        {
            var builtIn = TestWeights.Default(Key);
            store.SetWeight(Key, builtIn == 9 ? 8 : 9);
            store.SetWeight(Key, builtIn);
            Assert.False(TestWeights.IsAdjusted(Key));

            store.SetWeight(Key, builtIn == 9 ? 8 : 9);
            store.Reset(Key);
            Assert.False(TestWeights.IsAdjusted(Key));
            Assert.False(store.HasChanges);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void A_weight_is_kept_between_one_and_ten_and_the_limit_can_be_changed_and_reset()
    {
        var (store, root) = Open();
        try
        {
            store.SetWeight(Key, 99);
            Assert.Equal(TestWeights.Max, TestWeights.Of(Key));
            store.SetLimit(55);
            Assert.Equal(55, TestWeights.Limit);
            Assert.Equal(55, Tiers.MaxProblemsToContinue);

            store.ResetAll();
            Assert.Equal(TestWeights.DefaultLimit, TestWeights.Limit);
            Assert.False(store.HasChanges);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void A_damaged_file_is_kept_aside_and_the_built_in_weights_are_used()
    {
        var root = Path.Combine(Path.GetTempPath(), "gs-weights-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "test-weights.json"), "{ not json");
            var (store, _) = Open(root);
            Assert.False(store.HasChanges);
            Assert.True(File.Exists(Path.Combine(root, "test-weights.json.bad")));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void An_export_lists_every_test_and_imports_back_the_same()
    {
        var (store, root) = Open();
        try
        {
            var builtIn = TestWeights.Default(Key);
            var other = builtIn == 9 ? 8 : 9;
            store.SetWeight(Key, other);
            store.SetLimit(77);
            var json = store.Export();
            using (var doc = System.Text.Json.JsonDocument.Parse(json))
            {
                Assert.Equal(WeightStore.Format, doc.RootElement.GetProperty("format").GetString());
                Assert.Equal(TestCatalog.Entries.Count, doc.RootElement.GetProperty("weights").EnumerateObject().Count());
                Assert.Equal(other, doc.RootElement.GetProperty("weights").GetProperty(Key).GetInt32());
            }

            store.ResetAll();
            var result = store.Import(json);
            Assert.True(result.Ok);
            Assert.Empty(result.Skipped);
            Assert.Equal(TestCatalog.Entries.Count, result.Applied);
            Assert.Equal(other, TestWeights.Of(Key));
            Assert.Equal(77, TestWeights.Limit);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void An_import_leaves_out_what_it_cant_use_and_replaces_the_rest()
    {
        var (store, root) = Open();
        try
        {
            store.SetWeight("Performance.Hangs", TestWeights.Default("Performance.Hangs") == 9 ? 8 : 9); // not in the file: goes back
            var result = store.Import($$"""{ "weights": { "{{Key}}": 10, "Not.ATest": 5, "Performance.Hangs": 0 }, "limit": 20 }""");
            Assert.True(result.Ok);
            Assert.Equal(2, result.Skipped.Count);
            Assert.Equal(1, result.Applied);
            Assert.Equal(10, TestWeights.Of(Key));
            Assert.False(TestWeights.IsAdjusted("Performance.Hangs"));
            Assert.Equal(20, TestWeights.Limit);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void A_file_that_isnt_a_weights_file_changes_nothing()
    {
        var (store, root) = Open();
        try
        {
            store.SetWeight(Key, TestWeights.Default(Key) == 9 ? 8 : 9);
            var before = TestWeights.Of(Key);
            Assert.False(store.Import("not json").Ok);
            Assert.False(store.Import("""{ "weights": 5 }""").Ok);
            Assert.False(store.Import("""{ "format": "something-else", "weights": {} }""").Ok);
            Assert.Equal(before, TestWeights.Of(Key));
        }
        finally { Cleanup(root); }
    }

    private static CheckResult F(string id, Outcome outcome = Outcome.Fail, string group = "Motion")
        => new() { Id = id, Group = group, Member = "m", Outcome = outcome, Message = id };

    [Fact]
    public void A_failed_critical_test_stops_a_tier_at_once_but_a_pass_or_warning_never_does()
    {
        var critical = F("Motion.AbortSlew.DuringSlew");
        Assert.NotNull(Tiers.SafetyReason([critical], critical));
        var error = F("Motion.AbortSlew.DuringSlew", Outcome.Error);
        Assert.NotNull(Tiers.SafetyReason([error], error));
        var warning = F("Motion.AbortSlew.DuringSlew", Outcome.Warning);
        Assert.Null(Tiers.SafetyReason([warning], warning));
        var pass = F("Motion.AbortSlew.DuringSlew", Outcome.Pass);
        Assert.Null(Tiers.SafetyReason([pass], pass));
    }

    [Fact]
    public void Failed_weights_stop_a_tier_when_they_pass_the_limit()
    {
        try
        {
            TestWeights.Apply(new Dictionary<string, int>(), 10);
            var minor = F("Protocol.Reply.Json", group: "Protocol"); // weight 2
            var failed = new List<CheckResult>();
            string? reason = null;
            for (var i = 0; i < 10 && reason is null; i++)
            {
                failed.Add(minor);
                reason = Tiers.SafetyReason(failed, minor);
            }
            Assert.NotNull(reason);
            Assert.Equal(6, failed.Count); // 6 x 2 = 12, the first total over 10
            Assert.Contains("limit is 10", reason);
        }
        finally { TestWeights.Apply(new Dictionary<string, int>(), TestWeights.DefaultLimit); }
    }
}
