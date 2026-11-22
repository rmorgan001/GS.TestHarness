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

using System.Collections;
using ASCOM;
using ASCOM.Common.DeviceInterfaces;
using GS.TestHarness.Engine;
using GS.TestHarness.Engine.Checks;

namespace GS.TestHarness.Tests;

/// <summary>Faults the fake mount can be given, each of which some check must catch.</summary>
public enum Defect
{
    InterfaceVersion3,

    /// <summary>InterfaceVersion is 2: older than the harness tests, so the run stops.</summary>
    InterfaceVersion2,

    /// <summary>InterfaceVersion is 5: not one that exists, so the run stops.</summary>
    InterfaceVersion5,

    /// <summary>Reading InterfaceVersion throws: the run stops.</summary>
    InterfaceVersionUnreadable,
    TargetReadsZeroWhenNotSet,
    AcceptsInvalidLatitude,
    WrongErrorForInvalidAxis,
    NotSlewingDuringMoveAxis,
    AsyncSlewBlocks,
    UtcDateIsLocalTime,
    SideOfPierReversed,
    ParkedMountSlews,
    TrackingSetWhenCantSet,
    DeviceStateMissingTracking,
    SlewAcceptsRa25,
    PulseGuideWhileNotTracking,
    MoveAxisStopsTracking,
    SettleTimeIgnored,
    ClientDisconnectDropsAll,
    AzimuthFromSouth,
    ParkCantBeAborted,
    PulseGuideDecReversedThroughThePole,
    MoveAxisIgnoresSign,
    NoMeridianFlip,
    SideOfPierSetIgnored,
    SyncIgnored,
    SyncDoesntSetTargets,
    SyncToAltAzIgnored,
    RateOffsetKeptAtOtherRates,
    DecRateAcceptedAtOtherRates,
    NegativeRateOffsetRefused,
    SlewingDuringRateOffset,
    TrackingRateAcceptsUnlisted,

    /// <summary>The driver's server program dies at the first asynchronous slew: every call after it fails as COM does.</summary>
    ServerDiesAtFirstSlew,

    // For the ConformU diagnoses (ConformUDiagnosticsTests).

    /// <summary>RightAscension and Declination are cached, refreshed every 300 ms (as GSServer's DisplayInterval).</summary>
    StalePosition,

    /// <summary>Every pulse moves for 100 ms less than its duration (it starts late).</summary>
    PulseLosesTime,

    /// <summary>A RightAscensionRate offset moves the mount 1.5 times as fast as it should.</summary>
    RaOffsetScaled,

    /// <summary>RA jitters as GSServer's simulator's does: the axis moves in 20 ms steps while sidereal time runs on.</summary>
    PositionJitter,

    /// <summary>The first RightAscension read after RightAscensionRate is set is 0.015 s of RA out (a single stray reading).</summary>
    StrayReadingAfterRateSet,

    /// <summary>
    /// While tracking, RA drifts 0.0007 s of RA per second (0.0105"/s) whatever the offset, as GSServer's would with
    /// SkySettings.RaTrackingOffset at about 70 (or a custom gearing offset): the offset itself is right, but a 0.05"/s
    /// offset then measures about 1.21 × one way and 0.79 × the other.
    /// </summary>
    RaTrackingDrift,

    /// <summary>
    /// The RA reading wanders by up to 0.001 s of RA, slowly (a 4 s period from when RightAscensionRate was set), as
    /// readings do when a 20 ms position step is sampled at nearly the same rhythm: the error is bounded, so it doesn't
    /// grow with time as a wrong rate's does, but over a short measurement it looks like one.
    /// </summary>
    SlowPositionWander,

    /// <summary>The RA offset is applied rounded to a multiple of 0.002 s per second: 0.0033 moves at 0.004 (1.20 ×) either way.</summary>
    SmallRaOffsetRounded,

    /// <summary>Disconnect() returns without error but leaves the client connected (ConformU: "Connected Get returned True").</summary>
    ConnectedAfterDisconnect,

    /// <summary>Connected reports the hardware state: true for every client while any client is connected (allowed, as a hub does).</summary>
    ConnectedSharedByClients,

    /// <summary>As GS Server's COM driver: a client that hasn't called Connect itself is refused (NotConnected) even though Connected reads true for it.</summary>
    RefusesUnconnectedClient,

    // As GSServer 1.2.3.3 (the tier 2 area diagnoses, AreaDiagnosticsTests).

    /// <summary>No member checks the connection: everything answers while the client is disconnected.</summary>
    NoConnectedCheck,

    /// <summary>CanMoveAxis answers false for an invalid axis, and AxisRates returns null, instead of refusing.</summary>
    InvalidAxisUnchecked,

    /// <summary>AxisRates gives the tertiary axis rates too, though CanMoveAxis(Tertiary) is false.</summary>
    TertiaryAxisRates,

    /// <summary>PulseGuide passes an invalid direction to the mount code, whose ArgumentOutOfRangeException comes back as a DriverException.</summary>
    PulseGuideDirectionUnchecked,

    /// <summary>PulseGuide is accepted during a slew.</summary>
    PulseGuideWhileSlewing,

