using System.Text;
using TabulaSonora.Web.Services;

namespace TabulaSonora.Tests;

/// <summary>
/// The parser for the plugin's <c>.drf</c> kit-name file.
/// </summary>
/// <remarks>
/// The file itself is Roland's and is not in this repository, so the tests that read a real one skip
/// without it, the same way the ROM-backed tests do. The shape tests do not need it: the format is a
/// tab-delimited table and a hand-written sample exercises every branch.
/// </remarks>
public class DrumKitNamesTests
{
    // Tabs are what the format separates on, and the real file pads every row out to 128 columns.
    private const string Sample =
        "SSW DRUMFILE Ver 2.0\r\n" +
        "MODULENAME=Sc8820\t\t\t\r\n" +
        "PAGECOUNT=2\r\n" +
        ";\r\n" +
        "PAGE=4\t8820Drum\t\t\r\n" +
        "TONECOUNT=128\r\n" +
        "\t0\t1\t2\r\n" +
        "1\tSTANDARD 1\t\t\r\n" +
        "9\tROOM  \t\t\r\n" +
        "10\t\t\t\r\n" +
        "128\tCM-64/32L\t\t\r\n" +
        "PAGE=1\t55Drum\t\t\r\n" +
        "TONECOUNT=128\r\n" +
        "\t0\t1\t2\r\n" +
        "1\tSTANDARD\t\t\r\n";

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

    [Fact]
    public void ReadsKitsByPageAndProgram()
    {
        var names = DrumKitNames.Parse(Encoding.Latin1.GetBytes(Sample));

        Assert.Equal("Sc8820", names.Module);
        Assert.False(names.IsEmpty);

        // Rows are 1-based in the file and 0-based everywhere else, which is the one conversion the
        // parser owes the caller.
        Assert.Equal("STANDARD 1", names.Lookup(map: 4, program: 0));
        Assert.Equal("ROOM", names.Lookup(map: 4, program: 8));
        Assert.Equal("CM-64/32L", names.Lookup(map: 4, program: 127));

        // A different page is a different map, and the same program can be named differently in each.
        Assert.Equal("STANDARD", names.Lookup(map: 1, program: 0));

        // Blank rows are gaps, not empty names; unknown programs and maps are simply absent.
        Assert.Null(names.Lookup(map: 4, program: 9));
        Assert.Null(names.Lookup(map: 4, program: 42));
        Assert.Null(names.Lookup(map: 3, program: 0));

        Assert.Equal(4, names.Count);
    }

    [Fact]
    public void RefusesAFileThatIsNotOne()
    {
        var wrong = Encoding.Latin1.GetBytes("SSW TONEFILE Ver 2.0\r\nMODULENAME=Sc8820\r\n");

        var thrown = Assert.Throws<InvalidDataException>(() => DrumKitNames.Parse(wrong));
        Assert.Contains("SCVSC.drf", thrown.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void ReadsThePluginsOwnFile()
    {
        Skip.If(DrumFilePath is null, "SCVSC.drf not found beside SCCore.dll.");

        var names = DrumKitNames.Parse(File.ReadAllBytes(DrumFilePath!));

        Assert.Equal("Sc8820", names.Module);

        // The two kit sets the drum program map actually holds. Row 0's programs match this file's
        // SC-8820 page and row 1's its SC-88Pro page, which is how the rows were identified at all.
        Assert.Equal("STANDARD 1", names.Lookup(map: 4, program: 0));
        Assert.Equal("ROOM", names.Lookup(map: 4, program: 8));
        Assert.Equal("STANDARD 1", names.Lookup(map: 3, program: 0));

        // The SC-55 page is the GS kit list exactly, including the CM-64 kit at program 128 that the
        // later pages leave out.
        Assert.Equal("STANDARD", names.Lookup(map: 1, program: 0));
        Assert.Equal("CM-64/32L", names.Lookup(map: 1, program: 127));
    }
}
