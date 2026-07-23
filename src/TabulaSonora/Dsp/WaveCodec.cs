using System.Runtime.CompilerServices;

namespace TabulaSonora.Dsp;

/// <summary>
/// The wave ROM's block-floating-point DPCM codec.
/// </summary>
/// <remarks>
/// <para>
/// Each sample contributes one signed delta byte; each 16-sample block contributes a 4-bit shift
/// exponent, packed two blocks to a byte. Decoding integrates the shifted deltas into a predictor and
/// scales the result by 2⁻²⁷:
/// </para>
/// <code>
/// predictor += (sbyte)delta[i] &lt;&lt; (scale(i) + 10);
/// output[i]  = predictor * 2^-27;
/// </code>
/// <para>
/// The predictor is a pure integrator with no leak, which is what lets looping, ping-pong and reverse
/// playback all happen in the delta domain: rewinding the delta index while keeping the predictor
/// makes a loop seamless by construction. It is also why each loop pass adds a constant DC step.
/// </para>
/// </remarks>
public static class WaveCodec
{
    /// <summary>
    /// The codec's output scale, exactly 2⁻²⁷, which maps the integrated predictor into roughly
    /// [-1, 1].
    /// </summary>
    public const double OutputScale = 7.450580596923828e-09;

    /// <summary>Number of samples covered by one shift-exponent nibble.</summary>
    public const int SamplesPerScaleNibble = 16;

    /// <summary>Number of samples covered by one shift-exponent byte.</summary>
    public const int SamplesPerScaleByte = 32;

    /// <summary>Fixed bias added to every shift exponent.</summary>
    public const int ShiftBias = 10;

    /// <summary>Reads the shift exponent that applies to one sample position.</summary>
    /// <param name="scale">The wave's scale stream.</param>
    /// <param name="position">Sample index.</param>
    /// <returns>The 4-bit exponent.</returns>
    /// <remarks>
    /// One byte covers 32 samples: the low nibble serves the first 16, the high nibble the second 16.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ScaleAt(ReadOnlySpan<byte> scale, int position)
    {
        var packed = scale[position >> 5];
        return ((position >> 4) & 1) == 0 ? packed & 0x0F : (packed >> 4) & 0x0F;
    }

    /// <summary>Computes the predictor increment for one sample.</summary>
    /// <param name="delta">The sample's signed delta byte.</param>
    /// <param name="scale">The sample's shift exponent.</param>
    /// <returns>The amount to add to the predictor.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Step(byte delta, int scale) => (sbyte)delta << (scale + ShiftBias);

    /// <summary>
    /// Integrates the delta stream into the raw predictor values, before output scaling.
    /// </summary>
    /// <param name="delta">Delta stream, at least as long as <paramref name="destination"/>.</param>
    /// <param name="scale">Scale stream covering the same range.</param>
    /// <param name="destination">Receives one predictor value per sample.</param>
    /// <remarks>
    /// The predictor is a 32-bit accumulator, matching the engine's own field, and wraps rather than
    /// saturating. Tests compare these values directly against the DLL's predictor, so the width
    /// matters.
    /// </remarks>
    public static void DecodePredictors(ReadOnlySpan<byte> delta, ReadOnlySpan<byte> scale, Span<int> destination)
    {
        var predictor = 0;
        for (var i = 0; i < destination.Length; i++)
        {
            predictor += Step(delta[i], ScaleAt(scale, i));
            destination[i] = predictor;
        }
    }

    /// <summary>Decodes the delta and scale streams into normalised samples.</summary>
    /// <param name="delta">Delta stream, at least as long as <paramref name="destination"/>.</param>
    /// <param name="scale">Scale stream covering the same range.</param>
    /// <param name="destination">Receives the decoded samples.</param>
    /// <remarks>
    /// The predictor is integrated in exact integer arithmetic and only then scaled, so no rounding
    /// accumulates across the wave. The result is stored as <see cref="float"/> to match the engine's
    /// stage-boundary precision.
    /// </remarks>
    public static void Decode(ReadOnlySpan<byte> delta, ReadOnlySpan<byte> scale, Span<float> destination)
    {
        var predictor = 0;
        for (var i = 0; i < destination.Length; i++)
        {
            predictor += Step(delta[i], ScaleAt(scale, i));
            destination[i] = (float)(predictor * OutputScale);
        }
    }

    /// <summary>Decodes the delta and scale streams into a newly allocated buffer.</summary>
    /// <param name="delta">Delta stream.</param>
    /// <param name="scale">Scale stream.</param>
    /// <param name="sampleCount">Number of samples to decode.</param>
    /// <returns>The decoded samples.</returns>
    public static float[] Decode(ReadOnlySpan<byte> delta, ReadOnlySpan<byte> scale, int sampleCount)
    {
        var result = new float[sampleCount];
        Decode(delta, scale, result);
        return result;
    }
}
