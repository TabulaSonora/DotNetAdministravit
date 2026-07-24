using TabulaSonora.Dsp;

namespace TabulaSonora.Tests;

/// <summary>
/// The vector kernels against the scalar loops they replace.
/// </summary>
/// <remarks>
/// <para>
/// Exact equality, never a tolerance. The kernels are only admissible because they are
/// bit-identical — a rewrite that merely lands close would move every fixture in the suite by an
/// unpredictable amount, and the tolerance needed to absorb that would hide a real regression. So
/// these assertions are written as <see cref="Assert.Equal(object?, object?)"/> on the raw
/// <see cref="float"/> and would fail on a single differing ulp.
/// </para>
/// <para>
/// Every length from zero to three vector widths is covered, because the interesting failures are
/// at the seams: an off-by-one in the vector bound silently drops the last lane, and a tail loop
/// that starts at the wrong index double-counts it. Both produce output that still looks like
/// audio.
/// </para>
/// <para>
/// These need no ROM, so they run on a clone that skips the entire conformance suite.
/// </para>
/// </remarks>
public class SimdTests
{
    /// <summary>Lengths either side of every vector boundary, up to three widths.</summary>
    public static TheoryData<int> Lengths
    {
        get
        {
            var data = new TheoryData<int>();
            for (var length = 0; length <= (System.Numerics.Vector<float>.Count * 3) + 1; length++)
            {
                data.Add(length);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void AddMatchesTheScalarLoop(int length)
    {
        var (source, _) = Buffers(length);
        var (destination, expected) = Buffers(length, seed: 99);

        for (var i = 0; i < length; i++)
        {
            expected[i] += source[i];
        }

        Simd.Add(source, destination);
        Assert.Equal(expected, destination);
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void MixScaledMatchesTheScalarLoop(int length)
    {
        // An awkward gain rather than a round one: a power of two would scale exactly and could not
        // catch a kernel that dropped to float arithmetic.
        const double gain = 0.5906299212598425;

        var (source, _) = Buffers(length);
        var (destination, expected) = Buffers(length, seed: 99);

        for (var i = 0; i < length; i++)
        {
            expected[i] += (float)(source[i] * gain);
        }

        Simd.MixScaled(source, gain, destination);
        Assert.Equal(expected, destination);
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void StoreScaledMatchesTheScalarLoop(int length)
    {
        const double gain = -0.17320508075688773;

        var (source, _) = Buffers(length);
        var destination = new float[length];
        var expected = new float[length];

        for (var i = 0; i < length; i++)
        {
            expected[i] = (float)(source[i] * gain);
        }

        Simd.StoreScaled(source, gain, destination);
        Assert.Equal(expected, destination);
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void ScaleMatchesTheScalarLoop(int length)
    {
        const double gain = 0.99804;

        var (buffer, expected) = Buffers(length);

        for (var i = 0; i < length; i++)
        {
            expected[i] = (float)(expected[i] * gain);
        }

        Simd.Scale(buffer, gain);
        Assert.Equal(expected, buffer);
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void ScaleVaryingMatchesTheScalarLoop(int length)
    {
        var (buffer, expected) = Buffers(length);

        var gains = new double[length];
        for (var i = 0; i < length; i++)
        {
            // A gain that moves every sample, which is what a volume curve under a fader does.
            gains[i] = 0.3 + (i * 0.007);
        }

        for (var i = 0; i < length; i++)
        {
            expected[i] = (float)(expected[i] * gains[i]);
        }

        Simd.ScaleVarying(buffer, gains);
        Assert.Equal(expected, buffer);
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void PeakAbsMatchesTheScalarLoop(int length)
    {
        var (buffer, _) = Buffers(length);

        var expected = 0f;
        foreach (var sample in buffer)
        {
            expected = Math.Max(expected, Math.Abs(sample));
        }

        Assert.Equal(expected, Simd.PeakAbs(buffer));
    }

    [Fact]
    public void PeakAbsCarriesTheSeedThrough()
    {
        // The seed is how the two channels fold into one peak, so it has to survive a buffer that
        // is quieter than it -- including an empty one, where there is no vector body to run at all.
        Assert.Equal(5f, Simd.PeakAbs(new float[64], 5f));
        Assert.Equal(5f, Simd.PeakAbs([], 5f));
        Assert.Equal(7f, Simd.PeakAbs([-7f, 1f], 5f));
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void AnyNonZeroFindsASampleAtEveryPosition(int length)
    {
        Assert.False(Simd.AnyNonZero(new float[length]));
        Assert.False(Simd.AnyNonZero(new double[length]));

        for (var position = 0; position < length; position++)
        {
            var floats = new float[length];
            floats[position] = 1e-30f;
            Assert.True(Simd.AnyNonZero(floats), $"Missed a float at {position} of {length}.");

            var doubles = new double[length];
            doubles[position] = 1e-300;
            Assert.True(Simd.AnyNonZero(doubles), $"Missed a double at {position} of {length}.");
        }
    }

    [Fact]
    public void AnyNonZeroTreatsNegativeZeroAsZero()
    {
        // Matches the scalar `!= 0` it replaces: IEEE says -0.0 == 0.0, and a bus of negative zeros
        // carries no signal, so it must not light up HasSignal and run an effect over silence.
        var floats = new float[System.Numerics.Vector<float>.Count * 2];
        Array.Fill(floats, -0f);
        Assert.False(Simd.AnyNonZero(floats));

        var doubles = new double[System.Numerics.Vector<double>.Count * 2];
        Array.Fill(doubles, -0.0);
        Assert.False(Simd.AnyNonZero(doubles));
    }

    /// <summary>A deterministic buffer and an independent copy of it to mutate as the expectation.</summary>
    private static (float[] Buffer, float[] Copy) Buffers(int length, uint seed = 0x2545F491)
    {
        var buffer = new float[length];
        var state = seed;

        for (var i = 0; i < length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;

            // Deliberately not round numbers: values with a full mantissa are what make a dropped
            // rounding step visible.
            buffer[i] = ((state >> 8) / (float)0x800000) - 1f;
        }

        return (buffer, [.. buffer]);
    }
}
