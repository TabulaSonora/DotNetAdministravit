using TabulaSonora.Dsp;
using TabulaSonora.Patches;
using TabulaSonora.Rom;

namespace TabulaSonora;

/// <summary>One rendered note: stereo output plus the pre-pan mono the send buses are fed from.</summary>
/// <param name="Left">Left channel.</param>
/// <param name="Right">Right channel.</param>
/// <param name="Mono">Pre-pan sum of the partials — the pan-independent send source.</param>
/// <param name="Name">The tone's name.</param>
public sealed record RenderedNote(float[] Left, float[] Right, float[] Mono, string Name);

/// <summary>
/// Renders one note by assembling the whole voice chain: sampler, pitch, filter, amplitude and pan.
/// </summary>
/// <remarks>
/// <para>
/// Partials <em>sum</em>. Each is an independent voice dispatched into the same accumulation buffer,
/// and there is no divide-by-count anywhere in the mix path — averaging would silently halve every
/// two-partial patch relative to a one-partial one.
/// </para>
/// <para>
/// A note's timbre is latched at note-on, but volume and pitch keep tracking their controllers,
/// because the engine re-reads those each block. That is why they arrive here as optional per-sample
/// curves rather than scalars.
/// </para>
/// </remarks>
public sealed class NoteRenderer
{
    /// <summary>Samples per control tick.</summary>
    public const int ControlBlock = 320;

    /// <summary>Internal sample rate.</summary>
    public const int SampleRate = 32000;

    private readonly PatchDirectory _directory;
    private readonly DrumKitTable _drums;
    private readonly Interpolator _interpolator;
    private readonly Sampler _sampler;
    private readonly EnvelopeMachine _envelopes;
    private readonly TvaChain _tva;
    private readonly TvfChain _tvf;
    private readonly PitchChain _pitch;
    private readonly LfoEngine _lfo;
    private readonly PanLaw _pan;

    /// <summary>Creates a note renderer over an open ROM image.</summary>
    /// <param name="rom">An open, verified ROM image.</param>
    /// <remarks>
    /// The image must stay open for as long as this renderer is used. Tables are read once here, but
    /// wave data is read on demand, so disposing the image early fails at the first note rather than
    /// at construction.
    /// </remarks>
    public NoteRenderer(RomImage rom)
    {
        ArgumentNullException.ThrowIfNull(rom);

        var tables = TableSet.FromRom(rom);
        var envelope = new EnvelopeMachine(tables);

        _envelopes = envelope;
        Tables = tables;
        _directory = new PatchDirectory(tables);
        _drums = new DrumKitTable(rom);
        _interpolator = new Interpolator(tables);
        _sampler = new Sampler(new WaveRom(rom), _interpolator);
        _tva = new TvaChain(tables, envelope);
        _tvf = new TvfChain(tables, envelope);
        _pitch = new PitchChain(tables, envelope);
        _lfo = new LfoEngine(tables);
        _pan = new PanLaw(tables);
    }

    /// <summary>The static tables this renderer loaded.</summary>
    public TableSet Tables { get; }

    /// <summary>The patch directory.</summary>
    public PatchDirectory Directory => _directory;

    /// <summary>The drum kits.</summary>
    public DrumKitTable Drums => _drums;

    /// <summary>The 4-tap resampler.</summary>
    /// <remarks>
    /// The chains below are exposed so a host can assemble the voice itself — which is what the
    /// real-time <see cref="Realtime.ToneGenerator"/> does, sharing one loaded set of tables and one
    /// wave cache with this renderer rather than duplicating either.
    /// </remarks>
    public Interpolator Interpolator => _interpolator;

    /// <summary>The wave decoder and player.</summary>
    public Sampler Sampler => _sampler;

    /// <summary>The shared segment-rate machine, which also decodes the envelope hold clock.</summary>
    public EnvelopeMachine Envelopes => _envelopes;

    /// <summary>The amplitude chain.</summary>
    public TvaChain Tva => _tva;

