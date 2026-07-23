namespace TabulaSonora.Player;

/// <summary>
/// Something the transport can play: a position, a length, and blocks on demand.
/// </summary>
/// <remarks>
/// Two things implement this. <see cref="PlaybackBuffer"/> holds a finished render, which makes
/// seeking free and the peak meters exact. <see cref="StreamingSource"/> synthesises as it plays,
/// which makes playback start immediately and costs nothing to hold a long song in memory.
/// </remarks>
public interface IPlaybackSource
{
    /// <summary>Sample rate of the material.</summary>
    int SampleRate { get; }

    /// <summary>Total frames.</summary>
    int Length { get; }

    /// <summary>Total duration.</summary>
    TimeSpan Duration { get; }

    /// <summary>Current play position in frames.</summary>
    int Position { get; set; }

    /// <summary>Current play position as a time.</summary>
    TimeSpan Elapsed { get; }

    /// <summary>Whether playback has reached the end.</summary>
    bool AtEnd { get; }

    /// <summary>Moves the position by a number of seconds, clamped to the material.</summary>
    /// <param name="seconds">How far to move; negative rewinds.</param>
    void Seek(double seconds);

    /// <summary>Fills an interleaved stereo block from the current position and advances.</summary>
    /// <param name="destination">Interleaved stereo destination; its length must be even.</param>
    /// <param name="gain">Linear gain applied on the way out.</param>
    /// <returns>Frames actually produced, before any zero fill.</returns>
    int Read(Span<float> destination, float gain);

    /// <summary>Peak absolute level over the block that ends at the current position.</summary>
    /// <param name="frames">How far back to look.</param>
    /// <returns>Left and right peaks.</returns>
    (float Left, float Right) PeakBefore(int frames);
}
