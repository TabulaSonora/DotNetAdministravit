using System.Globalization;
using TabulaSonora.Dsp;
using TabulaSonora.Patches;
using TabulaSonora.Rom;

namespace TabulaSonora.Tests;

/// <summary>
/// Conformance against the real engine's own per-voice gain word, captured per control tick by the
/// <c>drumnote</c> harness.
/// </summary>
/// <remarks>
/// This is the tier that pins the level chain to the DLL rather than to the reference. The gain word
/// lives at <c>DAT_181a1d830 + (v &amp; 3) * 0x40 + (v &gt;&gt; 2) * 4</c>, is written every control tick,
/// and is handed straight to the sampler — so reproducing it from static tables alone, with no fitted
/// constant, is the strongest available statement that the level chain is right.
/// </remarks>
public class TvaTraceTests
{
    /// <summary>
    /// The velocity the drum traces were captured at.
    /// </summary>
    /// <remarks>
    /// Not recorded in the file name, which carries only the note. Recovered by sweeping velocity
    /// against the traces' first-tick amplitudes: 110 reproduces all five notes, and no other
    /// velocity comes close.
    /// </remarks>
    private const int TraceVelocity = 110;

    /// <summary>The harness prints the gain to six decimals, which bounds how exactly it can be matched.</summary>
    private const double PrintQuantum = 1e-4;

    [SkippableTheory]
    [InlineData(36, 1774)]   // kick
    [InlineData(38, 1776)]   // snare
    [InlineData(41, 1779)]   // low tom
    [InlineData(42, 1780)]   // closed hat
    [InlineData(49, 1785)]   // crash
    public void PerVoiceGainIsReproducedFromStaticTablesAlone(int note, int expectedTone)
    {
        var tracePath = TestData.RequireTrace($"dn_{note}.csv");
        using var rom = RomImage.Open(TestData.RequireSccore(), RomVerification.Quick);

        var tables = TableSet.FromRom(rom);
        var directory = new PatchDirectory(tables);
        var tva = new TvaChain(tables, new EnvelopeMachine(tables));
        var kits = new DrumKitTable(rom);

        var key = kits.Key(note);
        Assert.Equal(expectedTone, key.Tone);

        // Drums resolve at the neutral key; the note selects the kit entry, not the pitch.
        var resolved = directory.Resolve(key.Tone, note: 60, TraceVelocity);
        var tone = directory.GetTone(key.Tone)!;

        var ours = new List<double>();
        foreach (var partial in resolved.Partials)
        {
            var block = tone.Partials[partial.PartialIndex];
            var partialLevel = tva.PartialLevel(block, TraceVelocity);
            if (partialLevel is null)
            {
                continue;
            }

            var zoneLevel = directory.ZoneLevel(block.Multisample, 60, block.KeyCenter);
            var baseLevel = tva.BaseLevel(block, partialLevel.Value, key: 60, zoneLevel, directory.ToneLevel(key.Tone));
            ours.Add(TvaChain.AmpScale * tva.AmpOf(baseLevel));
        }

        var expected = FirstTickAmplitudes(tracePath);

        Assert.Equal(expected.Count, ours.Count);

        // Voice order is not guaranteed to follow partial order -- the kick's two voices are
        // reversed relative to its partials while the snare's are not -- so compare as sorted sets.
        ours.Sort();
        expected.Sort();

        for (var i = 0; i < expected.Count; i++)
        {
            Assert.True(Math.Abs(expected[i] - ours[i]) < PrintQuantum,
                $"note {note} voice {i}: engine {expected[i]:F6}, ours {ours[i]:F6}");
        }
    }

    [SkippableFact]
    public void KitLevelAndPanArePerKeyNotPerKit()
    {
        using var rom = RomImage.Open(TestData.RequireSccore(), RomVerification.Quick);
        var kits = new DrumKitTable(rom);

        // A single default per parameter would report a panned kit as dead centre.
        Assert.Equal(127, kits.Key(36).Level);
        Assert.Equal(99, kits.Key(38).Level);
        Assert.Equal(116, kits.Key(41).Level);
        Assert.Equal(64, kits.Key(36).Pan);
        Assert.Equal(34, kits.Key(41).Pan);
        Assert.Equal(84, kits.Key(42).Pan);
    }

    [SkippableFact]
    public void CoarsePitchAppliesAtHalfStrength()
    {
        // +6 units is 1.189x, not the 1.414x a semitone-per-unit reading would give.
        Assert.Equal(1.0, DrumKitTable.CoarsePitchRatio(60), 12);
        Assert.Equal(Math.Pow(2, 6 / 24.0), DrumKitTable.CoarsePitchRatio(66), 12);
        Assert.Equal(1.189, DrumKitTable.CoarsePitchRatio(66), 3);
    }

    [SkippableFact]
    public void DrumProgramsResolveThroughTheirOwnLookupNotTheMelodicOne()
    {
        using var rom = RomImage.Open(TestData.RequireSccore(), RomVerification.Quick);
        var kits = new DrumKitTable(rom);

        // Standard GM/GS drum parts use internal bank 0x04, which selects map row 0.
        Assert.Equal(0, kits.MapRow(0x04));
        Assert.Equal(0, kits.KitForProgram(0));
        Assert.True(kits.KitCount > 0);
    }

    private static List<double> FirstTickAmplitudes(string path)
    {
        var amplitudes = new List<double>();
        using var reader = new StreamReader(path);
        var header = reader.ReadLine()!.Split(',');
        var timeColumn = Array.IndexOf(header, "t_ms");
        var ampColumn = Array.IndexOf(header, "amp");

        while (reader.ReadLine() is { } line)
        {
            var fields = line.Split(',');
            if (fields.Length <= ampColumn || fields[timeColumn] != "0")
            {
                continue;
            }

            amplitudes.Add(double.Parse(fields[ampColumn], CultureInfo.InvariantCulture));
        }

        return amplitudes;
    }
}
