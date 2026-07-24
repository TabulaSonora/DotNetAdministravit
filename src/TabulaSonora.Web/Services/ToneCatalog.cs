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
