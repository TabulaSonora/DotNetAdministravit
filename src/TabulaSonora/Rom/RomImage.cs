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
/// A file image reads through <see cref="RandomAccess"/> rather than a memory map, so no 27 MB copy is
/// ever resident and no unsafe code is required. A host without a filesystem supplies the bytes
/// instead; see <see cref="FromMemory"/>.
/// </para>
/// </remarks>
public sealed class RomImage : IDisposable
{
    private readonly Source _source;
    private bool _disposed;

    private RomImage(string path, Source source, TableManifest manifest)
    {
        Path = path;
        _source = source;
        Length = source.Length;
        Manifest = manifest;
    }

    /// <summary>Path, or descriptive name, the image was opened from.</summary>
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
            return Verified(new RomImage(path, new FileSource(handle), manifest), verification);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Wraps an <c>SCCore.dll</c> already held in memory, and verifies it.</summary>
    /// <param name="image">The whole file's bytes.</param>
    /// <param name="verification">How strictly to check the build identity.</param>
    /// <param name="manifest">Offset map to use; defaults to the embedded one.</param>
    /// <param name="name">Descriptive name reported by <see cref="Path"/> and in error messages.</param>
    /// <returns>An open image.</returns>
    /// <exception cref="RomIdentityException">The bytes are not the pinned build.</exception>
    /// <remarks>
    /// <para>
    /// For a host with no filesystem to open the DLL from. The browser build is the reason this exists:
    /// there the file is picked once, cached in IndexedDB, and handed to the engine as bytes, so there
    /// is never a path to give <see cref="Open"/>.
    /// </para>
    /// <para>
    /// Nothing is copied — the memory is read in place — so the caller must keep it alive and unchanged
    /// for as long as the image is used, exactly as a file image needs its handle to stay open.
    /// </para>
    /// </remarks>
    public static RomImage FromMemory(
        ReadOnlyMemory<byte> image,
        RomVerification verification = RomVerification.Full,
        TableManifest? manifest = null,
        string name = "<memory>")
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        manifest ??= TableManifest.Default;

        return Verified(new RomImage(name, new MemorySource(image), manifest), verification);
    }

    private static RomImage Verified(RomImage image, RomVerification verification)
    {
        if (verification != RomVerification.None)
        {
            image.Verify(verification);
        }

        return image;
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
            var read = _source.Read(destination[total..], fileOffset + total);
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
    /// Hashes through the same block reads the rest of the class uses rather than a
    /// <see cref="FileStream"/> wrapper, because a stream constructed over the handle takes ownership
    /// of it and would close the image on dispose.
    /// </remarks>
    public string ComputeSha256()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        for (long offset = 0; offset < Length;)
        {
            var wanted = (int)Math.Min(buffer.Length, Length - offset);
            var read = _source.Read(buffer.AsSpan(0, wanted), offset);
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
                $"'{Path}' is {Length:N0} bytes; the pinned build — the one that ships with " +
                $"{expected.Product} {expected.Version} — is {expected.Size:N0} bytes. " +
                "A different build moves every table offset.");
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

    /// <summary>Releases the underlying source — a file handle, if there is one.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _source.Dispose();
    }

    /// <summary>Where an image's bytes come from.</summary>
    /// <remarks>
    /// The only thing the rest of the class needs of a source is a length and a positional read, which
    /// is exactly the shape both a file handle and a byte buffer offer.
    /// </remarks>
    private abstract class Source : IDisposable
    {
        /// <summary>Length of the whole image in bytes.</summary>
        public abstract long Length { get; }

        /// <summary>Reads at an absolute offset, returning how much was actually read.</summary>
        /// <param name="destination">Buffer to fill.</param>
        /// <param name="offset">Absolute offset within the image.</param>
        /// <returns>Bytes read; zero at the end of the image.</returns>
        public abstract int Read(Span<byte> destination, long offset);

        /// <summary>Releases anything the source owns.</summary>
        public virtual void Dispose()
        {
        }
    }

    private sealed class FileSource : Source
    {
        private readonly SafeFileHandle _handle;

        public FileSource(SafeFileHandle handle)
        {
            _handle = handle;
            Length = RandomAccess.GetLength(handle);
        }

        public override long Length { get; }

        public override int Read(Span<byte> destination, long offset) =>
            RandomAccess.Read(_handle, destination, offset);

        public override void Dispose() => _handle.Dispose();
    }

    private sealed class MemorySource(ReadOnlyMemory<byte> image) : Source
    {
        public override long Length => image.Length;

        public override int Read(Span<byte> destination, long offset)
        {
            if (offset >= image.Length)
            {
                return 0;
            }

            var available = Math.Min(destination.Length, (int)(image.Length - offset));
            image.Span.Slice((int)offset, available).CopyTo(destination);
            return available;
        }
    }
}
