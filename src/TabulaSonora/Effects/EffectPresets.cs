using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TabulaSonora.Effects;

/// <summary>One allpass stage: where it reads, where it writes, and its two coefficients.</summary>
/// <param name="WriteTap">Ring offset written.</param>
/// <param name="ReadTap">Ring offset read.</param>
/// <param name="CoefA">Feed-back coefficient.</param>
/// <param name="CoefB">Feed-forward coefficient.</param>
public sealed record AllpassStage(
    [property: JsonPropertyName("writeTap")] int WriteTap,
    [property: JsonPropertyName("readTap")] int ReadTap,
    [property: JsonPropertyName("coefA")] double CoefA,
    [property: JsonPropertyName("coefB")] double CoefB);

/// <summary>One reverb tank: its eight output/routing taps and damping coefficients.</summary>
/// <param name="Taps">Ring offsets, keyed by their name in the dump.</param>
/// <param name="CoefA">Damping feedback coefficient.</param>
/// <param name="CoefB">Damping input coefficient.</param>
public sealed record ReverbTank(
    [property: JsonPropertyName("taps")] Dictionary<string, int> Taps,
    [property: JsonPropertyName("coefA")] double CoefA,
    [property: JsonPropertyName("coefB")] double CoefB);

/// <summary>The four allpasses nested inside the two tanks.</summary>
/// <param name="A0">Tank A, first.</param>
/// <param name="A1">Tank A, second.</param>
/// <param name="B0">Tank B, first.</param>
/// <param name="B1">Tank B, second.</param>
public sealed record TankAllpasses(
    [property: JsonPropertyName("A0")] AllpassStage A0,
    [property: JsonPropertyName("A1")] AllpassStage A1,
    [property: JsonPropertyName("B0")] AllpassStage B0,
    [property: JsonPropertyName("B1")] AllpassStage B1);

/// <summary>A complete reverb coefficient set for one GS type.</summary>
/// <param name="Diffusers">The four series allpass diffusers.</param>
/// <param name="TankA">First parallel tank.</param>
/// <param name="TankB">Second parallel tank.</param>
/// <param name="TankAllpasses">The allpasses nested in each tank.</param>
/// <param name="InjectionTap">Ring offset the diffuser chain is injected from.</param>
/// <param name="DampFeedback">Damping filter's feedback coefficient.</param>
/// <param name="DampInput">Damping filter's input coefficient.</param>
/// <param name="GainInput">Input gain into the ring.</param>
/// <param name="GainInjection">Gain applied at the injection tap.</param>
/// <param name="GainFeedback">Tank feedback gain — the decay control.</param>
/// <param name="GainOutput">Output gain.</param>
public sealed record ReverbPreset(
    [property: JsonPropertyName("diffusers")] AllpassStage[] Diffusers,
    [property: JsonPropertyName("tankA")] ReverbTank TankA,
    [property: JsonPropertyName("tankB")] ReverbTank TankB,
    [property: JsonPropertyName("tankAllpasses")] TankAllpasses TankAllpasses,
    [property: JsonPropertyName("injectionTap")] int InjectionTap,
    [property: JsonPropertyName("dampFeedback")] double DampFeedback,
    [property: JsonPropertyName("dampInput")] double DampInput,
    [property: JsonPropertyName("gainInput")] double GainInput,
    [property: JsonPropertyName("gainInjection")] double GainInjection,
    [property: JsonPropertyName("gainFeedback")] double GainFeedback,
    [property: JsonPropertyName("gainOutput")] double GainOutput);

