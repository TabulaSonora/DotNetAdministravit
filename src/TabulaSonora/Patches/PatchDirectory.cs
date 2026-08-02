using System.Text;
using TabulaSonora.Rom;

namespace TabulaSonora.Patches;

/// <summary>Which vintage's tone map a program change resolves against.</summary>
public enum ToneMap
{
    /// <summary>SC-55.</summary>
    Sc55 = 1,

    /// <summary>SC-88.</summary>
    Sc88 = 2,

    /// <summary>SC-88Pro.</summary>
    Sc88Pro = 3,

    /// <summary>SC-8820.</summary>
    Sc8820 = 4,
}

/// <summary>One key zone of a tone, expressed in MIDI note numbers.</summary>
/// <param name="PartialIndex">Index within the tone's present partials.</param>
/// <param name="VelocityLow">Low edge of the partial's velocity window.</param>
/// <param name="VelocityHigh">High edge of the partial's velocity window.</param>
/// <param name="KeyLow">Lowest MIDI note in the zone.</param>
/// <param name="KeyHigh">Highest MIDI note in the zone.</param>
/// <param name="Wave">Wave number.</param>
/// <param name="Descriptor">The wave's ROM coordinates.</param>
public readonly record struct ToneZone(
    int PartialIndex,
    int VelocityLow,
    int VelocityHigh,
    int KeyLow,
    int KeyHigh,
    int Wave,
    WaveDescriptor Descriptor);

/// <summary>An entry of the alternate-articulation table.</summary>
/// <param name="Name">The entry's ASCII name.</param>
/// <param name="Threshold">Inter-note timing threshold that gates the alternate.</param>
/// <param name="Primary">The tone reference that normally sounds.</param>
/// <param name="Alternate">The alternate articulation, reachable only in mono/solo mode.</param>
public readonly record struct AlternateEntry(
    string Name,
    int Threshold,
    ProgramReference Primary,
    ProgramReference Alternate);

/// <summary>A (map, bank, program) triple that resolves through the same three-level lookup.</summary>
/// <param name="Map">Tone map.</param>
/// <param name="Bank">Bank select MSB.</param>
/// <param name="Program">Program number.</param>
public readonly record struct ProgramReference(int Map, int Bank, int Program);

/// <summary>
/// The static patch directory: resolves a program change and a played note to the waves that sound,
/// entirely from ROM tables with no engine involved.
/// </summary>
/// <remarks>
/// The chain is <c>(map, bank, program) → tone# → partial → multisample → key/velocity zone → wave#
/// → ROM coordinates</c>. The program lookup is three levels deep and its result carries three
/// distinct tone spaces, which is the part most likely to be got wrong.
/// </remarks>
public sealed class PatchDirectory
{
    /// <summary>Highest tone number in the melodic space, exclusive.</summary>
    public const int MelodicSpaceEnd = 0x4000;

    /// <summary>First tone number in the drum space.</summary>
    public const int DrumSpaceStart = 0x4000;

    /// <summary>First tone number in the alternate-articulation space.</summary>
    public const int AlternateSpaceStart = 0x6000;

    /// <summary>Tone numbers at or above this are reachable only indirectly.</summary>
    public const int IndirectOnlyFlag = 0x8000;

    /// <summary>Bytes per multisample record.</summary>
    public const int MultisampleStride = 0x8C;

    /// <summary>Bytes per alternate-articulation record.</summary>
    public const int AlternateStride = 0x18;

    /// <summary>Value meaning "unassigned" in the third lookup level.</summary>
    public const int Unassigned = 0xFFFF;

    private readonly TableSet _tables;
    private readonly byte[] _tone;
    private readonly byte[] _multisample;
    private readonly byte[] _wavedesc;
    private readonly byte[] _layered;

    /// <summary>Creates a directory over a loaded table set.</summary>
    /// <param name="tables">The static tables.</param>
    public PatchDirectory(TableSet tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        _tables = tables;
        _tone = tables.Tone;
        _multisample = tables.Multisample;
        _wavedesc = tables.Wavedesc;
        _layered = tables.Layered;

        ToneCount = _tone.Length / Patches.Tone.Stride;
        MultisampleCount = _multisample.Length / MultisampleStride;
        WaveCount = _wavedesc.Length / WaveDescriptor.Stride;
        AlternateCount = _layered.Length / AlternateStride;
    }

