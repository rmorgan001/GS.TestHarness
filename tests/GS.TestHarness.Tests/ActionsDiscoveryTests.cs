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
using GS.TestHarness.Engine.Checks;
using Xunit.Abstractions;

namespace GS.TestHarness.Tests;

/// <summary>Tier 2 for R0014's Action and discovery problems (the ASCOM Alpaca Simulators 0.5.0).</summary>
public class ActionsDiscoveryTests(ITestOutputHelper output)
{
    private static CheckResult Found(string id, string member, Outcome outcome = Outcome.Fail)
        => new() { Id = id, Group = id[..id.IndexOf('.')], Member = member, Outcome = outcome, Message = $"tier 1: {id}" };

    private async Task<RunReport> Tier2Async(FakeTelescope fake, RunReport tier1, SourceLocator locator)
    {
        var next = Tiers.Next(tier1);
        Assert.True(next is { Tier: 2 }, "tier 2 wasn't offered");
        var runner = new TestRunner(locator);
        await using var session = new DirectSession(fake);
        var report = await runner.RunAsync(session, tier1.Target, Tiers.Options(EngineTests.Everything, next!), followsUp: tier1);
        Assert.DoesNotContain(report.Results, r => TestCatalog.For(r.Id) is null);
        foreach (var r in report.Results.Where(Tiers.IsDiagnosis))
            output.WriteLine($"{r.Outcome,-8} {r.Number} {r.Id}: {r.Message}\n  {r.Actual?.Replace("\n", "\n  ")}\n  FIX: {r.Fix}\n");
        return report;
    }

    [Fact]
    public async Task Tier_2_finds_where_an_unknown_action_gets_the_wrong_error()
    {
        var root = Path.Combine(Path.GetTempPath(), "gs-harness-action-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Driver"));
            File.WriteAllText(Path.Combine(root, "Driver", "Telescope.cs"), """
                public class Telescope
                {
                    public string Action(string actionName, string actionParameters)
                    {
                        switch (actionName.ToUpperInvariant())
                        {
                            case "SLEWTOHA":
                                return "";
                            case "ASSEMBLYVERSIONNUMBER":
                                return "1.0";
                            default:
                                throw new InvalidOperationException($"Command: '{actionName}' is not recognised by the Scope Simulator .NET driver.");
                        }
                    }
                }
                """);
            var locator = new SourceLocator(new Dictionary<Codebase, CodebaseOptions>
            {
                [Codebase.GSServer] = new() { Root = root, TelescopeFiles = ["Driver/Telescope.cs"] }
            }, "");
            var tier1 = new RunReport
            {
                Target = new TestTarget { Codebase = Codebase.GSServer }, Options = EngineTests.Everything,
                Results = [Found("Common.Action.Unknown", "Action")]
            };
            Assert.Equal([AreaDiagnostics.Actions], Tiers.Next(tier1)!.Members);

            var wrong = (await Tier2Async(new FakeTelescope(Defect.ActionWrongError), tier1, locator)).Results.Single(r => r.Id == "Diagnose.Actions");
            Assert.Equal(Outcome.Fail, wrong.Outcome);
            Assert.Contains("InvalidOperationException", wrong.Message);
            Assert.Contains("is not recognised by the Scope Simulator .NET driver", wrong.Actual);
            Assert.Contains("Driver/Telescope.cs:12", wrong.Fix);
            Assert.Contains("Handled but not in SupportedActions: ASSEMBLYVERSIONNUMBER", wrong.Actual);
            Assert.Contains("Also list ASSEMBLYVERSIONNUMBER in SupportedActions", wrong.Fix);

            var right = (await Tier2Async(new FakeTelescope(), tier1, locator)).Results.Single(r => r.Id == "Diagnose.Actions");
            Assert.Equal(Outcome.Info, right.Outcome);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("Command: 'GS.TestHarness.NoSuchAction939442' is not recognised by the Scope Simulator .NET driver. ASSEMBLYVERSIONNUMBER SLEWTOHA",
        "is not recognised by the Scope Simulator .NET driver")]
    [InlineData("Action GS.TestHarness.NoSuchAction939442 is not implemented in this driver", "is not implemented in this driver")]
    public void The_errors_own_words_are_looked_for_in_the_code(string message, string fragment)
        => Assert.Equal(fragment, AreaDiagnostics.MessageFragment(message, "GS.TestHarness.NoSuchAction939442"));

    private static AreaDiagnostics.DiscoveryProbe Probe(string kind, bool answered) => new(kind, kind, answered, answered ? 1 : 0);

    [Fact]
    public void Discovery_answers_tell_the_cause()
    {
        // R0014: answers at 127.0.0.1 and at this PC's address, but no broadcast reaches it: likely not the server's fault.
        var (outcome, message, _) = AreaDiagnostics.DiscoveryVerdict([Probe("server", true), Probe("this PC", true), Probe("broadcast", false)], local: true);
        Assert.Equal(Outcome.Info, outcome);
        Assert.Contains("listens on every address", message);

        (outcome, message, _) = AreaDiagnostics.DiscoveryVerdict([Probe("server", true), Probe("this PC", false), Probe("broadcast", false)], local: true);
        Assert.Equal(Outcome.Fail, outcome);
        Assert.Contains("localhost only", message);

        (outcome, message, _) = AreaDiagnostics.DiscoveryVerdict([Probe("server", false), Probe("broadcast", false)], local: false);
        Assert.Equal(Outcome.Fail, outcome);
        Assert.Contains("isn't running", message);

        (outcome, _, _) = AreaDiagnostics.DiscoveryVerdict([Probe("server", true), Probe("broadcast", false)], local: false);
        Assert.Equal(Outcome.Warning, outcome);

        (outcome, _, _) = AreaDiagnostics.DiscoveryVerdict([Probe("server", true), Probe("broadcast", true)], local: true);
        Assert.Equal(Outcome.Info, outcome);
    }

    [Fact]
    public void Discovery_problems_are_offered_a_tier_2_diagnosis()
    {
        var tier1 = new RunReport
        {
            Target = new TestTarget { Codebase = Codebase.GreenSwampAlpaca, Host = "127.0.0.1", Port = 32323 }, Options = EngineTests.Everything,
            Results = [Found("Protocol.Discovery.Broadcast", "UDP discovery", Outcome.Warning)]
        };
        Assert.Equal([AreaDiagnostics.Discovery], Tiers.Next(tier1)!.Members);
        Assert.Empty(Tiers.OutlookFor(tier1).Uncovered);
    }
}
