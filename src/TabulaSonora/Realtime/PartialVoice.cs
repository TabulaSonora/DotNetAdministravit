using TabulaSonora.Dsp;
using TabulaSonora.Patches;

namespace TabulaSonora.Realtime;

/// <summary>
/// One sounding partial, rendered a block at a time.
/// </summary>
/// <remarks>
/// <para>
/// This is the engine's unit of polyphony: a note consumes one of these per sounding partial, so a
/// two-partial patch halves the effective note count. Everything a partial needs to keep between
/// blocks lives here — the read position, the filter state, the LFO accumulators and the sample
/// counter the envelopes are evaluated against.
/// </para>
/// <para>
/// The clock domains are nested. The sampler, filter and amplitude run per sample; the pitch, the
/// LFOs and the filter coefficients refresh on the 100 Hz control tick, which lands every ten blocks;
/// and pitch bend is re-read at the block boundary, which is the grid the engine applies events on.
/// </para>
/// </remarks>
internal sealed class PartialVoice
{
    /// <summary>Samples a choked voice holds at full level before it fades.</summary>
    private const int ChokeHold = 128;

    /// <summary>Samples a choked voice takes to fade out.</summary>
    private const int ChokeFade = 192;

    private readonly WaveReader _reader;
    private readonly TvfChain _tvf;
    private readonly PanLaw _panLaw;

    private SegmentEnvelope? _amplitude;
    private SegmentEnvelope? _cutoff;
    private PitchEnvelopeRunner? _pitchEnvelope;
    private LfoRunner? _lfo1;
    private LfoRunner? _lfo2;
    private StateVariableFilter _filter;

    private int _cutoffBase;
    private int _resonanceByte;
    private int _filterType;
    private FilterTap _tap;

    private bool _isDrum;
    private double _drumBaseRatio;
    private double _basePitch;
    private double _nativePitch;

    private int _pan;
    private bool _panFollowsPart;
    private double _levelGain = 1.0;

    private long _sample;
    private long _noteOff = -1;
    private long _chokeAt = -1;
    private long _autoRelease = -1;
    private long _controlTick;

    private double _ratio = 1.0;
    private double _tremolo = 1.0;
    private double _frequency;
    private double _damping;
    private double _pitchModulation;

    /// <summary>Creates a voice bound to the engine's shared chains.</summary>
    /// <param name="interpolator">The 4-tap resampler.</param>
    /// <param name="tvf">The filter chain, for the coefficient conversions.</param>
    /// <param name="panLaw">The pan table.</param>
    public PartialVoice(Interpolator interpolator, TvfChain tvf, PanLaw panLaw)
    {
        _reader = new WaveReader(interpolator);
        _tvf = tvf;
        _panLaw = panLaw;
    }

    /// <summary>The MIDI channel that started this voice.</summary>
    public int Channel { get; private set; }

    /// <summary>The MIDI note that started it.</summary>
    public int Note { get; private set; }

    /// <summary>The drum mute group this voice belongs to, or zero.</summary>
    public int MuteGroup { get; private set; }

    /// <summary>Whether the voice has stopped producing sound.</summary>
    public bool Finished { get; private set; } = true;

    /// <summary>Whether note-off has been received.</summary>
    public bool Released => _noteOff >= 0;

    /// <summary>Starts a melodic partial.</summary>
    /// <param name="setup">Everything latched at note-on.</param>
    public void Start(in VoiceSetup setup)
    {
        var partial = setup.Partial;
        var descriptor = setup.Descriptor;

        Channel = setup.Channel;
        Note = setup.Note;
        MuteGroup = setup.MuteGroup;
        Finished = false;

        _sample = 0;
        _noteOff = -1;
        _chokeAt = -1;
        _controlTick = 0;
        _autoRelease = setup.AutoReleaseSamples;
        _isDrum = setup.IsDrum;
        _levelGain = setup.LevelGain;
        _pan = setup.Pan;
        _panFollowsPart = setup.PanFollowsPart;

        _reader.Start(setup.Wave);
        _filter = default;

        _amplitude = setup.Amplitude;
        (_cutoff, _cutoffBase) = (setup.Cutoff, setup.CutoffBase);
        _pitchEnvelope = setup.PitchEnvelope;
        _lfo1 = setup.Lfo1;
        _lfo2 = setup.Lfo2;

        _resonanceByte = TvfChain.ResonanceByte(partial);
        _filterType = partial.FilterType;
        _tap = _tvf.Tap(_filterType);

        if (setup.IsDrum)
        {
            _drumBaseRatio = setup.DrumBaseRatio;
        }
        else
        {
            _basePitch = setup.BasePitch;
            _nativePitch = (descriptor.RootKey * 1000.0) + 1024.0 - descriptor.FineTune;
        }

        _pitchModulation = 0.0;
        _tremolo = 1.0;
        _ratio = 1.0;
    }