    /// <summary>The filter chain.</summary>
    public TvfChain Tvf => _tvf;

    /// <summary>The pitch chain.</summary>
    public PitchChain Pitch => _pitch;

    /// <summary>The two LFO engines.</summary>
    public LfoEngine Lfo => _lfo;

    /// <summary>The pan law.</summary>
    public PanLaw Pan => _pan;

    /// <summary>Renders one melodic note.</summary>
    /// <param name="program">Program number.</param>
    /// <param name="note">MIDI note.</param>
    /// <param name="velocity">MIDI velocity.</param>
    /// <param name="holdSeconds">Time from note-on to note-off.</param>
    /// <param name="tailSeconds">Time to render past note-off.</param>
    /// <param name="map">Tone map.</param>
    /// <param name="bank">Bank select MSB.</param>
    /// <param name="partPan">CC#10 at note-on.</param>
    /// <param name="volumeCurve">Per-sample part volume, or <see langword="null"/> for unity.</param>
    /// <param name="pitchAddCurve">Per-sample bend and tune in milli-semitones, or <see langword="null"/>.</param>
    /// <param name="modWheelPerTick">
    /// Mod-wheel depth per control tick in milli-semitones, or <see langword="null"/> when the wheel
    /// is untouched. This adds vibrato depth to LFO1 while the note sounds.
    /// </param>
    /// <returns>The rendered note.</returns>
    public RenderedNote RenderNote(
        int program,
        int note,
        int velocity,
        double holdSeconds,
        double tailSeconds,
        ToneMap map = ToneMap.Sc8820,
        int bank = 0,
        int partPan = 0x40,
        double[]? volumeCurve = null,
        double[]? pitchAddCurve = null,
        double[]? modWheelPerTick = null)
    {
        var sampleCount = (int)((holdSeconds + tailSeconds) * SampleRate);
        var tones = _directory.ProgramTones(program, map, bank);
        if (tones.Count == 0 || sampleCount <= 0)
        {
            return new RenderedNote(new float[Math.Max(0, sampleCount)],
                new float[Math.Max(0, sampleCount)], new float[Math.Max(0, sampleCount)], "(unassigned)");
        }

        var left = new float[sampleCount];
        var right = new float[sampleCount];
        var mono = new float[sampleCount];
        var name = "(none)";

        foreach (var toneNumber in tones)
        {
            var resolved = _directory.Resolve(toneNumber, note, velocity);
            name = resolved.Name.TrimEnd(':', ' ');
            var tone = _directory.GetTone(toneNumber)!;

            foreach (var sounding in resolved.Partials)
            {
                var descriptor = sounding.Descriptor;
                var partial = tone.Partials[sounding.PartialIndex];
                var signal = RenderPartial(
                    toneNumber, partial, descriptor, note, velocity,
                    holdSeconds, tailSeconds, sampleCount, pitchAddCurve,
                    modWheelPerTick: modWheelPerTick);

                if (signal is null)
                {
                    continue;
                }

                var (gainLeft, gainRight) = _pan.Gains(partial.Pan + (partPan - 0x40));
                Simd.MixScaled(signal, gainLeft, left);
                Simd.MixScaled(signal, gainRight, right);
                Simd.Add(signal, mono);
            }
        }

        ApplyVolume(left, right, mono, volumeCurve);
        return new RenderedNote(left, right, mono, name);
    }

    /// <summary>The key a drum hit's envelope rates are key-followed by.</summary>
    /// <param name="key">The drum key, as the kit stores it.</param>
    /// <param name="drumPitch">Coarse-pitch override from NRPN <c>0x18</c>, in semitone steps.</param>
    /// <returns>The index for the rate key-follow tables.</returns>
    /// <remarks>
    /// <para>
    /// Not 60, and not the MIDI note. The engine keeps this at <c>voice+0x161</c>, filled by
    /// <c>voice_trigger_partials</c> as the note plus a transpose, and on a drum part the sum works
    /// out to the kit's own coarse-pitch plane. Read back off the DLL for the 61 sounding keys of the
    /// SC-55 standard kit, the plane equals the engine's index for all 61.
    /// </para>
    /// <para>
    /// The NRPN moves it a whole semitone per step, where the same step moves the <em>pitch</em> plane
    /// by two — the plane runs at 50 cents a step and this index does not. That is why the offset is
    /// added here rather than reading the already-modified plane back.
    /// </para>
    /// </remarks>
    public static int EnvelopeRateKey(DrumKey key, int drumPitch) =>
        Math.Clamp(key.Pitch + drumPitch, 0, 0x7F);

