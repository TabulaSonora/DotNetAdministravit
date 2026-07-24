using System.Numerics;
using System.Runtime.InteropServices;

namespace TabulaSonora.Dsp;

/// <summary>
/// The elementwise buffer kernels the mix paths repeat, written once and vectorised.
/// </summary>
/// <remarks>
/// <para>
/// Every kernel here is <em>bit-identical</em> to the scalar loop it replaces, which is the whole
/// reason it is allowed to exist: this engine's constants were recovered by measurement, so a
/// rewrite that merely lands close is a regression that the fixtures would have to be loosened to
/// accept. Two properties give that guarantee.
/// </para>
/// <para>
/// First, none of these is a horizontal reduction over floats. Each output element is a function of
/// the input element at the same index, so no addition is ever reassociated — and floating-point
/// addition is not associative, which is exactly what disqualifies the tempting rewrite of the 4-tap
/// resampler in <see cref="Interpolator"/>.
/// </para>
/// <para>
/// Second, the widening and narrowing are the same conversions the scalar code performs.
/// <see cref="Vector.Widen(Vector{float}, out Vector{double}, out Vector{double})"/> is exact —
/// every float is a double — and <see cref="Vector.Narrow(Vector{double}, Vector{double})"/> rounds
/// to nearest-even, which is what a <c>(float)</c> cast does. So <c>dst[i] += (float)(src[i] *
/// gain)</c> performs the identical sequence of IEEE operations in either form. The kernels that
/// take a <see cref="double"/> gain therefore keep the arithmetic in double rather than dropping to
/// float, which would be faster and wrong.
/// </para>
/// <para>
/// <see cref="PeakAbs"/> is the one reduction, and it is admissible because maximum is exact under
/// any association — there is no rounding to reorder. It operates on absolute values, which also
/// removes the signed-zero ordering question. A NaN in an audio buffer is out of contract here; it
/// is already a bug upstream, and the lane-wise and scalar maxima disagree about it.
/// </para>
/// <para>
/// <see cref="Vector{T}"/> is 256 bits wide where AVX2 is available and <em>128 bits on
/// WebAssembly</em>, so the float kernels run 8 or 4 lanes at a time and the double-widening ones 4
/// or 2. The browser build already enables <c>WasmEnableSIMD</c>. The scalar loop at the foot of
/// each kernel is not only the tail handler — it is the reference the vector body must match, and
/// the tests assert exactly that at every length either side of a vector boundary.
/// </para>
/// </remarks>
internal static class Simd
{
    /// <summary>Accumulates one buffer into another: <c>destination[i] += source[i]</c>.</summary>
    /// <param name="source">Samples to add.</param>
    /// <param name="destination">Buffer accumulated into; must be at least as long as the source.</param>
    public static void Add(ReadOnlySpan<float> source, Span<float> destination)
    {
        var width = Vector<float>.Count;
        var i = 0;

        if (Vector.IsHardwareAccelerated && source.Length >= width)
        {
            ref var src = ref MemoryMarshal.GetReference(source);
            ref var dst = ref MemoryMarshal.GetReference(destination);

            for (; i <= source.Length - width; i += width)
            {
                var sum = Vector.LoadUnsafe(ref dst, (nuint)i) + Vector.LoadUnsafe(ref src, (nuint)i);
                sum.StoreUnsafe(ref dst, (nuint)i);
            }
        }

        for (; i < source.Length; i++)
        {
            destination[i] += source[i];
        }
    }