    /// <summary>Starts the release.</summary>
    /// <remarks>A drum ignores note-off: its ring is a fixed length set at note-on.</remarks>
    public void NoteOff()
    {
        if (!_isDrum)
        {
            Release();
        }
    }

    private void Release()
    {
        if (_noteOff >= 0)
        {
            return;
        }

        _noteOff = _sample;
        _amplitude?.NoteOff(_sample);
        _cutoff?.NoteOff(_sample);
    }

    /// <summary>
    /// Cuts the voice short with the engine's fast fade.
    /// </summary>
    /// <remarks>
    /// Used when a voice is stolen and when a drum choke group fires. The cut is near-instant — full
    /// at the strike, most of the way down by 5 ms and silent by 10 ms — but it is not a hard stop,
    /// which would click.
    /// </remarks>
    public void Choke()
    {
        if (_chokeAt < 0)
        {
            _chokeAt = _sample;
        }
    }

    /// <summary>Silences the voice immediately and releases its wave.</summary>
    public void Kill()
    {
        Finished = true;
        _reader.Stop();
        _amplitude = null;
        _cutoff = null;
        _pitchEnvelope = null;
        _lfo1 = null;
        _lfo2 = null;
    }

    /// <summary>The stereo gains for this voice against a part's pan.</summary>
    /// <param name="partPan">CC#10 for the part.</param>
    /// <returns>The left and right gains.</returns>
    public (double Left, double Right) PanGains(int partPan) =>
        _panLaw.Gains(_panFollowsPart ? _pan + (partPan - PanLaw.Centre) : _pan);

    /// <summary>The kit level, folded in downstream of the voice gain.</summary>
    public double LevelGain => _levelGain;

    /// <summary>
    /// Renders one block.
    /// </summary>
    /// <param name="destination">Receives the block; its length is the render block size.</param>
    /// <param name="bendMilliSemitones">The part's current bend, in milli-semitones.</param>
    /// <param name="modWheelDepth">The part's current mod-wheel depth, in milli-semitones.</param>
    /// <remarks>
    /// A block never straddles a control tick: a voice starts on the render-block grid and the tick is
    /// ten blocks long, so the coefficient refresh always lands on a block boundary.
    /// </remarks>
    public void Render(Span<float> destination, double bendMilliSemitones, double modWheelDepth)
    {
        if (Finished)
        {
            destination.Clear();
            return;
        }

        if (_sample % ToneGenerator.ControlBlock == 0)
        {
            Control(bendMilliSemitones, modWheelDepth, first: _controlTick == 0);
            _controlTick++;
        }
        else
        {
            // Bend moves on the block grid even between control ticks.
            _ratio = Ratio(bendMilliSemitones);
        }

        var amplitude = _amplitude;

        for (var i = 0; i < destination.Length; i++)
        {
            var value = _reader.Next(_ratio);

            if (_tap != FilterTap.Bypass)
            {
                value = (float)_filter.Process(value, _frequency, _damping, _tap);
            }

            var gain = (amplitude?.ValueAt(_sample) ?? 0.0) * _tremolo;

            if (_chokeAt >= 0)
            {
                gain *= ChokeGain(_sample - _chokeAt);
            }

            destination[i] = (float)(value * gain);
            _sample++;
        }

        if (_autoRelease >= 0 && _sample >= _autoRelease)
        {
            // A drum rings for a fixed time and then takes its release. The tone's own envelope has
            // usually done the decay long before; this only bounds the voice's life, since a drum
            // never sees a note-off of its own.
            Release();
        }

        if (_reader.Finished
            || (amplitude?.IsFinished(_sample) ?? true)
            || (_chokeAt >= 0 && _sample - _chokeAt >= ChokeHold + ChokeFade))
        {
            Kill();
        }
    }

    private static double ChokeGain(long since)
    {
        if (since < ChokeHold)
        {
            return 1.0;
        }

        var into = since - ChokeHold;
        return into < ChokeFade ? 1.0 - (into / (double)ChokeFade) : 0.0;
    }

