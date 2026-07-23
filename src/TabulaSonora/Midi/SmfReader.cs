using System.Buffers.Binary;

namespace TabulaSonora.Midi;

/// <summary>What kind of message an event carries.</summary>
public enum MidiEventKind
{
    /// <summary>A channel voice message.</summary>
    Channel,

    /// <summary>A system-exclusive message, including the leading <c>F0</c>.</summary>
    SysEx,
}

/// <summary>One scheduled MIDI message.</summary>
/// <param name="Position">Position in samples, quantised to the render-block grid.</param>
/// <param name="Kind">Which kind of message.</param>
/// <param name="Status">Status byte, for a channel message.</param>
/// <param name="Data1">First data byte.</param>
/// <param name="Data2">Second data byte, or zero for two-byte messages.</param>
/// <param name="SysEx">Raw bytes, for a system-exclusive message.</param>
public readonly record struct MidiEvent(
    long Position,
    MidiEventKind Kind,
    int Status,
    int Data1,
    int Data2,
    byte[]? SysEx)
{
    /// <summary>The channel a channel message addresses.</summary>
    public int Channel => Status & 0x0F;

    /// <summary>The message type nibble.</summary>
    public int MessageType => Status & 0xF0;
}

/// <summary>
/// A dependency-free Standard MIDI File reader.
/// </summary>
/// <remarks>
/// <para>
/// Handles formats 0, 1 and 2, running status, system-exclusive messages, and the tempo map. Track
/// ticks are converted to seconds through that map and then to samples, and finally quantised to the
/// render-block grid — the granularity at which the engine actually applies events. Quantising here
/// is what lets an offline render line up sample-for-sample with the real engine's own output.
/// </para>
/// <para>
/// Meta events other than tempo are dropped; nothing downstream consumes them.
/// </para>
/// </remarks>
public static class SmfReader
{
    /// <summary>Samples per render block — the grid events are applied on.</summary>
    public const int BlockGrid = 32;

    /// <summary>Default tempo when a file sets none: 120 bpm, in microseconds per quarter note.</summary>
    public const int DefaultTempo = 500_000;

    /// <summary>Reads a Standard MIDI File.</summary>
    /// <param name="path">Path to the file.</param>
    /// <param name="sampleRate">Internal sample rate.</param>
    /// <returns>The events, ordered by position.</returns>
    public static List<MidiEvent> Read(string path, int sampleRate = 32000) =>
        Parse(File.ReadAllBytes(path), sampleRate);

    /// <summary>Parses a Standard MIDI File held in memory.</summary>
    /// <param name="data">The file's bytes.</param>
    /// <param name="sampleRate">Internal sample rate.</param>
    /// <returns>The events, ordered by position.</returns>
    /// <exception cref="InvalidDataException">The file is malformed or uses SMPTE timing.</exception>
    public static List<MidiEvent> Parse(ReadOnlySpan<byte> data, int sampleRate = 32000)
    {
        if (data.Length < 14 || !data[..4].SequenceEqual("MThd"u8))
        {
            throw new InvalidDataException("Not a Standard MIDI File: missing MThd.");
        }

        var trackCount = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(10, 2));
        var division = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(12, 2));
        if ((division & 0x8000) != 0)
        {
            throw new InvalidDataException("SMPTE division is not supported.");
        }

        var ticksPerQuarter = division;
        var position = 14;
        var merged = new List<(long Tick, int Order, string Kind, int Status, int Data1, int Data2, byte[]? Bytes)>();
        var order = 0;

        for (var track = 0; track < trackCount; track++)
        {
            if (position + 8 > data.Length || !data.Slice(position, 4).SequenceEqual("MTrk"u8))
            {
                throw new InvalidDataException($"Track {track} is missing its MTrk header.");
            }

            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(position + 4, 4));
            position += 8;

            var end = Math.Min(position + length, data.Length);
            long tick = 0;
            var status = 0;

            while (position < end)
            {
                position = ReadVariableLength(data, position, out var delta);
                tick += delta;

                int message = data[position];
                if ((message & 0x80) != 0)
                {
                    status = message;
                    position++;
                }
                else
                {
                    // Running status: reuse the previous status byte.
                    message = status;
                }

                if (message == 0xFF)
                {
                    var metaType = data[position];
                    position++;
                    position = ReadVariableLength(data, position, out var metaLength);

                    if (metaType == 0x51 && metaLength >= 3)
                    {
                        var tempo = (data[position] << 16) | (data[position + 1] << 8) | data[position + 2];
                        merged.Add((tick, order++, "tempo", tempo, 0, 0, null));
                    }

                    position += metaLength;
                }
                else if (message is 0xF0 or 0xF7)
                {
                    position = ReadVariableLength(data, position, out var sysExLength);

                    if (message == 0xF0)
                    {
                        // The stored payload omits the leading F0; put it back.
                        var bytes = new byte[sysExLength + 1];
                        bytes[0] = 0xF0;
                        data.Slice(position, sysExLength).CopyTo(bytes.AsSpan(1));
                        merged.Add((tick, order++, "sysex", 0, 0, 0, bytes));
                    }

                    position += sysExLength;
                }
                else
                {
                    var type = message & 0xF0;
                    if (type is 0xC0 or 0xD0)
                    {
                        merged.Add((tick, order++, "channel", message, data[position], 0, null));
                        position += 1;
                    }
                    else
                    {
                        merged.Add((tick, order++, "channel", message, data[position], data[position + 1], null));
                        position += 2;
                    }
                }
            }

            position = end;
        }

        // Merge the tracks by tick, then walk the tempo map to assign absolute time.
        merged.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Order.CompareTo(b.Order));

        var events = new List<MidiEvent>(merged.Count);
        var tempoNow = DefaultTempo;
        long lastTick = 0;
        var seconds = 0.0;

        foreach (var entry in merged)
        {
            seconds += (entry.Tick - lastTick) * (tempoNow / 1e6 / ticksPerQuarter);
            lastTick = entry.Tick;

            switch (entry.Kind)
            {
                case "tempo":
                    tempoNow = entry.Status;
                    break;

                case "sysex":
                    events.Add(new MidiEvent(
                        Quantise(seconds * sampleRate), MidiEventKind.SysEx, 0, 0, 0, entry.Bytes));
                    break;

                default:
                    events.Add(new MidiEvent(
                        Quantise(seconds * sampleRate), MidiEventKind.Channel,
                        entry.Status, entry.Data1, entry.Data2, null));
                    break;
            }
        }

        // A stable sort keeps same-position events in their original tick order.
        return [.. events.OrderBy(e => e.Position)];
    }

    /// <summary>Rounds a sample position down onto the render-block grid.</summary>
    /// <param name="samples">Unquantised position.</param>
    /// <returns>The quantised position.</returns>
    public static long Quantise(double samples) =>
        (long)Math.Round(samples) / BlockGrid * BlockGrid;

    private static int ReadVariableLength(ReadOnlySpan<byte> data, int position, out int value)
    {
        value = 0;
        while (true)
        {
            var b = data[position];
            position++;
            value = (value << 7) | (b & 0x7F);
            if ((b & 0x80) == 0)
            {
                return position;
            }
        }
    }
}
