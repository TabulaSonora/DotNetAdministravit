namespace TabulaSonora.Dsp;

/// <summary>
/// The engine's shared pseudo-random generator.
/// </summary>
/// <remarks>
/// <para>
/// The original keeps two 16-bit registers, seeded at engine reset to <c>0xEFA6</c> and
/// <c>0x9C23</c>. The first is a right-shifting Fibonacci LFSR whose new bit 15 is bit 5 XOR
/// bit 15; the second shifts left and inserts bit 9 when bit 2 is clear, or the complement of
/// bit 13 when bit 2 is set. A draw steps both and returns their XOR.
/// </para>
/// <para>
/// The sequence is therefore deterministic from engine reset; what polyphony changes is only the
/// order in which voices consume draws. The generator is shared by the random LFO waveforms, the
/// pitch start jitter, and the random detune redraw.
/// </para>
/// </remarks>
public sealed class EngineNoise
{
    private ushort _shift = 0xEFA6;
    private ushort _walk = 0x9C23;

    /// <summary>Draws the next 16-bit value.</summary>
    /// <returns>The draw.</returns>
    public ushort Next()
    {
        var next = (ushort)(_shift >> 1);
        if (((_shift & 0x20) != 0) != ((_shift & 0x8000) != 0))
        {
            next |= 0x8000;
        }

        _shift = next;

        var insertOne = (_walk & 4) != 0 ? (_walk & 0x2000) == 0 : (_walk & 0x200) != 0;
        _walk = (ushort)((_walk << 1) | (insertOne ? 1 : 0));

        return (ushort)(_walk ^ _shift);
    }

    /// <summary>
    /// Draws a random pan position, 0 to 127.
    /// </summary>
    /// <returns>The position, on the same scale as CC#10.</returns>
    /// <remarks>
    /// The engine takes the top seven bits of a draw. This is what GS random pan resolves to — a
    /// part whose CC#10 is zero, or a drum key whose pan NRPN is zero, is repositioned per note.
    /// </remarks>
    public int NextPan() => Next() >> 9;

    /// <summary>Returns the generator to the engine's reset state.</summary>
    public void Reset()
    {
        _shift = 0xEFA6;
        _walk = 0x9C23;
    }
}
