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

using System.Runtime.InteropServices;
using GS.TestHarness.Engine;
using Xunit.Abstractions;

namespace GS.TestHarness.Tests;

public class EngineTests(ITestOutputHelper output)
{
    internal static readonly RunOptions Everything = new()
    {
        AllowMotion = true,
        AllowSettingsWrites = true,
        AllowSetPark = true,
        IncludeProtocol = false, // no HTTP server here
        IncludeDisconnected = true,
        SlewTimeoutSeconds = 10,
        CallTimeoutSeconds = 5,
        DriftSeconds = 3
    };

    /// <summary>Everything, plus the extended checks (tracking drift, several clients).</summary>
    internal static readonly RunOptions WithExtended = Everything with { Extended = true };

    internal static async Task<RunReport> RunAsync(FakeTelescope fake, RunOptions? options = null, TestTarget? target = null,
        SiteLocation? site = null)
    {
        var runner = new TestRunner(SourceLocator.None);
        await using var session = new DirectSession(fake);
        // Each extra client (the Clients checks) is another DirectSession with its own client number.
        var clients = 0;
        return await runner.RunAsync(session, target ?? new TestTarget(), options ?? Everything,
            openSession: () => Task.FromResult<ITelescopeSession>(new DirectSession(fake, Interlocked.Increment(ref clients))),
            site: site);
    }

    [Fact]
    public async Task A_run_at_another_site_sets_it_and_puts_the_servers_own_back()
    {
        var fake = new FakeTelescope();
        var report = await RunAsync(fake, Everything, site: SiteLocation.DefaultNorth);
        Dump(report);
        Assert.DoesNotContain(report.Results, r => r.IsProblem);
        Assert.Equal(Outcome.Info, Assert.Single(report.Results, r => r.Id == "Site.Apply").Outcome);
        Assert.Equal(Outcome.Info, Assert.Single(report.Results, r => r.Id == "Site.Restore").Outcome);
        Assert.Contains("@ Mauna Kea", report.Label);
        // The capability checks saw the new site.
        Assert.Contains(report.Results, r => r.Id == "Properties.SiteLatitude.Read" && r.Message.Contains("19.8207"));

        // Afterwards the mount is back at its own site.
        await using var reader = new DirectSession(fake, 99);
        var (lat, lon, elev) = await reader.RunAsync(t => { t.Connect(); return (t.SiteLatitude, t.SiteLongitude, t.SiteElevation); }, TimeSpan.FromSeconds(5));
        Assert.Equal((28.5, -81.4, 30.0), (lat, lon, elev));
    }

    private void Dump(RunReport report, Func<CheckResult, bool>? which = null)
    {
        foreach (var r in report.Results.Where(which ?? (r => r.IsProblem)))
            output.WriteLine($"{r.Outcome,-8} {r.Id}: {r.Message} | expected {r.Expected} | actual {r.Actual}");
    }

    [Fact]
    public async Task A_compliant_mount_has_no_failures_or_warnings()
    {
        var report = await RunAsync(new FakeTelescope(), WithExtended);
        Dump(report);
        Assert.DoesNotContain(report.Results, r => r.IsProblem);
        Assert.True(report.Count(Outcome.Pass) > 150, $"only {report.Count(Outcome.Pass)} checks passed");
        Assert.Equal("Fake GEM", report.Identity.Name);
        Assert.Equal((short)4, report.Identity.InterfaceVersion);
    }

    [Fact]
    public async Task A_compliant_COM_driver_has_no_failures_or_warnings()
    {
        // As GSServer's driver: COM, so the synchronous slews are implemented and checked too.
        var report = await RunAsync(new FakeTelescope { SyncSlews = true }, WithExtended, new TestTarget { Codebase = Codebase.GSServer });
        Dump(report);
        Assert.DoesNotContain(report.Results, r => r.IsProblem);
        Assert.Contains(report.Results, r => r is { Id: "Motion.SlewToCoordinates", Outcome: Outcome.Pass });
    }

