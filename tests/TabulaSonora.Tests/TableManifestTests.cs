using TabulaSonora.Rom;

namespace TabulaSonora.Tests;

/// <summary>
/// The embedded manifest is the only piece of the offset map that ships with this library, so it is
/// worth asserting hard. These tests need no Roland data.
/// </summary>
public class TableManifestTests
{
    private static readonly TableManifest Manifest = TableManifest.Default;

    [Fact]
    public void EmbeddedManifestLoads()
    {
        Assert.NotNull(Manifest);
        Assert.Equal(0x180000000L, Manifest.ImageBase);
    }

    [Fact]
    public void PinsTheExactDllBuild()
    {
        Assert.Equal("SCCore.dll", Manifest.Dll.FileName);
        Assert.Equal(27_347_456L, Manifest.Dll.Size);
        Assert.Equal(
            "117e6aa147a96fbde5e10d2caf16c89965acc1e44235fd245992216cc620bdb1",
            Manifest.Dll.Sha256);
        Assert.Equal(1_572_416_468u, Manifest.Dll.PeTimestamp);
    }

    [Fact]
    public void RecordsWhichSoundCanvasVaReleaseTheBuildComesFrom()
    {
        // Provenance, not evidence: the DLL has no version resource, so nothing reads this out of the
        // file and nothing verifies it. It is here because "the 1.1.6 installer" is how a person
        // finds the right file, while the hash above is how the code recognises it.
        Assert.Equal("1.1.6", Manifest.Dll.Version);
        Assert.Equal("Roland VS SOUND Canvas VA", Manifest.Dll.Product);
    }

    [Fact]
    public void DescribesFortyEightCachedTables()
    {
        Assert.Equal(48, Manifest.CachedTables.Count);
        Assert.All(Manifest.CachedTables, entry =>
        {
            Assert.True(entry.FileOffset > 0, $"{entry.Name} has no file offset.");
            Assert.True(entry.Size > 0, $"{entry.Name} has no size.");
            Assert.False(string.IsNullOrEmpty(entry.Symbol), $"{entry.Name} has no symbol.");
        });
    }

    [Fact]
    public void EveryCachedTableNameIsReferencedByTheLoader()
    {
        // TableSet indexes its dictionary by these names; a manifest entry the loader does not know
        // about would silently never be exposed.
        var known = typeof(TableSet.Names)
            .GetFields()
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        var declared = Manifest.CachedTables.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(declared.OrderBy(n => n, StringComparer.Ordinal),
                     known.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void DescribesTheWaveRomAndDrumRegions()
    {
        Assert.Equal(5, Manifest.LiveRegions.Count);

        var bankA = Manifest.Region("wave_rom_bank_A");
        Assert.Equal(0x92700L, bankA.FileOffset);
        Assert.Equal(12L * 1024 * 1024, bankA.Size);

        var bankB = Manifest.Region("wave_rom_bank_B");
        Assert.Equal(0x1092730L, bankB.FileOffset);
        Assert.Equal(12L * 1024 * 1024, bankB.Size);

        // The drum regions are recorded under a 'va' key, but those values carry no image base --
        // they are already file offsets, and the loader must surface them as such.
        Assert.Equal(0x18AD950L, Manifest.Region("drum_kit_records").FileOffset);
        Assert.Equal(0x19FFBB0L, Manifest.Region("drum_bank_row").FileOffset);
        Assert.Equal(0x19F1EB0L, Manifest.Region("drum_prog_map").FileOffset);
    }

    [Fact]
    public void OnlyTheOverReadTableIsAPrefixMatch()
    {
        var partial = Manifest.CachedTables.Where(t => t.Match != "full").ToList();
        var entry = Assert.Single(partial);
        Assert.Equal(TableSet.Names.KfTvfEnv, entry.Name);
    }

    [Fact]
    public void UnknownNamesThrowRatherThanReturnNull()
    {
        Assert.Throws<KeyNotFoundException>(() => Manifest.Table("no_such_table.bin"));
        Assert.Throws<KeyNotFoundException>(() => Manifest.Region("no_such_region"));
    }
}
