using System.Buffers.Binary;
using TabulaSonora.Dsp;
using TabulaSonora.Rom;

namespace TabulaSonora.Tests;

/// <summary>
/// The codec is claimed bit-exact against the engine's own predictor, so these assert equality rather
/// than a tolerance.
/// </summary>
/// <remarks>
/// The ground truth is a single flute note whose delta and scale streams were dumped straight out of
/// the live engine's sampler state — <c>delta_stream.bin</c> (6,875 bytes, one signed byte per sample)
/// and <c>scale_stream.bin</c> (218 bytes). Decoding those offline with no engine involvement is what
/// proved the codec in the first place.
/// </remarks>
public class WaveCodecTests
{
    [Fact]
    public void ScaleNibbleIsLowForTheFirstSixteenSamplesAndHighForTheNext()
    {
        // One byte covers 32 samples: low nibble for 0-15, high nibble for 16-31.
        ReadOnlySpan<byte> scale = [0xA3, 0x5C];

        Assert.Equal(0x3, WaveCodec.ScaleAt(scale, 0));
        Assert.Equal(0x3, WaveCodec.ScaleAt(scale, 15));
        Assert.Equal(0xA, WaveCodec.ScaleAt(scale, 16));
        Assert.Equal(0xA, WaveCodec.ScaleAt(scale, 31));
        Assert.Equal(0xC, WaveCodec.ScaleAt(scale, 32));
        Assert.Equal(0x5, WaveCodec.ScaleAt(scale, 48));
    }

    [Fact]
    public void DeltaBytesAreSignedAndShiftedByTheExponentPlusTen()
    {
        Assert.Equal(1 << 10, WaveCodec.Step(1, 0));
        Assert.Equal(-1 << 10, WaveCodec.Step(0xFF, 0));       // 0xFF is -1, not 255
        Assert.Equal(-128 << 10, WaveCodec.Step(0x80, 0));     // 0x80 is -128
        Assert.Equal(127 << 15, WaveCodec.Step(0x7F, 5));
    }

    [SkippableFact]
    public void DecodesTheFluteGroundTruthStreamsBitExactly()
    {
        var delta = File.ReadAllBytes(TestData.RequireTrace("delta_stream.bin"));
        var scale = File.ReadAllBytes(TestData.RequireTrace("scale_stream.bin"));
        var expected = ReadPcm16Mono(TestData.RequireTrace("flute_sample_ourcodec.wav"));

        // 6,875 samples of delta; 218 bytes of scale is ceil(6875/32) = 215 plus read-ahead, which is
        // what pins the layout at one nibble per 16 samples.
        Assert.Equal(6875, delta.Length);
        Assert.Equal(218, scale.Length);
        Assert.Equal(6875, expected.Length);

        var decoded = WaveCodec.Decode(delta, scale, delta.Length);

        var actual = new short[decoded.Length];
        for (var i = 0; i < decoded.Length; i++)
        {
            actual[i] = (short)Math.Clamp(decoded[i] * 32768.0, -32768.0, 32767.0);
        }

        Assert.Equal(expected, actual);
        Assert.Equal(8230, actual.Max(Math.Abs));
    }

    [SkippableFact]
    public void FlutePredictorStaysWithinThirtyTwoBits()
    {
        // The predictor is a 32-bit accumulator in the engine and here. If a real wave ever needed
        // more range, integrating in a wider type would be a silent behavioural change.
        var delta = File.ReadAllBytes(TestData.RequireTrace("delta_stream.bin"));
        var scale = File.ReadAllBytes(TestData.RequireTrace("scale_stream.bin"));

        var predictors = new int[delta.Length];
        WaveCodec.DecodePredictors(delta, scale, predictors);

        Assert.Equal(-33_710_080, predictors.Min());
        Assert.Equal(15_802_368, predictors.Max());
    }

