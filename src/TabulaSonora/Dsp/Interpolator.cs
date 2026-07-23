using System.Runtime.CompilerServices;
using TabulaSonora.Rom;

namespace TabulaSonora.Dsp;

/// <summary>
/// The sampler's 4-tap FIR resampler — the single most timbre-defining element of the engine.
/// </summary>
/// <remarks>
/// <para>
/// 128 phase rows of four coefficients, indexed by the top seven bits of the fractional phase, with
/// no interpolation between rows. Every row sums to 1.0.
/// </para>
/// <para>
/// The row at zero fractional phase is <c>[0.174, 0.653, 0.173, 1e-5]</c> — not a passthrough. That
/// mild lowpass is applied at <em>every</em> sample regardless of pitch, and it is why linear
/// interpolation sounds measurably brighter: at a quarter of the sample rate this kernel passes
/// 0.638 where linear passes 0.707.
/// </para>
/// </remarks>
public sealed class Interpolator
{
    /// <summary>Number of phase rows in the kernel.</summary>
    public const int PhaseCount = 128;

    /// <summary>Taps per phase row.</summary>
    public const int TapCount = 4;

    private readonly float[] _coefficients;

    /// <summary>Creates the resampler over a loaded table set.</summary>
    /// <param name="tables">The static tables.</param>
    public Interpolator(TableSet tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        _coefficients = tables.InterpCoef;
    }

    /// <summary>Reads one sample at a fractional position.</summary>
    /// <param name="buffer">Source samples.</param>
    /// <param name="position">Fractional read position.</param>
    /// <returns>The interpolated sample.</returns>
    /// <remarks>
    /// The window reaches from <c>i−1</c> to <c>i+2</c>, so the index is clamped to leave room for
    /// all four taps. Near a loop boundary the caller must supply the samples the loop wraps to, not
    /// whatever follows it in the buffer.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Sample(ReadOnlySpan<float> buffer, double position)
    {
        var index = (int)Math.Floor(position);
        var phase = Math.Clamp((int)((position - index) * PhaseCount), 0, PhaseCount - 1);
        index = Math.Clamp(index, 1, buffer.Length - 3);

        var c = phase * TapCount;
        return (_coefficients[c] * buffer[index - 1])
             + (_coefficients[c + 1] * buffer[index])
             + (_coefficients[c + 2] * buffer[index + 1])
             + (_coefficients[c + 3] * buffer[index + 2]);
    }

    /// <summary>Resamples a buffer at a series of fractional positions.</summary>
    /// <param name="buffer">Source samples.</param>
    /// <param name="positions">Read positions, one per output sample.</param>
    /// <param name="destination">Receives the resampled output.</param>
    public void Resample(ReadOnlySpan<float> buffer, ReadOnlySpan<double> positions, Span<float> destination)
    {
        for (var i = 0; i < destination.Length && i < positions.Length; i++)
        {
            destination[i] = Sample(buffer, positions[i]);
        }
    }
}

/// <summary>
/// The engine's pan law: an exact 128-entry table, not a computed curve.
/// </summary>
/// <remarks>
/// Left reads <c>T[127 − p]</c> and right reads <c>T[p − 1]</c>, so both land on index 63 at the
/// centre and pan 64 is exactly symmetric at 75/127 ≈ 0.5906 — neither the 0.707 of a constant-power
/// law nor the 0.5 of a linear one. Recovered by sweeping the controller through the engine and
/// reading per-channel RMS, and it reproduces that sweep to within 0.00037.
/// </remarks>
public sealed class PanLaw
{
    /// <summary>Pan value that sounds centred.</summary>
    public const int Centre = 64;

    private readonly byte[] _table;

    /// <summary>Creates the pan law over a loaded table set.</summary>
    /// <param name="tables">The static tables.</param>
    public PanLaw(TableSet tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        _table = tables.Pan;
    }

    /// <summary>The left and right gains for a pan position.</summary>
    /// <param name="pan">Pan position, 0 to 127, with 64 centred.</param>
    /// <returns>The two gains.</returns>
    /// <remarks>Pan 0 is fully left: the right channel is silent, not merely attenuated.</remarks>
    public (double Left, double Right) Gains(int pan)
    {
        var p = Math.Clamp(pan, 0, 127);
        var left = _table[127 - p] / 127.0;
        var right = p == 0 ? 0.0 : _table[p - 1] / 127.0;
        return (left, right);
    }
}
