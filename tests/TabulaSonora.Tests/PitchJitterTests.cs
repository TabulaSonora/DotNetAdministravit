using TabulaSonora.Dsp;

namespace TabulaSonora.Tests;

/// <summary>
/// The engine noise generator and the random start-pitch jitter — pure logic, no ROM needed.
/// </summary>
public class PitchJitterTests
{
    [Fact]
    public void NoiseSequenceMatchesTheAlgorithmFromReset()
    {
        // First eight draws of the two-register generator from the engine's reset seeds
        // (0xEFA6 / 0x9C23), computed independently from the documented algorithm.
        var noise = new EngineNoise();
        ushort[] expected = [0x4F95, 0x4B65, 0x7CEC, 0x8CCA, 0x231C, 0x5B7C, 0xB85B, 0xF7E6];
        foreach (var value in expected)
        {
            Assert.Equal(value, noise.Next());
        }

        noise.Reset();
        Assert.Equal(expected[0], noise.Next());
    }

    [Fact]
    public void StartJitterMatchesTheHardwareFormula()
    {
        // Bit 14 picks the sign; the positive branch slices 7 bits, the negative 8, so the range
        // is asymmetric: about [-10, +5] x depth.
        Assert.Equal(20, PitchChain.StartJitterMilliSemitones(5, 0x3FFF));
        Assert.Equal(-30, PitchChain.StartJitterMilliSemitones(5, 0x4000));
        Assert.Equal(-20, PitchChain.StartJitterMilliSemitones(5, 0x4F95));
        Assert.Equal(10, PitchChain.StartJitterMilliSemitones(10, 0x8CCA));
        Assert.Equal(0, PitchChain.StartJitterMilliSemitones(5, 0));

        // Whole-range sweep stays inside the derived bounds and both signs occur.
        var sawPositive = false;
        var sawNegative = false;
        for (var draw = 0; draw <= 0xFFFF; draw += 17)
        {
            var jitter = PitchChain.StartJitterMilliSemitones(10, (ushort)draw);
            Assert.InRange(jitter, -100, 50);
            sawPositive |= jitter > 0;
            sawNegative |= jitter < 0;
        }

        Assert.True(sawPositive);
        Assert.True(sawNegative);
    }

    [Fact]
    public void ConstantRunnerHoldsItsLevelThroughRelease()
    {
        var runner = PitchEnvelopeRunner.Constant(-20);
        Assert.Equal(-20, runner.Level);
        Assert.Equal(-20, runner.Tick(released: false));
        Assert.Equal(-20, runner.Tick(released: true));
        Assert.Equal(-20, runner.Tick(released: true));
    }

    [Fact]
    public void OneShotEnvelopeIgnoresNoteOff()
    {
        // A slow first segment: releasing a one-shot mid-segment must not divert it to the release
        // target, where the normal envelope does exactly that.
        var envelope = new PitchEnvelope(
            Start: 1000,
            Targets: [0, 0, 0, 0],
            Release: -5000,
            Times: [2000.0, 0, 0, 0],
            ReleaseMs: 0);

        var oneShot = new PitchEnvelopeRunner(envelope, ignoreNoteOff: true);
        var held = new PitchEnvelopeRunner(envelope);
        var normal = new PitchEnvelopeRunner(envelope);

        for (var tick = 0; tick < 20; tick++)
        {
            var releasedAt = tick >= 5;
            Assert.Equal(held.Tick(released: false), oneShot.Tick(releasedAt));
            normal.Tick(releasedAt);
        }

        // The control: the ordinary runner did get pulled toward the release target.
        Assert.True(normal.Level < oneShot.Level);
    }
}