    /// <summary>Accumulates a scaled buffer: <c>destination[i] += (float)(source[i] * gain)</c>.</summary>
    /// <param name="source">Samples to scale and add.</param>
    /// <param name="gain">Scalar gain, applied in double as the scalar path does.</param>
    /// <param name="destination">Buffer accumulated into; must be at least as long as the source.</param>
    public static void MixScaled(ReadOnlySpan<float> source, double gain, Span<float> destination)
    {
        var width = Vector<float>.Count;
        var i = 0;

        if (Vector.IsHardwareAccelerated && source.Length >= width)
        {
            ref var src = ref MemoryMarshal.GetReference(source);
            ref var dst = ref MemoryMarshal.GetReference(destination);
            var scale = new Vector<double>(gain);

            for (; i <= source.Length - width; i += width)
            {
                Vector.Widen(Vector.LoadUnsafe(ref src, (nuint)i), out var low, out var high);
                var scaled = Vector.Narrow(low * scale, high * scale);
                (Vector.LoadUnsafe(ref dst, (nuint)i) + scaled).StoreUnsafe(ref dst, (nuint)i);
            }
        }

        for (; i < source.Length; i++)
        {
            destination[i] += (float)(source[i] * gain);
        }
    }

    /// <summary>
    /// Accumulates a twice-scaled buffer: <c>destination[i] += (float)((source[i] * first) * second)</c>.
    /// </summary>
    /// <param name="source">Samples to scale and add.</param>
    /// <param name="first">Gain applied first — the voice gain, in the mix paths.</param>
    /// <param name="second">Gain applied second — a pan or send level.</param>
    /// <param name="destination">Buffer accumulated into; must be at least as long as the source.</param>
    /// <remarks>
    /// The two gains are kept apart rather than pre-multiplied into one. Floating-point
    /// multiplication is no more associative than addition is, so <c>(x·a)·b</c> and <c>x·(a·b)</c>
    /// differ in the last place, and the mix paths reach a bus through the former. Collapsing them
    /// would be the cheaper kernel and a silently different render.
    /// </remarks>
    public static void MixScaled(ReadOnlySpan<float> source, double first, double second, Span<float> destination)
    {
        var width = Vector<float>.Count;
        var i = 0;

        if (Vector.IsHardwareAccelerated && source.Length >= width)
        {
            ref var src = ref MemoryMarshal.GetReference(source);
            ref var dst = ref MemoryMarshal.GetReference(destination);
            var a = new Vector<double>(first);
            var b = new Vector<double>(second);

            for (; i <= source.Length - width; i += width)
            {
                Vector.Widen(Vector.LoadUnsafe(ref src, (nuint)i), out var low, out var high);
                var scaled = Vector.Narrow((low * a) * b, (high * a) * b);
                (Vector.LoadUnsafe(ref dst, (nuint)i) + scaled).StoreUnsafe(ref dst, (nuint)i);
            }
        }

        for (; i < source.Length; i++)
        {
            destination[i] += (float)((source[i] * first) * second);
        }
    }

    /// <summary>Writes a scaled buffer: <c>destination[i] = (float)(source[i] * gain)</c>.</summary>
    /// <param name="source">Samples to scale.</param>
    /// <param name="gain">Scalar gain, applied in double.</param>
    /// <param name="destination">Buffer written; must be at least as long as the source.</param>
    public static void StoreScaled(ReadOnlySpan<float> source, double gain, Span<float> destination)
    {
        var width = Vector<float>.Count;
        var i = 0;

        if (Vector.IsHardwareAccelerated && source.Length >= width)
        {
            ref var src = ref MemoryMarshal.GetReference(source);
            ref var dst = ref MemoryMarshal.GetReference(destination);
            var scale = new Vector<double>(gain);

            for (; i <= source.Length - width; i += width)
            {
                Vector.Widen(Vector.LoadUnsafe(ref src, (nuint)i), out var low, out var high);
                Vector.Narrow(low * scale, high * scale).StoreUnsafe(ref dst, (nuint)i);
            }
        }

        for (; i < source.Length; i++)
        {
            destination[i] = (float)(source[i] * gain);
        }
    }

    /// <summary>Scales a buffer in place: <c>buffer[i] = (float)(buffer[i] * gain)</c>.</summary>
    /// <param name="buffer">Samples, modified in place.</param>
    /// <param name="gain">Scalar gain, applied in double.</param>
    public static void Scale(Span<float> buffer, double gain) => StoreScaled(buffer, gain, buffer);

