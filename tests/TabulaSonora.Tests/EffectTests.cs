using System.Buffers.Binary;
using TabulaSonora.Effects;

namespace TabulaSonora.Tests;

/// <summary>
/// The three GS send effects, checked by impulse response against the reference.
/// </summary>
/// <remarks>
/// Each effect is a recursive network, so an impulse response pins the whole thing at once — tap
/// positions, gains, feedback and delay-line geometry all show up in it. A single wrong tap offset or
/// a sign error in the feedback path diverges within a few hundred samples.
/// </remarks>
public class EffectTests
{
    private const double Tolerance = 1e-6;

    [Fact]
    public void PresetsLoadForEveryType()
    {
        var presets = EffectPresets.Default;

        Assert.Equal(8, presets.Reverb.Types.Length);
        Assert.Equal(8, presets.Chorus.Types.Length);
        Assert.Equal(10, presets.Delay.RawPresets.Length);

        Assert.Equal("Room1", presets.Reverb.TypeNames[0]);
        Assert.Equal("Hall2", presets.Reverb.TypeNames[4]);
        Assert.Equal("Flanger", presets.Chorus.TypeNames[5]);
        Assert.Equal("PanRepeat", presets.Delay.TypeNames[9]);

        // The fixed 60 ms input pre-delay is not in the preset table -- it is a measured constant,
        // so it lives in the library rather than in the generated data.
        Assert.Equal(1920, DelayPresets.PreDelaySamples);
        Assert.Equal(115, presets.Delay.TimeMilliseconds.Length);
        Assert.Equal(120, presets.Delay.RatioPercent.Length);
    }

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void ReverbImpulseResponseMatchesTheReference(int type)
    {
        var expected = LoadFixture($"reverb_type_{type}.f32");
        var samples = expected.Length / 2;

        var input = new float[samples];
        input[0] = 1f;

        var left = new float[samples];
        var right = new float[samples];
        Reverb.ForType(type).Process(input, left, right);

        AssertMatches(expected, left, right, $"reverb type {type}");
    }

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void ChorusImpulseResponseMatchesTheReference(int type)
    {
        var expected = LoadFixture($"chorus_type_{type}.f32");
        var samples = expected.Length / 2;

        var input = new float[samples];
        input[0] = 1f;

        var left = new float[samples];
        var right = new float[samples];
        Chorus.ForType(type).Process(input, left, right);

        AssertMatches(expected, left, right, $"chorus type {type}");
    }

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void DelayImpulseResponseMatchesTheReference(int type)
    {
        var expected = LoadFixture($"delay_type_{type}.f32");
        var samples = expected.Length / 2;

        var input = new float[samples];
        input[0] = 1f;

        var left = new float[samples];
        var right = new float[samples];
        SystemDelay.ForType(type).Process(input, left, right);

        AssertMatches(expected, left, right, $"delay type {type}");
    }

    [Fact]
    public void DelayIsSilentUntilThePreDelayElapses()
    {
        // The first repeat lands at 60 ms plus the table time, uniformly across every type. Without
        // the pre-delay the envelope correlation against the engine sits around 0.6 rather than 0.99.
        var input = new float[3000];
        input[0] = 1f;
        var left = new float[3000];
        var right = new float[3000];

        SystemDelay.ForType(0).Process(input, left, right);

        for (var i = 0; i < DelayPresets.PreDelaySamples; i++)
        {
            Assert.Equal(0f, left[i]);
            Assert.Equal(0f, right[i]);
        }
    }

    [Fact]
    public void SendGainsAreLinearInTheirController()
    {
        // Both sends are linear in the controller, unlike the part volume law, which is squared.
        // Linear means the ratio tracks 64/127, not one half.
        Assert.Equal(0.0, Reverb.SendGain(0));
        Assert.Equal(Reverb.SendGain(127) * 64 / 127, Reverb.SendGain(64), 12);
        Assert.Equal(0.0, Chorus.SendGain(0));
        Assert.Equal(Chorus.SendGain(127) * 64 / 127, Chorus.SendGain(64), 12);

        // The delay send has no Control Change at all; it is reachable only over SysEx.
        Assert.Equal(0.0, SystemDelay.SendGain(0));
        Assert.True(SystemDelay.SendGain(127) > 0);
    }

    [Fact]
    public void DelayParameterCompilationMatchesTheDocumentedLaws()
    {
        var raw = EffectPresets.Default.Delay.RawPresets[0];
        var compiled = SystemDelay.Compile(raw);

        // Feedback raw 0-127 maps to -1..+1, displayed as -64..+63.
        Assert.Equal((raw[8] - 64) / 64.0, compiled.Feedback, 12);

        // Pan types put nothing in the centre and split the taps instead.
        var pan = SystemDelay.Compile(EffectPresets.Default.Delay.RawPresets[4]);
        Assert.Equal(0.0, pan.CentreGain);
        Assert.True(pan.LeftGain > 0 && pan.RightGain > 0);
        Assert.NotEqual(pan.LeftSamples, pan.RightSamples);
    }

    [Fact]
    public void DcBlockerRemovesAConstantOffset()
    {
        // A 20 Hz one-pole. The codec's predictor is a pure integrator, so it drifts on every loop
        // pass and the effect inputs would otherwise accumulate the offset.
        var blocker = default(DcBlocker);
        var last = 0.0;
        for (var i = 0; i < 32000; i++)
        {
            last = blocker.Process(1.0);
        }

        Assert.True(Math.Abs(last) < 0.05, $"DC offset survived at {last}.");
    }

    private static void AssertMatches(float[] expected, float[] left, float[] right, string what)
    {
        var worst = 0.0;
        var worstIndex = -1;

        for (var i = 0; i < left.Length; i++)
        {
            var errorLeft = Math.Abs(expected[i * 2] - left[i]);
            var errorRight = Math.Abs(expected[(i * 2) + 1] - right[i]);
            var error = Math.Max(errorLeft, errorRight);

            if (error > worst)
            {
                worst = error;
                worstIndex = i;
            }
        }

        Assert.True(worst < Tolerance,
            $"{what}: worst sample error {worst:E3} at index {worstIndex}.");

        // Guard against a silent network passing trivially.
        var energy = left.Sum(s => (double)s * s) + right.Sum(s => (double)s * s);
        Assert.True(energy > 1e-12, $"{what}: produced no output at all.");
    }

    private static float[] LoadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", name);
        Skip.IfNot(File.Exists(path),
            $"Fixture '{name}' not found. Regenerate with tools/gen_effect_fixtures.py.");

        var bytes = File.ReadAllBytes(path);
        var values = new float[bytes.Length / 4];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4, 4));
        }

        return values;
    }
}