    [SkippableFact]
    public void DecodedFluteIsAPitchedWaveNotNoise()
    {
        // A wrong codec produces noise, and the cheapest discriminator is the zero-crossing rate:
        // the reference set measures 1.7-9.6% for real instruments against roughly 50% for noise.
        var delta = File.ReadAllBytes(TestData.RequireTrace("delta_stream.bin"));
        var scale = File.ReadAllBytes(TestData.RequireTrace("scale_stream.bin"));
        var decoded = WaveCodec.Decode(delta, scale, delta.Length);

        var crossings = 0;
        for (var i = 1; i < decoded.Length; i++)
        {
            if ((decoded[i - 1] < 0) != (decoded[i] < 0))
            {
                crossings++;
            }
        }

        var rate = (double)crossings / decoded.Length;
        Assert.True(rate < 0.15, $"Zero-crossing rate {rate:P2} looks like noise, not a pitched wave.");
    }

    [SkippableFact]
    public void DecodesAWaveStraightFromTheRom()
    {
        // Wave 806 is a flute sample whose descriptor fields are quoted in the notes: region 6,
        // loop 800928, end 803508, start 807803, root 75. Reading it end to end exercises the region
        // addressing and the delta/scale stream split together.
        using var rom = RomImage.Open(TestData.RequireSccore(), RomVerification.Quick);
        var waveRom = new WaveRom(rom);

        var streams = waveRom.ReadStreams(region: 6, loop: 800928, start: 807803);
        Assert.NotNull(streams);

        var value = streams.Value;
        Assert.Equal(800928 & ~0x1f, value.AlignedLoop);
        Assert.Equal(807803 - (800928 & ~0x1f), value.SampleCount);

        var decoded = WaveCodec.Decode(value.Delta, value.Scale, value.SampleCount);

        var peak = decoded.Max(Math.Abs);
        Assert.InRange(peak, 1e-3f, 1.0f);
    }

    [SkippableFact]
    public void DecodedWaveIncludesTheSampleAtTheDataEnd()
    {
        // The forward loop's period is dataEnd - loopStart + 1, so the sample AT the data end is part
        // of the loop and has to exist. Decoding one short makes the wrap substitute the loop's first
        // sample, which then sounds twice per pass -- an audible click on a short single-cycle loop,
        // and a measurable dulling of the timbre on any looped wave.
        using var rom = RomImage.Open(TestData.RequireSccore(), RomVerification.Quick);
        var tables = TableSet.FromRom(rom);
        var directory = new TabulaSonora.Patches.PatchDirectory(tables);
        var sampler = new Sampler(new WaveRom(rom), new Interpolator(tables));

        var descriptor = directory.GetWave(806)!.Value;
        var wave = sampler.Decode(descriptor)!;

        Assert.Equal(SamplerMode.Loop, wave.Mode);
        Assert.Equal(wave.DataEnd + 1, wave.Samples.Length);

        // That last sample is a real decoded value, not a copy of the loop's first.
        var expected = 0;
        for (var i = 0; i <= wave.DataEnd; i++)
        {
            expected += wave.Steps[i];
        }

        Assert.Equal((float)(expected * WaveCodec.OutputScale), wave.Samples[wave.DataEnd]);
        Assert.NotEqual(wave.Samples[wave.LoopStart], wave.Samples[wave.DataEnd]);
    }

    [SkippableFact]
    public void ReportsNoStreamsWhenTheDescriptorHasNoData()
    {
        using var rom = RomImage.Open(TestData.RequireSccore(), RomVerification.Quick);
        var waveRom = new WaveRom(rom);

        // The loop start is aligned down to a 32-sample boundary before the length is taken, so a
        // descriptor is only empty once `start` falls at or below that aligned position.
        Assert.Null(waveRom.ReadStreams(region: 0, loop: 992, start: 992));
        Assert.Null(waveRom.ReadStreams(region: 0, loop: 2000, start: 1000));
        Assert.Null(waveRom.ReadStreams(region: 0, loop: 0, start: 3_000_000));

        // ...and 1000 aligns down to 992, which still leaves eight decodable samples.
        Assert.NotNull(waveRom.ReadStreams(region: 0, loop: 1000, start: 1000));
    }

    private static short[] ReadPcm16Mono(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var offset = 12; // past "RIFF" size "WAVE"
        while (offset + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            if (id == "data")
            {
                var samples = new short[size / 2];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset + 8 + (i * 2), 2));
                }

                return samples;
            }

            offset += 8 + size + (size & 1);
        }

        throw new InvalidDataException($"No data chunk in '{path}'.");
    }
}
