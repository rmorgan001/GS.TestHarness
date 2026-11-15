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
    MoveAxisIgnoresSign
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
    private double _ra = 3, _dec = 45;      // position at _anchor (while tracking)
    private double _ha;                      // hour angle held while not tracking
    private long _anchor = System.Diagnostics.Stopwatch.GetTimestamp();
    private double? _targetRa, _targetDec;
    private bool _tracking = true, _atPark, _atHome;
    private bool _slewing; // a timed motion (slew, park, home) is under way
    private readonly double[] _axisRates = new double[3];
    private DateTime _pulseUntil;
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
        if (!IsConnected) throw new NotConnectedException("not connected");
    }

    private double Lst => Values.LocalSiderealTime(DateTime.UtcNow, _lon);

    private void StartMotion(int ms, Action atEnd)
    {
        int generation;
        lock (_gate)
        {
            _slewing = true;
            generation = ++_motionGeneration;
        }
        _ = Task.Delay(ms).ContinueWith(_ =>
        {
            lock (_gate)
            {
                if (generation != _motionGeneration) return;
                atEnd();
                _slewing = false;
            }
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
        get { lock (_clients) return _clients.Contains(Client); }
    }

    public bool Connected
    {
        get => IsConnected;
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
            else _clients.Remove(Client);
        }
    }
    public string Description => "Fake GEM for the GS.TestHarness tests";
    public string DriverInfo => "GS.TestHarness.Tests fake telescope";
    public string DriverVersion => "1.0";
    public short InterfaceVersion => (short)(Has(Defect.InterfaceVersion3) ? 3 : 4);
    public string Name => "Fake GEM";
    public IList<string> SupportedActions => [];

    public string Action(string actionName, string actionParameters) => throw new ActionNotImplementedException(actionName);
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
            throw Has(Defect.WrongErrorForInvalidAxis) ? new DriverException("bad axis") : new InvalidValueException("axis");
        return axis != TelescopeAxis.Tertiary;
    }

    public IAxisRates AxisRates(TelescopeAxis axis)
    {
        NeedConnected();
        if (axis is < TelescopeAxis.Primary or > TelescopeAxis.Tertiary) throw new InvalidValueException("axis");
        return new FakeAxisRates(axis == TelescopeAxis.Tertiary ? [] : [new FakeRate(0, 4)]);
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
    public double Declination { get { NeedConnected(); return Pos().Dec; } }
    public double RightAscension { get { NeedConnected(); return Pos().Ra; } }

    /// <summary>
    /// Where the mount points now: tracking, the anchored position moved by any rate offsets; not tracking, a fixed
    /// hour angle (so RA follows sidereal time). Either way, MoveAxis turns the primary axis (RA) and the secondary
    /// axis (Dec) at their rates in degrees per second.
    /// </summary>
    private (double Ra, double Dec) Pos()
    {
        lock (_gate)
        {
            var seconds = (double)(System.Diagnostics.Stopwatch.GetTimestamp() - _anchor) / System.Diagnostics.Stopwatch.Frequency;
            var axisRa = _axisRates[0] * seconds / 15;
            var axisDec = _axisRates[1] * seconds;
            if (!_tracking) return (Values.Wrap24(Lst - _ha + axisRa), Math.Clamp(_dec + axisDec, -90, 90));
            return (Values.Wrap24(_ra + _raRate * seconds * 1.0027379 / 3600 + axisRa),
                Math.Clamp(_dec + _decRate * seconds / 3600 + axisDec, -90, 90));
        }
    }

    private void SetPos(double ra, double dec)
    {
        lock (_gate)
        {
            (_ra, _dec) = (Values.Wrap24(ra), dec);
            _ha = Values.HoursDiff(Lst, _ra);
            _anchor = System.Diagnostics.Stopwatch.GetTimestamp();
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
    public bool Slewing { get { NeedConnected(); return _slewing || _axisRates.Any(r => r != 0); } }
    public bool IsPulseGuiding { get { NeedConnected(); return DateTime.UtcNow < _pulseUntil; } }

    public double DeclinationRate { get { NeedConnected(); return _decRate; } set { NeedConnected(); NeedSidereal(); Bake(); _decRate = value; } }
    public double RightAscensionRate { get { NeedConnected(); return _raRate; } set { NeedConnected(); NeedSidereal(); Bake(); _raRate = value; } }

    private void NeedSidereal()
    {
        if (_trackingRate != DriveRate.Sidereal) throw new ASCOM.InvalidOperationException("offsets need sidereal rate");
    }
    public bool DoesRefraction { get { NeedConnected(); return _refraction; } set { NeedConnected(); _refraction = value; } }

    public double GuideRateDeclination
    {
        get { NeedConnected(); return _guideDec; }
        set { NeedConnected(); if (value < 0) throw new InvalidValueException("rate"); _guideDec = value; }
    }

    public double GuideRateRightAscension
    {
        get { NeedConnected(); return _guideRa; }
        set { NeedConnected(); if (value < 0) throw new InvalidValueException("rate"); _guideRa = value; }
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
        set { NeedConnected(); if (!Enum.IsDefined(value)) throw new InvalidValueException("rate"); _trackingRate = value; }
    }

    public ITrackingRates TrackingRates { get { NeedConnected(); return new FakeTrackingRates([DriveRate.Sidereal, DriveRate.Lunar, DriveRate.Solar]); } }

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
            StartMotion(200, () => _forcedSide = value);
        }
    }

    private PointingState SideFor(double ra) => Values.HoursDiff(Lst, ra) >= 0 ? PointingState.Normal : PointingState.ThroughThePole;

    public PointingState DestinationSideOfPier(double rightAscension, double declination)
    {
        NeedConnected();
        CheckRaDec(rightAscension, declination);
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
        if (!_tracking) throw new ASCOM.InvalidOperationException("not tracking");
        _targetRa = ra;
        _targetDec = dec;
        _atHome = false;
        _atPark = false;
        StartMotion(300 + (Has(Defect.SettleTimeIgnored) ? 0 : _settle * 1000), () => { SetPos(ra, dec); _forcedSide = null; });
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
    public void SlewToCoordinatesAsync(double rightAscension, double declination) => Slew(rightAscension, declination, true);

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
        SetPos(sra, sdec);
    }

    public void SyncToCoordinates(double rightAscension, double declination)
    {
        NeedConnected();
        CheckRaDec(rightAscension, declination);
        CheckCanMove();
        if (!_tracking) throw new ASCOM.InvalidOperationException("not tracking");
        SetPos(rightAscension, declination);
        (_targetRa, _targetDec) = (rightAscension, declination);
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
        if (!Has(Defect.NotSlewingDuringMoveAxis)) _axisRates[(int)axis] = Has(Defect.MoveAxisIgnoresSign) ? Math.Abs(rate) : rate;
        if (rate == 0 && Has(Defect.MoveAxisStopsTracking)) Tracking = false;
    }

    public void PulseGuide(GuideDirection direction, int duration)
    {
        NeedConnected();
        if (!Enum.IsDefined(direction)) throw new InvalidValueException("direction");
        if (duration < 0) throw new InvalidValueException("duration");
        CheckCanMove();
        if (!Has(Defect.PulseGuideWhileNotTracking) && !_tracking) throw new ASCOM.InvalidOperationException("not tracking");
        if (_slewing) throw new ASCOM.InvalidOperationException("slewing");
        _pulseUntil = DateTime.UtcNow.AddMilliseconds(duration);
        // The whole pulse's movement at once: North raises Dec and East raises RA, on either side of the pier.
        var seconds = duration / 1000.0;
        var flip = Has(Defect.PulseGuideDecReversedThroughThePole) && SideOfPier == PointingState.ThroughThePole ? -1 : 1;
        var (ra, dec) = Pos();
        switch (direction)
        {
            case GuideDirection.North: dec += flip * _guideDec * seconds; break;
            case GuideDirection.South: dec -= flip * _guideDec * seconds; break;
            case GuideDirection.East: ra += _guideRa * seconds / 15; break;
            case GuideDirection.West: ra -= _guideRa * seconds / 15; break;
        }
        SetPos(ra, dec);
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
