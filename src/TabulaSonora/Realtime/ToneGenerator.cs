using TabulaSonora.Dsp;
using TabulaSonora.Effects;
using TabulaSonora.Midi;
using TabulaSonora.Patches;
using TabulaSonora.Rom;
using TabulaSonora.Voices;

namespace TabulaSonora.Realtime;

/// <summary>How a running engine should behave.</summary>
public sealed record ToneGeneratorOptions
{
    /// <summary>Which vintage's tone map program changes resolve against.</summary>
    public ToneMap Map { get; init; } = ToneMap.Sc8820;

    /// <summary>MIDI channel routed to the drum path.</summary>
    public int DrumChannel { get; init; } = 9;

    /// <summary>Run the reverb on its send bus.</summary>
    public bool Reverb { get; init; } = true;

    /// <summary>Run the chorus on its send bus.</summary>
    public bool Chorus { get; init; } = true;

    /// <summary>Run the system delay on its send bus.</summary>
    public bool Delay { get; init; } = true;

    /// <summary>Force a reverb type instead of taking it from the stream.</summary>
    public int? ReverbType { get; init; }

    /// <summary>Force a chorus type instead of taking it from the stream.</summary>
    public int? ChorusType { get; init; }

    /// <summary>Force a delay type instead of taking it from the stream.</summary>
    public int? DelayType { get; init; }

    /// <summary>
    /// How long a drum hit rings before its release is spliced in.
    /// </summary>
    /// <remarks>
    /// A drum ignores note-off, so this is not the note's length: the tone's own envelope does the
    /// decay and this only bounds how long the voice occupies a slot.
    /// </remarks>
    public double DrumRingSeconds { get; init; } = 1.8;

    /// <summary>Per-channel mute and solo, read live so a mixer can change it while sound is running.</summary>
    public ChannelMask? Channels { get; init; }
}

/// <summary>
/// The real-time engine: MIDI in, audio out, rendered a block at a time.
/// </summary>
/// <remarks>
/// <para>
/// This is the block-based voice loop the hardware runs. Events are applied at the render-block
/// boundary — the grid the engine itself quantises them to — voices are allocated from a fixed pool of
/// 64 and stolen when it runs out, and each block is summed into a dry pair and three send buses that
/// the effects then process. Nothing about a note has to be known in advance, so a note can be held
/// indefinitely and released whenever.
/// </para>
/// <para>
/// It shares its DSP with <see cref="SequenceRenderer"/> rather than reimplementing it: the same
/// envelopes, the same sampler, the same tables. The two differ in what they can express, not in how
/// they sound — the offline path renders each note whole and never runs out of polyphony, while this
/// one enforces the engine's own limit and can be driven live.
/// </para>
/// <para>
/// Not thread-safe. Events and rendering must come from the same thread, or be serialised by the
/// caller; <see cref="ChannelMask"/> is the exception and is safe to change from anywhere.
/// </para>
/// </remarks>
public sealed class ToneGenerator
{
    /// <summary>Internal sample rate.</summary>
    public const int SampleRate = NoteRenderer.SampleRate;

    /// <summary>Samples per render block — the grid events are applied on.</summary>
    public const int BlockSize = SmfReader.BlockGrid;

    /// <summary>Samples per control tick, at 100 Hz.</summary>
    public const int ControlBlock = NoteRenderer.ControlBlock;

    private readonly NoteRenderer _notes;
    private readonly ToneGeneratorOptions _options;
    private readonly VoicePool _pool = new();
    private readonly Part[] _parts = new Part[SequenceBuilder.ChannelCount];
    private readonly PartialVoice?[] _slots = new PartialVoice?[VoicePool.MaxVoices];
    private readonly Stack<PartialVoice> _spare = new();
    private readonly List<PartialVoice> _dying = [];

