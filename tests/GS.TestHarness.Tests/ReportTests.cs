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

    [Fact]
    public void Comparing_runs_sorts_checks_into_fixed_regressed_and_still_failing()
    {
        var before = Report(Result("A.X", Outcome.Fail), Result("A.Y", Outcome.Pass), Result("A.Z", Outcome.Fail, "one"),
            Result("A.Gone", Outcome.Fail), Result("A.Same", Outcome.Pass));
        var after = Report(Result("A.X", Outcome.Pass), Result("A.Y", Outcome.Warning), Result("A.Z", Outcome.Fail, "one"),
            Result("A.New", Outcome.Fail), Result("A.Same", Outcome.Pass), Result("A.Added", Outcome.Pass));
        var diff = ReportComparer.Compare(before, after).ToDictionary(d => d.Id, d => d.Change);
        Assert.Equal(Change.Fixed, diff["A.X"]);
        Assert.Equal(Change.Regressed, diff["A.Y"]);
        Assert.Equal(Change.StillFailing, diff["A.Z"]);
        Assert.Equal(Change.Gone, diff["A.Gone"]);
        Assert.Equal(Change.New, diff["A.New"]);
        Assert.Equal(Change.Same, diff["A.Same"]);
        Assert.Equal(Change.Added, diff["A.Added"]);
    }

    [Fact]
    public void Comparing_sites_finds_what_differs_what_fails_everywhere_and_what_some_sites_skipped()
    {
        var server = Report(Result("A.Diff", Outcome.Pass), Result("A.Msg", Outcome.Fail, "one"), Result("A.Bad", Outcome.Fail),
            Result("A.Some", Outcome.Pass), Result("A.Ok", Outcome.Pass));
        var north = Report(Result("A.Diff", Outcome.Fail), Result("A.Msg", Outcome.Fail, "two"), Result("A.Bad", Outcome.Fail),
            Result("A.Ok", Outcome.Pass));
        var south = Report(Result("A.Diff", Outcome.Pass), Result("A.Msg", Outcome.Fail, "one"), Result("A.Bad", Outcome.Fail),
            Result("A.Some", Outcome.Pass), Result("A.Ok", Outcome.Pass));
        var rows = ReportComparer.CompareSites([server, north, south]).ToDictionary(d => d.Id);
        Assert.Equal(SiteChange.Differs, rows["A.Diff"].Change);
        Assert.Equal(SiteChange.Differs, rows["A.Msg"].Change);
        Assert.Equal(SiteChange.FailsEverywhere, rows["A.Bad"].Change);
        Assert.Equal(SiteChange.NotEverywhere, rows["A.Some"].Change);
        Assert.Null(rows["A.Some"].Results[1]);
        Assert.Equal(SiteChange.Same, rows["A.Ok"].Change);
        Assert.Equal(Outcome.Fail, rows["A.Diff"].Results[1]!.Outcome);
    }

    [Fact]
    public void Runs_of_another_device_codebase_or_transport_are_different_programs()
    {
        var gss = Report() with { Target = new TestTarget { Codebase = Codebase.GSServer }, Identity = new DeviceIdentity { Name = "GSServer", DriverVersion = "1.2.3.3" } };
        var gss2 = gss with { Identity = new DeviceIdentity { Name = "GSServer", DriverVersion = "1.2.3.4" } };
        var gsa = Report() with { Target = new TestTarget { Codebase = Codebase.GreenSwampAlpaca }, Identity = new DeviceIdentity { Name = "GS Simulator (GEM)" } };
        Assert.False(ReportComparer.AreDifferentPrograms(gss, gss2));
        // The same server at another site is still the same program: those runs compare too.
        Assert.False(ReportComparer.AreDifferentPrograms(gss, gss with { Site = SiteLocation.DefaultSouth }));
        Assert.True(ReportComparer.AreDifferentPrograms(gss, gsa));
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
}