/// <summary>A complete chorus coefficient set for one GS type.</summary>
/// <param name="LfoIncrement">Per-sample increment of the 24-bit sawtooth phase; zero means static.</param>
/// <param name="LpfA">Input one-pole feedback coefficient.</param>
/// <param name="LpfB">Input one-pole input coefficient.</param>
/// <param name="Tap1Depth">Modulation depth of the first tap.</param>
/// <param name="Tap1Base">Base delay of the first tap, in 12.12 fixed point.</param>
/// <param name="Tap2Depth">Modulation depth of the second tap.</param>
/// <param name="Tap2Base">Base delay of the second tap.</param>
/// <param name="Feedback">Feedback coefficient.</param>
/// <param name="GainWrite">Gain applied when writing into the ring.</param>
/// <param name="GainTap">Gain applied to the tapped output.</param>
public sealed record ChorusPreset(
    [property: JsonPropertyName("lfoIncrement")] int LfoIncrement,
    [property: JsonPropertyName("lpfA")] double LpfA,
    [property: JsonPropertyName("lpfB")] double LpfB,
    [property: JsonPropertyName("tap1Depth")] int Tap1Depth,
    [property: JsonPropertyName("tap1Base")] int Tap1Base,
    [property: JsonPropertyName("tap2Depth")] int Tap2Depth,
    [property: JsonPropertyName("tap2Base")] int Tap2Base,
    [property: JsonPropertyName("feedback")] double Feedback,
    [property: JsonPropertyName("gainWrite")] double GainWrite,
    [property: JsonPropertyName("gainTap")] double GainTap);

/// <summary>Reverb presets.</summary>
/// <param name="TypeNames">The eight GS type names.</param>
/// <param name="Default">The GM power-on default.</param>
/// <param name="Types">One preset per GS type.</param>
public sealed record ReverbPresets(
    [property: JsonPropertyName("typeNames")] string[] TypeNames,
    [property: JsonPropertyName("default")] ReverbPreset Default,
    [property: JsonPropertyName("types")] ReverbPreset[] Types)
{
    /// <summary>Send-bus gain at controller 127. A measured calibration, not DLL data.</summary>
    public const double SendAtFullScale = 1.02;
}

/// <summary>Chorus presets.</summary>
/// <param name="TypeNames">The eight GS type names.</param>
/// <param name="Default">The GM power-on default.</param>
/// <param name="Types">One preset per GS type.</param>
public sealed record ChorusPresets(
    [property: JsonPropertyName("typeNames")] string[] TypeNames,
    [property: JsonPropertyName("default")] ChorusPreset Default,
    [property: JsonPropertyName("types")] ChorusPreset[] Types)
{
    /// <summary>Send-bus gain at controller 127. A measured calibration, not DLL data.</summary>
    public const double SendAtFullScale = 0.3428;
}

/// <summary>System-delay presets and their conversion tables.</summary>
/// <param name="TypeNames">The ten GS type names.</param>
/// <param name="TimeMilliseconds">Raw time value 1–115 to milliseconds.</param>
/// <param name="RatioPercent">Raw ratio value 1–120 to percent.</param>
/// <param name="RawPresets">Ten raw GS parameter rows.</param>
public sealed record DelayPresets(
    [property: JsonPropertyName("typeNames")] string[] TypeNames,
    [property: JsonPropertyName("timeMilliseconds")] double[] TimeMilliseconds,
    [property: JsonPropertyName("ratioPercent")] double[] RatioPercent,
    [property: JsonPropertyName("rawPresets")] int[][] RawPresets)
{
    /// <summary>Send-bus gain at full part send. A measured calibration, not DLL data.</summary>
    public const double SendAtFullScale = 0.356;

    /// <summary>
    /// Fixed input pre-delay ahead of the feedback line, in samples — 60 ms at 32 kHz.
    /// </summary>
    /// <remarks>Measured from first arrival; it is not in the preset table.</remarks>
    public const int PreDelaySamples = 1920;

    /// <summary>Pre-lowpass coefficient ladder; index 0 bypasses, which every preset uses.</summary>
    public static ReadOnlySpan<double> PreLowPassCoefficients =>
        [0.0, 0.10, 0.18, 0.28, 0.40, 0.55, 0.70, 0.84];
}