    /// <summary>SlewToCoordinatesAsync with tracking off turns tracking on and slews, instead of refusing (the other slews refuse).</summary>
    AsyncSlewTurnsTrackingOn,

    /// <summary>DriverVersion is the whole assembly version, "1.2.3.3".</summary>
    DriverVersionFull,

    /// <summary>
    /// As GSServer's simulator: after the main slew a final precision correction runs for 1.5 s, the position creeping
    /// the last 0.9" (Slewing true throughout), and the settle time (if used) comes before it.
    /// </summary>
    SlewEndsWithPrecisionCorrection,

    /// <summary>
    /// As GSServer's simulator measured (R0008): after the main slew, Slewing stays true 1.5 s with the mount still
    /// (its precision stage's tries make corrections too small to see), and the settle time (if used) comes before it.
    /// </summary>
    SlewEndsWithStillWait,

    // As the ASCOM Alpaca Simulators 0.5.0 (R0010): the arguments and rate offsets areas.

    /// <summary>The mount keeps moving at the rate offsets at tracking rates other than sidereal (with <see cref="RateOffsetKeptAtOtherRates"/>, they're read too).</summary>
    RateOffsetAppliedAtOtherRates,

    /// <summary>DestinationSideOfPier doesn't check its coordinates: any value is accepted.</summary>
    DestinationSideOfPierUnchecked,

    /// <summary>The guide rates accept a negative value.</summary>
    GuideRateNegativeAccepted,

    /// <summary>MoveAxis(axis, 0) makes Slewing false at once, but the axis coasts on at its rate for a second before it stops.</summary>
    SlewingEndsBeforeAxisStops,

    /// <summary>As the ASCOM Alpaca Simulators: SupportedActions lists one action, and an unknown one gets InvalidOperationException.</summary>
    ActionWrongError
}

/// <summary>
/// An in-memory German equatorial mount that follows ITelescopeV4 as the harness expects, unless given
/// <see cref="Defect"/>s. Slews, parks and homes take a moment, so the asynchronous behaviour is real.
/// </summary>
public sealed class FakeTelescope(params Defect[] defects) : ITelescopeV4
{
    private readonly HashSet<Defect> _defects = [.. defects];
    private readonly object _gate = new();
    private readonly HashSet<int> _clients = []; // the clients (DirectSession numbers) connected
    // Position at _anchor (while tracking): the home position (the pole, RA = LST - 6), where the mount starts with AtHome true.
    private double _ra = Values.Wrap24(Values.LocalSiderealTime(DateTime.UtcNow, -81.4) - 6), _dec = 90;
    private double _ha = 6;                  // hour angle held while not tracking (home is 6 h)
    private long _anchor = System.Diagnostics.Stopwatch.GetTimestamp();
    private double? _targetRa, _targetDec;
    private bool _tracking = true, _atPark, _atHome = true; // the mount starts at home, as a run expects
    private bool _slewing; // a timed motion (slew, park, home) is under way
    private readonly double[] _axisRates = new double[3];
    private readonly bool[] _coasting = new bool[3]; // SlewingEndsBeforeAxisStops: moving, but not reported as slewing
    private DateTime _pulseUntil;
    private bool _strayNext; // StrayReadingAfterRateSet: the next RightAscension read is out
    private long _raRateSetAt = System.Diagnostics.Stopwatch.GetTimestamp(); // SlowPositionWander: its phase starts here
    // The pulse under way on each axis: from Start (a Stopwatch timestamp), for Seconds, at these rates (hours and
    // degrees per second). A pulse moves the mount gradually, so a position read during or just after it shows that.
    private (long Start, double Seconds, double Ra, double Dec)? _pulseRa, _pulseDec;
    private PointingState? _forcedSide;
    private DriveRate _trackingRate = DriveRate.Sidereal;
    private double _raRate, _decRate, _guideRa = 0.004, _guideDec = 0.004;
    private double _lat = 28.5, _lon = -81.4, _elevation = 30;
    private short _settle;
    private bool _refraction;
    private int _motionGeneration;

    /// <summary>
    /// Whether the deprecated synchronous slews are implemented: as a COM driver must (when it can slew), and an
    /// Alpaca driver must not.
    /// </summary>
    public bool SyncSlews { get; init; }

    private bool Has(Defect d) => _defects.Contains(d);

    private void NeedSyncSlews(string member)
    {
        if (!SyncSlews) throw new MethodNotImplementedException(member);
    }

    private void NeedConnected()
    {
        NeedAlive();
        if (Has(Defect.RefusesUnconnectedClient))
        {
            lock (_clients) if (!_clients.Contains(Client)) throw new NotConnectedException("Telescope not connected");
        }
        if (!IsConnected && !Has(Defect.NoConnectedCheck)) throw new NotConnectedException("not connected");
    }

    private bool _dead;

    /// <summary>After the server has died, every call fails with RPC_S_SERVER_UNAVAILABLE, as a COM proxy does.</summary>
    private void NeedAlive()
    {
        if (_dead) throw new System.Runtime.InteropServices.COMException("The RPC server is unavailable.", unchecked((int)0x800706BA));
    }

