using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TabulaSonora.Rom;

namespace TabulaSonora.Tools;

/// <summary>
/// Builds the effect preset file from a local <c>SCCore.dll</c> and the coefficient dumps taken
/// from it.
/// </summary>
/// <remarks>
/// <para>
/// The reverb and chorus coefficients come from <c>scdec revdump</c> / <c>chodump</c>, which read
/// the engine's own runtime state; the delay presets are read straight out of the DLL. All of it is
/// Roland's, so it is generated locally and never redistributed.
/// </para>
/// <para>
/// This replaces the Python generator that previously did the same job, so nothing in the build or
/// run path needs Python.
/// </para>
/// </remarks>
public static partial class PresetBaker
{
    /// <summary>
    /// File offset of <c>g_delay_preset_tbl</c>.
    /// </summary>
    /// <remarks>
    /// Documented at virtual address <c>0x181893930</c>; this region takes the same −0x1000 section
    /// adjustment as the <c>.rdata</c> curve tables, which was confirmed by locating the table's
    /// bytes in the file.
    /// </remarks>
    public const long DelayPresetOffset = 0x1892930;

    /// <summary>Number of GS delay macros.</summary>
    public const int DelayTypeCount = 10;

    /// <summary>Bytes per delay preset row.</summary>
    public const int DelayPresetStride = 10;

    private static readonly string[] ReverbTypeNames =
        ["Room1", "Room2", "Room3", "Hall1", "Hall2", "Plate", "Delay", "PanDelay"];

    private static readonly string[] ChorusTypeNames =
        ["Chorus1", "Chorus2", "Chorus3", "Chorus4", "FeedbackChorus", "Flanger", "ShortDelay", "ShortDelayFB"];

    private static readonly string[] DelayTypeNames =
    [
        "Delay1", "Delay2", "Delay3", "Delay4", "PanDelay1",
        "PanDelay2", "PanDelay3", "PanDelay4", "DelayToReverb", "PanRepeat",
    ];

    private static readonly string[] TankTapNames =
        ["tap10", "tap14", "tap18", "tap1C", "tap20", "tap24", "tap28", "tap2C"];

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

    /// <summary>Builds the preset file.</summary>
    /// <param name="dllPath">Path to the pinned <c>SCCore.dll</c>.</param>
    /// <param name="tablesDirectory">Directory holding the <c>scdec</c> reverb and chorus dumps.</param>
    /// <param name="outputPath">Where to write the JSON.</param>
    /// <returns>A one-line summary of what was written.</returns>
    public static string Bake(string dllPath, string tablesDirectory, string outputPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(dllPath);
        ArgumentException.ThrowIfNullOrEmpty(tablesDirectory);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        var reverb = new object[ReverbTypeNames.Length];
        for (var t = 0; t < ReverbTypeNames.Length; t++)
        {
            reverb[t] = ParseReverb(ReadDump(tablesDirectory, $"reverb_type_{t}_{ReverbTypeNames[t]}.txt"));
        }

        var chorus = new object[ChorusTypeNames.Length];
        for (var t = 0; t < ChorusTypeNames.Length; t++)
        {
            chorus[t] = ParseChorus(ReadDump(tablesDirectory, $"chorus_type_{t}_{ChorusTypeNames[t]}.txt"));
        }

        var reverbDefault = ParseReverb(ReadDump(tablesDirectory, "reverb_gm_default.txt"));
        var chorusDefault = ParseChorus(ReadDump(tablesDirectory, "chorus_gm_default.txt"));

        using var rom = RomImage.Open(dllPath, RomVerification.Full);
        var delayPresets = ReadDelayPresets(rom);

        var document = new
        {
            _note = "Reverb and chorus coefficients dumped from a live SCCore.dll via scdec revdump/" +
                    "chodump; delay presets read from the DLL's own preset table. Roland-derived: " +
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

        return $"wrote {outputPath}: {new FileInfo(outputPath).Length:N0} bytes " +
               $"({reverb.Length} reverb, {chorus.Length} chorus, {delayPresets.Length} delay types)";
    }

    private static int[][] ReadDelayPresets(RomImage rom)
    {
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

        // The first row is quoted in the notes; a wrong offset would silently produce plausible junk.
        if (presets[0][1] != 97 || presets[0][4] != 127 || presets[0][8] != 80)
        {
            throw new InvalidDataException(
                $"Delay preset table at 0x{DelayPresetOffset:x} does not look right " +
                $"(first row [{string.Join(",", presets[0])}]).");
        }

        return presets;
    }

    private static string ReadDump(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Coefficient dump '{name}' not found in '{directory}'. Produce it with " +
                $"'scdec <SCCore.dll> revdump|chodump'.", path);
        }

