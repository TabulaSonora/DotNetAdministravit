using System.Collections.Concurrent;
using TabulaSonora.Patches;
using TabulaSonora.Rom;

namespace TabulaSonora.Dsp;

/// <summary>Which sampler variant plays a wave.</summary>
public enum SamplerMode
{
    /// <summary>Plays once and stops. The predictor is zeroed at the end.</summary>
    OneShot,

    /// <summary>Forward loop. Rewinds the delta index and keeps the predictor.</summary>
    Loop,

    /// <summary>Bidirectional. Walks up to the end, turns around, and walks back to the loop point.</summary>
    PingPong,
}

/// <summary>A wave decoded from the ROM, with everything playback needs.</summary>
/// <param name="Samples">The decoded samples.</param>
/// <param name="Steps">The shifted per-sample deltas, needed to rebuild a ping-pong traversal.</param>
/// <param name="LoopStart">Index of the loop point.</param>
/// <param name="DataEnd">Number of decodable samples.</param>
/// <param name="Mode">Which sampler variant applies.</param>
public sealed record DecodedWave(float[] Samples, int[] Steps, int LoopStart, int DataEnd, SamplerMode Mode)
{
    /// <summary>
    /// The buffer a forward loop reads from, or <see langword="null"/> for the other modes.
    /// </summary>
    /// <remarks>
    /// The 4-tap window reaches two samples past the read index, so the buffer must carry the samples
    /// the loop wraps to rather than whatever follows it. It is the wave plus a few wrapped samples —
    /// a fixed size, independent of how long a note holds — so it is built once with the wave and
    /// shared by every voice that plays it.
    /// </remarks>
    public float[]? LoopBuffer { get; init; }

    /// <summary>
    /// The forward loop's period in samples.
    /// </summary>
    /// <remarks>
    /// Inclusive of the data end: one pass plays <c>LoopStart..DataEnd</c>. Dropping that last sample
    /// is inaudible on a long loop but detunes a short single-cycle one outright — a 63 against 64
    /// sample period is 27 cents sharp.
    /// </remarks>
    public int LoopPeriod => DataEnd - LoopStart + 1;

    /// <summary>Whether a forward loop is actually in effect.</summary>
    public bool IsLooping => Mode == SamplerMode.Loop && DataEnd > LoopStart + 1 && LoopBuffer is not null;

    /// <summary>
    /// Whether <see cref="Samples"/> has been turned round because the wave plays backwards.
    /// </summary>
    /// <remarks>
    /// Playback does not consult this: the samples are already in the order they are read, so a
    /// reverse wave is an ordinary one-shot from here on. It is recorded because a buffer that no
    /// longer matches the order <see cref="Steps"/> is in would otherwise be silently confusing —
    /// the steps stay forward, and nothing that reads them applies to a reverse wave.
    /// </remarks>
    public bool Reversed { get; init; }
}

/// <summary>
/// Wave playback: decode, then resample at a moving rate while honouring the loop.
/// </summary>
/// <remarks>
/// Looping happens in the delta domain — the index rewinds while the predictor carries on — so loops,
/// ping-pong and reverse are all seamless by construction with no crossfade anywhere.
/// </remarks>
public sealed class Sampler
{
    private readonly WaveRom _rom;
    private readonly Interpolator _interpolator;
    private readonly ConcurrentDictionary<(int Region, int Loop, int Start), DecodedWave?> _cache = new();

    /// <summary>Creates the sampler.</summary>
    /// <param name="rom">The wave ROM.</param>
    /// <param name="interpolator">The 4-tap resampler.</param>
    public Sampler(WaveRom rom, Interpolator interpolator)
    {
        ArgumentNullException.ThrowIfNull(rom);
        ArgumentNullException.ThrowIfNull(interpolator);
        _rom = rom;
        _interpolator = interpolator;
    }

    /// <summary>Decodes a wave, caching the result.</summary>
    /// <param name="descriptor">The wave's descriptor.</param>
    /// <returns>The decoded wave, or <see langword="null"/> when the descriptor has no usable data.</returns>
    public DecodedWave? Decode(WaveDescriptor descriptor) =>
        _cache.GetOrAdd((descriptor.Region, descriptor.Loop, descriptor.Start), _ => DecodeCore(descriptor));

