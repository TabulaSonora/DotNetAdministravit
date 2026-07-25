using TabulaSonora.Dsp;
using TabulaSonora.Effects;
using TabulaSonora.Midi;
using TabulaSonora.Patches;
using TabulaSonora.Rom;

namespace TabulaSonora;

/// <summary>How a sequence should be rendered.</summary>
public sealed record RenderOptions
{
    /// <summary>Which vintage's tone map program changes resolve against.</summary>
    public ToneMap Map { get; init; } = ToneMap.Sc8820;

    /// <summary>Seconds of release and effect tail rendered past the last event.</summary>
    public double TailSeconds { get; init; } = 2.2;

    /// <summary>Linear gain applied to the finished stereo output.</summary>
    /// <remarks>
    /// A host-side trim on the way out, not part of the engine: it is applied after the effect mix,
    /// so it scales the wet and dry alike and nothing upstream of it can see the difference. One
    /// leaves the output exactly as the engine produced it, which is the default because the
    /// comparisons this project lives on are against the DLL's own levels.
    /// </remarks>
    public double OutputGain { get; init; } = 1.0;

    /// <summary>MIDI channel routed to the drum path.</summary>
    public int DrumChannel { get; init; } = 9;

    /// <summary>Which drum map row a program change on the drum part resolves against.</summary>
    /// <remarks>
    /// The counterpart of <see cref="Realtime.ToneGenerator.DrumMapRow"/>, and here for the same
    /// reason it is there: the module derives the row from the part's internal bank code, which is
    /// not reversed, so nothing in a MIDI file can reach the second map. Both renderers have to take
    /// it the same way or a file would render differently through the two paths.
    /// </remarks>
    public int DrumMapRow { get; init; }

    /// <summary>
    /// Run the reverb on its send bus. On by default — the module always has it, and a GM reset
    /// leaves every part's reverb send at 40, so a dry render does not sound like the real thing.
    /// </summary>
    public bool Reverb { get; init; } = true;

    /// <summary>Run the chorus on its send bus. On by default, for the same reason.</summary>
    public bool Chorus { get; init; } = true;

    /// <summary>
    /// Run the system delay on its send bus. On by default, though its send is zero unless a song
    /// sets it over SysEx, so it usually costs nothing.
    /// </summary>
    public bool Delay { get; init; } = true;

    /// <summary>Force a reverb type instead of taking it from the sequence.</summary>
    public int? ReverbType { get; init; }

    /// <summary>Force a chorus type instead of taking it from the sequence.</summary>
    public int? ChorusType { get; init; }

    /// <summary>Force a delay type instead of taking it from the sequence.</summary>
    public int? DelayType { get; init; }

    /// <summary>Truncate the render at this many seconds.</summary>
    public double? EndSeconds { get; init; }

    /// <summary>
    /// Receives rendering progress from 0 to 1. Optional.
    /// </summary>
    /// <remarks>
    /// Reported per note, so it advances smoothly but not perfectly linearly in time — a long
    /// sustained note takes longer than a short one.
    /// </remarks>
    public IProgress<double>? Progress { get; init; }

    /// <summary>
    /// Per-channel mute and solo. Omit to hear everything.
    /// </summary>
    /// <remarks>
    /// A silenced channel contributes nothing at all — not to the dry mix and not to the reverb,
    /// chorus or delay buses either, so muting a part also removes its tail.
    /// </remarks>
    public ChannelMask? Channels { get; init; }

    /// <summary>
    /// How long a drum hit rings before its release is spliced in.
    /// </summary>
    /// <remarks>
    /// A drum ignores note-off, so this is not the note's length: the tone's own envelope does the
    /// decay and this only sets where the release segment begins.
    /// </remarks>
    public double DrumRingSeconds { get; init; } = 1.8;

    /// <summary>Extra time rendered past a drum's ring.</summary>
    public double DrumTailSeconds { get; init; } = 0.4;
}

/// <summary>The result of rendering a sequence.</summary>
/// <param name="Left">Left channel.</param>
/// <param name="Right">Right channel.</param>
/// <param name="SampleRate">Sample rate of the output.</param>
/// <param name="NoteCount">How many notes were rendered.</param>
/// <param name="Peak">Peak absolute sample value.</param>
public sealed record RenderResult(float[] Left, float[] Right, int SampleRate, int NoteCount, float Peak);

