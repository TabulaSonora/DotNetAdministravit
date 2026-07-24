namespace TabulaSonora.Web.Services;

/// <summary>
/// What each drum kit is called.
/// </summary>
/// <remarks>
/// <para>
/// The DLL has no kit names. It names every drum <em>sound</em> — those come out of the melodic tone
/// table, because drum sounds are melodic tones — but nothing in it says that program 9 selects
/// "ROOM". Those names live in the plugin's companion <c>SCVSC.drf</c>, and this is the kit-name half
/// of that file, transcribed. See <c>NOTICE.md</c>: it is the second piece of Roland-derived data
/// this repository carries and it is declared there, alongside the effect coefficients.
/// </para>
/// <para>
/// Only two of the file's five pages are here, because only two are reachable. The drum program map
/// has two rows, and their contents identify them exactly: row 0 defines the programs on the file's
/// SC-8820 page and row 1 those on its SC-88Pro page. A part plays drums through one row or the
/// other whatever tone map is selected, so the SC-55 and SC-88 pages name kits nothing can select.
/// </para>
/// <para>
/// One kit is deliberately unnamed. Program 127 — the CM-64/32L kit, the MT-32's drum set — is
/// defined by the ROM on both rows and appears on neither of these pages, so it keeps its program
/// number rather than borrowing a name from the SC-55 page that does list it.
/// </para>
/// </remarks>
public static class DrumKitNames
{
    private static readonly Dictionary<int, Dictionary<int, string>> Pages = new()
    {
        [4] = new Dictionary<int, string>
        {
            [0] = "STANDARD 1",
            [1] = "STANDARD 2",
            [2] = "STANDARD L/R",
            [8] = "ROOM",
            [9] = "HIP HOP",
            [10] = "JUNGLE",
            [11] = "TECHNO",
            [12] = "ROOM L/R",
            [13] = "HOUSE",
            [16] = "POWER",
            [24] = "ELECTRONIC",
            [25] = "TR-808",
            [26] = "DANCE",
            [27] = "CR-78",
            [28] = "TR-606",
            [29] = "TR-707",
            [30] = "TR-909",
            [32] = "JAZZ",
            [33] = "JAZZ L/R",
            [40] = "BRUSH",
            [41] = "BRUSH 2",
            [42] = "BRUSH 2 L/R",
            [48] = "ORCHESTRA",
            [49] = "ETHNIC",
            [50] = "KICK & SNARE",
            [51] = "KICK & SNARE2",
            [52] = "ASIA",
            [53] = "CYMBAL&CLAPS",
            [54] = "GAMELAN 1",
            [55] = "GAMELAN 2",
            [56] = "SFX",
            [57] = "RHYTHM FX",
            [58] = "RHYTHM FX 2",
            [59] = "RHYTHM FX 3",
            [60] = "SFX 2",
            [61] = "VOICE",
            [62] = "CYM&CLAPS 2",
        },
        [3] = new Dictionary<int, string>
        {
            [0] = "STANDARD 1",
            [1] = "STANDARD 2",
            [2] = "STANDARD 3",
            [8] = "ROOM",
            [9] = "HIP HOP",
            [10] = "JUNGLE",
            [11] = "TECHNO",
            [16] = "POWER",
            [24] = "ELECTRONIC",
            [25] = "TR-808",
            [26] = "DANCE",
            [27] = "CR-78",
            [28] = "TR-606",
            [29] = "TR-707",
            [30] = "TR-909",
            [32] = "JAZZ",
            [40] = "BRUSH",
            [48] = "ORCHESTRA",
            [49] = "ETHNIC",
            [50] = "KICK & SNARE",
            [52] = "ASIA",
            [53] = "CYMBAL&CLAPS",
            [56] = "SFX",
            [57] = "RHYTHM FX",
            [58] = "RHYTHM FX 2",
        },
    };

    /// <summary>The tone map whose kit list each drum map row holds.</summary>
    /// <remarks>
    /// Measured rather than assumed: the set of programs each row defines matches one page's set of
    /// kits exactly, in both cases plus program 127, which the ROM carries and no page offers.
    /// </remarks>
    public static readonly int[] RowMaps = [4, 3];

    /// <summary>Names the kit a program selects.</summary>
    /// <param name="row">Drum map row.</param>
    /// <param name="program">Program number on the drum part.</param>
    /// <returns>The name, or <see langword="null"/> where nothing names it.</returns>
    public static string? ForRow(int row, int program) =>
        row >= 0 && row < RowMaps.Length &&
        Pages.TryGetValue(RowMaps[row], out var page) &&
        page.TryGetValue(program, out var name)
            ? name
            : null;

    /// <summary>Names the kit set a drum map row holds, as the module whose list it is.</summary>
    /// <param name="row">Drum map row.</param>
    /// <returns>The module's name.</returns>
    public static string RowName(int row) =>
        row >= 0 && row < RowMaps.Length
            ? ToneCatalog.VintageName((TabulaSonora.Patches.ToneMap)RowMaps[row])
            : $"row {row}";

    /// <summary>How many kits a row's page names.</summary>
    /// <param name="row">Drum map row.</param>
    /// <returns>The count.</returns>
    public static int CountFor(int row) =>
        row >= 0 && row < RowMaps.Length && Pages.TryGetValue(RowMaps[row], out var page)
            ? page.Count
            : 0;
}
