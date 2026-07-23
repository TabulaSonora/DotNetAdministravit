using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace TabulaSonora.Rom;

/// <summary>
/// Identity of the <c>SCCore.dll</c> build every offset in the manifest is pinned to.
/// </summary>
/// <param name="FileName">Expected file name, for diagnostics only.</param>
/// <param name="Product">Human-readable product description.</param>
/// <param name="Size">Exact file size in bytes.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the whole file.</param>
/// <param name="Sha1">Lower-case hex SHA-1 of the whole file.</param>
/// <param name="Md5">Lower-case hex MD5 of the whole file.</param>
/// <param name="PeTimestamp">COFF <c>TimeDateStamp</c> from the PE header.</param>
public sealed record DllIdentity(
    string FileName,
    string Product,
    long Size,
    string Sha256,
    string Sha1,
    string Md5,
    uint PeTimestamp);

/// <summary>
/// One static table sliced byte-for-byte out of the DLL.
/// </summary>
/// <param name="Name">Cache file name, e.g. <c>curve_amp_hi_2ba0.bin</c>.</param>
/// <param name="Symbol">Project label for the symbol, e.g. <c>g_amp_curve_hi</c>. Not Roland's name.</param>
/// <param name="Subsystem">Owning subsystem: TVA, TVF, PITCH, ENV, LFO, DIR, SAMPLER, MIX or CTRL.</param>
/// <param name="DType">Element type as recorded by the manifest generator.</param>
/// <param name="Shape">Human-readable shape, e.g. <c>128x4</c> or <c>128x128 (row*0x80+key)</c>.</param>
/// <param name="Size">Length in bytes.</param>
/// <param name="FileOffset">Offset of the first byte within the DLL file.</param>
/// <param name="Va">Virtual address in the loaded image, when known.</param>
/// <param name="SectionAdjust">Section skew used to derive <paramref name="FileOffset"/> from <paramref name="Va"/>.</param>
/// <param name="Match">
/// <c>full</c> when the cached bytes match the DLL exactly, or <c>prefix n/m</c> for an
/// over-read cache whose leading <c>n</c> bytes match.
/// </param>
/// <param name="Purpose">What the table is for.</param>
public sealed record TableEntry(
    string Name,
    string Symbol,
    string Subsystem,
    string DType,
    string Shape,
    int Size,
    long FileOffset,
    long? Va,
    long? SectionAdjust,
    string Match,
    string Purpose);

/// <summary>
/// A region read straight from the DLL at render time rather than cached to a <c>.bin</c> file —
/// the two wave-ROM banks and the drum-kit lookup tables.
/// </summary>
/// <param name="Name">Region name, e.g. <c>wave_rom_bank_A</c>.</param>
/// <param name="Symbol">Project label for the symbol.</param>
/// <param name="Subsystem">Owning subsystem, <c>ROM</c> or <c>DIR</c>.</param>
/// <param name="DType">Element description.</param>
/// <param name="FileOffset">Offset of the first byte within the DLL file.</param>
/// <param name="Size">Length in bytes, when the manifest records one.</param>
/// <param name="Purpose">What the region is for.</param>
/// <remarks>
/// The drum regions are recorded under a <c>va</c> key, but those values are already DLL
/// <em>file offsets</em> — they carry no image base. They are surfaced here as
/// <paramref name="FileOffset"/> so every consumer addresses the file uniformly.
/// </remarks>
public sealed record LiveRegion(
    string Name,
    string Symbol,
    string Subsystem,
    string DType,
    long FileOffset,
    long? Size,
    string Purpose);

/// <summary>
/// The machine-readable map of everything the engine needs out of <c>SCCore.dll</c>: the pinned
/// build identity, every static table's byte-exact offset, and the live ROM/directory regions.
/// </summary>
/// <remarks>
/// A copy of <c>tables/manifest.json</c> from the Tabula Sonora spec repo ships as an embedded
/// resource, so the library is self-describing with no data files on disk.
/// </remarks>
public sealed class TableManifest
{
    private const string ResourceName = "TabulaSonora.Rom.manifest.json";

    private static readonly Lazy<TableManifest> LazyDefault = new(LoadEmbedded, isThreadSafe: true);

    private readonly Dictionary<string, TableEntry> _byName;
    private readonly Dictionary<string, LiveRegion> _regionsByName;

    private TableManifest(
        DllIdentity dll,
        long imageBase,
        IReadOnlyList<TableEntry> cachedTables,
        IReadOnlyList<LiveRegion> liveRegions)
    {
        Dll = dll;
        ImageBase = imageBase;
        CachedTables = cachedTables;
        LiveRegions = liveRegions;
        _byName = cachedTables.ToDictionary(static t => t.Name, StringComparer.Ordinal);
        _regionsByName = liveRegions.ToDictionary(static r => r.Name, StringComparer.Ordinal);
    }

    /// <summary>The manifest embedded in this assembly.</summary>
    public static TableManifest Default => LazyDefault.Value;

    /// <summary>The DLL build these offsets are valid for.</summary>
    public DllIdentity Dll { get; }

    /// <summary>The DLL's preferred image base, <c>0x180000000</c>.</summary>
    public long ImageBase { get; }

