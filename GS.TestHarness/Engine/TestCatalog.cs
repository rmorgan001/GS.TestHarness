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

using System.Text.RegularExpressions;

namespace GS.TestHarness.Engine;

/// <summary>A source a check is based on. <see cref="Url"/> is null for one that isn't online (a rule of this harness).</summary>
public sealed record Reference(string Key, string Title, string? Url);

/// <summary>
/// One check, as the Tests page lists it. <see cref="Id"/> is the finding ID without the group, and may hold
/// placeholders such as <c>{axis}</c> that stand for one word (Primary, Secondary...), or, ending in <c>*</c>
/// (<c>{slew*}</c>), for a dotted name such as SlewToCoordinatesAsync.West.
/// </summary>
public sealed record CatalogEntry(string Group, string Id, string Objective, string Method, string Verification, IReadOnlyList<string> Refs)
{
    private readonly Regex _pattern = new("^" + Regex.Replace(Regex.Escape($"{Group}.{Id}"), @"\\\{([^}]+)}",
        m => m.Groups[1].Value.EndsWith('*') ? "[A-Za-z0-9.]+" : "[A-Za-z0-9]+") + "$");

    /// <summary>The ID as shown: placeholders without their <c>*</c>.</summary>
    public string DisplayId => Id.Replace("*}", "}");

    /// <summary>Whether a finding's full ID (Group.Id) is this check.</summary>
    public bool Matches(string findingId) => _pattern.IsMatch(findingId);
}

/// <summary>A check group on the Tests page: what it covers and when it runs.</summary>
public sealed record CatalogGroup(string Name, string Summary, string RunsWhen);

/// <summary>
/// Every check the harness makes: its objective, how it is tested, how the result is judged, and what it is based on.
/// A unit test (CatalogTests) runs the checks against the fake mount and fails if a finding has no entry here.
/// </summary>
public static class TestCatalog
{
    private const string V4Url = "https://ascom-standards.org/newdocs/telescope.html";
    private const string ConformUUrl = "https://github.com/ASCOMInitiative/ConformU/blob/main/ConformU/Conform/TelescopeTester.cs";

    public static readonly IReadOnlyList<Reference> References =
    [
        new("V4", "ASCOM ITelescopeV4 interface documentation", V4Url),
        new("Async", "ASCOM: asynchronous operations (Connect, slews, Connecting / Slewing)", "https://ascom-standards.org/newdocs/async-faq.html"),
        new("PierSide", "ASCOM: pointing state and SideOfPier", "https://ascom-standards.org/newdocs/ptgstate-faq.html"),
        new("Offsets", "ASCOM: tracking offsets (RightAscensionRate, DeclinationRate)", "https://ascom-standards.org/newdocs/trkoffset-faq.html"),
        new("MoveAxisFaq", "ASCOM: MoveAxis", "https://ascom-standards.org/newdocs/moveaxis-faq.html"),
        new("SlewFaq", "ASCOM: telescope slewing", "https://ascom-standards.org/newdocs/scope-slew-faq.html"),
        new("ConformU", "ConformU telescope tests (TelescopeTester.cs)", ConformUUrl),
        new("ConformUProtocol", "ConformU Alpaca protocol tests (AlpacaProtocolTestManager.cs)",
            "https://github.com/ASCOMInitiative/ConformU/blob/main/ConformU/AlpacaProtocolTestManager.cs"),
        new("Alpaca", "ASCOM Alpaca API (device and management API)", "https://ascom-standards.org/api/"),
        new("AlpacaRef", "ASCOM Alpaca API Reference (protocol rules, discovery)", null),
        new("Registration", "ASCOM Platform driver registration (ASCOM Profile, COM ProgID and CLSID)", null),
        new("Harness", "GS Test Harness rule: good practice, not an ASCOM requirement", null),
    ];

    public static Reference Reference(string key) => References.First(r => r.Key == key);

    public static readonly IReadOnlyList<CatalogGroup> Groups =
    [
        new("Run", "Run-level findings: the site the run used, groups skipped and runs stopped.", "Always"),
        new("Site", "Moving the mount to the run's site and back.", "A run at a saved northern or southern site"),
        new("Connection", "Connecting, and what the device says it is.", "Always"),
        new("Protocol", "The Alpaca HTTP protocol under the interface.", "Alpaca servers, unless turned off"),
        new("Common", "Members every ASCOM device has.", "Always"),
        new("Capabilities", "The Can* properties; their answers decide what later groups expect.", "Always"),
        new("Properties", "Reading every property.", "Always"),
        new("Methods", "Methods that don't move the mount.", "Always"),
        new("Settings", "Writing properties.", "Unless settings writes are turned off"),
        new("Motion", "Everything that moves the mount.", "Only with motion allowed"),
        new("ParkHome", "FindHome, Park, Unpark, SetPark and what a parked mount must refuse.", "Only with motion allowed (SetPark has its own switch)"),
        new("Tracking", "Tracking measured over time.", "Extended checks with motion allowed"),
        new("Clients", "Several clients at once.", "Extended checks"),
        new("Registration", "How a COM driver is registered, read from the registry.", "COM (GSServer) only"),
        new("ConformU", "Following up a ConformU log: each problem it found, reproduced or not, and generated checks for members no other group covers.", "Only in a run started from a ConformU log"),
        new("Performance", "How quickly the run's calls answered.", "Always"),
        new("Disconnected", "Disconnecting, and what a disconnected device must refuse.", "Unless disconnected checks are turned off"),
    ];

    // Shorthand for the entries below.
    private static CatalogEntry E(string group, string id, string objective, string method, string verification, params string[] refs)
        => new(group, id, objective, method, verification, refs);

    private const string NotImpl = "Must raise MethodNotImplementedException / PropertyNotImplementedException (Alpaca error 0x400). Any other error, or no error, fails.";
    private const string Invalid = "Must raise InvalidValueException (0x401). No error, another error, a timeout or HTTP 400 fails.";
    private const string InvalidOp = "Must raise InvalidOperationException (0x40B). No error or another error fails.";
    private const string ParkedErr = "Must raise ParkedException (0x408). No error or another error fails.";
    private const string NoError = "Passes when the call returns without an error.";
    private const string Position = "Pass within 10\" of the expected position; Info up to 1°; Fail beyond 1° (ConformU's tolerances).";

    public static readonly IReadOnlyList<CatalogEntry> Entries = Build();