/// <summary>
/// Renders a parsed MIDI sequence to audio.
/// </summary>
/// <remarks>
/// <para>
/// Notes are rendered whole and summed at their start position, which is what the reference does and
/// what makes the output comparable against it. The engine itself is a real-time block renderer with
/// a 64-voice allocator; this path does not steal voices, so a sequence that would exceed the
/// polyphony renders every note instead of dropping the oldest.
/// </para>
/// <para>
/// Send buses are fed from each note's <em>pre-pan</em> mono and are post-fader, so the wet scales
/// with volume and expression but not with pan — matching the engine's mono send bus.
/// </para>
/// </remarks>
public sealed class SequenceRenderer
{
    private readonly NoteRenderer _notes;

    /// <summary>Creates a renderer.</summary>
    /// <param name="notes">The note renderer to draw voices from.</param>
    public SequenceRenderer(NoteRenderer notes)
    {
        ArgumentNullException.ThrowIfNull(notes);
        _notes = notes;
    }

    /// <summary>Creates a renderer over an open ROM image.</summary>
    /// <param name="rom">An open, verified ROM image.</param>
    /// <returns>The renderer.</returns>
    public static SequenceRenderer Create(RomImage rom) => new(new NoteRenderer(rom));

    /// <summary>Renders a Standard MIDI File.</summary>
    /// <param name="path">Path to the file.</param>
    /// <param name="options">Render options.</param>
    /// <returns>The rendered audio.</returns>
    public RenderResult RenderFile(string path, RenderOptions? options = null) =>
        Render(SequenceBuilder.Build(SmfReader.Read(path, NoteRenderer.SampleRate)), options);

    /// <summary>Renders a parsed sequence.</summary>
    /// <param name="sequence">The sequence.</param>
    /// <param name="options">Render options.</param>
    /// <returns>The rendered audio.</returns>
    public RenderResult Render(Sequence sequence, RenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        options ??= new RenderOptions();

        const int rate = NoteRenderer.SampleRate;

        // The render runs to the last event of any kind, not the last note: files routinely close
        // with controller traffic after the music stops, and truncating there clips the tail.
        var end = sequence.LastEventPosition;
        if (options.EndSeconds is { } limit)
        {
            end = Math.Min(end, (long)(limit * rate));
        }

        var total = (int)(end + (options.TailSeconds * rate));
        if (total <= 0)
        {
            return new RenderResult([], [], rate, 0, 0f);
        }

        var left = new float[total];
        var right = new float[total];

        var reverbSend = options.Reverb ? new float[total] : null;
        var chorusSend = options.Chorus ? new float[total] : null;
        var delaySend = options.Delay ? new float[total] : null;

        var kits = ResolveDrumKits(sequence, options);
        var chokeAt = ComputeDrumChokes(sequence, options, total, kits);
        var rendered = 0;

        // Take one snapshot for the whole render: a UI toggling mid-render would otherwise make some
        // notes of a part sound and others not.
        var channels = options.Channels?.Snapshot();

        for (var index = 0; index < sequence.Notes.Count; index++)
        {
            var note = sequence.Notes[index];
            if (note.On >= total)
            {
                continue;
            }

            if (channels is not null && !channels.IsAudible(note.Channel))
            {
                continue;
            }

            var isDrum = note.Channel == options.DrumChannel;
            var hold = Math.Max(0.0, (note.Off - note.On) / (double)rate);

            // A pitched-down key rings proportionally longer, so the window has to follow it or the
            // hit is cut off mid-decay -- see NoteRenderer.DrumRingScale.
            var drumRing = isDrum
                ? options.DrumRingSeconds * _notes.DrumRingScale(note.Note, kits[index], note.DrumPitch)
                : 0.0;

            var span = (int)Math.Min(
                total - note.On,
                (isDrum ? drumRing + options.DrumTailSeconds : hold + options.TailSeconds) * rate);

            if (span <= 0)
            {
                continue;
            }

            var volume = BuildVolumeCurve(sequence, note, span);

            RenderedNote voice;
            if (isDrum)
            {
                // A drum rings out: note-off is ignored, so the ring is a fixed length rather than
                // the note's own duration, and the tone's envelope does the decay.
                voice = _notes.RenderDrumNote(
                    note.Note, note.Velocity, options.DrumRingSeconds, options.DrumTailSeconds,
                    kits[index], volume, note.DrumPitch, note.DrumPan);

                if (chokeAt.TryGetValue(index, out var cut))
                {
                    ApplyChoke(voice, cut);
                }
            }
            else
            {
                voice = _notes.RenderNote(
                    note.Program, note.Note, note.Velocity, hold, options.TailSeconds,
                    options.Map, note.Bank, note.Pan, volume,
                    BuildPitchCurve(sequence, note, span),
                    BuildModWheelCurve(sequence, note, span));
            }

            rendered++;
            options.Progress?.Report((index + 1) / (double)sequence.Notes.Count);

            var count = Math.Min(voice.Left.Length, total - (int)note.On);

            Simd.Add(voice.Left.AsSpan(0, count), left.AsSpan((int)note.On, count));
            Simd.Add(voice.Right.AsSpan(0, count), right.AsSpan((int)note.On, count));

            AccumulateSend(reverbSend, voice.Mono, note.On, count, note.ReverbSend, Reverb.SendGain);
            AccumulateSend(chorusSend, voice.Mono, note.On, count, note.ChorusSend, Chorus.SendGain);
            AccumulateSend(delaySend, voice.Mono, note.On, count, note.DelaySend, SystemDelay.SendGain);
        }

        MixEffects(sequence, options, left, right, reverbSend, chorusSend, delaySend);

        // The trim goes on before the peak is measured, so the number a caller reports is the peak of
        // what it will actually write rather than of an intermediate it never sees.
        if (options.OutputGain != 1.0)
        {
            Simd.Scale(left, options.OutputGain);
            Simd.Scale(right, options.OutputGain);
        }

        // One peak across both channels: maximum is exact under any association, so folding the
        // right channel into the left channel's result is the same number the interleaved scan gave.
        var peak = Simd.PeakAbs(right, Simd.PeakAbs(left));

        return new RenderResult(left, right, rate, rendered, peak);
    }

