namespace TabulaSonora.Patches;

/// <summary>
/// A zero-copy typed view over one 0x6e-byte partial parameter block inside a tone record.
/// </summary>
/// <remarks>
/// <para>
/// A melodic tone holds two of these at <c>toneBase + 0x24</c>. The block carries everything the
/// synthesis chain needs: the multisample and key mapping, the pitch/TVF/TVA envelopes and their
/// key-follow rows, the two LFO depth sets, and the velocity window.
/// </para>
/// <para>
/// Only the fields the patch directory needs are surfaced so far; the envelope and filter fields are
/// added as the DSP milestones land. Offsets are block-relative and match the reverse-engineering
/// notes directly.
/// </para>
/// </remarks>
public readonly struct PartialParameters : IEquatable<PartialParameters>
{
    /// <summary>Bytes per partial block.</summary>
    public const int Stride = 0x6E;

    /// <summary>Value of <see cref="Multisample"/> meaning the partial is absent.</summary>
    public const int NoMultisample = 0xFFFF;

    private readonly byte[] _source;
    private readonly int _offset;

    /// <summary>Creates a view over a partial block.</summary>
    /// <param name="source">Backing array, normally the whole tone table.</param>
    /// <param name="offset">Offset of the block's first byte.</param>
    public PartialParameters(byte[] source, int offset)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _offset = offset;
    }

    /// <summary>The block's raw bytes.</summary>
    public ReadOnlySpan<byte> Raw => _source.AsSpan(_offset, Stride);

    /// <summary>Offset of this block within its backing array.</summary>
    public int Offset => _offset;

    /// <summary>True when this slot holds a real partial.</summary>
    public bool IsPresent => _source is not null && Multisample != NoMultisample;

    /// <summary>Multisample index, or <see cref="NoMultisample"/> when the slot is empty. Block +0x02.</summary>
    public int Multisample => U16(0x02);

    /// <summary>Key centre — the origin the multisample's key zones are measured from. Block +0x04.</summary>
    public int KeyCenter => _source[_offset + 0x04];

    /// <summary>Per-partial pan, 0x40 centre. Block +0x0f.</summary>
    public int Pan => _source[_offset + 0x0F];

    /// <summary>Key transpose in whole semitones, 0x40 neutral. Block +0x10.</summary>
    public int KeyTranspose => _source[_offset + 0x10];

    /// <summary>Raw coarse-tune byte, 0x40 neutral. Block +0x11.</summary>
    public int CoarseTuneRaw => _source[_offset + 0x11];

    /// <summary>Coarse tune in milli-semitones: <c>(raw − 0x40) × 10</c>. Block +0x11.</summary>
    public int CoarseTuneMilliSemitones => (CoarseTuneRaw - 0x40) * 10;

    /// <summary>Pitch key-follow amount: 0x40 is 0%, 0x4a is 100%. Block +0x13.</summary>
    public int PitchKeyFollow => _source[_offset + 0x13];

    /// <summary>TVF cutoff base, scaled by 0x100 into the runtime domain. Block +0x2f.</summary>
    public int CutoffBase => _source[_offset + 0x2F];

    /// <summary>TVF resonance, 0x40 neutral. Block +0x30.</summary>
    public int Resonance => _source[_offset + 0x30];

    /// <summary>TVF filter type selector. Block +0x31.</summary>
    public int FilterType => _source[_offset + 0x31];

    /// <summary>Velocity-crossfade curve selector; only the low nibble is used. Block +0x4e.</summary>
    public int VelocityCrossfadeCurve => _source[_offset + 0x4E] & 0x0F;

    /// <summary>One edge of the velocity window. Block +0x4f.</summary>
    public int VelocityLow => _source[_offset + 0x4F];

    /// <summary>The other edge of the velocity window. Block +0x51.</summary>
    public int VelocityHigh => _source[_offset + 0x51];

    /// <summary>Partial level at the <see cref="VelocityLow"/> edge. Block +0x50.</summary>
    public int LevelAtLow => _source[_offset + 0x50];

    /// <summary>Partial level at the <see cref="VelocityHigh"/> edge. Block +0x52.</summary>
    public int LevelAtHigh => _source[_offset + 0x52];

    /// <summary>TVA base level. Block +0x53.</summary>
    public int TvaBaseLevel => _source[_offset + 0x53];

    /// <summary>
    /// The velocity window as an ordered range. The stored edges may be the other way round, which is
    /// how a partial fades in from the top of the window rather than the bottom.
    /// </summary>
    public (int Low, int High) VelocityWindow =>
        VelocityLow <= VelocityHigh ? (VelocityLow, VelocityHigh) : (VelocityHigh, VelocityLow);

    /// <summary>True when a velocity falls inside this partial's window.</summary>
    /// <param name="velocity">MIDI velocity.</param>
    /// <returns>Whether the partial sounds.</returns>
    public bool AcceptsVelocity(int velocity)
    {
        var (low, high) = VelocityWindow;
        return velocity >= low && velocity <= high;
    }

    /// <summary>
    /// The key handed to the multisample zone lookup.
    /// </summary>
    /// <param name="note">MIDI note number.</param>
    /// <returns>The transposed key, clamped to 0–127.</returns>
    /// <remarks>
    /// The engine shifts the voice's key by the partial transpose before the multisample sees it, and
    /// the multisample then adds <c>0x40 − keyCenter</c> itself. Skipping the transpose selects a wave
    /// from the wrong zone, which the pitch chain then stretches to compensate — audible aliasing.
    /// </remarks>
    public int TransposedKey(int note)
    {
        var key = note + (KeyTranspose - 0x40) + (0x40 - KeyCenter);
        return Math.Clamp(key, 0, 0x7F);
    }

    private int U16(int offset) => _source[_offset + offset] | (_source[_offset + offset + 1] << 8);

    /// <inheritdoc/>
    public bool Equals(PartialParameters other) =>
        ReferenceEquals(_source, other._source) && _offset == other._offset;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is PartialParameters other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_source?.Length ?? 0, _offset);

    /// <summary>Compares two views for identity.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns>Whether both refer to the same block.</returns>
    public static bool operator ==(PartialParameters left, PartialParameters right) => left.Equals(right);

    /// <summary>Compares two views for non-identity.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns>Whether they refer to different blocks.</returns>
    public static bool operator !=(PartialParameters left, PartialParameters right) => !left.Equals(right);
}
