using TabulaSonora.Patches;
using TabulaSonora.Rom;

namespace TabulaSonora.Dsp;

/// <summary>The pitch envelope's decoded segment targets and timings.</summary>
/// <param name="Start">Starting offset, in milli-semitones.</param>
/// <param name="Targets">The four segment targets.</param>
/// <param name="Release">The release target.</param>
/// <param name="Times">Segment durations in milliseconds.</param>
/// <param name="ReleaseMs">Release duration in milliseconds.</param>
public readonly record struct PitchEnvelope(
    int Start,
    int[] Targets,
    int Release,
    double[] Times,
    double ReleaseMs);

/// <summary>
/// Walks a pitch envelope one control tick at a time.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the TVA and TVF envelopes, this one is not a function of elapsed time: a segment completes
/// when a 16-bit phase accumulator reaches 0xffff — not exceeds it — and the next segment starts with
/// a fresh phase rather than carrying the remainder, so the trajectory depends on the accumulator's
/// history. It therefore has to be stepped rather than evaluated.
/// </para>
/// <para>
/// The value is stepwise, held for the whole control block, and the value returned by
/// <see cref="Tick"/> is the state <em>after</em> that tick's update.
/// </para>
/// </remarks>
public sealed class PitchEnvelopeRunner
{
    private readonly int[] _targets;
    private readonly int[] _rates;
    private readonly int _release;
    private readonly int _releaseRate;
    private readonly bool _ignoreNoteOff;

    private double _segmentStart;
    private double _level;
    private int _segment;
    private int _phase;
    private bool _released;
    private int _releaseDamper;

    /// <summary>Creates a runner over a decoded envelope.</summary>
    /// <param name="envelope">The decoded targets and timings.</param>
    /// <param name="ignoreNoteOff">Whether the envelope runs through note-off (one-shot mode).</param>
    /// <remarks>
    /// One-shot mode is bit 7 of partial block byte 0x00: the engine's note-off handler skips
    /// engaging the pitch release while the envelope machine still runs. It is what the <c>.o</c>
    /// variation tones (Harpsi.o, Nylon Gt.o, Organ o, …) use.
    /// </remarks>
    public PitchEnvelopeRunner(PitchEnvelope envelope, bool ignoreNoteOff = false)
    {
        _targets = envelope.Targets;
        _release = envelope.Release;
        _releaseRate = PitchChain.SegmentRateWord(envelope.ReleaseMs);
        _ignoreNoteOff = ignoreNoteOff;

        _rates = new int[envelope.Times.Length];
        for (var i = 0; i < _rates.Length; i++)
        {
            _rates[i] = PitchChain.SegmentRateWord(envelope.Times[i]);
        }

        _segmentStart = envelope.Start;
        _level = envelope.Start;
    }

    private PitchEnvelopeRunner(double level)
    {
        _targets = [];
        _rates = [];
        _segmentStart = level;
        _level = level;
        _segment = int.MaxValue;
        _ignoreNoteOff = true;
    }

    /// <summary>
    /// A runner that holds one level forever.
    /// </summary>
    /// <param name="level">The offset in milli-semitones.</param>
    /// <returns>The runner.</returns>
    /// <remarks>
    /// A partial with random start jitter but a disabled envelope still gets the jitter: the engine
    /// writes the jittered start level and a zero rate word, so the level is never stepped — a
    /// per-note constant random detune.
    /// </remarks>
    public static PitchEnvelopeRunner Constant(double level) => new(level);

    /// <summary>The offset in milli-semitones after the last tick.</summary>
    public double Level => _level;

    /// <summary>
    /// Sets the half-damper pedal value the release will be scaled by.
    /// </summary>
    /// <param name="damper">CC64 value at release, 1–0x3f for a half-pressed pedal, else zero.</param>
    /// <remarks>
    /// The engine writes the pedal value into the release ramp's rate-scale byte; the effective
    /// rate is <c>rate × (0xffff − (v&lt;&lt;9)) &gt;&gt; 16</c>. Only meaningful before the
    /// release engages; the pitch-only 0x4000 shape word is unaffected.
    /// </remarks>
    public void SetReleaseDamper(int damper) => _releaseDamper = Math.Clamp(damper, 0, 0x3F);

