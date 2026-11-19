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

using System.Diagnostics;
using System.Globalization;
using ASCOM.Common.DeviceInterfaces;

namespace GS.TestHarness.Engine.Checks;

/// <summary>A problem to diagnose: the member, and what found it (ConformU's lines, or the tier below's findings), for each finding's Expected.</summary>
public sealed record DiagnosisProblem(string Member, string Said);

/// <summary>
/// The diagnoses of tiers 2 and 3 (<see cref="Tiers"/>): measurements made for a particular problem, to tell its causes
/// apart. They run in a tier 2 or 3 run only: in the ConformU group for a ConformU log's problems (findings
/// ConformU.Diagnose.*), in the Diagnose group for a conformance run's (Diagnose.*). A check only says a value was out
/// of tolerance; these measure how:
/// <list type="bullet">
/// <item>Tier 2, RightAscensionRate / DeclinationRate: ConformU's offset and one 800 times larger, each way, against how
/// much the position readings scatter with no offset. A rate that is wrong is wrong at both sizes; one only wrong at
/// ConformU's tiny offset is a small-offset fault, a drift, or the readings, which tier 3 tells apart.</item>
/// <item>Tier 3, RightAscensionRate / DeclinationRate: the tier 2 measurements again, plus the same with no offset at
/// all, and the small offset for three times as long. A drift moves the mount with no offset and adds the same to
/// both signs; a reading error is bounded, so it shrinks as the measurement gets longer; a small offset rounded or
/// scaled is off by the same factor at both signs and every length.</item>
/// <item>Tier 2, PulseGuide: pulses of three lengths each way, the position read as the pulse ends and again a moment
/// later. Movement that only shows later is a stale position reading; a shortfall that grows with the pulse is a wrong
/// guide rate; one that doesn't is time lost in every pulse.</item>
/// </list>
/// Each finding's fix says what the pattern points to and, for GSServer, where in its code to look (the simulator's
/// code or the SkyWatcher code, by the Mount option). The harness only recommends: it never changes the server.
/// </summary>
internal static class Diagnostics
{
    /// <summary>A diagnosis finding's ID in the group running it: ConformU.Diagnose.* in the ConformU group, Diagnose.* in its own.</summary>
    private static string Id(CheckContext ctx, string rest) => ctx.Group == DiagnoseChecks.GroupName ? rest : $"Diagnose.{rest}";

    /// <summary>A member's diagnosis finding at the run's tier: {member} at tier 2, {member}.Cause at tier 3 (each its own catalog entry).</summary>
    private static string MemberId(CheckContext ctx, string member) => Id(ctx, ctx.Options.Tier >= 3 ? $"{member}.Cause" : member);

    /// <summary>ConformU's rate offset test value: 0.05 arc-seconds per second (RightAscensionRate in s of RA per sidereal second).</summary>
    private const double SmallArcsec = 0.05;

    /// <summary>The large offset: ConformU's other test value, 40 arc-seconds per second.</summary>
    private const double LargeArcsec = 40;

    /// <summary>Sidereal seconds to SI seconds: RightAscensionRate is per sidereal second.</summary>
    private const double SiderealPerSi = 1.0027379;

    /// <summary>
    /// Diagnoses each problem at the run's tier (<see cref="RunOptions.Tier"/>). A member with no diagnosis at that tier
    /// is reported as skipped, so the run still says why nothing was measured for it.
    /// </summary>
    public static async Task RunAsync(CheckContext ctx, IReadOnlyList<DiagnosisProblem> problems)
    {
        var tier = ctx.Options.Tier;
        if (problems.Count == 0) return;
        var areas = new List<DiagnosisProblem>();
        foreach (var p in problems)
        {
            if (Tiers.Has(p.Member, tier)) areas.Add(p);
            else ctx.Report(MemberId(ctx, p.Member), p.Member, Outcome.Skipped,
                $"{p.Member} has no tier {tier} diagnosis" + (Tiers.Has(p.Member, tier - 1) ? $": tier {tier - 1} is as deep as it goes." : "."),
                expected: p.Said);
        }
        if (areas.Count == 0) return;
        if (!ctx.Options.AllowMotion)
        {
            foreach (var a in areas)
                ctx.Report(MemberId(ctx, a.Member), a.Member, Outcome.Skipped,
                    $"The {a.Member} diagnosis moves the mount: turn on Move the mount under Options and run again.", expected: a.Said);
            return;
        }
        if ((await ctx.TryAsync(t => t.AtPark)).Value)
        {
            foreach (var a in areas)
                ctx.Report(MemberId(ctx, a.Member), a.Member, Outcome.Skipped, $"The mount is parked, so the {a.Member} diagnosis wasn't made.", expected: a.Said);
            return;
        }

        var c = ctx.Caps;
        var wasTracking = (await ctx.TryAsync(t => t.Tracking)).Value;
        var rate = await ctx.TryAsync(t => t.TrackingRate);
        try
        {
            if (c.CanSetTracking)
            {
                await ctx.TryAsync(t => t.TrackingRate = DriveRate.Sidereal);
                await ctx.TryAsync(t => t.Tracking = true);
            }
            // Somewhere guiding works the same as in ConformU's tests: west of the meridian, at a safe declination.
            await MotionChecks.PlaceForGuidingAsync(ctx, hourAngle: 3);
            var noise = await ReadingsAsync(ctx);

            foreach (var a in areas)
            {
                switch (a.Member)
                {
                    case "RightAscensionRate" when c.CanSetRightAscensionRate:
                    case "DeclinationRate" when c.CanSetDeclinationRate:
                        if (tier >= 3)
                        {
                            await RateDeepAsync(ctx, a, noise);
                            FinalAnalysis(ctx, a);
                        }
                        else await RateAsync(ctx, a, noise);
                        break;
                    case "PulseGuide" when c.CanPulseGuide:
                        await PulseGuideAsync(ctx, a, noise);
                        break;
                    default:
                        ctx.Report(MemberId(ctx, a.Member), a.Member, Outcome.Skipped,
                            $"The mount says {a.Member} can't be used (its Can property is false), so it wasn't diagnosed.", expected: a.Said);
                        break;
                }
            }
        }
        finally
        {
            if (c.CanSetRightAscensionRate) await ctx.TryAsync(t => t.RightAscensionRate = 0);
            if (c.CanSetDeclinationRate) await ctx.TryAsync(t => t.DeclinationRate = 0);
            await ctx.WaitUntilAsync(t => !t.IsPulseGuiding, TimeSpan.FromSeconds(10), 100);
            if (rate.Ok) await ctx.TryAsync(t => t.TrackingRate = rate.Value);
            if (c.CanSetTracking) await ctx.TryAsync(t => t.Tracking = wasTracking);
        }
    }

    // ---------- How the position readings behave ----------

    /// <summary>How much the position readings scatter while tracking with no offset, and how often they change.</summary>
    private sealed record Noise(double RaSpreadSec, double DecSpreadArcsec, double? UpdateMs, int Changes, int Reads);

