using TabulaSonora.Patches;
using TabulaSonora.Rom;

namespace TabulaSonora.Dsp;

/// <summary>
/// The filter side of a voice: the cutoff chain that turns static bytes into the two coefficients the
/// state-variable filter needs, and the envelope that moves the cutoff over time.
/// </summary>
/// <remarks>
/// <para>
/// The cutoff runs through four stages. A derives the resonance byte, B sums the base with the
/// envelope, C warps the sum and clamps it to a resonance-dependent ceiling, and D produces the Q
/// trim. The warped result then passes through an exponential table before it reaches the filter,
/// which is the step an earlier attempt missed: feeding linear cutoff units to a Chamberlin filter
/// drives <c>f</c> to about 1.9, where the state matrix diverges.
/// </para>
/// <para>
/// Cutoff is note-independent. There is no key-follow on it; brightness tracks the keyboard through
/// the multisample instead.
/// </para>
/// </remarks>
public sealed class TvfChain
{
    /// <summary>Samples between coefficient refreshes — the 100 Hz control tick.</summary>
    public const int ControlBlockSamples = 320;

    // The env-depth region is addressed by absolute VA in the decompile. Relative to the start of
    // the cached slice (VA 0x1819a2f00) the two sub-tables sit here:
    //   0x1819a3028 - 0x1819a2f00 = 0x128   (depth below neutral)
    //   0x1819a2fa8 - 0x1819a2f00 = 0x0a8   (depth at or above neutral)
    private const int EnvDepthLowBranch = 0x128;
    private const int EnvDepthHighBranch = 0x0A8;

    // g_pitch_bias lives inside the pitch-envelope export: 0x1819a2890 - 0x1819a2578 = 0x318.
    private const int PitchBiasOffset = 0x318;

    private readonly EnvelopeMachine _envelope;
    private readonly byte[] _envDepth;
    private readonly byte[] _pitchEnv;
    private readonly short[] _envDepthKeyFollow;
    private readonly short[] _resoCurve;
    private readonly byte[] _rateKeyFollow;
    private readonly uint[] _filterTypeCoef;
    private readonly ushort[] _warp;
    private readonly ushort[] _ceiling;
    private readonly ushort[] _qLowPass;
    private readonly ushort[] _qType6;
    private readonly int[] _rampExp;

    /// <summary>Creates the chain over a loaded table set.</summary>
    /// <param name="tables">The static tables.</param>
    /// <param name="envelope">The shared segment machine.</param>
    public TvfChain(TableSet tables, EnvelopeMachine envelope)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(envelope);