    /// <summary>
    /// Scales a buffer in place by a moving gain: <c>buffer[i] = (float)(buffer[i] * gains[i])</c>.
    /// </summary>
    /// <param name="buffer">Samples, modified in place.</param>
    /// <param name="gains">One gain per sample; must be at least as long as the buffer.</param>
    public static void ScaleVarying(Span<float> buffer, ReadOnlySpan<double> gains)
    {
        var width = Vector<float>.Count;
        var half = Vector<double>.Count;
        var i = 0;

        if (Vector.IsHardwareAccelerated && buffer.Length >= width)
        {
            ref var samples = ref MemoryMarshal.GetReference(buffer);
            ref var gain = ref MemoryMarshal.GetReference(gains);

            for (; i <= buffer.Length - width; i += width)
            {
                Vector.Widen(Vector.LoadUnsafe(ref samples, (nuint)i), out var low, out var high);
                var scaled = Vector.Narrow(
                    low * Vector.LoadUnsafe(ref gain, (nuint)i),
                    high * Vector.LoadUnsafe(ref gain, (nuint)(i + half)));
                scaled.StoreUnsafe(ref samples, (nuint)i);
            }
        }

        for (; i < buffer.Length; i++)
        {
            buffer[i] = (float)(buffer[i] * gains[i]);
        }
    }

    /// <summary>The largest absolute sample, or a running peak carried in.</summary>
    /// <param name="buffer">Samples to scan.</param>
    /// <param name="seed">A peak already established, to fold this buffer into.</param>
    /// <returns>The larger of the seed and the buffer's greatest absolute value.</returns>
    public static float PeakAbs(ReadOnlySpan<float> buffer, float seed = 0f)
    {
        var width = Vector<float>.Count;
        var i = 0;
        var peak = seed;

        if (Vector.IsHardwareAccelerated && buffer.Length >= width)
        {
            ref var src = ref MemoryMarshal.GetReference(buffer);
            var lanes = new Vector<float>(seed);

            for (; i <= buffer.Length - width; i += width)
            {
                lanes = Vector.Max(lanes, Vector.Abs(Vector.LoadUnsafe(ref src, (nuint)i)));
            }

            for (var lane = 0; lane < width; lane++)
            {
                peak = Math.Max(peak, lanes[lane]);
            }
        }

        for (; i < buffer.Length; i++)
        {
            peak = Math.Max(peak, Math.Abs(buffer[i]));
        }

        return peak;
    }

    /// <summary>Whether any sample is non-zero.</summary>
    /// <param name="values">Samples to scan.</param>
    /// <returns>Whether the buffer carries anything at all.</returns>
    public static bool AnyNonZero(ReadOnlySpan<float> values)
    {
        var width = Vector<float>.Count;
        var i = 0;

        if (Vector.IsHardwareAccelerated && values.Length >= width)
        {
            ref var src = ref MemoryMarshal.GetReference(values);

            for (; i <= values.Length - width; i += width)
            {
                if (!Vector.EqualsAll(Vector.LoadUnsafe(ref src, (nuint)i), Vector<float>.Zero))
                {
                    return true;
                }
            }
        }

        for (; i < values.Length; i++)
        {
            if (values[i] != 0f)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether any value is non-zero.</summary>
    /// <param name="values">Values to scan.</param>
    /// <returns>Whether the array carries anything at all.</returns>
    public static bool AnyNonZero(ReadOnlySpan<double> values)
    {
        var width = Vector<double>.Count;
        var i = 0;

        if (Vector.IsHardwareAccelerated && values.Length >= width)
        {
            ref var src = ref MemoryMarshal.GetReference(values);

            for (; i <= values.Length - width; i += width)
            {
                if (!Vector.EqualsAll(Vector.LoadUnsafe(ref src, (nuint)i), Vector<double>.Zero))
                {
                    return true;
                }
            }
        }

        for (; i < values.Length; i++)
        {
            if (values[i] != 0.0)
            {
                return true;
            }
        }

        return false;
    }
}