    /// <summary>Reads the position 25 times, 80 ms apart, tracking with no offset: it should hold still.</summary>
    private static async Task<Noise> ReadingsAsync(CheckContext ctx)
    {
        var reads = new List<(double Ms, double Ra, double Dec)>();
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 25; i++)
        {
            if (await MotionChecks.PositionAsync(ctx) is { } p) reads.Add((watch.Elapsed.TotalMilliseconds, p.Ra, p.Dec));
            await Task.Delay(80, ctx.Token);
        }
        if (reads.Count < 2) return new Noise(0, 0, null, 0, reads.Count);

        var ra0 = reads[0].Ra;
        var ras = reads.Select(r => Values.HoursDiff(r.Ra, ra0) * 3600).ToList();
        var decs = reads.Select(r => r.Dec * 3600).ToList();
        var changedAt = new List<double>();
        for (var i = 1; i < reads.Count; i++)
            if (reads[i].Ra != reads[i - 1].Ra || reads[i].Dec != reads[i - 1].Dec) changedAt.Add(reads[i].Ms);
        // A reading that changes on most reads is fresh each time: no update interval to speak of.
        double? updateMs = changedAt.Count is >= 2 and < 20
            ? (changedAt[^1] - changedAt[0]) / (changedAt.Count - 1) : null;
        var noise = new Noise(ras.Max() - ras.Min(), decs.Max() - decs.Min(), updateMs, changedAt.Count, reads.Count);

