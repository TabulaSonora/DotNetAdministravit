using System.Buffers.Binary;
using TabulaSonora.Rom;

namespace TabulaSonora.Patches;

/// <summary>One drum key's settings within a kit.</summary>
/// <param name="Tone">Tone number. Drum sounds are ordinary melodic tones.</param>
/// <param name="Level">Kit level for this key. Applies downstream as <c>(level / 127)²</c>.</param>
/// <param name="Pitch">Coarse pitch, 60 being the tone's natural pitch.</param>
/// <param name="Group">Mute/assign group; keys sharing a non-zero group cut each other off.</param>
/// <param name="Pan">Pan position, per key.</param>
public readonly record struct DrumKey(int Tone, int Level, int Pitch, int Group, int Pan);

/// <summary>
/// The drum kit records: for each of 128 keys, which tone sounds and how it is levelled, tuned,
/// panned and grouped.
/// </summary>
/// <remarks>
/// <para>
/// Three things about drums are easy to get wrong, and all three are settled by measurement:
/// </para>
/// <list type="bullet">
/// <item>
/// Drum sounds are ordinary <em>melodic</em> tones. They do not use the separate drum tone table
/// (stride 0x1e8), which is why that table's remaining unreversed state does not block GM kits.
/// </item>
/// <item>
/// They do not resolve through the three-level melodic lookup; a program change on the drum part
/// selects a kit through its own pair of lookups.
/// </item>
/// <item>
/// The note does not transpose the sample. The tone resolves at key 60, and the kit's coarse-pitch
/// plane is then applied at <em>half</em> strength — see <see cref="CoarsePitchRatio"/>.
/// </item>
/// </list>
/// </remarks>
public sealed class DrumKitTable
{
    /// <summary>Bytes per kit record.</summary>
    public const int KitStride = 0x50C;

    /// <summary>Keys per kit.</summary>
    public const int KeyCount = 128;

    /// <summary>Rows in the drum program map.</summary>
    /// <remarks>
    /// <para>
    /// Six, not the two this read for a long time. Each row maps a program to a kit, and program 0
    /// resolves to kit 0, 38, 47, 59, 68 and 79 across rows 0 to 5, with 38, 26, 15, 10, 11 and 9
    /// programs defined in each. Reading two rows sized <see cref="KitCount"/> at 47 and put every
    /// kit from 47 up out of reach entirely.
    /// </para>
    /// <para>
    /// Measured against the DLL, which is how the short read surfaced: driven with the SC-55 tone map
    /// on program 0 of the drum part, the engine resolves a tone for all 61 sounding keys that
    /// <em>kit 59</em> reproduces exactly, 61 of 61 — and kit 59 is only reachable through row 3. The
    /// best any kit the two-row read could see managed was 30 of 61.
    /// </para>
    /// </remarks>
    public const int MapRowCount = 6;

    // Plane offsets within a kit record.
    private const int ToneP1ane = 0x000;   // 128 x u16
    private const int LevelPlane = 0x100;
    private const int PitchPlane = 0x180;
    private const int GroupPlane = 0x200;
    private const int PanPlane = 0x280;

    /// <summary>
    /// File offset of the table mapping a level-two index to a kit record index.
    /// </summary>
    /// <remarks>
    /// Unlike the other three drum regions this one is not recorded in the manifest, so it is pinned
    /// here. It is <c>DAT_1819f31b0</c> in the decompile.
    /// </remarks>
    public const long KitIndexOffset = 0x19F21B0;

    private readonly long _kitBase;
    private readonly byte[] _bankRow;
    private readonly byte[] _programMap;
    private readonly ushort[] _kitIndex;
    private readonly byte[] _kits;

    /// <summary>Creates the table by reading the drum regions out of the ROM image.</summary>
    /// <param name="rom">An open, verified ROM image.</param>
    public DrumKitTable(RomImage rom)
    {
        ArgumentNullException.ThrowIfNull(rom);

        _kitBase = rom.Manifest.Region("drum_kit_records").FileOffset;
        _bankRow = rom.Read(rom.Manifest.Region("drum_bank_row").FileOffset, 256);
        _programMap = rom.Read(rom.Manifest.Region("drum_prog_map").FileOffset, MapRowCount * 0x80);

        var indexBytes = rom.Read(KitIndexOffset, 256 * sizeof(ushort));
        _kitIndex = new ushort[256];
        for (var i = 0; i < _kitIndex.Length; i++)
        {
            _kitIndex[i] = BinaryPrimitives.ReadUInt16LittleEndian(indexBytes.AsSpan(i * 2, 2));
        }

        // Read only as many records as the index can actually reach.
        var highest = 0;
        for (var row = 0; row < MapRowCount; row++)
        {
            for (var program = 0; program < 0x80; program++)
            {
                var level2 = _programMap[(row * 0x80) + program];
                if (level2 != 0xFF)
                {
                    highest = Math.Max(highest, _kitIndex[level2]);
                }
            }
        }

        KitCount = highest + 1;
        _kits = rom.Read(_kitBase, KitCount * KitStride);
    }