        _envelope = envelope;
        _envDepth = tables.TvfEnvDepth;
        _pitchEnv = tables.PitchEnv;
        _envDepthKeyFollow = tables.KfTvfEnv;
        _resoCurve = tables.ResoCurve;
        _rateKeyFollow = tables.KfTvfRate;
        _filterTypeCoef = tables.FilterTypeCoef;
        _warp = tables.TvfCutoffWarp;
        _ceiling = tables.TvfCutoffCeil;
        _qLowPass = tables.TvfQLp;
        _qType6 = tables.TvfQT6;
        _rampExp = tables.RampExp;
    }

    /// <summary>
    /// Which filter response a partial takes, or <see cref="FilterTap.Bypass"/>.
    /// </summary>
    /// <param name="filterType">The partial's filter-type byte.</param>
    /// <returns>The tap.</returns>
    /// <remarks>
    /// Only types 0, 1, 2, 4, 5 and 6 are valid; 3 and 7 bypass. The response comes from bits 10–11
    /// of the type's coefficient word, so the mapping is not the obvious one — type 1 is highpass and
    /// type 2 is bandpass.
    /// </remarks>
    public FilterTap Tap(int filterType)
    {
        var type = filterType & 0xFF;
        if (type > 2 && ((type - 4) & 0xFF) > 2)
        {
            return FilterTap.Bypass;
        }

        return (_filterTypeCoef[type] & 0xC00) switch
        {
            0x000 => FilterTap.LowPass,
            0x400 => FilterTap.BandPass,
            0x800 => FilterTap.HighPass,
            _ => FilterTap.Notch,
        };
    }

    /// <summary>
    /// The resonance byte — stage A.
    /// </summary>
    /// <param name="partial">The partial's parameter block.</param>
    /// <param name="partResonance">GS TVF Resonance for the part, 0x40 neutral.</param>
    /// <param name="partResonanceDefault">The per-program resonance default, 0x40 neutral.</param>
    /// <returns>The resonance byte, floored at 4.</returns>
    public static int ResonanceByte(PartialParameters partial, int partResonance = 0x40, int partResonanceDefault = 0x40)
    {
        var value = (2 * (0x80 - partResonanceDefault - partResonance)) + partial.Resonance;
        return Math.Max(4, Math.Min(0x7F, Math.Max(0, value)));
    }

    /// <summary>
    /// Warps a 15-bit cutoff sum and clamps it to the resonance-dependent ceiling — stage C.
    /// </summary>
    /// <param name="cutoff15">The 15-bit cutoff sum.</param>
    /// <param name="resonanceByte">The resonance byte from <see cref="ResonanceByte"/>.</param>
    /// <returns>The cutoff in the runtime units the filter consumes.</returns>
    /// <remarks>
    /// The ceiling at neutral resonance times four is 245,760 — the "fully open" constant an earlier
    /// calibration measured empirically. It is a ceiling, not a saturation of the sum.
    /// </remarks>
    public int CutoffUnits(double cutoff15, int resonanceByte)
    {
        var c = (int)Math.Clamp(cutoff15, 0, 0x7FFF);
        var high = c >> 8;
        var low = c & 0xFF;

        int value = _warp[high];
        if (low != 0)
        {
            value += ((_warp[high + 1] - value) * low) >> 8;
        }

        return Math.Min(value, _ceiling[resonanceByte]) << 2;
    }

    /// <summary>
    /// The filter's frequency coefficient.
    /// </summary>
    /// <param name="units">Cutoff in runtime units, from <see cref="CutoffUnits"/>.</param>
    /// <returns>The coefficient <c>f</c>.</returns>
    /// <remarks>
    /// Every ramp target passes through the exponential table before it reaches the filter, so what
    /// the filter sees is exponential in the cutoff units. This collapses to
    /// <c>f = 2^(C / 16384 − 15)</c>, and Chamberlin's <c>f = 2·sin(π·fc/fs)</c> then gives the cutoff
    /// in Hz with no fitted constant at all. The table entries reach 2¹⁸, so the interpolation must be
    /// widened before the shift.
    /// </remarks>
    public double FrequencyCoefficient(int units)
    {
        var x = Math.Clamp(units, 0, 0x3FFFF);
        var index = (x >> 6) & 0xFF;
        var fraction = x & 0x3F;

        var value = (((long)_rampExp[index] * (0x40 - fraction)) + ((long)_rampExp[index + 1] * fraction)) >> 6;
        value >>= 0xF - ((x >> 0xE) & 0xF);

        return Math.Min(0x7FFF, value >> 3) / 16384.0;
    }

    /// <summary>
    /// The filter's damping coefficient — stage D.
    /// </summary>
    /// <param name="units">Cutoff in runtime units.</param>
    /// <param name="resonanceByte">The resonance byte.</param>
    /// <param name="filterType">The partial's filter-type byte.</param>
    /// <returns>The coefficient <c>q</c>.</returns>
    /// <remarks>
    /// This is reciprocal-Q, so the neutral resonance byte 0x40 yields exactly 1.0 and smaller values
    /// are more resonant — effectively <c>Q = 64 / resonanceByte</c>.
    /// </remarks>
    public double DampingCoefficient(int units, int resonanceByte, int filterType)
    {
        int raw;
        if (filterType == 0 && resonanceByte == 0x40)
        {
            raw = _qLowPass[(units >> 2) >> 8] << 2;
        }
        else if (filterType == 6)
        {
            raw = _qType6[(units >> 2) >> 8] << 2;
        }
        else
        {
            raw = resonanceByte << 11;
        }

        return raw / (double)0x20000;
    }

    /// <summary>The cutoff in Hz. Diagnostic only — the filter never needs it.</summary>
    /// <param name="cutoff15">The 15-bit cutoff sum.</param>
    /// <param name="resonanceByte">The resonance byte.</param>
    /// <param name="sampleRate">Internal sample rate.</param>
    /// <returns>The cutoff frequency.</returns>
    public double CutoffHz(double cutoff15, int resonanceByte, int sampleRate = 32000)
    {
        var f = FrequencyCoefficient(CutoffUnits(cutoff15, resonanceByte));
        return sampleRate / Math.PI * Math.Asin(Math.Min(1.0, f / 2.0));
    }

    /// <summary>
    /// The envelope's peak and its five stage offsets, in cutoff units relative to that peak.
    /// </summary>
    /// <param name="partial">The partial's parameter block.</param>
    /// <param name="key">MIDI key, for the depth key-follow.</param>
    /// <param name="velocity">MIDI velocity.</param>
    /// <returns>The peak, the four segment offsets, and the release offset.</returns>
    public (int Peak, int[] Segments, int Release) EnvelopeOffsets(PartialParameters partial, int key, int velocity)
    {
        var raw = partial.Raw;

        var keyFollow = _envDepthKeyFollow[((raw[0x32] & 0x0F) * 0x80) + (key & 0x7F)];
        int depth = raw[0x33];

        int peak;
        if (depth < 0x40)
        {
            var value = EnvDepthWord(EnvDepthLowBranch + (0x80 - (depth * 2)));
            peak = keyFollow >= 0 ? -((keyFollow * value) >> 9) : (-keyFollow * value) >> 9;
        }
        else
        {
            var value = EnvDepthWord(EnvDepthHighBranch + (depth * 2));
            peak = keyFollow >= 0 ? (keyFollow * value) >> 9 : -((-keyFollow * value) >> 9);
        }

        // Resonance scaling of the envelope depth. Note block[0x4a] is this scaler, not resonance --
        // that is block[0x30].
        var resonanceScale = raw[0x4A] - 0x40;
        int c0;
        var negated = 0;
        if (resonanceScale == 0)
        {
            c0 = 0x7FFF;
        }
        else
        {
            var s = 0x7F - Math.Min(127, velocity);
            c0 = resonanceScale < 0
                ? (_resoCurve[Math.Abs(resonanceScale) & 0x3F] * s) - 0x7FFF
                : 0x7FFF - (_resoCurve[resonanceScale & 0x3F] * s);

            if (c0 < 0)
            {
                c0 = -c0;
                negated = 1;
            }
        }

        var multiplier = raw[0x38] | (raw[0x39] << 8);

        int Convert(int level)
        {
            var offset = level - 0x40;
            var magnitude = Math.Abs(offset);
            if (magnitude == 0)
            {
                return peak;
            }

            var flag = offset >= 0 ? negated : ~negated & 1;

            // The 31-bit mask is a sign-strip, not a 32-bit truncation.
            var delta = ((((multiplier * c0) & 0x7FFFFFFF) >> 15) * PitchBias(magnitude)) >> 7;
            return flag == 0 ? peak + delta : peak - delta;
        }

        var segments = new int[4];
        for (var i = 0; i < 4; i++)
        {
            segments[i] = Convert(raw[0x3A + i]) - peak;
        }

        return (peak, segments, Convert(raw[0x3E]) - peak);
    }

    /// <summary>
    /// Builds the cutoff envelope for one note, ready to be evaluated at any sample position.
    /// </summary>
    /// <param name="partial">The partial's parameter block.</param>
    /// <param name="velocity">MIDI velocity.</param>
    /// <param name="key">MIDI key.</param>
    /// <param name="sampleRate">Internal sample rate.</param>
    /// <returns>
    /// The envelope, which yields an <em>offset</em> from the peak, and the base cutoff that offset
    /// is added to before the sum is clamped to 15 bits.
    /// </returns>
    /// <remarks>
    /// The running level starts at zero rather than at the release level: all five targets are made
    /// relative to the peak, and the peak itself is folded into the base cutoff instead. TVF segments
    /// are always linear — unlike the TVA, the shape is not data-driven here.
    /// </remarks>
    public (SegmentEnvelope Offsets, int BaseCutoff) CreateEnvelope(
        PartialParameters partial,
        int velocity,
        int key,
        int sampleRate = 32000)
    {
        var raw = partial.Raw;
        var (peak, segments, release) = EnvelopeOffsets(partial, key, velocity);

        var mainRate = _envelope.RateScale(
            (_rateKeyFollow[(raw[0x46] * 0x80) + (key & 0x7F)] - 0x80) & 0xFF, raw[0x48]);

        // Two velocity level-scales, as in the TVA: 0x4b for segments 0-1, 0x4c for 2-3 and release.
        // Sharing 0x4b makes the later segments run about 1.45x too fast on patches where they differ,
        // closing the filter early in the sustain.
        var velocityEarly = _envelope.LevelScale(velocity, raw[0x4B]);
        var velocityLate = _envelope.LevelScale(velocity, raw[0x4C]);

        var targets = new double[SegmentEnvelope.SegmentCount];
        var segmentSamples = new double[SegmentEnvelope.SegmentCount];
        var linear = new bool[SegmentEnvelope.SegmentCount];

        for (var i = 0; i < targets.Length; i++)
        {
            var milliseconds = _envelope.SegmentMilliseconds(
                raw[0x3F + i], mainRate, i < 2 ? velocityEarly : velocityLate);

            targets[i] = segments[i];

            // Every TVF segment occupies at least 2 ms, so an instant one still slews rather than
            // stepping the coefficients.
            segmentSamples[i] = Math.Max(milliseconds / 1000.0, 0.002) * sampleRate;
            linear[i] = true;
        }

        // The release has its own rate row and modifier -- 0x47/0x49, not the main 0x46/0x48.
        var releaseRate = _envelope.RateScale(
            (_rateKeyFollow[(raw[0x47] * 0x80) + (key & 0x7F)] - 0x80) & 0xFF, raw[0x49]);
        var releaseMs = _envelope.SegmentMilliseconds(raw[0x43], releaseRate, velocityLate);

        var envelope = new SegmentEnvelope(
            _envelope, targets, segmentSamples, linear,
            releaseTarget: release,
            releaseSamples: Math.Max(releaseMs / 1000.0 * sampleRate, 1e-9),
            releaseLinear: true,
            afterRelease: release);

        // Stage B: the part-level cutoff terms cancel at neutral, so only the base and envelope remain.
        return (envelope, Math.Min(0x7FFF, (raw[0x2F] * 0x100) + peak));
    }

    /// <summary>
    /// The 15-bit cutoff trajectory over a note.
    /// </summary>
    /// <param name="partial">The partial's parameter block.</param>
    /// <param name="velocity">MIDI velocity.</param>
    /// <param name="key">MIDI key.</param>
    /// <param name="holdSeconds">Time from note-on to note-off.</param>
    /// <param name="tailSeconds">Time to render past note-off.</param>
    /// <param name="sampleRate">Internal sample rate.</param>
    /// <returns>Per-sample cutoff sums, clamped to 15 bits.</returns>
    public double[] Envelope(
        PartialParameters partial,
        int velocity,
        int key,
        double holdSeconds,
        double tailSeconds,
        int sampleRate = 32000)
    {
        var sampleCount = (int)((holdSeconds + tailSeconds) * sampleRate);
        if (sampleCount <= 0)
        {
            return [];
        }

        var (envelope, baseCutoff) = CreateEnvelope(partial, velocity, key, sampleRate);
        envelope.NoteOff(Math.Min((int)(holdSeconds * sampleRate), sampleCount));

        var offsets = new double[sampleCount];
        for (var n = 0; n < sampleCount; n++)
        {
            offsets[n] = Math.Clamp(baseCutoff + envelope.ValueAt(n), 0, 0x7FFF);
        }

        return offsets;
    }

    /// <summary>
    /// Filters a signal with a cutoff that moves per control block.
    /// </summary>
    /// <param name="signal">Input samples, modified in place.</param>
    /// <param name="cutoff15">Per-sample cutoff sums from <see cref="Envelope"/>.</param>
    /// <param name="filterType">The partial's filter-type byte.</param>
    /// <param name="resonanceByte">The resonance byte.</param>
    /// <param name="blockSamples">Samples between coefficient refreshes.</param>
    /// <remarks>
    /// Coefficients refresh once per control block, matching the engine's 100 Hz rate. The engine
    /// additionally slews them over 2–40 ms through its anti-zipper ramps, which is not modelled.
    /// </remarks>
    public void Apply(
        Span<float> signal,
        ReadOnlySpan<double> cutoff15,
        int filterType,
        int resonanceByte,
        int blockSamples = ControlBlockSamples)
    {
        var tap = Tap(filterType);
        if (tap == FilterTap.Bypass)
        {
            return;
        }

        var filter = default(StateVariableFilter);

        for (var start = 0; start < signal.Length; start += blockSamples)
        {
            var end = Math.Min(start + blockSamples, signal.Length);

            var sum = 0.0;
            for (var n = start; n < end; n++)
            {
                sum += cutoff15[n];
            }

            var units = CutoffUnits(sum / (end - start), resonanceByte);
            var f = FrequencyCoefficient(units);
            var q = DampingCoefficient(units, resonanceByte, filterType);

            for (var n = start; n < end; n++)
            {
                signal[n] = (float)filter.Process(signal[n], f, q, tap);
            }
        }
    }

    private int EnvDepthWord(int offset)
    {
        if (offset < 0 || offset + 1 >= _envDepth.Length)
        {
            throw new InvalidOperationException(
                $"TVF env-depth read at offset {offset} is outside the {_envDepth.Length}-byte table.");
        }

        return _envDepth[offset] | (_envDepth[offset + 1] << 8);
    }

    private int PitchBias(int magnitude)
    {
        var offset = PitchBiasOffset + (magnitude & 0xFF);
        if (offset >= _pitchEnv.Length)
        {
            throw new InvalidOperationException(
                $"Pitch-bias read at offset {offset} is outside the {_pitchEnv.Length}-byte table.");
        }

        return _pitchEnv[offset];
    }
}
