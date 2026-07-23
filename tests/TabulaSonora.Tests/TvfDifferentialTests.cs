using System.Text.Json;
using TabulaSonora.Dsp;
using TabulaSonora.Patches;
using TabulaSonora.Rom;

namespace TabulaSonora.Tests;

/// <summary>
/// The TVF cutoff chain, checked against the reference across its whole input domain.
/// </summary>
/// <remarks>
/// The frequency coefficient is the part most worth pinning: the ramp exponential's entries reach
/// 2¹⁸, so the interpolation overflows 32 bits at the top of the range, and an earlier attempt that
/// fed the filter linear cutoff units instead drove it unstable.
/// </remarks>
public class TvfDifferentialTests
{
    private static JsonElement Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", name);
        Skip.IfNot(File.Exists(path), $"Fixture '{name}' not found. Regenerate with tools/gen_fixtures.py.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.GetProperty("values").Clone();
    }

    private static (PatchDirectory Directory, TvfChain Tvf) LoadChain()
    {
        var tables = TableSet.FromCacheDirectory(TestData.RequireTables());
        return (new PatchDirectory(tables), new TvfChain(tables, new EnvelopeMachine(tables)));
    }

    [SkippableFact]
    public void FilterResponseMappingMatchesTheReference()
    {
        var (_, tvf) = LoadChain();
        var values = Load("tvf_tap.json");

        var type = 0;
        foreach (var element in values.EnumerateArray())
        {
            var expected = element.ValueKind == JsonValueKind.Null ? FilterTap.Bypass : element.GetString() switch
            {
                "lp" => FilterTap.LowPass,
                "bp" => FilterTap.BandPass,
                "hp" => FilterTap.HighPass,
                "notch" => FilterTap.Notch,
                var other => throw new InvalidOperationException($"Unknown tap '{other}'."),
            };

            Assert.True(expected == tvf.Tap(type), $"Tap({type}) = {tvf.Tap(type)}, expected {expected}");
            type++;
        }

        Assert.Equal(256, type);

        // The mapping is not the obvious one: it comes from bits 10-11 of the type's coefficient word.
        Assert.Equal(FilterTap.LowPass, tvf.Tap(0));
        Assert.Equal(FilterTap.HighPass, tvf.Tap(1));
        Assert.Equal(FilterTap.BandPass, tvf.Tap(2));
        Assert.Equal(FilterTap.Bypass, tvf.Tap(3));
        Assert.Equal(FilterTap.Notch, tvf.Tap(4));
        Assert.Equal(FilterTap.Bypass, tvf.Tap(7));
    }

    [SkippableFact]
    public void CutoffWarpAndCeilingMatchTheReference()
    {
        var (_, tvf) = LoadChain();
        foreach (var row in Load("tvf_cutoff_units.json").EnumerateArray())
        {
            var cutoff = row[0].GetInt32();
            var resonance = row[1].GetInt32();
            var expected = row[2].GetInt32();

            var actual = tvf.CutoffUnits(cutoff, resonance);
            Assert.True(expected == actual,
                $"CutoffUnits({cutoff}, {resonance}) = {actual}, expected {expected}");
        }

        // The neutral-resonance ceiling times four is the "fully open" constant an earlier
        // calibration measured by ear.
        Assert.Equal(245_760, tvf.CutoffUnits(0x7FFF, 0x40));
    }

    [SkippableFact]
    public void FrequencyCoefficientMatchesTheReference()
    {
        var (_, tvf) = LoadChain();
        var checkedRows = 0;

        foreach (var row in Load("tvf_f_coef.json").EnumerateArray())
        {
            var units = row[0].GetInt32();
            var expected = row[1].GetDouble();
            var actual = tvf.FrequencyCoefficient(units);

            Assert.True(Math.Abs(expected - actual) < 1e-12,
                $"FrequencyCoefficient({units}) = {actual}, expected {expected}");
            checkedRows++;
        }

        Assert.True(checkedRows > 2000);
    }