    [Fact]
    public async Task An_Alpaca_driver_with_synchronous_slews_gets_a_warning()
    {
        var report = await RunAsync(new FakeTelescope { SyncSlews = true });
        Assert.Equal(Outcome.Warning, Assert.Single(report.Results, r => r.Id == "Motion.SlewToCoordinates.Deprecated").Outcome);
    }

    [Fact]
    public async Task Without_motion_the_mount_isnt_moved()
    {
        var report = await RunAsync(new FakeTelescope(), Everything with { AllowMotion = false });
        Dump(report);
        Assert.DoesNotContain(report.Results, r => r.IsProblem);
        Assert.Contains(report.Results, r => r is { Group: "Motion", Outcome: Outcome.Skipped });
        Assert.Contains(report.Results, r => r is { Group: "ParkHome", Outcome: Outcome.Skipped });
        Assert.DoesNotContain(report.Results, r => r.Id.StartsWith("Motion.Slew"));
    }

    [Theory]
    [InlineData(Defect.InterfaceVersion3, "Connection.InterfaceVersion.Current", Outcome.Warning)]
    [InlineData(Defect.TargetReadsZeroWhenNotSet, "Properties.TargetRightAscension.NotSet", Outcome.Info)]
    [InlineData(Defect.AcceptsInvalidLatitude, "Settings.SiteLatitude.InvalidHigh", Outcome.Fail)]
    [InlineData(Defect.WrongErrorForInvalidAxis, "Capabilities.CanMoveAxis.InvalidAxis", Outcome.Fail)]
    [InlineData(Defect.UtcDateIsLocalTime, "Properties.UTCDate.Value", Outcome.Warning)]
    [InlineData(Defect.TrackingSetWhenCantSet, "Settings.Tracking.NotImplemented", Outcome.Fail)]
    [InlineData(Defect.AzimuthFromSouth, "Properties.AltAz.Consistent", Outcome.Fail)]
    [InlineData(Defect.DeviceStateMissingTracking, "Common.DeviceState.Contents", Outcome.Fail)]
    public async Task Each_defect_is_caught(Defect defect, string id, Outcome expected)
        => await DefectAsync(defect, id, expected, Everything with { AllowMotion = false });

    internal async Task DefectAsync(Defect defect, string id, Outcome expected, RunOptions options)
    {
        var report = await RunAsync(new FakeTelescope(defect), options);
        Dump(report);
        var finding = Assert.Single(report.Results, r => r.Id == id);
        Assert.Equal(expected, finding.Outcome);
        if (expected is Outcome.Fail or Outcome.Warning) Assert.False(string.IsNullOrWhiteSpace(finding.Fix));
    }

    [Fact]
    public async Task An_unreachable_device_stops_the_run_with_an_error()
    {
        var runner = new TestRunner(SourceLocator.None);
        await using var session = new AlpacaSession(new TestTarget { Host = "127.0.0.1", Port = 1 }, TimeSpan.FromSeconds(2));
        var report = await runner.RunAsync(session, new TestTarget { Host = "127.0.0.1", Port = 1 }, Everything);
        Dump(report);
        Assert.Contains(report.Results, r => r is { Id: "Connection.Stopped", Outcome: Outcome.Error });
        Assert.DoesNotContain(report.Results, r => r.Group == "Properties");
    }

    [Fact]
    public async Task Stopping_a_run_keeps_the_findings_so_far()
    {
        var runner = new TestRunner(SourceLocator.None);
        await using var session = new DirectSession(new FakeTelescope());
        using var cts = new CancellationTokenSource();
        var report = await runner.RunAsync(session, new TestTarget(), Everything,
            onProgress: p => { if (p.Group == "Properties") cts.Cancel(); }, token: cts.Token);
        Assert.True(report.Cancelled);
        Assert.Contains(report.Results, r => r.Group == "Connection");
        Assert.DoesNotContain(report.Results, r => r.Group == "Motion");
    }

