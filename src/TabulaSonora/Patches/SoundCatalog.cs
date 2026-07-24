namespace TabulaSonora.Patches;

/// <summary>What a (map, bank, program) slot actually is.</summary>
public enum CatalogEntryKind
{
    /// <summary>The bank defines this program itself, and a program change to it sounds that tone.</summary>
    Native,

    /// <summary>
    /// The bank leaves this program empty, so the module sounds bank 0's capital tone instead.
    /// </summary>
    CapitalFallback,

    /// <summary>
    /// Defined, but marked reachable only through an alternate-articulation entry. A program change
    /// to it sounds nothing.
    /// </summary>
    IndirectOnly,

    /// <summary>Nothing here, and no capital tone behind it either.</summary>
    Unassigned,
}

/// <summary>One program slot of one bank.</summary>
/// <param name="Program">Program number, 0–127.</param>
/// <param name="Tone">The tone number that sounds, or -1.</param>
/// <param name="Name">The tone's name, or a dash where nothing is named.</param>
/// <param name="Kind">What the slot is.</param>
public readonly record struct CatalogEntry(int Program, int Tone, string Name, CatalogEntryKind Kind)
{
    /// <summary>Whether a program change to this slot makes a sound.</summary>
    public bool Playable => Kind is CatalogEntryKind.Native or CatalogEntryKind.CapitalFallback;
}

/// <summary>One variation bank of one vintage, as all 128 of its program slots.</summary>
/// <param name="Bank">Bank select MSB.</param>
/// <param name="Programs">The 128 slots, in program order.</param>
/// <param name="NativeCount">How many of them the bank defines itself.</param>
public sealed record CatalogBank(int Bank, IReadOnlyList<CatalogEntry> Programs, int NativeCount);

/// <summary>Every bank one vintage defines.</summary>
/// <param name="Map">The vintage.</param>
/// <param name="Banks">The banks that define at least one program, in bank order.</param>
/// <param name="NativeCount">How many program slots are defined across all of them.</param>
/// <param name="ToneCount">How many distinct tones those slots reach.</param>
public sealed record VintageCatalog(
    ToneMap Map,
    IReadOnlyList<CatalogBank> Banks,
    int NativeCount,
    int ToneCount);

/// <summary>One key of one drum kit.</summary>
/// <param name="Note">MIDI note number.</param>
/// <param name="Tone">The melodic tone the key sounds.</param>
/// <param name="Name">That tone's name.</param>
/// <param name="Level">Kit level for the key.</param>
/// <param name="Pitch">Coarse pitch, 60 being the tone's natural pitch.</param>
/// <param name="Group">Mute/assign group; keys sharing a non-zero group cut each other off.</param>
/// <param name="Pan">Pan position.</param>
public readonly record struct DrumKeyEntry(
    int Note,
    int Tone,
    string Name,
    int Level,
    int Pitch,
    int Group,
    int Pan);

/// <summary>One drum kit, and the programs that select it.</summary>
/// <param name="Kit">Kit record index.</param>
/// <param name="Programs">Every program on the drum part that resolves to this kit, in order.</param>
/// <param name="Keys">The keys that sound, low to high.</param>
public sealed record DrumKitEntry(int Kit, IReadOnlyList<int> Programs, IReadOnlyList<DrumKeyEntry> Keys);

/// <summary>Every kit one drum map row reaches.</summary>
/// <param name="Row">Map row — 0 is the GM/GS map.</param>
/// <param name="Kits">The kits, ordered by the first program that selects each.</param>
public sealed record DrumCatalog(int Row, IReadOnlyList<DrumKitEntry> Kits);

/// <summary>
/// The whole sound set of a ROM, enumerated: every bank and program a vintage defines, and every kit
/// a drum map row reaches.
/// </summary>
/// <remarks>
/// <para>
/// Every answer comes out of <see cref="PatchDirectory"/> and <see cref="DrumKitTable"/> by the same
/// calls the engine makes when a program change arrives, so there is no second lookup path to drift
/// out of step with the first. What this adds is only the sweep the engine never does: it asks about
/// one program at a time, and this asks about all of them.
/// </para>
/// <para>
/// A vintage costs 16 384 lookups over byte arrays already in memory. That is cheap, but it is not
/// free, and nothing here caches — a caller showing one vintage at a time should hold on to what it
/// built.
/// </para>
/// </remarks>
public static class SoundCatalog
{
    /// <summary>How many drum map rows the program map holds.</summary>
    /// <remarks>Row 0 is the GM/GS map; row 1 is the second.</remarks>
    public const int DrumMapRows = 2;

    /// <summary>Enumerates every bank and program one vintage defines.</summary>
    /// <param name="directory">The patch directory.</param>
    /// <param name="map">The vintage.</param>
    /// <returns>The banks that define at least one program, in bank order.</returns>
    public static VintageCatalog Build(PatchDirectory directory, ToneMap map)
    {
        ArgumentNullException.ThrowIfNull(directory);

        // Bank 0 first and unconditionally: it is the capital bank every other bank falls back to, so
        // building it once spares re-resolving every hole in all 127 variations.
        var capital = BuildBank(directory, map, 0, capital: null);

        var banks = new List<CatalogBank> { capital };
        var tones = new HashSet<int>();
        var native = capital.NativeCount;

        Collect(capital, tones);

        for (var bank = 1; bank < BankCount; bank++)
        {
            var built = BuildBank(directory, map, bank, capital);
            if (built.NativeCount == 0)
            {
                continue;
            }

            banks.Add(built);
            native += built.NativeCount;
            Collect(built, tones);
        }

        return new VintageCatalog(map, banks, native, tones.Count);
    }