    [SkippableFact]
    public void DampingCoefficientMatchesTheReference()
    {
        var (_, tvf) = LoadChain();
        foreach (var row in Load("tvf_q.json").EnumerateArray())
        {
            var units = row[0].GetInt32();
            var resonance = row[1].GetInt32();
            var filterType = row[2].GetInt32();
            var expected = row[3].GetDouble();

            var actual = tvf.DampingCoefficient(units, resonance, filterType);
            Assert.True(Math.Abs(expected - actual) < 1e-12,
                $"DampingCoefficient({units}, {resonance}, {filterType}) = {actual}, expected {expected}");
        }

        // Reciprocal-Q: the neutral resonance byte is exactly 1.0, and smaller is more resonant.
        Assert.Equal(1.0, LoadChain().Tvf.DampingCoefficient(0, 0x40, filterType: 1), 12);
    }

    [SkippableFact]
    public void EnvelopeOffsetsMatchTheReferenceOnEveryRealPartial()
    {
        var (directory, tvf) = LoadChain();
        var checkedRows = 0;

        foreach (var row in Load("tvf_env_offsets.json").EnumerateArray())
        {
            var tone = row[0].GetInt32();
            var slot = row[1].GetInt32();
            var velocity = row[2].GetInt32();
            var key = row[3].GetInt32();
            var expectedPeak = row[4].GetInt32();
            var expectedRelease = row[6].GetInt32();

            var partial = directory.GetPartialBySlot(tone, slot);
            var (peak, segments, release) = tvf.EnvelopeOffsets(partial, key, velocity);

            Assert.True(expectedPeak == peak,
                $"peak(tone {tone} slot {slot}, vel {velocity}, key {key}) = {peak}, expected {expectedPeak}");

            var index = 0;
            foreach (var expectedSegment in row[5].EnumerateArray())
            {
                Assert.True(expectedSegment.GetInt32() == segments[index],
                    $"segment {index} (tone {tone} slot {slot}) = {segments[index]}, expected {expectedSegment.GetInt32()}");
                index++;
            }

            Assert.True(expectedRelease == release,
                $"release(tone {tone} slot {slot}) = {release}, expected {expectedRelease}");
            checkedRows++;
        }

        Assert.True(checkedRows > 10000, $"Only {checkedRows} envelope-offset rows checked.");
    }

    [SkippableFact]
    public void FilterStaysStableAcrossEveryRealPatchCutoff()
    {
        // The failure this guards against is concrete: feeding the filter linear cutoff units gives
        // f around 1.9, where the state matrix has a real eigenvalue of -2.2 and the filter diverges.
        // Through the exponential, real patches land between roughly 0.05 and 1.25.
        var (directory, tvf) = LoadChain();

        var lowest = double.MaxValue;
        var highest = 0.0;

        for (var tone = 0; tone < 512; tone++)
        {
            var record = directory.GetTone(tone);
            if (record is null || !record.IsDefined)
            {
                continue;
            }

            foreach (var partial in record.Partials)
            {
                var resonance = TvfChain.ResonanceByte(partial);
                var units = tvf.CutoffUnits(partial.CutoffBase * 0x100, resonance);
                var f = tvf.FrequencyCoefficient(units);
                lowest = Math.Min(lowest, f);
                highest = Math.Max(highest, f);
            }
        }

        Assert.True(highest < 1.5, $"Frequency coefficient reached {highest}, which is not stable.");
        Assert.True(lowest > 0.0, $"Frequency coefficient reached {lowest}.");
    }

    [SkippableFact]
    public void FilterIsSilentOnSilenceAndPassesThroughWhenBypassed()
    {
        var (directory, tvf) = LoadChain();
        var partial = directory.GetPartialBySlot(0, 0);

        var signal = new float[512];
        var cutoff = new double[512];
        Array.Fill(cutoff, 20000.0);

        tvf.Apply(signal, cutoff, filterType: 0, resonanceByte: 0x40);
        Assert.All(signal.ToArray(), s => Assert.Equal(0f, s));

        // Bypass leaves the signal untouched rather than zeroing it.
        for (var i = 0; i < signal.Length; i++)
        {
            signal[i] = 0.5f;
        }

        tvf.Apply(signal, cutoff, filterType: 3, resonanceByte: 0x40);
        Assert.All(signal.ToArray(), s => Assert.Equal(0.5f, s));

        Assert.True(partial.IsPresent);
    }
}