    private DecodedWave? DecodeCore(WaveDescriptor descriptor)
    {
        var streams = _rom.ReadStreams(descriptor.Region, descriptor.Loop, descriptor.Start);
        if (streams is null)
        {
            return null;
        }

        var (delta, scale, sampleCount, alignedLoop) = streams.Value;

        // One extra step: the ping-pong sampler applies the delta at the turnaround index.
        var steps = new int[sampleCount + 1];
        for (var i = 0; i < steps.Length; i++)
        {
            steps[i] = WaveCodec.Step(delta[i], WaveCodec.ScaleAt(scale, i));
        }

        // Decode one sample past the data end. The forward loop is inclusive of that index -- its
        // period is dataEnd - loopStart + 1 -- so the sample genuinely exists in the delta stream and
        // is needed to close the loop. Stopping one short makes the wrap substitute the loop's first
        // sample instead, which then plays twice per pass; on a short single-cycle loop that is an
        // audible click every period.
        var samples = new float[sampleCount + 1];
        var predictor = 0;
        for (var i = 0; i <= sampleCount; i++)
        {
            predictor += steps[i];
            samples[i] = (float)(predictor * WaveCodec.OutputScale);
        }

        var loopStart = Math.Max(0, descriptor.End - alignedLoop);

        // A reverse wave is the same data read the other way, so it is turned round here rather than
        // given a downward-walking read path. Two things fall out of that. Both renderers get it at
        // once, because they share this sampler and neither knows the difference; and the seam
        // problems that make the engine's own backwards walk delicate do not arise, because the
        // predictor is integrated forward exactly as for any other wave and only the finished
        // samples are reversed.
        //
        // Playing one is always a one-shot. Statically 202 of the 218 reverse descriptors already
        // collapse end onto start, and the engine reconfigures the rest to match: every reverse wave
        // the harness captured came back with its loop point above its end, which is the register
        // setup for running downward once. See ReverseWavesUseDifferentRuntimeRegistersThanTheStaticDescriptor.
        if (descriptor.Reverse)
        {
            Array.Reverse(samples);
            return new DecodedWave(samples, steps, loopStart, sampleCount, SamplerMode.OneShot)
            {
                Reversed = true,
            };
        }

        // The descriptor's loop flag is NOT what gates looping: it reads zero for piano, so trusting
        // it makes held notes run out as one-shots. What decides is whether a sustain region exists.
        var mode = descriptor.PingPong ? SamplerMode.PingPong
            : sampleCount - loopStart > 0 ? SamplerMode.Loop
            : SamplerMode.OneShot;

        var wave = new DecodedWave(samples, steps, loopStart, sampleCount, mode);
        return mode == SamplerMode.Loop && sampleCount > loopStart + 1
            ? wave with { LoopBuffer = BuildLoopBuffer(wave) }
            : wave;
    }

    /// <summary>Plays a wave at a constant rate.</summary>
    /// <param name="wave">The decoded wave.</param>
    /// <param name="sampleCount">How many output samples to produce.</param>
    /// <param name="ratio">Playback rate relative to the recorded pitch.</param>
    /// <returns>The rendered samples.</returns>
    public float[] Play(DecodedWave wave, int sampleCount, double ratio)
    {
        var positions = new double[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            positions[i] = i * ratio;
        }

        return Play(wave, positions);
    }

    /// <summary>Plays a wave at a rate that changes per sample.</summary>
    /// <param name="wave">The decoded wave.</param>
    /// <param name="sampleCount">How many output samples to produce.</param>
    /// <param name="ratios">Instantaneous rate per sample; shorter input holds its last value.</param>
    /// <returns>The rendered samples.</returns>
    /// <remarks>
    /// Vibrato and pitch envelopes are applied by integrating the instantaneous rate into the read
    /// position, so the first sample always reads position zero.
    /// </remarks>
    public float[] PlayVariable(DecodedWave wave, int sampleCount, ReadOnlySpan<double> ratios)
    {
        var positions = new double[sampleCount];
        var accumulated = 0.0;
        for (var i = 0; i < sampleCount; i++)
        {
            positions[i] = accumulated;
            var rate = ratios.Length == 0 ? 1.0 : ratios[Math.Min(i, ratios.Length - 1)];
            accumulated += rate;
        }

        return Play(wave, positions);
    }