    private double Lst => Values.LocalSiderealTime(DateTime.UtcNow, _lon);

    /// <summary>
    /// A timed motion: <paramref name="atEnd"/> puts the mount where it goes after <paramref name="arriveMs"/> (by
    /// default when the motion ends), and Slewing stays true until <paramref name="ms"/> (a slew's settle time after it arrives).
    /// </summary>
    private void StartMotion(int ms, Action atEnd, int? arriveMs = null)
    {
        int generation;
        lock (_gate)
        {
            _slewing = true;
            generation = ++_motionGeneration;
        }
        var arrive = Math.Min(arriveMs ?? ms, ms);
        _ = Task.Delay(arrive).ContinueWith(_ =>
        {
            lock (_gate)
            {
                if (generation != _motionGeneration) return;
                atEnd();
                if (arrive >= ms) _slewing = false;
            }
        });
        if (arrive < ms)
            _ = Task.Delay(ms).ContinueWith(_ =>
            {
                lock (_gate)
                    if (generation == _motionGeneration) _slewing = false;
            });
    }

    private void StopMotion()
    {
        lock (_gate)
        {
            _motionGeneration++;
            _slewing = false;
        }
    }

    private static void CheckRaDec(double ra, double dec)
    {
        if (ra is < 0 or >= 24) throw new InvalidValueException("RA", ra.ToString(), "0 to 24");
        if (dec is < -90 or > 90) throw new InvalidValueException("Dec", dec.ToString(), "-90 to 90");
    }

    private void CheckCanMove()
    {
        if (_atPark) throw new ParkedException("parked");
    }

    // ---------- IAscomDevice ----------

    private static int Client => DirectSession.CurrentClient.Value;

    private bool IsConnected
    {
        get { lock (_clients) return Has(Defect.ConnectedSharedByClients) ? _clients.Count > 0 : _clients.Contains(Client); }
    }

    public bool Connected
    {
        get { NeedAlive(); return IsConnected; }
        set { if (value) Connect(); else Disconnect(); }
    }
    public bool Connecting => false;
    public void Connect()
    {
        lock (_clients) _clients.Add(Client);
    }

    public void Disconnect()
    {
        lock (_clients)
        {
            if (Has(Defect.ClientDisconnectDropsAll)) _clients.Clear();
            else if (!Has(Defect.ConnectedAfterDisconnect)) _clients.Remove(Client);
        }
    }
    public string Description => "Fake GEM for the GS.TestHarness tests";
    public string DriverInfo => "GS.TestHarness.Tests fake telescope";
    public string DriverVersion => Has(Defect.DriverVersionFull) ? "1.2.3.3" : "1.0";
    public short InterfaceVersion => Has(Defect.InterfaceVersionUnreadable) ? throw new ASCOM.DriverException("InterfaceVersion failed") : (short)(Has(Defect.InterfaceVersion5) ? 5 : Has(Defect.InterfaceVersion2) ? 2 : Has(Defect.InterfaceVersion3) ? 3 : 4);
    public string Name => "Fake GEM";
    public IList<string> SupportedActions => Has(Defect.ActionWrongError) ? ["SLEWTOHA"] : [];

    public string Action(string actionName, string actionParameters) => Has(Defect.ActionWrongError)
        ? throw new ASCOM.InvalidOperationException($"Command: '{actionName}' is not recognised by the Scope Simulator .NET driver.")
        : throw new ActionNotImplementedException(actionName);
    public void CommandBlind(string command, bool raw = false) => throw new MethodNotImplementedException("CommandBlind");
    public bool CommandBool(string command, bool raw = false) => throw new MethodNotImplementedException("CommandBool");
    public string CommandString(string command, bool raw = false) => throw new MethodNotImplementedException("CommandString");
    public void Dispose() { }

    public List<StateValue> DeviceState
    {
        get
        {
            NeedConnected();
            var list = new List<StateValue>
            {
                new("Altitude", Altitude), new("AtHome", AtHome), new("AtPark", AtPark), new("Azimuth", Azimuth),
                new("Declination", Declination), new("IsPulseGuiding", IsPulseGuiding), new("RightAscension", RightAscension),
                new("SideOfPier", SideOfPier), new("SiderealTime", SiderealTime), new("Slewing", Slewing),
                new("UTCDate", UTCDate), new("TimeStamp", DateTime.UtcNow)
            };
            if (!Has(Defect.DeviceStateMissingTracking)) list.Add(new StateValue("Tracking", Tracking));
            return list;
        }
    }

    // ---------- Capabilities ----------

