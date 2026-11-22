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
/// How much it matters that a test passes, from 1 to 10 (<see cref="CatalogEntry.Weight"/>). A chained run adds up the
/// weights of a tier's failures to decide whether it is safe to go on to the next tier (<see cref="Tiers.ProblemScore"/>),
/// and a failed test of <see cref="Critical"/> weight stops it at once. In order of what is looked up first:
/// <list type="number">
/// <item>the entry's own weight in <see cref="ByKey"/> (its Group.Id, placeholders as in the catalog);</item>
/// <item>a rule for a kind of test (<see cref="Rules"/>): checks of an invalid value or of a method that shouldn't be there are about
/// the driver's manners, not about whether the mount is safe to move;</item>
/// <item>its group's weight in <see cref="ByGroup"/>.</item>
/// </list>
/// 10 is a test that can't fail if the run is to go on (the connection, stopping the mount); 1 is a nicety.
/// </summary>
public static class TestWeights
{
    public const int Min = 1, Max = 10;

    /// <summary>A failure of a test this important ends the run's tiers: nothing deeper is worth running on a mount that fails it.</summary>
    public const int Critical = 10;

    /// <summary>What a finding with no catalog entry counts for.</summary>
    public const int Unknown = 5;

    private static readonly Dictionary<string, int> ByKey = new()
    {
        // Can the harness talk to the mount at all?
        ["Connection.Connected.Read"] = 10,
        ["Connection.Connect"] = 10,
        ["{group}.CommunicationLost"] = 10,
        ["Connection.Connect.WhenConnected"] = 9,
        ["Connection.Connected.SetTrueWhenConnected"] = 9,
        ["Connection.Connect.ConnectingOnReturn"] = 8,
        ["Connection.Connecting.AfterConnect"] = 8,
        ["Connection.{member}.Read"] = 6,
        ["Connection.Description.Length"] = 2,
        ["Connection.DriverVersion.Format"] = 2,
        ["Connection.InterfaceVersion.Current"] = 3,

        // Can the mount be stopped, and is it put back as it was?
        ["Motion.AbortSlew.Idle"] = 8,
        ["Motion.AbortSlew.DuringSlew"] = 10,
        ["ParkHome.AbortSlew.DuringPark"] = 10,
        ["ParkHome.AbortSlew.DuringFindHome"] = 10,
        ["Motion.MoveAxis.{axis}.Stop"] = 10,
        ["Motion.MoveAxis.{axis}.SlewingUntilStopped"] = 8,
        ["Motion.MoveAxis.{axis}.TrackingRestored"] = 7,
        ["Motion.MoveAxis.{axis}.TooFast"] = 8,
        ["Motion.MoveAxis.{axis}.TooFastNegative"] = 8,
        ["Settings.Restore"] = 9,
        ["Motion.{member}.Restore"] = 9,
        ["{group}.Unexpected"] = 7,

        // What the mount does when it moves: where it goes, and whether it says so.
        ["Motion.Prepare"] = 9,
        ["Motion.StartPosition"] = 9,
        ["Tracking.Prepare"] = 9,
        ["Motion.AltAz.Prepare"] = 8,
        ["Motion.SlewToCoordinatesAsync.West"] = 8,
        ["Motion.SlewToCoordinatesAsync.East"] = 8,
        ["Motion.SlewToTargetAsync"] = 8,
        ["Motion.SlewToCoordinates"] = 8,
        ["Motion.SlewToTarget"] = 8,
        ["Motion.SlewToAltAzAsync"] = 8,
        ["Motion.SlewToAltAz"] = 8,
        ["Motion.{slew*}.SlewingOnReturn"] = 7,
        ["Motion.{slew*}.Returns"] = 7,
        ["Motion.{slew*}.Targets"] = 8,
        ["Motion.Flip.Goto.{direction}"] = 7,
        ["Motion.Flip.Goto.{direction}.Flipped"] = 7,
        ["ParkHome.Park"] = 8,
        ["ParkHome.Unpark"] = 8,
        ["ParkHome.FindHome"] = 8,
        ["ParkHome.Parked.{member}"] = 8,
        ["ParkHome.Parked.MoveAxis.{axis}"] = 8,
        ["ParkHome.Parked.{member}.StillParked"] = 9,
        ["ParkHome.Parked.MoveAxis.{axis}.StillParked"] = 9,
        ["ParkHome.SetPark"] = 6,

        // What later groups depend on, and what a COM driver can't work without.
        ["Capabilities.{member}.Read"] = 7,
        ["Registration.Profile"] = 8,
        ["Registration.ProgId"] = 8,
        ["Registration.Server.{bitness}"] = 8,
        ["Disconnected.Disconnect"] = 7,
        ["Disconnected.Disconnect.WhenDisconnected"] = 5,
        ["Performance.Hangs"] = 7,
    };