/// <summary>
/// The coefficient sets for the three GS send effects.
/// </summary>
/// <remarks>
/// Read out of the live engine rather than fitted to audio: the reverb and chorus coefficients come
/// from its own runtime-computed state, and the delay presets from its preset table. Within each
/// effect the topology is identical across all types — only these numbers change, which is why one
/// implementation runs every type unchanged.
/// </remarks>
public sealed class EffectPresets
{
    /// <summary>Conventional file name, searched for beside the running assembly.</summary>
    public const string FileName = "presets.json";

    /// <summary>Environment variable that overrides where the presets are loaded from.</summary>
    public const string PathVariable = "TABULASONORA_PRESETS";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static EffectPresets? _current;
    private static readonly Lock Gate = new();

    [JsonConstructor]
    private EffectPresets(ReverbPresets reverb, ChorusPresets chorus, DelayPresets delay)
    {
        Reverb = reverb;
        Chorus = chorus;
        Delay = delay;
    }

    /// <summary>
    /// The presets in use.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No presets have been supplied and none could be found. The message explains how to build them.
    /// </exception>
    /// <remarks>
    /// These coefficients are read out of a running <c>SCCore.dll</c> and are therefore Roland's, not
    /// ours to redistribute — so they are <em>not</em> compiled into this assembly. Supply them with
    /// <see cref="Use"/>, point <see cref="PathVariable"/> at a generated file, or leave a
    /// <see cref="FileName"/> beside the assembly.
    /// </remarks>
    public static EffectPresets Default
    {
        get
        {
            lock (Gate)
            {
                return _current ??= Locate()
                    ?? throw new InvalidOperationException(
                        $"Effect presets not found. They are derived from SCCore.dll and are not shipped. " +
                        $"Generate them with 'tabula-sonora bake-presets <SCCore.dll> <tables-dir> " +
                        $"<{FileName}>', then either place the file beside the assembly, set " +
                        $"{PathVariable}, or call {nameof(EffectPresets)}.{nameof(Use)}().");
            }
        }
    }

    /// <summary>Whether presets are available, without throwing.</summary>
    public static bool IsAvailable
    {
        get
        {
            lock (Gate)
            {
                return (_current ??= Locate()) is not null;
            }
        }
    }

    /// <summary>Reverb coefficients.</summary>
    [JsonPropertyName("reverb")]
    public ReverbPresets Reverb { get; }

    /// <summary>Chorus coefficients.</summary>
    [JsonPropertyName("chorus")]
    public ChorusPresets Chorus { get; }

    /// <summary>Delay presets.</summary>
    [JsonPropertyName("delay")]
    public DelayPresets Delay { get; }

    /// <summary>Supplies the presets explicitly, for a host that manages its own data files.</summary>
    /// <param name="presets">The presets to use.</param>
    public static void Use(EffectPresets presets)
    {
        ArgumentNullException.ThrowIfNull(presets);
        lock (Gate)
        {
            _current = presets;
        }
    }

    /// <summary>Loads presets from a JSON file.</summary>
    /// <param name="path">Path to a file produced by <c>bake-presets</c>.</param>
    /// <returns>The presets.</returns>
    public static EffectPresets Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    /// <summary>Loads presets from a JSON stream.</summary>
    /// <param name="json">Stream positioned at the start of the document.</param>
    /// <returns>The presets.</returns>
    public static EffectPresets Load(Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<EffectPresets>(json, Options)
            ?? throw new InvalidDataException("Effect presets failed to deserialize.");
    }

    private static EffectPresets? Locate()
    {
        var overridden = Environment.GetEnvironmentVariable(PathVariable);
        if (!string.IsNullOrWhiteSpace(overridden) && File.Exists(overridden))
        {
            return Load(overridden);
        }

        var beside = Path.Combine(AppContext.BaseDirectory, FileName);
        return File.Exists(beside) ? Load(beside) : null;
    }
}
