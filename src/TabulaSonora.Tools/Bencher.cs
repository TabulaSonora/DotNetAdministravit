using System.Diagnostics;
using System.Globalization;
using TabulaSonora.Effects;
using TabulaSonora.Realtime;
using TabulaSonora.Rom;

namespace TabulaSonora.Tools;

/// <summary>
/// Times the render path stage by stage, so a performance change can be attributed rather than
/// guessed at.
/// </summary>
/// <remarks>
/// <para>
/// Stages are reported separately because they move independently: the effects are single-sample
/// feedback loops, the block loop is dominated by the sampler and filter, and the offline renderer
/// spends much of its time in elementwise mix loops that neither of the others runs.
/// </para>
/// <para>
/// Best-of-N rather than the mean. Measurement noise on a desktop is one-sided — a scheduling
/// preemption can only make an iteration slower — so the minimum is the closest estimate of the
/// work actually being done, and it is far more stable run to run than an average.
/// </para>
/// </remarks>
public static class Bencher
{
    /// <summary>Seconds of audio each effect stage is timed over.</summary>
    private const double EffectSeconds = 10.0;

    /// <summary>The note the single-voice stage renders, held then left to ring.</summary>
    private const double NoteHoldSeconds = 1.0;

    /// <summary>Runs the benchmark.</summary>
    /// <param name="dllPath">Path to the pinned <c>SCCore.dll</c>.</param>
    /// <param name="midiPath">A MIDI file for the sequence stages, or <see langword="null"/> to skip them.</param>
    /// <param name="iterations">How many times each stage runs; the fastest is reported.</param>
    /// <param name="log">Receives the report.</param>
    /// <returns>Zero on success.</returns>
    public static int Run(string dllPath, string? midiPath, int iterations, Action<string> log)
    {
        ArgumentException.ThrowIfNullOrEmpty(dllPath);
        ArgumentNullException.ThrowIfNull(log);
        iterations = Math.Max(1, iterations);

        using var rom = RomImage.Open(dllPath, RomVerification.Quick);
        var notes = new TabulaSonora.NoteRenderer(rom);

        log($"{Environment.ProcessorCount} cores, vector width "
            + $"{System.Numerics.Vector<float>.Count} floats / {System.Numerics.Vector<double>.Count} doubles, "
            + $"best of {iterations}");
        log(string.Empty);
        log("stage                        ms      xrt     alloc");
        log("-----------------------------------------------------");

        // Effects first: they need no MIDI and no wave data, so they always report.
        var effectSamples = (int)(EffectSeconds * TabulaSonora.NoteRenderer.SampleRate);
        var excitation = Excitation(effectSamples);
        var wetLeft = new float[effectSamples];
        var wetRight = new float[effectSamples];

        foreach (var (name, factory) in new (string, Func<IEffect>)[]
        {
            ("reverb", () => Reverb.ForType(null)),
            ("chorus", () => Chorus.ForType(null)),
            ("delay", () => SystemDelay.ForType(0)),
        })
        {
            var effect = factory();
            Report(log, name, EffectSeconds, iterations, () =>
            {
                effect.Reset();
                effect.Process(excitation, wetLeft, wetRight);
            });
        }

        // One voice, which isolates the sampler, the filter and the envelopes from the mix.
        Report(log, "note (piano, 1s hold)", NoteHoldSeconds + 1.8, iterations,
            () => notes.RenderNote(program: 0, note: 60, velocity: 100, NoteHoldSeconds, tailSeconds: 1.8));

        if (midiPath is null)
        {
            log(string.Empty);
            log("Pass a MIDI file to also time the offline renderer and the block loop.");
            return 0;
        }

        // Parsed once: what is being timed is rendering, not the SMF reader.
        var sequence = TabulaSonora.Midi.SequenceBuilder.Build(
            TabulaSonora.Midi.SmfReader.Read(midiPath, TabulaSonora.NoteRenderer.SampleRate));
        var seconds = sequence.LastEventPosition / (double)TabulaSonora.NoteRenderer.SampleRate;

        var renderer = new TabulaSonora.SequenceRenderer(notes);
        Report(log, "offline sequence", seconds, iterations, () => renderer.Render(sequence));

        Report(log, "block loop (stream)", seconds, iterations, () =>
            SequencePlayer
                .FromFile(ToneGenerator.Create(rom), midiPath)
                .RenderToEnd(tailSeconds: 2.2));

        log(string.Empty);
        log($"{Path.GetFileName(midiPath)}: {seconds:F1}s, {sequence.Notes.Count} notes");
        return 0;
    }

    /// <summary>
    /// A deterministic broadband signal for the effect stages.
    /// </summary>
    /// <remarks>
    /// Not an impulse. A decaying impulse response falls into denormal range within a second or two,
    /// where x86 arithmetic is an order of magnitude slower, and the stage would then be measuring the
    /// denormal penalty rather than the algorithm. Keeping the rings excited keeps every sample normal.
    /// </remarks>
    private static float[] Excitation(int count)
    {
        var samples = new float[count];
        var state = 0x2545F491u;
        for (var i = 0; i < count; i++)
        {
            // xorshift32, so the signal is identical on every machine and every run.
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            samples[i] = ((state >> 8) / (float)0x800000) - 1f;
        }

        return samples;
    }

    private static void Report(Action<string> log, string name, double audioSeconds, int iterations, Action stage)
    {
        // One untimed pass so tiering and the first-call allocations are not attributed to the work.
        stage();

        var best = double.MaxValue;
        long allocated = 0;

        for (var i = 0; i < iterations; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            stage();
            watch.Stop();

            if (watch.Elapsed.TotalMilliseconds < best)
            {
                best = watch.Elapsed.TotalMilliseconds;
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }
        }

        log(string.Format(
            CultureInfo.InvariantCulture,
            "{0,-24} {1,8:F1} {2,8:F1} {3,9}",
            name,
            best,
            audioSeconds * 1000.0 / best,
            Bytes(allocated)));
    }

    private static string Bytes(long value) => value switch
    {
        < 1024 => $"{value}B",
        < 1024 * 1024 => $"{value / 1024.0:F0}K",
        _ => $"{value / (1024.0 * 1024.0):F0}M",
    };
}
