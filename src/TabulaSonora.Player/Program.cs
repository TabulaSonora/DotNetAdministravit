using System.Globalization;
using Ownaudio.Core;
using OwnaudioNET;
using TabulaSonora;
using TabulaSonora.Patches;
using TabulaSonora.Player;
using TabulaSonora.Rom;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Usage();
    return 0;
}

if (args[0] is "--list-devices" or "devices")
{
    return ListDevices();
}

if (args.Length < 2)
{
    Usage();
    return 1;
}

var dllPath = args[0];
var midiPath = args[1];

var options = new RenderOptions();
var sampleRate = NoteRenderer.SampleRate;   // the engine's own rate; no conversion by default
var bufferFrames = 512;
var gain = 1.0f;
var latencyMs = 150;
string? device = null;

// MME is PortAudio's fallback host on Windows and is far too coarse for smooth playback.
var host = OperatingSystem.IsWindows() ? EngineHostType.WASAPI : EngineHostType.None;

try
{
    for (var i = 2; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--map":
                options = options with { Map = (ToneMap)int.Parse(args[++i], CultureInfo.InvariantCulture) };
                break;
            case "--rate": sampleRate = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
            case "--buffer": bufferFrames = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
            case "--latency": latencyMs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
            case "--gain": gain = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
            case "--device": device = args[++i]; break;
            case "--host":
                if (!Enum.TryParse(args[++i], ignoreCase: true, out host))
                {
                    Console.Error.WriteLine(
                        $"error: unknown host '{args[i]}'. One of: {string.Join(", ", Enum.GetNames<EngineHostType>())}");
                    return 1;
                }

                break;
            case "--tail":
                options = options with { TailSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture) };
                break;
            case "--no-reverb": options = options with { Reverb = false }; break;
            case "--no-chorus": options = options with { Chorus = false }; break;
            case "--no-delay": options = options with { Delay = false }; break;
            case "--mute": options = options with { Channels = Channels(options.Channels, args[++i], true) }; break;
            case "--solo": options = options with { Channels = Channels(options.Channels, args[++i], false) }; break;
            default:
                Console.Error.WriteLine($"error: unknown option '{args[i]}'");
                return 1;
        }
    }
}
catch (IndexOutOfRangeException)
{
    Console.Error.WriteLine("error: an option is missing its value.");
    return 1;
}

try
{
    return Play(dllPath, midiPath, options, sampleRate, bufferFrames, latencyMs, gain, device, host);
}
catch (RomIdentityException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}
catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static void Usage() => Console.WriteLine("""
    tabula-sonora-play - play a MIDI file through the Sound Canvas engine

      tabula-sonora-play <SCCore.dll> <song.mid> [options]
      tabula-sonora-play --list-devices

    Options
      --map 1..4        SC-55, SC-88, SC-88Pro, SC-8820 (default 4)
      --device <name>   output device; substring match, or an index
      --host <api>      WASAPI (default on Windows), WDMKS, ASIO, COREAUDIO, ALSA, ...
                        MME is PortAudio's fallback and is too coarse for smooth playback
      --rate <hz>       output rate (default 32000, the engine's own rate)
      --buffer <frames> device buffer (default 512)
      --latency <ms>    how far ahead to keep the device fed (default 150)
      --gain <x>        linear output gain (default 1.0)
      --tail <sec>      release tail past the last event
      --mute 1,2        silence channels, as a mixer labels them (1-16)
      --solo 5,6        hear only these channels
      --no-reverb, --no-chorus, --no-delay

    While playing
      space   pause / resume          left / right   seek 5s
      , / .   seek 30s                home           back to the start
      q, esc  quit
    """);

static int ListDevices()
{
    OwnaudioNet.Initialize(Config(NoteRenderer.SampleRate, 512, EngineHostType.None), false, 8);
    try
    {
        var devices = OwnaudioNet.GetOutputDevices();
        Console.WriteLine($"{devices.Count} output device(s):");
        for (var i = 0; i < devices.Count; i++)
        {
            Console.WriteLine($"  [{i}] {devices[i]}");
        }
    }
    finally
    {
        OwnaudioNet.Shutdown();
    }

    return 0;
}

