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

namespace GS.TestHarness.Engine;

/// <summary>
/// Each test's number: one per <see cref="TestCatalog"/> entry, by its key (Group.Id, placeholders and all), shown on the
/// Tests list and in the help's Diagnostic tiers section. The first digit is the test's tier (1001 on for tier 1, 2001 on
/// for tier 2, 3001 on for tier 3), so a number says how deep its test goes. A number is never reused:
/// <list type="bullet">
/// <item>A new test takes the next number of its tier: one more than the highest here that starts with its tier's digit
/// (CatalogTests fails until it has one, or if its first digit isn't its tier).</item>
/// <item>A test that is renamed keeps its number (change the key here too).</item>
/// <item>A test that is removed keeps its line, with its key marked <c>retired:</c>, so its number isn't given again.</item>
/// <item>A test that moves to another tier takes the next number of that tier, and its old line is retired.</item>
/// </list>
/// </summary>
public static class TestNumbers
{
    /// <summary>The prefix of a removed test's key: its number stays taken.</summary>
    public const string Retired = "retired:";

    public static readonly IReadOnlyList<(int Number, string Key)> All =
    [
        (1001, "{group}.Group"),
        (1002, "{group}.Stopped"),
        (1003, "{group}.CommunicationLost"),
        (1004, "{group}.Unexpected"),
        (1005, "Site.Apply"),
        (1006, "Site.Restore"),
        (1007, "Connection.Connected.Read"),
        (1008, "Connection.Connect"),
        (1009, "Connection.Connect.ConnectingOnReturn"),
        (1010, "Connection.Connecting.AfterConnect"),
        (1011, "Connection.Connect.WhenConnected"),
        (1012, "Connection.Connected.SetTrueWhenConnected"),
        (1013, "Connection.{member}.Read"),
        (1014, "Connection.Description.Length"),
        (1015, "Connection.DriverVersion.Format"),
        (1016, "Connection.InterfaceVersion.Current"),
        (1017, "Protocol.Management.ApiVersions"),
        (1018, "Protocol.Management.Description"),
        (1019, "Protocol.Management.ConfiguredDevices"),
        (1020, "Protocol.Management.UniqueIdStable"),
        (1021, "Protocol.Management.UniqueIds"),
        (1022, "Protocol.ClientTransactionID.Echo"),
        (1023, "Protocol.ServerTransactionID.Increases"),
        (1024, "Protocol.Query.CaseInsensitive"),
        (1025, "Protocol.ClientTransactionID.Missing"),
        (1026, "Protocol.ClientTransactionID.Invalid"),
        (1027, "Protocol.Reply.Json"),
        (1028, "Protocol.Reply.Shape"),
        (1029, "Protocol.Endpoint.{member}"),
        (1030, "Protocol.Errors.InvalidValue"),
        (1031, "Protocol.Errors.Message"),
        (1032, "Protocol.BadRequest.DeviceNumber"),
        (1033, "Protocol.BadRequest.MissingParameter"),
        (1034, "Protocol.BadRequest.BadNumber"),
        (1035, "Protocol.BadRequest.BadBoolean"),
        (1036, "Protocol.BadRequest.PutCasing"),
        (1037, "Protocol.BadRequest.UnknownMember"),
        (1038, "Protocol.BadRequest.DecimalComma"),
        (1039, "Protocol.BadRequest.Method{method}"),
        (1040, "Protocol.UTCDate.Format"),
        (1041, "Protocol.Setup.{page}"),
        (1042, "Protocol.Discovery"),
        (1043, "Protocol.Discovery.Broadcast"),
        (1044, "Protocol.Discovery.IPv6"),
        (1045, "Common.SupportedActions.Read"),
        (1046, "Common.Action.Unknown"),
        (1047, "Common.{member}.Implemented"),
        (1048, "Common.Connecting.Read"),
        (1049, "Common.DeviceState.Read"),
        (1050, "Common.DeviceState.Duplicates"),
        (1051, "Common.DeviceState.TimeStamp"),
        (1052, "Common.DeviceState.Contents"),
        (1053, "Common.DeviceState.Values"),
        (1054, "Capabilities.{member}.Read"),
        (1055, "Capabilities.CanMoveAxis.{axis}"),
        (1056, "Capabilities.CanMoveAxis.InvalidAxis"),
        (1057, "Capabilities.Consistency.Unpark"),
        (1058, "Capabilities.Consistency.SetPark"),
        (1059, "Capabilities.Consistency.SlewPairs"),
        (1060, "Capabilities.Consistency.SyncAltAz"),
        (1061, "Properties.{member}.Read"),
        (1062, "Properties.IsPulseGuiding.NotImplemented"),
        (1063, "Properties.{member}.Zero"),
        (1064, "Properties.SiderealTime.Value"),
        (1065, "Properties.{member}.NotSet"),
        (1066, "Properties.TrackingRates.HasCurrent"),
        (1067, "Properties.UTCDate.Value"),
        (1068, "Properties.AltAz.Consistent"),
        (1069, "Methods.AxisRates.{axis}"),
        (1070, "Methods.AxisRates.InvalidAxis"),
        (1071, "Methods.DestinationSideOfPier.Read"),
        (1072, "Methods.DestinationSideOfPier.Sides"),
        (1073, "Methods.DestinationSideOfPier.InvalidRa"),
        (1074, "Methods.DestinationSideOfPier.InvalidDec"),
        (1075, "Settings.{member}.Write"),
        (1076, "Settings.{member}.ReadBack"),
        (1077, "Settings.{member}.InvalidLow"),
        (1078, "Settings.{member}.InvalidHigh"),
        (1079, "Settings.{member}.NotImplemented"),
        (1080, "Settings.TrackingRate.Set{rate}"),
        (1081, "Settings.TrackingRate.InvalidLow"),
        (1082, "Settings.TrackingRate.InvalidHigh"),
        (1083, "Settings.TrackingRate.Unsupported{rate}"),
        (1084, "Settings.UTCDate.Write"),
        (1085, "Settings.Restore"),
        (1086, "Motion.{member}.NotImplemented"),
        (1087, "Motion.MoveAxis.{axis}.NotImplemented"),
        (1088, "Motion.Prepare"),
        (1089, "Motion.SlewToCoordinatesAsync.West"),
        (1090, "Motion.SlewToCoordinatesAsync.East"),
        (1091, "Motion.SlewToTargetAsync"),
        (1092, "Motion.SlewToCoordinates"),
        (1093, "Motion.SlewToTarget"),
        (1094, "Motion.{slew*}.SlewingOnReturn"),
        (1095, "Motion.{slew*}.Returns"),
        (1096, "Motion.{slew*}.Targets"),
        (1097, "Motion.{slew*}.DestinationSideOfPier"),
        (1098, "Motion.{slew*}.SideOfPier"),
        (1099, "Motion.{member}.Deprecated"),
        (1100, "Motion.SlewSettleTime.Effect"),
        (1101, "Motion.SideOfPier.FarHourAngles"),
        (1102, "Motion.Flip.Goto.{direction}"),
        (1103, "Motion.Flip.Goto.{direction}.{check}"),
        (1104, "Motion.Flip.Goto.{direction}.Flipped"),
        (1105, "Motion.Flip.SideOfPier"),
        (1106, "Motion.Flip.SideOfPier.{side}"),
        (1107, "Motion.{slew}.InvalidRaLow"),
        (1108, "Motion.{slew}.InvalidRaHigh"),
        (1109, "Motion.{slew}.InvalidDecLow"),
        (1110, "Motion.{slew}.InvalidDecHigh"),
        (1111, "Motion.{slew}.NotTracking"),
        (1112, "Motion.AbortSlew.Idle"),
        (1113, "Motion.AbortSlew.DuringSlew"),
        (1114, "Motion.SyncToCoordinates"),
        (1115, "Motion.SyncToCoordinates.Targets"),
        (1116, "Motion.SyncToTarget"),
        (1117, "Motion.{member}.Restore"),
        (1118, "Motion.SyncToCoordinates.InvalidRa"),
        (1119, "Motion.SyncToCoordinates.InvalidDec"),
        (1120, "Motion.SlewToAltAzAsync.Tracking"),
        (1121, "Motion.AltAz.Prepare"),
        (1122, "Motion.SlewToAltAzAsync"),
        (1123, "Motion.SlewToAltAz"),
        (1124, "Motion.{slew}.InvalidAltLow"),
        (1125, "Motion.{slew}.InvalidAltHigh"),
        (1126, "Motion.{slew}.InvalidAzLow"),
        (1127, "Motion.{slew}.InvalidAzHigh"),
        (1128, "Motion.SyncToAltAz"),
        (1129, "Motion.SyncToAltAz.InvalidAlt"),
        (1130, "Motion.MoveAxis.{axis}"),
        (1131, "Motion.MoveAxis.{axis}.Slewing"),
        (1132, "Motion.MoveAxis.{axis}.Speed"),
        (1133, "Motion.MoveAxis.{axis}.Stop"),
        (1134, "Motion.MoveAxis.{axis}.TrackingRestored"),
        (1135, "Motion.MoveAxis.{axis}.Negative"),
        (1136, "Motion.MoveAxis.{axis}.Direction"),
        (1137, "Motion.MoveAxis.{axis}.TooFast"),
        (1138, "Motion.MoveAxis.{axis}.TooFastNegative"),
        (1139, "Motion.MoveAxis.{axis}.InGap"),
        (1140, "Motion.MoveAxis.BothAxes"),
        (1141, "Motion.PulseGuide.{direction}"),
        (1142, "Motion.PulseGuide.{direction}.Asynchronous"),
        (1143, "Motion.PulseGuide.{direction}.Slewing"),
        (1144, "Motion.PulseGuide.{direction}.IsPulseGuiding"),
        (1145, "Motion.PulseGuide.{direction}.Moves"),
        (1146, "Motion.PulseGuide.{direction}.Moves.{side}"),
        (1147, "Motion.PulseGuide.InvalidDirection"),
        (1148, "Motion.PulseGuide.NegativeDuration"),
        (1149, "Motion.PulseGuide.NotTracking"),
        (1150, "Motion.PulseGuide.WhileSlewing"),
        (1151, "Motion.PulseGuide.Concurrent"),
        (1152, "Motion.Tracking.Set{value}"),
        (1153, "Motion.{member}.Offset"),
        (1154, "Motion.{member}.Offset.Slewing"),
        (1155, "Motion.{member}.ZeroAt{rate}"),
        (1156, "Motion.{member}.NotSidereal{rate}"),
        (1157, "ParkHome.AbortSlew.DuringPark"),
        (1158, "ParkHome.AbortSlew.DuringFindHome"),
        (1159, "ParkHome.FindHome"),
        (1160, "ParkHome.FindHome.Returns"),
        (1161, "ParkHome.AtHome.AfterSlew"),
        (1162, "ParkHome.Park"),
        (1163, "ParkHome.Park.Tracking"),
        (1164, "ParkHome.Park.WhenParked"),
        (1165, "ParkHome.Parked.{member}"),
        (1166, "ParkHome.Parked.MoveAxis.{axis}"),
        (1167, "ParkHome.Parked.{member}.StillParked"),
        (1168, "ParkHome.Parked.MoveAxis.{axis}.StillParked"),
        (1169, "ParkHome.Parked.TrackingOff"),
        (1170, "ParkHome.Parked.TrackingOn"),
        (1171, "ParkHome.Unpark"),
        (1172, "ParkHome.Unpark.WhenUnparked"),
        (1173, "ParkHome.SetPark"),
        (1174, "ParkHome.SetPark.NotImplemented"),
        (1175, "Tracking.Prepare"),
        (1176, "Tracking.TrackingOn"),
        (1177, "Tracking.TrackingOff"),
        (1178, "Tracking.Offsets"),
        (1179, "Tracking.RightAscensionRate.Moves"),
        (1180, "Tracking.DeclinationRate.Moves"),
        (1181, "Clients.DisconnectedClient.Connected"),
        (1182, "Clients.OneDisconnects"),
        (1183, "Clients.ParallelReads"),
        (1184, "Clients.ConnectCycles"),
        (1185, "Registration.Profile"),
        (1186, "Registration.ProgId"),
        (1187, "Registration.Server.{bitness}"),
        (1188, "ConformU.Log"),
        (1189, "ConformU.{member}.Probe"),
        (1190, "ConformU.{member}.Reproduced"),
        (1191, "ConformU.{member}.Explained"),
        (1192, "ConformU.{member}.NotReproduced"),
        (1193, "ConformU.{member}.NotChecked"),
        (1194, "ConformU.{member}.NoCheck"),
        (1195, "Performance.ReadTimes"),
        (1196, "Performance.Hangs"),
        (1197, "Disconnected.Disconnect"),
        (1198, "Disconnected.Connecting.AfterDisconnect"),
        (1199, "Disconnected.NotConnected.{member}"),
        (1200, "Disconnected.WhenDisconnected.{member}"),
        (1201, "Disconnected.Disconnect.WhenDisconnected"),
        (2001, "Diagnose.Readings"),
        (2002, "Diagnose.PulseGuide.{axis}"),
        (2003, "Diagnose.{member}"),
        (2004, "ConformU.Diagnose.Readings"),
        (2005, "ConformU.Diagnose.PulseGuide.{axis}"),
        (2006, "ConformU.Diagnose.{member}"),
        (2007, "Diagnose.Arguments.{member}"),
        (2008, "Diagnose.Preconditions.{member}"),
        (2009, "Diagnose.NotConnected"),
        (2010, "retired:Diagnose.Connected.PerClient"), // Connected reports the hardware state: nothing per client to diagnose
        (2011, "Diagnose.SlewSettleTime"),
        (2012, "Diagnose.AxisRates.Axes"),
        (2013, "Diagnose.DriverVersion"),
        (2014, "Diagnose.Registration"),
        (2015, "Diagnose.RateOffsets.{member}"),
        (2016, "Diagnose.Clients.Connections"),
        (2017, "Diagnose.Alpaca.Binding"),
        (2018, "Diagnose.Alpaca.Setup"),
        (3001, "Diagnose.{member}.Cause"),
        (3002, "Diagnose.{member}.Summary"),
        (3003, "ConformU.Diagnose.{member}.Cause"),
        (3004, "ConformU.Diagnose.{member}.Summary"),
        (3005, "Diagnose.SlewSettleTime.Cause"),
        (3006, "Diagnose.SlewSettleTime.Summary"),
    ];

    private static readonly Dictionary<string, int> ByKey = All.Where(t => !t.Key.StartsWith(Retired))
        .GroupBy(t => t.Key).ToDictionary(g => g.Key, g => g.First().Number);

    /// <summary>The number of the test with this key, or 0 if it has none (CatalogTests catches that).</summary>
    public static int For(string key) => ByKey.GetValueOrDefault(key);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> ByFinding = new();

    /// <summary>The number of the test a finding (its full ID, Group.Id) belongs to, or 0 if none.</summary>
    public static int Of(string findingId) => ByFinding.GetOrAdd(findingId, id => TestCatalog.For(id)?.Number ?? 0);

    /// <summary>"1194 Tracking.RightAscensionRate.Moves": a finding's ID with its test's number in front.</summary>
    public static string Label(string findingId) => Of(findingId) is > 0 and var n ? $"{n} {findingId}" : findingId;

    /// <summary>The tier a number belongs to: its first digit.</summary>
    public static int TierOf(int number) => number / 1000;
}