    /// <summary>Largest factor the drum ring may be stretched by.</summary>
    /// <remarks>
    /// A bound on the render window, not on the sound. The deepest a file can send is −64 semitones,
    /// which would want 40×; capping at 16 keeps a single hit's buffers to a few tens of megabytes
    /// while still covering −48 semitones, past anything a General MIDI kit reaches.
    /// </remarks>
    public const double MaxDrumRingScale = 16.0;

    /// <summary>How much longer a hit needs than the nominal ring, given its coarse pitch.</summary>
    /// <param name="key">The drum key, after any NRPN override.</param>
    /// <returns>A factor of one or more.</returns>
    /// <remarks>
    /// The ring exists because a drum ignores note-off, so the renderer has to decide for itself how
    /// long to run. A fixed window is wrong once a key is pitched down: the sample plays proportionally
    /// slower, so the hit outlasts it and is cut off mid-decay. Measured on the real DLL, the splash at
    /// NRPN 18h = 24 takes 4.68 s to fall 40 dB against 1.15 s untouched — four times the nominal
    /// 1.8 s ring. Scaling by the inverse pitch ratio covers that with room to spare; the envelope,
    /// not this window, is what should end the note.
    /// </remarks>
    public static double DrumRingScale(DrumKey key)
    {
        var ratio = DrumKitTable.CoarsePitchRatio(key.Pitch);
        return ratio <= 0 ? 1.0 : Math.Clamp(1.0 / ratio, 1.0, MaxDrumRingScale);
    }

    /// <summary>How much longer a hit needs than the nominal ring, before it is rendered.</summary>
    /// <param name="note">MIDI note, which selects the kit entry.</param>
    /// <param name="kit">Kit record index.</param>
    /// <param name="drumPitch">Coarse-pitch override from NRPN <c>0x18</c>, in semitone steps.</param>
    /// <returns>A factor of one or more.</returns>
    /// <remarks>Lets a caller size its own buffers to the same window the render will use.</remarks>
    public double DrumRingScale(int note, int kit, int drumPitch) =>
        DrumRingScale(DrumKeyOverrides.Apply(_drums.Key(note, kit), drumPitch, null));