    private readonly float[] _scratch = new float[BlockSize];
    private readonly float[] _reverbBus = new float[BlockSize];
    private readonly float[] _chorusBus = new float[BlockSize];
    private readonly float[] _delayBus = new float[BlockSize];
    private readonly float[] _wetLeft = new float[BlockSize];
    private readonly float[] _wetRight = new float[BlockSize];
    private readonly float[] _blockLeft = new float[BlockSize];
    private readonly float[] _blockRight = new float[BlockSize];

    // How much of the current block has been handed out. Blocks are always rendered whole, whatever
    // the caller asks for, because a voice's control tick is counted in them.
    private int _blockOffset = BlockSize;

    private Reverb? _reverb;
    private Chorus? _chorus;
    private SystemDelay? _delay;
    private int? _reverbType;
    private int? _chorusType;
    private int? _delayType;

    // What each effect was last built for. Distinct from the selection above so that reselecting the
    // type a network already has does not throw its tail away.
    private int? _reverbBuiltFor = -1;
    private int? _chorusBuiltFor = -1;
    private int? _delayBuiltFor = -1;

    private int _drumKit;
    private long _position;

    /// <summary>Creates an engine over a note renderer's loaded tables.</summary>
    /// <param name="notes">The note renderer whose chains and directory this engine shares.</param>
    /// <param name="options">How the engine should behave.</param>
    public ToneGenerator(NoteRenderer notes, ToneGeneratorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(notes);

        _notes = notes;
        _options = options ?? new ToneGeneratorOptions();

        for (var i = 0; i < _parts.Length; i++)
        {
            _parts[i] = new Part();
        }

        _pool.Stealing = index =>
        {
            if (_slots[index] is { } voice)
            {
                // Fade it out where it stands rather than cutting it dead, and let it finish in the
                // dying list -- the slot itself is about to be reused.
                voice.Choke();
                _dying.Add(voice);
                _slots[index] = null;
            }
        };

        _reverbType = _options.ReverbType;
        _chorusType = _options.ChorusType;
        _delayType = _options.DelayType;
    }

    /// <summary>Creates an engine over an open ROM image.</summary>
    /// <param name="rom">An open, verified ROM image.</param>
    /// <param name="options">How the engine should behave.</param>
    /// <returns>The engine.</returns>
    public static ToneGenerator Create(RomImage rom, ToneGeneratorOptions? options = null) =>
        new(new NoteRenderer(rom), options);

    /// <summary>How many samples have been rendered since the last reset.</summary>
    public long Position => _position;

    /// <summary>How many notes have sounded since the last reset.</summary>
    /// <remarks>A note that resolves to nothing — an unassigned program, or a velocity outside every
    /// partial's window — is not counted, since no voice starts.</remarks>
    public int NoteCount { get; private set; }

