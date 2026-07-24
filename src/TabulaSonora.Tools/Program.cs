using System.Globalization;
using TabulaSonora.Rom;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Usage();
    return 0;
}

try
{
    return args[0] switch
    {
        "extract-tables" => ExtractTables(args.AsSpan(1)),
        "info" => Info(args.AsSpan(1)),
        "dump-effect" => DumpEffect(args.AsSpan(1)),
        "render" => Render(args.AsSpan(1)),
        "prepare" => Prepare(args.AsSpan(1)),
        "render-note" => RenderNote(args.AsSpan(1)),
        "bench" => Bench(args.AsSpan(1)),
        _ => Fail($"Unknown command '{args[0]}'."),
    };
}
catch (RomIdentityException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}
catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException
                              or InvalidOperationException)
{
    // Missing locally generated data is an expected, actionable condition -- report it as advice
    // rather than as a crash.
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static void Usage()
{
    Console.WriteLine("""
        tabula-sonora - tooling for the Tabula Sonora engine

          prepare <SCCore.dll> [--presets <path>] [--tables <directory>]
              Everything the engine needs, from one DLL. Verifies the build, reads the
              delay presets, harvests the reverb and chorus coefficients, and writes
              presets.json. This is the only setup step; run it once.

          render <SCCore.dll> <input.mid> <output.wav> [options]
              Render a MIDI file. --stream uses the real-time block loop instead of
              rendering note by note. Run with no options for the full list.

          info <SCCore.dll>
              Print the build identity and the wave-ROM block map.

          bench <SCCore.dll> [input.mid] [--iterations N]
              Time the render path stage by stage. Reports each effect, one voice,
              and -- given a MIDI file -- the offline renderer and the block loop.

          extract-tables <SCCore.dll> <output-directory>
              Write the 48 static tables as byte-for-byte .bin slices. The library reads
              them from the DLL directly, so this is only for tests and inspection.

        Everything derived from the DLL is Roland's and is generated locally, never
        redistributed. The library looks for presets.json beside itself, or wherever
        TABULASONORA_PRESETS points.
        """);
}

static int Fail(string message)
{
    Console.Error.WriteLine($"error: {message}");
    Console.Error.WriteLine();
    Usage();
    return 1;
}

static int ExtractTables(ReadOnlySpan<string> args)
{
    if (args.Length != 2)
    {
        return Fail("extract-tables needs a DLL path and an output directory.");
    }

    var dllPath = args[0];
    var outputDirectory = args[1];

    using var rom = RomImage.Open(dllPath);
    Directory.CreateDirectory(outputDirectory);

    var written = 0;
    long bytes = 0;
    foreach (var entry in rom.Manifest.CachedTables)
    {
        var data = rom.Read(entry);
        File.WriteAllBytes(Path.Combine(outputDirectory, entry.Name), data);
        written++;
        bytes += data.Length;
    }

    Console.WriteLine($"Verified {Path.GetFileName(dllPath)} against the pinned build.");
    Console.WriteLine($"Wrote {written} tables ({bytes:N0} bytes) to {outputDirectory}");
    return 0;
}

static int Info(ReadOnlySpan<string> args)
{
    if (args.Length != 1)
    {
        return Fail("info needs a DLL path.");
    }

    using var rom = RomImage.Open(args[0], RomVerification.Quick);
    var dll = rom.Manifest.Dll;
    var timestamp = rom.ReadPeTimestamp();
    var sha256 = rom.ComputeSha256();
    var matches = string.Equals(sha256, dll.Sha256, StringComparison.OrdinalIgnoreCase);

    Console.WriteLine($"path          {rom.Path}");
    Console.WriteLine($"size          {rom.Length:N0} bytes (pinned {dll.Size:N0})");
    Console.WriteLine($"pe timestamp  {timestamp} " +
        $"({DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC)");
    Console.WriteLine($"sha256        {sha256}");
    // The DLL carries no version resource, so the hash is what says which release this is.
    Console.WriteLine($"              {(matches
        ? $"matches the pinned build ({dll.Product} {dll.Version})"
        : "DOES NOT MATCH the pinned build")}");
    Console.WriteLine();
    Console.WriteLine(
        $"{rom.Manifest.CachedTables.Count} static tables, {rom.Manifest.LiveRegions.Count} live regions");
    Console.WriteLine();

    var waveRom = new WaveRom(rom);
    Console.WriteLine("wave ROM blocks");
    foreach (var bank in new[] { 0, 1 })
    {
        for (var region = 0; region < WaveRom.RegionCount(bank); region++)
        {
            var offset = waveRom.BankBase(bank) + ((long)region * WaveRom.RegionSize);
            var header = rom.Read(offset, 0x50);
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "  bank {0} region {1,2}  0x{2:x8}  {3,-10} {4}",
                bank == 0 ? "A" : "B",
                region,
                offset,
                Ascii(header, 0x20),
                Ascii(header, 0x30)));
        }
    }

    return 0;
}