    /// <summary>
    /// Resolves each drum note's kit, carrying the selection forward.
    /// </summary>
    /// <remarks>
    /// A program change on the drum part selects a kit through its own lookup. An <em>undefined</em>
    /// program leaves the current kit in place rather than falling back to Standard, so the choice
    /// has to be tracked in note order.
    /// </remarks>
    private Dictionary<int, int> ResolveDrumKits(Sequence sequence, RenderOptions options)
    {
        var kits = new Dictionary<int, int>();
        var current = 0;

        foreach (var (note, index) in sequence.Notes
            .Select((n, i) => (Note: n, Index: i))
            .Where(x => x.Note.Channel == options.DrumChannel)
            .OrderBy(x => x.Note.On))
        {
            if (_notes.Drums.KitForProgram(note.Program, options.DrumMapRow) is { } resolved)
            {
                current = resolved;
            }

            kits[index] = current;
        }

        return kits;
    }

    private static void AccumulateSend(
        float[]? bus, float[] mono, long position, int count, int level, Func<int, double> gainOf)
    {
        if (bus is null || level <= 0)
        {
            return;
        }

        Simd.MixScaled(mono.AsSpan(0, count), gainOf(level), bus.AsSpan((int)position, count));
    }

    private void MixEffects(
        Sequence sequence,
        RenderOptions options,
        float[] left,
        float[] right,
        float[]? reverbSend,
        float[]? chorusSend,
        float[]? delaySend)
    {
        // One scratch pair for all three effects. Each Process writes every element it is given, so
        // there is nothing to clear between them, and at a render's full length these are the two
        // largest buffers in the method -- allocating a fresh pair per effect was ~230 MB on a
        // five-minute song, which matters most in the browser.
        float[]? wetLeft = null;
        float[]? wetRight = null;

        if (chorusSend is not null && HasSignal(chorusSend))
        {
            var type = options.ChorusType ?? Selected(sequence.ChorusType);
            MixWet(Chorus.ForType(type), chorusSend, left, right, ref wetLeft, ref wetRight);
        }

        if (delaySend is not null && HasSignal(delaySend))
        {
            var type = options.DelayType ?? Selected(sequence.DelayType) ?? 0;
            MixWet(SystemDelay.ForType(type), delaySend, left, right, ref wetLeft, ref wetRight);
        }

        if (reverbSend is not null && HasSignal(reverbSend))
        {
            var type = options.ReverbType ?? Selected(sequence.ReverbType);
            MixWet(Reverb.ForType(type), reverbSend, left, right, ref wetLeft, ref wetRight);
        }
    }

    private static int? Selected(ControllerTimeline timeline)
    {
        var value = timeline.ValueAt(long.MaxValue, -1);
        return value < 0 ? null : value;
    }

    private static void MixWet(
        IEffect effect, float[] send, float[] left, float[] right,
        ref float[]? wetLeft, ref float[]? wetRight)
    {
        wetLeft ??= new float[send.Length];
        wetRight ??= new float[send.Length];
        effect.Process(send, wetLeft, wetRight);

        Simd.Add(wetLeft, left);
        Simd.Add(wetRight, right);
    }

    private static bool HasSignal(float[] bus) => Simd.AnyNonZero(bus);