    /// <summary>Enumerates every kit one drum map row reaches.</summary>
    /// <param name="directory">The patch directory, which names the tones the keys sound.</param>
    /// <param name="drums">The drum kit table.</param>
    /// <param name="row">Map row — 0 is the GM/GS map.</param>
    /// <returns>The kits, ordered by the first program that selects each.</returns>
    /// <remarks>
    /// Kits carry the programs that select them rather than a name, and keys are named from the
    /// melodic tone table, because that is all there is: the DLL holds no kit-name or drum-key-name
    /// table at all — those live in the plugin's companion tone files, which this project does not
    /// read.
    /// </remarks>
    public static DrumCatalog BuildDrums(PatchDirectory directory, DrumKitTable drums, int row)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(drums);

        var order = new List<int>();
        var programs = new Dictionary<int, List<int>>();

        for (var program = 0; program < ProgramCount; program++)
        {
            if (drums.KitForProgram(program, row) is not { } kit)
            {
                continue;
            }

            if (!programs.TryGetValue(kit, out var selectors))
            {
                selectors = [];
                programs[kit] = selectors;
                order.Add(kit);
            }

            selectors.Add(program);
        }

        var kits = new List<DrumKitEntry>(order.Count);
        foreach (var kit in order)
        {
            kits.Add(new DrumKitEntry(kit, programs[kit], Keys(directory, drums, kit)));
        }

        return new DrumCatalog(row, kits);
    }

    /// <summary>Programs per bank.</summary>
    private const int ProgramCount = 128;

    /// <summary>Banks a bank select MSB can name.</summary>
    private const int BankCount = 128;

    private static void Collect(CatalogBank bank, HashSet<int> tones)
    {
        foreach (var entry in bank.Programs)
        {
            if (entry.Kind == CatalogEntryKind.Native)
            {
                tones.Add(entry.Tone);
            }
        }
    }

    private static CatalogBank BuildBank(
        PatchDirectory directory,
        ToneMap map,
        int bank,
        CatalogBank? capital)
    {
        var entries = new CatalogEntry[ProgramCount];
        var native = 0;

        for (var program = 0; program < entries.Length; program++)
        {
            var raw = directory.Lut3Raw(program, map, bank);

            if (raw is null || raw == PatchDirectory.Unassigned)
            {
                // Nothing native here. On a variation bank the module sounds bank 0's capital tone
                // rather than falling silent, which is what Lut3Resolved reproduces at play time; on
                // bank 0 itself there is nothing behind it.
                entries[program] = capital?.Programs[program] switch
                {
                    { Kind: CatalogEntryKind.Native } fallback =>
                        fallback with { Kind = CatalogEntryKind.CapitalFallback },
                    _ => new CatalogEntry(program, -1, Nothing, CatalogEntryKind.Unassigned),
                };

                continue;
            }

            if (raw >= PatchDirectory.IndirectOnlyFlag)
            {
                entries[program] = new CatalogEntry(
                    program,
                    -1,
                    IndirectName(directory, raw.Value) ?? Nothing,
                    CatalogEntryKind.IndirectOnly);
                continue;
            }

            // ProgramTones is what the engine's note-on calls, so a slot counts as playable only where
            // the engine would find something to play: it follows the alternate-articulation
            // dereference, and an entry pointing at an undefined tone resolves to no partials at all.
            var tones = directory.ProgramTones(program, map, bank);
            var tone = tones.Count > 0 ? tones[0] : -1;

            if (tone >= 0 && directory.GetTone(tone) is { IsDefined: true } record)
            {
                entries[program] = new CatalogEntry(
                    program, tone, record.Name.Trim(), CatalogEntryKind.Native);
                native++;
                continue;
            }

            entries[program] = new CatalogEntry(program, -1, Nothing, CatalogEntryKind.Unassigned);
        }

        return new CatalogBank(bank, entries, native);
    }

    /// <summary>Stands in for a name where there is nothing to name.</summary>
    private const string Nothing = "—";

    // The indirect-only marker is the top bit of an otherwise ordinary tone reference, and the
    // program-change handler strips it the same way. Naming one is worth the mask: those entries are
    // real sounds the module reaches in mono mode, and listing them unnamed would say the ROM is
    // emptier than it is.
    private static string? IndirectName(PatchDirectory directory, int raw)
    {
        var reference = raw & 0x7FFF;

        if (reference < PatchDirectory.MelodicSpaceEnd)
        {
            return directory.GetTone(reference) is { IsDefined: true } tone ? tone.Name.Trim() : null;
        }

        if (reference >= PatchDirectory.AlternateSpaceStart)
        {
            return directory.Alternate(reference - PatchDirectory.AlternateSpaceStart)?.Name;
        }

        return null;
    }

    // Drum sounds are ordinary melodic tones, so a key is named from the same table a program change
    // names an instrument from.
    private static IReadOnlyList<DrumKeyEntry> Keys(
        PatchDirectory directory,
        DrumKitTable drums,
        int kit)
    {
        var keys = new List<DrumKeyEntry>(64);

        for (var note = 0; note < DrumKitTable.KeyCount; note++)
        {
            var key = drums.Key(note, kit);
            if (directory.GetTone(key.Tone) is not { IsDefined: true } tone)
            {
                continue;
            }

            keys.Add(new DrumKeyEntry(
                note, key.Tone, tone.Name.Trim(), key.Level, key.Pitch, key.Group, key.Pan));
        }

        return keys;
    }
}
