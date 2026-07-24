namespace TabulaSonora;

/// <summary>
/// Writes a rendered result as a 16-bit stereo RIFF/WAVE stream.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately the plainest possible container: canonical 44-byte header, PCM, no metadata chunks.
/// The engine's own rate is written through unchanged, so a file carries the 32 kHz the samples were
/// produced at rather than whatever a host would have resampled them to.
/// </para>
/// <para>
/// One implementation for every caller, so a command-line render and a browser export of the same
/// song are byte-identical files rather than merely similar ones.
/// </para>
/// </remarks>
public static class WavWriter
{
    /// <summary>Bytes of header ahead of the sample data.</summary>
    public const int HeaderSize = 44;

    /// <summary>Writes a stereo render.</summary>
    /// <param name="destination">Stream to write to; left open.</param>
    /// <param name="left">Left channel.</param>
    /// <param name="right">Right channel; must be the same length as <paramref name="left"/>.</param>
    /// <param name="sampleRate">Sample rate to record in the header.</param>
    /// <exception cref="ArgumentException">The two channels are different lengths.</exception>
    /// <remarks>
    /// Fixed full-scale gain, no per-file normalisation, so absolute level stays comparable between
    /// renders — a quiet passage exported on its own must not come back louder than the same passage
    /// inside a whole song.
    /// </remarks>
    public static void Write(
        Stream destination,
        ReadOnlySpan<float> left,
        ReadOnlySpan<float> right,
        int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (left.Length != right.Length)
        {
            throw new ArgumentException("The two channels must be the same length.", nameof(right));
        }

        using var writer = new BinaryWriter(destination, System.Text.Encoding.UTF8, leaveOpen: true);

        var frames = left.Length;
        var dataBytes = frames * 4;

        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 4);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);

        for (var i = 0; i < frames; i++)
        {
            writer.Write((short)Math.Clamp(left[i] * 32767.0, -32768.0, 32767.0));
            writer.Write((short)Math.Clamp(right[i] * 32767.0, -32768.0, 32767.0));
        }
    }

    /// <summary>Writes a stereo render to a file.</summary>
    /// <param name="path">File to create, overwriting any existing one.</param>
    /// <param name="left">Left channel.</param>
    /// <param name="right">Right channel; must be the same length as <paramref name="left"/>.</param>
    /// <param name="sampleRate">Sample rate to record in the header.</param>
    public static void Write(string path, ReadOnlySpan<float> left, ReadOnlySpan<float> right, int sampleRate)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        using var stream = File.Create(path);
        Write(stream, left, right, sampleRate);
    }

    /// <summary>Writes a stereo render into a new byte array.</summary>
    /// <param name="left">Left channel.</param>
    /// <param name="right">Right channel; must be the same length as <paramref name="left"/>.</param>
    /// <param name="sampleRate">Sample rate to record in the header.</param>
    /// <returns>The complete file.</returns>
    /// <remarks>For a host that has somewhere to put bytes but no filesystem to write them to.</remarks>
    public static byte[] ToBytes(ReadOnlySpan<float> left, ReadOnlySpan<float> right, int sampleRate)
    {
        using var stream = new MemoryStream(HeaderSize + (left.Length * 4));
        Write(stream, left, right, sampleRate);
        return stream.ToArray();
    }
}
