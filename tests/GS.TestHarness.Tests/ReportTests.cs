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

namespace GS.TestHarness.Tests;

public class ReportTests
{
    private static CheckResult Result(string id, Outcome outcome, string message = "m")
        => new() { Id = id, Group = id.Split('.')[0], Member = id.Split('.')[1], Outcome = outcome, Message = message, Fix = "do this" };

    private static RunReport Report(params CheckResult[] results)
        => new() { Target = new TestTarget(), Options = new RunOptions(), Results = [.. results], Identity = new DeviceIdentity { Name = "Dev", DriverVersion = "1.2" } };

    private static RunReport Run(string version, int minutesAgo)
        => Report() with
        {
            Identity = new DeviceIdentity { Name = "Dev", DriverVersion = version },
            Started = DateTimeOffset.Now.AddMinutes(-minutesAgo)
        };

    [Fact]
    public void A_report_links_to_every_run_of_its_tiers_and_lists_their_own_tests()
    {
        var tier1 = Run("1.3", 30);
        var tier2 = Report(Result("Diagnose.RightAscensionRate", Outcome.Fail), Result("Connection.Connected.Read", Outcome.Pass)) with
            { Started = DateTimeOffset.Now.AddMinutes(-20), Options = new RunOptions { Tier = 2, FollowsUp = tier1.Id } };
        var tier3 = Run("1.3", 10) with { Options = new RunOptions { Tier = 3, FollowsUp = tier2.Id } };
        var other = Run("1.3", 5);
        RunReport[] all = [other, tier3, tier2, tier1];

        // From any run of the chain: the same runs, by tier; another tier 1 run isn't in it.
        Assert.Equal([tier1.Id, tier2.Id, tier3.Id], Tiers.Chain(tier3, all).Select(r => r.Id));
        Assert.Equal([tier1.Id, tier2.Id, tier3.Id], Tiers.Chain(tier1, all).Select(r => r.Id));
        Assert.Equal([other.Id], Tiers.Chain(other, all).Select(r => r.Id));
        // A deeper run whose tier 1 run was deleted still shows itself.
        Assert.Equal([tier2.Id, tier3.Id], Tiers.Chain(tier2, [tier2, tier3]).Select(r => r.Id));

        // Its own tier's tests by number: the diagnosis (2003), not the connection check (tier 1).
        Assert.Equal([2003], Tiers.OwnTests(tier2));
    }

    [Fact]
    public void CSV_quotes_what_needs_it_and_defuses_formulas()
    {
        var report = Report(Result("A.X", Outcome.Fail, "says \"no\", twice\nthen stops"), Result("A.Y", Outcome.Pass, "=HYPERLINK(\"x\")"),
            Result("A.Z", Outcome.Pass, "-91"));
        var bytes = ReportCsv.Report(report);
        Assert.Equal(System.Text.Encoding.UTF8.GetPreamble(), bytes[..3]);
        var text = System.Text.Encoding.UTF8.GetString(bytes[3..]);
        var lines = text.Split("\r\n");
        Assert.StartsWith("No,Id,Group,Member,Outcome,Weight,Message", lines[0]);
        Assert.Contains("\"says \"\"no\"\", twice\nthen stops\"", text);
        // A leading apostrophe makes the formula text; the quotes inside it force quoting.
        Assert.Contains(",\"'=HYPERLINK(\"\"x\"\")\",", text);
        Assert.Contains(",-91,", text);

    }

    [Fact]
    public void Markdown_keeps_every_line_of_actual_and_csv_escapes_minus_infinity()
    {
        var diagnosis = Result("Diagnose.X", Outcome.Fail) with
        {
            Expected = "first\nsecond", Actual = "5 of 25 members answered:\n  RightAscension: answered\n  Tracking: answered", Fix = "add\nthe guard"
        };
        var md = ReportStore.ToMarkdown(Report(diagnosis));
        Assert.Contains("  RightAscension: answered", md);
        Assert.Contains("  Tracking: answered", md);
        Assert.Contains("- Expected: first second", md);
        Assert.Contains("- Fix: add the guard", md);

        var csv = System.Text.Encoding.UTF8.GetString(ReportCsv.Report(Report(Result("A.X", Outcome.Fail) with { Actual = "-Infinity" }))[3..]);
        Assert.Contains(",'-Infinity,", csv);
    }

    [Fact]
    public void Settings_sit_beside_the_reports_folder_and_inside_one_written_with_a_trailing_separator()
    {
        var root = Path.Combine(Path.GetTempPath(), "gs-harness-settings");
        Assert.Equal(root, new ReportStore(Path.Combine(root, "Reports")).SettingsFolder);
        // As before the upgrade: "...\Reports\" kept its settings in Reports itself.
        Assert.Equal(Path.Combine(root, "Reports"), new ReportStore(Path.Combine(root, "Reports") + Path.DirectorySeparatorChar).SettingsFolder);
    }

    [Fact]
    public async Task References_are_RAC_for_conformance_runs_and_RCU_for_ConformU_runs()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gs-harness-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ReportStore(folder);
            var conformance = Report(Result("A.X", Outcome.Fail));
            var conformU = Report(Result("A.Y", Outcome.Fail)) with { ConformULog = "ConformU.debug.txt" };
            await store.SaveAsync(conformance);
            await store.SaveAsync(conformU);
            Assert.Equal("1-RAC0001", conformance.Reference);
            Assert.Equal("1-RCU0002", conformU.Reference);

