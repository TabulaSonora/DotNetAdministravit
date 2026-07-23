namespace TabulaSonora.Midi;

/// <summary>
/// A controller's value over time, as breakpoints.
/// </summary>
/// <remarks>
/// Zero-order hold: a value applies from its position until the next breakpoint. Note parameters are
/// latched at note-on, so what matters is the value in force at that instant.
/// </remarks>
public sealed class ControllerTimeline
{
    private readonly List<(long Position, int Value)> _points = [];

    /// <summary>Records a change.</summary>
    /// <param name="position">Position in samples.</param>
    /// <param name="value">New value.</param>
    public void Add(long position, int value) => _points.Add((position, value));

    /// <summary>The value in force at a position.</summary>
    /// <param name="position">Position in samples.</param>
    /// <param name="fallback">Value to use before the first breakpoint.</param>
    /// <returns>The value.</returns>
    public int ValueAt(long position, int fallback)
    {
        var result = fallback;
        foreach (var (at, value) in _points)
        {
            if (at > position)
            {
                break;
            }

            result = value;
        }

        return result;
    }

    /// <summary>Number of recorded changes.</summary>
    public int Count => _points.Count;

    /// <summary>
    /// Fills a span with this controller's value at every sample from a starting position.
    /// </summary>
    /// <param name="start">Position of the first sample.</param>
    /// <param name="destination">Receives one value per sample.</param>
    /// <param name="fallback">Value to use before the first breakpoint.</param>
    /// <returns>Whether the value changed at any point within the span.</returns>
    /// <remarks>
    /// Walks the breakpoints once rather than searching per sample. Sampling this at control-tick
    /// resolution instead would be ten times too coarse: events land on the 32-sample render grid,
    /// which is the granularity the engine applies them at.
    /// </remarks>
    public bool Fill(long start, Span<int> destination, int fallback)
    {
        var value = fallback;
        var cursor = 0;

        // Advance past everything already in force at the start.
        while (cursor < _points.Count && _points[cursor].Position <= start)
        {
            value = _points[cursor].Value;
            cursor++;
        }

        var changed = false;
        for (var i = 0; i < destination.Length; i++)
        {
            var at = start + i;
            while (cursor < _points.Count && _points[cursor].Position <= at)
            {
                if (_points[cursor].Value != value)
                {
                    changed = true;
                }

                value = _points[cursor].Value;
                cursor++;
            }

            destination[i] = value;
        }

        return changed;
    }
}

/// <summary>One note, with every parameter latched as it was at note-on.</summary>
/// <param name="Channel">MIDI channel.</param>
/// <param name="Note">MIDI note number.</param>
/// <param name="Velocity">MIDI velocity.</param>
/// <param name="On">Note-on position in samples.</param>
/// <param name="Off">Note-off position in samples, after any damper extension.</param>
/// <param name="Program">Program in force at note-on.</param>
/// <param name="Bank">Bank select MSB in force at note-on.</param>
/// <param name="Pan">CC#10.</param>
/// <param name="Volume">CC#7.</param>
/// <param name="Expression">CC#11.</param>
/// <param name="ReverbSend">CC#91.</param>
/// <param name="ChorusSend">CC#93.</param>
/// <param name="DelaySend">Part delay send, which has no Control Change and arrives only over SysEx.</param>
public readonly record struct NoteRecord(
    int Channel,
    int Note,
    int Velocity,
    long On,
    long Off,
    int Program,
    int Bank,
    int Pan,
    int Volume,
    int Expression,
    int ReverbSend,
    int ChorusSend,
    int DelaySend);

/// <summary>Per-channel controller timelines.</summary>
public sealed class PartTimelines
{
    /// <summary>CC#7 volume.</summary>
    public ControllerTimeline Volume { get; } = new();

    /// <summary>CC#11 expression.</summary>
    public ControllerTimeline Expression { get; } = new();

    /// <summary>CC#10 pan.</summary>
    public ControllerTimeline Pan { get; } = new();

    /// <summary>Pitch bend, as a 14-bit value.</summary>
    public ControllerTimeline Bend { get; } = new();

    /// <summary>Bend range in semitones, from RPN 00/00.</summary>
    public ControllerTimeline BendRange { get; } = new();

    /// <summary>CC#64 damper.</summary>
    public ControllerTimeline Damper { get; } = new();

    /// <summary>CC#1 modulation.</summary>
    public ControllerTimeline Modulation { get; } = new();

    /// <summary>CC#91 reverb send.</summary>
    public ControllerTimeline ReverbSend { get; } = new();

    /// <summary>CC#93 chorus send.</summary>
    public ControllerTimeline ChorusSend { get; } = new();

    /// <summary>Delay send, set over SysEx.</summary>
    public ControllerTimeline DelaySend { get; } = new();

    /// <summary>Program changes.</summary>
    public ControllerTimeline Program { get; } = new();

    /// <summary>Bank select MSB.</summary>
    public ControllerTimeline Bank { get; } = new();
}

