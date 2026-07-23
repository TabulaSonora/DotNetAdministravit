using System.Globalization;
using TabulaSonora.Dsp;
using TabulaSonora.Patches;
using TabulaSonora.Rom;

namespace TabulaSonora.Tests;

/// <summary>
/// Conformance of the cutoff chain against the real engine's own runtime registers, captured per
/// control tick by the <c>tvftrace</c> harness.
/// </summary>
/// <remarks>
/// <para>
/// The trace does not record which patch it was taken from. Searching all 3,089 real partials for one
/// whose static registers match the trace's constant columns — base cutoff 14284, filter type 0,
/// resonance byte 23 — yields exactly one candidate: tone 871, "Sweep Pad", partial 0. That
/// identification is asserted below, so a change that breaks it fails loudly rather than silently
/// testing the wrong patch.
/// </para>
/// <para>
/// The capture velocity is also unrecorded, but this patch's velocity level-scale bytes are all
/// neutral, so its filter envelope is velocity-independent and the comparison does not depend on
/// recovering it.
/// </para>
/// </remarks>
public class TvfTraceTests
{
    private const int SweepPadTone = 871;
    private const int SweepPadSlot = 0;
    private const int TraceKey = 60;
    private const double TraceHoldSeconds = 2.4;
    private const int ControlBlock = 320;

    /// <summary>
    /// Documented agreement for the runtime cutoff is 0.086–0.16%; measured worst case here is 0.185%.
    /// </summary>
    private const double CutoffTolerance = 0.0025;

    [SkippableFact]
    public void SweepPadIsTheOnlyPatchMatchingTheTraceRegisters()
    {
        var tables = TableSet.FromCacheDirectory(TestData.RequireTables());
        var directory = new PatchDirectory(tables);
        var tvf = new TvfChain(tables, new EnvelopeMachine(tables));

        var partial = directory.GetPartialBySlot(SweepPadTone, SweepPadSlot);
        Assert.Equal("Sweep Pad", directory.GetTone(SweepPadTone)!.Name);

        Assert.Equal(0, partial.FilterType);
        Assert.Equal(23, TvfChain.ResonanceByte(partial));

        var (peak, _, _) = tvf.EnvelopeOffsets(partial, TraceKey, velocity: 100);
        Assert.Equal(14_284, Math.Min(0x7FFF, (partial.CutoffBase * 0x100) + peak));

        // Resonance 23 is well below the neutral 64, so this patch is genuinely resonant:
        // q is reciprocal-Q, giving Q = 64/23 which is about 2.78.
        Assert.Equal(64.0 / 23.0, 1.0 / tvf.DampingCoefficient(0, 23, filterType: 1), 6);
    }

    [SkippableFact]
    public void RuntimeCutoffTracksTheEngineAcrossTheWholeSweep()
    {
        var tracePath = TestData.RequireTrace("tvftrace_sweep.csv");
        var expected = ReadColumn(tracePath, "cc_cutoff");
        Assert.Equal(240, expected.Count);

        var tables = TableSet.FromCacheDirectory(TestData.RequireTables());
        var directory = new PatchDirectory(tables);
        var tvf = new TvfChain(tables, new EnvelopeMachine(tables));

        var partial = directory.GetPartialBySlot(SweepPadTone, SweepPadSlot);
        var resonance = TvfChain.ResonanceByte(partial);

        var cutoff15 = tvf.Envelope(partial, velocity: 100, TraceKey, TraceHoldSeconds, tailSeconds: 0.05);

        var worst = 0.0;
        var total = 0.0;
        for (var tick = 0; tick < expected.Count; tick++)
        {
            var sample = Math.Min(tick * ControlBlock, cutoff15.Length - 1);
            var ours = tvf.CutoffUnits(cutoff15[sample], resonance);
            var error = Math.Abs(ours - expected[tick]) / Math.Max(expected[tick], 1.0);

            worst = Math.Max(worst, error);
            total += error;
        }

        var mean = total / expected.Count;
        Assert.True(worst < CutoffTolerance,
            $"Worst cutoff error {worst:P4} exceeds {CutoffTolerance:P4} (mean {mean:P4}).");

        // The sweep really does sweep: a flat trajectory would pass the error check trivially.
        Assert.True(expected[^1] - expected[0] > 50_000,
            "Expected the trace to sweep the cutoff upward by a wide margin.");
    }