            Assert.Equal(conformance.Id, (await store.LoadAsync("1-RAC0001"))!.Id);
            Assert.Equal(conformance.Id, (await store.LoadAsync("RAC0001"))!.Id); // before the tier was in it
            Assert.Equal(conformU.Id, (await store.LoadAsync("rcu2"))!.Id);
            // The old form and a bare number still find either kind; a prefix naming the other test finds nothing.
            Assert.Equal(conformU.Id, (await store.LoadAsync("R0002"))!.Id);
            Assert.Equal(conformance.Id, (await store.LoadAsync("1"))!.Id);
            Assert.Null(await store.LoadAsync("RCU0001"));
            Assert.Null(await store.LoadAsync("RAC0002"));
            // A tier that isn't the run's finds nothing.
            Assert.Null(await store.LoadAsync("2-RAC0001"));
            Assert.Null(await store.LoadAsync("3-RCU0002"));
            Assert.Equal(conformU.Id, (await store.LoadAsync("1-rcu2"))!.Id);

            // A deeper tier of a ConformU run follows up the same test's run.
            var tier2 = conformU with { Number = 3, FollowsUpNumber = 2, Options = new RunOptions { Tier = 2, FollowsUp = conformU.Id } };
            Assert.Equal("1-RCU0002", tier2.FollowsUpReference);
            Assert.Equal("2-RCU0003", tier2.Reference);
            Assert.Equal("3-RAC0004", (conformance with { Number = 4, Options = new RunOptions { Tier = 3 } }).Reference);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task Reports_are_saved_listed_loaded_and_deleted()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gs-harness-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ReportStore(folder);
            var report = Report(Result("A.X", Outcome.Fail));
            await store.SaveAsync(report);
            var listed = Assert.Single(await store.ListAsync());
            Assert.Equal(report.Id, listed.Id);
            var loaded = await store.LoadAsync(report.Id);
            Assert.Equal(Outcome.Fail, Assert.Single(loaded!.Results).Outcome);
            store.Delete(report.Id);
            Assert.Empty(await store.ListAsync());
            Assert.Throws<ArgumentException>(() => store.Delete("../evil"));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task Listing_again_picks_up_changed_new_and_deleted_files()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gs-harness-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ReportStore(folder);
            var report = Report(Result("A.X", Outcome.Fail));
            await store.SaveAsync(report);
            Assert.Same(Assert.Single(await store.ListAsync()), Assert.Single(await store.ListAsync()));

            report.Results.Add(Result("A.Y", Outcome.Pass));
            await store.SaveAsync(report);
            Assert.Equal(2, Assert.Single(await store.ListAsync()).Results.Count);

            var other = Report(Result("B.X", Outcome.Pass));
            await store.SaveAsync(other);
            Assert.Equal(2, (await store.ListAsync()).Count);

            store.Delete(report.Id);
            Assert.Equal(other.Id, Assert.Single(await store.ListAsync()).Id);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Markdown_lists_each_problem_with_its_fix()
    {
        var md = ReportStore.ToMarkdown(Report(Result("A.X", Outcome.Fail, "broken"), Result("A.Y", Outcome.Pass)));
        Assert.Contains("1 failed", md);
        Assert.Contains("### X: broken", md);
        Assert.Contains("- Fix: do this", md);
        Assert.DoesNotContain("A.Y", md);
    }

    [Fact]
    public void Source_locator_finds_a_member_definition_and_falls_back_to_patterns()
    {
        var root = Path.Combine(Path.GetTempPath(), "gs-harness-src-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Driver"));
            File.WriteAllLines(Path.Combine(root, "Driver", "Telescope.cs"),
            [
                "class Telescope {",
                "    public double TargetDeclinationRate => 0;",
                "    public double TargetDeclination",
                "    {",
                "    public void SlewToTarget() { }",
                "}"
            ]);
            File.WriteAllText(Path.Combine(root, "Server.cs"), "// writes ClientTransactionID\n");
            var locator = new SourceLocator(new Dictionary<Codebase, CodebaseOptions>
            {
                [Codebase.GSServer] = new() { Root = root, TelescopeFiles = ["Driver/Telescope.cs"] }
            }, "");
            Assert.Equal("Driver/Telescope.cs:3", locator.Find(Codebase.GSServer, "TargetDeclination"));
            Assert.Equal("Driver/Telescope.cs:5", locator.Find(Codebase.GSServer, "SlewToTarget"));
            Assert.Equal("Server.cs:1", locator.Find(Codebase.GSServer, "/management", "ClientTransactionID"));
            Assert.Null(locator.Find(Codebase.GreenSwampAlpaca, "TargetDeclination"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void A_finding_made_by_a_run_keeps_the_weight_the_run_used()
    {
        // The fake mount's run: every finding is stamped with its test's weight when it is made.
        var report = EngineTests.RunAsync(new FakeTelescope()).GetAwaiter().GetResult();
        Assert.NotEmpty(report.Results);
        Assert.All(report.Results, r => Assert.InRange(r.Weight, TestWeights.Min, TestWeights.Max));
        Assert.Equal(TestWeights.Limit, report.WeightLimit);
    }

    [Fact]
    public void The_markdown_and_the_csv_list_the_weights_used()
    {
        var failed = Result("Connection.Connect", Outcome.Fail) with { Weight = 10 };
        var passed = Result("Protocol.Reply.Json", Outcome.Pass) with { Weight = 2 };
        var report = Report(failed, passed) with { WeightLimit = 40, AdjustedWeights = new() { ["Connection.Connect"] = 10 } };
        Assert.Equal(10, report.FailedWeight);
        var md = ReportStore.ToMarkdown(report);
        Assert.Contains("failed tests add up to 10 (limit 40)", md);
        Assert.Contains("`Connection.Connect` = 10", md);
        Assert.Contains("(weight 10 of 10)", md);
        var csv = System.Text.Encoding.UTF8.GetString(ReportCsv.Report(report)[3..]);
        Assert.Contains(",Fail,10,", csv);
        Assert.Contains(",Pass,2,", csv);
    }
}
