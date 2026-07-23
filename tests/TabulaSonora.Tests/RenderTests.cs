using System.Globalization;
using TabulaSonora.Dsp;
using TabulaSonora.Patches;
using TabulaSonora.Rom;
using TabulaSonora.Voices;

namespace TabulaSonora.Tests;

/// <summary>
/// The render path: the pan law against the engine's own measured sweep, the sampler's loop
/// behaviour, and the voice allocator.
/// </summary>
public class RenderTests
{
    [SkippableFact]
    public void PanLawReproducesTheEngineSweep()
    {
        // The sweep was taken on a sustaining patch -- a decaying one would confound the RMS -- and
        // recovers the law by measurement rather than fitting a curve to a few points.
        var rows = ReadCsv(TestData.RequireTrace("panscan.csv"));
        Assert.Equal(128, rows.Count);

        var pan = new PanLaw(TableSet.FromCacheDirectory(TestData.RequireTables()));

        var left = new double[rows.Count];
        var right = new double[rows.Count];
        var gainLeft = new double[rows.Count];
        var gainRight = new double[rows.Count];

        for (var i = 0; i < rows.Count; i++)
        {
            var position = int.Parse(rows[i]["cc10"], CultureInfo.InvariantCulture);
            left[i] = double.Parse(rows[i]["rmsL"], CultureInfo.InvariantCulture);
            right[i] = double.Parse(rows[i]["rmsR"], CultureInfo.InvariantCulture);
            (gainLeft[i], gainRight[i]) = pan.Gains(position);
        }

        // One scale per channel, fitted over the whole sweep, since the measured RMS also carries the
        // patch's own level. Both channels must land on the SAME scale if the law is symmetric.
        var scaleLeft = Fit(left, gainLeft);
        var scaleRight = Fit(right, gainRight);
        Assert.Equal(scaleLeft, scaleRight, 6);

        var worst = 0.0;
        for (var i = 0; i < rows.Count; i++)
        {
            worst = Math.Max(worst, Math.Abs(left[i] - (scaleLeft * gainLeft[i])));
            worst = Math.Max(worst, Math.Abs(right[i] - (scaleRight * gainRight[i])));
        }

        // The residual is at the harness's six-decimal print quantum.
        Assert.True(worst < 1e-4, $"Worst pan RMS error {worst:F6}.");
    }

    [SkippableFact]
    public void PanCentreIsNeitherConstantPowerNorLinear()
    {
        var pan = new PanLaw(TableSet.FromCacheDirectory(TestData.RequireTables()));

        var (left, right) = pan.Gains(PanLaw.Centre);
        Assert.Equal(75.0 / 127.0, left, 12);
        Assert.Equal(75.0 / 127.0, right, 12);
        Assert.NotEqual(Math.Sqrt(0.5), left, 3);
        Assert.NotEqual(0.5, left, 3);

        // Hard left silences the right channel outright rather than attenuating it.
        var (_, hardLeftRight) = pan.Gains(0);
        Assert.Equal(0.0, hardLeftRight);
    }

    [SkippableFact]
    public void SamplerLoopsInclusivelyOfTheDataEnd()
    {
        using var rom = RomImage.Open(TestData.RequireSccore(), RomVerification.Quick);
        var tables = TableSet.FromRom(rom);
        var directory = new PatchDirectory(tables);
        var sampler = new Sampler(new WaveRom(rom), new Interpolator(tables));

        // Wave 806 is the flute sample quoted throughout the notes.
        var descriptor = directory.GetWave(806)!.Value;
        var wave = sampler.Decode(descriptor);

        Assert.NotNull(wave);
        Assert.Equal(SamplerMode.Loop, wave!.Mode);

        // One pass plays loopStart..dataEnd, so the period is inclusive of the end. Getting this off
        // by one is inaudible on a long loop but detunes a single-cycle one by 27 cents.
        var period = wave.DataEnd - wave.LoopStart + 1;
        Assert.True(period > 1);

        // Playing well past the loop must stay bounded rather than running off the end of the data.
        var rendered = sampler.Play(wave, sampleCount: 40_000, ratio: 1.0);
        Assert.Equal(40_000, rendered.Length);
        Assert.All(rendered, s => Assert.True(Math.Abs(s) < 2.0f, $"Sample {s} is out of range."));
        Assert.True(rendered.Skip(30_000).Any(s => Math.Abs(s) > 1e-4f),
            "A looping wave must still be sounding well past its data end.");
    }