    /// <summary>Advances one control tick.</summary>
    /// <param name="released">Whether the note has been released by this tick.</param>
    /// <returns>The offset in milli-semitones.</returns>
    public double Tick(bool released)
    {
        double target;
        int rate;

        if (released && !_released && !_ignoreNoteOff)
        {
            _released = true;
            _segmentStart = _level;
            _segment = -1;
            _phase = 0;
            target = _release;
            rate = ScaledReleaseRate();
        }
        else if (_segment < 0)
        {
            target = _release;
            rate = ScaledReleaseRate();
        }
        else if (_segment < _rates.Length)
        {
            target = _targets[_segment];
            rate = _rates[_segment];
        }
        else
        {
            return _level;
        }

        _phase += rate;
        if (_phase >= 0xFFFF)
        {
            _level = target;
            _segmentStart = target;
            _phase = 0;
            if (_segment >= 0)
            {
                _segment++;
            }
        }
        else
        {
            _level = _segmentStart + ((target - _segmentStart) * (_phase / 65536.0));
        }

        return _level;
    }

    private int ScaledReleaseRate() =>
        _releaseDamper == 0 ? _releaseRate : (0xFFFF - (_releaseDamper << 9)) * _releaseRate >> 16;
}

/// <summary>
/// The pitch side of a voice: key-follow, transposition, the pitch envelope, and bend.
/// </summary>
/// <remarks>
/// <para>
/// Pitch is absolute, in milli-semitones, and the engine clamps the accumulator to
/// <c>[0, 0x1f018]</c> — 127 semitones × 1000, which is what fixes the unit. Working in offsets from
/// a base cannot express that clamp, and at least one patch sits exactly on it: Jetplane's first
/// partial has a base of 24000 with an envelope starting at −24000, landing on zero.
/// </para>
/// </remarks>
public sealed class PitchChain
{
    /// <summary>Upper clamp of the pitch accumulator: 127 semitones in milli-semitones.</summary>
    public const int MaxPitchMilliSemitones = 0x1F018;

    /// <summary>The key a drum's coarse-pitch plane pivots around: 60 is its natural pitch.</summary>
    public const int DrumKeyCentre = 60;

    /// <summary>The key-follow scale lookup, giving a clean 0% to 100% ladder in steps of 10%.</summary>
    private static readonly int[] KeyFollowScale =
        [0, 3276, 6553, 9830, 13107, 16383, 19660, 22937, 26214, 29491, 32767];

    private readonly EnvelopeMachine _envelope;
    private readonly EngineNoise _noise;
    private readonly ushort[] _portamentoStep;
    private readonly byte[] _pitchBias;
    private readonly ushort[] _depthVelocitySensitivity;
    private readonly byte[] _rateKeyFollow0;
    private readonly byte[] _rateKeyFollow1;
    private readonly short[] _keyFollow;
    private readonly short[] _depthSlope;
    private readonly ushort[] _rateCurve;

    /// <summary>Creates the chain over a loaded table set.</summary>
    /// <param name="tables">The static tables.</param>
    /// <param name="envelope">The shared segment machine.</param>
    /// <param name="noise">
    /// The engine's shared generator; one is created here when the caller has none, which keeps a
    /// standalone chain usable but gives it a private draw sequence.
    /// </param>
    public PitchChain(TableSet tables, EnvelopeMachine envelope, EngineNoise? noise = null)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(envelope);