/// <summary>A parsed sequence: controller timelines, notes, and the global effect selections.</summary>
/// <param name="Parts">Per-channel timelines, indexed by MIDI channel.</param>
/// <param name="Notes">Every note, with its parameters latched at note-on.</param>
/// <param name="MasterVolume">Master volume over time.</param>
/// <param name="ReverbType">GS reverb macro selections.</param>
/// <param name="ChorusType">GS chorus macro selections.</param>
/// <param name="DelayType">GS delay macro selections.</param>
/// <param name="LastEventPosition">
/// Position of the final event of any kind. This sets where a render ends, and it is not the same as
/// the last note: a file commonly closes with controller or meta traffic after the music stops.
/// </param>
public sealed record Sequence(
    PartTimelines[] Parts,
    IReadOnlyList<NoteRecord> Notes,
    ControllerTimeline MasterVolume,
    ControllerTimeline ReverbType,
    ControllerTimeline ChorusType,
    ControllerTimeline DelayType,
    long LastEventPosition);

/// <summary>
/// Turns a flat event list into per-part controller timelines and a note list.
/// </summary>
/// <remarks>
/// The engine has no MIDI output, so nothing can be read back from it: every piece of channel state
/// has to be tracked here.
/// </remarks>
public static class SequenceBuilder
{
    /// <summary>MIDI channels.</summary>
    public const int ChannelCount = 16;

    /// <summary>Power-on volume after a GM or GS reset.</summary>
    public const int DefaultVolume = 100;

    /// <summary>Power-on expression.</summary>
    public const int DefaultExpression = 127;

    /// <summary>Power-on pan.</summary>
    public const int DefaultPan = 64;

    /// <summary>Power-on master volume.</summary>
    public const int DefaultMaster = 127;

    /// <summary>GM default reverb send.</summary>
    public const int DefaultReverbSend = 40;

    /// <summary>GM default chorus send.</summary>
    public const int DefaultChorusSend = 0;

    /// <summary>Builds a sequence from a flat event list.</summary>
    /// <param name="events">Events, ordered by position.</param>
    /// <returns>The sequence.</returns>
    public static Sequence Build(IEnumerable<MidiEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var parts = new PartTimelines[ChannelCount];
        for (var i = 0; i < ChannelCount; i++)
        {
            parts[i] = new PartTimelines();
            parts[i].BendRange.Add(0, 2);
        }

        var notes = new List<NoteRecord>();
        var master = new ControllerTimeline();
        var reverbType = new ControllerTimeline();
        var chorusType = new ControllerTimeline();
        var delayType = new ControllerTimeline();

        // Insertion-ordered rather than a dictionary: the order notes close in is the order they are
        // reported, and a hash table's enumeration order is not stable across removals.
        var open = new List<(int Channel, int Note, long On, int Velocity)>();
        var sustained = new List<(int Channel, int Note, long RequestedOff)>();
        var rpnMsb = new int[ChannelCount];
        var rpnLsb = new int[ChannelCount];
        Array.Fill(rpnMsb, 0x7F);
        Array.Fill(rpnLsb, 0x7F);

        long lastPosition = 0;

        void CloseNote(int channel, int note, long offPosition)
        {
            var slot = open.FindIndex(o => o.Channel == channel && o.Note == note);
            if (slot < 0)
            {
                return;
            }

            var state = open[slot];
            open.RemoveAt(slot);

            var part = parts[channel];
            notes.Add(new NoteRecord(
                channel, note, state.Velocity, state.On, offPosition,
                Program: part.Program.ValueAt(state.On, 0),
                Bank: part.Bank.ValueAt(state.On, 0),
                Pan: part.Pan.ValueAt(state.On, DefaultPan),
                Volume: part.Volume.ValueAt(state.On, DefaultVolume),
                Expression: part.Expression.ValueAt(state.On, DefaultExpression),
                ReverbSend: part.ReverbSend.ValueAt(state.On, DefaultReverbSend),
                ChorusSend: part.ChorusSend.ValueAt(state.On, DefaultChorusSend),
                DelaySend: part.DelaySend.ValueAt(state.On, 0)));
        }

        foreach (var e in events)
        {
            lastPosition = Math.Max(lastPosition, e.Position);

            if (e.Kind == MidiEventKind.SysEx)
            {
                ApplySysEx(e, parts, master, reverbType, chorusType, delayType);
                continue;
            }

            var channel = e.Channel;
            var part = parts[channel];

            switch (e.MessageType)
            {
                case 0x90 when e.Data2 > 0:
                    // Re-striking a still-open note closes the old voice first.
                    CloseNote(channel, e.Data1, e.Position);
                    open.Add((channel, e.Data1, e.Position, e.Data2));
                    break;

                case 0x80:
                case 0x90:
                    if (part.Damper.ValueAt(e.Position, 0) >= 0x40)
                    {
                        // Damper down: the release waits for the pedal to lift.
                        sustained.Add((channel, e.Data1, e.Position));
                    }
                    else
                    {
                        CloseNote(channel, e.Data1, e.Position);
                    }

                    break;

                case 0xC0:
                    part.Program.Add(e.Position, e.Data1);
                    break;

                case 0xE0:
                    part.Bend.Add(e.Position, e.Data1 | (e.Data2 << 7));
                    break;

                case 0xB0:
                    ApplyControlChange(e, channel, part, rpnMsb, rpnLsb, sustained, open, CloseNote);
                    break;
            }
        }

        // Anything still ringing at the end closes there.
        foreach (var (channel, note, requestedOff) in sustained)
        {
            CloseNote(channel, note, requestedOff);
        }

        while (open.Count > 0)
        {
            CloseNote(open[0].Channel, open[0].Note, lastPosition);
        }

        return new Sequence(parts, notes, master, reverbType, chorusType, delayType, lastPosition);
    }

