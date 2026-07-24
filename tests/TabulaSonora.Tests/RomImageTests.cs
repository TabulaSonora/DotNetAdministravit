using TabulaSonora.Rom;

namespace TabulaSonora.Tests;

/// <summary>
/// The identity gate. Every table offset in this library is valid for exactly one DLL build, so
/// opening the wrong file must fail loudly rather than produce plausible nonsense.
/// </summary>
public class RomImageTests
{
    [SkippableFact]
    public void OpensThePinnedBuild()
    {
        using var rom = RomImage.Open(TestData.RequireSccore());

        Assert.Equal(27_347_456L, rom.Length);
        Assert.Equal(1_572_416_468u, rom.ReadPeTimestamp());
        Assert.Equal(
            "117e6aa147a96fbde5e10d2caf16c89965acc1e44235fd245992216cc620bdb1",
            rom.ComputeSha256());
    }

    [SkippableFact]
    public void RejectsAFileOfTheWrongSize()
    {
        // Guard the guard: truncate a copy and confirm the size check fires before anything is read.
        var source = TestData.RequireSccore();
        var temp = Path.Combine(Path.GetTempPath(), $"sccore-truncated-{Guid.NewGuid():N}.dll");
        try
        {
            using (var input = File.OpenRead(source))
            using (var output = File.Create(temp))
            {
                var buffer = new byte[64 * 1024];
                var read = input.Read(buffer);
                output.Write(buffer.AsSpan(0, read));
            }

            var ex = Assert.Throws<RomIdentityException>(() => RomImage.Open(temp));
            Assert.Contains("27,347,456", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [SkippableFact]
    public void RejectsAFileWithTheRightSizeButWrongContent()
    {
        // Same length, one byte flipped well away from the PE header, so only the hash can catch it.
        var source = TestData.RequireSccore();
        var temp = Path.Combine(Path.GetTempPath(), $"sccore-tampered-{Guid.NewGuid():N}.dll");
        try
        {
            File.Copy(source, temp, overwrite: true);
            using (var handle = File.OpenHandle(temp, FileMode.Open, FileAccess.ReadWrite))
            {
                var one = new byte[1];
                RandomAccess.Read(handle, one, 0x200000);
                one[0] ^= 0xFF;
                RandomAccess.Write(handle, one, 0x200000);
            }

            var ex = Assert.Throws<RomIdentityException>(() => RomImage.Open(temp));
            Assert.Contains("SHA-256", ex.Message, StringComparison.Ordinal);

            // Quick verification only checks size and timestamp, so it must NOT catch this.
            using var quick = RomImage.Open(temp, RomVerification.Quick);
            Assert.Equal(27_347_456L, quick.Length);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [SkippableFact]
    public void ReadsTheWaveRomBankBases()
    {
        using var rom = RomImage.Open(TestData.RequireSccore(), RomVerification.Quick);
        var waveRom = new WaveRom(rom);

        Assert.Equal(0x92700L, waveRom.BankBase(0));
        Assert.Equal(0x1092730L, waveRom.BankBase(1));

        // Region bytes above 0x0f select bank B, and the region index is taken relative to it.
        Assert.Equal((0, 6), WaveRom.SplitRegion(6));
        Assert.Equal((1, 4), WaveRom.SplitRegion(20));
        Assert.Equal(0x92700L + (6L * WaveRom.RegionSize), waveRom.RegionBase(6));
    }

    [SkippableTheory]
    // Bank A: the 1994 SC-88 image, then the 1996 SC-88Pro image from region 8 on.
    [InlineData(0, 0, "ver200", "1994-12-08")]
    [InlineData(0, 7, "ver200", "1994-12-08")]
    [InlineData(0, 8, "rom_make", "1996-06-16")]
    [InlineData(0, 11, "rom_make", "1996-06-16")]
    // Bank B: the tail of the SC-88Pro image, then the 1999 SC-8820 image from region 4 on.
    [InlineData(1, 0, "rom_make", "1996-06-16")]
    [InlineData(1, 3, "rom_make", "1996-06-16")]
    [InlineData(1, 4, "8820_wv0", "1999-08-17")]
    [InlineData(1, 7, "8820_wv0", "1999-08-17")]
    public void EachRomBlockIdentifiesItsHardwareGeneration(int bank, int region, string label, string date)
    {
        // Every 1 MB block opens with a 0x50-byte header: magic, then an ASCII generation label at
        // +0x20 and a build date at +0x30. This is the evidence that the embedded ROM is the literal
        // hardware mask ROM, and asserting the labels pins the bank bases far harder than a magic
        // check alone would.
        using var rom = RomImage.Open(TestData.RequireSccore(), RomVerification.Quick);
        var waveRom = new WaveRom(rom);

        var header = rom.Read(waveRom.BankBase(bank) + ((long)region * WaveRom.RegionSize), 0x50);

        Assert.Equal<byte[]>([0xA4, 0xEB, 0xA5, 0x2B, 0xE9, 0x29], header[..6]);
        Assert.Equal(label, ReadAscii(header, 0x20));
        Assert.Equal(date, ReadAscii(header, 0x30));
    }

    [SkippableFact]
    public void BankBHoldsEightRegionsOfRomNotTheTwelveTheManifestDeclares()
    {
        // The manifest records both banks as 12 MB, but bank B's data stops at 0x1892730 -- and the
        // declared span runs past the end of the file entirely. Anything using the nominal size as a
        // bound would read adjacent DLL data as though it were samples.
        using var rom = RomImage.Open(TestData.RequireSccore(), RomVerification.Quick);
        var waveRom = new WaveRom(rom);

        Assert.Equal(12, WaveRom.RegionCount(0));
        Assert.Equal(8, WaveRom.RegionCount(1));

        var pastRom = rom.Read(waveRom.BankBase(1) + (8L * WaveRom.RegionSize), 6);
        Assert.NotEqual<byte[]>([0xA4, 0xEB, 0xA5, 0x2B, 0xE9, 0x29], pastRom);

        var declared = rom.Manifest.Region("wave_rom_bank_B");
        Assert.True(declared.FileOffset + declared.Size > rom.Length,
            "Expected the manifest's nominal bank B span to overrun the file.");
    }

    [SkippableFact]
    public void AMemoryImageReadsIdenticallyToAFileImage()
    {
        // The browser has no filesystem and hands the DLL over as bytes. Nothing downstream knows
        // which kind of image it holds, so the two must be indistinguishable through every read the
        // engine actually performs -- the tables it slices and the wave ROM it samples.
        var path = TestData.RequireSccore();

        using var file = RomImage.Open(path, RomVerification.Quick);
        using var memory = RomImage.FromMemory(File.ReadAllBytes(path), RomVerification.Quick);

        Assert.Equal(file.Length, memory.Length);
        Assert.Equal(file.ReadPeTimestamp(), memory.ReadPeTimestamp());
        Assert.Equal(file.ComputeSha256(), memory.ComputeSha256());

        var compared = 0;
        foreach (var entry in file.Manifest.CachedTables)
        {
            Assert.Equal<byte[]>(file.Read(entry), memory.Read(entry));
            compared++;
        }

        Assert.Equal(file.Manifest.CachedTables.Count, compared);

        // A slice from each bank, taken through the wave-ROM view rather than at a bare offset, so a
        // regression in positional reads past the 2 GB-safe range would show up here too.
        var waves = new WaveRom(file);
        foreach (var bank in (int[])[0, 1])
        {
            var offset = waves.BankBase(bank) + (3L * WaveRom.RegionSize);
            Assert.Equal<byte[]>(file.Read(offset, 4096), memory.Read(offset, 4096));
        }
    }

    [SkippableFact]
    public void AMemoryImageIsHeldToTheSameIdentityAsAFile()
    {
        var bytes = File.ReadAllBytes(TestData.RequireSccore());

        // Truncated: the size check fires first, before anything is read.
        var truncated = Assert.Throws<RomIdentityException>(
            () => RomImage.FromMemory(bytes.AsMemory(0, 64 * 1024)));
        Assert.Contains("27,347,456", truncated.Message, StringComparison.Ordinal);

        // Right length, one byte flipped away from the PE header: only the hash can catch it.
        var tampered = bytes.ToArray();
        tampered[0x200000] ^= 0xFF;

        var ex = Assert.Throws<RomIdentityException>(() => RomImage.FromMemory(tampered));
        Assert.Contains("SHA-256", ex.Message, StringComparison.Ordinal);

        using var quick = RomImage.FromMemory(tampered, RomVerification.Quick);
        Assert.Equal(27_347_456L, quick.Length);
    }

    [SkippableFact]
    public void AMemoryImageNamesItselfInItsErrors()
    {
        // There is no path to quote, so the caller's name has to carry the diagnostic instead --
        // "sccore.dll (from IndexedDB)" is a far more useful thing to see than "<memory>".
        var bytes = File.ReadAllBytes(TestData.RequireSccore());

        using var named = RomImage.FromMemory(bytes, RomVerification.Quick, name: "cached copy");
        Assert.Equal("cached copy", named.Path);

        var ex = Assert.Throws<RomIdentityException>(
            () => RomImage.FromMemory(bytes.AsMemory(0, 1024), name: "cached copy"));
        Assert.Contains("cached copy", ex.Message, StringComparison.Ordinal);
    }

    private static string ReadAscii(byte[] header, int offset)
    {
        var span = header.AsSpan(offset, 16);
        var end = span.IndexOf((byte)0);
        return System.Text.Encoding.ASCII.GetString(end < 0 ? span : span[..end]).Trim();
    }
}