        return File.ReadAllText(path);
    }

    private static object ParseReverb(string text)
    {
        var diffusers = new object[4];
        for (var i = 0; i < 4; i++)
        {
            diffusers[i] = ParseAllpass(Line(text, $"ap{i}"));
        }

        return new
        {
            diffusers,
            tankA = ParseTank(Line(text, "LA")),
            tankB = ParseTank(Line(text, "LB")),
            tankAllpasses = new
            {
                A0 = ParseAllpass(Line(text, "LA.sA0")),
                A1 = ParseAllpass(Line(text, "LA.sA1")),
                B0 = ParseAllpass(Line(text, "LB.sA0")),
                B1 = ParseAllpass(Line(text, "LB.sA1")),
            },
            injectionTap = Hex(Match(text, @"injTap=([0-9A-Fa-f]+)")),
            dampFeedback = Real(text, @"aa8_fb=(\S+)"),
            dampInput = Real(text, @"aac_in=(\S+)"),
            gainInput = Real(text, @"ed70_in=(\S+)"),
            gainInjection = Real(text, @"ee70_inj=(\S+)"),
            gainFeedback = Real(text, @"eef0_fb=(\S+)"),
            gainOutput = Real(text, @"edf0_out=(\S+)"),
        };
    }

    private static object ParseAllpass(string line) => new
    {
        writeTap = Hex(Match(line, @"writeTap=([0-9A-Fa-f]+)")),
        readTap = Hex(Match(line, @"readTap=([0-9A-Fa-f]+)")),
        coefA = Real(line, @"coefA=(\S+)"),
        coefB = Real(line, @"coefB=(\S+)"),
    };

    private static object ParseTank(string line)
    {
        var taps = new Dictionary<string, int>(TankTapNames.Length, StringComparer.Ordinal);
        foreach (var name in TankTapNames)
        {
            taps[name] = Hex(Match(line, $@"{name}=([0-9A-Fa-f]+)"));
        }

        return new { taps, coefA = Real(line, @"cA=(\S+)"), coefB = Real(line, @"cB=(\S+)") };
    }

    private static object ParseChorus(string text)
    {
        // Only the first snapshot is needed, and the right-hand stage is gated off for every GS type.
        var snapshotB = text.IndexOf("# snapshot B", StringComparison.Ordinal);
        if (snapshotB >= 0)
        {
            text = text[..snapshotB];
        }

        // These prefixes run straight into their first field ("L lfoPhase="), so no word boundary.
        var lfo = Line(text, "L lfo", wordBoundary: false);
        var taps = Line(text, "L tap1", wordBoundary: false);
        var gains = Line(text, "L gains", wordBoundary: false);

        return new
        {
            lfoIncrement = Integer(lfo, @"lfoInc=(-?\d+)"),
            lpfA = Real(lfo, @"lpfA=(\S+)"),
            lpfB = Real(lfo, @"lpfB=(\S+)"),
            tap1Depth = Integer(taps, @"tap1 depth=(-?\d+)"),
            tap1Base = Integer(taps, @"tap1 depth=-?\d+ base=(-?\d+)"),
            tap2Depth = Integer(taps, @"tap2 depth=(-?\d+)"),
            tap2Base = Integer(taps, @"tap2 depth=-?\d+ base=(-?\d+)"),
            feedback = Real(taps, @"fbCoef=(\S+)"),
            gainWrite = Real(gains, @"writeIn=(\S+)"),
            gainTap = Real(gains, @"tapOut=(\S+)"),
        };
    }

    /// <summary>Finds the line starting with a prefix.</summary>
    /// <param name="text">The dump.</param>
    /// <param name="prefix">Prefix to look for.</param>
    /// <param name="wordBoundary">
    /// Require a space or end of line after the prefix. Needed for the reverb tanks, where
    /// <c>LA</c> must not match <c>LA.sA0</c>.
    /// </param>
    /// <returns>The matching line.</returns>
    private static string Line(string text, string prefix, bool wordBoundary = true)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (!trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (!wordBoundary || trimmed.Length == prefix.Length || trimmed[prefix.Length] == ' ')
            {
                return trimmed;
            }
        }

        throw new InvalidDataException($"No '{prefix}' line in the dump.");
    }

    private static string Match(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
        if (!match.Success)
        {
            throw new InvalidDataException($"Pattern '{pattern}' not found in the dump.");
        }

        return match.Groups[1].Value;
    }

    private static int Hex(string value) => int.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static int Integer(string text, string pattern) =>
        int.Parse(Match(text, pattern), CultureInfo.InvariantCulture);

    private static double Real(string text, string pattern) =>
        double.Parse(Match(text, pattern), NumberStyles.Float, CultureInfo.InvariantCulture);
}