    /// <summary>Renders one drum hit.</summary>
    /// <param name="note">MIDI note, which selects the kit entry rather than the pitch.</param>
    /// <param name="velocity">MIDI velocity.</param>
    /// <param name="ringSeconds">How long the hit rings; a drum ignores note-off.</param>
    /// <param name="tailSeconds">Extra time past the ring.</param>
    /// <param name="kit">Kit record index.</param>
    /// <param name="volumeCurve">Per-sample part volume, or <see langword="null"/> for unity.</param>
    /// <param name="drumPitch">
    /// Coarse-pitch override from NRPN <c>0x18</c> in steps of one semitone; zero leaves the kit's
    /// own value alone.
    /// </param>
    /// <param name="drumPan">
    /// Panpot override from NRPN <c>0x1C</c>, or <see langword="null"/> to keep the kit's own.
    /// </param>
    /// <returns>The rendered hit.</returns>
    /// <remarks>
    /// The note does not transpose the sample: the tone resolves at key 60 and the kit's coarse-pitch
    /// plane supplies an offset at half strength. The kit level is not part of the voice gain — it
    /// enters downstream, squared.
    /// </remarks>
    public RenderedNote RenderDrumNote(
        int note,
        int velocity,
        double ringSeconds,
        double tailSeconds = 0.4,
        int kit = 0,
        double[]? volumeCurve = null,
        int drumPitch = 0,
        int? drumPan = null)
    {
        // The kit's own key is kept alongside the overridden one: the envelope rate key-follow is
        // indexed from the stored plane plus the NRPN offset in semitones, not from the plane after
        // the override has doubled that offset into it.
        var kitKey = _drums.Key(note, kit);
        var key = DrumKeyOverrides.Apply(kitKey, drumPitch, drumPan);

        // The ring stretches with the coarse pitch, and the hold has to stretch with it: the hold is
        // where note-off lands, and both the TVA and TVF envelopes are sized from it. Leaving it at
        // the nominal ring builds envelopes shorter than the signal they gate.
        var ring = ringSeconds * DrumRingScale(key);
        var sampleCount = (int)((ring + tailSeconds) * SampleRate);
        var resolved = _directory.Resolve(key.Tone, note: 60, velocity);

        var left = new float[Math.Max(0, sampleCount)];
        var right = new float[Math.Max(0, sampleCount)];
        var mono = new float[Math.Max(0, sampleCount)];

        if (sampleCount <= 0 || resolved.Partials.Count == 0)
        {
            return new RenderedNote(left, right, mono, resolved.Name);
        }

        var tone = _directory.GetTone(key.Tone)!;
        var coarse = DrumKitTable.CoarsePitchRatio(key.Pitch);

        foreach (var sounding in resolved.Partials)
        {
            var descriptor = sounding.Descriptor;
            var partial = tone.Partials[sounding.PartialIndex];
            var signal = RenderPartial(
                key.Tone, partial, descriptor, note: 60, velocity,
                ring, tailSeconds, sampleCount, pitchAddCurve: null,
                drumCoarseRatio: coarse,
                envelopeRateKey: EnvelopeRateKey(kitKey, drumPitch));

            if (signal is null)
            {
                continue;
            }

            Simd.Add(signal, mono);
        }

        // The kit level acts as (level/127) squared, and pan is per drum key.
        var levelGain = DrumKitTable.LevelGain(key.Level);
        var (gainLeft, gainRight) = _pan.Gains(key.Pan);

        // Pan reads the levelled mono back after it has been narrowed to float, as the scalar loop
        // did -- the kit level is not a second factor folded into the pan gain.
        Simd.Scale(mono, levelGain);
        Simd.StoreScaled(mono, gainLeft, left);
        Simd.StoreScaled(mono, gainRight, right);

        ApplyVolume(left, right, mono, volumeCurve);
        return new RenderedNote(left, right, mono, resolved.Name);
    }

