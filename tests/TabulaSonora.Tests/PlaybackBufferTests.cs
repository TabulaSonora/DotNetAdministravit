using TabulaSonora.Player;

namespace TabulaSonora.Tests;

/// <summary>
/// The player's transport. Small, but every method clamps, and a seek that runs off either end is
/// the easiest way to make a player crash.
/// </summary>
public class PlaybackBufferTests
{
    private static PlaybackBuffer Buffer(int frames = 32000)
    {
        var left = new float[frames];
        var right = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            left[i] = 0.5f;
            right[i] = -0.25f;
        }

        return new PlaybackBuffer(left, right, 32000);
    }

    [Fact]
    public void ReportsItsDuration()
    {
        var buffer = Buffer(48000);
        Assert.Equal(1.5, buffer.Duration.TotalSeconds, 6);
        Assert.Equal(TimeSpan.Zero, buffer.Elapsed);
        Assert.False(buffer.AtEnd);
    }

    [Fact]
    public void ReadInterleavesAndAppliesGain()
    {
        var buffer = Buffer();
        var block = new float[8];

        var frames = buffer.Read(block, gain: 2.0f);

        Assert.Equal(4, frames);
        Assert.Equal(1.0f, block[0]);      // left  0.5 x 2
        Assert.Equal(-0.5f, block[1]);     // right -0.25 x 2
        Assert.Equal(4, buffer.Position);
    }

    [Fact]
    public void ReadPastTheEndFillsSilenceRatherThanRepeating()
    {
        // The device keeps being fed after the music stops; repeating the tail would be a glitch.
        var buffer = Buffer(frames: 3);
        var block = new float[8];

        var frames = buffer.Read(block, gain: 1.0f);

        Assert.Equal(3, frames);
        Assert.Equal(0f, block[6]);
        Assert.Equal(0f, block[7]);
        Assert.True(buffer.AtEnd);

        // A second read yields nothing at all, and does not run off the end.
        Assert.Equal(0, buffer.Read(block, 1.0f));
    }

    [Fact]
    public void SeekingClampsAtBothEnds()
    {
        var buffer = Buffer(32000);

        buffer.Seek(-10);
        Assert.Equal(0, buffer.Position);

        buffer.Seek(600);
        Assert.Equal(buffer.Length, buffer.Position);
        Assert.True(buffer.AtEnd);

        buffer.Position = -5;
        Assert.Equal(0, buffer.Position);
    }

    [Fact]
    public void SeekingMovesByWholeSeconds()
    {
        var buffer = Buffer(32000 * 10);
        buffer.Seek(3);
        Assert.Equal(3.0, buffer.Elapsed.TotalSeconds, 6);

        buffer.Seek(-1);
        Assert.Equal(2.0, buffer.Elapsed.TotalSeconds, 6);
    }

    [Fact]
    public void PeakLooksBackwardsFromThePosition()
    {
        var buffer = Buffer(1000);
        buffer.Position = 500;

        var (left, right) = buffer.PeakBefore(100);
        Assert.Equal(0.5f, left, 6);
        Assert.Equal(0.25f, right, 6);

        // At the very start there is nothing behind the cursor to measure.
        buffer.Position = 0;
        Assert.Equal((0f, 0f), buffer.PeakBefore(100));
    }

    [Fact]
    public void HandlesAnEmptyRender()
    {
        var buffer = new PlaybackBuffer([], [], 32000);
        var block = new float[4];

        Assert.True(buffer.AtEnd);
        Assert.Equal(0, buffer.Read(block, 1.0f));
        Assert.Equal(TimeSpan.Zero, buffer.Duration);
    }
}