    public bool CanFindHome { get { NeedConnected(); return true; } }
    public bool CanPark { get { NeedConnected(); return true; } }
    public bool CanPulseGuide { get { NeedConnected(); return true; } }
    public bool CanSetDeclinationRate { get { NeedConnected(); return true; } }
    public bool CanSetGuideRates { get { NeedConnected(); return true; } }
    public bool CanSetPark { get { NeedConnected(); return true; } }
    public bool CanSetPierSide { get { NeedConnected(); return true; } }
    public bool CanSetRightAscensionRate { get { NeedConnected(); return true; } }
    public bool CanSetTracking { get { NeedConnected(); return !Has(Defect.TrackingSetWhenCantSet); } }
    public bool CanSlew { get { NeedConnected(); return true; } }
    public bool CanSlewAltAz { get { NeedConnected(); return true; } }
    public bool CanSlewAltAzAsync { get { NeedConnected(); return true; } }
    public bool CanSlewAsync { get { NeedConnected(); return true; } }
    public bool CanSync { get { NeedConnected(); return true; } }
    public bool CanSyncAltAz { get { NeedConnected(); return true; } }
    public bool CanUnpark { get { NeedConnected(); return true; } }

    public bool CanMoveAxis(TelescopeAxis axis)
    {
        NeedConnected();
        if (axis is < TelescopeAxis.Primary or > TelescopeAxis.Tertiary)
        {
            if (Has(Defect.InvalidAxisUnchecked)) return false;
            throw Has(Defect.WrongErrorForInvalidAxis) ? new DriverException("bad axis") : new InvalidValueException("axis");
        }
        return axis != TelescopeAxis.Tertiary;
    }

    public IAxisRates AxisRates(TelescopeAxis axis)
    {
        NeedConnected();
        if (axis is < TelescopeAxis.Primary or > TelescopeAxis.Tertiary)
            return Has(Defect.InvalidAxisUnchecked) ? null! : throw new InvalidValueException("axis");
        return new FakeAxisRates(axis == TelescopeAxis.Tertiary && !Has(Defect.TertiaryAxisRates) ? [] : [new FakeRate(0, 4)]);
    }

    // ---------- Properties ----------

    public AlignmentMode AlignmentMode { get { NeedConnected(); return AlignmentMode.GermanPolar; } }

    private (double Alt, double Az) AltAz()
    {
        static double R(double d) => d * Math.PI / 180;
        static double D(double r) => r * 180 / Math.PI;
        var (pra, pdec) = Pos();
        var ha = R(Values.HoursDiff(Lst, pra) * 15);
        var dec = R(pdec);
        var lat = R(_lat);
        var sinAlt = Math.Sin(dec) * Math.Sin(lat) + Math.Cos(dec) * Math.Cos(lat) * Math.Cos(ha);
        var alt = Math.Asin(sinAlt);
        var az = Math.Atan2(-Math.Sin(ha) * Math.Cos(dec), Math.Sin(dec) * Math.Cos(lat) - Math.Cos(dec) * Math.Sin(lat) * Math.Cos(ha));
        return (D(alt), (D(az) + 360) % 360);
    }

    private (double Ra, double Dec) FromAltAz(double alt, double az)
    {
        static double R(double d) => d * Math.PI / 180;
        static double D(double r) => r * 180 / Math.PI;
        var lat = R(_lat);
        var sinDec = Math.Sin(R(alt)) * Math.Sin(lat) + Math.Cos(R(alt)) * Math.Cos(lat) * Math.Cos(R(az));
        var dec = Math.Asin(sinDec);
        var ha = Math.Atan2(-Math.Sin(R(az)) * Math.Cos(R(alt)), Math.Sin(R(alt)) * Math.Cos(lat) - Math.Cos(R(alt)) * Math.Sin(lat) * Math.Cos(R(az)));
        return (Values.Wrap24(Lst - D(ha) / 15), D(dec));
    }

    public double Altitude { get { NeedConnected(); return AltAz().Alt; } }
    public double Azimuth { get { NeedConnected(); return Has(Defect.AzimuthFromSouth) ? (AltAz().Az + 180) % 360 : AltAz().Az; } }
    public double ApertureArea { get { NeedConnected(); return 0.0269; } }
    public double ApertureDiameter { get { NeedConnected(); return 0.2; } }
    public double FocalLength { get { NeedConnected(); return 1.0; } }
    public bool AtHome { get { NeedConnected(); return _atHome; } }
    public bool AtPark { get { NeedConnected(); return _atPark; } }
    public double Declination { get { NeedConnected(); return Reported().Dec; } }
    public double RightAscension
    {
        get
        {
            NeedConnected();
            var ra = Reported().Ra;
            if (!_strayNext) return ra;
            _strayNext = false;
            return Values.Wrap24(ra - 0.015 / 3600);
        }
    }

    /// <summary>The position as the properties report it: now, or with a defect, cached or jittering.</summary>
    /// <summary>SlewEndsWithPrecisionCorrection: when the final correction ends; until then Dec is off by what's left of 0.9".</summary>
    private DateTime _correctUntil;

    private const double CorrectionSeconds = 1.5;

    private (double Ra, double Dec) Reported()
    {
        var (ra, dec) = ReportedRaw();
        var left = (_correctUntil - DateTime.UtcNow).TotalSeconds;
        return left > 0 ? (ra, dec + 0.9 / 3600 * left / CorrectionSeconds) : (ra, dec);
    }