    private float[]? RenderPartial(
        int toneNumber,
        PartialParameters partial,
        WaveDescriptor descriptor,
        int note,
        int velocity,
        double holdSeconds,
        double tailSeconds,
        int sampleCount,
        double[]? pitchAddCurve,
        double? drumCoarseRatio = null,
        double[]? modWheelPerTick = null,
        int? envelopeRateKey = null)
    {
        var wave = _sampler.Decode(descriptor);
        if (wave is null)
        {
            return null;
        }

        var key = Math.Clamp(note, 0, 0x7F);
        var tickCount = (int)Math.Ceiling(sampleCount / (double)ControlBlock) + 1;

        // The hold clock: a delayed layer's envelopes and LFOs all start late, and a one-shot's
        // never start at all — every control value stays where the note-on compute left it. The
        // sample is NOT delayed; it runs from note-on under the frozen values.
        var hold = _envelopes.HoldSamples(partial, velocity);
        var suspended = hold == EnvelopeMachine.HoldForever;
        var holdSamples = suspended ? 0L : hold;
        if (holdSamples > 0 && holdSeconds * SampleRate <= holdSamples)
        {
            // Note-off landed while the delay was still running: the engine kills the armed voice
            // before it has ever sounded.
            return null;
        }

        var holdTicks = (int)(holdSamples / ControlBlock);
        var envelopeHoldSeconds = holdSeconds - (holdSamples / (double)SampleRate);

        var envelope = PitchEnvelopeTicksHeld(partial, key, velocity, holdSeconds, tickCount, holdTicks, suspended);

        // The mod wheel adds vibrato depth to LFO1 only, and only while it is actually moved.
        var lfoPitch = suspended
            ? null
            : ShiftTicks(modWheelPerTick is null
                ? _lfo.Modulation(toneNumber, partial, tickCount, LfoDestination.Pitch)
                : _lfo.PitchModulationWithWheel(toneNumber, partial, tickCount, modWheelPerTick), holdTicks);

        var pitchEnvelope = envelope is null ? null : Expand(envelope, sampleCount);
        var pitchLfo = lfoPitch is not null && AnyNonZero(lfoPitch) ? Expand(lfoPitch, sampleCount) : null;

        var ratios = new double[sampleCount];

        if (drumCoarseRatio is { } coarse)
        {
            // Drums take a different pitch route. The note does not transpose the sample, so there
            // is no key-follow and no absolute-pitch accumulator to clamp: the tone sounds at its
            // own root, trimmed by the partial's transpose and coarse tune, and the kit's
            // coarse-pitch plane scales it.
            var raw = partial.Raw;
            var nativeSemitones = descriptor.RootKey
                + ((1024.0 - descriptor.FineTune) / 1000.0)
                + (0x40 - raw[0x10])
                - ((raw[0x11] - 0x40) / 100.0);

            var baseRatio = Math.Pow(2.0, (60 - nativeSemitones) / 12.0) * coarse;

            // Both contributions are control-rate -- see the memo note in the melodic branch below.
            var lastModulation = double.NaN;
            var lastRatio = 0.0;

            for (var i = 0; i < sampleCount; i++)
            {
                var modulation = 0.0;
                if (pitchEnvelope is not null)
                {
                    modulation += pitchEnvelope[i];
                }

                if (pitchLfo is not null)
                {
                    modulation += pitchLfo[i];
                }

                if (modulation != lastModulation)
                {
                    lastRatio = baseRatio * Math.Pow(2.0, modulation / 12000.0);
                    lastModulation = modulation;
                }

                ratios[i] = lastRatio;
            }
        }
        else
        {
            // Melodic: an absolute pitch in milli-semitones, clamped as the engine's accumulator is,
            // then taken as a ratio against the sample's own root.
            var basePitch = (double)_pitch.BasePitchMilliSemitones(partial, note, partial.KeyCenter);
            var nativePitch = (descriptor.RootKey * 1000.0) + 1024.0 - descriptor.FineTune;

            // Memoised on the exact input, the same way BuildVolumeCurve memoises PartVolumeScale.
            // Both modulation terms come from Expand, so they hold one value across each 320-sample
            // control block; only bend moves per sample, and it is null on a note that never bends.
            // A note without bend therefore takes one Math.Pow per control block rather than 320.
            // Reusing the result only when the input is bit-identical keeps this exact.
            var lastPitch = double.NaN;
            var lastRatio = 0.0;

            for (var i = 0; i < sampleCount; i++)
            {
                var pitch = basePitch;
                if (pitchEnvelope is not null)
                {
                    pitch += pitchEnvelope[i];
                }

                if (pitchLfo is not null)
                {
                    pitch += pitchLfo[i];
                }

                if (pitchAddCurve is not null)
                {
                    pitch += pitchAddCurve[Math.Min(i, pitchAddCurve.Length - 1)];
                }

                if (pitch != lastPitch)
                {
                    lastRatio = Math.Pow(2.0, (PitchChain.Clamp(pitch) - nativePitch) / 12000.0);
                    lastPitch = pitch;
                }

                ratios[i] = lastRatio;
            }
        }

        var signal = _sampler.PlayVariable(wave, sampleCount, ratios);

        // --- filter: the cutoff envelope with the LFO added, then clamped, as the engine does ---
        var cutoff = _tvf.Envelope(partial, velocity, key, envelopeHoldSeconds, tailSeconds);
        if (suspended)
        {
            Array.Fill(cutoff, cutoff.Length > 0 ? cutoff[0] : 0.0);
        }
        else if (holdSamples > 0)
        {
            cutoff = ShiftSamples(cutoff, holdSamples, sampleCount, cutoff.Length > 0 ? cutoff[0] : 0.0);
        }

        if (!suspended)
        {
            var lfoTvf = ShiftTicks(_lfo.Modulation(toneNumber, partial, tickCount, LfoDestination.Tvf), holdTicks);
            if (AnyNonZero(lfoTvf))
            {
                var expanded = Expand(lfoTvf, cutoff.Length);
                for (var i = 0; i < cutoff.Length; i++)
                {
                    cutoff[i] = Math.Clamp(cutoff[i] + expanded[i], 0, 0x7FFF);
                }
            }
        }

        _tvf.Apply(signal, cutoff, partial.FilterType, TvfChain.ResonanceByte(partial));

        // --- amplitude ---
        var zoneLevel = _directory.ZoneLevel(partial.Multisample, key, partial.KeyCenter);
        var amplitude = _tva.Render(
            partial, velocity, key, envelopeHoldSeconds, tailSeconds, zoneLevel, _directory.ToneLevel(toneNumber),
            rateKey: envelopeRateKey);

        if (suspended)
        {
            amplitude = SuspendedAmplitude(amplitude, sampleCount, (long)(holdSeconds * SampleRate));
        }
        else if (holdSamples > 0)
        {
            amplitude = ShiftSamples(amplitude, holdSamples, sampleCount,
                amplitude.Length > 0 ? amplitude[0] : 0f);
        }

        var lfoTva = suspended
            ? null
            : ShiftTicks(_lfo.Modulation(toneNumber, partial, tickCount, LfoDestination.Tva), holdTicks);
        var tremolo = lfoTva is not null && AnyNonZero(lfoTva) ? Expand(lfoTva, sampleCount) : null;

        for (var i = 0; i < sampleCount && i < amplitude.Length; i++)
        {
            var gain = (double)amplitude[i];
            if (tremolo is not null)
            {
                // Amplitude modulation folds in as a fraction of 0x7f00, clamped first.
                gain *= 1.0 + (Math.Clamp(tremolo[i], -0x7F00, 0x7F00) / (double)0x7F00);
            }

            signal[i] = (float)(signal[i] * gain);
        }

        return signal;
    }

