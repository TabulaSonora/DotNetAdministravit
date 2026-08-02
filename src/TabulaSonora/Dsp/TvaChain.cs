using TabulaSonora.Patches;
using TabulaSonora.Rom;

namespace TabulaSonora.Dsp;

/// <summary>
/// The amplitude side of a voice: the level chain that turns static bytes into a gain, and the
/// four-segment envelope that moves it.
/// </summary>
/// <remarks>
/// <para>
/// Levels are logarithmic and, composed through the amplitude curves, work out to amplitude
/// <em>squared</em> — <c>g_level_curve</c> through <c>g_amp_curve</c> is <c>(l / 127)²</c> to within
/// 6e-05. The chain subtracts four attenuations from a base, clamping to 1 after each: the partial
/// level, the crossfaded velocity level, the multisample zone level, and the tone's master level.
/// </para>
/// <para>
/// Dropping the last two is what once left a 1.15–2.06× per-voice residual against the engine's own
/// gain word. With all four applied the reproduction is exact to the harness's print precision.
/// </para>
/// </remarks>
public sealed class TvaChain
{
    /// <summary>
    /// The engine hands the sampler twice the amplitude the level chain yields.
    /// </summary>
    public const double AmpScale = 2.0;

    /// <summary>The control-tick rate, which is the grid note-off is acted on.</summary>
    public const int ControlTickHz = 100;

    private readonly ushort[] _levelCurve;
    private readonly ushort[] _ampHigh;
    private readonly ushort[] _ampLow;
    private readonly byte[] _velocityCurve;
    private readonly byte[] _scaleCurve;
    private readonly byte[] _levelKeyFollow;
    private readonly byte[] _rateKeyFollow0;
    private readonly byte[] _rateKeyFollow1;
    private readonly byte[] _velocityCrossfade;

    /// <summary>Creates the chain over a loaded table set.</summary>
    /// <param name="tables">The static tables.</param>
    /// <param name="envelope">The shared segment machine.</param>
    public TvaChain(TableSet tables, EnvelopeMachine envelope)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(envelope);

