using TabulaSonora.Rom;

namespace TabulaSonora.Dsp;

/// <summary>
/// The segment-rate and segment-shape machine shared by the TVA, TVF and pitch envelopes.
/// </summary>
/// <remarks>
/// <para>
/// An envelope is a chain of segments, each with a target level and a rate byte. The rate byte
/// selects a base duration from <c>g_rate_curve</c>, which two 8.8 multipliers then scale: one from
/// key-follow and one from velocity. A segment completes when a 16-bit phase accumulator wraps, which
/// makes the duration <c>0x10000 / (rate × speed) × 10 ms</c> at the 100 Hz control tick.
/// </para>
/// <para>
/// Everything here is integer arithmetic with deliberate 16-bit truncation. The control path of the
/// original is exclusively 16-bit fixed point, and several of these expressions depend on wrapping or
/// on truncation toward zero.
/// </para>
/// </remarks>
public sealed class EnvelopeMachine
{
    /// <summary>Neutral value of an 8.8 multiplier, meaning 1.0.</summary>
    public const int UnityMultiplier = 0x100;

    /// <summary>A rate-curve entry below this yields a zero-length segment.</summary>
    public const int MinimumSegmentTicks = 9;

    private readonly ushort[] _rateCurve;
    private readonly ushort[] _rateOut;
    private readonly byte[] _scaleCurve;
    private readonly double[] _shape;

    /// <summary>Creates the machine over a loaded table set.</summary>
    /// <param name="tables">The static tables.</param>
    public EnvelopeMachine(TableSet tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        _rateCurve = tables.RateCurve;
        _rateOut = tables.EnvRateOut;
        _scaleCurve = tables.EnvScaleCurve;

        // Only the first 256 entries of g_env_shape are used; the cache over-reads.
        _shape = new double[256];
        for (var i = 0; i < _shape.Length; i++)
        {
            _shape[i] = tables.EnvShape[i] / 65535.0;
        }
    }

    /// <summary>
    /// True when a segment interpolates linearly rather than through the fast-approach curve.
    /// </summary>
    /// <param name="rateByte">The segment's rate byte.</param>
    /// <returns>Whether the segment is linear.</returns>
    /// <remarks>
    /// Bit 7 of the rate byte is the segment <em>shape</em> flag, not part of the rate. The TVF and
    /// pitch envelopes hardcode linear; the TVA is the only one where the shape is data-driven.
    /// </remarks>
    public static bool IsLinearSegment(int rateByte) => (rateByte & 0x80) != 0;

    /// <summary>
    /// Converts a base rate byte and a 0x40-neutral modifier into an 8.8 rate multiplier.
    /// </summary>
    /// <param name="baseRate">Base rate byte, normally a key-follow table entry.</param>
    /// <param name="modifier">0x40-neutral modifier from the partial block.</param>
    /// <returns>The multiplier, <see cref="UnityMultiplier"/> for no change.</returns>
    public int RateScale(int baseRate, int modifier)
    {
        var u = modifier - 0x40;
        if (u == 0)
        {
            return UnityMultiplier;
        }

        if (u < 0)
        {
            // Negative modifiers mirror the curve: negate the signed base and walk the other way.
            var signed = (sbyte)baseRate;
            u = -u;
            baseRate = (-signed) & 0xFF;
            if (baseRate == 0)
            {
                baseRate = 0xFF;
            }
        }

        var centred = (short)(baseRate - 0x80);
        if (centred == 0)
        {
            return UnityMultiplier;
        }

        var scale = _scaleCurve[u];

        // Kept as int so the shift stays arithmetic; on uint it would be logical and the negative
        // branch would index the wrong end of the output curve.
        return centred < 0
            ? _rateOut[(0x80 - ((centred * scale * -2) >> 8)) & 0x1FF]
            : _rateOut[(((centred * scale * 2) >> 8) + 0x80) & 0x1FF];
    }