    /// <summary>Number of kit records reachable through the program map.</summary>
    public int KitCount { get; }

    /// <summary>The drum map row a vintage's tone map selects.</summary>
    /// <param name="map">The tone map.</param>
    /// <returns>The row, or <see langword="null"/> where no measurement pins one.</returns>
    /// <remarks>
    /// <para>
    /// The module takes this from the part's internal bank code, and that translation is not
    /// reversed — but which row each vintage ends up on is observable, so it is measured here rather
    /// than left to the host to guess. Driving the DLL with each tone map in turn and reading back
    /// the tone it resolves for program 0 on the drum part:
    /// </para>
    /// <list type="bullet">
    /// <item><description>SC-55 selects row 3, kit 59 — the only row whose kit carries tones 2124,
    /// 2116 and 2331 on keys 55, 46 and 41.</description></item>
    /// <item><description>SC-88 selects row 2, kit 47, likewise uniquely.</description></item>
    /// <item><description>SC-88Pro row 1 and SC-8820 row 0, which the browser build had already
    /// established by matching each row's set of defined programs against each vintage's kit
    /// list.</description></item>
    /// </list>
    /// <para>
    /// Rows 4 and 5 exist and no vintage selects them, so nothing here reaches them; a host that
    /// wants them has to set the row itself.
    /// </para>
    /// </remarks>
    public static int? RowForMap(ToneMap map) => map switch
    {
        ToneMap.Sc55 => 3,
        ToneMap.Sc88 => 2,
        ToneMap.Sc88Pro => 1,
        ToneMap.Sc8820 => 0,
        _ => null,
    };

    /// <summary>Maps an internal bank code to a drum map row.</summary>
    /// <param name="internalBank">Internal bank code; standard GM/GS drum parts use 0x04.</param>
    /// <returns>The map row — 0 for map A, 1 for map B.</returns>
    public int MapRow(int internalBank) => _bankRow[internalBank & 0xFF];

    /// <summary>
    /// Resolves a drum program to its kit record index.
    /// </summary>
    /// <param name="program">Program number on the drum part.</param>
    /// <param name="row">Map row, 0 to <see cref="MapRowCount"/> minus one; 0 is the GM/GS map.</param>
    /// <returns>
    /// The kit index, or <see langword="null"/> for an undefined program — the engine leaves the kit
    /// unchanged rather than silencing the part.
    /// </returns>
    public int? KitForProgram(int program, int row = 0)
    {
        if ((uint)row >= MapRowCount)
        {
            return null;
        }

        var level2 = _programMap[(row * 0x80) + (program & 0x7F)];
        return level2 == 0xFF ? null : _kitIndex[level2];
    }

    /// <summary>Reads one key's settings from a kit.</summary>
    /// <param name="note">MIDI note number.</param>
    /// <param name="kit">Kit record index; 0 is GM Standard.</param>
    /// <returns>The key's settings.</returns>
    public DrumKey Key(int note, int kit = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(note);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(note, KeyCount);

        var offset = kit * KitStride;
        return new DrumKey(
            Tone: BinaryPrimitives.ReadUInt16LittleEndian(_kits.AsSpan(offset + ToneP1ane + (note * 2), 2)),
            Level: _kits[offset + LevelPlane + note],
            Pitch: _kits[offset + PitchPlane + note],
            Group: _kits[offset + GroupPlane + note],
            Pan: _kits[offset + PanPlane + note]);
    }

    /// <summary>
    /// The playback ratio the kit's coarse-pitch plane implies.
    /// </summary>
    /// <param name="pitch">The key's coarse-pitch byte, 60 being natural.</param>
    /// <returns>The frequency ratio.</returns>
    /// <remarks>
    /// Applied at <em>half</em> strength — 50 cents per unit, not a whole semitone. Measured against
    /// the engine: keys sharing a tone (41 and 43, 45 and 47) come out <c>2^(offset/24)</c> apart,
    /// so +6 gives 1.189× rather than the 1.414× a semitone-per-unit reading would predict.
    /// </remarks>
    public static double CoarsePitchRatio(int pitch) => Math.Pow(2.0, (pitch - 60) / 24.0);

    /// <summary>
    /// The gain the kit level contributes, as amplitude.
    /// </summary>
    /// <param name="level">The key's kit level.</param>
    /// <returns>The linear gain.</returns>
    /// <remarks>
    /// The kit level is <em>not</em> part of the per-voice amplitude — that matches the engine
    /// exactly without it. It enters downstream in the part-volume computation, where the result is
    /// squared, so it acts as <c>(level / 127)²</c>.
    /// </remarks>
    public static double LevelGain(int level) => (level / 127.0) * (level / 127.0);
}
