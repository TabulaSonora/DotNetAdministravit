using TabulaSonora.Midi;

namespace TabulaSonora.Realtime;

/// <summary>
/// Streams a parsed MIDI file through a running engine.
/// </summary>
/// <remarks>
/// <para>
/// Events carry a sample position already quantised to the render-block grid, so dispatching them at
/// block boundaries places every one exactly where the engine would. Rendering is therefore chopped
/// into blocks whether the caller asks for 32 samples or 32,000.
/// </para>
/// <para>
/// This is the streaming counterpart of <see cref="SequenceRenderer"/>. It produces audio as it goes
/// rather than all at once, so playback can start immediately and a note can be held for as long as
/// the file holds it.
/// </para>
/// </remarks>
public sealed class SequencePlayer
{
    private readonly ToneGenerator _generator;
    private readonly IReadOnlyList<MidiEvent> _events;

    private int _cursor;
    private long _position;

    /// <summary>Creates a player over an engine and an event list.</summary>
    /// <param name="generator">The engine to drive.</param>
    /// <param name="events">Events, ordered by position.</param>
    public SequencePlayer(ToneGenerator generator, IReadOnlyList<MidiEvent> events)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(events);

        _generator = generator;
        _events = events;
        LastEventPosition = events.Count > 0 ? events[^1].Position : 0;
    }

    /// <summary>Reads a Standard MIDI File and creates a player for it.</summary>
    /// <param name="generator">The engine to drive.</param>
    /// <param name="path">Path to the file.</param>
    /// <returns>The player.</returns>
    public static SequencePlayer FromFile(ToneGenerator generator, string path) =>
        new(generator, SmfReader.Read(path, ToneGenerator.SampleRate));

    /// <summary>The engine being driven.</summary>
    public ToneGenerator Generator => _generator;

    /// <summary>Position of the final event of any kind.</summary>
    /// <remarks>
    /// Not the same as the last note: a file commonly closes with controller or meta traffic after the
    /// music stops, and stopping at the last note clips the tail.
    /// </remarks>
    public long LastEventPosition { get; }

    /// <summary>Current position in samples.</summary>
    public long Position => _position;

    /// <summary>Whether every event has been dispatched and nothing is still sounding.</summary>
    public bool AtEnd => _cursor >= _events.Count && _generator.ActiveVoices == 0;

    /// <summary>
    /// Renders audio, dispatching every event that falls inside it.
    /// </summary>
    /// <param name="left">Receives the left channel.</param>
    /// <param name="right">Receives the right channel; must be the same length as <paramref name="left"/>.</param>
    /// <exception cref="ArgumentException">The two channels are different lengths.</exception>
    public void Render(Span<float> left, Span<float> right)
    {
        if (left.Length != right.Length)
        {
            throw new ArgumentException("The two channels must be the same length.", nameof(right));
        }

        for (var start = 0; start < left.Length; start += ToneGenerator.BlockSize)
        {
            var count = Math.Min(ToneGenerator.BlockSize, left.Length - start);

            while (_cursor < _events.Count && _events[_cursor].Position <= _position)
            {
                _generator.Send(_events[_cursor]);
                _cursor++;
            }

            _generator.Render(left.Slice(start, count), right.Slice(start, count));
            _position += count;
        }
    }

    /// <summary>
    /// Streams the whole file into memory, from wherever the player currently is.
    /// </summary>
    /// <param name="tailSeconds">Seconds of release and effect tail rendered past the last event.</param>
    /// <param name="endSeconds">Truncate at this many seconds.</param>
    /// <returns>The rendered audio.</returns>
    /// <remarks>
    /// A convenience for comparing this path against <see cref="SequenceRenderer"/> and for tooling
    /// that wants a file rather than a stream; the length is computed the same way the offline
    /// renderer computes it, so the two line up sample for sample.
    /// </remarks>
    public RenderResult RenderToEnd(double tailSeconds = 2.2, double? endSeconds = null)
    {
        var end = LastEventPosition;
        if (endSeconds is { } limit)
        {
            end = Math.Min(end, (long)(limit * ToneGenerator.SampleRate));
        }

        var total = (int)(end + (tailSeconds * ToneGenerator.SampleRate));
        if (total <= 0)
        {
            return new RenderResult([], [], ToneGenerator.SampleRate, 0, 0f);
        }

        var left = new float[total];
        var right = new float[total];
        Render(left, right);

        var peak = 0f;
        for (var i = 0; i < total; i++)
        {
            peak = Math.Max(peak, Math.Max(Math.Abs(left[i]), Math.Abs(right[i])));
        }

        return new RenderResult(left, right, ToneGenerator.SampleRate, _generator.NoteCount, peak);
    }

    /// <summary>
    /// Jumps to a position, leaving the engine in the state the file would have put it in.
    /// </summary>
    /// <param name="sample">Where to jump to; negative rewinds to the start.</param>
    /// <remarks>
    /// Every event up to that point is replayed except the notes themselves, so program changes, bank
    /// selects, controllers and the GS effect selections all arrive — a seek into the middle of a song
    /// sounds the way playing up to that point would, without the notes in between.
    /// </remarks>
    public void Seek(long sample)
    {
        var target = Math.Max(0, sample);

        _generator.Reset();
        _cursor = 0;
        _position = 0;

        while (_cursor < _events.Count && _events[_cursor].Position < target)
        {
            var e = _events[_cursor];
            _cursor++;

            // Notes are what a seek skips. Everything else is state the engine would be carrying.
            var type = e.Kind == MidiEventKind.Channel ? e.MessageType : 0;
            if (type is 0x80 or 0x90 or 0xA0)
            {
                continue;
            }

            _generator.Send(e);
        }

        _position = target;
    }
}
