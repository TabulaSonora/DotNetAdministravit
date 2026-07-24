using System.Text;

namespace TabulaSonora.Web.Services;

/// <summary>
/// Kit names read from a Sound Canvas VA <c>.drf</c> tone file.
/// </summary>
/// <remarks>
/// <para>
/// The DLL holds no kit names at all — the tone table names the individual drum <em>sounds</em>, but
/// nothing in it says that program 8 is "ROOM". Those names live in the plugin's companion
/// <c>SCVSC.drf</c>, which is Roland's file and not this repository's to carry, so this reads the
/// user's own copy exactly as the engine reads the user's own DLL. Without one, kits are known by the
/// program that selects them, which is true but terse.
/// </para>
/// <para>
/// The format is a tab-delimited text table: a header, then a <c>PAGE=</c> per tone map whose first
/// character is the map index, then one row per kit as <c>program</c> (1-based) and name. Rows are
/// padded out to 128 tab-separated columns for the variation banks, which drums do not use — only the
/// first column is ever filled.
/// </para>
/// </remarks>
public sealed class DrumKitNames
{
    private readonly Dictionary<int, Dictionary<int, string>> _pages = [];

    /// <summary>The module the file names, as it names itself.</summary>
    public string Module { get; private set; } = string.Empty;

    /// <summary>How many kits were read, across every page.</summary>
    public int Count { get; private set; }

    /// <summary>Whether anything was read at all.</summary>
    public bool IsEmpty => Count == 0;

    /// <summary>Reads a <c>.drf</c> file.</summary>
    /// <param name="bytes">The file's bytes.</param>
    /// <returns>The names it holds.</returns>
    /// <exception cref="InvalidDataException">The file is not an SSW drum file.</exception>
    public static DrumKitNames Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        // Latin-1 rather than UTF-8: the file is a fixed-width era artefact and decoding it as UTF-8
        // turns any high byte into a replacement character rather than failing loudly.
        var text = Encoding.Latin1.GetString(bytes);
        var names = new DrumKitNames();

        if (!text.StartsWith("SSW DRUMFILE", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "That is not a Sound Canvas drum file. Look for SCVSC.drf beside SCCore.dll.");
        }

        Dictionary<int, string>? page = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var fields = line.Split('\t');
            var head = fields[0];

            if (head.StartsWith("MODULENAME=", StringComparison.Ordinal))
            {
                names.Module = head["MODULENAME=".Length..].Trim();
                continue;
            }

            if (head.StartsWith("PAGE=", StringComparison.Ordinal))
            {
                // "48820Drum", "388ProDrum", "155Drum", "0Default" -- the first character is the tone
                // map the page belongs to, and the rest is the module's own label for it.
                var label = head["PAGE=".Length..].Trim();
                page = label.Length > 0 && char.IsAsciiDigit(label[0])
                    ? names.PageFor(label[0] - '0')
                    : null;

                continue;
            }

            if (page is null || fields.Length < 2 || !int.TryParse(head, out var ordinal))
            {
                continue;
            }

            var name = fields[1].Trim();
            if (name.Length == 0)
            {
                continue;
            }

            // Rows are 1-based; the engine's programs are not.
            page[ordinal - 1] = name;
            names.Count++;
        }

        return names;
    }

    /// <summary>The tone maps this file has a page for.</summary>
    public IReadOnlyCollection<int> Maps => _pages.Keys;

    /// <summary>The page every module carries, holding its own full kit set.</summary>
    private const int DefaultPage = 0;

    /// <summary>Names the kit a program selects.</summary>
    /// <param name="map">Tone map index the kit set belongs to.</param>
    /// <param name="program">Program number on the drum part.</param>
    /// <returns>The name, or <see langword="null"/> if this file does not name it.</returns>
    /// <remarks>
    /// Falls back to the file's Default page, and to that page only. In <c>SCVSC.drf</c> it is the
    /// same 37 kits as the SC-8820 page, so a file whose per-map pages are missing still names
    /// everything; but walking every page in turn would let an SC-55 page answer for an SC-8820 kit
    /// and hand back "STANDARD" where the right answer is "STANDARD 1". A slightly wrong name is
    /// worse than a program number, because a program number does not claim to be a name.
    /// </remarks>
    public string? Lookup(int map, int program) =>
        Page(map, program) ?? (map == DefaultPage ? null : Page(DefaultPage, program));

    /// <summary>How many of a set's kits this file can name.</summary>
    /// <param name="map">Tone map index the kit set belongs to.</param>
    /// <param name="programs">The programs that select the set's kits.</param>
    /// <returns>How many of them are named.</returns>
    /// <remarks>
    /// What the panel reports. A file that parses but names nothing for the set on screen is the one
    /// failure this format makes easy — <c>GM.drf</c> sits in the same folder, is accepted by the same
    /// file filter, and holds a single page naming a single kit — and silence there looks exactly like
    /// the feature not working.
    /// </remarks>
    public int CountFor(int map, IEnumerable<int> programs)
    {
        ArgumentNullException.ThrowIfNull(programs);
        return programs.Count(program => Lookup(map, program) is not null);
    }

    private string? Page(int map, int program) =>
        _pages.TryGetValue(map, out var page) && page.TryGetValue(program, out var name) ? name : null;

    private Dictionary<int, string> PageFor(int map)
    {
        if (!_pages.TryGetValue(map, out var page))
        {
            page = [];
            _pages[map] = page;
        }

        return page;
    }
}
