namespace TabulaSonora.Patches;

/// <summary>
/// One entry of the wave-descriptor table: where a sample lives in the wave ROM, how it is tuned, and
/// how it loops.
/// </summary>
/// <param name="Region">ROM region, 0–127. Bit 4 selects the bank.</param>
/// <param name="Loop">Start of the sample data — delta index zero.</param>
/// <param name="End">The loop point.</param>
/// <param name="Start">The physical end of the data.</param>
/// <param name="RootKey">MIDI note at which the sample plays back untransposed.</param>
/// <param name="FineTune">Fine-tune word; native pitch is <c>RootKey + (1024 − FineTune) / 1000</c>.</param>
/// <param name="Flags">Raw flag byte. Bit 0 is bidirectional, bit 2 is reverse; bit 1 is unused.</param>
/// <remarks>
/// The field names are as recovered and they are genuinely confusing: <see cref="Loop"/> is the data
/// start, <see cref="End"/> is the loop point, and <see cref="Start"/> is the physical end. They are
/// kept rather than renamed so the code lines up with the reverse-engineering notes and the trace
/// dumps.
/// </remarks>
public readonly record struct WaveDescriptor(
    int Region,
    int Loop,
    int End,
    int Start,
    int RootKey,
    int FineTune,
    int Flags)
{
    /// <summary>Bytes per descriptor record.</summary>
    public const int Stride = 0x16;

    /// <summary>ROM bank: 0 for bank A, 1 for bank B.</summary>
    public int Bank => (Region >> 4) & 1;

    /// <summary>True when the sampler plays this wave backwards.</summary>
    public bool Reverse => ((Flags >> 2) & 1) != 0;

    /// <summary>True when the sampler plays this wave bidirectionally (ping-pong).</summary>
    public bool PingPong => (Flags & 1) != 0;

    /// <summary>
    /// Native pitch in semitones — the effective root, including fine tune.
    /// </summary>
    public double NativePitch => RootKey + ((1024.0 - FineTune) / 1000.0);

    /// <summary>Parses a descriptor from its 0x16 raw bytes.</summary>
    /// <param name="record">Exactly <see cref="Stride"/> bytes.</param>
    /// <returns>The parsed descriptor.</returns>
    public static WaveDescriptor Parse(ReadOnlySpan<byte> record)
    {
        // The three position fields are 20-bit, stored most-significant nibble first.
        var loop = ((record[0x01] & 0x0F) << 16) | (record[0x02] << 8) | record[0x03];
        var end = ((record[0x07] & 0x0F) << 16) | (record[0x08] << 8) | record[0x09];
        var start = ((record[0x0B] & 0x0F) << 16) | (record[0x0C] << 8) | record[0x0D];

        return new WaveDescriptor(
            Region: record[0x00] & 0x7F,
            Loop: loop,
            End: end,
            Start: start,
            RootKey: record[0x06],
            FineTune: record[0x04] | (record[0x05] << 8),
            Flags: record[0x0A]);
    }
}

/// <summary>A key range within a multisample and the wave it selects.</summary>
/// <param name="KeyLow">Lowest transposed key in the zone.</param>
/// <param name="KeyHigh">Highest transposed key in the zone.</param>
/// <param name="Wave">Wave number.</param>
public readonly record struct MultisampleZone(int KeyLow, int KeyHigh, int Wave);

/// <summary>One partial that sounds for a given note and velocity.</summary>
/// <param name="PartialIndex">Index within the tone's present partials.</param>
/// <param name="Wave">Selected wave number.</param>
/// <param name="Descriptor">The wave's ROM coordinates.</param>
public readonly record struct ResolvedPartial(int PartialIndex, int Wave, WaveDescriptor Descriptor);
