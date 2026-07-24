using System.Text;
using TabulaSonora.Web.Services;

namespace TabulaSonora.Tests;

/// <summary>
/// The embedded kit names, checked against the file they were transcribed from.
/// </summary>
/// <remarks>
/// Names are the one thing here that cannot be derived from the DLL, so they are carried as data —
/// and data that was copied by hand is data that can drift from its source. Where the plugin is
/// installed, these read its <c>SCVSC.drf</c> and compare every entry; where it is not, they still
/// check the shape the panel depends on.
/// </remarks>
public class DrumKitNamesTests
{
    private static string? DrumFilePath
    {
        get
        {
            var beside = Path.Combine(
                Path.GetDirectoryName(TestData.SccorePath ?? string.Empty) ?? string.Empty,
                "SCVSC.drf");

            return File.Exists(beside) ? beside : null;
        }
    }

    /// <summary>Reads one page of the plugin's tab-delimited kit table.</summary>
    /// <param name="path">The <c>.drf</c> file.</param>
    /// <param name="map">Tone map index, which is the page's leading field.</param>
    /// <returns>Program to kit name, with programs 0-based as everywhere but the file.</returns>
    private static Dictionary<int, string> ReadPage(string path, int map)
    {
        var kits = new Dictionary<int, string>();
        var inPage = false;

        foreach (var raw in Encoding.Latin1.GetString(File.ReadAllBytes(path)).Split('\n'))
        {
            var fields = raw.TrimEnd('\r').Split('\t');

            if (fields[0].StartsWith("PAGE=", StringComparison.Ordinal))
            {
                inPage = fields[0]["PAGE=".Length..].Trim() == map.ToString();
                continue;
            }

            if (inPage && fields.Length >= 2 && int.TryParse(fields[0], out var ordinal) &&
                fields[1].Trim().Length > 0)
            {
                kits[ordinal - 1] = fields[1].Trim();
            }
        }

        return kits;
    }

    [Theory]
    [InlineData(0, 37)]
    [InlineData(1, 25)]
    public void EachRowNamesItsWholeKitSet(int row, int expected)
    {
        Assert.Equal(expected, DrumKitNames.CountFor(row));
    }

    [Fact]
    public void TheRowsAreTheModulesTheirContentsIdentify()
    {
        Assert.Equal(new[] { 4, 3 }, DrumKitNames.RowMaps);
        Assert.Equal("SC-8820", DrumKitNames.RowName(0));
        Assert.Equal("SC-88Pro", DrumKitNames.RowName(1));

        Assert.Equal("STANDARD 1", DrumKitNames.ForRow(row: 0, program: 0));
        Assert.Equal("ROOM", DrumKitNames.ForRow(row: 0, program: 8));

        // The two sets differ, which is the whole reason the row is selectable: program 12 is
        // ROOM L/R on the SC-8820 and is not in the SC-88Pro's list at all.
        Assert.Equal("ROOM L/R", DrumKitNames.ForRow(row: 0, program: 12));
        Assert.Null(DrumKitNames.ForRow(row: 1, program: 12));
    }

    [Fact]
    public void TheCmKitKeepsItsNumber()
    {
        // Program 127 is the CM-64/32L kit. The ROM defines it on both rows and neither module's list
        // names it, so it is left unnamed rather than borrowing the SC-55 page's name for it — the
        // one deliberate gap in this table.
        Assert.Null(DrumKitNames.ForRow(row: 0, program: 127));
        Assert.Null(DrumKitNames.ForRow(row: 1, program: 127));
    }

    [Fact]
    public void NothingIsNamedOutsideTheTwoRows()
    {
        Assert.Null(DrumKitNames.ForRow(row: -1, program: 0));
        Assert.Null(DrumKitNames.ForRow(row: 2, program: 0));
        Assert.Equal(0, DrumKitNames.CountFor(2));
    }

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(1)]
    public void EveryNameMatchesThePluginsOwnFile(int row)
    {
        Skip.If(DrumFilePath is null, "SCVSC.drf not found beside SCCore.dll.");

        var page = ReadPage(DrumFilePath!, DrumKitNames.RowMaps[row]);

        // Complete in both directions: nothing transcribed that the file does not have, and nothing
        // of the file's left out. A missing kit would show as a program number and look like data
        // the module simply lacks, which is exactly the failure this is here to catch.
        Assert.Equal(page.Count, DrumKitNames.CountFor(row));

        foreach (var (program, name) in page)
        {
            Assert.Equal(name, DrumKitNames.ForRow(row, program));
        }
    }
}