    /// <summary>
    /// Converts a level byte and a 0x40-neutral modifier into an 8.8 multiplier.
    /// </summary>
    /// <param name="level">Level byte — for the TVA this is the crossfaded partial level.</param>
    /// <param name="modifier">0x40-neutral velocity-sensitivity byte.</param>
    /// <returns>The multiplier, <see cref="UnityMultiplier"/> for no change.</returns>
    public int LevelScale(int level, int modifier)
    {
        var fromLevel = 0x40 - level;
        if (fromLevel == 0)
        {
            return UnityMultiplier;
        }

        var fromModifier = modifier - 0x40;
        if (fromModifier == 0)
        {
            return UnityMultiplier;
        }

        int product;
        if (fromLevel < 0)
        {
            if (fromModifier >= 0)
            {
                return _rateOut[(0x80 - Shift8(-(_scaleCurve[(fromModifier * 2) & 0xFF] * fromLevel))) & 0x1FF];
            }

            product = -(_scaleCurve[(-fromModifier * 2) & 0xFF] * fromLevel);
        }
        else
        {
            if (fromModifier < 0)
            {
                return _rateOut[(0x80 - Shift8(_scaleCurve[(-fromModifier * 2) & 0xFF] * fromLevel)) & 0x1FF];
            }

            product = _scaleCurve[(fromModifier * 2) & 0xFF] * fromLevel;
        }

        return _rateOut[(Shift8(product) + 0x80) & 0x1FF];
    }

    /// <summary>
    /// The duration of one envelope segment in milliseconds.
    /// </summary>
    /// <param name="rateByte">The segment's rate byte; bit 7 is the shape flag and is masked off.</param>
    /// <param name="rateMultiplier">8.8 multiplier from <see cref="RateScale"/>.</param>
    /// <param name="velocityMultiplier">8.8 multiplier from <see cref="LevelScale"/>.</param>
    /// <param name="bias">Part-level rate offset; zero when both patch rate parameters are neutral.</param>
    /// <returns>The duration in milliseconds, or zero for an instant segment.</returns>
    public double SegmentMilliseconds(
        int rateByte,
        int rateMultiplier,
        int velocityMultiplier = UnityMultiplier,
        int bias = 0)
    {
        var index = (rateByte & 0x7F) + bias;
        if (index < 0)
        {
            return 0.0;
        }

        var ticks = _rateCurve[Math.Min(index, 0x7F)];
        if (ticks < MinimumSegmentTicks)
        {
            return 0.0;
        }

        var scaled = Math.Min(0xFFFF, (rateMultiplier * ticks) >> 8);
        return (velocityMultiplier * scaled) >> 8;
    }

    /// <summary>
    /// Interpolates one segment's value at a normalised position.
    /// </summary>
    /// <param name="position">Position through the segment, 0 to 1.</param>
    /// <param name="start">Value at the start of the segment.</param>
    /// <param name="target">Value at the end of the segment.</param>
    /// <param name="linear">Whether the segment is linear rather than fast-approach.</param>
    /// <returns>The interpolated value.</returns>
    /// <remarks>
    /// The fast-approach curve must be <em>interpolated</em> between adjacent entries using the low
    /// byte of the phase. A bare 256-level lookup steps across the segment, and on a long decay those
    /// steps are amplitude discontinuities that inject a broadband noise floor — roughly 80 dB of
    /// sustain signal-to-noise against the engine's 96 dB.
    /// </remarks>
    public double SegmentCurve(double position, double start, double target, bool linear)
    {
        position = Math.Clamp(position, 0.0, 1.0);
        if (linear)
        {
            return start + ((target - start) * position);
        }

        var phase = (int)(position * 0xFFFF);
        var index = phase >> 8;
        var fraction = (phase & 0xFF) / 256.0;

        // The curve is walked backwards: entry 255 at the start of the segment down to 0 at the end.
        var i0 = Math.Clamp(255 - index, 0, 255);
        var i1 = Math.Clamp(254 - index, 0, 255);
        var remaining = _shape[i0] + ((_shape[i1] - _shape[i0]) * fraction);

        return target + ((start - target) * remaining);
    }

    /// <summary>Applies the engine's <c>(short)(v &lt;&lt; 2) &gt;&gt; 8</c> idiom.</summary>
    /// <param name="value">Value to scale.</param>
    /// <returns>The scaled value.</returns>
    /// <remarks>
    /// The 16-bit truncation between the shifts is load-bearing — it is what makes large products
    /// wrap rather than saturate.
    /// </remarks>
    public static int Shift8(int value) => (short)(value << 2) >> 8;
}