    /// <summary>Number of tone records in the melodic table.</summary>
    public int ToneCount { get; }

    /// <summary>Number of multisample records.</summary>
    public int MultisampleCount { get; }

    /// <summary>Number of wave descriptors.</summary>
    public int WaveCount { get; }

    /// <summary>Number of alternate-articulation records.</summary>
    public int AlternateCount { get; }

    /// <summary>Reads a wave descriptor.</summary>
    /// <param name="waveNumber">Wave number.</param>
    /// <returns>The descriptor, or <see langword="null"/> if the number is out of range.</returns>
    public WaveDescriptor? GetWave(int waveNumber)
    {
        if (waveNumber < 0 || waveNumber >= WaveCount)
        {
            return null;
        }

        return WaveDescriptor.Parse(
            _wavedesc.AsSpan(waveNumber * WaveDescriptor.Stride, WaveDescriptor.Stride));
    }

    /// <summary>Reads a tone record.</summary>
    /// <param name="toneNumber">Tone number.</param>
    /// <returns>The tone, or <see langword="null"/> if the number is out of range.</returns>
    public Tone? GetTone(int toneNumber) =>
        toneNumber < 0 || toneNumber >= ToneCount ? null : Tone.Read(_tone, toneNumber);

    /// <summary>Reads a tone's master level from its header.</summary>
    /// <param name="toneNumber">Tone number.</param>
    /// <returns>The level byte.</returns>
    public int ToneLevel(int toneNumber) => _tone[(toneNumber * Patches.Tone.Stride) + 0x0C];

    /// <summary>Whether a tone responds to half-damper (continuous CC64).</summary>
    /// <param name="toneNumber">Tone number.</param>
    /// <returns>Whether the pedal value reaches the release rate unquantised.</returns>
    /// <remarks>
    /// Tone header byte 0x0d bit 2, copied to the part on tone select. Set on exactly 57 of the
    /// 2363 tones — the piano family — matching the hardware, where half-pedal is a piano feature.
    /// Every other tone's pedal is quantised to fully up or fully down.
    /// </remarks>
    public bool HalfDamper(int toneNumber) =>
        toneNumber >= 0 && toneNumber < ToneCount
        && (_tone[(toneNumber * Patches.Tone.Stride) + 0x0D] & 4) != 0;

    /// <summary>
    /// Reads a partial block by its <em>slot</em> rather than its position among the present partials.
    /// </summary>
    /// <param name="toneNumber">Tone number.</param>
    /// <param name="slot">Slot index, 0 or 1 for a melodic tone.</param>
    /// <returns>The block, which may be an empty slot — check <see cref="PartialParameters.IsPresent"/>.</returns>
    /// <remarks>
    /// <see cref="Patches.Tone.Partials"/> drops empty slots, so its indices are not slot numbers.
    /// Anything correlating against the reference or a voice dump needs the slot.
    /// </remarks>
    public PartialParameters GetPartialBySlot(int toneNumber, int slot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, Patches.Tone.PartialSlots);

