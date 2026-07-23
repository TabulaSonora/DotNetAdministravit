using TabulaSonora.Realtime;

namespace TabulaSonora.Player;

/// <summary>
/// A song synthesised as it plays, through the engine's block-based voice loop.
/// </summary>
/// <remarks>
/// <para>
/// Playback starts at once — there is nothing to wait for — and a song of any length costs the same
/// few kilobytes of buffers. What it gives up against <see cref="PlaybackBuffer"/> is lookahead: the
/// peak meters can only show what has already been played, and seeking has to rebuild the engine's
/// state by replaying the file's controllers up to the target, which drops whatever tail was ringing.
/// </para>
/// <para>
/// Rendering happens on the thread that calls <see cref="Read"/>, not under the audio callback, so a
/// slow block eats into the send loop's lead rather than glitching the device outright.
/// </para>
/// </remarks>
public sealed class StreamingSource : IPlaybackSource
{
    /// <summary>Frames of played audio kept for the meters.</summary>
    private const int HistoryFrames = 8192;

    private readonly SequencePlayer _player;
    private readonly float[] _historyLeft = new float[HistoryFrames];
    private readonly float[] _historyRight = new float[HistoryFrames];

    private float[] _left = [];
    private float[] _right = [];
    private int _history;

    /// <summary>Creates a source over a player.</summary>
    /// <param name="player">The player to pull blocks from.</param>
    /// <param name="tailSeconds">Seconds of release and effect tail played past the last event.</param>
    public StreamingSource(SequencePlayer player, double tailSeconds)
    {
        ArgumentNullException.ThrowIfNull(player);

        _player = player;
        Length = (int)(player.LastEventPosition + (tailSeconds * SampleRate));
    }

    /// <inheritdoc/>
    public int SampleRate => ToneGenerator.SampleRate;

    /// <inheritdoc/>
    public int Length { get; }

    /// <inheritdoc/>
    public TimeSpan Duration => TimeSpan.FromSeconds(Length / (double)SampleRate);

    /// <inheritdoc/>
    /// <remarks>Setting this seeks, which replays the file's state up to the new position.</remarks>
    public int Position
    {
        get => (int)_player.Position;
        set
        {
            _player.Seek(Math.Clamp(value, 0, Length));
            Array.Clear(_historyLeft);
            Array.Clear(_historyRight);
        }
    }

    /// <inheritdoc/>
    public TimeSpan Elapsed => TimeSpan.FromSeconds(Position / (double)SampleRate);

    /// <inheritdoc/>
    public bool AtEnd => Position >= Length;

    /// <inheritdoc/>
    public void Seek(double seconds) => Position = Position + (int)(seconds * SampleRate);

    /// <inheritdoc/>
    public int Read(Span<float> destination, float gain)
    {
        var frames = destination.Length / 2;
        if (_left.Length < frames)
        {
            _left = new float[frames];
            _right = new float[frames];
        }

        var available = Math.Clamp(Length - Position, 0, frames);
        if (available > 0)
        {
            _player.Render(_left.AsSpan(0, available), _right.AsSpan(0, available));
        }

        for (var i = 0; i < available; i++)
        {
            destination[i * 2] = _left[i] * gain;
            destination[(i * 2) + 1] = _right[i] * gain;

            _historyLeft[_history] = _left[i];
            _historyRight[_history] = _right[i];
            _history = (_history + 1) % HistoryFrames;
        }

        // Past the end, keep feeding the device silence rather than stopping abruptly.
        destination[(available * 2)..].Clear();
        return available;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Only what has already been played, and only as far back as the history ring reaches — a
    /// streaming source has no future to look at.
    /// </remarks>
    public (float Left, float Right) PeakBefore(int frames)
    {
        var count = Math.Min(frames, HistoryFrames);
        float left = 0, right = 0;

        for (var i = 1; i <= count; i++)
        {
            var index = ((_history - i) % HistoryFrames + HistoryFrames) % HistoryFrames;
            left = Math.Max(left, Math.Abs(_historyLeft[index]));
            right = Math.Max(right, Math.Abs(_historyRight[index]));
        }

        return (left, right);
    }
}