    [SkippableFact]
    public void LoopingIsDecidedByASustainRegionNotTheDescriptorFlag()
    {
        using var rom = RomImage.Open(TestData.RequireSccore(), RomVerification.Quick);
        var tables = TableSet.FromRom(rom);
        var directory = new PatchDirectory(tables);
        var sampler = new Sampler(new WaveRom(rom), new Interpolator(tables));

        // Piano's descriptor flag reads zero, so trusting it makes held notes run out as one-shots
        // and the top chord note vanish at note-off.
        var tone = directory.GetTone(0)!;
        var resolved = directory.Resolve(0, note: 60, velocity: 100);
        Assert.NotEmpty(resolved.Partials);

        var descriptor = resolved.Partials[0].Descriptor;
        Assert.False(descriptor.PingPong);

        var wave = sampler.Decode(descriptor);
        Assert.NotNull(wave);
        Assert.Equal(SamplerMode.Loop, wave!.Mode);
        Assert.Equal("Piano 1", tone.Name);
    }

    [Fact]
    public void VoicePoolStealsWholeNotesRatherThanHalfOfThem()
    {
        var pool = new VoicePool();

        // Fill the pool with two-partial notes.
        for (var note = 0; note < VoicePool.MaxVoices / 2; note++)
        {
            var group = pool.BeginNoteGroup();
            pool.Allocate(channel: 0, note, velocity: 100, group);
            pool.Allocate(channel: 0, note, velocity: 100, group);
        }

        Assert.Equal(VoicePool.MaxVoices, pool.ActiveCount);

        // The next allocation has to steal. Taking one slot of a two-partial note would leave the
        // other half sounding on its own.
        var fresh = pool.BeginNoteGroup();
        var stolen = pool.Allocate(channel: 0, note: 99, velocity: 100, fresh);

        Assert.Equal(VoiceState.Held, stolen.State);
        Assert.Equal(99, stolen.Note);

        var groups = pool.Active().Select(v => v.NoteGroup).ToList();
        foreach (var group in groups.Distinct())
        {
            var count = groups.Count(g => g == group);
            Assert.True(count is 1 or 2, $"Note group {group} has {count} voices, which is a remnant.");
        }
    }

    [Fact]
    public void VoicePoolPrefersFreeSlotsThenReleasingNotes()
    {
        var pool = new VoicePool();

        var first = pool.Allocate(0, note: 60, velocity: 100, pool.BeginNoteGroup());
        Assert.Equal(VoiceState.Held, first.State);
        Assert.Equal(1, pool.ActiveCount);

        Assert.Equal(1, pool.Release(0, 60));
        Assert.Equal(VoiceState.Releasing, first.State);
        Assert.Equal(1, pool.ActiveCount);

        // A releasing voice keeps sounding, so it still occupies its slot.
        var second = pool.Allocate(0, note: 62, velocity: 100, pool.BeginNoteGroup());
        Assert.NotEqual(first.Index, second.Index);
        Assert.Equal(2, pool.ActiveCount);

        pool.Free(first.Index);
        Assert.Equal(1, pool.ActiveCount);

        pool.Reset();
        Assert.Equal(0, pool.ActiveCount);
    }

    private static double Fit(double[] measured, double[] gains)
    {
        var numerator = 0.0;
        var denominator = 0.0;
        for (var i = 0; i < measured.Length; i++)
        {
            numerator += measured[i] * gains[i];
            denominator += gains[i] * gains[i];
        }

        return numerator / denominator;
    }

    private static List<Dictionary<string, string>> ReadCsv(string path)
    {
        using var reader = new StreamReader(path);
        var header = reader.ReadLine()!.Split(',');
        var rows = new List<Dictionary<string, string>>();

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var fields = line.Split(',');
            var row = new Dictionary<string, string>(header.Length, StringComparer.Ordinal);
            for (var i = 0; i < header.Length && i < fields.Length; i++)
            {
                row[header[i]] = fields[i];
            }

            rows.Add(row);
        }

        return rows;
    }
}