static int Render(ReadOnlySpan<string> args)
{
    if (args.Length < 3)
    {
        return Fail("render <SCCore.dll> <input.mid> <output.wav> [--map 1..4] [--drum-map 0|1] " +
                    "[--tail SEC] [--end SEC] [--stream] [--no-reverb] [--no-chorus] [--no-delay] " +
                    "[--mute 1,2] [--solo 5,6]   (channels are 1-16)");
    }

    var dllPath = args[0];
    var midiPath = args[1];
    var outputPath = args[2];

    var options = new TabulaSonora.RenderOptions();
    var stream = false;

    for (var i = 3; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--stream": stream = true; break;
            case "--map":
                options = options with { Map = (TabulaSonora.Patches.ToneMap)int.Parse(args[++i], CultureInfo.InvariantCulture) };
                break;
            // Not reachable from a MIDI file at all: the module picks the drum map from the part's
            // internal bank code, and that translation is not reversed. This is the only way to hear
            // the second map's kits outside the browser.
            case "--drum-map":
                options = options with { DrumMapRow = int.Parse(args[++i], CultureInfo.InvariantCulture) };
                break;
            case "--tail":
                options = options with { TailSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture) };
                break;
            case "--end":
                options = options with { EndSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture) };
                break;
            case "--reverb": options = options with { Reverb = true }; break;
            case "--chorus": options = options with { Chorus = true }; break;
            case "--delay": options = options with { Delay = true }; break;
            case "--no-reverb": options = options with { Reverb = false }; break;
            case "--no-chorus": options = options with { Chorus = false }; break;
            case "--no-delay": options = options with { Delay = false }; break;
            case "--mute":
                options = options with { Channels = WithChannels(options.Channels, args[++i], mute: true) };
                break;
            case "--solo":
                options = options with { Channels = WithChannels(options.Channels, args[++i], mute: false) };
                break;
            default: return Fail($"Unknown option '{args[i]}'.");
        }
    }

    using var rom = RomImage.Open(dllPath, RomVerification.Quick);
    var started = Environment.TickCount64;

    var result = stream
        ? RenderStreaming(rom, midiPath, options)
        : TabulaSonora.SequenceRenderer.Create(rom).RenderFile(midiPath, options);

    TabulaSonora.WavWriter.Write(outputPath, result.Left, result.Right, result.SampleRate);

    var seconds = result.Left.Length / (double)result.SampleRate;
    var routing = options.Channels is { IsDefault: false } mask
        ? $", channels {string.Join(",", mask.AudibleChannels().Select(c => c + 1))}"
        : string.Empty;

    Console.WriteLine(
        $"{Path.GetFileName(midiPath)} -> {outputPath}: {seconds:F1}s stereo, " +
        $"{result.NoteCount} notes, peak {result.Peak:F4}{routing}, " +
        $"took {(Environment.TickCount64 - started) / 1000.0:F1}s");
    return 0;
}

/// <summary>Applies a comma-separated 1-based channel list to the mask.</summary>
static TabulaSonora.ChannelMask WithChannels(TabulaSonora.ChannelMask? mask, string list, bool mute)
{
    mask ??= new TabulaSonora.ChannelMask();
    foreach (var part in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        // Channels are given the way a mixer shows them, 1-16.
        var channel = int.Parse(part, CultureInfo.InvariantCulture) - 1;
        if (mute)
        {
            mask.SetMuted(channel, true);
        }
        else
        {
            mask.SetSoloed(channel, true);
        }
    }

    return mask;
}

static int Prepare(ReadOnlySpan<string> args)
{
    if (args.Length == 0)
    {
        TabulaSonora.Tools.Preparer.Explain(Console.WriteLine);
        Console.WriteLine();
        return Fail("prepare <SCCore.dll> [--presets <path>] [--tables <directory>]");
    }

    var dllPath = args[0];

    // Default beside the library sources, which the build copies to every output directory.
    var presetsPath = Path.Combine(FindRepositoryRoot(), "src", "TabulaSonora", "Effects", "presets.json");
    string? tablesDirectory = null;

    for (var i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--presets": presetsPath = args[++i]; break;
            case "--tables": tablesDirectory = args[++i]; break;
            default: return Fail($"Unknown option '{args[i]}'.");
        }
    }

    return TabulaSonora.Tools.Preparer.Prepare(dllPath, presetsPath, tablesDirectory, Console.WriteLine);
}

/// <summary>Walks up from the executable to the repository root, falling back to the current directory.</summary>
static string FindRepositoryRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "DotNetAdministravitTabulaSonora.slnx")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    return Directory.GetCurrentDirectory();
}

