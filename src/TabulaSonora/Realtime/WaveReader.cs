using TabulaSonora.Dsp;

namespace TabulaSonora.Realtime;

/// <summary>
/// Plays one decoded wave forward at a rate that can change every sample.
/// </summary>
/// <remarks>
/// <para>
/// The offline sampler integrates a whole note's rates into an array of read positions and resamples
/// in one pass. A block renderer cannot: it does not know how long the note will last. This keeps the
/// read position as running state instead, and reproduces the same three traversals.
/// </para>
/// <para>
/// A forward loop reads the buffer the wave already carries, so no per-voice allocation is involved. A
/// ping-pong traversal has to be generated as it is read — the predictor keeps accumulating in both
/// directions rather than subtracting, so the material never repeats and cannot be precomputed for an
/// unbounded note — and is written into a small ring the read window slides along.
/// </para>
/// </remarks>
internal sealed class WaveReader
{
    /// <summary>Samples of ping-pong material kept live. Must be a power of two.</summary>
    private const int PingPongWindow = 1024;

    private readonly Interpolator _interpolator;

    private DecodedWave? _wave;
    private float[]? _buffer;
    private double _position;
    private bool _finished;

    private float[]? _ring;
    private long _generated;
    private int _pathIndex;
    private int _pathLeg;
    private bool _pathEnded;
    private int _predictor;

    /// <summary>Creates a reader.</summary>
    /// <param name="interpolator">The 4-tap resampler.</param>
    public WaveReader(Interpolator interpolator)
    {
        ArgumentNullException.ThrowIfNull(interpolator);
        _interpolator = interpolator;
    }

    /// <summary>Whether the wave has run out and is now silent.</summary>
    public bool Finished => _finished;

    /// <summary>Starts a wave from its beginning.</summary>
    /// <param name="wave">The decoded wave.</param>
    public void Start(DecodedWave wave)
    {
        ArgumentNullException.ThrowIfNull(wave);

        _wave = wave;
        _position = 0.0;
        _finished = false;
        _buffer = wave.IsLooping ? wave.LoopBuffer : wave.Samples;

        if (wave.Mode == SamplerMode.PingPong)
        {
            _ring ??= new float[PingPongWindow];
            _generated = 0;
            _pathIndex = 0;
            _pathLeg = 0;
            _pathEnded = false;
            _predictor = 0;
        }
    }

    /// <summary>Releases the wave so nothing is held between notes.</summary>
    public void Stop()
    {
        _wave = null;
        _buffer = null;
        _finished = true;
    }

    /// <summary>Reads one sample and advances by a playback rate.</summary>
    /// <param name="ratio">Playback rate relative to the recorded pitch.</param>
    /// <returns>The sample.</returns>
    public float Next(double ratio)
    {
        var wave = _wave;
        if (wave is null || _finished)
        {
            return 0f;
        }

        var sample = wave.Mode == SamplerMode.PingPong ? ReadPingPong() : ReadLinear(wave);
        _position += ratio;

        if (wave.IsLooping)
        {
            // Subtracting the period is the same wrap the offline path takes modulo, and it keeps the
            // fractional phase exact however long the note holds.
            var limit = wave.DataEnd + 1;
            var period = wave.LoopPeriod;
            while (_position >= limit)
            {
                _position -= period;
            }
        }

        return sample;
    }

    private float ReadLinear(DecodedWave wave)
    {
        var buffer = _buffer!;

        if (wave.IsLooping)
        {
            return _interpolator.Sample(buffer, _position);
        }

        // One-shot: hold the last sample, then go silent. The engine's own gate is the read position
        // reaching the end of the data, not the envelope.
        var end = wave.DataEnd - 1;
        if (_position >= end)
        {
            _finished = true;
            return 0f;
        }

        return _interpolator.Sample(buffer, _position);
    }

    private float ReadPingPong()
    {
        var index = (long)Math.Floor(_position);
        Generate(index + 2);

        if (_pathEnded && index + 2 >= _generated)
        {
            _finished = true;
            return 0f;
        }

        return _interpolator.SampleRing(_ring!, index, _position - index);
    }

    /// <summary>
    /// Extends the ping-pong stream far enough to cover a read at an index.
    /// </summary>
    /// <remarks>
    /// The index walks up to the data end, turns around and walks back to the loop point, then turns
    /// around again. The index is unchanged on a turnaround, so that sample's delta is applied twice;
    /// and the predictor accumulates in both directions rather than subtracting, which makes the
    /// backward leg the wave inverted and time-reversed. Both turnarounds are continuous by
    /// construction — there is no seam and no phase jump.
    /// </remarks>
    private void Generate(long upTo)
    {
        var wave = _wave!;
        var mask = PingPongWindow - 1;

        while (_generated <= upTo && !_pathEnded)
        {
            var index = _pathIndex;

            switch (_pathLeg)
            {
                case 0:
                    _pathIndex++;
                    if (_pathIndex > wave.DataEnd)
                    {
                        if (wave.DataEnd <= wave.LoopStart)
                        {
                            _pathEnded = true;
                        }
                        else
                        {
                            _pathLeg = 1;
                            _pathIndex = wave.DataEnd;
                        }
                    }

                    break;

                case 1:
                    _pathIndex--;
                    if (_pathIndex < wave.LoopStart)
                    {
                        _pathLeg = 2;
                        _pathIndex = wave.LoopStart;
                    }

                    break;

                default:
                    _pathIndex++;
                    if (_pathIndex > wave.DataEnd)
                    {
                        _pathLeg = 1;
                        _pathIndex = wave.DataEnd;
                    }

                    break;
            }

            _predictor += wave.Steps[index];
            _ring![(int)(_generated & mask)] = (float)(_predictor * WaveCodec.OutputScale);
            _generated++;
        }
    }
}