        ctx.Report(Id(ctx, "Readings"), "RightAscension", Outcome.Info,
            $"Tracking with no offset, the position was read {noise.Reads} times over {watch.Elapsed.TotalSeconds:0.0} s: RA spread " +
            $"{noise.RaSpreadSec:0.000} s ({noise.RaSpreadSec * 15:0.00}\"), Dec spread {noise.DecSpreadArcsec:0.00}\"; it changed " +
            $"{noise.Changes} times" + (updateMs is { } u ? $", about every {u:0} ms." : "."),
            expected: "the same position every time (tracking holds RA and Dec still)",
            actual: $"RA spread {noise.RaSpreadSec:0.000} s, Dec spread {noise.DecSpreadArcsec:0.00}\", {noise.Changes} changes in {noise.Reads} reads");
        return noise;
    }

    // ---------- RightAscensionRate and DeclinationRate ----------

    private static async Task RateAsync(CheckContext ctx, DiagnosisProblem area, Noise noise)
    {
        var ra = area.Member == "RightAscensionRate";
        // RightAscensionRate is in seconds of RA per sidereal second; DeclinationRate in arc-seconds per SI second.
        double Offset(double arcsec) => ra ? arcsec / 15 : arcsec;
        var unit = ra ? "s of RA per sidereal second" : "\"/s";
        // As long as ConformU's test (10 s) by default: half the drift time (20 s).
        var seconds = Math.Max(2, ctx.Options.DriftSeconds / 2.0);

        var results = new List<Measured>();
        foreach (var arcsec in new[] { SmallArcsec, -SmallArcsec, LargeArcsec, -LargeArcsec })
            results.Add(await MeasureRateAsync(ctx, Offset(arcsec), seconds, ra));

        var ok = results.Where(r => r.Fitted is not null).ToList();
        // Signed, but a value that rounds to zero is plain "0.0000" (not "-+0.0000").
        string Signed(double v)
        {
            var (format, suffix) = ra ? ("0.0000", " s") : ("0.000", "\"");
            var rounded = Math.Round(v, format.Length - 2);
            return (rounded == 0 ? format : rounded.ToString("+" + format + ";-" + format, CultureInfo.InvariantCulture)) + suffix;
        }
        string Size(double v) => ra ? v.ToString("0.0000", CultureInfo.InvariantCulture) + " s" : v.ToString("0.000", CultureInfo.InvariantCulture) + "\"";
        // Each offset: the fitted rate, ConformU's two-reading rate, and where those two readings sit against the line
        // fitted through all of them, beside how far the readings in between wander from it.
        var actual = string.Join('\n', results.Select(r => $"{area.Member} = {Show(r.Value)} {unit}: " + (r.Fitted is { } f
            ? $"fitted rate {f:0.000} × expected ({Pct(f)}), first-to-last reading as ConformU {r.TwoPoint:0.000} × ({Pct(r.TwoPoint!.Value)}); " +
              $"first and last readings {Signed(r.FirstOff)} and {Signed(r.LastOff)} off the fitted line, the readings between within ±{Size(r.Wander)} " +
              $"(RMS {Size(r.Scatter)})"
            : r.Note)));
        var smallMove = (ra ? SmallArcsec / 15 : SmallArcsec) * seconds;
        var spread = ra ? noise.RaSpreadSec : noise.DecSpreadArcsec;
        var moveText = ra ? $"{smallMove:0.000} s of RA" : $"{smallMove:0.00}\"";
        var spreadText = ra ? $"{spread:0.000} s" : $"{spread:0.00}\"";
        var id = Id(ctx, area.Member);
        var conformU = area.Said;

        if (ok.Count == 0)
        {
            ctx.Report(id, area.Member, Outcome.Error, $"{area.Member} couldn't be measured: no offset was accepted or the position couldn't be read.",
                expected: conformU, actual: actual);
            return;
        }

        // The fitted rate (a reading every 100 ms) is what the mount really does; ConformU's two readings are what it saw.
        // The large offsets settle whether the rate is right (the readings' scatter is tiny next to them). A small offset
        // is only wrong if it's off by more than its measurement can tell (three standard errors, and at least 10%).
        var largeOk = ok.Where(r => Math.Abs(r.Value) == Offset(LargeArcsec)).ToList();
        var smallOk = ok.Where(r => Math.Abs(r.Value) == Offset(SmallArcsec)).ToList();
        var smallWrong = smallOk.Where(r => Math.Abs(r.Fitted!.Value - 1) > Math.Max(0.1, 3 * r.FittedError!.Value)).ToList();
        var fittedDev = largeOk.Count > 0 ? largeOk.Max(r => Math.Abs(r.Fitted!.Value - 1)) : smallOk.Max(r => Math.Abs(r.Fitted!.Value - 1));
        if (fittedDev <= 0.03 && smallWrong.Count > 0)
        {
            // Each sign on its own: a drift (or a wandering reading) puts one sign above 1 × and the other below, and an
            // average of the two would hide it.
            var plus = smallOk.FirstOrDefault(r => r.Value > 0)?.Fitted;
            var minus = smallOk.FirstOrDefault(r => r.Value < 0)?.Fitted;
            var each = string.Join(" and ", new[] { (Sign: "+", R: plus), (Sign: "-", R: minus) }
                .Where(x => x.R is not null).Select(x => $"{x.Sign}0.05\"/s at {x.R:0.00} ×"));
            string pattern;
            if (plus is { } p && minus is { } m && Math.Sign(p - 1) != Math.Sign(m - 1))
                pattern = "One sign is fast and the other slow, by about as much: something adds the same movement whichever way the offset " +
                          "goes (a drift with no offset at all), or the readings wander over the measurement.";
            else
            {
                var at = smallWrong.Average(r => r.Fitted!.Value);
                pattern = at > 1 ? "Both signs are fast: the small offset is rounded up or scaled up before it's applied."
                    : Math.Abs(at) < 0.1 ? "Neither moves the mount: a small offset is ignored."
                    : "Both signs are slow: the small offset is rounded down or held back.";
            }
            ctx.Report(id, area.Member, Outcome.Fail,
                $"{area.Member} is right at 40\"/s but not at ConformU's 0.05\"/s: small offsets move the mount at {each} the expected rate, " +
                $"beyond what the readings' scatter explains. {pattern} Tier 3 tells a drift, wandering readings and a rounded offset apart.",
                expected: conformU, actual: actual, fix: Recommend(ctx, Cause.Inconsistent, smallMove, spread, ra), sourceMember: area.Member);
            return;
        }
        if (fittedDev <= 0.03)
        {
            fittedDev = ok.Where(r => !smallOk.Contains(r) || Math.Abs(r.Fitted!.Value - 1) <= 0.03).Select(r => Math.Abs(r.Fitted!.Value - 1)).DefaultIfEmpty(fittedDev).Max();
            var conformUDev = ok.Where(r => Math.Abs(r.Value) == Offset(SmallArcsec)).Select(r => Math.Abs(r.TwoPoint!.Value - 1)).DefaultIfEmpty(0).Max();
            if (conformUDev <= 0.05)
            {
                ctx.Report(id, area.Member, Outcome.Info,
                    $"Not reproduced: {area.Member} moved the mount within 3% of the expected rate at ConformU's offset and one 800 times larger, " +
                    $"each way, over {seconds:0} s, and ConformU's way of measuring it came within 5% too.", expected: conformU, actual: actual);
                return;
            }
            // The small offset ConformU's way was off: by a stray reading at an end, or by the readings' general scatter.
            var worstSmall = ok.Where(r => Math.Abs(r.Value) == Offset(SmallArcsec)).MaxBy(r => Math.Abs(r.TwoPoint!.Value - 1))!;
            string Amount(double v) => ra ? $"{v:0.000} s" : $"{v:0.00}\"";
            // A stray reading stands well outside where the readings in between wander; within it, they're just coarse.
            var stray = worstSmall.EndOff > Math.Max(Math.Max(2 * worstSmall.Wander, 3 * worstSmall.Scatter), 0.1 * smallMove);
            var rateRight = $"The rate is right (the 40\"/s offsets track within {fittedDev * 100:0.0}%, and ConformU's small one fitted over every " +
                            "reading is too), but measured as ConformU does, from one reading at each end, " +
                            $"{area.Member} = {Show(worstSmall.Value)} is off by {Math.Abs(worstSmall.TwoPoint!.Value - 1) * 100:0}%: ";
            ctx.Report(id, area.Member, Outcome.Warning,
                rateRight + (stray
                    ? $"one of those two readings was {Amount(worstSmall.EndOff)} out of line with the readings around it (which scatter by only " +
                      $"{Amount(worstSmall.Scatter)}), against a movement of {moveText} in {seconds:0} s. A single stray reading, not the rate."
                    : $"it moves only {moveText} in {seconds:0} s, while the readings wander up to ±{Amount(worstSmall.Wander)} about a steady " +
                      $"line as it moves (and spread {spreadText} with no offset at all), so each of ConformU's two readings can be that far out."),
                expected: conformU, actual: actual,
                fix: Recommend(ctx, stray ? Cause.StrayReading : Cause.Resolution, smallMove, spread, ra), sourceMember: ra ? "RightAscension" : "Declination");
            return;
        }

        // The mount itself moves at the wrong rate.
        var large = ok.Where(r => Math.Abs(r.Value) == Offset(LargeArcsec)).Select(r => r.Fitted!.Value).ToList();
        var both = large.Count == 2 ? large : ok.Select(r => r.Fitted!.Value).Take(2).ToList();
        var sameSide = both.Count == 2 && Math.Sign(both[0] - 1) == Math.Sign(both[1] - 1) && Math.Abs(both[0] - both[1]) < 0.05;
        var oneSign = both.Count == 2 && (Math.Abs(both[0] - 1) <= 0.03) != (Math.Abs(both[1] - 1) <= 0.03);
        var ratio = both.Average();
        var (cause, summary) = sameSide
            ? (Cause.Scale, $"{area.Member} moves the mount at {ratio:0.000} × the expected rate, at both signs: the offset is scaled wrongly.")
            : oneSign
                ? (Cause.Direction, $"{area.Member} is right one way and wrong the other: the sign of the offset is handled wrongly.")
                : (Cause.Inconsistent, $"{area.Member} doesn't move the mount at a steady rate: the error changes with the offset's size and sign.");
        ctx.Report(id, area.Member, Outcome.Fail, summary + Hint(ratio), expected: conformU, actual: actual,
            fix: Recommend(ctx, cause, smallMove, spread, ra), sourceMember: area.Member);
    }

    /// <summary>
    /// One offset's measurement, as ratios to the expected rate: fitted to readings every 100 ms (with the fit's standard
    /// error), and from the first and last reading only (as ConformU).
    /// </summary>
    /// <param name="FirstOff">The first reading's distance from the fitted line, signed (seconds of RA, or arc-seconds).</param>
    /// <param name="LastOff">The last reading's, signed. With FirstOff, the two readings ConformU uses.</param>
    /// <param name="Scatter">The readings in between: their typical distance from the line (RMS, same units).</param>
    /// <param name="Wander">The readings in between: the furthest any is from the line (same units).</param>
    /// <param name="Slope">The fitted rate itself (seconds of RA, or arc-seconds, per SI second): with no offset, the drift.</param>
    /// <param name="SlopeError">Its standard error, same units.</param>
    /// <param name="Seconds">How long the readings were taken for.</param>
    private sealed record Measured(double Value, double? Fitted, double? FittedError, double? TwoPoint, string Note,
        double FirstOff = 0, double LastOff = 0, double Scatter = 0, double Wander = 0, double Slope = 0, double SlopeError = 0, double Seconds = 0)
    {
        /// <summary>The further of the two end readings from the line.</summary>
        public double EndOff => Math.Max(Math.Abs(FirstOff), Math.Abs(LastOff));
    }

    /// <summary>
    /// Sets the offset, lets it settle a second, then reads the position every 100 ms for <paramref name="seconds"/>.
    /// Both rates are ratios to the expected one; null, with why, when it can't be measured.
    /// </summary>
    private static async Task<Measured> MeasureRateAsync(CheckContext ctx, double value, double seconds, bool ra)
    {
        var set = await ctx.TryAsync(t => { if (ra) t.RightAscensionRate = value; else t.DeclinationRate = value; });
        if (!set.Ok) return new Measured(value, null, null, null, $"refused ({set.ErrorText})");
        try
        {
            await Task.Delay(1000, ctx.Token);
            var samples = new List<(double T, double V)>();
            var watch = Stopwatch.StartNew();
            (double Ra, double Dec)? first = null;
            while (true)
            {
                var t = watch.Elapsed.TotalSeconds;
                if (await MotionChecks.PositionAsync(ctx) is { } p)
                {
                    first ??= p;
                    // RA: seconds of RA moved; Dec: arc-seconds moved.
                    samples.Add((t, ra ? Values.HoursDiff(p.Ra, first.Value.Ra) * 3600 : (p.Dec - first.Value.Dec) * 3600));
                }
                if (t >= seconds) break;
                await Task.Delay(100, ctx.Token);
            }
            if (samples.Count < 3) return new Measured(value, null, null, null, "the position couldn't be read");
            // RA: seconds of RA per SI second, against the offset per sidereal second; Dec: arc-seconds per second.
            var expected = ra ? value * SiderealPerSi : value;
            var mt = samples.Average(s => s.T);
            var mv = samples.Average(s => s.V);
            var sxx = samples.Sum(s => (s.T - mt) * (s.T - mt));
            var slope = samples.Sum(s => (s.T - mt) * (s.V - mv)) / sxx;
            var residuals = samples.Sum(s => Math.Pow(s.V - (mv + slope * (s.T - mt)), 2));
            var error = Math.Sqrt(residuals / (samples.Count - 2) / sxx);
            var twoPoint = (samples[^1].V - samples[0].V) / (samples[^1].T - samples[0].T);
            double Off((double T, double V) x) => x.V - (mv + slope * (x.T - mt));
            var inner = samples.Skip(1).SkipLast(1).ToList();
            var scatter = inner.Count > 0 ? Math.Sqrt(inner.Average(x => Off(x) * Off(x))) : 0;
            var wander = inner.Count > 0 ? inner.Max(x => Math.Abs(Off(x))) : 0;
            // With no offset (tier 3's control) there is no expected rate to compare with: only the slope means anything.
            var ratio = expected == 0 ? 1 : 1 / expected;
            return new Measured(value, slope * ratio, error * Math.Abs(ratio), twoPoint * ratio, "",
                Off(samples[0]), Off(samples[^1]), scatter, wander, slope, error, samples[^1].T - samples[0].T);
        }
        finally
        {
            await ctx.TryAsync(t => { if (ra) t.RightAscensionRate = 0; else t.DeclinationRate = 0; });
        }
    }

    // ---------- Tier 3: RightAscensionRate and DeclinationRate ----------

    /// <summary>
    /// Tier 3: what tier 2 can't tell apart when only ConformU's small offset is off. With no offset at all the mount
    /// must hold still: any movement is a drift, which adds the same to both signs of an offset. The small offset again,
    /// for three times as long: an error in the readings is bounded, so the rate error it makes shrinks as the
    /// measurement gets longer, while a wrong rate stays as wrong. The two signs are kept apart (a drift moves them
    /// opposite ways; a rounded offset the same way) and the large offset is measured again for the scale.
    /// </summary>
    private static async Task RateDeepAsync(CheckContext ctx, DiagnosisProblem area, Noise noise)
    {
        var ra = area.Member == "RightAscensionRate";
        double Offset(double arcsec) => ra ? arcsec / 15 : arcsec;
        double PerSi(double offset) => ra ? offset * SiderealPerSi : offset;
        var unit = ra ? "s of RA per sidereal second" : "\"/s";
        var shortS = Math.Max(2, ctx.Options.DriftSeconds / 2.0);
        var longS = 3 * shortS;
        var id = MemberId(ctx, area.Member);
        var small = Offset(SmallArcsec);
        var e = PerSi(small); // ConformU's offset as it should move the mount, per SI second

        Task<Measured> M(double value, double seconds) => MeasureRateAsync(ctx, value, seconds, ra);
        var controlShort = await M(0, shortS);
        var controlLong = await M(0, longS);
        var plusShort = await M(small, shortS);
        var minusShort = await M(-small, shortS);
        var plusLong = await M(small, longS);
        var minusLong = await M(-small, longS);
        var largePlus = await M(Offset(LargeArcsec), shortS);
        var largeMinus = await M(-Offset(LargeArcsec), shortS);
        Measured[] all = [controlShort, controlLong, plusShort, minusShort, plusLong, minusLong, largePlus, largeMinus];

        string Rate(double v) => ra ? v.ToString("+0.00000;-0.00000;0.00000", CultureInfo.InvariantCulture) + " s of RA per second"
            : v.ToString("+0.0000;-0.0000;0.0000", CultureInfo.InvariantCulture) + "\"/s";
        var actual = string.Join('\n', all.Select(r =>
            (r.Value == 0 ? $"No offset, {Seconds(r)}: " : $"{area.Member} = {Show(r.Value)} {unit}, {Seconds(r)}: ") + (r.Fitted is { } f
                ? r.Value == 0 ? $"moved {Rate(r.Slope)} (standard error {Rate(r.SlopeError).TrimStart('+')})"
                : $"fitted rate {f:0.000} × expected ({Pct(f)}), first-to-last reading as ConformU {r.TwoPoint:0.000} ×"
                : r.Note)));
        static string Seconds(Measured r) => r.Seconds > 0 ? $"{r.Seconds:0} s" : "not measured";

        if (all.Any(r => r.Fitted is null))
        {
            ctx.Report(id, area.Member, Outcome.Error, $"{area.Member} couldn't be measured at tier 3: an offset was refused or the position couldn't be read.",
                expected: area.Said, actual: actual);
            return;
        }

        // Each length: how far the worse sign is off, and the offset's scale with whatever both signs share taken off.
        static double Dev(Measured p, Measured m) => Math.Max(Math.Abs(p.Fitted!.Value - 1), Math.Abs(m.Fitted!.Value - 1));
        double Scale(Measured p, Measured m) => (p.Slope - m.Slope) / (2 * e);
        var (devShort, devLong) = (Dev(plusShort, minusShort), Dev(plusLong, minusLong));
        var (kShort, kLong) = (Scale(plusShort, minusShort), Scale(plusLong, minusLong));
        var kLarge = (largePlus.Slope - largeMinus.Slope) / (2 * PerSi(Offset(LargeArcsec)));
        // A drift: movement with no offset beyond 5% of ConformU's offset (and three standard errors), the same at both lengths.
        bool Moves(Measured c) => Math.Abs(c.Slope) > Math.Max(0.05 * e, 3 * c.SlopeError);
        var drift = controlLong.Slope;
        var drifts = Moves(controlShort) && Moves(controlLong) && Math.Abs(controlShort.Slope - drift) < 0.5 * Math.Abs(drift);
        var lengths = $"over {shortS:0} s and {longS:0} s alike";

        if (devShort <= 0.05 && devLong <= 0.05 && !Moves(controlLong))
        {
            ctx.Report(id, area.Member, Outcome.Info,
                $"Not reproduced at tier 3: ConformU's offset moved the mount within 5% of the expected rate each way, over {shortS:0} s and " +
                $"{longS:0} s, and the mount held still with no offset.", expected: area.Said, actual: actual);
            return;
        }
        if (!drifts && devShort > 0.05 && devLong <= 0.1 && devLong < 0.5 * devShort)
        {
            ctx.Report(id, area.Member, Outcome.Warning,
                $"The rate is right: measured for longer ({longS:0} s) ConformU's offset comes within {devLong * 100:0}% each way, where over " +
                $"{shortS:0} s it was up to {devShort * 100:0}% out, and the mount holds still with no offset. An error in the readings is bounded, " +
                "so it shows less the longer the measurement; a wrong rate would be as wrong for longer. ConformU's result comes from the readings.",
                expected: area.Said, actual: actual,
                fix: Recommend(ctx, Cause.Resolution, small * shortS, ra ? noise.RaSpreadSec : noise.DecSpreadArcsec, ra),
                sourceMember: ra ? "RightAscension" : "Declination");
            return;
        }
        var offsetRight = Math.Abs(kLong - 1) <= 0.05;
        if (drifts)
        {
            var share = Math.Abs(drift) / e * 100;
            var offsetText = offsetRight
                ? $"The offset itself is right ({kLong:0.000} × once the drift is taken off, and 40\"/s at {kLarge:0.000} ×)"
                : $"The offset is off too: {kLong:0.000} × once the drift is taken off (40\"/s at {kLarge:0.000} ×)";
            ctx.Report(id, area.Member, Outcome.Fail,
                $"{area.Member}: the mount moves {Rate(drift)} with no offset at all, a drift of {share:0}% of ConformU's offset, {lengths}. " +
                $"{offsetText}, but measured as ConformU does the drift adds to one sign and takes from the other: +0.05\"/s at " +
                $"{plusLong.Fitted:0.00} ×, -0.05\"/s at {minusLong.Fitted:0.00} ×.",
                expected: area.Said, actual: actual,
                fix: Recommend(ctx, Cause.Drift, drift, 0, ra) + (offsetRight ? "" : " " + Recommend(ctx, Cause.SmallOffset, kLong, 0, ra)),
                sourceMember: area.Member);
            return;
        }
        if (!offsetRight && Math.Abs(kShort - kLong) <= 0.05 * Math.Max(1, Math.Abs(kLong)))
        {
            var largeRight = Math.Abs(kLarge - 1) <= 0.03;
            var how = kLong > 1 ? "rounded up (or scaled up) before it's applied"
                : Math.Abs(kLong) < 0.1 ? "ignored (rounded to nothing)" : "rounded down or held back";
            ctx.Report(id, area.Member, Outcome.Fail,
                $"{area.Member} applies ConformU's 0.05\"/s offset at {kLong:0.00} × at both signs, {lengths}, and the mount holds still with no " +
                (largeRight ? $"offset, while 40\"/s is right ({kLarge:0.000} ×): the small offset is {how}."
                    : $"offset; 40\"/s moves at {kLarge:0.000} × too: the offset is scaled wrongly at every size." + Hint(kLarge)),
                expected: area.Said, actual: actual,
                fix: Recommend(ctx, largeRight ? Cause.SmallOffset : Cause.Scale, kLong, 0, ra), sourceMember: area.Member);
            return;
        }
        ctx.Report(id, area.Member, Outcome.Fail,
            $"{area.Member} doesn't settle into one pattern: ConformU's offset is up to {devShort * 100:0}% out over {shortS:0} s and " +
            $"{devLong * 100:0}% over {longS:0} s ({kShort:0.00} × and {kLong:0.00} × once what both signs share is taken off), " +
            $"with {Rate(drift)} of movement with no offset. The offset isn't applied steadily.",
            expected: area.Said, actual: actual, fix: Recommend(ctx, Cause.Inconsistent, small * shortS, ra ? noise.RaSpreadSec : noise.DecSpreadArcsec, ra),
            sourceMember: area.Member);
    }

    /// <summary>
    /// Tier 3's last word on a member: whether its measurements confirm or overturn the tier 2 diagnosis it follows up
    /// (and that diagnosis's recommendation), with the final recommendation.
    /// </summary>
    private static void FinalAnalysis(CheckContext ctx, DiagnosisProblem area)
    {
        var causeId = $"{ctx.Group}.{MemberId(ctx, area.Member)}";
        if (ctx.Results.LastOrDefault(r => r.Id == causeId) is not { } cause || cause.Outcome == Outcome.Skipped) return;
        var tier2 = ctx.FollowsUp is { } from ? Tiers.ProblemsWith(from, area.Member).OrderByDescending(r => r.Outcome.Severity()).FirstOrDefault() : null;
        var verdict = (tier2?.Outcome, cause.Outcome) switch
        {
            (_, Outcome.Error) => "tier 3 couldn't measure it, so tier 2's recommendation stands unverified",
            (null, _) => "tier 2's finding isn't at hand to compare with, so this is tier 3's own",
            (_, Outcome.Info) => "tier 3 doesn't confirm tier 2: measured more thoroughly, the problem didn't show",
            (Outcome.Fail, Outcome.Fail) => "tier 3 confirms tier 2's fault in the driver, and pins down its cause",
            (Outcome.Fail, Outcome.Warning) => "tier 3 overturns tier 2: the driver's rate is right, and the result comes from the position readings",
            (Outcome.Warning, Outcome.Warning) => "tier 3 confirms tier 2: the rate is right, and the position readings explain the result",
            (Outcome.Warning, Outcome.Fail) => "tier 3 overturns tier 2: there is a fault in the driver after all",
            _ => "tier 3 measured it again"
        };
        ctx.Report(Id(ctx, $"{area.Member}.Summary"), area.Member, cause.Outcome,
            $"Final analysis of {area.Member}: {verdict}. {cause.Message}",
            expected: tier2 is null ? area.Said
                : $"Tier 2 ({tier2.Id}, {tier2.Outcome}): {tier2.Message}" + (tier2.Fix is { } fix ? $"\nTier 2's recommendation: {fix}" : ""),
            actual: cause.Actual, fix: cause.Fix, sourceMember: area.Member);
    }

    /// <summary>What a ratio near a well-known factor suggests.</summary>
    private static string Hint(double ratio)
    {
        static bool Near(double r, double f) => Math.Abs(r / f - 1) < 0.02;
        if (Near(ratio, 15) || Near(ratio, 1 / 15.0)) return " A factor of 15 is the difference between seconds of RA and arc-seconds.";
        if (Near(ratio, 3600) || Near(ratio, 1 / 3600.0)) return " A factor of 3600 is the difference between degrees and arc-seconds.";
        if (Near(ratio, -1)) return " The offset moves the mount the wrong way.";
        if (Math.Abs(ratio - SiderealPerSi) < 0.001 || Math.Abs(ratio - 1 / SiderealPerSi) < 0.001)
            return " A factor of 1.0027 is the difference between sidereal and SI seconds.";
        return "";
    }

    // ---------- PulseGuide ----------

    private sealed record Pulse(int Ms, double Expected, double Immediate, double Later);

    private static async Task PulseGuideAsync(CheckContext ctx, DiagnosisProblem area, Noise noise)
    {
        var (rateRa, rateDec) = await MotionChecks.GuideRatesAsync(ctx);
        // ConformU pulses for 5 s; the shorter two show how the error grows with the pulse.
        var longest = (int)Math.Clamp(ctx.Options.DriftSeconds * 250.0, 1000, 5000);
        int[] durations = [longest / 5, longest / 2, longest];
        // Long enough after the pulse for any later position update to arrive.
        var later = (int)Math.Max(600, 2 * (noise.UpdateMs ?? 300));

        foreach (var (axis, directions, rate) in new (string, GuideDirection[], double?)[]
                 {
                     ("Dec", [GuideDirection.North, GuideDirection.South], rateDec),
                     ("RA", [GuideDirection.East, GuideDirection.West], rateRa)
                 })
        {
            var guideRate = rate ?? MotionChecks.AssumedGuideRate; // degrees per second
            var pulses = new List<Pulse>();
            var errors = new List<string>();
            foreach (var direction in directions)
            foreach (var ms in durations)
            {
                var before = await MotionChecks.PositionAsync(ctx);
                var call = await ctx.TryAsync(t => t.PulseGuide(direction, ms), ctx.CallTimeout + TimeSpan.FromMilliseconds(ms));
                if (!call.Ok) { errors.Add($"PulseGuide({direction}, {ms}): {call.ErrorText}"); continue; }
                await ctx.WaitUntilAsync(t => !t.IsPulseGuiding, TimeSpan.FromMilliseconds(ms + 3000), 20);
                var immediate = await MotionChecks.PositionAsync(ctx);
                await Task.Delay(later, ctx.Token);
                var after = await MotionChecks.PositionAsync(ctx);
                if (before is not { } b || immediate is not { } i || after is not { } a) { errors.Add($"PulseGuide({direction}, {ms}): the position couldn't be read"); continue; }
                // Arc-seconds moved along the axis (RA as degrees of the RA axis, as ConformU and the guide rate measure it).
                double Moved((double Ra, double Dec) p) => axis == "Dec" ? Math.Abs(p.Dec - b.Dec) * 3600 : Math.Abs(Values.HoursDiff(p.Ra, b.Ra)) * 15 * 3600;
                pulses.Add(new Pulse(ms, guideRate * ms / 1000 * 3600, Moved(i), Moved(a)));
            }
            ReportPulses(ctx, area, axis, guideRate, pulses, errors, later);
        }
    }

    private static void ReportPulses(CheckContext ctx, DiagnosisProblem area, string axis, double guideRate, List<Pulse> pulses,
        List<string> errors, int laterMs)
    {
        var id = Id(ctx, $"PulseGuide.{axis}");
        var conformU = area.Said;
        var actual = string.Join('\n', pulses.Select(p =>
            $"{p.Ms} ms: expected {p.Expected:0.00}\", read {p.Immediate:0.00}\" as the pulse ended, {p.Later:0.00}\" {laterMs} ms later")
            .Concat(errors));
        if (pulses.Count < 3)
        {
            ctx.Report(id, "PulseGuide", Outcome.Error, $"The {axis} pulses couldn't be measured.", expected: conformU, actual: actual);
            return;
        }

        // ConformU's tolerance: 1" in Dec, 0.07 s of RA (about 1") in RA.
        const double tolerance = 1.0;
        var shortNow = pulses.Average(p => p.Expected - p.Immediate);
        var shortLater = pulses.Average(p => p.Expected - p.Later);
        var late = shortNow - shortLater;
        var arcsecPerMs = guideRate * 3600 / 1000;

        // The movement against the pulse's length: slope = the real guide rate, intercept = time lost (or gained) per pulse.
        var n = pulses.Count;
        var mx = pulses.Average(p => (double)p.Ms);
        var my = pulses.Average(p => p.Later);
        var sxx = pulses.Sum(p => (p.Ms - mx) * (p.Ms - mx));
        var slope = sxx > 0 ? pulses.Sum(p => (p.Ms - mx) * (p.Later - my)) / sxx : 0; // arc-seconds per ms
        var intercept = my - slope * mx;
        var rateRatio = slope / arcsecPerMs;
        var lossMs = slope > 0 ? -intercept / slope : 0;
        var fit = $"Fitted: moves at {rateRatio:0.000} × the guide rate, {(lossMs >= 0 ? "losing" : "gaining")} {Math.Abs(lossMs):0} ms per pulse.";
        actual += "\n" + fit;

        if (Math.Abs(shortNow) <= tolerance / 2 && Math.Abs(shortLater) <= tolerance / 2)
        {
            ctx.Report(id, "PulseGuide", Outcome.Info,
                $"Not reproduced: the {axis} pulses moved the expected distance (within {tolerance / 2:0.0}\"), read as each pulse ended.",
                expected: conformU, actual: actual);
            return;
        }
        if (late >= 0.5 && Math.Abs(shortLater) <= Math.Max(tolerance / 2, Math.Abs(shortNow) * 0.4))
        {
            ctx.Report(id, "PulseGuide", Outcome.Warning,
                $"The {axis} pulses move the right distance, but the position read as each pulse ends is {shortNow:0.0}\" short on average " +
                $"(about {shortNow / arcsecPerMs:0} ms of guiding); {laterMs} ms later the rest is there. The position reading is stale, not the pulse.",
                expected: conformU, actual: actual, fix: Recommend(ctx, Cause.StalePosition, shortNow / arcsecPerMs, 0, axis == "RA"),
                sourceMember: axis == "Dec" ? "Declination" : "RightAscension");
            return;
        }
        if (Math.Abs(rateRatio - 1) > 0.03)
        {
            ctx.Report(id, "PulseGuide", Outcome.Fail,
                $"The {axis} pulses move at {rateRatio:0.000} × GuideRate{(axis == "RA" ? "RightAscension" : "Declination")}: the shortfall grows with the " +
                "pulse's length, so the guide rate applied isn't the one reported.",
                expected: conformU, actual: actual, fix: Recommend(ctx, Cause.GuideRate, rateRatio, 0, axis == "RA"), sourceMember: "PulseGuide");
            return;
        }
        ctx.Report(id, "PulseGuide", Outcome.Fail,
            $"Every {axis} pulse {(lossMs >= 0 ? "loses" : "gains")} about {Math.Abs(lossMs):0} ms of movement whatever its length " +
            $"({Math.Abs(shortLater):0.0}\" on average): the rate is right, but the pulse starts late or stops early.",
            expected: conformU, actual: actual, fix: Recommend(ctx, Cause.PulseTime, lossMs, 0, axis == "RA"), sourceMember: "PulseGuide");
    }

    // ---------- What each pattern points to ----------

    private enum Cause { Resolution, StrayReading, Scale, Direction, Inconsistent, Drift, SmallOffset, StalePosition, GuideRate, PulseTime }

    /// <summary>
    /// The recommendation for a pattern: what to look at, and for GSServer where in its code (the simulator's or the
    /// SkyWatcher code, by the Mount option; both when it isn't set). Only recommendations: the harness changes nothing.
    /// </summary>
    private static string Recommend(CheckContext ctx, Cause cause, double value, double spread, bool ra)
    {
        var gs = ctx.Target.Codebase == Codebase.GSServer;
        var mount = ctx.Options.Mount;
        var sim = mount is MountKind.Simulator or MountKind.Unknown;
        var real = mount is MountKind.RealMount or MountKind.Unknown;
        var both = mount == MountKind.Unknown ? " (Mount isn't set, so both code paths are listed: set it under the server on the Server card)" : "";
        var lines = new List<string>();
        string F(double v, string f) => v.ToString(f, CultureInfo.InvariantCulture);

        switch (cause)
        {
            case Cause.Resolution:
                lines.Add("Nothing is wrong with the rate itself. ConformU measures a 0.05\"/s offset over 10 s, a movement no bigger than the " +
                          "position readings' own scatter, so its result swings either way from run to run.");
                if (gs)
                {
                    lines.Add($"In GSServer{both}: RightAscension and Declination (GS.Ascom.SkyTelescope/Telescope.cs) return SkyServer.RightAscensionXForm " +
                              "and DeclinationXForm, which SkyServer.UpdateServerEvent works out from the axis positions with the sidereal time at that moment.");
                    if (sim)
                        lines.Add("Simulator: GS.Simulator/Controllers.cs MoveAxes moves the axes once every 20 ms, so a tracking axis advances in steps " +
                                  "of about 0.3\" (15\"/s × 20 ms) while the sidereal time used for RA runs smoothly: RA jitters by up to about 0.02 s. " +
                                  "Recommendation: when the simulator answers a position request (the \"degrees\" command in Controllers.cs), add the movement " +
                                  "since _lastUpdateTime (each rate × the time elapsed), so it reports where the axis is now rather than at the last 20 ms tick.");
                    if (real)
                        lines.Add("Real mount: the axis position is a motor step count, and one step is a fraction of an arc-second. Recommendation: " +
                                  "take the sidereal time for the RA conversion from when the step counts were read, not when they are converted, so " +
                                  "the reading doesn't jump between samples.");
                    lines.Add("To confirm, run ConformU again with a longer rate offset test if your ConformU version has that setting: the error " +
                              "shrinks as the test gets longer if it's the readings.");
                }
                else
                {
                    lines.Add("Make the position readings finer or steadier (compute them for the moment they're read), or run ConformU's rate " +
                              "test for longer to confirm.");
                }
                break;

            case Cause.StrayReading:
                lines.Add("Nothing is wrong with the rate itself. One position reading at the start or end of ConformU's measurement was out " +
                          "of line with the readings around it, and from only those two readings ConformU can't tell. Run ConformU again: if " +
                          "it doesn't come back, it was a one-off.");
                if (gs)
                    lines.Add($"In GSServer{both}: if it keeps happening, look at what RightAscension and Declination (GS.Ascom.SkyTelescope/Telescope.cs) " +
                              "return between position updates: SkyServer.UpdateServerEvent works out SkyServer.RightAscensionXForm and " +
                              "DeclinationXForm from the axis positions and the sidereal time, and both must come from the same moment.");
                else
                    lines.Add("If it keeps happening, look at how the position properties are worked out: the axis positions and the sidereal " +
                              "time used must come from the same moment, and a reading mustn't see a half-updated position.");
                break;

            case Cause.Scale or Cause.Direction or Cause.Inconsistent:
                lines.Add(cause switch
                {
                    Cause.Scale => "The offset is converted or applied with the wrong factor.",
                    Cause.Direction => "The offset's sign is handled differently from its size: check the direction logic.",
                    _ => "The offset isn't applied steadily: check what else changes the tracking rate while an offset is set."
                });
                if (gs)
                {
                    lines.Add($"In GSServer{both}: the RightAscensionRate setter in GS.Ascom.SkyTelescope/Telescope.cs converts with " +
                              "Conversions.ArcSec2Deg(Conversions.SideSec2ArcSec(value)) (seconds of RA per sidereal second × 15 × 1.0027 → degrees " +
                              "per SI second) into SkyServer.RateRa; DeclinationRate is arc-seconds per SI second into SkyServer.RateDec. " +
                              "SkyServer.ActionRateRaDec then calls SetTracking, and SkyServer.SkyGetRate adds GetRaRateDirection(RateRa) and " +
                              "GetDecRateDirection(RateDec), which decide the sign from the hemisphere and side of pier.");
                    lines.Add("SkyServer.CurrentTrackingRate also adds TrackingOffsetRaRate (custom gearing) and SkySettings.RaTrackingOffset to " +
                              "the tracking rate: check that neither scales the offset too.");
                    if (sim) lines.Add("Simulator: GS.Simulator/Controllers.cs RaDecRate applies the rate on each 20 ms tick.");
                    if (real) lines.Add("Real mount: GS.SkyWatcher works the motor speed out with CalculateSpeed: check the units it's given.");
                }
                if (cause == Cause.Inconsistent && ctx.Options.Tier < Tiers.Max)
                    lines.Add("Run tier 3 to tell a drift with no offset, readings that wander and a rounded small offset apart.");
                break;

            case Cause.Drift:
            {
                var expected = ra ? SmallArcsec / 15 * SiderealPerSi : SmallArcsec;
                var drift = ra ? $"{F(value, "+0.00000;-0.00000")} s of RA" : $"{F(value, "+0.0000;-0.0000")}\"";
                lines.Add($"Nothing is wrong with the offset's conversion: with no offset at all the mount moves {drift} every second, which " +
                          $"against ConformU's 0.05\"/s is a {F(Math.Abs(value) / expected * 100, "0")}% error, added to one sign and taken from the other.");
                if (gs)
                {
                    if (ra)
                    {
                        // RaTrackingOffset is parts per 100000 of the tracking rate (arc-seconds per second, about 15.041 at sidereal).
                        var parts = Math.Abs(value) * 15 / 15.0410671787 * 100000;
                        lines.Add($"In GSServer{both}: SkyServer.CurrentTrackingRate (GS.Server/SkyTelescope/SkyServer.cs) makes the tracking rate from " +
                                  "SkySettings.SiderealRate, adds TrackingOffsetRaRate (the custom gearing offset, SkySettings.CustomRaTrackingOffset) and PEC, " +
                                  "then SkySettings.RaTrackingOffset (in parts per 100000 of the rate). Any of these that isn't zero, or a SiderealRate other " +
                                  $"than 15.0410671787, makes RA drift with tracking on: this drift is what RaTrackingOffset ≈ {F(parts, "0")} would give.");
                        lines.Add("Recommendation: check those settings on the server (RA tracking offset, custom gearing, PEC) and set them to 0 for " +
                                  "ConformU. If they are all 0, RA is being worked out with a different sidereal rate from the one the mount tracks at.");
                    }
                    else
                    {
                        lines.Add($"In GSServer{both}: with tracking on and no offset, Dec must hold still. Check that the custom gearing offset " +
                                  "(TrackingOffsetDecRate, from SkySettings.CustomDecTrackingOffset, in SkyServer.cs) doesn't reach the Dec axis, and that " +
                                  "SkyServer.SkyGetRate's GetDecRateDirection(RateDec) is 0 when DeclinationRate is 0.");
                    }
                    if (sim) lines.Add("Simulator: GS.Simulator/Controllers.cs Tracking() moves the axis by the rate it's given × the time elapsed each tick, " +
                                       "so it drifts exactly as much as the rate CurrentTrackingRate gives it is off.");
                    if (real) lines.Add("Real mount: GS.SkyWatcher turns the tracking rate into a whole :I motor speed (CalculateSpeed), so a sidereal rate " +
                                        "off by part of a step drifts too.");
                }
                else
                {
                    lines.Add("Check that with tracking on and no offset the mount runs at exactly the sidereal rate its position readings assume.");
                }
                break;
            }

            case Cause.SmallOffset:
                lines.Add($"ConformU's small offset is applied at {F(value, "0.00")} × while 40\"/s is right: it's rounded, or given a floor, " +
                          "somewhere between the property and the motor.");
                if (gs)
                {
                    lines.Add($"In GSServer{both}: the {(ra ? "RightAscensionRate" : "DeclinationRate")} setter in GS.Ascom.SkyTelescope/Telescope.cs " +
                              "converts the value without rounding and SkyServer.SkyGetRate adds it to the tracking rate, so look further down.");
                    if (sim) lines.Add("Simulator: GS.Simulator/Actions.cs RaDecRate sends the rate as text (\"radecrate|axis|{degrees}\") and " +
                                       "Controllers.cs reads it back with Convert.ToDouble: check nothing on that path cuts it to too few digits.");
                    if (real) lines.Add("Real mount: GS.SkyWatcher CalculateSpeed turns the tracking rate plus the offset into a whole :I motor speed, so " +
                                        "an offset smaller than one :I step at sidereal rate is rounded to whole steps: compare one step there with 0.05\"/s. " +
                                        "Recommendation: carry the rounding remainder over between speed updates, or apply small offsets as timed pulses.");
                }
                else
                {
                    lines.Add("Look for rounding of the rate (or a minimum step) between the property and the motor speed.");
                }
                break;

            case Cause.StalePosition:
                lines.Add($"The movement is right; the position is read before it has been updated (about {F(value, "0")} ms behind).");
                if (gs)
                {
                    lines.Add($"In GSServer{both}: RightAscension and Declination (GS.Ascom.SkyTelescope/Telescope.cs) return SkyServer.RightAscensionXForm " +
                              "and DeclinationXForm, which only SkyServer.UpdateServerEvent refreshes, every SkySettings.DisplayInterval ms (300 by default). " +
                              "When a pulse ends, IsPulseGuiding goes false before the next refresh, so a client that reads the position at once " +
                              "(as ConformU and guiding software do) gets one up to 300 ms old.");
                    if (sim) lines.Add("Simulator: GS.Simulator/Actions.cs AxisPulse clears MountQueue.IsPulseGuidingRa/Dec in its finally block, just after sending \"pulse|axis|0\".");
                    if (real) lines.Add("Real mount: GS.SkyWatcher/SkyWatcher.cs AxisPulse clears SkyQueue.IsPulseGuidingRa/Dec after restoring the tracking speed.");
                    lines.Add("Recommendation: refresh the position (the steps request and conversion UpdateServerEvent makes) when a pulse ends, " +
                              "before IsPulseGuiding is cleared; or have the position properties refresh when the cached value is older than about " +
                              "100 ms. A smaller DisplayInterval narrows the gap but adds serial traffic on a real mount.");
                }
                else
                {
                    lines.Add("Update the reported position when a pulse ends, before IsPulseGuiding goes false, or compute it when it is read.");
                }
                break;

            case Cause.GuideRate:
                lines.Add($"The pulse moves at {F(value, "0.000")} × the guide rate the driver reports.");
                if (gs)
                {
                    lines.Add($"In GSServer{both}: SkyServer.PulseGuide takes Math.Abs(GuideRateRa or GuideRateDec) (degrees per second) and sets its sign.");
                    if (sim) lines.Add("Simulator: GS.Simulator/Actions.cs AxisPulse sends \"pulse|axis|rate\"; GS.Simulator/Controllers.cs applies it as the " +
                                       "slew rate (_slewX/_slewY, moved in Slew()), not through _pulseX/_pulseY: check it isn't also scaled there.");
                    if (real) lines.Add("Real mount: GS.SkyWatcher/SkyWatcher.cs AxisPulse adds the guide rate to the tracking rate in radians per second " +
                                        "(applyRate) and CalculateSpeed rounds it to a whole :I speed: check the conversion and the rounding.");
                }
                break;

            case Cause.PulseTime:
                lines.Add($"About {F(Math.Abs(value), "0")} ms is {(value >= 0 ? "lost" : "gained")} in every pulse, whatever its length: the axis starts " +
                          "moving later (or stops sooner) than the pulse's timer.");
                if (gs)
                {
                    lines.Add($"In GSServer{both}: SkyServer.PulseGuide queues the pulse (CmdAxisPulse or SkyAxisPulse), and the pulse's own Stopwatch " +
                              "starts only once the command has been sent.");
                    if (sim) lines.Add("Simulator: GS.Simulator/Actions.cs AxisPulse runs in Task.Run and the simulator applies rates on its 20 ms MoveAxes tick.");
                    if (real) lines.Add("Real mount: GS.SkyWatcher/SkyWatcher.cs AxisPulse changes the :I speed (and may stop and restart the axis on a " +
                                        "direction change), the motor needs time to reach the new speed, and Dec backlash (SkySettings.DecBacklash) " +
                                        "is added on a direction change; MinPulseDurationRa/Dec drop short pulses.");
                    lines.Add($"Recommendation: measure the delay from the command to the axis moving and extend each pulse by it (about {F(Math.Abs(value), "0")} ms here), " +
                              "or time the pulse from when the axis starts moving.");
                }
                break;
        }
        return string.Join(' ', lines);
    }

    private static string Show(double v) => v.ToString("0.#####", CultureInfo.InvariantCulture);

    /// <summary>A ratio's difference from 1 as a signed percentage ("+2.5%", "0.0%").</summary>
    private static string Pct(double ratio)
    {
        var pct = Math.Round((ratio - 1) * 100, 1);
        return pct == 0 ? "0.0%" : pct.ToString("+0.0;-0.0", CultureInfo.InvariantCulture) + "%";
    }
}