static int Bench(ReadOnlySpan<string> args)
{
    if (args.Length == 0)
    {
        return Fail("bench <SCCore.dll> [input.mid] [--iterations N]");
    }

    var dllPath = args[0];
    string? midiPath = null;
    var iterations = 5;

    for (var i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--iterations": iterations = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
            default:
                if (args[i].StartsWith('-'))
                {
                    return Fail($"Unknown option '{args[i]}'.");
                }

                midiPath = args[i];
                break;
        }
    }

    return TabulaSonora.Tools.Bencher.Run(dllPath, midiPath, iterations, Console.WriteLine);
}

static int RenderNote(ReadOnlySpan<string> args)
{
    if (args.Length < 6)
    {
        return Fail("render-note <SCCore.dll> <program> <note> <velocity> <holdSec> <out.f32> [map]");
    }

    using var rom = RomImage.Open(args[0], RomVerification.Quick);
    var renderer = new TabulaSonora.NoteRenderer(rom);

    var program = int.Parse(args[1], CultureInfo.InvariantCulture);
    var note = int.Parse(args[2], CultureInfo.InvariantCulture);
    var velocity = int.Parse(args[3], CultureInfo.InvariantCulture);
    var hold = double.Parse(args[4], CultureInfo.InvariantCulture);
    var map = args.Length > 6
        ? (TabulaSonora.Patches.ToneMap)int.Parse(args[6], CultureInfo.InvariantCulture)
        : TabulaSonora.Patches.ToneMap.Sc8820;

    var voice = renderer.RenderNote(program, note, velocity, hold, tailSeconds: 1.8, map);

    using var stream = File.Create(args[5]);
    using var writer = new BinaryWriter(stream);
    for (var i = 0; i < voice.Left.Length; i++)
    {
        writer.Write(voice.Left[i]);
        writer.Write(voice.Right[i]);
    }

    Console.WriteLine($"{voice.Name}: {voice.Left.Length} frames");
    return 0;
}

/// <summary>
/// Renders through the real-time block loop rather than note by note.
/// </summary>
/// <remarks>
/// Useful for A/B: the two paths share their DSP, so what this exposes is the difference the
/// architecture makes — a 64-voice limit that actually steals, live controllers, and effect types that
/// can change mid-song.
/// </remarks>
static TabulaSonora.RenderResult RenderStreaming(
    RomImage rom, string midiPath, TabulaSonora.RenderOptions options)
{
    var generator = TabulaSonora.Realtime.ToneGenerator.Create(rom, new TabulaSonora.Realtime.ToneGeneratorOptions
    {
        Map = options.Map,
        DrumChannel = options.DrumChannel,
        Reverb = options.Reverb,
        Chorus = options.Chorus,
        Delay = options.Delay,
        ReverbType = options.ReverbType,
        ChorusType = options.ChorusType,
        DelayType = options.DelayType,
        DrumRingSeconds = options.DrumRingSeconds,
        Channels = options.Channels,
    });

    // Not a ToneGeneratorOptions value: the row is settable while the engine runs, because it is what
    // the module would have taken from a bank select. Carried over here so --stream and the offline
    // path resolve the same kits from the same file.
    generator.DrumMapRow = options.DrumMapRow;

    return TabulaSonora.Realtime.SequencePlayer
        .FromFile(generator, midiPath)
        .RenderToEnd(options.TailSeconds, options.EndSeconds);
}

static int DumpEffect(ReadOnlySpan<string> args)
{
    if (args.Length != 4)
    {
        return Fail("dump-effect needs <reverb|chorus|delay> <type> <samples> <out.f32>");
    }

    var kind = args[0];
    var type = int.Parse(args[1], CultureInfo.InvariantCulture);
    var samples = int.Parse(args[2], CultureInfo.InvariantCulture);
    var path = args[3];

    var input = new float[samples];
    input[0] = 1f;
    var left = new float[samples];
    var right = new float[samples];

    TabulaSonora.Effects.IEffect effect = kind switch
    {
        "reverb" => TabulaSonora.Effects.Reverb.ForType(type),
        "chorus" => TabulaSonora.Effects.Chorus.ForType(type),
        "delay" => TabulaSonora.Effects.SystemDelay.ForType(type),
        _ => throw new ArgumentException($"Unknown effect '{kind}'."),
    };

    effect.Process(input, left, right);

    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    for (var i = 0; i < samples; i++)
    {
        writer.Write(left[i]);
        writer.Write(right[i]);
    }

    Console.WriteLine($"wrote {path}: {samples} stereo samples");
    return 0;
}

static string Ascii(byte[] header, int offset)
{
    var span = header.AsSpan(offset, 16);
    var end = span.IndexOf((byte)0);
    return System.Text.Encoding.ASCII.GetString(end < 0 ? span : span[..end]).Trim();
}