    private static void ApplyControlChange(
        MidiEvent e,
        int channel,
        PartTimelines part,
        int[] rpnMsb,
        int[] rpnLsb,
        List<(int Channel, int Note, long RequestedOff)> sustained,
        List<(int Channel, int Note, long On, int Velocity)> open,
        Action<int, int, long> closeNote)
    {
        var controller = e.Data1;
        var value = e.Data2;

        switch (controller)
        {
            case 1: part.Modulation.Add(e.Position, value); break;
            case 7: part.Volume.Add(e.Position, value); break;
            case 10: part.Pan.Add(e.Position, value); break;
            case 11: part.Expression.Add(e.Position, value); break;
            case 91: part.ReverbSend.Add(e.Position, value); break;
            case 93: part.ChorusSend.Add(e.Position, value); break;

            // Bank select MSB carries the variation; the LSB is unused by this engine.
            case 0: part.Bank.Add(e.Position, value); break;
            case 32: break;

            case 64:
                part.Damper.Add(e.Position, value);
                if (value < 0x40)
                {
                    // Oldest first, matching the order the reference releases them in -- the note
                    // list is ordered by when notes close, so iterating backwards reorders the output.
                    var releasing = sustained.Where(s => s.Channel == channel).ToList();
                    sustained.RemoveAll(s => s.Channel == channel);
                    foreach (var note in releasing)
                    {
                        closeNote(channel, note.Note, e.Position);
                    }
                }

                break;

            case 101: rpnMsb[channel] = value; break;
            case 100: rpnLsb[channel] = value; break;

            case 6:
                // Data entry commits the selected RPN. RPN 00/00 is the bend range in semitones.
                if (rpnMsb[channel] == 0 && rpnLsb[channel] == 0)
                {
                    part.BendRange.Add(e.Position, value);
                }

                break;

            case 120:
            case 123:
                foreach (var note in open.Where(o => o.Channel == channel).Select(o => o.Note).ToList())
                {
                    closeNote(channel, note, e.Position);
                }

                sustained.RemoveAll(s => s.Channel == channel);
                break;

            case 121:
                part.Expression.Add(e.Position, 127);
                part.Bend.Add(e.Position, 8192);
                part.Damper.Add(e.Position, 0);
                part.Modulation.Add(e.Position, 0);
                break;
        }
    }

    private static void ApplySysEx(
        MidiEvent e,
        PartTimelines[] parts,
        ControllerTimeline master,
        ControllerTimeline reverbType,
        ControllerTimeline chorusType,
        ControllerTimeline delayType)
    {
        var b = e.SysEx;
        if (b is null)
        {
            return;
        }

        // Universal master volume: F0 7F 7F 04 01 ll mm F7.
        if (b.Length >= 8 && b[0] == 0xF0 && b[1] == 0x7F && b[3] == 0x04 && b[4] == 0x01)
        {
            master.Add(e.Position, b[6]);
            return;
        }

        // Roland GS DT1: F0 41 dev 42 12 <3-byte address> <data> checksum F7.
        if (b.Length < 11 || b[0] != 0xF0 || b[1] != 0x41 || b[3] != 0x42 || b[4] != 0x12)
        {
            return;
        }

        int a1 = b[5], a2 = b[6], a3 = b[7], value = b[8];

        if (a1 == 0x40 && a2 == 0x01)
        {
            switch (a3)
            {
                case 0x30 when value <= 7:
                case 0x31 when value <= 7:
                    reverbType.Add(e.Position, value);
                    break;
                case 0x38 when value <= 7:
                    chorusType.Add(e.Position, value);
                    break;
                case 0x50 when value <= 9:
                    delayType.Add(e.Position, value);
                    break;
            }
        }
        else if (a1 == 0x40 && (a2 & 0xF0) == 0x10 && a3 == 0x2C)
        {
            parts[ChannelFromBlock(a2 & 0x0F)].DelaySend.Add(e.Position, value);
        }
    }

    /// <summary>
    /// Converts a GS block number to a MIDI channel.
    /// </summary>
    /// <param name="block">GS block number.</param>
    /// <returns>The MIDI channel.</returns>
    /// <remarks>
    /// The block number is not the channel: block 0 is channel 9 (the drum part), blocks 1–9 are
    /// channels 0–8, and blocks 10–15 map straight through.
    /// </remarks>
    public static int ChannelFromBlock(int block) =>
        block == 0 ? 9 : block <= 9 ? block - 1 : block;
}