        Envelope = envelope;
        _levelCurve = tables.LevelCurve;
        _ampHigh = tables.AmpCurveHi;
        _ampLow = tables.AmpCurveLo;
        _velocityCurve = tables.VelCurve;
        _scaleCurve = tables.EnvScaleCurve;
        _levelKeyFollow = tables.KfTvaLevel;
        _rateKeyFollow0 = tables.KfTvaRate0;
        _rateKeyFollow1 = tables.KfTvaRate1;
        _velocityCrossfade = tables.VelXfade;
    }

    /// <summary>The shared segment-rate machine.</summary>
    public EnvelopeMachine Envelope { get; }

    /// <summary>
    /// Converts a 16-bit logarithmic level to a linear gain in [0, 1].
    /// </summary>
    /// <param name="level16">The logarithmic level.</param>
    /// <returns>The linear gain.</returns>
    /// <remarks>
    /// The two table entries multiply to more than 32 bits before the shift, so the product must be
    /// widened. Note also that <c>g_amp_curve_hi[0]</c> is 4 rather than 0, so this returns 4.6e-05
    /// at level zero — a level that has decayed past the floor must be forced to true silence rather
    /// than clamped into the table.
    /// </remarks>
    public double AmpOf(int level16)
    {
        var level = Math.Clamp(level16, 0, 0xFFFF);
        var product = (long)_ampHigh[level >> 8] * _ampLow[level & 0xFF];
        return (product >> 16) / 65535.0;
    }

    /// <summary>
    /// The partial's velocity-crossfaded level — the byte the engine keeps at <c>voice+0x164</c>.
    /// </summary>
    /// <param name="partial">The partial's parameter block.</param>
    /// <param name="velocity">MIDI velocity.</param>
    /// <returns>The level, or <see langword="null"/> when the velocity window gates the partial off.</returns>
    /// <remarks>
    /// This is <em>not</em> the MIDI velocity, which lives at <c>voice+0x166</c>. Everything
    /// downstream — the second attenuation in <see cref="BaseLevel"/> and both envelope rate
    /// multipliers — reads this instead.
    /// </remarks>
    public int? PartialLevel(PartialParameters partial, int velocity)
    {
        var raw = partial.Raw;
        int low = raw[0x4F];
        int high = raw[0x51];
        var windowLow = Math.Min(low, high);
        var windowHigh = Math.Max(low, high);

        if (velocity < windowLow || velocity > windowHigh)
        {
            return null;
        }

        int position;
        if (windowHigh == windowLow)
        {
            position = 0x7F;
        }
        else
        {
            var offset = velocity - windowLow;
            var widened = ((offset * 0x100) | (offset >> 8)) & 0xFFFF;
            var scaled = widened / (windowHigh - windowLow);
            position = scaled > 0xFF ? 0x7F : scaled >> 1;
        }

        var span = (sbyte)((raw[0x52] - raw[0x50]) & 0xFF);

        // A window stored high-to-low crossfades the other way round.
        var index = low <= high ? position : (windowLow - position) & 0xFF;

        var curve = _velocityCrossfade[(partial.VelocityCrossfadeCurve * 0x80) + index];
        var delta = (sbyte)(((curve * Math.Abs(span)) + 0x7F) * 2 >> 8);

        var level = (sbyte)((raw[0x50] + (span >= 0 ? delta : -delta)) & 0xFF);
        return level == 0 ? 1 : level;
    }

    /// <summary>
    /// The 16-bit logarithmic base level for a voice, before the envelope moves it.
    /// </summary>
    /// <param name="partial">The partial's parameter block.</param>
    /// <param name="partialLevel">The crossfaded level from <see cref="PartialLevel"/>.</param>
    /// <param name="key">MIDI key, for the level key-follow.</param>
    /// <param name="zoneLevel">The multisample's per-key-zone level.</param>
    /// <param name="toneLevel">The tone header's master level.</param>
    /// <returns>The base level.</returns>
    public int BaseLevel(PartialParameters partial, int partialLevel, int key, int zoneLevel, int toneLevel)
    {
        var raw = partial.Raw;
        var level = 0xFFFF - _levelCurve[raw[0x53]];

        var keyFollow = (sbyte)_levelKeyFollow[(raw[0x54] * 0x80) + (key & 0x7F)];
        var depth = raw[0x55] - 0x40;

        if (depth != 0 && keyFollow != 0)
        {
            var scale = _scaleCurve[Math.Abs(depth) & 0xFF];
            var term = _velocityCurve[((Math.Abs(keyFollow) * scale * 2) >> 8) & 0xFF] * 0x100;

            // Matching signs reinforce, mixed signs oppose.
            var sameSign = (depth < 0 && keyFollow < 0) || (depth >= 0 && keyFollow >= 0);
            level = sameSign ? Math.Min(0xFFFF, level + term) : level - term;
            if (level < 1)
            {
                level = 1;
            }
        }

        foreach (var attenuation in (int[])
        [
            _levelCurve[Math.Clamp(partialLevel, 0, 127)],
            _levelCurve[Math.Clamp(zoneLevel, 0, 127)],
            _levelCurve[Math.Clamp(toneLevel, 0, 127)],
        ])
        {
            level -= attenuation;
            if (level < 1)
            {
                level = 1;
            }
        }

        return level;
    }

    /// <summary>
    /// Builds the amplitude envelope for one note, ready to be evaluated at any sample position.
    /// </summary>
    /// <param name="partial">The partial's parameter block.</param>
    /// <param name="velocity">MIDI velocity.</param>
    /// <param name="key">MIDI key.</param>
    /// <param name="zoneLevel">The multisample's per-key-zone level.</param>
    /// <param name="toneLevel">The tone header's master level.</param>
    /// <param name="sampleRate">Internal sample rate.</param>
    /// <param name="attackMilliseconds">
    /// Optional attack softening. Zero is faithful — the engine's amplitude attack is instant.
    /// </param>
    /// <returns>The envelope, in the linear gain domain.</returns>
    /// <remarks>
    /// The envelope does not need to know when the note ends: call
    /// <see cref="SegmentEnvelope.NoteOff(long, int)"/> when it does. That is what lets the real-time voice loop
    /// and the offline renderer share one implementation.
    /// </remarks>
    /// <param name="rateKey">
    /// Index for the two rate key-follow tables when it differs from <paramref name="key"/>. The
    /// engine keeps it at <c>voice+0x161</c>, which <c>voice_trigger_partials</c> fills from the note
    /// plus a transpose; on a drum part that works out to the kit's coarse-pitch plane, so it is not
    /// the MIDI key. Measured over the 61 sounding keys of the SC-55 standard kit, the plane equals
    /// the engine's index for all 61. <see langword="null"/> uses <paramref name="key"/>, which is
    /// what a melodic note wants.
    /// </param>
    public SegmentEnvelope CreateEnvelope(
        PartialParameters partial,
        int velocity,
        int key,
        int zoneLevel = 127,
        int toneLevel = 127,
        int sampleRate = 32000,
        double attackMilliseconds = 0.0,
        int? rateKey = null)
    {
        var raw = partial.Raw;
        var partialLevel = PartialLevel(partial, velocity) ?? velocity;
        var baseLevel = BaseLevel(partial, partialLevel, key, zoneLevel, toneLevel);

        // Segment targets in the gain domain. A stage whose attenuation exceeds the base is exact
        // silence, not the amplitude table's floor -- the engine's gain word reads 0.000000 there.
        var targets = new double[SegmentEnvelope.SegmentCount];
        for (var i = 0; i < targets.Length; i++)
        {
            var level = baseLevel - _levelCurve[raw[0x5A + i]];
            targets[i] = level <= 0 ? 0.0 : AmpScale * AmpOf(level);
        }

        // Two key-follow tables, not one. The engine reads g_kf_tvarate0 for the four main segments
        // and g_kf_tvarate1 for the release -- see tva_compute_env_rates, where the two calls to
        // env_rate_scale index different bases with block[0x65] and block[0x66].
        //
        // Both index by voice+0x161, which is not the MIDI key on a drum part -- see rateKey.
        var index = rateKey ?? key;
        var mainRate = Envelope.RateScale((_rateKeyFollow0[(raw[0x65] * 0x80) + index] - 0x80) & 0xFF, raw[0x67]);
        var releaseRate = Envelope.RateScale((_rateKeyFollow1[(raw[0x66] * 0x80) + index] - 0x80) & 0xFF, raw[0x68]);

        // Two velocity level-scales, not one: segments 0-1 use 0x69, segments 2-3 and the release
        // use 0x6a. Sharing one makes the later segments descend far too steeply.
        var velocityEarly = Envelope.LevelScale(partialLevel, raw[0x69]);
        var velocityLate = Envelope.LevelScale(partialLevel, raw[0x6A]);

        var segmentSamples = new double[SegmentEnvelope.SegmentCount];
        var linear = new bool[SegmentEnvelope.SegmentCount];

        for (var i = 0; i < segmentSamples.Length; i++)
        {
            var seconds = Envelope.SegmentMilliseconds(
                raw[0x5E + i], mainRate, i < 2 ? velocityEarly : velocityLate) / 1000.0;

            if (i == 0 && attackMilliseconds > 0)
            {
                seconds = Math.Max(seconds, attackMilliseconds / 1000.0);
            }

            segmentSamples[i] = seconds * sampleRate;
            linear[i] = EnvelopeMachine.IsLinearSegment(raw[0x5E + i]);
        }

        var releaseMs = Envelope.SegmentMilliseconds(raw[0x62], releaseRate, velocityLate);

        return new SegmentEnvelope(
            Envelope, targets, segmentSamples, linear,
            releaseTarget: 0.0,
            releaseSamples: releaseMs / 1000.0 * sampleRate,
            releaseLinear: EnvelopeMachine.IsLinearSegment(raw[0x62]),
            afterRelease: 0.0,
            controlTickSamples: sampleRate / ControlTickHz);
    }

    /// <summary>
    /// Renders the amplitude envelope for one note.
    /// </summary>
    /// <param name="partial">The partial's parameter block.</param>
    /// <param name="velocity">MIDI velocity.</param>
    /// <param name="key">MIDI key.</param>
    /// <param name="holdSeconds">Time from note-on to note-off.</param>
    /// <param name="tailSeconds">Time to render past note-off.</param>
    /// <param name="zoneLevel">The multisample's per-key-zone level.</param>
    /// <param name="toneLevel">The tone header's master level.</param>
    /// <param name="sampleRate">Internal sample rate.</param>
    /// <param name="attackMilliseconds">
    /// Optional attack softening. Zero is faithful — the engine's amplitude attack is instant.
    /// </param>
    /// <returns>Per-sample gain, or an empty array when the note is not long enough to sound.</returns>
    /// <remarks>
    /// The gain is deliberately left smooth per sample rather than stepped per control tick. A
    /// stepwise model looks more faithful to a control-rate gain word, but it measures worse against
    /// the engine — consistent with the envelope block's own anti-zipper ramp interpolating across
    /// the block.
    /// </remarks>
    /// <param name="rateKey">
    /// Index for the two rate key-follow tables when it differs from <paramref name="key"/>. The
    /// engine keeps it at <c>voice+0x161</c>, which <c>voice_trigger_partials</c> fills from the note
    /// plus a transpose; on a drum part that works out to the kit's coarse-pitch plane, so it is not
    /// the MIDI key. Measured over the 61 sounding keys of the SC-55 standard kit, the plane equals
    /// the engine's index for all 61. <see langword="null"/> uses <paramref name="key"/>, which is
    /// what a melodic note wants.
    /// </param>
    public float[] Render(
        PartialParameters partial,
        int velocity,
        int key,
        double holdSeconds,
        double tailSeconds,
        int zoneLevel = 127,
        int toneLevel = 127,
        int sampleRate = 32000,
        double attackMilliseconds = 0.0,
        int? rateKey = null)
    {
        var sampleCount = (int)((holdSeconds + tailSeconds) * sampleRate);
        if (sampleCount <= 0)
        {
            return [];
        }

        var envelope = CreateEnvelope(
            partial, velocity, key, zoneLevel, toneLevel, sampleRate, attackMilliseconds, rateKey);

        envelope.NoteOff(Math.Min((int)(holdSeconds * sampleRate), sampleCount));

        var gain = new float[sampleCount];
        for (var n = 0; n < sampleCount; n++)
        {
            gain[n] = (float)envelope.ValueAt(n);
        }

        return gain;
    }

    /// <summary>
    /// The part-level volume multiplier, relative to everything at 127.
    /// </summary>
    /// <param name="volume">CC#7.</param>
    /// <param name="expression">CC#11.</param>
    /// <param name="master">Master volume.</param>
    /// <returns>The multiplier, 1.0 at the reference.</returns>
    /// <remarks>
    /// Volume and expression enter symmetrically as their product, and the result is squared. The
    /// intermediate exceeds 32 bits, so it is computed widened.
    /// </remarks>
    public static double PartVolumeScale(int volume = 127, int expression = 127, int master = 127)
    {
        static double Scale(int a, int b, int m)
        {
            var u = ((((b * a) & 0xFFFF) * m) >> 6) & 0xFFFF;
            var squared = ((long)u * 0x10410) >> 16;
            return (double)squared * squared;
        }

        var reference = Scale(127, 127, 127);
        return Scale(
            Math.Clamp(volume, 0, 127),
            Math.Clamp(expression, 0, 127),
            Math.Clamp(master, 0, 127)) / reference;
    }
}