    private static List<CatalogEntry> Build()
    {
        var list = new List<CatalogEntry>
        {
            // ---------- Run and Site ----------
            E("{group}", "Group", "Say why a whole group didn't run.", "Each group's skip rule is checked before it runs (motion off, not Alpaca, not COM...).", "Skipped, with the reason. Not a problem.", "Harness"),
            E("{group}", "Stopped", "Record a run that couldn't go on.", "A group raises a stop (e.g. the device can't be reached or won't connect).", "Error: the rest of the run is abandoned and the reason given.", "Harness"),
            E("{group}", "CommunicationLost", "Stop cleanly when the device goes away, instead of failing every remaining check.", "After connecting, a call that fails with a lost-connection error (COM RPC server unavailable or disconnected, Alpaca connection refused or reset), or three timeouts in a row, is followed by a short Connected read to confirm the device has gone.", "Error, with the last call's error and the confirming read's error. The groups that need the device are skipped; Performance still summarises the run; the findings so far are kept.", "Harness"),
            E("{group}", "Unexpected", "Record a check that crashed.", "Any unexpected exception inside a group is caught.", "Error, with the exception; the remaining groups still run.", "Harness"),
            E("Site", "Apply", "Run the checks at a chosen site.", "Reads the server's own SiteLatitude/Longitude/Elevation, then writes the site's values after connecting.", "Info when set; Error if the server's site can't be read or the new site isn't accepted.", "V4", "Harness"),
            E("Site", "Restore", "Leave the server as it was found.", "Writes the server's own site back at the end of the run.", "Info when restored; Error if it couldn't be put back.", "Harness"),

            // ---------- Connection ----------
            E("Connection", "Connected.Read", "Connected must always be readable.", "Reads Connected before connecting.", "Pass if it reads; Fail on an error (Error and the run stops if the device can't be reached).", "V4"),
            E("Connection", "Connect", "Connect() starts connecting and returns; the device ends up connected.", "Calls Connect(), times it, waits for Connecting to go false, reads Connected.", "Fail if not implemented, if it errors or Connected isn't true; Warning if Connect() itself took over 5 s.", "V4", "Async"),
            E("Connection", "Connect.ConnectingOnReturn", "\"On return, Connecting must be True unless already connected.\"", "Reads Connecting and Connected straight after Connect() returns.", "Fail if both are false.", "V4", "Async"),
            E("Connection", "Connecting.AfterConnect", "Connecting goes false when connecting has finished.", "Polls Connecting for up to 30 s after Connect().", "Pass when it goes false; Fail if still true after 30 s.", "V4", "Async"),
            E("Connection", "Connect.WhenConnected", "Connect() on a connected device is harmless.", "Calls Connect() again.", NoError, "V4"),
            E("Connection", "Connected.SetTrueWhenConnected", "Older clients set Connected = true; it must be harmless when connected.", "Writes Connected = true while connected.", NoError, "V4"),
            E("Connection", "{member}.Read", "Identity properties are mandatory and not empty.", "Reads Name, Description, DriverInfo, DriverVersion and InterfaceVersion.", "Fail on an error or an empty value; InterfaceVersion must be 1 to 4.", "V4", "ConformU"),
            E("Connection", "Description.Length", "Description fits in a FITS header.", "Measures Description.", "Warning over 64 characters.", "V4"),
            E("Connection", "DriverVersion.Format", "DriverVersion \"must be in the form 'n.n'\".", "Matches DriverVersion against major.minor.", "Warning if it isn't n.n.", "V4"),
            E("Connection", "InterfaceVersion.Current", "Platform 7 members need InterfaceVersion 4.", "Reads InterfaceVersion.", "Warning below 4.", "V4"),

            // ---------- Protocol (Alpaca) ----------
            E("Protocol", "Management.ApiVersions", "The management API lists the supported API versions.", "GET /management/apiversions.", "Fail unless the reply's Value includes 1.", "Alpaca", "ConformUProtocol"),
            E("Protocol", "Management.Description", "The server describes itself.", "GET /management/v1/description.", "Fail unless Value has ServerName, Manufacturer, ManufacturerVersion and Location.", "Alpaca"),
            E("Protocol", "Management.ConfiguredDevices", "Every device is listed with its identity.", "GET /management/v1/configureddevices.", "Fail unless each device has DeviceName, DeviceType, DeviceNumber and UniqueID, and the device under test is there.", "Alpaca"),
            E("Protocol", "Management.UniqueIdStable", "A device's UniqueID never changes.", "Compares the UniqueID with the last saved run of the same device.", "Fail if it changed; Info on the first run.", "AlpacaRef"),
            E("Protocol", "Management.UniqueIds", "No two devices share a UniqueID.", "Looks for duplicates in configureddevices.", "Fail on a duplicate.", "AlpacaRef"),
            E("Protocol", "ClientTransactionID.Echo", "The reply echoes the client's transaction ID.", "GET name with ClientTransactionID=4242.", "Fail unless the reply's ClientTransactionID is 4242.", "Alpaca", "ConformUProtocol"),
            E("Protocol", "ServerTransactionID.Increases", "Server transaction IDs are positive and increase.", "Two GETs in a row.", "Fail unless the second ServerTransactionID is greater than the first (and above 0).", "Alpaca", "ConformUProtocol"),
            E("Protocol", "Query.CaseInsensitive", "GET parameter names are case-insensitive.", "GET with clientid / clienttransactionid in lower case.", "Fail unless the transaction ID is still echoed.", "AlpacaRef", "ConformUProtocol"),
            E("Protocol", "ClientTransactionID.Missing", "A missing transaction ID isn't an error.", "GET without ClientTransactionID.", "Fail on HTTP 400 or an ASCOM error.", "AlpacaRef", "ConformUProtocol"),
            E("Protocol", "ClientTransactionID.Invalid", "A malformed transaction ID isn't an error.", "GET with ClientTransactionID=abc.", "Fail on an HTTP error.", "AlpacaRef", "ConformUProtocol"),
            E("Protocol", "Reply.Json", "Replies are JSON.", "GET rightascension.", "Fail unless the body parses as JSON.", "Alpaca"),
            E("Protocol", "Reply.Shape", "Replies have the standard fields, correctly cased.", "Looks for ClientTransactionID, ServerTransactionID, ErrorNumber, ErrorMessage and Value.", "Fail for a missing or wrongly cased field.", "Alpaca", "ConformUProtocol"),
            E("Protocol", "Endpoint.{member}", "Interface version 4 adds the devicestate and connecting endpoints.", "GET each endpoint.", "Fail if it isn't there.", "Alpaca", "V4"),
            E("Protocol", "Errors.InvalidValue", "ASCOM errors are HTTP 200 replies with an ErrorNumber.", "GET canmoveaxis?Axis=9.", "Fail unless HTTP 200 with ErrorNumber 0x401.", "AlpacaRef", "ConformUProtocol"),
            E("Protocol", "Errors.Message", "An error reply explains itself.", "The same reply's ErrorMessage.", "Fail if it is empty.", "AlpacaRef"),
            E("Protocol", "BadRequest.DeviceNumber", "A device that isn't configured is an HTTP error.", "GET telescope 99.", "Pass on HTTP 400 (other 4xx: warning); Fail on 200.", "AlpacaRef", "ConformUProtocol"),
            E("Protocol", "BadRequest.MissingParameter", "A PUT without its parameter is HTTP 400.", "PUT targetrightascension without TargetRightAscension.", "Pass on HTTP 400; Fail otherwise.", "AlpacaRef", "ConformUProtocol"),
            E("Protocol", "BadRequest.BadNumber", "A parameter that can't be parsed is HTTP 400.", "PUT TargetRightAscension=abc.", "Pass on HTTP 400; Fail otherwise.", "AlpacaRef", "ConformUProtocol"),
            E("Protocol", "BadRequest.BadBoolean", "A boolean that can't be parsed is HTTP 400.", "PUT Tracking=maybe.", "Pass on HTTP 400; Fail otherwise.", "AlpacaRef", "ConformUProtocol"),
            E("Protocol", "BadRequest.PutCasing", "PUT body names are case-sensitive: a wrongly cased name is missing.", "PUT targetrightascension=12 (lower case name).", "Pass on HTTP 400; Fail if accepted.", "AlpacaRef", "ConformUProtocol"),
            E("Protocol", "BadRequest.UnknownMember", "An unknown member is an HTTP error.", "GET nosuchmember.", "Pass on a 4xx; Warning otherwise.", "AlpacaRef", "ConformUProtocol"),
            E("Protocol", "BadRequest.DecimalComma", "Numbers use the invariant culture.", "PUT TargetRightAscension=12,5.", "Fail if it is accepted as a number.", "AlpacaRef"),
            E("Protocol", "BadRequest.Method{method}", "Only GET and PUT are used.", "POST and DELETE to a member.", "Pass on a 4xx; Fail on a 5xx; Warning on 2xx.", "AlpacaRef", "ConformUProtocol"),
            E("Protocol", "UTCDate.Format", "UTCDate is ISO 8601 with a Z suffix.", "GET utcdate and inspects the string.", "Fail without the Z or if it doesn't parse.", "Alpaca"),
            E("Protocol", "Setup.{page}", "Alpaca servers provide HTML setup pages.", "GET /setup and /setup/v1/telescope/{n}/setup.", "Warning if a page is missing.", "AlpacaRef"),
            E("Protocol", "Discovery", "Clients find the server by discovery.", "Summary of the discovery checks.", "As the discovery checks below.", "AlpacaRef"),
            E("Protocol", "Discovery.Broadcast", "The server answers IPv4 discovery.", "Broadcasts \"alpacadiscovery1\" on UDP 32227.", "Fail unless a reply gives this server's port.", "AlpacaRef"),
            E("Protocol", "Discovery.IPv6", "The server answers IPv6 discovery.", "Sends to the ff12::00a1:9aca multicast group.", "Info when there's no reply (IPv6 discovery is less common).", "AlpacaRef"),

            // ---------- Common ----------
            E("Common", "SupportedActions.Read", "SupportedActions is mandatory and a clean list.", "Reads SupportedActions.", "Fail on an error, null, or an empty name.", "V4", "ConformU"),
            E("Common", "Action.Unknown", "An action that isn't supported is refused properly.", "Calls Action with a made-up name.", "Must raise ActionNotImplementedException (0x40C); NotImplemented is also accepted when SupportedActions is empty.", "V4", "ConformU"),
            E("Common", "{member}.Implemented", "Record whether the raw command methods exist.", "Calls CommandBlind/Bool/String with an empty command.", "Info only: these are optional.", "V4"),
            E("Common", "Connecting.Read", "Connecting is false once connected.", "Reads Connecting.", "Fail if true or not readable.", "V4", "Async"),
            E("Common", "DeviceState.Read", "DeviceState (ITelescopeV4) can be read.", "Reads DeviceState (skipped below interface version 4).", "Fail on an error or null.", "V4"),
            E("Common", "DeviceState.Duplicates", "Each property appears once.", "Groups the entries by name.", "Fail for a name listed twice.", "V4", "ConformU"),
            E("Common", "DeviceState.TimeStamp", "DeviceState says when it was read.", "Looks for a TimeStamp entry.", "Warning if missing.", "V4"),
            E("Common", "DeviceState.Contents", "Every operational property the device implements is there.", "Compares the entries with Altitude, AtHome, AtPark, Azimuth, Declination, IsPulseGuiding, RightAscension, SideOfPier, SiderealTime, Slewing, Tracking, UTCDate (those that read).", "Fail for a missing one.", "V4", "ConformU"),
            E("Common", "DeviceState.Values", "DeviceState agrees with the individual properties.", "Compares Tracking, AtPark, AtHome, Declination, RightAscension and SiderealTime with reads made just after.", "Fail on a mismatch beyond the time between reads.", "V4"),

            // ---------- Capabilities ----------
            E("Capabilities", "{member}.Read", "Every Can* property is mandatory.", "Reads each Can* property.", "Fail on an error.", "V4", "ConformU"),
            E("Capabilities", "CanMoveAxis.{axis}", "CanMoveAxis is mandatory for all three axes.", "Calls CanMoveAxis(0, 1, 2).", "Fail on an error.", "V4", "ConformU"),
            E("Capabilities", "CanMoveAxis.InvalidAxis", "An invalid axis is refused.", "Calls CanMoveAxis(3).", Invalid, "V4", "ConformU"),
            E("Capabilities", "Consistency.Unpark", "CanUnpark without CanPark makes no sense.", "Compares the two.", "Warning when CanUnpark is true and CanPark false.", "ConformU"),
            E("Capabilities", "Consistency.SetPark", "CanSetPark without CanPark makes no sense.", "Compares the two.", "Warning.", "Harness"),
            E("Capabilities", "Consistency.SlewPairs", "A COM driver implements synchronous and asynchronous slews together.", "Compares CanSlew with CanSlewAsync and CanSlewAltAz with CanSlewAltAzAsync (COM only).", "Fail if they differ.", "ConformU"),
            E("Capabilities", "Consistency.SyncAltAz", "Note an unusual combination.", "CanSyncAltAz true with CanSync false.", "Info.", "Harness"),

            // ---------- Properties ----------
            E("Properties", "{member}.Read", "Every property is implemented when required, with a value in range.", "Reads each property once: mandatory ones must work; optional ones may be not implemented; some are required only when a capability is true.", "Fail for a missing mandatory property, an error, or a value out of range (e.g. Declination outside ±90°, RightAscension outside 0–24 h, TrackingRates without driveSidereal).", "V4", "ConformU"),
            E("Properties", "IsPulseGuiding.NotImplemented", "Without CanPulseGuide, IsPulseGuiding isn't there.", "Reads IsPulseGuiding when CanPulseGuide is false.", NotImpl, "V4", "ConformU"),
            E("Properties", "{member}.Zero", "An offset that can't be set can only be 0.", "Reads RightAscensionRate / DeclinationRate when its CanSet… is false.", "Warning if it isn't 0 (ConformU reports an issue; the documentation doesn't say so outright).", "ConformU"),
            E("Properties", "SiderealTime.Value", "SiderealTime is the local sidereal time at SiteLongitude.", "Compares it with mean sidereal time computed from this computer's clock and SiteLongitude.", "Pass within 2 s; Info up to an hour; Fail beyond an hour (sign of longitude, or a time zone).", "V4", "ConformU"),
            E("Properties", "{member}.NotSet", "Targets report that they have no value until set.", "Reads TargetRightAscension and TargetDeclination before setting them.", "Pass on InvalidOperationException (or ValueNotSetException); Info if they already have a value; Fail for another error.", "V4", "ConformU"),
            E("Properties", "TrackingRates.HasCurrent", "TrackingRates lists the current rate.", "Looks for TrackingRate in TrackingRates.", "Fail if missing.", "V4"),
            E("Properties", "UTCDate.Value", "UTCDate is UTC.", "Compares UTCDate with this computer's clock.", "Pass within 10 s; Warning otherwise (a whole number of half hours means local time).", "V4", "ConformU"),
            E("Properties", "AltAz.Consistent", "Altitude/Azimuth describe the same place as RA/Dec.", "Converts RA/Dec with SiderealTime and SiteLatitude and compares.", "Pass within 0.5°; Warning to 2°; Fail beyond (azimuth 180° out is called out).", "V4", "Harness"),

            // ---------- Methods ----------
            E("Methods", "AxisRates.{axis}", "AxisRates is mandatory and consistent with CanMoveAxis.", "Calls AxisRates for each axis.", "Fail on an error, null, rates for an axis that can't move (or none for one that can), negative rates, Minimum above Maximum, or overlapping ranges.", "V4", "ConformU"),
            E("Methods", "AxisRates.InvalidAxis", "An invalid axis is refused.", "Calls AxisRates(3).", Invalid, "V4", "ConformU"),
            E("Methods", "DestinationSideOfPier.Read", "DestinationSideOfPier works (or is not implemented).", "Calls it two hours either side of the meridian.", "Fail on an error; Warning on a German mount that doesn't implement it; Info otherwise.", "V4", "PierSide"),
            E("Methods", "DestinationSideOfPier.Sides", "It returns the pointing state, not the physical side.", "Two hours east of the meridian and two hours west, on a German mount.", "Pass for pierWest (east) and pierEast (west); Fail for pierUnknown, the same side twice, or reversed.", "V4", "PierSide", "ConformU"),
            E("Methods", "DestinationSideOfPier.InvalidRa", "Invalid coordinates are refused.", "DestinationSideOfPier(25, dec).", Invalid, "V4"),
            E("Methods", "DestinationSideOfPier.InvalidDec", "Invalid coordinates are refused.", "DestinationSideOfPier(ra, 91).", Invalid, "V4"),

            // ---------- Settings ----------
            E("Settings", "{member}.Write", "A writable property accepts the value it already has (and is put back afterwards).", "Reads the property and writes the same value back (sites, SlewSettleTime, guide rates, rate offsets, Tracking, DoesRefraction, targets).", "Pass on no error; Info if read-only (allowed); Fail on another error. Targets must also read back what was written.", "V4", "ConformU"),
            E("Settings", "{member}.ReadBack", "A property stores exactly what was written.", "Reads it again after writing it back.", "Fail if it changed (rounding or unit conversion).", "V4", "ConformU"),
            E("Settings", "{member}.InvalidLow", "Values below the valid range are refused.", "Writes e.g. latitude -91, longitude -181, elevation -301, settle time -1, guide rate -1, TargetRightAscension -1, TargetDeclination -91. Anything accepted is put back.", Invalid, "V4", "ConformU"),
            E("Settings", "{member}.InvalidHigh", "Values above the valid range are refused.", "Writes e.g. latitude 91, longitude 181, elevation 10001, TargetRightAscension 25, TargetDeclination 91.", Invalid, "V4", "ConformU"),
            E("Settings", "{member}.NotImplemented", "A write a false capability rules out is refused.", "Writes the current value to guide rates, rate offsets, Tracking or SideOfPier when the capability is false.", NotImpl, "V4", "ConformU"),
            E("Settings", "TrackingRate.Set{rate}", "Every rate in TrackingRates can be set.", "Sets each listed rate and reads it back.", "Fail if refused or it reads back differently.", "V4", "ConformU"),
            E("Settings", "TrackingRate.InvalidLow", "A value that isn't a DriveRate is refused.", "TrackingRate = -1 (ConformU's value).", Invalid, "V4", "ConformU"),
            E("Settings", "TrackingRate.InvalidHigh", "A value that isn't a DriveRate is refused.", "TrackingRate = 5 (ConformU's value).", Invalid, "V4", "ConformU"),
            E("Settings", "TrackingRate.Unsupported{rate}", "\"…or if the requested rate is not supported by the mount\".", "Writes each DriveRate that TrackingRates doesn't list.", Invalid, "V4", "ConformU"),
            E("Settings", "UTCDate.Write", "UTCDate is writable (or not implemented).", "Writes an hour ahead, reads it back, then sets the real time again.", "Pass within 5 s; Info if not implemented; Fail otherwise.", "V4", "ConformU"),
            E("Settings", "Restore", "Leave the mount as found.", "Puts back every value a faulty server accepted.", "Error if something couldn't be put back.", "Harness"),

            // ---------- Motion: capabilities that are false ----------
            E("Motion", "{member}.NotImplemented", "A method a false capability rules out isn't there.", "Calls each slew, sync, PulseGuide, FindHome, Park or Unpark whose capability is false (a harmless target: where the mount already is).", NotImpl + " Anything that moved is stopped and a park undone.", "V4", "ConformU"),
            E("Motion", "MoveAxis.{axis}.NotImplemented", "MoveAxis on an axis that can't move is refused.", "MoveAxis(axis, 0.1) where CanMoveAxis is false.", NotImpl, "V4", "ConformU"),
            E("Motion", "Prepare", "The mount can be moved.", "Unparks a parked mount first.", "Skipped/Error if it can't be unparked.", "Harness"),

            // ---------- Motion: slews ----------
            E("Motion", "SlewToCoordinatesAsync.West", "An asynchronous slew reaches its target.", "Slews 3 h west of the meridian at a declination high at the site.", Position + " Fail on an error or if Slewing never goes false.", "V4", "SlewFaq", "ConformU"),
            E("Motion", "SlewToCoordinatesAsync.East", "An asynchronous slew reaches its target on the other side.", "Slews 3 h east of the meridian (through the pole on a German mount).", Position + " Fail on an error or if Slewing never goes false.", "V4", "SlewFaq", "ConformU"),
            E("Motion", "SlewToTargetAsync", "A slew to the target properties reaches it.", "Sets the targets 3 h west and calls SlewToTargetAsync.", Position, "V4", "ConformU"),
            E("Motion", "SlewToCoordinates", "The synchronous slew (COM) reaches its target.", "SlewToCoordinates 3 h east.", Position, "V4", "ConformU"),
            E("Motion", "SlewToTarget", "The synchronous slew to target (COM) reaches it.", "Sets the targets and calls SlewToTarget.", Position, "V4", "ConformU"),
            E("Motion", "{slew*}.SlewingOnReturn", "\"Slewing must be True immediately upon returning\" from an asynchronous slew.", "Reads Slewing as soon as the call returns.", "Fail if false.", "V4", "Async", "ConformU"),
            E("Motion", "{slew*}.Returns", "Asynchronous slews return at once; synchronous ones only when finished.", "Times the call and reads Slewing after it.", "Fail if an async slew takes over 5 s, or a sync slew returns with Slewing true.", "V4", "Async"),
            E("Motion", "{slew*}.Targets", "A slew sets the targets to where it went.", "Reads TargetRightAscension/Declination after the slew.", "Fail if they aren't the slew's coordinates.", "V4", "ConformU"),
            E("Motion", "{slew*}.DestinationSideOfPier", "DestinationSideOfPier predicts the side the slew ends on.", "Calls it before the slew and compares with SideOfPier after.", "Fail on a mismatch.", "V4", "PierSide", "ConformU"),
            E("Motion", "{slew*}.SideOfPier", "SideOfPier is the pointing state after a slew.", "3 h west must be pierEast (Normal); 3 h east pierWest (ThroughThePole), on a German mount.", "Pass as expected; Warning for pierUnknown; Fail if reversed.", "V4", "PierSide", "ConformU"),
            E("Motion", "{member}.Deprecated", "Alpaca drivers must not implement the deprecated synchronous slews.", "Calls SlewToCoordinates, SlewToTarget and SlewToAltAz on an Alpaca device.", "Pass on MethodNotImplemented; Warning otherwise.", "V4"),
            E("Motion", "SlewSettleTime.Effect", "SlewSettleTime keeps Slewing true after the mount stops.", "Times the same short slew with 0 s and 3 s settle time.", "Pass if the second takes ≥ 2.5 s longer; Fail otherwise.", "V4"),
            E("Motion", "SideOfPier.FarHourAngles", "Record SideOfPier where the mount must point through the pole.", "Slews 9 h either side of the meridian, high in the sky (ConformU's side-of-pier model).", "Info: what is right there depends on the mount's limits.", "ConformU", "PierSide"),
            E("Motion", "Flip.Goto.{direction}", "A goto across the meridian, past the flip angle, works.", "Slews 5° past the flip angle on one side, then 5° past it on the other.", Position, "PierSide", "Harness"),
            E("Motion", "Flip.Goto.{direction}.{check}", "The cross-meridian goto behaves like any slew.", "As the slew checks (returns, Slewing, targets, DestinationSideOfPier).", "As the matching slew check.", "V4", "PierSide"),
            E("Motion", "Flip.Goto.{direction}.Flipped", "Past the flip angle only one side reaches the sky, so the mount flips.", "Reads SideOfPier before and after the goto.", "Fail unless it went ThroughThePole → Normal (east to west) or the reverse.", "PierSide", "Harness"),
            E("Motion", "Flip.SideOfPier", "Say why the SideOfPier command wasn't checked.", "CanSetPierSide false, or a flip angle of 0.", "Skipped.", "Harness"),
            E("Motion", "Flip.SideOfPier.{side}", "Setting SideOfPier flips the mount without losing the target.", "Halfway inside the flip angle, sets SideOfPier to the other side, then back.", "Fail if the side doesn't change; " + Position, "V4", "PierSide"),
            E("Motion", "{slew}.InvalidRaLow", "Invalid coordinates are refused before the mount moves.", "Slew to RA -1.", Invalid, "V4", "ConformU"),
            E("Motion", "{slew}.InvalidRaHigh", "Invalid coordinates are refused.", "Slew to RA 25.", Invalid, "V4", "ConformU"),
            E("Motion", "{slew}.InvalidDecLow", "Invalid coordinates are refused.", "Slew to Dec -91.", Invalid, "V4", "ConformU"),
            E("Motion", "{slew}.InvalidDecHigh", "Invalid coordinates are refused.", "Slew to Dec 91.", Invalid, "V4", "ConformU"),
            E("Motion", "{slew}.NotTracking", "An equatorial slew needs tracking on.", "Turns tracking off and slews.", InvalidOp, "V4", "ConformU"),
            E("Motion", "AbortSlew.Idle", "AbortSlew when nothing moves is harmless.", "Calls AbortSlew while idle.", NoError, "V4", "ConformU"),
            E("Motion", "AbortSlew.DuringSlew", "AbortSlew stops a slew.", "Starts a two-hour slew, aborts after 0.5 s.", "Pass if Slewing goes false within 10 s; Info if the slew finished first.", "V4", "ConformU"),

            // ---------- Motion: syncs ----------
            E("Motion", "SyncToCoordinates", "A sync changes where the mount says it points.", "Syncs to a position 1° west and 1° south of where the mount points (ConformU's 60' error), reads RA/Dec, then syncs back by the same amount.", "Fail if RA/Dec still read the old position (sync ignored) or are over 1° out; Info beyond 10\"; Pass within 10\".", "V4", "ConformU"),
            E("Motion", "SyncToCoordinates.Targets", "A sync sets the targets to the coordinates synced to.", "Reads TargetRightAscension/Declination after the sync.", "Fail unless both are within 10\" of the sync coordinates.", "ConformU"),
            E("Motion", "SyncToTarget", "SyncToTarget syncs to the target properties.", "Sets the targets 1° off, calls SyncToTarget, then syncs back.", "As SyncToCoordinates.", "V4", "ConformU"),
            E("Motion", "{member}.Restore", "Leave the pointing model as found.", "The sync back after a sync check.", "Error if it failed: the mount's pointing may be out.", "Harness"),
            E("Motion", "SyncToCoordinates.InvalidRa", "Invalid coordinates are refused.", "SyncToCoordinates(25, dec).", Invalid, "V4", "ConformU"),
            E("Motion", "SyncToCoordinates.InvalidDec", "Invalid coordinates are refused.", "SyncToCoordinates(ra, 91).", Invalid, "V4", "ConformU"),

            // ---------- Motion: alt/az ----------
            E("Motion", "SlewToAltAzAsync.Tracking", "Alt/az slews need tracking off.", "SlewToAltAzAsync with tracking on.", InvalidOp, "V4", "ConformU"),
            E("Motion", "AltAz.Prepare", "Tracking can be turned off for the alt/az checks.", "Turns tracking off.", "Skipped if it can't be.", "Harness"),
            E("Motion", "SlewToAltAzAsync", "An alt/az slew reaches its target.", "Slews to altitude 50°, azimuth 150° (30° in the south) with tracking off.", Position, "V4", "ConformU"),
            E("Motion", "SlewToAltAz", "The synchronous alt/az slew (COM) reaches its target.", "Slews 5° and 10° further.", Position, "V4", "ConformU"),
            E("Motion", "{slew}.InvalidAltLow", "Invalid alt/az is refused.", "Altitude -100 (ConformU's value).", Invalid, "V4", "ConformU"),
            E("Motion", "{slew}.InvalidAltHigh", "Invalid alt/az is refused.", "Altitude 100.", Invalid, "V4", "ConformU"),
            E("Motion", "{slew}.InvalidAzLow", "Invalid alt/az is refused.", "Azimuth -10.", Invalid, "V4", "ConformU"),
            E("Motion", "{slew}.InvalidAzHigh", "Invalid alt/az is refused.", "Azimuth 370.", Invalid, "V4", "ConformU"),
            E("Motion", "SyncToAltAz", "SyncToAltAz changes where the mount says it points.", "With tracking off, syncs 1° off in altitude and azimuth, reads Altitude/Azimuth, then syncs back.", "Fail if Altitude/Azimuth still read the old position or are over 1° out; Info beyond 10\".", "V4", "ConformU"),
            E("Motion", "SyncToAltAz.InvalidAlt", "Invalid alt/az is refused.", "SyncToAltAz(az, 100).", Invalid, "V4", "ConformU"),

            // ---------- Motion: MoveAxis ----------
            E("Motion", "MoveAxis.{axis}", "A rate inside AxisRates is accepted.", "MoveAxis at about 1°/s (clamped into the lowest range).", "Fail on an error.", "V4", "MoveAxisFaq", "ConformU"),
            E("Motion", "MoveAxis.{axis}.Slewing", "\"Slewing must also be true during any MoveAxis() operations.\"", "Reads Slewing 1.5 s into the move.", "Fail if false.", "V4", "MoveAxisFaq", "ConformU"),
            E("Motion", "MoveAxis.{axis}.Speed", "The axis turns at the rate asked for.", "Measures how far the axis turned over the move.", "Fail if far off the rate; small movements aren't judged.", "MoveAxisFaq", "Harness"),
            E("Motion", "MoveAxis.{axis}.Stop", "Rate 0 stops the axis.", "MoveAxis(axis, 0), then waits for Slewing false.", "Fail on an error or if Slewing stays true for 15 s.", "V4", "ConformU"),
            E("Motion", "MoveAxis.{axis}.TrackingRestored", "\"Rate = 0 … return[s] to the previous tracking state.\"", "Compares Tracking before the move and after stopping.", "Fail if it changed.", "V4", "MoveAxisFaq", "ConformU"),
            E("Motion", "MoveAxis.{axis}.Negative", "A negative rate (the other direction) is accepted.", "MoveAxis(axis, -rate).", "Warning if refused.", "V4", "MoveAxisFaq"),
            E("Motion", "MoveAxis.{axis}.Direction", "A negative rate turns the axis the other way.", "Compares the direction moved with + and - rates.", "Fail if both moved the same way.", "MoveAxisFaq", "Harness"),
            E("Motion", "MoveAxis.{axis}.TooFast", "Rates beyond AxisRates are refused.", "MoveAxis at the maximum + 1.", Invalid, "V4", "ConformU"),
            E("Motion", "MoveAxis.{axis}.TooFastNegative", "Rates beyond AxisRates are refused, either sign.", "MoveAxis at -(maximum + 1).", Invalid, "V4", "ConformU"),
            E("Motion", "MoveAxis.{axis}.InGap", "A rate between two AxisRates ranges is refused.", "MoveAxis halfway between two ranges (when there is a gap).", Invalid, "V4"),
            E("Motion", "MoveAxis.BothAxes", "Both axes move together (diagonal moves).", "MoveAxis on primary and secondary at once, then stops both.", "Fail unless both are accepted, Slewing is true and both stop.", "V4", "Harness"),

            // ---------- Motion: PulseGuide ----------
            E("Motion", "PulseGuide.{direction}", "PulseGuide works in all four directions and ends.", "Pulses each direction (long enough to move ~20\") 3 h west of the meridian.", "Fail on an error or if IsPulseGuiding stays true 3 s past the pulse.", "V4", "ConformU"),
            E("Motion", "PulseGuide.{direction}.Asynchronous", "In ITelescopeV4 PulseGuide returns at once.", "Times the call against the pulse duration.", "Fail if it blocked for the pulse.", "V4", "Async", "ConformU"),
            E("Motion", "PulseGuide.{direction}.Slewing", "Slewing \"must not be True\" while pulse guiding.", "Reads Slewing during the pulse.", "Fail if true.", "V4"),
            E("Motion", "PulseGuide.{direction}.IsPulseGuiding", "IsPulseGuiding is true during an asynchronous pulse.", "Reads it straight after the call.", "Fail if false while the pulse should be running.", "V4", "ConformU"),
            E("Motion", "PulseGuide.{direction}.Moves", "Each pulse moves the right way, about the right distance.", "Compares RA/Dec before and after with the guide rate × duration.", "Fail if it moved the wrong way or far from the expected distance.", "V4", "ConformU"),
            E("Motion", "PulseGuide.{direction}.Moves.{side}", "Pulses are right on both sides of the pier.", "The same, measured west (Normal) and east (ThroughThePole) of the meridian on a German mount.", "Fail if a direction is reversed on one side.", "V4", "PierSide", "ConformU"),
            E("Motion", "PulseGuide.InvalidDirection", "An invalid direction is refused.", "PulseGuide(9, 100).", Invalid, "V4"),
            E("Motion", "PulseGuide.NegativeDuration", "A negative duration is refused.", "PulseGuide(North, -1).", Invalid, "V4"),
            E("Motion", "PulseGuide.NotTracking", "No guiding without tracking.", "Turns tracking off and pulses.", InvalidOp, "V4"),
            E("Motion", "PulseGuide.WhileSlewing", "No guiding during a slew.", "Pulses during a two-hour slew.", InvalidOp + " Info if the slew ended first.", "V4"),
            E("Motion", "PulseGuide.Concurrent", "An RA and a Dec pulse together.", "Pulses East then North at once.", "Pass if both are accepted, or the second is refused with InvalidOperationException.", "V4", "ConformU"),

            // ---------- Motion: tracking and offsets ----------
            E("Motion", "Tracking.Set{value}", "Tracking can be turned off and on.", "Sets Tracking false, then true, reading back each time.", "Fail if it doesn't take.", "V4", "ConformU"),
            E("Motion", "{member}.Offset", "Rate offsets of either sign are accepted and read back at sidereal rate.", "At driveSidereal, writes ±0.05 and ±40 arc-seconds per second (ConformU's values; RA in seconds of RA), waits 1 s, reads back, then sets 0.", "Fail if a small offset or its negative is refused, or any value reads back differently. A large value may be refused (Info).", "V4", "Offsets", "ConformU"),
            E("Motion", "{member}.Offset.Slewing", "An offset is tracking, not slewing.", "Reads Slewing 1 s after each offset is applied.", "Fail if Slewing is true.", "ConformU"),
            E("Motion", "{member}.ZeroAt{rate}", "\"Reading this property must return a value of zero if TrackingRate is not driveSidereal.\"", "Sets an offset at sidereal rate, switches to each other listed rate, reads both offsets.", "Pass if 0; Warning for InvalidOperationException (the exception list allows it); Fail for any other value or error.", "V4", "Offsets", "ConformU"),
            E("Motion", "{member}.NotSidereal{rate}", "Offsets only apply at sidereal rate.", "Writes an offset at each other listed rate.", InvalidOp, "V4", "Offsets", "ConformU"),

            // ---------- ParkHome ----------
            E("ParkHome", "AbortSlew.DuringPark", "AbortSlew stops parking.", "Moves away, starts Park, aborts part way.", "Fail on an error, if Slewing stays true, or if AtPark is true.", "V4", "ConformU"),
            E("ParkHome", "AbortSlew.DuringFindHome", "AbortSlew stops a find-home.", "Moves away, starts FindHome, aborts part way.", "Fail on an error, if Slewing stays true, or if AtHome is true.", "V4", "ConformU"),
            E("ParkHome", "FindHome", "FindHome reaches home.", "Calls FindHome and waits.", "Pass when AtHome is true and Slewing false; Fail on an error or timeout.", "V4", "ConformU"),
            E("ParkHome", "FindHome.Returns", "FindHome is asynchronous in ITelescopeV4.", "Times the call.", "Warning over 5 s.", "V4", "Async"),
            E("ParkHome", "AtHome.AfterSlew", "AtHome clears when the mount leaves home.", "Slews 10° away after FindHome.", "Fail if AtHome is still true.", "V4", "ConformU"),
            E("ParkHome", "Park", "Park reaches the park position.", "Calls Park and waits.", "Pass when AtPark is true and Slewing false; Fail on an error or timeout. Skipped when CanUnpark is false.", "V4", "ConformU"),
            E("ParkHome", "Park.Tracking", "A parked mount stops tracking.", "Reads Tracking while parked.", "Warning if true (\"Tracking must be False\").", "V4"),
            E("ParkHome", "Park.WhenParked", "Park while parked is harmless.", "Calls Park again.", NoError, "V4"),
            E("ParkHome", "Parked.{member}", "\"Attempts to slew, move or enable tracking while parked must raise a ParkedException.\"", "While parked: each slew, sync, PulseGuide, MoveAxis (rate 0), SlewToAltAzAsync, FindHome and AbortSlew.", ParkedErr, "V4", "ConformU"),
            E("ParkHome", "Parked.MoveAxis.{axis}", "MoveAxis is refused while parked.", "MoveAxis(axis, 0) while parked.", ParkedErr, "V4", "ConformU"),
            E("ParkHome", "Parked.{member}.StillParked", "A refused command must not unpark the mount.", "Reads AtPark after each refused command.", "Fail if AtPark went false.", "ConformU"),
            E("ParkHome", "Parked.MoveAxis.{axis}.StillParked", "A refused MoveAxis must not unpark the mount.", "Reads AtPark after it.", "Fail if AtPark went false.", "ConformU"),
            E("ParkHome", "Parked.TrackingOff", "Turning tracking off while parked works.", "Tracking = false while parked.", NoError, "V4", "ConformU"),
            E("ParkHome", "Parked.TrackingOn", "Turning tracking on while parked is refused.", "Tracking = true while parked.", ParkedErr, "V4", "ConformU"),
            E("ParkHome", "Unpark", "Unpark works.", "Calls Unpark.", "Fail on an error or if AtPark stays true.", "V4", "ConformU"),
            E("ParkHome", "Unpark.WhenUnparked", "\"Unparking a mount that is not parked … must always be successful.\"", "Calls Unpark again.", NoError, "V4"),
            E("ParkHome", "SetPark", "SetPark works (when allowed for the run).", "Calls SetPark where the mount points.", "Fail on an error; Skipped unless the SetPark switch is on.", "V4", "ConformU"),
            E("ParkHome", "SetPark.NotImplemented", "Without CanSetPark, SetPark isn't there.", "Calls SetPark when CanSetPark is false.", NotImpl, "V4", "ConformU"),

            // ---------- Tracking (extended) ----------
            E("Tracking", "Prepare", "The mount can be measured.", "Checks it isn't parked.", "Skipped if parked.", "Harness"),
            E("Tracking", "TrackingOn", "With tracking on, RA/Dec hold still.", "Samples RA/Dec at the start and end of the drift time.", "Pass if the drift is within a few seconds of RA / arc-seconds; Fail beyond.", "V4", "Harness"),
            E("Tracking", "TrackingOff", "With tracking off the mount stays put against the ground: RA follows sidereal time.", "Samples hour angle (LST - RA) over the drift time.", "Fail if the hour angle changes.", "V4", "Harness"),
            E("Tracking", "Offsets", "Say why the offsets weren't measured.", "Motion turned off.", "Skipped.", "Harness"),
            E("Tracking", "RightAscensionRate.Moves", "RightAscensionRate moves RA at the rate set.", "Sets 0.05 s of RA per sidereal second and measures RA over the drift time.", "Fail if the measured rate is far from the rate set.", "V4", "Offsets", "ConformU"),
            E("Tracking", "DeclinationRate.Moves", "DeclinationRate moves Dec at the rate set.", "Sets an arc-seconds per second offset towards the equator and measures Dec.", "Fail if the measured rate is far from the rate set.", "V4", "Offsets", "ConformU"),

            // ---------- Clients (extended) ----------
            E("Clients", "OneDisconnects", "One client disconnecting mustn't cut off another.", "Clients B and C connect; B disconnects.", "Fail unless C (and the run's own client) is still connected and answering.", "V4", "Harness"),
            E("Clients", "ParallelReads", "Several clients can read at once.", "Four clients read the same properties at the same time.", "Fail on an error, a slow answer, or clients that disagree.", "Harness"),
            E("Clients", "ConnectCycles", "Quick connect/disconnect cycles leave no trouble.", "Ten connect/disconnect cycles from another client.", "Fail on an error or Connected not true after each connect.", "Harness"),

            // ---------- Registration (COM) ----------
            E("Registration", "Profile", "The driver is in the ASCOM Profile (what the Chooser lists).", "Reads the registry's ASCOM Profile telescope entry.", "Fail if missing.", "Registration"),
            E("Registration", "ProgId", "The ProgID resolves to a CLSID.", "Reads HKCR\\<ProgID>\\CLSID.", "Fail if missing.", "Registration"),
            E("Registration", "Server.{bitness}", "64-bit and 32-bit clients can both start the driver.", "Reads LocalServer32 / InprocServer32 in both registry views and checks the program exists.", "Fail if missing or the program isn't there.", "Registration"),

            // ---------- ConformU ----------
            E("ConformU", "Log", "Say what the ConformU log found, so the run's purpose is on record.", "Counts the log's ERROR, ISSUE, CONFIGURATION ALERT and WARNING lines (repeats in its summary counted once) and groups them by member.", "Info, listing the problem areas.", "ConformU"),
            E("ConformU", "{member}.Probe", "Make a check for a property ConformU flagged that no harness check reached in this run (its groups were skipped or don't cover it).", "Reads the property three times while connected.", "Info with the values; Info if not implemented; Warning if a read took over a second; Fail if it raised another error.", "V4", "ConformU"),
            E("ConformU", "{member}.Reproduced", "Confirm a ConformU problem with the harness's own checks.", "After every group has run: for a member the harness diagnosed (rate offsets, pulse guiding), the diagnosis's verdict; otherwise the harness findings about the same member (its Member, or a part of its ID) that are problems.", "Fail when the diagnosis found the cause in the driver, or a harness check failed or errored; Warning when only warnings. ConformU's problem is real; the fix is the diagnosis's or the harness finding's. Other problems with a diagnosed member are listed as also found.", "ConformU", "Harness"),
            E("ConformU", "{member}.Explained", "Say when a ConformU problem comes from something other than the member itself.", "The member's diagnosis found it working, and ConformU's result explained by something else (a stale or coarse position reading).", "Warning, with the diagnosis's explanation and recommendation. Other problems the harness found with the member are listed as also found, not as ConformU's.", "ConformU", "Harness"),
            E("ConformU", "{member}.NotReproduced", "Say when the harness can't see a problem ConformU found.", "As Reproduced, but every harness check of the member passed, or its diagnosis found it within tolerance.", "Info: fixed since the ConformU run, or ConformU tested something the harness doesn't (or in conditions this run didn't meet).", "ConformU", "Harness"),
            E("ConformU", "{member}.NotChecked", "Say when a ConformU problem couldn't be looked at in this run.", "The groups that check the member were skipped (motion or settings writes off, extended checks off, communication lost).", "Skipped, with the reason; turn on what it needs and run again.", "Harness"),
            E("ConformU", "Diagnose.Readings", "Show how much the position readings scatter, the yardstick for ConformU's small movements.", "Before a rate offset or pulse guide diagnosis: tracking with no offset, reads RightAscension and Declination 25 times, 80 ms apart.", "Info: the RA and Dec spread, how often the reading changes (a cached position changes every few hundred ms).", "ConformU", "Harness"),
            E("ConformU", "Diagnose.PulseGuide.{axis}", "Tell apart the causes of a PulseGuide that moved too little or too much (ConformU only says it was outside its tolerance).", "Pulses each way on the axis for three lengths (up to ConformU's 5 s), reading the position as each pulse ends and again a moment later; fits the movement against the pulse's length.", "Info when every pulse moves the expected distance; Warning when the rest of the movement only shows later (a stale position reading); Fail when the shortfall grows with the pulse (wrong guide rate) or is the same for every pulse (time lost per pulse). The fix names the GSServer code to look at (simulator or real mount, by the Mount option).", "V4", "ConformU", "Harness"),
            E("ConformU", "Diagnose.{member}", "Tell apart the causes of a RightAscensionRate or DeclinationRate outside ConformU's tolerance.", "Applies ConformU's offset (0.05\"/s) and one 800 times larger (40\"/s), each way, for ConformU's 10 s, and measures the movement; compares with the readings' scatter.", "Info when all four are within 5%; Warning when the large offset is right and only ConformU's small one is off (the measurement is at the limit of the readings); Fail when the large one is off too (scaled, sign-dependent or unsteady). The fix names the GSServer code to look at. Skipped when motion is off or the mount is parked.", "V4", "ConformU", "Harness"),
            E("ConformU", "{member}.NoCheck", "Flag a ConformU problem nothing here can check.", "The member isn't checked by any group and isn't a property that can be read safely (a method that needs ConformU's arguments, or a ConformU-only topic).", "Warning: reproduce it by hand.", "ConformU", "Harness"),

            // ---------- Performance ----------
            E("Performance", "ReadTimes", "Properties answer quickly: clients poll them several times a second.", "Summarises property read times from the run.", "Warning for slow reads.", "Harness", "ConformU"),
            E("Performance", "Hangs", "No call hangs.", "Lists every call the harness gave up waiting for.", "Fail for each hang.", "Harness"),

            // ---------- Disconnected ----------
            E("Disconnected", "Disconnect", "Disconnect() works and the device reports it.", "Calls Disconnect(), waits for Connecting false, reads Connected.", "Fail if not implemented or an error; Info if another client keeps it connected.", "V4", "Async"),
            E("Disconnected", "Connecting.AfterDisconnect", "Connecting goes false when disconnecting finishes.", "Polls Connecting for 30 s.", "Fail if still true.", "V4", "Async"),
            E("Disconnected", "NotConnected.{member}", "A disconnected device refuses operational members.", "Reads RightAscension, Tracking, CanSlew and calls AxisRates while disconnected.", "Must raise NotConnectedException (0x407).", "V4", "ConformU"),
            E("Disconnected", "WhenDisconnected.{member}", "Identity members work while disconnected.", "Reads Name, DriverInfo, DriverVersion, InterfaceVersion and SupportedActions.", "Fail on an error.", "V4"),
            E("Disconnected", "Disconnect.WhenDisconnected", "Disconnect() while disconnected is harmless.", "Calls Disconnect() again.", NoError, "V4"),
        };
        return list;
    }

    /// <summary>The catalog entry for a finding (the most specific one that matches), or null if it has none.</summary>
    public static CatalogEntry? For(string findingId)
        => Entries.Where(e => e.Matches(findingId)).OrderBy(e => $"{e.Group}.{e.Id}".Count(ch => ch == '{')).FirstOrDefault();

    /// <summary>The group an entry is shown under: run-level entries (any group) under "Run".</summary>
    public static string DisplayGroup(CatalogEntry e) => e.Group.Contains('{') ? "Run" : e.Group;
}
