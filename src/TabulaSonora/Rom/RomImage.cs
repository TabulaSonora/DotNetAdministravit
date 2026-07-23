using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace TabulaSonora.Rom;

/// <summary>How strictly <see cref="RomImage.Open"/> checks that the file is the pinned build.</summary>
public enum RomVerification
{
    /// <summary>Verify size, PE timestamp and the full SHA-256. The default, and the only safe choice.</summary>
    Full,

    /// <summary>Verify size and PE timestamp but skip the SHA-256. Faster; use only in tight loops.</summary>
    Quick,

    /// <summary>Perform no checks at all. For experimenting with a different build; offsets will be wrong.</summary>
    None,
}

/// <summary>
/// Thrown when the supplied <c>SCCore.dll</c> is not the build the table offsets are pinned to.
/// </summary>
public sealed class RomIdentityException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Description of the mismatch.</param>
    public RomIdentityException(string message) : base(message)
    {
    }
}

/// <summary>
/// Read-only access to <c>SCCore.dll</c> <em>as a data file</em>.
/// </summary>
/// <remarks>
/// <para>
/// The DLL is never loaded as code — no <c>LoadLibrary</c>, no P/Invoke, no native dependency. It is
/// opened as a plain file and sliced at the offsets recorded in <see cref="TableManifest"/>, which is
/// what lets this library stay fully managed and portable.
/// </para>
/// <para>
/// Reads go through <see cref="RandomAccess"/> rather than a memory map, so no 27 MB copy is ever
/// resident and no unsafe code is required.
/// </para>
/// </remarks>
public sealed class RomImage : IDisposable
{
    private readonly SafeFileHandle _handle;
    private bool _disposed;

    private RomImage(string path, SafeFileHandle handle, long length, TableManifest manifest)
    {
        Path = path;
        _handle = handle;
        Length = length;
        Manifest = manifest;
    }

    /// <summary>Path the image was opened from.</summary>
    public string Path { get; }

    /// <summary>Length of the file in bytes.</summary>
    public long Length { get; }

    /// <summary>The offset map these reads are interpreted through.</summary>
    public TableManifest Manifest { get; }

    /// <summary>Opens and verifies an <c>SCCore.dll</c>.</summary>
    /// <param name="path">Path to the DLL.</param>
    /// <param name="verification">How strictly to check the build identity.</param>
    /// <param name="manifest">Offset map to use; defaults to the embedded one.</param>
    /// <returns>An open image.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="RomIdentityException">The file is not the pinned build.</exception>
    public static RomImage Open(
        string path,
        RomVerification verification = RomVerification.Full,
        TableManifest? manifest = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        manifest ??= TableManifest.Default;

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"SCCore.dll not found at '{path}'.", path);
        }

        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var length = RandomAccess.GetLength(handle);
            var image = new RomImage(path, handle, length, manifest);
            if (verification != RomVerification.None)
            {
                image.Verify(verification);
            }

            return image;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Reads <paramref name="length"/> bytes starting at <paramref name="fileOffset"/>.</summary>
    /// <param name="fileOffset">Offset within the DLL file.</param>
    /// <param name="length">Number of bytes to read.</param>
    /// <returns>A newly allocated array holding exactly the requested bytes.</returns>
    public byte[] Read(long fileOffset, int length)
    {
        var buffer = new byte[length];
        Read(fileOffset, buffer.AsSpan());
        return buffer;
    }

    /// <summary>Reads into a caller-supplied buffer, filling it completely.</summary>
    /// <param name="fileOffset">Offset within the DLL file.</param>
    /// <param name="destination">Buffer to fill.</param>
    /// <exception cref="EndOfStreamException">The file ended before the buffer was filled.</exception>
    public void Read(long fileOffset, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(fileOffset);

        var total = 0;
        while (total < destination.Length)
        {
            var read = RandomAccess.Read(_handle, destination[total..], fileOffset + total);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"Short read at offset 0x{fileOffset:x}: wanted {destination.Length} bytes, got {total}.");
            }

            total += read;
        }
    }

    /// <summary>Reads the bytes of one cached table.</summary>
    /// <param name="entry">Manifest entry describing the table.</param>
    /// <returns>The table's bytes, exactly <see cref="TableEntry.Size"/> long.</returns>
    public byte[] Read(TableEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Read(entry.FileOffset, entry.Size);
    }

    /// <summary>Reads the COFF <c>TimeDateStamp</c> from the PE header.</summary>
    /// <returns>The timestamp, or <c>0</c> if the file is not a PE image.</returns>
    public uint ReadPeTimestamp()
    {
        Span<byte> four = stackalloc byte[4];
        Read(0x3C, four);
        long peHeader = BinaryPrimitives.ReadUInt32LittleEndian(four);
        if (peHeader <= 0 || peHeader + 8 > Length)
        {
            return 0;
        }

        Span<byte> signature = stackalloc byte[4];
        Read(peHeader, signature);
        if (signature[0] != (byte)'P' || signature[1] != (byte)'E' || signature[2] != 0 || signature[3] != 0)
        {
            return 0;
        }

        Read(peHeader + 8, four);
        return BinaryPrimitives.ReadUInt32LittleEndian(four);
    }

    /// <summary>Computes the SHA-256 of the whole file as lower-case hex.</summary>
    /// <returns>The digest.</returns>
    /// <remarks>
    /// Hashes through <see cref="RandomAccess"/> rather than a <see cref="FileStream"/> wrapper,
    /// because a stream constructed over the handle takes ownership of it and would close the image
    /// on dispose.
    /// </remarks>
    public string ComputeSha256()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        for (long offset = 0; offset < Length;)
        {
            var read = RandomAccess.Read(_handle, buffer, offset);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer.AsSpan(0, read));
            offset += read;
        }

        return Convert.ToHexStringLower(hash.GetCurrentHash());
    }

    private void Verify(RomVerification verification)
    {
        var expected = Manifest.Dll;

        if (Length != expected.Size)
        {
            throw new RomIdentityException(
                $"'{Path}' is {Length:N0} bytes; the pinned {expected.Product} build is " +
                $"{expected.Size:N0} bytes. A different build moves every table offset.");
        }

        var timestamp = ReadPeTimestamp();
        if (timestamp != expected.PeTimestamp)
        {
            throw new RomIdentityException(
                $"'{Path}' has PE timestamp {timestamp} " +
                $"({FormatTimestamp(timestamp)}); the pinned build is {expected.PeTimestamp} " +
                $"({FormatTimestamp(expected.PeTimestamp)}).");
        }

        if (verification != RomVerification.Full)
        {
            return;
        }

        var sha256 = ComputeSha256();
        if (!string.Equals(sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new RomIdentityException(
                $"'{Path}' has SHA-256 {sha256}; the pinned build is {expected.Sha256}. " +
                "Table offsets are only valid for that exact build.");
        }
    }

    private static string FormatTimestamp(uint timestamp) =>
        DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime
            .ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>Closes the underlying file handle.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
    }
}
