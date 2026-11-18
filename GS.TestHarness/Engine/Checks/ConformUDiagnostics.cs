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

/// <summary>
/// Measurements made for particular ConformU problems, to tell their causes apart (part of the ConformU group, so only
/// in a run started from a ConformU log). ConformU only says a value was outside its tolerance; these measure how:
/// <list type="bullet">
/// <item>RightAscensionRate / DeclinationRate outside tolerance: the offset ConformU uses and one 800 times larger, each
/// way, against how much the position readings scatter with no offset. A rate that is wrong is wrong at both sizes; a
/// measurement limited by the readings is only wrong at ConformU's tiny one.</item>
/// <item>PulseGuide moved too little or too much: pulses of three lengths each way, the position read as the pulse ends
/// and again a moment later. Movement that only shows later is a stale position reading; a shortfall that grows with
/// the pulse is a wrong guide rate; one that doesn't is time lost in every pulse.</item>
/// </list>
/// Each finding's fix says what the pattern points to and, for GSServer, where in its code to look (the simulator's
/// code or the SkyWatcher code, by the Mount option). The harness only recommends: it never changes the server.
/// </summary>
internal static class ConformUDiagnostics
{
    /// <summary>The members a diagnosis exists for.</summary>
    public static readonly string[] Members = ["RightAscensionRate", "DeclinationRate", "PulseGuide"];

    /// <summary>ConformU's rate offset test value: 0.05 arc-seconds per second (RightAscensionRate in s of RA per sidereal second).</summary>
    private const double SmallArcsec = 0.05;

    /// <summary>The large offset: ConformU's other test value, 40 arc-seconds per second.</summary>
    private const double LargeArcsec = 40;

    /// <summary>Sidereal seconds to SI seconds: RightAscensionRate is per sidereal second.</summary>
    private const double SiderealPerSi = 1.0027379;

