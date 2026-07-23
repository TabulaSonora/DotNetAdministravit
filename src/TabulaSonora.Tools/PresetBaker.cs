using System.Text.Json;
using TabulaSonora.Rom;

namespace TabulaSonora.Tools;

/// <summary>
/// Assembles the effect preset file: the delay presets read from the DLL, the GS conversion tables,
/// and the reverb and chorus coefficients harvested from a live engine.
/// </summary>
public static class PresetBaker
{
    /// <summary>
    /// File offset of <c>g_delay_preset_tbl</c>.
    /// </summary>
    /// <remarks>
    /// Documented at virtual address <c>0x181893930</c>; this region takes the same −0x1000 section
    /// adjustment as the <c>.rdata</c> curve tables, confirmed by locating the table's bytes.
    /// </remarks>
    public const long DelayPresetOffset = 0x1892930;

    /// <summary>Number of GS delay macros.</summary>
    public const int DelayTypeCount = 10;

    /// <summary>Bytes per delay preset row.</summary>
    public const int DelayPresetStride = 10;

    /// <summary>The eight GS reverb macro names.</summary>
    public static readonly string[] ReverbTypeNames =
        ["Room1", "Room2", "Room3", "Hall1", "Hall2", "Plate", "Delay", "PanDelay"];

    /// <summary>The eight GS chorus macro names.</summary>
    public static readonly string[] ChorusTypeNames =
        ["Chorus1", "Chorus2", "Chorus3", "Chorus4", "FeedbackChorus", "Flanger", "ShortDelay", "ShortDelayFB"];

    /// <summary>The ten GS delay macro names.</summary>
    public static readonly string[] DelayTypeNames =
    [
        "Delay1", "Delay2", "Delay3", "Delay4", "PanDelay1",
        "PanDelay2", "PanDelay3", "PanDelay4", "DelayToReverb", "PanRepeat",
    ];

    /// <summary>
    /// Roland's DELAY TIME CENTER conversion, raw 1–115 to milliseconds.
    /// </summary>
    /// <remarks>
    /// Table 16 of the published GS MIDI implementation — documentation rather than extracted binary
    /// data, which is why it can live in source.
    /// </remarks>
    private static readonly double[] DelayTimeMilliseconds =
    [
        0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0, 1.1, 1.2, 1.3, 1.4, 1.5, 1.6, 1.7, 1.8, 1.9, 2.0,
        2.2, 2.4, 2.6, 2.8, 3.0, 3.2, 3.4, 3.6, 3.8, 4.0, 4.2, 4.4, 4.6, 4.8, 5.0, 5.5, 6.0, 6.5, 7.0, 7.5,
        8.0, 8.5, 9.0, 9.5, 10.0, 11.0, 12.0, 13.0, 14.0, 15.0, 16.0, 17.0, 18.0, 19.0, 20.0, 22.0, 24.0,
        26.0, 28.0, 30.0, 32.0, 34.0, 36.0, 38.0, 40.0, 42.0, 44.0, 46.0, 48.0, 50.0, 55.0, 60.0, 65.0,
        70.0, 75.0, 80.0, 85.0, 90.0, 95.0, 100.0, 110.0, 120.0, 130.0, 140.0, 150.0, 160.0, 170.0, 180.0,
        190.0, 200.0, 220.0, 240.0, 260.0, 280.0, 300.0, 320.0, 340.0, 360.0, 380.0, 400.0, 420.0, 440.0,
        460.0, 480.0, 500.0, 550.0, 600.0, 650.0, 700.0, 750.0, 800.0, 850.0, 900.0, 950.0, 1000.0,
    ];

    /// <summary>Roland's DELAY TIME RATIO conversion, raw 1–120 to percent. Table 17.</summary>
    private static readonly double[] DelayRatioPercent =
    [
        4, 8, 13, 17, 21, 25, 29, 33, 38, 42, 46, 50, 54, 58, 63, 67, 71, 75, 79, 83, 88, 92, 96, 100,
        104, 108, 113, 117, 121, 125, 129, 133, 138, 142, 146, 150, 154, 158, 163, 167, 171, 175, 179,
        183, 188, 192, 196, 200, 204, 208, 213, 217, 221, 225, 229, 233, 238, 242, 246, 250, 254, 258,
        263, 267, 271, 275, 279, 283, 288, 292, 296, 300, 304, 308, 313, 317, 321, 325, 329, 333, 338,
        342, 346, 350, 354, 358, 363, 367, 371, 375, 379, 383, 388, 392, 396, 400, 404, 408, 413, 417,
        421, 425, 429, 433, 438, 442, 446, 450, 454, 458, 463, 467, 471, 475, 479, 483, 488, 492, 496, 500,
    ];

    /// <summary>Reads the ten GS delay presets out of the DLL.</summary>
    /// <param name="rom">An open ROM image.</param>
    /// <returns>Ten rows of ten raw parameters.</returns>
    /// <exception cref="InvalidDataException">The table does not look like the expected presets.</exception>
    public static int[][] ReadDelayPresets(RomImage rom)
    {
        ArgumentNullException.ThrowIfNull(rom);

        var bytes = rom.Read(DelayPresetOffset, DelayTypeCount * DelayPresetStride);
        var presets = new int[DelayTypeCount][];

        for (var t = 0; t < DelayTypeCount; t++)
        {
            presets[t] = new int[DelayPresetStride];
            for (var i = 0; i < DelayPresetStride; i++)
            {
                presets[t][i] = bytes[(t * DelayPresetStride) + i];
            }
        }

        // A wrong offset would silently produce plausible junk, so check a known row.
        if (presets[0][1] != 97 || presets[0][4] != 127 || presets[0][8] != 80)
        {
            throw new InvalidDataException(
                $"Delay preset table at 0x{DelayPresetOffset:x} does not look right " +
                $"(first row [{string.Join(",", presets[0])}]).");
        }

        return presets;
    }

    /// <summary>Writes the preset file.</summary>
    /// <param name="outputPath">Destination path.</param>
    /// <param name="reverbDefault">The GM power-on reverb.</param>
    /// <param name="reverb">One coefficient set per GS reverb type.</param>
    /// <param name="chorusDefault">The GM power-on chorus.</param>
    /// <param name="chorus">One coefficient set per GS chorus type.</param>
    /// <param name="delayPresets">The raw delay preset rows.</param>
    public static void Write(
        string outputPath,
        object reverbDefault,
        object[] reverb,
        object chorusDefault,
        object[] chorus,
        int[][] delayPresets)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        var document = new
        {
            _note = "Derived from a licensed SCCore.dll by 'tabula-sonora prepare'. Roland-derived: " +
                    "generate locally, do not redistribute.",
            reverb = new { typeNames = ReverbTypeNames, @default = reverbDefault, types = reverb },
            chorus = new { typeNames = ChorusTypeNames, @default = chorusDefault, types = chorus },
            delay = new
            {
                typeNames = DelayTypeNames,
                timeMilliseconds = DelayTimeMilliseconds,
                ratioPercent = DelayRatioPercent,
                rawPresets = delayPresets,
            },
        };

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(outputPath,
            JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
    }
}