    private (double Ra, double Dec) ReportedRaw()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var f = System.Diagnostics.Stopwatch.Frequency;
        if (Has(Defect.StalePosition))
        {
            // Refreshed on a 300 ms timer: the position at the last tick (never before the latest change of anchor).
            var tick = now - now % (long)(0.3 * f);
            return Pos(Math.Max(tick, _anchor));
        }
        var (ra, dec) = Pos(now);
        if (Has(Defect.PositionJitter))
        {
            // The axis advances every 20 ms; RA is worked out with the sidereal time now: up to 20 ms of sidereal motion ahead.
            var sinceStep = (double)(now % (long)(0.02 * f)) / f;
            ra = Values.Wrap24(ra + sinceStep * 1.0027379 / 3600);
        }
        if (Has(Defect.SlowPositionWander))
        {
            var sinceSet = (double)(now - _raRateSetAt) / f;
            ra = Values.Wrap24(ra + 0.001 * Math.Sin(2 * Math.PI * sinceSet / 4) / 3600);
        }
        return (ra, dec);
    }

    /// <summary>
    /// Where the mount points now: tracking, the anchored position moved by any rate offsets; not tracking, a fixed
    /// hour angle (so RA follows sidereal time). Either way, MoveAxis turns the primary axis (RA) and the secondary
    /// axis (Dec) at their rates in degrees per second.
    /// </summary>
    private (double Ra, double Dec) Pos(long? at = null)
    {
        lock (_gate)
        {
            var when = at ?? System.Diagnostics.Stopwatch.GetTimestamp();
            var seconds = (double)(when - _anchor) / System.Diagnostics.Stopwatch.Frequency;
            var axisRa = _axisRates[0] * seconds / 15;
            var axisDec = _axisRates[1] * seconds;
            // The pulses' movement so far.
            foreach (var p in new[] { _pulseRa, _pulseDec })
            {
                if (p is not { } pulse) continue;
                var done = Math.Clamp((double)(when - pulse.Start) / System.Diagnostics.Stopwatch.Frequency, 0, pulse.Seconds);
                axisRa += pulse.Ra * done;
                axisDec += pulse.Dec * done;
            }
            if (!_tracking) return (Values.Wrap24(Lst - _ha + axisRa), Math.Clamp(_dec + axisDec, -90, 90));
            // Offsets only move the mount at sidereal rate.
            var offsets = _trackingRate == DriveRate.Sidereal || Has(Defect.RateOffsetAppliedAtOtherRates);
            var raRate = !offsets ? 0 : Has(Defect.RaOffsetScaled) ? _raRate * 1.5 : _raRate;
            var decRate = offsets ? _decRate : 0;
            if (Has(Defect.SmallRaOffsetRounded)) raRate = Math.Round(raRate / 0.002) * 0.002;
            // Seconds of RA per sidereal second, as the offset: 0.0007 s per SI second.
            if (Has(Defect.RaTrackingDrift)) raRate += 0.0007 / 1.0027379;
            return (Values.Wrap24(_ra + raRate * seconds * 1.0027379 / 3600 + axisRa),
                Math.Clamp(_dec + decRate * seconds / 3600 + axisDec, -90, 90));
        }
    }

    /// <summary>A pulse's remaining part from now, or null once it has ended (its movement so far is in the new anchor).</summary>
    private static (long Start, double Seconds, double Ra, double Dec)? Rest((long Start, double Seconds, double Ra, double Dec)? p, long now)
    {
        if (p is not { } pulse) return null;
        var done = Math.Max(0, (double)(now - pulse.Start) / System.Diagnostics.Stopwatch.Frequency);
        var left = pulse.Seconds - Math.Min(done, pulse.Seconds);
        if (left <= 0) return null;
        // Not started yet (a late start): unchanged. Under way: the rest, from now.
        return done > 0 ? (now, left, pulse.Ra, pulse.Dec) : pulse;
    }

    private void SetPos(double ra, double dec)
    {
        lock (_gate)
        {
            (_ra, _dec) = (Values.Wrap24(ra), dec);
            _ha = Values.HoursDiff(Lst, _ra);
            _anchor = System.Diagnostics.Stopwatch.GetTimestamp();
            // The new position includes the pulses' movement so far: keep only what's left of them.
            _pulseRa = Rest(_pulseRa, _anchor);
            _pulseDec = Rest(_pulseDec, _anchor);
        }
    }

    /// <summary>Makes the current position the anchor (before the tracking state or a rate changes).</summary>
    private void Bake()
    {
        var (ra, dec) = Pos();
        SetPos(ra, dec);
    }
    public double SiderealTime { get { NeedConnected(); return Lst; } }
    public EquatorialCoordinateType EquatorialSystem { get { NeedConnected(); return EquatorialCoordinateType.Topocentric; } }
    public bool Slewing
    {
        get
        {
            NeedConnected();
            return _slewing || _axisRates.Where((r, i) => !_coasting[i]).Any(r => r != 0) || (Has(Defect.SlewingDuringRateOffset) && (_raRate != 0 || _decRate != 0));
        }
    }
    public bool IsPulseGuiding { get { NeedConnected(); return DateTime.UtcNow < _pulseUntil; } }

    public double DeclinationRate
    {
        get { NeedConnected(); return OffsetRead(_decRate); }
        set
        {
            NeedConnected();
            if (!Has(Defect.DecRateAcceptedAtOtherRates)) NeedSidereal();
            CheckOffset(value);
            Bake();
            _decRate = value;
        }
    }

    public double RightAscensionRate
    {
        get { NeedConnected(); return OffsetRead(_raRate); }
        set
        {
            NeedConnected(); NeedSidereal(); CheckOffset(value); Bake(); _raRate = value; _strayNext = Has(Defect.StrayReadingAfterRateSet);
            _raRateSetAt = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    /// <summary>Offsets only apply at sidereal rate: at any other rate they read 0.</summary>
    private double OffsetRead(double offset)
        => _trackingRate == DriveRate.Sidereal || Has(Defect.RateOffsetKeptAtOtherRates) ? offset : 0;

    private void CheckOffset(double value)
    {
        if (value < 0 && Has(Defect.NegativeRateOffsetRefused)) throw new InvalidValueException("offset", value.ToString(), "0 or more");
    }

    private void NeedSidereal()
    {
        if (_trackingRate != DriveRate.Sidereal) throw new ASCOM.InvalidOperationException("offsets need sidereal rate");
    }
    public bool DoesRefraction { get { NeedConnected(); return _refraction; } set { NeedConnected(); _refraction = value; } }

    public double GuideRateDeclination
    {
        get { NeedConnected(); return _guideDec; }
        set { NeedConnected(); if (value < 0 && !Has(Defect.GuideRateNegativeAccepted)) throw new InvalidValueException("rate"); _guideDec = value; }
    }

    public double GuideRateRightAscension
    {
        get { NeedConnected(); return _guideRa; }
        set { NeedConnected(); if (value < 0 && !Has(Defect.GuideRateNegativeAccepted)) throw new InvalidValueException("rate"); _guideRa = value; }
    }

    public double SiteElevation
    {
        get { NeedConnected(); return _elevation; }
        set { NeedConnected(); if (value is < -300 or > 10000) throw new InvalidValueException("elevation"); _elevation = value; }
    }

    public double SiteLatitude
    {
        get { NeedConnected(); return _lat; }
        set
        {
            NeedConnected();
            if (value is < -90 or > 90 && !Has(Defect.AcceptsInvalidLatitude)) throw new InvalidValueException("latitude");
            _lat = value;
        }
    }

    public double SiteLongitude
    {
        get { NeedConnected(); return _lon; }
        set { NeedConnected(); if (value is < -180 or > 180) throw new InvalidValueException("longitude"); _lon = value; }
    }

    public short SlewSettleTime
    {
        get { NeedConnected(); return _settle; }
        set { NeedConnected(); if (value < 0) throw new InvalidValueException("settle"); _settle = value; }
    }

    public double TargetDeclination
    {
        get
        {
            NeedConnected();
            return _targetDec ?? (Has(Defect.TargetReadsZeroWhenNotSet) ? 0 : throw new ValueNotSetException("TargetDeclination"));
        }
        set { NeedConnected(); if (value is < -90 or > 90) throw new InvalidValueException("dec"); _targetDec = value; }
    }

    public double TargetRightAscension
    {
        get
        {
            NeedConnected();
            return _targetRa ?? (Has(Defect.TargetReadsZeroWhenNotSet) ? 0 : throw new ValueNotSetException("TargetRightAscension"));
        }
        set { NeedConnected(); if (value is < 0 or >= 24) throw new InvalidValueException("ra"); _targetRa = value; }
    }

    public bool Tracking
    {
        get { NeedConnected(); return _tracking; }
        set
        {
            NeedConnected();
            if (value && _atPark) throw new ParkedException("parked");
            // With the defect, CanSetTracking says false yet the setter works anyway.
            Bake();
            lock (_gate) _tracking = value;
        }
    }

    public DriveRate TrackingRate
    {
        get { NeedConnected(); return _trackingRate; }
        set
        {
            NeedConnected();
            if (!Enum.IsDefined(value) || (!SupportedRates.Contains(value) && !Has(Defect.TrackingRateAcceptsUnlisted)))
                throw new InvalidValueException("rate");
            Bake(); // the offsets stop (or start) moving the mount from here
            _trackingRate = value;
        }
    }

    private static readonly DriveRate[] SupportedRates = [DriveRate.Sidereal, DriveRate.Lunar, DriveRate.Solar];

    public ITrackingRates TrackingRates { get { NeedConnected(); return new FakeTrackingRates([.. SupportedRates]); } }

    public DateTime UTCDate
    {
        get { NeedConnected(); return Has(Defect.UtcDateIsLocalTime) ? DateTime.SpecifyKind(DateTime.UtcNow.AddHours(-5), DateTimeKind.Utc) : DateTime.UtcNow; }
        set => throw new PropertyNotImplementedException("UTCDate", true);
    }

    public PointingState SideOfPier
    {
        get
        {
            NeedConnected();
            if (_forcedSide is { } forced) return forced;
            var side = SideFor(Pos().Ra);
            return Has(Defect.SideOfPierReversed) ? (side == PointingState.Normal ? PointingState.ThroughThePole : PointingState.Normal) : side;
        }
        set
        {
            NeedConnected();
            CheckCanMove();
            StartMotion(200, () => { if (!Has(Defect.SideOfPierSetIgnored)) _forcedSide = value; });
        }
    }

    private PointingState SideFor(double ra) => Values.HoursDiff(Lst, ra) >= 0 ? PointingState.Normal : PointingState.ThroughThePole;

    public PointingState DestinationSideOfPier(double rightAscension, double declination)
    {
        NeedConnected();
        if (!Has(Defect.DestinationSideOfPierUnchecked)) CheckRaDec(rightAscension, declination);
        return SideFor(rightAscension);
    }

    // ---------- Motion ----------

    public void AbortSlew()
    {
        NeedConnected();
        if (_atPark) throw new ParkedException("parked");
        if (Has(Defect.ParkCantBeAborted) && _parking) return;
        StopMotion();
        Bake();
        Array.Clear(_axisRates);
    }

    private void Slew(double ra, double dec, bool async)
    {
        NeedConnected();
        if (!(Has(Defect.SlewAcceptsRa25) && ra == 25)) CheckRaDec(ra, dec);
        if (_atPark && !Has(Defect.ParkedMountSlews)) throw new ParkedException("parked");
        if (!_tracking)
        {
            if (!(async && Has(Defect.AsyncSlewTurnsTrackingOn))) throw new ASCOM.InvalidOperationException("not tracking");
            Bake();
            lock (_gate) _tracking = true;
        }
        _targetRa = ra;
        _targetDec = dec;
        _atHome = false;
        _atPark = false;
        // With the defect, the mount never changes side (though DestinationSideOfPier says it will).
        PointingState? keep = Has(Defect.NoMeridianFlip) ? SideOfPier : null;
        // The mount arrives after 300 ms, then settles (Slewing still true) for SlewSettleTime; with the defect a final
        // correction follows, creeping the last 0.9" for 1.5 s.
        var settle = Has(Defect.SettleTimeIgnored) ? 0 : _settle * 1000;
        var correct = Has(Defect.SlewEndsWithPrecisionCorrection) ? (int)(CorrectionSeconds * 1000) : 0;
        var still = Has(Defect.SlewEndsWithStillWait) ? 1500 : 0;
        StartMotion(300 + settle + correct + still, () =>
        {
            SetPos(ra, dec);
            _forcedSide = keep;
            if (correct > 0) _correctUntil = DateTime.UtcNow.AddMilliseconds(settle + correct);
        }, arriveMs: 300);
        if (!async || Has(Defect.AsyncSlewBlocks)) WaitForMotion(Has(Defect.AsyncSlewBlocks) ? 5500 : 0);
    }

    private void WaitForMotion(int atLeastMs = 0)
    {
        var start = DateTime.UtcNow;
        while (_slewing || (DateTime.UtcNow - start).TotalMilliseconds < atLeastMs) Thread.Sleep(20);
    }

    public void SlewToCoordinates(double rightAscension, double declination)
    {
        NeedSyncSlews("SlewToCoordinates");
        Slew(rightAscension, declination, false);
    }
    public void SlewToCoordinatesAsync(double rightAscension, double declination)
    {
        Slew(rightAscension, declination, true);
        if (Has(Defect.ServerDiesAtFirstSlew)) _dead = true;
    }

    public void SlewToTarget()
    {
        NeedSyncSlews("SlewToTarget");
        if (_targetRa is null || _targetDec is null) throw new ASCOM.InvalidOperationException("no target");
        Slew(_targetRa.Value, _targetDec.Value, false);
    }

    public void SlewToTargetAsync()
    {
        if (_targetRa is null || _targetDec is null) throw new ASCOM.InvalidOperationException("no target");
        Slew(_targetRa.Value, _targetDec.Value, true);
    }

    private void SlewAltAz(double azimuth, double altitude, bool async)
    {
        NeedConnected();
        if (altitude is < -90 or > 90 || azimuth is < 0 or >= 360) throw new InvalidValueException("alt/az");
        CheckCanMove();
        if (_tracking) throw new ASCOM.InvalidOperationException("tracking");
        var (ra, dec) = FromAltAz(altitude, azimuth);
        StartMotion(300, () => SetPos(ra, dec));
        if (!async) WaitForMotion();
    }

    public void SlewToAltAz(double azimuth, double altitude)
    {
        NeedSyncSlews("SlewToAltAz");
        SlewAltAz(azimuth, altitude, false);
    }
    public void SlewToAltAzAsync(double azimuth, double altitude) => SlewAltAz(azimuth, altitude, true);

    public void SyncToAltAz(double azimuth, double altitude)
    {
        NeedConnected();
        if (altitude is < -90 or > 90 || azimuth is < 0 or >= 360) throw new InvalidValueException("alt/az");
        CheckCanMove();
        if (_tracking) throw new ASCOM.InvalidOperationException("tracking");
        var (sra, sdec) = FromAltAz(altitude, azimuth);
        if (!Has(Defect.SyncToAltAzIgnored)) SetPos(sra, sdec);
    }

    public void SyncToCoordinates(double rightAscension, double declination)
    {
        NeedConnected();
        CheckRaDec(rightAscension, declination);
        CheckCanMove();
        if (!_tracking) throw new ASCOM.InvalidOperationException("not tracking");
        if (!Has(Defect.SyncIgnored)) SetPos(rightAscension, declination);
        if (!Has(Defect.SyncDoesntSetTargets)) (_targetRa, _targetDec) = (rightAscension, declination);
    }

    public void SyncToTarget()
    {
        if (_targetRa is null || _targetDec is null) throw new ASCOM.InvalidOperationException("no target");
        SyncToCoordinates(_targetRa.Value, _targetDec.Value);
    }

    public void MoveAxis(TelescopeAxis axis, double rate)
    {
        NeedConnected();
        if (!CanMoveAxis(axis)) throw new MethodNotImplementedException("MoveAxis");
        if (Math.Abs(rate) > 4) throw new InvalidValueException("rate");
        CheckCanMove();
        Bake();
        if (rate == 0 && Has(Defect.SlewingEndsBeforeAxisStops) && _axisRates[(int)axis] != 0)
        {
            // Reported stopped now, but the axis runs on for a second.
            var i = (int)axis;
            _coasting[i] = true;
            _ = Task.Delay(1000).ContinueWith(_ =>
            {
                lock (_gate)
                {
                    Bake();
                    _axisRates[i] = 0;
                    _coasting[i] = false;
                }
            });
            return;
        }
        _coasting[(int)axis] = false;
        if (!Has(Defect.NotSlewingDuringMoveAxis)) _axisRates[(int)axis] = Has(Defect.MoveAxisIgnoresSign) ? Math.Abs(rate) : rate;
        if (rate == 0 && Has(Defect.MoveAxisStopsTracking)) Tracking = false;
    }

    public void PulseGuide(GuideDirection direction, int duration)
    {
        NeedConnected();
        if (!Enum.IsDefined(direction))
            throw Has(Defect.PulseGuideDirectionUnchecked)
                ? new DriverException($"Exception of type 'System.ArgumentOutOfRangeException' was thrown. Parameter name: direction. Actual value was {(int)direction}.")
                : new InvalidValueException("direction");
        if (duration < 0) throw new InvalidValueException("duration");
        CheckCanMove();
        if (!Has(Defect.PulseGuideWhileNotTracking) && !_tracking) throw new ASCOM.InvalidOperationException("not tracking");
        if (_slewing && !Has(Defect.PulseGuideWhileSlewing)) throw new ASCOM.InvalidOperationException("slewing");
        var until = DateTime.UtcNow.AddMilliseconds(duration);
        if (until > _pulseUntil) _pulseUntil = until;
        // The pulse moves the mount over its duration: North raises Dec and East raises RA, on either side of the pier.
        var flip = Has(Defect.PulseGuideDecReversedThroughThePole) && SideOfPier == PointingState.ThroughThePole ? -1 : 1;
        var lost = Has(Defect.PulseLosesTime) ? 0.1 : 0;
        Bake();
        var start = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(lost * System.Diagnostics.Stopwatch.Frequency);
        var seconds = Math.Max(0, duration / 1000.0 - lost);
        lock (_gate)
        {
            switch (direction)
            {
                case GuideDirection.North: _pulseDec = (start, seconds, 0, flip * _guideDec); break;
                case GuideDirection.South: _pulseDec = (start, seconds, 0, -flip * _guideDec); break;
                case GuideDirection.East: _pulseRa = (start, seconds, _guideRa / 15, 0); break;
                case GuideDirection.West: _pulseRa = (start, seconds, -_guideRa / 15, 0); break;
            }
        }
    }

    public void FindHome()
    {
        NeedConnected();
        CheckCanMove();
        StartMotion(600, () => { _atHome = true; SetPos(Lst - 6, 90); });
    }

    private bool _parking;

    public void Park()
    {
        NeedConnected();
        if (_atPark) return;
        _parking = true;
        StartMotion(600, () => { SetPos(Lst - 6, 90); _atPark = true; _parking = false; Bake(); _tracking = false; });
    }

    public void Unpark()
    {
        NeedConnected();
        _atPark = false;
    }

    public void SetPark()
    {
        NeedConnected();
    }

    // ---------- Collections ----------

    private sealed class FakeRate(double minimum, double maximum) : IRate
    {
        public double Minimum { get; set; } = minimum;
        public double Maximum { get; set; } = maximum;
        public void Dispose() { }
    }

    private sealed class FakeAxisRates(List<IRate> rates) : IAxisRates
    {
        public int Count => rates.Count;
        public IRate this[int index] => rates[index - 1];
        public IEnumerator GetEnumerator() => rates.GetEnumerator();
        public void Dispose() { }
    }

    private sealed class FakeTrackingRates(List<DriveRate> rates) : ITrackingRates
    {
        public int Count => rates.Count;
        public DriveRate this[int index] => rates[index - 1];
        public IEnumerator GetEnumerator() => rates.GetEnumerator();
        public void Dispose() { }
    }
}