    public static async Task RunAsync(CheckContext ctx, ConformULog log)
    {
        var areas = log.Areas.Where(a => Members.Contains(a.Member, StringComparer.OrdinalIgnoreCase)).ToList();
        if (areas.Count == 0) return;
        if (!ctx.Options.AllowMotion)
        {
            foreach (var a in areas)
                ctx.Report($"Diagnose.{a.Member}", a.Member, Outcome.Skipped,
                    $"The {a.Member} diagnosis moves the mount: turn on Move the mount under Options and run again.");
            return;
        }
        if ((await ctx.TryAsync(t => t.AtPark)).Value)
        {
            foreach (var a in areas)
                ctx.Report($"Diagnose.{a.Member}", a.Member, Outcome.Skipped, $"The mount is parked, so the {a.Member} diagnosis wasn't made.");
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
                        await RateAsync(ctx, a, noise);
                        break;
                    case "PulseGuide" when c.CanPulseGuide:
                        await PulseGuideAsync(ctx, a, noise);
                        break;
                    default:
                        ctx.Report($"Diagnose.{a.Member}", a.Member, Outcome.Skipped,
                            $"The mount says {a.Member} can't be used (its Can property is false), so it wasn't diagnosed.");
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

        ctx.Report("Diagnose.Readings", "RightAscension", Outcome.Info,
            $"Tracking with no offset, the position was read {noise.Reads} times over {watch.Elapsed.TotalSeconds:0.0} s: RA spread " +
            $"{noise.RaSpreadSec:0.000} s ({noise.RaSpreadSec * 15:0.00}\"), Dec spread {noise.DecSpreadArcsec:0.00}\"; it changed " +
            $"{noise.Changes} times" + (updateMs is { } u ? $", about every {u:0} ms." : "."),
            expected: "the same position every time (tracking holds RA and Dec still)",
            actual: $"RA spread {noise.RaSpreadSec:0.000} s, Dec spread {noise.DecSpreadArcsec:0.00}\", {noise.Changes} changes in {noise.Reads} reads");
        return noise;
    }

    // ---------- RightAscensionRate and DeclinationRate ----------

    private static async Task RateAsync(CheckContext ctx, ConformUArea area, Noise noise)
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
        var actual = string.Join('\n', results.Select(r => $"{area.Member} = {Show(r.Value)} {unit}: " + (r.Fitted is { } f
            ? $"fitted rate {f:0.000} × expected ({Pct(f)}), first-to-last reading as ConformU {r.TwoPoint:0.000} × ({Pct(r.TwoPoint!.Value)})"
            : r.Note)));
        var smallMove = (ra ? SmallArcsec / 15 : SmallArcsec) * seconds;
        var spread = ra ? noise.RaSpreadSec : noise.DecSpreadArcsec;
        var moveText = ra ? $"{smallMove:0.000} s of RA" : $"{smallMove:0.00}\"";
        var spreadText = ra ? $"{spread:0.000} s" : $"{spread:0.00}\"";
        var id = $"Diagnose.{area.Member}";
        var conformU = ConformUChecks.Said(area);

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
            var at = smallWrong.Average(r => r.Fitted!.Value);
            ctx.Report(id, area.Member, Outcome.Fail,
                $"{area.Member} is right at 40\"/s but not at ConformU's 0.05\"/s: small offsets move the mount at about {at:0.00} × the expected " +
                "rate, beyond what the readings' scatter explains. A small offset is rounded, held back or ignored.",
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
            var stray = worstSmall.EndOff > Math.Max(3 * worstSmall.Scatter, 0.1 * smallMove);
            var rateRight = $"The rate is right (the 40\"/s offsets track within {fittedDev * 100:0.0}%, and ConformU's small one fitted over every " +
                            "reading is too), but measured as ConformU does, from one reading at each end, " +
                            $"{area.Member} = {Show(worstSmall.Value)} is off by {Math.Abs(worstSmall.TwoPoint!.Value - 1) * 100:0}%: ";
            ctx.Report(id, area.Member, Outcome.Warning,
                rateRight + (stray
                    ? $"one of those two readings was {Amount(worstSmall.EndOff)} out of line with the readings around it (which scatter by only " +
                      $"{Amount(worstSmall.Scatter)}), against a movement of {moveText} in {seconds:0} s. A single stray reading, not the rate."
                    : $"it moves only {moveText} in {seconds:0} s, and the readings scatter by {spreadText} with no offset at all."),
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
    /// <param name="EndOff">How far the first or last reading (the ones ConformU uses) is from the fitted line, the larger
    /// (seconds of RA, or arc-seconds).</param>
    /// <param name="Scatter">The other readings' typical distance from the line (RMS, same units).</param>
    private sealed record Measured(double Value, double? Fitted, double? FittedError, double? TwoPoint, string Note,
        double EndOff = 0, double Scatter = 0);

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
            double Off((double T, double V) x) => Math.Abs(x.V - (mv + slope * (x.T - mt)));
            var inner = samples.Skip(1).SkipLast(1).ToList();
            var scatter = inner.Count > 0 ? Math.Sqrt(inner.Average(x => Off(x) * Off(x))) : 0;
            return new Measured(value, slope / expected, error / Math.Abs(expected), twoPoint / expected, "",
                Math.Max(Off(samples[0]), Off(samples[^1])), scatter);
        }
        finally
        {
            await ctx.TryAsync(t => { if (ra) t.RightAscensionRate = 0; else t.DeclinationRate = 0; });
        }
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

    private static async Task PulseGuideAsync(CheckContext ctx, ConformUArea area, Noise noise)
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

    private static void ReportPulses(CheckContext ctx, ConformUArea area, string axis, double guideRate, List<Pulse> pulses,
        List<string> errors, int laterMs)
    {
        var id = $"Diagnose.PulseGuide.{axis}";
        var conformU = ConformUChecks.Said(area);
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

    private enum Cause { Resolution, StrayReading, Scale, Direction, Inconsistent, StalePosition, GuideRate, PulseTime }

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