    /// <summary>A kind of test, by its key, for the ones <see cref="ByKey"/> doesn't name; the first that fits is used.</summary>
    private static readonly (Func<string, bool> Fits, int Weight)[] Rules =
    [
        (k => k.Contains(".NotImplemented", StringComparison.Ordinal), 3),
        (k => k.Contains(".Invalid", StringComparison.Ordinal) || k.EndsWith(".NegativeDuration", StringComparison.Ordinal), 4),
        (k => k.Contains(".Deprecated", StringComparison.Ordinal), 2),
    ];

    private static readonly Dictionary<string, int> ByGroup = new()
    {
        ["Run"] = 5,
        ["Connection"] = 7,
        ["Protocol"] = 2,
        ["Common"] = 4,
        ["Capabilities"] = 5,
        ["Properties"] = 4,
        ["Methods"] = 3,
        ["Settings"] = 4,
        ["Motion"] = 6,
        ["ParkHome"] = 7,
        ["Tracking"] = 5,
        ["Clients"] = 4,
        ["Registration"] = 8,
        ["ConformU"] = 4,
        ["Diagnose"] = 3,
        ["Performance"] = 2,
        ["Disconnected"] = 4,
    };

    /// <summary>What a tier's failed tests may add up to before the next tier isn't run, unless the user has set another (<see cref="Limit"/>).</summary>
    public const int DefaultLimit = 40;

    private static volatile IReadOnlyDictionary<string, int> _adjusted = new Dictionary<string, int>();
    private static int _limit = DefaultLimit;

    /// <summary>What a tier's failed tests may add up to (<see cref="Tiers.ProblemScore"/>) before the next tier isn't run.</summary>
    public static int Limit => Volatile.Read(ref _limit);

    /// <summary>
    /// The weights the user has changed from the built-in ones (by key), and the limit: set by the <see cref="WeightStore"/> when
    /// the app starts and whenever they are changed. Used by every run from then on.
    /// </summary>
    public static void Apply(IReadOnlyDictionary<string, int> adjusted, int limit)
    {
        _adjusted = new Dictionary<string, int>(adjusted);
        Volatile.Write(ref _limit, Math.Clamp(limit, 1, 10_000));
    }

    /// <summary>A copy of the weights the user has changed (by test key), or null when there are none: kept in a report to say what the run used.</summary>
    public static Dictionary<string, int>? AdjustedSnapshot() => _adjusted.Count == 0 ? null : new Dictionary<string, int>(_adjusted);

    /// <summary>Whether the user has changed this test's weight from the built-in one.</summary>
    public static bool IsAdjusted(string key) => _adjusted.ContainsKey(key);

    /// <summary>The weight (1 to 10) of the catalog entry with this key (Group.Id): the user's own if they have changed it.</summary>
    public static int Of(string key) => _adjusted.TryGetValue(key, out var weight) ? Math.Clamp(weight, Min, Max) : Default(key);

    /// <summary>The built-in weight of the catalog entry with this key, whatever the user has set.</summary>
    public static int Default(string key)
    {
        // A ConformU run's diagnosis is the same test as the conformance check's (ConformU.Diagnose.X is Diagnose.X for the log's
        // problems), so it weighs the same.
        if (key.StartsWith("ConformU.Diagnose.", StringComparison.Ordinal)) return Default(key["ConformU.".Length..]);
        if (ByKey.TryGetValue(key, out var weight)) return weight;
        foreach (var (fits, w) in Rules)
            if (fits(key)) return w;
        var group = key[..Math.Max(0, key.IndexOf('.'))];
        return ByGroup.TryGetValue(group, out var g) ? g : Unknown;
    }

    /// <summary>The weight of a finding: its catalog entry's, or <see cref="Unknown"/> for one with none.</summary>
    public static int Of(CheckResult r) => KeyOf(r.Id) is { } key ? Of(key) : Unknown;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> Keys = new();

    /// <summary>The catalog key of the test a finding's id belongs to (cached: the catalog is searched by pattern), or null.</summary>
    private static string? KeyOf(string findingId) => Keys.GetOrAdd(findingId, id => TestCatalog.For(id)?.Key);
}