    private float[] Play(DecodedWave wave, double[] positions)
    {
        ArgumentNullException.ThrowIfNull(wave);

        var output = new float[positions.Length];
        if (positions.Length == 0)
        {
            return output;
        }

        var loopStart = wave.LoopStart;
        var dataEnd = wave.DataEnd;

        if (wave.Mode == SamplerMode.PingPong)
        {
            var buffer = BuildPingPongBuffer(wave, (int)positions[^1] + 3);
            _interpolator.Resample(buffer, positions, output);
            return output;
        }

        if (wave.IsLooping)
        {
            var period = wave.LoopPeriod;
            var wrapped = new double[positions.Length];
            for (var i = 0; i < positions.Length; i++)
            {
                var p = positions[i];
                wrapped[i] = p < dataEnd + 1
                    ? p
                    : loopStart + Modulo(p - (dataEnd + 1), period);
            }

            _interpolator.Resample(wave.LoopBuffer!, wrapped, output);
            return output;
        }

        // One-shot: hold the last sample, then go silent.
        var held = new double[positions.Length];
        for (var i = 0; i < positions.Length; i++)
        {
            held[i] = Math.Min(positions[i], dataEnd - 1);
        }

        _interpolator.Resample(wave.Samples, held, output);

        for (var i = 0; i < positions.Length; i++)
        {
            if (held[i] >= dataEnd - 1)
            {
                output[i] = 0f;
            }
        }

        return output;
    }

    /// <summary>
    /// Builds the buffer a forward loop reads from, with the wrapped samples appended.
    /// </summary>
    private static float[] BuildLoopBuffer(DecodedWave wave)
    {
        // Both the data and the repeated region are inclusive of the data end, matching the loop's
        // own period.
        var period = wave.LoopPeriod;
        var required = wave.DataEnd + 4;
        var buffer = new List<float>(required);

        for (var i = 0; i <= wave.DataEnd && i < wave.Samples.Length; i++)
        {
            buffer.Add(wave.Samples[i]);
        }

        while (buffer.Count < required && period > 0)
        {
            for (var i = wave.LoopStart; i <= wave.DataEnd && buffer.Count < required; i++)
            {
                buffer.Add(wave.Samples[i]);
            }
        }

        return [.. buffer];
    }

    /// <summary>
    /// Rebuilds a ping-pong traversal by integrating the deltas along the back-and-forth index path.
    /// </summary>
    /// <remarks>
    /// The index is unchanged on a turnaround, so that sample's delta is applied twice; and the
    /// predictor keeps accumulating in both directions rather than subtracting, which makes the
    /// backward leg the wave inverted and time-reversed. Both turnarounds are continuous by
    /// construction — there is no seam and no phase jump.
    /// </remarks>
    private static float[] BuildPingPongBuffer(DecodedWave wave, int required)
    {
        required = Math.Max(1, required);

        var indices = new List<int>(required + 8);
        for (var i = 0; i <= wave.DataEnd && indices.Count < required; i++)
        {
            indices.Add(i);
        }

        while (indices.Count < required)
        {
            for (var i = wave.DataEnd; i >= wave.LoopStart && indices.Count < required; i--)
            {
                indices.Add(i);
            }

            for (var i = wave.LoopStart; i <= wave.DataEnd && indices.Count < required; i++)
            {
                indices.Add(i);
            }

            if (wave.DataEnd <= wave.LoopStart)
            {
                break;
            }
        }

        var buffer = new float[indices.Count];
        var predictor = 0;
        for (var i = 0; i < indices.Count; i++)
        {
            predictor += wave.Steps[indices[i]];
            buffer[i] = (float)(predictor * WaveCodec.OutputScale);
        }

        return buffer;
    }

    private static double Modulo(double value, int period)
    {
        var result = value % period;
        return result < 0 ? result + period : result;
    }
}