        var offset = (toneNumber * Patches.Tone.Stride)
            + Patches.Tone.HeaderSize
            + (slot * PartialParameters.Stride);
        return new PartialParameters(_tone, offset);
    }

    /// <summary>
    /// Selects the wave a multisample yields for a transposed key.
    /// </summary>
    /// <param name="multisample">Multisample index.</param>
    /// <param name="transposedKey">Key after the partial transpose and key-centre shift.</param>
    /// <returns>The wave number, or <see langword="null"/> when the partial does not sound.</returns>
    /// <remarks>
    /// An empty <em>bottom</em> zone means the note is below the multisample's sampled range, and the
    /// partial is silent — it must not fall through to the neighbouring wave. The engine does
    /// statically down-extrapolate, but the resulting voice is terminated, so the audible result is
    /// silence. Slap Bass 2 below note 40 is the case that proved it.
    /// </remarks>
    public int? MultisampleWave(int multisample, int transposedKey)
    {
        if (multisample < 0 || multisample >= MultisampleCount)
        {
            return null;
        }

        var record = _multisample.AsSpan(multisample * MultisampleStride, MultisampleStride);

        var zone = 0x1F;
        for (var z = 0; z < 0x20; z++)
        {
            var bound = record[0x0C + z];
            if (transposedKey <= bound || bound >= 0x7F)
            {
                zone = z;
                break;
            }
        }

        var wave = ReadInt16(record, 0x2C + (zone * 2));
        if (wave < 0 && zone == 0)
        {
            return null;
        }

        if (wave < 0)
        {
            wave = ReadInt16(record, 0x2E + (zone * 2));
        }

        if (wave < 0)
        {
            wave = ReadInt16(record, 0x6A);
        }

        return wave >= 0 ? wave : null;
    }

    /// <summary>
    /// Reads the per-key-zone level, the second attenuation in the TVA base-level chain.
    /// </summary>
    /// <param name="multisample">Multisample index.</param>
    /// <param name="key">Key before the key-centre shift.</param>
    /// <param name="keyCenter">The partial's key centre.</param>
    /// <returns>The level byte.</returns>
    public int ZoneLevel(int multisample, int key, int keyCenter)
    {
        if (multisample < 0 || multisample >= MultisampleCount)
        {
            return 127;
        }

        var record = _multisample.AsSpan(multisample * MultisampleStride, MultisampleStride);
        var transposedKey = Math.Clamp(key + (0x40 - keyCenter), 0, 0x7F);

        // 0x2b is the last key bound; past it, the tail entry applies.
        if (record[0x2B] < transposedKey)
        {
            return record[0x8B];
        }

        var z = 0;
        while (record[0x0C + z] < transposedKey)
        {
            z++;
        }

        if (ReadInt16(record, 0x2C + (z * 2)) < 0)
        {
            // No direct wave: the level comes from the velocity-alternate plane instead.
            if (z == 0)
            {
                return record[0x6D + z];
            }

            return record[0x0C + z] == 0x7F ? record[0x6B + z] : 127;
        }

        return record[0x6C + z];
    }

    /// <summary>Lists a multisample's key zones in transposed-key space.</summary>
    /// <param name="multisample">Multisample index.</param>
    /// <returns>The zones, low to high.</returns>
    public IReadOnlyList<MultisampleZone> MultisampleZones(int multisample)
    {
        if (multisample < 0 || multisample >= MultisampleCount)
        {
            return [];
        }

        var record = _multisample.AsSpan(multisample * MultisampleStride, MultisampleStride);
        var zones = new List<MultisampleZone>(8);
        var previous = 0;

        for (var z = 0; z < 0x20; z++)
        {
            var bound = record[0x0C + z];
            var wave = ReadInt16(record, 0x2C + (z * 2));
            if (wave < 0)
            {
                wave = ReadInt16(record, 0x2E + (z * 2));
            }

            if (wave >= 0)
            {
                zones.Add(new MultisampleZone(previous, bound, wave));
            }

            previous = bound + 1;
            if (bound >= 0x7F)
            {
                break;
            }
        }

        return zones;
    }

    /// <summary>
    /// Builds the full static zone map for a tone, with key bounds converted back to MIDI notes.
    /// </summary>
    /// <param name="toneNumber">Tone number.</param>
    /// <param name="zones">Receives the zones.</param>
    /// <returns>The tone's name, or <see langword="null"/> when the tone is undefined.</returns>
    /// <remarks>
    /// This is the static map, so it applies only the key centre — not the partial's key transpose,
    /// which belongs to the played-note path in <see cref="Resolve"/>.
    /// </remarks>
    public string? ToneZones(int toneNumber, out IReadOnlyList<ToneZone> zones)
    {
        zones = [];
        var tone = GetTone(toneNumber);
        if (tone is null || !tone.IsDefined)
        {
            return null;
        }

        var result = new List<ToneZone>();
        for (var partialIndex = 0; partialIndex < tone.Partials.Count; partialIndex++)
        {
            var partial = tone.Partials[partialIndex];
            var keyCenter = partial.KeyCenter;
            var (velocityLow, velocityHigh) = partial.VelocityWindow;

            foreach (var zone in MultisampleZones(partial.Multisample))
            {
                var noteLow = Math.Max(0, zone.KeyLow + keyCenter - 0x40);
                var noteHigh = Math.Min(127, zone.KeyHigh + keyCenter - 0x40);
                if (noteHigh < noteLow)
                {
                    continue;
                }

                var descriptor = GetWave(zone.Wave);
                if (descriptor is null)
                {
                    continue;
                }

                result.Add(new ToneZone(
                    partialIndex, velocityLow, velocityHigh, noteLow, noteHigh, zone.Wave, descriptor.Value));
            }
        }

        zones = result;
        return tone.Name;
    }

    /// <summary>
    /// The raw unsigned word the three-level program lookup yields.
    /// </summary>
    /// <param name="program">Program number, 0–127.</param>
    /// <param name="map">Tone map.</param>
    /// <param name="bank">Bank select MSB.</param>
    /// <returns>The word, or <see langword="null"/> if any level is unassigned.</returns>
    /// <remarks>
    /// It must be read as <em>unsigned</em>. Reading it signed and bailing on the sign bit is what
    /// once made four string patches look unassigned — the 0x8000 bit marks a tone reachable only
    /// through an alternate-articulation entry, not a missing one.
    /// </remarks>
    public int? Lut3Raw(int program, ToneMap map, int bank)
    {
        var mapIndex = (int)map;
        if (mapIndex < 0 || mapIndex >= _tables.DirLut1.Length)
        {
            return null;
        }

        var level1 = _tables.DirLut1[mapIndex];
        if (level1 == 0xFF)
        {
            return null;
        }

        var index2 = (level1 * 0x80) + bank;
        if (index2 >= _tables.DirLut2.Length || _tables.DirLut2[index2] == 0xFF)
        {
            return null;
        }

        var index3 = (_tables.DirLut2[index2] * 0x80) + program;
        if (index3 < 0 || index3 >= _tables.DirLut3.Length)
        {
            return null;
        }

        return _tables.DirLut3[index3];
    }

    /// <summary>
    /// The three-level lookup with the GS capital-tone fallback applied.
    /// </summary>
    /// <param name="program">Program number, 0–127.</param>
    /// <param name="map">Tone map.</param>
    /// <param name="bank">Bank select MSB.</param>
    /// <returns>The word, or <see langword="null"/> if even the capital tone is unassigned.</returns>
    /// <remarks>
    /// A bank select picks a variation. When the selected variation has no entry for the program, a
    /// Sound Canvas does not fall silent — it sounds the capital tone at bank 0 instead. Reproducing
    /// that here is what lets a file play a note on a variation bank the ROM never filled in, which is
    /// exactly what one honky-tonk part in passport.mid does: it selects bank 5, whose program-3 slot
    /// is empty, and every note would otherwise be dropped.
    /// </remarks>
    public int? Lut3Resolved(int program, ToneMap map, int bank)
    {
        var raw = Lut3Raw(program, map, bank);
        if (bank != 0 && (raw is null || raw == Unassigned))
        {
            raw = Lut3Raw(program, map, 0);
        }

        return raw;
    }

    /// <summary>Decodes an entry of the alternate-articulation table.</summary>
    /// <param name="index">Entry index, i.e. <c>tone# − 0x6000</c>.</param>
    /// <returns>The entry, or <see langword="null"/> if out of range.</returns>
    public AlternateEntry? Alternate(int index)
    {
        if (index < 0 || index >= AlternateCount)
        {
            return null;
        }

        var record = _layered.AsSpan(index * AlternateStride, AlternateStride);
        var name = Encoding.Latin1.GetString(record[..10]).TrimEnd();

        return new AlternateEntry(
            name,
            record[0x0E],
            new ProgramReference(record[0x10], record[0x11], record[0x12]),
            new ProgramReference(record[0x14], record[0x15], record[0x16]));
    }

    /// <summary>
    /// Resolves a program change to the tone numbers that sound for an ordinary poly note.
    /// </summary>
    /// <param name="program">Program number.</param>
    /// <param name="map">Tone map.</param>
    /// <param name="bank">Bank select MSB.</param>
    /// <returns>Zero or one tone numbers.</returns>
    /// <remarks>
    /// Returned as a list because the caller walks it, but an alternate-articulation entry's two
    /// references are <em>not</em> layers played together: the second is a conditional articulation
    /// that only the mono/solo path can reach.
    /// </remarks>
    public IReadOnlyList<int> ProgramTones(int program, ToneMap map, int bank)
    {
        var raw = Lut3Resolved(program, map, bank);
        if (raw is null || raw == Unassigned || raw >= IndirectOnlyFlag)
        {
            return [];
        }

        if (raw < AlternateSpaceStart)
        {
            return [raw.Value];
        }

        var entry = Alternate(raw.Value - AlternateSpaceStart);
        if (entry is null)
        {
            return [];
        }

        var tone = Dereference(entry.Value.Primary);
        return tone >= 0 ? [tone] : [];
    }

    /// <summary>
    /// The mono/solo alternate-articulation tone for a program.
    /// </summary>
    /// <param name="program">Program number.</param>
    /// <param name="map">Tone map.</param>
    /// <param name="bank">Bank select MSB.</param>
    /// <returns>The tone number, or -1.</returns>
    /// <remarks>
    /// Not rendered: reproducing it needs the part's mono-mode flag and the inter-note timing test.
    /// </remarks>
    public int AlternateTone(int program, ToneMap map, int bank)
    {
        var raw = Lut3Resolved(program, map, bank);
        if (raw is null || raw == Unassigned || raw >= IndirectOnlyFlag || raw < AlternateSpaceStart)
        {
            return -1;
        }

        var entry = Alternate(raw.Value - AlternateSpaceStart);
        return entry is null ? -1 : Dereference(entry.Value.Alternate);
    }

    /// <summary>Resolves a program change to a single tone number.</summary>
    /// <param name="program">Program number.</param>
    /// <param name="map">Tone map.</param>
    /// <param name="bank">Bank select MSB.</param>
    /// <returns>The tone number, or -1 when unassigned.</returns>
    public int ProgramToTone(int program, ToneMap map, int bank)
    {
        var tones = ProgramTones(program, map, bank);
        return tones.Count > 0 ? tones[0] : -1;
    }

    /// <summary>Resolves a played note against a tone.</summary>
    /// <param name="toneNumber">Tone number.</param>
    /// <param name="note">MIDI note.</param>
    /// <param name="velocity">MIDI velocity.</param>
    /// <returns>The tone name and the partials that sound.</returns>
    public ResolvedTone Resolve(int toneNumber, int note, int velocity)
    {
        var tone = GetTone(toneNumber);
        if (tone is null || !tone.IsDefined)
        {
            return new ResolvedTone("(none)", []);
        }

        var sounding = new List<ResolvedPartial>(2);
        for (var partialIndex = 0; partialIndex < tone.Partials.Count; partialIndex++)
        {
            var partial = tone.Partials[partialIndex];
            if (!partial.AcceptsVelocity(velocity))
            {
                continue;
            }

            var wave = MultisampleWave(partial.Multisample, partial.TransposedKey(note));
            if (wave is null)
            {
                continue;
            }

            var descriptor = GetWave(wave.Value);
            if (descriptor is null)
            {
                continue;
            }

            sounding.Add(new ResolvedPartial(partialIndex, wave.Value, descriptor.Value));
        }

        return new ResolvedTone(tone.Name, sounding);
    }

    /// <summary>Resolves a MIDI program change and played note in one step.</summary>
    /// <param name="program">Program number.</param>
    /// <param name="note">MIDI note.</param>
    /// <param name="velocity">MIDI velocity.</param>
    /// <param name="map">Tone map.</param>
    /// <param name="bank">Bank select MSB.</param>
    /// <returns>The tone name and the partials that sound.</returns>
    public ResolvedTone ResolveMidi(int program, int note, int velocity, ToneMap map, int bank)
    {
        var tone = ProgramToTone(program, map, bank);
        return tone < 0 ? new ResolvedTone("(unassigned)", []) : Resolve(tone, note, velocity);
    }

    private int Dereference(ProgramReference reference)
    {
        var raw = Lut3Raw(reference.Program, (ToneMap)reference.Map, reference.Bank);
        if (raw is null || raw == Unassigned)
        {
            return -1;
        }

        // Strip the indirect-only marker; the program-change handler does the same.
        var tone = raw.Value & 0x7FFF;
        return tone < MelodicSpaceEnd ? tone : -1;
    }

    private static int ReadInt16(ReadOnlySpan<byte> record, int offset) =>
        (short)(record[offset] | (record[offset + 1] << 8));
}