    private static void ApplyVolume(float[] left, float[] right, float[] mono, double[]? volumeCurve)
    {
        if (volumeCurve is null)
        {
            return;
        }

        // The send is post-fader: the wet scales with volume too.
        //
        // Split at the curve's end rather than clamping the index per sample. The curve is built
        // per note and can be shorter than the buffer, in which case the scalar loop held its last
        // value across the remainder -- so the tail is one constant gain, and only the covered
        // prefix needs a moving one.
        var moving = Math.Min(left.Length, volumeCurve.Length);

        ApplyChannel(left, volumeCurve, moving);
        ApplyChannel(right, volumeCurve, moving);
        ApplyChannel(mono, volumeCurve, moving);
    }

    private static void ApplyChannel(float[] channel, double[] volumeCurve, int moving)
    {
        Simd.ScaleVarying(channel.AsSpan(0, moving), volumeCurve);

        if (moving < channel.Length)
        {
            Simd.Scale(channel.AsSpan(moving), volumeCurve[^1]);
        }
    }

    /// <summary>Holds control-rate values across their block, as the engine does.</summary>
    /// <param name="ticks">One value per control tick.</param>
    /// <param name="sampleCount">How many samples to fill.</param>
    /// <returns>The expanded array.</returns>
    /// <remarks>
    /// Walked a control block at a time rather than a sample at a time. Per sample it costs an
    /// integer division to recover a tick index that only advances every <see cref="ControlBlock"/>
    /// samples; filling each block in one go leaves the same values and lets the runtime's vectorised
    /// <see cref="Span{T}.Fill"/> do the writing.
    /// </remarks>
    public static double[] Expand(double[] ticks, int sampleCount)
    {
        ArgumentNullException.ThrowIfNull(ticks);

        var result = new double[sampleCount];
        for (var start = 0; start < sampleCount; start += ControlBlock)
        {
            var length = Math.Min(ControlBlock, sampleCount - start);
            result.AsSpan(start, length).Fill(ticks[Math.Min(start / ControlBlock, ticks.Length - 1)]);
        }

        return result;
    }