    [Theory]
    [InlineData(unchecked((int)0x80040400), AscomErrorKind.NotImplemented)]
    [InlineData(unchecked((int)0x80040401), AscomErrorKind.InvalidValue)]
    [InlineData(unchecked((int)0x80040402), AscomErrorKind.ValueNotSet)]
    [InlineData(unchecked((int)0x80040407), AscomErrorKind.NotConnected)]
    [InlineData(unchecked((int)0x80040408), AscomErrorKind.Parked)]
    [InlineData(unchecked((int)0x8004040B), AscomErrorKind.InvalidOperation)]
    [InlineData(unchecked((int)0x80040500), AscomErrorKind.DriverSpecific)]
    [InlineData(unchecked((int)0x80004005), AscomErrorKind.Other)]
    public void COM_errors_are_classified_by_their_HRESULT(int hresult, AscomErrorKind kind)
        => Assert.Equal(kind, AscomError.Classify(new COMException("x", hresult)).Kind);

    [Fact]
    public void ASCOM_exceptions_are_classified_by_type()
    {
        Assert.Equal(AscomErrorKind.NotImplemented, AscomError.Classify(new ASCOM.PropertyNotImplementedException("p", false)).Kind);
        Assert.Equal(AscomErrorKind.ActionNotImplemented, AscomError.Classify(new ASCOM.ActionNotImplementedException("a")).Kind);
        Assert.Equal(AscomErrorKind.InvalidValue, AscomError.Classify(new AggregateException(new ASCOM.InvalidValueException("v"))).Kind);
        Assert.Equal(AscomErrorKind.Other, AscomError.Classify(new NullReferenceException()).Kind);
        Assert.Equal(AscomErrorKind.Timeout, AscomError.Classify(new CallTimeoutException(TimeSpan.FromSeconds(1))).Kind);
    }
}

/// <summary>Defects only motion shows; in their own class so xUnit runs them alongside the others.</summary>
public class MotionDefectTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(Defect.NotSlewingDuringMoveAxis, "Motion.MoveAxis.Primary.Slewing")]
    [InlineData(Defect.AsyncSlewBlocks, "Motion.SlewToCoordinatesAsync.West.Returns")]
    [InlineData(Defect.SideOfPierReversed, "Motion.SlewToCoordinatesAsync.West.DestinationSideOfPier")]
    [InlineData(Defect.ParkedMountSlews, "ParkHome.Parked.SlewToCoordinatesAsync")]
    [InlineData(Defect.SlewAcceptsRa25, "Motion.SlewToCoordinatesAsync.InvalidRaHigh")]
    [InlineData(Defect.PulseGuideWhileNotTracking, "Motion.PulseGuide.NotTracking")]
    [InlineData(Defect.MoveAxisStopsTracking, "Motion.MoveAxis.Primary.TrackingRestored")]
    [InlineData(Defect.SettleTimeIgnored, "Motion.SlewSettleTime.Effect")]
    [InlineData(Defect.ParkCantBeAborted, "ParkHome.AbortSlew.DuringPark")]
    [InlineData(Defect.PulseGuideDecReversedThroughThePole, "Motion.PulseGuide.North.Moves.ThroughThePole")]
    [InlineData(Defect.PulseGuideDecReversedThroughThePole, "Motion.PulseGuide.South.Moves.ThroughThePole")]
    [InlineData(Defect.MoveAxisIgnoresSign, "Motion.MoveAxis.Primary.Direction")]
    // The defect keeps whatever side the mount started on, so only the flip checks fail on both crossings every time.
    [InlineData(Defect.NoMeridianFlip, "Motion.Flip.Goto.EastToWest.Flipped")]
    [InlineData(Defect.NoMeridianFlip, "Motion.Flip.Goto.WestToEast.Flipped")]
    [InlineData(Defect.SideOfPierSetIgnored, "Motion.Flip.SideOfPier.West")]
    public Task Each_motion_defect_is_caught(Defect defect, string id)
        => new EngineTests(output).DefectAsync(defect, id, Outcome.Fail, EngineTests.Everything);

    [Fact]
    public Task A_client_disconnecting_others_is_caught()
        => new EngineTests(output).DefectAsync(Defect.ClientDisconnectDropsAll, "Clients.OneDisconnects", Outcome.Fail,
            EngineTests.WithExtended with { AllowMotion = false });
}