        _envelope = envelope;
        _noise = noise ?? new EngineNoise();
        _pitchBias = tables.PitchBias;
        _depthVelocitySensitivity = tables.PitchDepthVs;
        _rateKeyFollow0 = tables.KfPitchRate0;
        _rateKeyFollow1 = tables.KfPitchRate1;
        _keyFollow = tables.KfPitch;
        _rateCurve = tables.RateCurve;
        _portamentoStep = tables.PortamentoStep;

        // The depth velocity-sensitivity slope is a signed-16 view of the first 0x80 entries of the
        // pitch-envelope export.
        _depthSlope = new short[0x80];
        for (var i = 0; i < _depthSlope.Length; i++)
        {
            _depthSlope[i] = (short)(tables.PitchEnv[i * 2] | (tables.PitchEnv[(i * 2) + 1] << 8));
        }
    }

    /// <summary>
    /// The effective pitch key and its fractional crossfade weight.
    /// </summary>
    /// <param name="partial">The partial's parameter block.</param>
    /// <param name="note">MIDI note.</param>
    /// <param name="keyCenter">The multisample's key centre.</param>
    /// <returns>The effective key and the crossfade weight.</returns>
    /// <remarks>
    /// <c>block[0x13]</c> is the follow amount: 0x40 is no follow (the key centre sounds regardless of
    /// the note), 0x4a is full follow, and values between scale the note's distance from the centre.
    /// A reduced-follow patch like the Seashore effect plays note 33 as key 55, which keeps its noise
    /// bright instead of an octave-too-low rumble.
    /// </remarks>
    public static (int Key, int Weight) KeyFollowKey(PartialParameters partial, int note, int keyCenter = 0x3C)
    {
        var raw = partial.Raw;
        var transpose = raw[0x10] - 0x40;
        var amount = raw[0x13] - 0x40;

        if (amount == 10 || amount < 0)
        {
            return (note + transpose, 0);
        }

        if (amount == 0)
        {
            return (keyCenter + transpose, 0);
        }

        var distance = note - keyCenter;
        var product = KeyFollowScale[Math.Min(amount, 10)] * (Math.Abs(distance) * 2);
        var high = product >> 16;
        var low = product & 0xFFFF;

        int weight;
        int keyStep;
        if (low < 65000)
        {
            var scaled = (low * 999) / 65000;
            if (distance >= 0)
            {
                weight = scaled;
                keyStep = high;
            }
            else
            {
                weight = 1000 - scaled;
                keyStep = ~high;
            }
        }
        else
        {
            weight = 0;
            keyStep = distance >= 0 ? high + 1 : ~high;
        }

        return (keyCenter + keyStep + transpose, weight);
    }

    /// <summary>
    /// The note's absolute base pitch in milli-semitones.
    /// </summary>
    /// <param name="partial">The partial's parameter block.</param>
    /// <param name="note">MIDI note.</param>
    /// <param name="keyCenter">The multisample's key centre.</param>
    /// <returns>The base pitch.</returns>
    public int BasePitchMilliSemitones(PartialParameters partial, int note, int keyCenter = 0x3C)
    {
        var raw = partial.Raw;
        var (key, weight) = KeyFollowKey(partial, note, keyCenter);
        key = Math.Clamp(key, 0, 0x7F);

        var row = Math.Clamp((raw[0x13] - 0x40) >> 2, 0, 7);
        return (key * 1000) + weight + _keyFollow[(row * 0x80) + key] + ((raw[0x11] - 0x40) * 10);
    }

    /// <summary>
    /// A drum key's absolute pitch in milli-semitones, from its coarse-pitch plane value.
    /// </summary>
    /// <param name="partial">The partial's parameter block.</param>
    /// <param name="coarsePitch">The kit's coarse-pitch byte for this key, NRPN offset included.</param>
    /// <returns>The pitch.</returns>
    /// <remarks>
    /// <para>
    /// A drum note does not transpose its sample; the coarse-pitch plane supplies the key instead,
    /// pivoting on <see cref="DrumKeyCentre"/>. What a step of that plane is <em>worth</em> is then
    /// the tone's own business: it runs through the same <c>block[0x13]</c> key-follow ladder as a
    /// melodic note, so a 100%-follow tone moves a whole semitone per unit and a 50%-follow tone
    /// half of one. Modelling it as a fixed half-semitone is right only for the latter — see the
    /// FINDINGS entry on the drum-pitch NRPN, where the SC-55 kits and the Electronic family come
    /// out an octave short under that reading.
    /// </para>
    /// <para>
    /// Unlike the melodic chain this omits the <c>g_kf_pitch</c> key-follow curve: the engine gates
    /// that term off for drums, and the measured pitches are exact multiples of 1000 where the
    /// curve would have contributed −11 or +3.
    /// </para>
    /// </remarks>
    public static int DrumPitchMilliSemitones(PartialParameters partial, int coarsePitch)
    {
        var (key, weight) = KeyFollowKey(partial, coarsePitch, DrumKeyCentre);
        key = Math.Clamp(key, 0, 0x7F);
        return (key * 1000) + weight + ((partial.Raw[0x11] - 0x40) * 10);
    }

    /// <summary>
    /// The portamento glide step for a time byte, in milli-semitones per control tick.
    /// </summary>
    /// <param name="time">CC#5 portamento time.</param>
    /// <returns>The step; zero never arrives, so a step of at least one always finishes.</returns>
    public int PortamentoStep(int time) => _portamentoStep[Math.Clamp(time, 0, 0x7F)];

    /// <summary>
    /// The glide offset a note starts with, in milli-semitones.
    /// </summary>
    /// <param name="fromKey">The key being glided from.</param>
    /// <param name="targetPitch">The note's own absolute pitch.</param>
    /// <returns>The offset, which decays to zero as the glide completes.</returns>
    /// <remarks>
    /// The engine takes the source key at a flat <c>key × 1000</c> rather than running it back
    /// through the key-follow chain, so a glide always departs from equal temperament and lands on
    /// whatever the destination partial's own chain produces.
    /// </remarks>
    public static int PortamentoOffset(int fromKey, int targetPitch) =>
        (Math.Clamp(fromKey, 0, 0x7F) * 1000) - targetPitch;

    /// <summary>Pitch-bend offset in milli-semitones.</summary>
    /// <param name="bend">14-bit bend value, 8192 centre.</param>
    /// <param name="semitoneRange">Bend range from RPN 00/00; the GM default is 2.</param>
    /// <returns>The offset.</returns>
    public static double BendOffsetMilliSemitones(int bend = 8192, double semitoneRange = 2.0) =>
        (bend - 8192) / 8192.0 * semitoneRange * 1000.0;

    /// <summary>
    /// Decodes the pitch envelope's targets and timings.
    /// </summary>
    /// <param name="partial">The partial's parameter block.</param>
    /// <param name="key">MIDI key.</param>
    /// <param name="velocity">MIDI velocity.</param>
    /// <returns>The envelope, or <see langword="null"/> when it is disabled.</returns>
    public PitchEnvelope? EnvelopeOffsets(PartialParameters partial, int key, int velocity)
    {
        var raw = partial.Raw;
        var depth = raw[0x18] | (raw[0x19] << 8);
        if (depth == 0)
        {
            return null;
        }

        var sensitivity = raw[0x2B] - 0x40;
        int scaledDepth;
        if (sensitivity == 0)
        {
            scaledDepth = depth;
        }
        else
        {
            var v = Math.Clamp(velocity, 0, 127);
            if (sensitivity < 0)
            {
                sensitivity = -sensitivity;
                v = (-v) & 0x7F;
            }

            scaledDepth = (((_depthSlope[sensitivity] * v) + _depthVelocitySensitivity[sensitivity]) * depth
                + 0x8000) >> 16;
        }

        int Delta(int bias)
        {
            if (bias == 0)
            {
                return 0;
            }

            var d = (_pitchBias[Math.Min(64, Math.Abs(bias))] * scaledDepth) >> 7;
            return bias < 0 ? -d : d;
        }

        var biases = new int[5];
        for (var i = 0; i < 5; i++)
        {
            biases[i] = (sbyte)raw[0x1B + i] - 0x40;
        }

        var mainRate = _envelope.RateScale(
            (_rateKeyFollow0[(raw[0x27] * 0x80) + (key & 0x7F)] - 0x80) & 0xFF, raw[0x29]);
        var releaseRate = _envelope.RateScale(
            (_rateKeyFollow1[(raw[0x27] * 0x80) + (key & 0x7F)] - 0x80) & 0xFF, raw[0x2A]);
        var velocityScale = _envelope.LevelScale(Math.Clamp(velocity, 0, 127), raw[0x2C]);

        double Time(int rateByte, int rateMultiplier)
        {
            var r = rateByte & 0x7F;
            int ticks = _rateCurve[r];
            return r == 0 || ticks < EnvelopeMachine.MinimumSegmentTicks
                ? 0
                : ((((rateMultiplier * ticks) >> 8) & 0xFFFF) * velocityScale) >> 8;
        }

        var times = new double[4];
        for (var i = 0; i < 4; i++)
        {
            times[i] = Time(raw[0x20 + i], mainRate);
        }

        // The fourth target is always zero: the envelope returns to the base pitch.
        var targets = new[] { Delta(biases[1]), Delta(biases[2]), Delta(biases[3]), 0 };

        return new PitchEnvelope(
            Delta(biases[0]), targets, Delta(biases[4]), times, Time(raw[0x24], releaseRate));
    }

    /// <summary>
    /// The segment machine's rate word for a duration.
    /// </summary>
    /// <param name="milliseconds">Segment duration.</param>
    /// <returns>The rate word.</returns>
    /// <remarks>
    /// 0xffff wraps the 16-bit phase in exactly one control tick, so even a zero-length segment still
    /// occupies one tick.
    /// </remarks>
    public static int SegmentRateWord(double milliseconds) =>
        milliseconds < 11 ? 0xFFFF : Math.Min(0xFFFF, 0xA0000 / (int)milliseconds);

    /// <summary>
    /// The pitch envelope's offset at each control tick, in milli-semitones.
    /// </summary>
    /// <param name="partial">The partial's parameter block.</param>
    /// <param name="key">MIDI key.</param>
    /// <param name="velocity">MIDI velocity.</param>
    /// <param name="holdSeconds">Time from note-on to note-off.</param>
    /// <param name="tickCount">How many control ticks to produce.</param>
    /// <returns>The offsets, or <see langword="null"/> when the envelope is inactive.</returns>
    /// <remarks>
    /// Tick <c>i</c> is the state <em>after</em> that tick's update; see
    /// <see cref="PitchEnvelopeRunner"/> for why the envelope has to be stepped rather than evaluated.
    /// </remarks>
    public double[]? EnvelopeTicks(
        PartialParameters partial,
        int key,
        int velocity,
        double holdSeconds,
        int tickCount)
    {
        var runner = CreateEnvelopeRunner(partial, key, velocity);
        if (runner is null)
        {
            return null;
        }

        var output = new double[Math.Max(0, tickCount)];
        var noteOffTick = (int)(holdSeconds * 100);

        for (var i = 0; i < output.Length; i++)
        {
            output[i] = runner.Tick(i >= noteOffTick);
        }

        return output;
    }

    /// <summary>
    /// The random start-pitch jitter for one note-on, in milli-semitones.
    /// </summary>
    /// <param name="depth">Partial block byte 0x1a.</param>
    /// <param name="draw">One draw from the engine's noise generator.</param>
    /// <returns>The offset, roughly −10 to +5 × <paramref name="depth"/> milli-semitones.</returns>
    /// <remarks>
    /// Bit 14 of the draw picks the sign. The magnitude slice is asymmetric — 7 bits on the
    /// positive side, 8 on the negative — so the range is about [−10·d, +5·d], and that asymmetry
    /// is the hardware's, not an artefact. The engine applies this to the envelope's start level at
    /// every note-on, even when the envelope itself is disabled. 19 partials carry a non-zero depth
    /// (5 or 10 — the "analog feel" detune on layered bass/brass patches).
    /// </remarks>
    public static int StartJitterMilliSemitones(int depth, ushort draw)
    {
        if ((short)(draw << 1) >= 0)
        {
            return ((((draw & 0x7FFF) >> 7) * depth) + 0x80 >> 8) * 10;
        }

        return ((((ushort)(draw * -2) >> 8) * depth) + 0x80 >> 8) * -10;
    }

    /// <summary>
    /// Creates a runner for a partial's pitch envelope.
    /// </summary>
    /// <param name="partial">The partial's parameter block.</param>
    /// <param name="key">MIDI key.</param>
    /// <param name="velocity">MIDI velocity.</param>
    /// <returns>The runner, or <see langword="null"/> when the envelope does nothing.</returns>
    /// <remarks>
    /// <para>
    /// An envelope whose start, four targets and release are all zero is not merely flat — it is
    /// absent, and skipping it saves the pitch chain a per-tick update on most patches.
    /// </para>
    /// <para>
    /// A non-zero block byte 0x1a draws start jitter here, one draw per partial voice, matching the
    /// engine (which skips the draw entirely when the byte is zero, so unaffected patches consume
    /// nothing from the generator). The engine clamps the jittered <em>absolute</em> start level at
    /// zero; this model relies on the downstream pitch clamp instead, which only differs when
    /// base + start is already near zero — no jittered partial is (their bases sit tens of
    /// semitones above the floor and the jitter is at most ±100 milli-semitones).
    /// </para>
    /// </remarks>
    public PitchEnvelopeRunner? CreateEnvelopeRunner(PartialParameters partial, int key, int velocity)
    {
        var raw = partial.Raw;
        var jitterDepth = raw[0x1A];
        var jitter = jitterDepth != 0 ? StartJitterMilliSemitones(jitterDepth, _noise.Next()) : 0;
        var oneShot = (raw[0x00] & 0x80) != 0;

        var envelope = EnvelopeOffsets(partial, key, velocity);
        if (envelope is null)
        {
            return jitter == 0 ? null : PitchEnvelopeRunner.Constant(jitter);
        }

        if (jitter != 0)
        {
            envelope = envelope.Value with { Start = envelope.Value.Start + jitter };
        }

        var (start, targets, release, _, _) = envelope.Value;
        return start == 0 && Array.TrueForAll(targets, t => t == 0) && release == 0
            ? null
            : new PitchEnvelopeRunner(envelope.Value, oneShot);
    }

    /// <summary>
    /// The playback ratio for a partial, from its absolute pitch against the sample's own root.
    /// </summary>
    /// <param name="partial">The partial's parameter block.</param>
    /// <param name="descriptor">The selected wave's descriptor.</param>
    /// <param name="pitchMilliSemitones">Absolute pitch, already summed and clamped.</param>
    /// <returns>The frequency ratio.</returns>
    public static double Ratio(PartialParameters partial, WaveDescriptor descriptor, double pitchMilliSemitones)
    {
        var native = (descriptor.RootKey * 1000.0) + 1024.0 - descriptor.FineTune;
        return Math.Pow(2.0, (pitchMilliSemitones - native) / 12000.0);
    }

    /// <summary>Clamps an absolute pitch the way the engine's accumulator does.</summary>
    /// <param name="milliSemitones">The unclamped pitch.</param>
    /// <returns>The clamped pitch.</returns>
    public static double Clamp(double milliSemitones) =>
        Math.Clamp(milliSemitones, 0, MaxPitchMilliSemitones);
}