    private static bool AnyNonZero(double[] values) => Simd.AnyNonZero(values);

    /// <summary>
    /// The pitch envelope per control tick, honouring the hold clock.
    /// </summary>
    /// <remarks>
    /// While the hold runs the runner is read without being stepped, so the head of the series is
    /// the envelope's start level — the value the note-on compute froze — and the machine then runs
    /// from the top when the hold expires. A suspended (one-shot) envelope never leaves that level.
    /// Note-off is on absolute time: a delayed envelope that starts 3 ticks late and is released at
    /// tick 50 has run for 47 of its own ticks, exactly as the engine's armed clock leaves it.
    /// </remarks>
    private double[]? PitchEnvelopeTicksHeld(
        PartialParameters partial,
        int key,
        int velocity,
        double holdSeconds,
        int tickCount,
        int holdTicks,
        bool suspended)
    {
        var runner = _pitch.CreateEnvelopeRunner(partial, key, velocity);
        if (runner is null)
        {
            return null;
        }

        var output = new double[Math.Max(0, tickCount)];
        if (suspended)
        {
            Array.Fill(output, runner.Level);
            return output;
        }

        var noteOffTick = (int)(holdSeconds * 100);
        for (var i = 0; i < output.Length; i++)
        {
            output[i] = i < holdTicks ? runner.Level : runner.Tick(i >= noteOffTick);
        }

        return output;
    }

    /// <summary>Delays a per-tick series by a hold, with silence at the head.</summary>
    private static double[] ShiftTicks(double[] ticks, int holdTicks)
    {
        if (holdTicks == 0)
        {
            return ticks;
        }

        var result = new double[ticks.Length];
        Array.Copy(ticks, 0, result, holdTicks, Math.Max(0, ticks.Length - holdTicks));
        return result;
    }

    /// <summary>Delays a per-sample series by a hold, repeating its first value across the head.</summary>
    private static T[] ShiftSamples<T>(T[] values, long holdSamples, int totalLength, T head)
    {
        var result = new T[totalLength];
        var holdLength = (int)Math.Min(holdSamples, totalLength);
        Array.Fill(result, head, 0, holdLength);

        var copy = Math.Min(values.Length, totalLength - holdLength);
        if (copy > 0)
        {
            Array.Copy(values, 0, result, holdLength, copy);
        }

        return result;
    }

    /// <summary>
    /// A one-shot's amplitude: the note-on level held flat, then the engine's fast fade at note-off.
    /// </summary>
    /// <remarks>
    /// The fade constants match the realtime voice's choke — full for 128 samples, linear to silence
    /// over the next 192 — which is the model of the ramp the engine's armed-clock note-off path
    /// requests in place of a release.
    /// </remarks>
    private static float[] SuspendedAmplitude(float[] amplitude, int sampleCount, long noteOffSample)
    {
        var level = amplitude.Length > 0 ? amplitude[0] : 0f;
        var result = new float[sampleCount];

        var flat = (int)Math.Clamp(noteOffSample + 128, 0, sampleCount);
        Array.Fill(result, level, 0, flat);

        for (var i = flat; i < sampleCount && i < noteOffSample + 128 + 192; i++)
        {
            result[i] = level * (float)(1.0 - ((i - noteOffSample - 128) / 192.0));
        }

        return result;
    }
}
