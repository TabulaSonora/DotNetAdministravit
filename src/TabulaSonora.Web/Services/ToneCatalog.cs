using TabulaSonora.Patches;

namespace TabulaSonora.Web.Services;

/// <summary>
/// The loaded ROM's sound set, built once per vintage and kept.
/// </summary>
/// <remarks>
/// <para>
/// The enumeration itself is <see cref="SoundCatalog"/> in the library, beside the tables it reads.
/// What belongs to the page is only this: knowing when it is worth building, when what was built is
/// stale, and how to say a vintage's name out loud.
/// </para>
/// <para>
/// The cache is keyed on the <see cref="PatchDirectory"/> instance rather than on an event, because a
/// new directory is exactly what loading a different DLL produces and nothing else does — a vintage
/// change rebuilds the generator but not the tables, so the catalogs stay good across it.
/// </para>
/// </remarks>
/// <param name="session">The session holding the loaded ROM.</param>
public sealed class ToneCatalog(SynthSession session)
{
    private static readonly string[] Names =
        ["C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B"];

    private readonly Dictionary<ToneMap, VintageCatalog> _vintages = [];
    private readonly Dictionary<int, DrumCatalog> _drums = [];

    private PatchDirectory? _builtFor;

    /// <summary>Every bank and program one vintage defines.</summary>
    /// <param name="map">The vintage.</param>
    /// <returns>The catalog, or <see langword="null"/> when no ROM is loaded.</returns>
    public VintageCatalog? For(ToneMap map)
    {
        if (Refresh() is not { } directory)
        {
            return null;
        }

        if (!_vintages.TryGetValue(map, out var catalog))
        {
            catalog = SoundCatalog.Build(directory, map);
            _vintages[map] = catalog;
        }

        return catalog;
    }

    /// <summary>Every kit one drum map row reaches.</summary>
    /// <param name="row">Map row — 0 is the GM/GS map.</param>
    /// <returns>The catalog, or <see langword="null"/> when no ROM is loaded.</returns>
    public DrumCatalog? DrumsFor(int row)
    {
        if (Refresh() is not { } directory || session.Drums is not { } drums)
        {
            return null;
        }

        if (!_drums.TryGetValue(row, out var catalog))
        {
            catalog = SoundCatalog.BuildDrums(directory, drums, row);
            _drums[row] = catalog;
        }

        return catalog;
    }

    /// <summary>Names a vintage as the module it is.</summary>
    /// <param name="map">The tone map.</param>
    /// <returns>The module's name.</returns>
    /// <remarks>
    /// Written once here rather than in each panel that shows one. The plugin's own tone files name
    /// these maps <c>55Map</c>, <c>88Map</c>, <c>88ProMap</c> and <c>8820Map</c>; the modules are what
    /// a reader recognises.
    /// </remarks>
    public static string VintageName(ToneMap map) => map switch
    {
        ToneMap.Sc55 => "SC-55",
        ToneMap.Sc88 => "SC-88",
        ToneMap.Sc88Pro => "SC-88Pro",
        _ => "SC-8820",
    };

    /// <summary>Names a MIDI note the way the on-screen keyboard labels its keys.</summary>
    /// <param name="note">MIDI note number.</param>
    /// <returns>The name, middle C being <c>C4</c>.</returns>
    public static string NoteName(int note) => $"{Names[note % 12]}{(note / 12) - 1}";

    /// <summary>What a bank is, where it is something other than a variation of the capital tone.</summary>
    /// <param name="bank">Bank select MSB.</param>
    /// <returns>The bank's name, or <see langword="null"/> for an ordinary variation bank.</returns>
    /// <remarks>
    /// <para>
    /// The top two banks are not variations at all: they are the module's CM-64 compatibility map, so
    /// that a file written for Roland's older Computer Music modules plays. 127 is the LA half — the
    /// MT-32 / CM-32L side — and 126 is the PCM half, the CM-32P side.
    /// </para>
    /// <para>
    /// The ROM agrees with the documentation on both counts. Bank 127 holds 128 programs whose tones
    /// carry MT-32 names (<c>Acou Piano1</c>, <c>Elec Piano1</c>) rather than Sound Canvas ones, and
    /// bank 126 holds exactly 64 — the CM-32P's tone count. Both are identical across all four
    /// vintages, as a compatibility map that belongs to no generation should be.
    /// </para>
    /// </remarks>
    public static string? BankName(int bank) => bank switch
    {
        126 => "CM-64 PCM",
        127 => "CM-64 LA · MT-32",
        _ => null,
    };

    /// <summary>The banks that are a whole other module's sound set rather than a variation.</summary>
    /// <remarks>
    /// Listed beside the vintages rather than among a program's variants, because that is what they
    /// are: a file selects one for the same reason it selects a vintage, and neither is a variation
    /// of a Sound Canvas instrument.
    /// </remarks>
    public static readonly int[] EmulationBanks = [126, 127];

    /// <summary>The General MIDI family a program number falls in.</summary>
    /// <param name="program">Program number, 0–127.</param>
    /// <returns>The family's name.</returns>
    /// <remarks>
    /// General MIDI lays its 128 programs out as sixteen families of eight, and that layout is the
    /// spec's rather than this ROM's — no table in the DLL carries it. It is a grouping of program
    /// <em>numbers</em>, so it holds whatever the vintage names those programs; the names themselves
    /// still come from the tone table.
    /// </remarks>
    public static string FamilyOf(int program) => Families[program / 8];

    /// <summary>How many programs share a family.</summary>
    public const int FamilySize = 8;

    private static readonly string[] Families =
    [
        "Piano", "Chromatic percussion", "Organ", "Guitar",
        "Bass", "Strings", "Ensemble", "Brass",
        "Reed", "Pipe", "Synth lead", "Synth pad",
        "Synth effects", "Ethnic", "Percussive", "Sound effects",
    ];

    // Loading a different DLL builds a new NoteRenderer and so a new directory; nothing else does.
    // Comparing the instance is therefore a complete invalidation test, and it costs a reference
    // compare rather than a subscription to an event that also fires for songs and settings.
    private PatchDirectory? Refresh()
    {
        var directory = session.Directory;
        if (!ReferenceEquals(directory, _builtFor))
        {
            _vintages.Clear();
            _drums.Clear();
            _builtFor = directory;
        }

        return directory;
    }
}