    private void Control(double bendMilliSemitones, double modWheelDepth, bool first)
    {
        var released = _noteOff >= 0 && _sample >= _noteOff;

        // The first control tick carries no LFO: the LFO object is created after that tick's update,
        // so nothing has been applied yet. Aligning to that is what makes the modulation line up with
        // the engine's own trace.
        double lfoPitch = 0, lfoTvf = 0, lfoTva = 0;

        if (!first)
        {
            _lfo1?.Tick();
            _lfo2?.Tick();

            if (_lfo1 is { } one)
            {
                // Only LFO1 takes the mod wheel; LFO2 is unaffected.
                lfoPitch += one.PitchValue(modWheelDepth);
                lfoTvf += one.Value(LfoDestination.Tvf);
                lfoTva += one.Value(LfoDestination.Tva);
            }

            if (_lfo2 is { } two)
            {
                lfoPitch += two.Value(LfoDestination.Pitch);
                lfoTvf += two.Value(LfoDestination.Tvf);
                lfoTva += two.Value(LfoDestination.Tva);
            }
        }

        var envelope = _pitchEnvelope?.Tick(released) ?? 0.0;
        _pitchModulation = envelope + lfoPitch;
        _ratio = Ratio(bendMilliSemitones);

        // Amplitude modulation folds in as a fraction of 0x7f00, clamped first.
        _tremolo = 1.0 + (Math.Clamp(lfoTva, -0x7F00, 0x7F00) / (double)0x7F00);

        if (_tap != FilterTap.Bypass && _cutoff is { } cutoff)
        {
            // Averaged over the block the coefficients are about to serve, not sampled at its start.
            // The envelope can cross several segments inside one 10 ms tick, and taking a single
            // point instead costs about 1.7% of peak on a piano attack -- the one place where the two
            // render paths measurably parted company.
            var total = 0.0;
            for (var n = 0; n < ToneGenerator.ControlBlock; n++)
            {
                total += Math.Clamp(_cutoffBase + cutoff.ValueAt(_sample + n) + lfoTvf, 0, 0x7FFF);
            }

            var units = _tvf.CutoffUnits(total / ToneGenerator.ControlBlock, _resonanceByte);

            _frequency = _tvf.FrequencyCoefficient(units);
            _damping = _tvf.DampingCoefficient(units, _resonanceByte, _filterType);
        }
    }

    private double Ratio(double bendMilliSemitones)
    {
        if (_isDrum)
        {
            // Drums take a different pitch route: the note does not transpose the sample, so there is
            // no key-follow and no absolute-pitch accumulator to clamp.
            return _drumBaseRatio * Math.Pow(2.0, _pitchModulation / 12000.0);
        }

        var pitch = _basePitch + _pitchModulation + bendMilliSemitones;
        return Math.Pow(2.0, (PitchChain.Clamp(pitch) - _nativePitch) / 12000.0);
    }
}

/// <summary>Everything a voice latches at note-on.</summary>
internal readonly record struct VoiceSetup
{
    /// <summary>MIDI channel.</summary>
    public required int Channel { get; init; }

    /// <summary>MIDI note.</summary>
    public required int Note { get; init; }

    /// <summary>The decoded wave to play.</summary>
    public required DecodedWave Wave { get; init; }

    /// <summary>The partial's parameter block.</summary>
    public required PartialParameters Partial { get; init; }

    /// <summary>The selected wave's descriptor.</summary>
    public required WaveDescriptor Descriptor { get; init; }

    /// <summary>The amplitude envelope.</summary>
    public required SegmentEnvelope Amplitude { get; init; }

    /// <summary>The cutoff envelope, which yields an offset from the peak.</summary>
    public required SegmentEnvelope Cutoff { get; init; }

    /// <summary>The base cutoff the envelope's offset is added to.</summary>
    public required int CutoffBase { get; init; }

    /// <summary>The pitch envelope, or <see langword="null"/> when it does nothing.</summary>
    public PitchEnvelopeRunner? PitchEnvelope { get; init; }

    /// <summary>The tone-common LFO.</summary>
    public LfoRunner? Lfo1 { get; init; }

    /// <summary>The per-partial LFO.</summary>
    public LfoRunner? Lfo2 { get; init; }

    /// <summary>Absolute base pitch in milli-semitones, for a melodic voice.</summary>
    public double BasePitch { get; init; }

    /// <summary>Playback ratio before modulation, for a drum voice.</summary>
    public double DrumBaseRatio { get; init; }

    /// <summary>Whether this voice takes the drum pitch route.</summary>
    public bool IsDrum { get; init; }

    /// <summary>Pan position, absolute for a drum and partial-relative otherwise.</summary>
    public int Pan { get; init; }

    /// <summary>Whether the part's own pan offsets this voice's.</summary>
    public bool PanFollowsPart { get; init; }

    /// <summary>The drum kit level, applied downstream of the voice gain.</summary>
    public double LevelGain { get; init; }

    /// <summary>The drum mute group, or zero.</summary>
    public int MuteGroup { get; init; }

    /// <summary>Sample at which a drum's ring is released, or −1.</summary>
    public long AutoReleaseSamples { get; init; }
}