    /// <summary>The 48 static tables cached as <c>.bin</c> slices.</summary>
    public IReadOnlyList<TableEntry> CachedTables { get; }

    /// <summary>The regions read directly from the DLL rather than cached.</summary>
    public IReadOnlyList<LiveRegion> LiveRegions { get; }

    /// <summary>Looks up a cached table by its <c>.bin</c> file name.</summary>
    /// <param name="name">Cache file name, e.g. <c>pan_a2fa1.bin</c>.</param>
    /// <returns>The matching entry.</returns>
    /// <exception cref="KeyNotFoundException">No table with that name exists.</exception>
    public TableEntry Table(string name) =>
        _byName.TryGetValue(name, out var entry)
            ? entry
            : throw new KeyNotFoundException($"No cached table named '{name}' in the manifest.");

    /// <summary>Looks up a live region by name.</summary>
    /// <param name="name">Region name, e.g. <c>wave_rom_bank_A</c>.</param>
    /// <returns>The matching region.</returns>
    /// <exception cref="KeyNotFoundException">No region with that name exists.</exception>
    public LiveRegion Region(string name) =>
        _regionsByName.TryGetValue(name, out var region)
            ? region
            : throw new KeyNotFoundException($"No live region named '{name}' in the manifest.");

    /// <summary>Parses a manifest from a JSON stream.</summary>
    /// <param name="json">Stream positioned at the start of a <c>manifest.json</c> document.</param>
    /// <returns>The parsed manifest.</returns>
    public static TableManifest Load(Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var dllElement = root.GetProperty("dll");
        var dll = new DllIdentity(
            FileName: dllElement.GetProperty("filename").GetString() ?? "SCCore.dll",
            Product: dllElement.GetProperty("product").GetString() ?? string.Empty,
            Size: dllElement.GetProperty("size").GetInt64(),
            Sha256: RequireHex(dllElement, "sha256"),
            Sha1: RequireHex(dllElement, "sha1"),
            Md5: RequireHex(dllElement, "md5"),
            PeTimestamp: (uint)dllElement.GetProperty("pe_timestamp").GetInt64());

        var imageBase = ParseHex(root.GetProperty("image_base").GetString())
            ?? throw new InvalidDataException("manifest.json is missing 'image_base'.");

        var tables = new List<TableEntry>();
        foreach (var e in root.GetProperty("cached_tables").EnumerateArray())
        {
            var fileOffset = ParseHex(GetStringOrNull(e, "file_offset"));
            if (fileOffset is null)
            {
                // An entry with no file offset cannot be extracted; the generator emits these
                // only when it failed to locate the bytes in the DLL.
                continue;
            }

            tables.Add(new TableEntry(
                Name: e.GetProperty("name").GetString()!,
                Symbol: GetStringOrNull(e, "symbol") ?? string.Empty,
                Subsystem: GetStringOrNull(e, "subsystem") ?? string.Empty,
                DType: GetStringOrNull(e, "dtype") ?? string.Empty,
                Shape: GetStringOrNull(e, "shape") ?? string.Empty,
                Size: e.GetProperty("size").GetInt32(),
                FileOffset: fileOffset.Value,
                Va: ParseHex(GetStringOrNull(e, "va")),
                SectionAdjust: ParseHex(GetStringOrNull(e, "section_adjust")),
                Match: GetStringOrNull(e, "match") ?? "full",
                Purpose: GetStringOrNull(e, "purpose") ?? string.Empty));
        }

        var regions = new List<LiveRegion>();
        foreach (var e in root.GetProperty("live_regions").EnumerateArray())
        {
            // Wave-ROM banks carry 'file_offset'; the drum tables carry 'va', but those values
            // are already file offsets (no image base applied). Either way it addresses the file.
            var offset = ParseHex(GetStringOrNull(e, "file_offset"))
                ?? ParseHex(GetStringOrNull(e, "va"))
                ?? throw new InvalidDataException(
                    $"Live region '{e.GetProperty("name").GetString()}' has neither file_offset nor va.");

            long? size = e.TryGetProperty("size", out var sizeElement) && sizeElement.ValueKind == JsonValueKind.Number
                ? sizeElement.GetInt64()
                : null;

            regions.Add(new LiveRegion(
                Name: e.GetProperty("name").GetString()!,
                Symbol: GetStringOrNull(e, "symbol") ?? string.Empty,
                Subsystem: GetStringOrNull(e, "subsystem") ?? string.Empty,
                DType: GetStringOrNull(e, "dtype") ?? string.Empty,
                FileOffset: offset,
                Size: size,
                Purpose: GetStringOrNull(e, "purpose") ?? string.Empty));
        }

        return new TableManifest(
            dll,
            imageBase,
            new ReadOnlyCollection<TableEntry>(tables),
            new ReadOnlyCollection<LiveRegion>(regions));
    }

    private static TableManifest LoadEmbedded()
    {
        using var stream = typeof(TableManifest).GetTypeInfo().Assembly
            .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' is missing.");
        return Load(stream);
    }

    private static string RequireHex(JsonElement element, string property) =>
        element.GetProperty(property).GetString()?.ToLowerInvariant()
        ?? throw new InvalidDataException($"manifest.json dll.{property} is missing.");

    private static string? GetStringOrNull(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ParseHex(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var span = text.AsSpan();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
        }

        return long.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