    [SkippableFact]
    public void EnvelopeShapeIsKeyIndependentButItsPeakFollowsTheKey()
    {
        // Worth stating precisely, because the notes' summary claim that "cutoff is note-independent"
        // is true only of the cutoff *base*. The envelope *depth* has its own key-follow, selected by
        // the low nibble of block[0x32], and it is non-zero on 1,299 of the 3,089 real partials.
        var tables = TableSet.FromCacheDirectory(TestData.RequireTables());
        var directory = new PatchDirectory(tables);
        var tvf = new TvfChain(tables, new EnvelopeMachine(tables));
        var partial = directory.GetPartialBySlot(SweepPadTone, SweepPadSlot);

        Assert.Equal(3, partial.Raw[0x32] & 0x0F);

        var (lowPeak, lowSegments, lowRelease) = tvf.EnvelopeOffsets(partial, key: 36, velocity: 100);
        var (highPeak, highSegments, highRelease) = tvf.EnvelopeOffsets(partial, key: 96, velocity: 100);

        // The shape is identical: every offset is expressed relative to the peak.
        Assert.Equal(lowSegments, highSegments);
        Assert.Equal(lowRelease, highRelease);

        // The peak is not: it moves the whole envelope up the spectrum with the key.
        Assert.Equal(-5740, lowPeak);
        Assert.Equal(6560, highPeak);
        Assert.Equal(9364, Math.Min(0x7FFF, (partial.CutoffBase * 0x100) + lowPeak));
        Assert.Equal(21_664, Math.Min(0x7FFF, (partial.CutoffBase * 0x100) + highPeak));
    }

    [SkippableFact]
    public void EveryEnvelopeDepthKeyFollowRowSlopesWithTheKey()
    {
        // There is no "key-follow off" setting. The nibble of block[0x32] chooses which curve is
        // used, not whether one is; row 0 spans -16384 to 16128 just as the others do. Combined with
        // the rate key-follow on block[0x46]/[0x47], that makes every partial's filter envelope
        // key-dependent in both level and timing.
        var tables = TableSet.FromCacheDirectory(TestData.RequireTables());

        for (var row = 0; row < 4; row++)
        {
            var lowest = short.MaxValue;
            var highest = short.MinValue;
            for (var key = 0; key < 0x80; key++)
            {
                var value = tables.KfTvfEnv[(row * 0x80) + key];
                lowest = Math.Min(lowest, value);
                highest = Math.Max(highest, value);
            }

            Assert.True(highest - lowest > 1000,
                $"Key-follow row {row} spans only {highest - lowest}, which would be effectively flat.");
        }

        Assert.Equal(-7168, tables.KfTvfEnv[36]);
        Assert.Equal(-1024, tables.KfTvfEnv[60]);
        Assert.Equal(8192, tables.KfTvfEnv[96]);
    }

    private static List<double> ReadColumn(string path, string column)
    {
        var values = new List<double>();
        using var reader = new StreamReader(path);

        // Parse by header name: the harness gained four columns after these files were captured.
        var header = reader.ReadLine()!.Split(',');
        var index = Array.IndexOf(header, column);
        Assert.True(index >= 0, $"Column '{column}' not present in {Path.GetFileName(path)}.");

        while (reader.ReadLine() is { } line)
        {
            var fields = line.Split(',');
            if (fields.Length > index)
            {
                values.Add(double.Parse(fields[index], CultureInfo.InvariantCulture));
            }
        }

        return values;
    }
}