static AudioConfig Config(int sampleRate, int bufferFrames, EngineHostType host) => new()
{
    SampleRate = sampleRate,
    Channels = 2,
    BufferSize = bufferFrames,
    EnableOutput = true,
    EnableInput = false,
    HostType = host,
};

static ChannelMask Channels(ChannelMask? mask, string list, bool mute)
{
    mask ??= new ChannelMask();
    foreach (var part in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
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

static int Play(
    string dllPath, string midiPath, RenderOptions options,
    int sampleRate, int bufferFrames, int latencyMs, float gain, string? device, EngineHostType host)
{
    using var rom = RomImage.Open(dllPath, RomVerification.Quick);

    // Render up front. The engine synthesises offline, so this is not a real-time path -- but at
    // better than ten times realtime the wait is short, and it makes seeking free and exact.
    Console.Write($"Rendering {Path.GetFileName(midiPath)} ");
    var started = Environment.TickCount64;
    var lastStep = -1;

    var progress = new Progress<double>(p =>
    {
        var step = (int)(p * 20);
        if (step > lastStep)
        {
            lastStep = step;
            Console.Write('.');
        }
    });

    var renderer = SequenceRenderer.Create(rom);
    var result = renderer.RenderFile(midiPath, options with { Progress = progress });
    var elapsed = (Environment.TickCount64 - started) / 1000.0;

    var buffer = new PlaybackBuffer(result.Left, result.Right, result.SampleRate);
    Console.WriteLine();
    Console.WriteLine(
        $"  {buffer.Duration:mm\\:ss}, {result.NoteCount} notes, peak {result.Peak:F3} - " +
        $"rendered in {elapsed:F1}s ({buffer.Duration.TotalSeconds / Math.Max(elapsed, 0.001):F0}x realtime)");

    if (result.Peak * gain > 1.0f)
    {
        Console.WriteLine($"  note: peak x gain is {result.Peak * gain:F2}, so the output will clip.");
    }

    if (sampleRate != buffer.SampleRate)
    {
        Console.WriteLine(
            $"  note: device set to {sampleRate} Hz but the engine renders at {buffer.SampleRate} Hz, " +
            "so the driver will convert.");
    }

    // A generous circular buffer: the send loop is paced from managed code, so it must tolerate the
    // scheduler losing it for a few milliseconds without the device running dry.
    OwnaudioNet.Initialize(Config(sampleRate, bufferFrames, host), false, 16);
    try
    {
        if (device is not null && !SelectDevice(device))
        {
            return 1;
        }

        OwnaudioNet.Start();
        Console.WriteLine(
            $"  {host} · {sampleRate} Hz · {bufferFrames} frames · ~{latencyMs} ms buffered");
        Console.WriteLine("  space pause | left/right 5s | , / . 30s | home restart | q quit");
        Console.WriteLine();

        Stream(buffer, gain, bufferFrames, latencyMs);
    }
    finally
    {
        OwnaudioNet.Stop();
        OwnaudioNet.Shutdown();
    }

    Console.WriteLine();
    return 0;
}

static bool SelectDevice(string device)
{
    var devices = OwnaudioNet.GetOutputDevices();

    if (int.TryParse(device, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
    {
        if (index < 0 || index >= devices.Count)
        {
            Console.Error.WriteLine($"error: no output device with index {index}; there are {devices.Count}.");
            return false;
        }

        // Index selection lives on the underlying engine; the wrapper only takes names.
        OwnaudioNet.Engine!.UnderlyingEngine.SetOutputDeviceByIndex(index);
        Console.WriteLine($"  output: {devices[index]}");
        return true;
    }

    var match = devices.FindIndex(d =>
        (d?.ToString() ?? string.Empty).Contains(device, StringComparison.OrdinalIgnoreCase));

    if (match < 0)
    {
        Console.Error.WriteLine($"error: no output device matching '{device}'. Try --list-devices.");
        return false;
    }

    OwnaudioNet.Engine!.UnderlyingEngine.SetOutputDeviceByIndex(match);
    Console.WriteLine($"  output: {devices[match]}");
    return true;
}

static void Stream(PlaybackBuffer buffer, float gain, int bufferFrames, int latencyMs)
{
    var block = new float[bufferFrames * 2];
    var silence = new float[bufferFrames * 2];
    var paused = false;
    var untilRedraw = 0;

    // Console.KeyAvailable throws when stdin is redirected, so a piped or scripted run just plays
    // straight through rather than failing.
    var interactive = !Console.IsInputRedirected;

    // Send does not block: it hands the block to a circular buffer and returns. Without pacing the
    // loop would push the whole song in a fraction of a second and overrun the buffer. The device's
    // own consumption counter is the clock to pace against.
    //
    // The lead has to cover the scheduler's worst nap, not the average one: a Sleep(1) on Windows
    // routinely lasts 15 ms, so a lead of a few blocks drains dry and the result is choppy even
    // though nothing reports an underrun.
    var lead = Math.Max((long)bufferFrames * 4, (long)latencyMs * buffer.SampleRate / 1000);
    long sent = 0;

    while (true)
    {
        while (interactive && Console.KeyAvailable)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Spacebar: paused = !paused; break;
                case ConsoleKey.LeftArrow: buffer.Seek(-5); break;
                case ConsoleKey.RightArrow: buffer.Seek(5); break;
                case ConsoleKey.OemComma: buffer.Seek(-30); break;
                case ConsoleKey.OemPeriod: buffer.Seek(30); break;
                case ConsoleKey.Home: buffer.Position = 0; break;
                case ConsoleKey.Q or ConsoleKey.Escape:
                    Console.WriteLine();
                    return;
            }

            untilRedraw = 0;
        }

        // Wait until the device has drained enough that another block fits within the target lead.
        var pumped = OwnaudioNet.Engine?.TotalPumpedFrames ?? sent;
        if (sent - pumped >= lead)
        {
            Thread.Sleep(1);

            if (--untilRedraw <= 0)
            {
                Draw(buffer, paused, gain);
                untilRedraw = Math.Max(1, buffer.SampleRate / bufferFrames / 20);
            }

            continue;
        }

        if (paused)
        {
            // Keep the device fed so it does not underrun, but hold position.
            OwnaudioNet.Send(silence);
        }
        else
        {
            buffer.Read(block, gain);
            OwnaudioNet.Send(block);
        }

        sent += bufferFrames;

        if (!paused && buffer.AtEnd)
        {
            // Let what is already queued finish before tearing the device down.
            while ((OwnaudioNet.Engine?.TotalPumpedFrames ?? sent) < sent)
            {
                Thread.Sleep(2);
            }

            Draw(buffer, paused, gain);
            Console.WriteLine();
            return;
        }

        if (--untilRedraw <= 0)
        {
            Draw(buffer, paused, gain);
            untilRedraw = Math.Max(1, buffer.SampleRate / bufferFrames / 20);
        }
    }
}

static void Draw(PlaybackBuffer buffer, bool paused, float gain)
{
    const int width = 32;

    var fraction = buffer.Length == 0 ? 0 : buffer.Position / (double)buffer.Length;
    var filled = Math.Clamp((int)(fraction * width), 0, width);
    var bar = (new string('=', filled) + (filled < width ? ">" : string.Empty)).PadRight(width);

    var (left, right) = buffer.PeakBefore(2048);

    // Underruns are the one thing a player should never hide: a glitch the listener hears but the
    // display does not mention looks like a fault in the engine.
    var underruns = OwnaudioNet.Engine?.TotalUnderruns ?? 0;
    var warning = underruns > 0 ? $"  {underruns} underrun{(underruns == 1 ? "" : "s")}" : string.Empty;

    Console.Write(
        $"\r  {(paused ? "||" : " >")} [{bar}] {buffer.Elapsed:mm\\:ss} / {buffer.Duration:mm\\:ss}  " +
        $"{Meter(left * gain)}{Meter(right * gain)}{warning}   ");
}

static string Meter(float peak)
{
    // Eight blocks at roughly 6 dB each, so the tallest means close to full scale.
    const string Blocks = "▁▂▃▄▅▆▇█";
    if (peak <= 0.0005f)
    {
        return "▁";
    }

    var db = 20 * Math.Log10(peak);
    return Blocks[(int)Math.Clamp((db + 42) / 6, 0, Blocks.Length - 1)].ToString();
}
