using System.Globalization;
using TabulaSonora.Rom;

namespace TabulaSonora.Tests;

/// <summary>
/// Proves the loader reproduces the extracted table cache byte for byte, and spot-checks the handful
/// of table values the reverse-engineering notes state outright. Everything downstream is built on
/// these bytes, so a silent mismatch here would surface as a mysterious DSP bug much later.
/// </summary>
public class TableSetTests
{
    [SkippableFact]
    public void EveryTableSlicedFromTheDllMatchesTheExtractedCache()
    {
        var dll = TestData.RequireSccore();
        var tablesDirectory = TestData.RequireTables();

        using var rom = RomImage.Open(dll);
        var manifest = rom.Manifest;

        var compared = 0;
        var failures = new List<string>();

        foreach (var entry in manifest.CachedTables)
        {
            var cachePath = Path.Combine(tablesDirectory, entry.Name);
            if (!File.Exists(cachePath))
            {
                failures.Add($"{entry.Name}: cache file missing");
                continue;
            }

            var cached = File.ReadAllBytes(cachePath);
            var fromDll = rom.Read(entry);

            if (cached.Length != entry.Size)
            {
                failures.Add($"{entry.Name}: cache is {cached.Length} bytes, manifest says {entry.Size}");
                continue;
            }

            // Most entries match in full. `kf_tvfenv` was captured from the loaded image with an
            // over-read, so only its recorded prefix is expected to agree with the file.
            var comparable = ComparableLength(entry);
            if (!cached.AsSpan(0, comparable).SequenceEqual(fromDll.AsSpan(0, comparable)))
            {
                var at = FirstDifference(cached, fromDll, comparable);
                failures.Add(
                    $"{entry.Name} ({entry.Symbol}): differs at byte {at} of {comparable} " +
                    $"(file offset 0x{entry.FileOffset + at:x})");
                continue;
            }

            compared++;
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        Assert.Equal(48, compared);
    }

    [SkippableFact]
    public void LoadingFromTheDllAndFromTheCacheAgree()
    {
        using var rom = RomImage.Open(TestData.RequireSccore(), RomVerification.Quick);
        var fromRom = TableSet.FromRom(rom);
        var fromCache = TableSet.FromCacheDirectory(TestData.RequireTables());

        // kf_tvfenv is the one over-read cache; compare only the rows the engine actually reads.
        Assert.Equal(fromRom.KfTvfEnv.AsSpan(0, 16 * 128).ToArray(),
                     fromCache.KfTvfEnv.AsSpan(0, 16 * 128).ToArray());

        Assert.Equal(fromRom.AmpCurveHi, fromCache.AmpCurveHi);
        Assert.Equal(fromRom.InterpCoef, fromCache.InterpCoef);
        Assert.Equal(fromRom.Tone, fromCache.Tone);
        Assert.Equal(fromRom.Wavedesc, fromCache.Wavedesc);
        Assert.Equal(fromRom.Multisample, fromCache.Multisample);
    }

    [SkippableFact]
    public void TablesHaveTheExpectedElementCounts()
    {
        var tables = TableSet.FromCacheDirectory(TestData.RequireTables());

        // Derived from byte length, deliberately not from the manifest's `shape` string: g_level_curve
        // is 256 bytes holding 128 ushorts, yet its shape reads "256".
        Assert.Equal(128, tables.LevelCurve.Length);
        Assert.Equal(256, tables.AmpCurveHi.Length);
        Assert.Equal(256, tables.AmpCurveLo.Length);
        Assert.Equal(512, tables.EnvRateOut.Length);
        Assert.Equal(128, tables.RateCurve.Length);
        Assert.Equal(256, tables.EnvScaleCurve.Length);
        Assert.Equal(64, tables.ResoCurve.Length);
        Assert.Equal(128, tables.Pan.Length);
        Assert.Equal(257, tables.RampExp.Length);
        Assert.Equal(129, tables.TvfCutoffWarp.Length);
        Assert.Equal(128, tables.TvfCutoffCeil.Length);
        Assert.Equal(512, tables.InterpCoef.Length);       // 128 phases x 4 taps
        Assert.Equal(8 * 128, tables.KfPitch.Length);
        Assert.Equal(128 * 128, tables.KfTvaLevel.Length);
        Assert.Equal(24 * 0x81, tables.LfoWaveBank.Length);
        Assert.Equal(32768, tables.DirLut3.Length);
        Assert.Equal(16, tables.FilterTypeCoef.Length);
        Assert.Equal(2048, tables.VelXfade.Length);        // 16 rows x 0x80 BYTES, not ushorts
    }

    [SkippableFact]
    public void EnvelopeRateOutputCurveIsExactlyPowersOfTwoIn8Point8()
    {
        var tables = TableSet.FromCacheDirectory(TestData.RequireTables());

        // g_env_rate_out is 2^((i - 0x80) / 32) in 8.8 fixed point: 0x100 is 1.0 at the neutral index.
        Assert.Equal(0x100, tables.EnvRateOut[0x80]);
        Assert.Equal(0x040, tables.EnvRateOut[0x40]);
        Assert.Equal(0x200, tables.EnvRateOut[0xA0]);
        Assert.Equal(0x400, tables.EnvRateOut[0xC0]);
    }

    [SkippableFact]
    public void AntiZipperDividersAreTheDocumentedMasks()
    {
        var tables = TableSet.FromCacheDirectory(TestData.RequireTables());

        // One render block is 32 samples at 32 kHz, so these select updates every 1, 8, 32 or 128 ms.
        Assert.Equal<byte[]>([0x00, 0x07, 0x1F, 0x7F], tables.RampDivider);
    }

    [SkippableFact]
    public void RampExponentTableSpansExactlyOneOctave()
    {
        var tables = TableSet.FromCacheDirectory(TestData.RequireTables());

        // 2^17 * 2^(i/256): the last entry is exactly twice the first, which is what makes the
        // SVF f-coefficient decode f = 2^(C/16384 - 15) come out with no fitted constant.
        Assert.Equal(1 << 17, tables.RampExp[0]);
        Assert.Equal(2 * (long)tables.RampExp[0], tables.RampExp[256]);
    }

    [SkippableFact]
    public void EveryResamplerPhaseSumsToUnity()
    {
        var tables = TableSet.FromCacheDirectory(TestData.RequireTables());

        // The coefficients are stored as float32, so a phase sums to 1.0 only to within single
        // precision -- the worst row is off by about 1e-5. Accumulate in double so the tolerance
        // measures the table rather than the test's own rounding.
        var worst = 0.0;
        var worstPhase = -1;
        for (var phase = 0; phase < 128; phase++)
        {
            double sum = tables.InterpCoef[(phase * 4) + 0];
            sum += tables.InterpCoef[(phase * 4) + 1];
            sum += tables.InterpCoef[(phase * 4) + 2];
            sum += tables.InterpCoef[(phase * 4) + 3];

            var deviation = Math.Abs(sum - 1.0);
            if (deviation > worst)
            {
                worst = deviation;
                worstPhase = phase;
            }
        }

        Assert.True(worst < 5e-5,
            $"Resampler phase {worstPhase} deviates from unity by " +
            $"{worst.ToString("R", CultureInfo.InvariantCulture)}.");

        // Even at zero fractional phase the kernel is a mild lowpass, not a passthrough. This is the
        // single most timbre-defining table in the engine; linear interpolation ([0,1,0,0] here) is
        // measurably brighter.
        Assert.Equal(0.174f, tables.InterpCoef[0], 3);
        Assert.Equal(0.653f, tables.InterpCoef[1], 3);
        Assert.Equal(0.173f, tables.InterpCoef[2], 3);

        // The fourth tap is 1e-5, not zero -- the same anti-denormal seed the effects matrix uses.
        // It is also the whole of phase 0's 1e-5 deviation from unity.
        Assert.Equal(1e-5f, tables.InterpCoef[3], 7);
    }

    [SkippableFact]
    public void PanTableCentreIsSeventyFiveOverOneTwentySeven()
    {
        var tables = TableSet.FromCacheDirectory(TestData.RequireTables());

        // Left is T[127 - p], right is T[p - 1]; at p = 64 both land on index 63. The result is
        // 0.5906 -- neither constant-power (0.707) nor linear (0.5).
        Assert.Equal(75, tables.Pan[63]);
    }

    [SkippableFact]
    public void AmplitudeCurveFloorIsFourNotZero()
    {
        var tables = TableSet.FromCacheDirectory(TestData.RequireTables());

        // g_amp_curve_hi[0] == 4, so amp_of(0) is 4.6e-05 rather than silence. Any level chain that
        // clamps a negative level to zero will sit at this floor forever instead of going quiet.
        Assert.Equal(4, tables.AmpCurveHi[0]);
    }

    private static int ComparableLength(TableEntry entry)
    {
        // "full" or "prefix N/M".
        const string prefix = "prefix ";
        if (!entry.Match.StartsWith(prefix, StringComparison.Ordinal))
        {
            return entry.Size;
        }

        var span = entry.Match.AsSpan(prefix.Length);
        var slash = span.IndexOf('/');
        return slash > 0 && int.TryParse(span[..slash], out var matched)
            ? Math.Min(matched, entry.Size)
            : entry.Size;
    }

    private static int FirstDifference(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int length)
    {
        for (var i = 0; i < length; i++)
        {
            if (a[i] != b[i])
            {
                return i;
            }
        }

        return -1;
    }
}
