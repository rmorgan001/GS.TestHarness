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
}