    /// <summary>How many voices are currently sounding.</summary>
    public int ActiveVoices
    {
        get
        {
            var count = _dying.Count;
            foreach (var slot in _slots)
            {
                if (slot is not null)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>The sixteen parts, indexed by MIDI channel.</summary>
    public IReadOnlyList<Part> Parts => _parts;

    /// <summary>The voice allocator.</summary>
    public VoicePool Voices => _pool;

    /// <summary>The drum kit in force, as the last program change on the drum part resolved it.</summary>
    /// <remarks>
    /// Worth reading rather than recomputing, because the two can differ: a program the map does not
    /// define leaves the kit as it was, so <see cref="DrumKitTable.KitForProgram"/> over the part's
    /// current program does not always answer what is actually loaded.
    /// </remarks>
    public int DrumKit => _drumKit;

    /// <summary>Which drum map row a program change on the drum part resolves against.</summary>
    /// <remarks>
    /// <para>
    /// Row 0 is the GM/GS map and the power-on value. The module does not take this from anywhere a
    /// MIDI file can reach directly: it derives it from the part's <em>internal</em> bank code through
    /// <see cref="DrumKitTable.MapRow"/>, and the translation from a bank select to that code is not
    /// yet reversed. Until it is, the row is set by the host rather than inferred, which is the only
    /// way the second map's kits can be sounded at all.
    /// </para>
    /// <para>
    /// Deliberately <em>not</em> cleared by <see cref="Reset"/>. The kit is, because a program change
    /// selects it and <see cref="Reset"/> undoes what MIDI did; the row is configuration, in the same
    /// class as <see cref="ToneGeneratorOptions.Map"/>, which also survives.
    /// </para>
    /// </remarks>
    public int DrumMapRow { get; set; }

    /// <summary>Silences everything and returns every part to its power-on state.</summary>
    public void Reset()
    {
        for (var i = 0; i < _slots.Length; i++)
        {
            Recycle(i);
        }

        foreach (var voice in _dying)
        {
            voice.Kill();
            _spare.Push(voice);
        }

        _dying.Clear();
        _pool.Reset();

        foreach (var part in _parts)
        {
            part.Reset();
        }

        _reverb?.Reset();
        _chorus?.Reset();
        _delay?.Reset();

        _drumKit = 0;
        _position = 0;
        _blockOffset = BlockSize;
        NoteCount = 0;
    }

    /// <summary>Applies one MIDI event.</summary>
    /// <param name="message">The event; its position is ignored, since it applies now.</param>
    public void Send(in MidiEvent message)
    {
        if (message.Kind == MidiEventKind.SysEx)
        {
            if (message.SysEx is { } bytes)
            {
                SendSysEx(bytes);
            }

            return;
        }

        SendChannel(message.Status, message.Data1, message.Data2);
    }

    /// <summary>Applies one channel voice message.</summary>
    /// <param name="status">Status byte.</param>
    /// <param name="data1">First data byte.</param>
    /// <param name="data2">Second data byte.</param>
    public void SendChannel(int status, int data1, int data2)
    {
        var channel = status & 0x0F;
        var part = _parts[channel];

        switch (status & 0xF0)
        {
            case 0x90 when data2 > 0:
                // Re-striking a still-open note takes the old voice first.
                StopNote(channel, data1);

                // The new strike supersedes a note-off the pedal is still holding for this note.
                // Without this the pedal's lift releases the note being struck here.
                part.Sustained.RemoveAll(n => n == data1);

                StartNote(channel, data1, data2);
                break;

            case 0x80:
            case 0x90:
                if (part.DamperDown)
                {
                    part.Sustained.Add(data1);
                }
                else
                {
                    StopNote(channel, data1);
                }

                break;

            case 0xB0:
                ControlChange(channel, part, data1, data2);
                break;

            case 0xC0:
                part.Program = data1;
                if (channel == _options.DrumChannel &&
                    _notes.Drums.KitForProgram(data1, DrumMapRow) is { } kit)
                {
                    // An undefined program leaves the current kit in place rather than falling back
                    // to Standard.
                    _drumKit = kit;
                }

                break;

            case 0xE0:
                part.Bend = data1 | (data2 << 7);
                break;
        }
    }

    /// <summary>Applies one system-exclusive message.</summary>
    /// <param name="bytes">The message, including the leading <c>F0</c>.</param>
    public void SendSysEx(ReadOnlySpan<byte> bytes)
    {
        // Universal master volume: F0 7F 7F 04 01 ll mm F7.
        if (bytes.Length >= 8 && bytes[0] == 0xF0 && bytes[1] == 0x7F && bytes[3] == 0x04 && bytes[4] == 0x01)
        {
            foreach (var part in _parts)
            {
                part.Master = bytes[6];
            }

            return;
        }

        // Roland GS DT1: F0 41 dev 42 12 <3-byte address> <data> checksum F7.
        if (bytes.Length < 11 || bytes[0] != 0xF0 || bytes[1] != 0x41 || bytes[3] != 0x42 || bytes[4] != 0x12)
        {
            return;
        }

        int a1 = bytes[5], a2 = bytes[6], a3 = bytes[7], value = bytes[8];

        if (a1 == 0x40 && a2 == 0x01)
        {
            switch (a3)
            {
                case 0x30 or 0x31 when value <= 7:
                    _reverbType = _options.ReverbType ?? value;
                    break;
                case 0x38 when value <= 7:
                    _chorusType = _options.ChorusType ?? value;
                    break;
                case 0x50 when value <= 9:
                    _delayType = _options.DelayType ?? value;
                    break;
            }
        }
        else if (a1 == 0x40 && (a2 & 0xF0) == 0x10 && a3 == 0x2C)
        {
            _parts[SequenceBuilder.ChannelFromBlock(a2 & 0x0F)].DelaySend = value;
        }
    }

    /// <summary>
    /// Renders audio.
    /// </summary>
    /// <param name="left">Receives the left channel.</param>
    /// <param name="right">Receives the right channel; must be the same length as <paramref name="left"/>.</param>
    /// <exception cref="ArgumentException">The two channels are different lengths.</exception>
    /// <remarks>
    /// Any length is accepted. Blocks are still rendered whole and the remainder carried, because a
    /// voice counts its control tick in blocks — asking for a length that is not a multiple of
    /// <see cref="BlockSize"/> costs nothing but a copy. A caller that wants events to land exactly
    /// where the engine would put them should render in multiples of it and send between calls.
    /// </remarks>
    public void Render(Span<float> left, Span<float> right)
    {
        if (left.Length != right.Length)
        {
            throw new ArgumentException("The two channels must be the same length.", nameof(right));
        }

        var written = 0;
        while (written < left.Length)
        {
            if (_blockOffset >= BlockSize)
            {
                RenderBlock();
                _blockOffset = 0;
            }

            var count = Math.Min(BlockSize - _blockOffset, left.Length - written);
            _blockLeft.AsSpan(_blockOffset, count).CopyTo(left[written..]);
            _blockRight.AsSpan(_blockOffset, count).CopyTo(right[written..]);

            _blockOffset += count;
            written += count;
            _position += count;
        }
    }

    private void RenderBlock()
    {
        var left = _blockLeft.AsSpan();
        var right = _blockRight.AsSpan();

        left.Clear();
        right.Clear();

        var reverbBus = _reverbBus.AsSpan();
        var chorusBus = _chorusBus.AsSpan();
        var delayBus = _delayBus.AsSpan();

        reverbBus.Clear();
        chorusBus.Clear();
        delayBus.Clear();

        for (var i = 0; i < _slots.Length; i++)
        {
            if (_slots[i] is { } voice)
            {
                MixVoice(voice, left, right, reverbBus, chorusBus, delayBus);
                if (voice.Finished)
                {
                    Recycle(i);
                }
            }
        }

        for (var i = _dying.Count - 1; i >= 0; i--)
        {
            var voice = _dying[i];
            MixVoice(voice, left, right, reverbBus, chorusBus, delayBus);
            if (voice.Finished)
            {
                _dying.RemoveAt(i);
                _spare.Push(voice);
            }
        }

        MixEffects(left, right, reverbBus, chorusBus, delayBus);
    }

    private void MixVoice(
        PartialVoice voice,
        Span<float> left,
        Span<float> right,
        Span<float> reverbBus,
        Span<float> chorusBus,
        Span<float> delayBus)
    {
        var part = _parts[voice.Channel];
        var scratch = _scratch.AsSpan(0, left.Length);

        voice.Render(scratch, part.BendMilliSemitones, part.ModWheelDepth);

        // A silenced channel contributes nothing at all -- not to the dry mix and not to the sends
        // either, so muting a part also removes its tail. It keeps running, so unmuting is instant.
        if (_options.Channels is { } mask && !mask.IsAudible(voice.Channel))
        {
            return;
        }

        var (panLeft, panRight) = voice.PanGains(part.Pan);
        var gain = part.VolumeScale * voice.LevelGain;

        // Sends are fed from the pre-pan mono and are post-fader, so the wet scales with volume and
        // expression but not with pan -- matching the engine's mono send bus.
        var toReverb = _reverb is not null && part.ReverbSend > 0 ? Reverb.SendGain(part.ReverbSend) : 0.0;
        var toChorus = _chorus is not null && part.ChorusSend > 0 ? Chorus.SendGain(part.ChorusSend) : 0.0;
        var toDelay = _delay is not null && part.DelaySend > 0 ? SystemDelay.SendGain(part.DelaySend) : 0.0;

        // The voice gain stays a separate multiply from the pan and send levels; see the remarks on
        // the two-gain overload for why it cannot be folded into them.
        Simd.MixScaled(scratch, gain, panLeft, left);
        Simd.MixScaled(scratch, gain, panRight, right);

        // A send at zero is skipped rather than multiplied out, which drops three of the five passes
        // over the block on a part with no sends -- the usual case for the delay. That is exact
        // rather than merely harmless: the buses are cleared to +0 at the top of every block, and a
        // sum that starts at +0 can never reach -0, so adding the ±0 the multiply would have
        // produced is guaranteed to leave every element as it stands.
        if (toReverb != 0.0)
        {
            Simd.MixScaled(scratch, gain, toReverb, reverbBus);
        }

        if (toChorus != 0.0)
        {
            Simd.MixScaled(scratch, gain, toChorus, chorusBus);
        }

        if (toDelay != 0.0)
        {
            Simd.MixScaled(scratch, gain, toDelay, delayBus);
        }
    }

    private void MixEffects(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> reverbBus,
        ReadOnlySpan<float> chorusBus,
        ReadOnlySpan<float> delayBus)
    {
        EnsureEffects();

        var wetLeft = _wetLeft.AsSpan(0, left.Length);
        var wetRight = _wetRight.AsSpan(0, left.Length);

        // The same order the offline renderer mixes in: chorus, delay, then reverb.
        if (_chorus is { } chorus)
        {
            chorus.Process(chorusBus, wetLeft, wetRight);
            Add(left, right, wetLeft, wetRight);
        }

        if (_delay is { } delay)
        {
            delay.Process(delayBus, wetLeft, wetRight);
            Add(left, right, wetLeft, wetRight);
        }

        if (_reverb is { } reverb)
        {
            reverb.Process(reverbBus, wetLeft, wetRight);
            Add(left, right, wetLeft, wetRight);
        }
    }

    private static void Add(Span<float> left, Span<float> right, ReadOnlySpan<float> wetLeft, ReadOnlySpan<float> wetRight)
    {
        Simd.Add(wetLeft, left);
        Simd.Add(wetRight, right);
    }

    /// <summary>
    /// Builds the effects, or rebuilds one whose GS type has changed.
    /// </summary>
    /// <remarks>
    /// A type change mid-stream replaces the network, which clears its tail. The offline renderer
    /// cannot do this at all — it picks the last type the file selects and runs the whole song through
    /// it — so a file that switches reverb types part way sounds different here, and closer to the
    /// module.
    /// </remarks>
    private void EnsureEffects()
    {
        if (_options.Reverb && (_reverb is null || _reverbBuiltFor != _reverbType))
        {
            _reverb = Reverb.ForType(_reverbType);
            _reverbBuiltFor = _reverbType;
        }

        if (_options.Chorus && (_chorus is null || _chorusBuiltFor != _chorusType))
        {
            _chorus = Chorus.ForType(_chorusType);
            _chorusBuiltFor = _chorusType;
        }

        if (_options.Delay && (_delay is null || _delayBuiltFor != _delayType))
        {
            _delay = SystemDelay.ForType(_delayType ?? 0);
            _delayBuiltFor = _delayType;
        }
    }

    private void ControlChange(int channel, Part part, int controller, int value)
    {
        switch (controller)
        {
            case 0: part.Bank = value; break;
            case 1: part.Modulation = value; break;
            case 7: part.Volume = value; break;
            case 10: part.Pan = value; break;
            case 11: part.Expression = value; break;
            case 91: part.ReverbSend = value; break;
            case 93: part.ChorusSend = value; break;

            case 64:
                part.Damper = value;
                if (value < 0x40)
                {
                    // Oldest first, which is the order the offline renderer closes them in too.
                    foreach (var note in part.Sustained)
                    {
                        StopNote(channel, note);
                    }

                    part.Sustained.Clear();
                }

                break;

            case 101: part.RpnMsb = value; break;
            case 100: part.RpnLsb = value; break;

            case 6:
                // Data entry commits the selected RPN. RPN 00/00 is the bend range in semitones.
                if (part.RpnMsb == 0 && part.RpnLsb == 0)
                {
                    part.BendRange = value;
                }

                break;

            case 120:
            case 123:
                for (var i = 0; i < _slots.Length; i++)
                {
                    if (_slots[i] is { } voice && voice.Channel == channel)
                    {
                        // All Sound Off cuts; All Notes Off releases. Both stop the note sounding, and
                        // the fade is what keeps the cut from clicking.
                        if (controller == 120)
                        {
                            voice.Choke();
                        }
                        else
                        {
                            voice.NoteOff();
                        }
                    }
                }

                part.Sustained.Clear();
                break;

            case 121:
                part.ResetControllers();
                break;
        }
    }

    private void StopNote(int channel, int note)
    {
        for (var i = 0; i < _slots.Length; i++)
        {
            if (_slots[i] is { } voice && voice.Channel == channel && voice.Note == note && !voice.Released)
            {
                voice.NoteOff();
            }
        }

        _pool.Release(channel, note);
    }

    private void StartNote(int channel, int note, int velocity)
    {
        if (channel == _options.DrumChannel)
        {
            StartDrum(channel, note, velocity);
            return;
        }

        var part = _parts[channel];
        var tones = _notes.Directory.ProgramTones(part.Program, _options.Map, part.Bank);
        if (tones.Count == 0)
        {
            return;
        }

        var group = _pool.BeginNoteGroup();
        var sounded = false;

        foreach (var toneNumber in tones)
        {
            var resolved = _notes.Directory.Resolve(toneNumber, note, velocity);
            var tone = _notes.Directory.GetTone(toneNumber);
            if (tone is null)
            {
                continue;
            }

            foreach (var sounding in resolved.Partials)
            {
                var descriptor = sounding.Descriptor;
                var partial = tone.Partials[sounding.PartialIndex];
                var wave = _notes.Sampler.Decode(descriptor);
                if (wave is null)
                {
                    continue;
                }

                var key = Math.Clamp(note, 0, 0x7F);
                var (amplitude, cutoff, cutoffBase) = Envelopes(toneNumber, partial, key, velocity);
                var (lfo1, lfo2) = _notes.Lfo.CreateRunners(toneNumber, partial);

                Begin(channel, note, velocity, group, new VoiceSetup
                {
                    Channel = channel,
                    Note = note,
                    Wave = wave,
                    Partial = partial,
                    Descriptor = descriptor,
                    Amplitude = amplitude,
                    Cutoff = cutoff,
                    CutoffBase = cutoffBase,
                    PitchEnvelope = _notes.Pitch.CreateEnvelopeRunner(partial, key, velocity),
                    Lfo1 = lfo1,
                    Lfo2 = lfo2,
                    BasePitch = _notes.Pitch.BasePitchMilliSemitones(partial, note, partial.KeyCenter),
                    Pan = partial.Pan,
                    PanFollowsPart = true,
                    LevelGain = 1.0,
                    AutoReleaseSamples = -1,
                });

                sounded = true;
            }
        }

        if (sounded)
        {
            NoteCount++;
        }
    }

    private void StartDrum(int channel, int note, int velocity)
    {
        var key = _notes.Drums.Key(note, _drumKit);
        var resolved = _notes.Directory.Resolve(key.Tone, note: 60, velocity);
        var tone = _notes.Directory.GetTone(key.Tone);
        if (tone is null || resolved.Partials.Count == 0)
        {
            return;
        }

        // A drum in a mute group cuts the last one that sounded in it -- what silences an open hi-hat
        // when the closed one is played.
        if (key.Group != 0)
        {
            foreach (var slot in _slots)
            {
                if (slot is { } voice && voice.MuteGroup == key.Group)
                {
                    voice.Choke();
                }
            }
        }

        var coarse = DrumKitTable.CoarsePitchRatio(key.Pitch);
        var levelGain = DrumKitTable.LevelGain(key.Level);
        var ring = (long)(_options.DrumRingSeconds * SampleRate);
        var group = _pool.BeginNoteGroup();
        var sounded = false;

        foreach (var sounding in resolved.Partials)
        {
            var descriptor = sounding.Descriptor;
            var partial = tone.Partials[sounding.PartialIndex];
            var wave = _notes.Sampler.Decode(descriptor);
            if (wave is null)
            {
                continue;
            }

            var (amplitude, cutoff, cutoffBase) = Envelopes(key.Tone, partial, 60, velocity);
            var (lfo1, lfo2) = _notes.Lfo.CreateRunners(key.Tone, partial);

            // The note does not transpose the sample: the tone sounds at its own root, trimmed by the
            // partial's transpose and coarse tune, and the kit's coarse-pitch plane scales it.
            var raw = partial.Raw;
            var nativeSemitones = descriptor.RootKey
                + ((1024.0 - descriptor.FineTune) / 1000.0)
                + (0x40 - raw[0x10])
                - ((raw[0x11] - 0x40) / 100.0);

            Begin(channel, note, velocity, group, new VoiceSetup
            {
                Channel = channel,
                Note = note,
                Wave = wave,
                Partial = partial,
                Descriptor = descriptor,
                Amplitude = amplitude,
                Cutoff = cutoff,
                CutoffBase = cutoffBase,
                PitchEnvelope = _notes.Pitch.CreateEnvelopeRunner(partial, 60, velocity),
                Lfo1 = lfo1,
                Lfo2 = lfo2,
                IsDrum = true,
                DrumBaseRatio = Math.Pow(2.0, (60 - nativeSemitones) / 12.0) * coarse,
                Pan = key.Pan,
                PanFollowsPart = false,
                LevelGain = levelGain,
                MuteGroup = key.Group,
                AutoReleaseSamples = ring,
            });

            sounded = true;
        }

        if (sounded)
        {
            NoteCount++;
        }
    }

    private (SegmentEnvelope Amplitude, SegmentEnvelope Cutoff, int CutoffBase) Envelopes(
        int toneNumber, PartialParameters partial, int key, int velocity)
    {
        var zoneLevel = _notes.Directory.ZoneLevel(partial.Multisample, key, partial.KeyCenter);
        var amplitude = _notes.Tva.CreateEnvelope(
            partial, velocity, key, zoneLevel, _notes.Directory.ToneLevel(toneNumber), SampleRate);

        var (cutoff, cutoffBase) = _notes.Tvf.CreateEnvelope(partial, velocity, key, SampleRate);
        return (amplitude, cutoff, cutoffBase);
    }

    private void Begin(int channel, int note, int velocity, int group, in VoiceSetup setup)
    {
        // Allocating may steal, which hands whatever was sounding to the dying list and empties the
        // slot; anything still there was already finished.
        var slot = _pool.Allocate(channel, note, velocity, group);
        Release(slot.Index);

        var voice = _spare.Count > 0
            ? _spare.Pop()
            : new PartialVoice(_notes.Interpolator, _notes.Tvf, _notes.Pan);

        voice.Start(setup);
        _slots[slot.Index] = voice;
    }

    private void Recycle(int index)
    {
        Release(index);
        _pool.Free(index);
    }

    private void Release(int index)
    {
        if (_slots[index] is { } voice)
        {
            voice.Kill();
            _spare.Push(voice);
            _slots[index] = null;
        }
    }
}