    private static double[] BuildVolumeCurve(Sequence sequence, NoteRecord note, int span)
    {
        // Volume, expression and master are re-read as the note plays, at the render-block grid the
        // events land on -- not at the 100 Hz control tick, which is ten times too coarse.
        var part = sequence.Parts[note.Channel];

        var volume = new int[span];
        var expression = new int[span];
        var master = new int[span];

        part.Volume.Fill(note.On, volume, SequenceBuilder.DefaultVolume);
        part.Expression.Fill(note.On, expression, SequenceBuilder.DefaultExpression);
        sequence.MasterVolume.Fill(note.On, master, SequenceBuilder.DefaultMaster);

        var curve = new double[span];
        var lastKey = (-1, -1, -1);
        var lastValue = 0.0;

        for (var i = 0; i < span; i++)
        {
            var key = (volume[i], expression[i], master[i]);
            if (key != lastKey)
            {
                lastValue = TvaChain.PartVolumeScale(key.Item1, key.Item2, key.Item3);
                lastKey = key;
            }

            curve[i] = lastValue;
        }

        return curve;
    }

    /// <summary>
    /// The mod wheel's vibrato depth per control tick, or null when the wheel is never touched.
    /// </summary>
    /// <remarks>
    /// Returned per tick rather than per sample: it feeds LFO1's depth, which the engine only
    /// re-evaluates on the control tick.
    /// </remarks>
    private static double[]? BuildModWheelCurve(Sequence sequence, NoteRecord note, int span)
    {
        var part = sequence.Parts[note.Channel];
        if (part.Modulation.Count == 0)
        {
            return null;
        }

        var ticks = (span / NoteRenderer.ControlBlock) + 1;
        var perTick = new double[ticks];
        var touched = false;

        for (var t = 0; t < ticks; t++)
        {
            var at = note.On + ((long)t * NoteRenderer.ControlBlock);
            var controller = part.Modulation.ValueAt(at, 0);
            perTick[t] = LfoEngine.ModWheelDepth(controller);
            touched |= controller > 0;
        }

        return touched ? perTick : null;
    }

    private static double[]? BuildPitchCurve(Sequence sequence, NoteRecord note, int span)
    {
        // Bend also moves on the render-block grid. A song that bends continuously carries thousands
        // of changes, so reading it per control tick smears every slide.
        var part = sequence.Parts[note.Channel];

        var bend = new int[span];
        var range = new int[span];
        part.Bend.Fill(note.On, bend, 8192);
        part.BendRange.Fill(note.On, range, 2);

        var curve = new double[span];
        var moved = false;

        for (var i = 0; i < span; i++)
        {
            curve[i] = PitchChain.BendOffsetMilliSemitones(bend[i], range[i]);
            moved |= curve[i] != 0.0;
        }

        return moved ? curve : null;
    }

    private Dictionary<int, int> ComputeDrumChokes(
        Sequence sequence, RenderOptions options, int total, Dictionary<int, int> kits)
    {
        // Assign groups: a drum note in a group is cut when the next note in the same group hits.
        // This is what silences an open hi-hat when the closed one is played.
        var choke = new Dictionary<int, int>();
        var lastInGroup = new Dictionary<int, (int Index, long On)>();

        var ordered = sequence.Notes
            .Select((n, i) => (Note: n, Index: i))
            .Where(x => x.Note.Channel == options.DrumChannel && x.Note.On < total)
            .OrderBy(x => x.Note.On)
            .ToList();

        foreach (var (note, index) in ordered)
        {
            var group = _notes.Drums.Key(note.Note, kits.GetValueOrDefault(index)).Group;
            if (group == 0)
            {
                continue;
            }

            if (lastInGroup.TryGetValue(group, out var previous) && note.On > previous.On)
            {
                choke[previous.Index] = (int)(note.On - previous.On);
            }

            lastInGroup[group] = (index, note.On);
        }

        return choke;
    }

    private static void ApplyChoke(RenderedNote voice, int cutAt)
    {
        // The engine's cut is near-instant: full at the strike, most of the way down by 5 ms and
        // silent by 10 ms. Modelled as a short hold followed by a linear fade.
        const int hold = 128;
        const int fade = 192;

        var start = cutAt + hold;
        if (start >= voice.Left.Length)
        {
            return;
        }

        var length = Math.Min(fade, voice.Left.Length - start);
        for (var i = 0; i < length; i++)
        {
            var gain = 1.0f - (i / (float)length);
            voice.Left[start + i] *= gain;
            voice.Right[start + i] *= gain;
            voice.Mono[start + i] *= gain;
        }

        Array.Clear(voice.Left, start + length, voice.Left.Length - start - length);
        Array.Clear(voice.Right, start + length, voice.Right.Length - start - length);
        Array.Clear(voice.Mono, start + length, voice.Mono.Length - start - length);
    }
}
