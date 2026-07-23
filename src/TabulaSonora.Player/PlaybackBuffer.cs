namespace TabulaSonora.Player;

/// <summary>
/// A rendered song held in memory, with a transport over it.
/// </summary>
/// <remarks>
/// <para>
/// The whole song is rendered before playback starts, which makes seeking free and exact and lets the
/// peak meters look at material that already exists. <see cref="StreamingSource"/> is the alternative,
/// and the usual one: it synthesises through the block loop as it plays.
/// </para>
/// </remarks>
public sealed class PlaybackBuffer : IPlaybackSource
{
    private readonly float[] _left;
    private readonly float[] _right;
    private int _position;

    /// <summary>Wraps a rendered result.</summary>
    /// <param name="left">Left channel.</param>
    /// <param name="right">Right channel.</param>
    /// <param name="sampleRate">Sample rate of the material.</param>
    public PlaybackBuffer(float[] left, float[] right, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        _left = left;
        _right = right;
        SampleRate = sampleRate;
    }

    /// <summary>Sample rate of the rendered material.</summary>
    public int SampleRate { get; }

    /// <summary>Total frames.</summary>
    public int Length => _left.Length;

    /// <summary>Total duration.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds(Length / (double)SampleRate);

    /// <summary>Current play position in frames.</summary>
    public int Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, Length);
    }

    /// <summary>Current play position as a time.</summary>
    public TimeSpan Elapsed => TimeSpan.FromSeconds(_position / (double)SampleRate);

    /// <summary>Whether playback has reached the end.</summary>
    public bool AtEnd => _position >= Length;

    /// <summary>Moves the position by a number of seconds, clamped to the material.</summary>
    /// <param name="seconds">How far to move; negative rewinds.</param>
    public void Seek(double seconds) =>
        Position = _position + (int)(seconds * SampleRate);

    /// <summary>
    /// Fills an interleaved stereo block from the current position and advances.
    /// </summary>
    /// <param name="destination">Interleaved stereo destination; its length must be even.</param>
    /// <param name="gain">Linear gain applied on the way out.</param>
    /// <returns>Frames actually taken from the material, before any zero fill.</returns>
    public int Read(Span<float> destination, float gain)
    {
        var frames = destination.Length / 2;
        var available = Math.Clamp(Length - _position, 0, frames);

        for (var i = 0; i < available; i++)
        {
            destination[i * 2] = _left[_position + i] * gain;
            destination[(i * 2) + 1] = _right[_position + i] * gain;
        }

        // Past the end, keep feeding the device silence rather than stopping abruptly.
        destination[(available * 2)..].Clear();

        _position += available;
        return available;
    }

    /// <summary>Peak absolute level over the block that ends at the current position.</summary>
    /// <param name="frames">How far back to look.</param>
    /// <returns>Left and right peaks.</returns>
    public (float Left, float Right) PeakBefore(int frames)
    {
        var end = Math.Clamp(_position, 0, Length);
        var start = Math.Max(0, end - frames);

        float left = 0, right = 0;
        for (var i = start; i < end; i++)
        {
            left = Math.Max(left, Math.Abs(_left[i]));
            right = Math.Max(right, Math.Abs(_right[i]));
        }

        return (left, right);
    }
}
