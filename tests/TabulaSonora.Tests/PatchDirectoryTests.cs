using TabulaSonora.Patches;
using TabulaSonora.Rom;

namespace TabulaSonora.Tests;

/// <summary>
/// Directory resolution, checked against the two enumerations the harness swept out of the real
/// engine: <c>wave_directory_full.csv</c> (every wave it ever selected) and <c>zones_all.csv</c>
/// (every key and velocity zone boundary it observed).
/// </summary>
public class PatchDirectoryTests
{
    private static PatchDirectory Load() =>
        new(TableSet.FromCacheDirectory(TestData.RequireTables()));

    [SkippableFact]
    public void TableCountsMatchTheRecordStrides()
    {
        var directory = Load();

        Assert.Equal(2048, directory.ToneCount);
        Assert.Equal(2048, directory.MultisampleCount);
        Assert.Equal(4096, directory.WaveCount);
        Assert.Equal(50, directory.AlternateCount);
    }

    [SkippableFact]
    public void ReadsTheDocumentedToneNamesAndWaveCoordinates()
    {
        var directory = Load();

        Assert.Equal("Piano 1", directory.GetTone(0)!.Name);
        Assert.Equal("Harpsichord", directory.GetTone(39)!.Name);
        Assert.Equal("Marimba", directory.GetTone(71)!.Name);

        var wave = directory.GetWave(806)!.Value;
        Assert.Equal(6, wave.Region);
        Assert.Equal(800_928, wave.Loop);
        Assert.Equal(803_508, wave.End);
        Assert.Equal(807_803, wave.Start);
        Assert.Equal(75, wave.RootKey);
        Assert.Equal(0, wave.Bank);
    }

    [SkippableTheory]
    // The same program pulls era-correct samples from a different part of the ROM per tone map.
    [InlineData(ToneMap.Sc55, 1291, 4)]
    [InlineData(ToneMap.Sc88, 1455, 5)]
    [InlineData(ToneMap.Sc88Pro, 0, 8)]
    [InlineData(ToneMap.Sc8820, 0, 8)]
    public void ToneMapSelectsTheEraCorrectSample(ToneMap map, int expectedTone, int expectedRegion)
    {
        var directory = Load();

        var tone = directory.ProgramToTone(program: 0, map, bank: 0);
        Assert.Equal(expectedTone, tone);

        var resolved = directory.Resolve(tone, note: 60, velocity: 100);
        Assert.Equal("Piano 1", resolved.Name);
        Assert.All(resolved.Partials, p => Assert.Equal(expectedRegion, p.Descriptor.Region));
    }

    [SkippableFact]
    public void ProgramsInTheAlternateArticulationSpaceResolveThroughTheirPrimaryReference()
    {
        var directory = Load();

        // Program 48 does not name a tone directly: the lookup yields 0x600e, an index into the
        // alternate-articulation table. Reading that word as signed and bailing on the sign bit is
        // what once made the string patches look unassigned.
        var raw = directory.Lut3Raw(program: 48, ToneMap.Sc8820, bank: 0);
        Assert.Equal(0x600E, raw);

        var entry = directory.Alternate(0x600E - PatchDirectory.AlternateSpaceStart)!.Value;
        Assert.Equal("Strings", entry.Name);
        Assert.Equal(3, entry.Threshold);

        // The two references are not layers played together: the second is a conditional
        // articulation only the mono/solo path reaches.
        Assert.Equal([390], directory.ProgramTones(48, ToneMap.Sc8820, bank: 0));
        Assert.Equal(391, directory.AlternateTone(48, ToneMap.Sc8820, bank: 0));
    }

    [SkippableFact]
    public void AnEmptyBottomZoneSilencesThePartialInsteadOfFallingThrough()
    {
        var directory = Load();

        // Slap Bass 2's multisample 196 has no wave in zone 0, so notes below 40 must produce
        // nothing at all -- falling through to the neighbouring wave would give the low notes a
        // sound the engine does not make.
        Assert.Null(directory.MultisampleWave(196, 30));
        Assert.Null(directory.MultisampleWave(196, 39));
        Assert.Equal(1099, directory.MultisampleWave(196, 40));
        Assert.Equal(1099, directory.MultisampleWave(196, 41));
        Assert.Equal(1102, directory.MultisampleWave(196, 60));
    }

    [SkippableFact]
    public void EveryForwardWaveTheEngineSelectedResolvesToADescriptor()
    {
        var directory = Load();
        var rows = ReadCsv(TestData.RequireTrace("wave_directory_full.csv"));

        var byCoordinates = new HashSet<(int Region, int Loop, int End, int Start)>();
        for (var n = 0; n < directory.WaveCount; n++)
        {
            var wave = directory.GetWave(n)!.Value;
            byCoordinates.Add((wave.Region, wave.Loop, wave.End, wave.Start));
        }

        var forward = 0;
        var matched = 0;
        var unmatchedWithLoop = new List<string>();

        foreach (var row in rows)
        {
            if (row["reverse"] != "0")
            {
                continue;
            }

            forward++;
            var key = (Int(row, "region"), Int(row, "loop_start"), Int(row, "end"), Int(row, "start"));
            if (byCoordinates.Contains(key))
            {
                matched++;
            }
            else if (key.Item3 != key.Item4)
            {
                // An unmatched row with a real loop region would mean the descriptor decode is wrong.
                unmatchedWithLoop.Add($"r{key.Item1} loop{key.Item2} end{key.Item3} start{key.Item4}");
            }
        }

        Assert.Equal(2022, forward);
        Assert.Equal(2014, matched);

        // The only forward waves without a descriptor are empty-loop one-shots (end == start),
        // plausibly reached through the drum tone table, which is not reversed.
        Assert.Empty(unmatchedWithLoop);
    }

    [SkippableFact]
    public void ReverseWavesUseDifferentRuntimeRegistersThanTheStaticDescriptor()
    {
        var rows = ReadCsv(TestData.RequireTrace("wave_directory_full.csv"));
        var reverse = rows.Where(r => r["reverse"] == "1").ToList();

        Assert.Equal(67, reverse.Count);

        // For a reverse wave the engine reconfigures the registers: playback runs downward from
        // loop_start, and end/start collapse together. Comparing them against the static descriptor
        // fields directly is therefore meaningless.
        Assert.All(reverse, r =>
        {
            Assert.True(Int(r, "loop_start") > Int(r, "end"));
            Assert.Equal(Int(r, "start"), Int(r, "end"));
        });
    }

    [SkippableFact]
    public void ZoneSegmentationReproducesTheEngineSweep()
    {
        var directory = Load();
        var rows = ReadCsv(TestData.RequireTrace("zones_all.csv"));

        var expected = new Dictionary<(int Page, int Program), HashSet<(int, int, int, int)>>();
        foreach (var row in rows)
        {
            if (row["module"] != "SC")
            {
                continue;
            }

            var page = Int(row, "page");
            if (page is < 1 or > 4)
            {
                continue;
            }

            var key = (page, Int(row, "prog"));
            if (!expected.TryGetValue(key, out var set))
            {
                expected[key] = set = [];
            }

            // The sweep starts at velocity 1, since 0 is a note-off.
            set.Add((Math.Max(1, Int(row, "velLo")), Int(row, "velHi"), Int(row, "keyLo"), Int(row, "keyHi")));
        }

        var compared = 0;
        var matched = 0;
        foreach (var ((page, program), csvZones) in expected.OrderBy(e => e.Key))
        {
            var tone = directory.ProgramToTone(program, (ToneMap)page, bank: 0);
            if (tone < 0)
            {
                continue;
            }

            compared++;
            if (Segment(directory, tone).SetEquals(csvZones))
            {
                matched++;
            }
        }

        Assert.Equal(512, compared);

        // The residual divergence is at the very top of the key range, where the engine holds the
        // last zone but the static walk falls through to the fallback wave and splits it. It is a
        // known limit of the reference resolver, not of this port -- the reference scores the same
        // 470, and the differential fixtures pin this port to the reference exactly.
        Assert.Equal(470, matched);
    }

    /// <summary>
    /// Reproduces the sweep the <c>mapall</c> harness performed: find a reference note, split the
    /// velocity axis where the sounding voice set changes, then split the key axis inside each band.
    /// </summary>
    private static HashSet<(int, int, int, int)> Segment(PatchDirectory directory, int tone)
    {
        var result = new HashSet<(int, int, int, int)>();

        var reference = -1;
        foreach (var candidate in (int[])[60, 55, 67, 48, 72, 43, 79, 36, 84])
        {
            if (VoiceSet(directory, tone, candidate, 110).Count > 0)
            {
                reference = candidate;
                break;
            }
        }

        if (reference < 0)
        {
            return result;
        }

        var bands = new List<(int Low, int High)>();
        HashSet<(int, int, int)>? previous = null;
        var bandStart = 1;
        for (var velocity = 1; velocity <= 127; velocity++)
        {
            var set = VoiceSet(directory, tone, reference, velocity);
            if (previous is null || !set.SetEquals(previous))
            {
                if (previous is not null)
                {
                    bands.Add((bandStart, velocity - 1));
                }

                previous = set;
                bandStart = velocity;
            }
        }

        bands.Add((bandStart, 127));

        foreach (var (low, high) in bands)
        {
            var velocity = (low + high) / 2;
            HashSet<(int, int, int)>? current = null;
            var keyStart = 0;

            for (var note = 0; note < 128; note++)
            {
                var set = VoiceSet(directory, tone, note, velocity);
                if (current is null || !set.SetEquals(current))
                {
                    if (current is { Count: > 0 })
                    {
                        result.Add((low, high, keyStart, note - 1));
                    }

                    current = set;
                    keyStart = note;
                }
            }

            if (current is { Count: > 0 })
            {
                result.Add((low, high, keyStart, 127));
            }
        }

        return result;
    }

    private static HashSet<(int, int, int)> VoiceSet(PatchDirectory directory, int tone, int note, int velocity)
    {
        var resolved = directory.Resolve(tone, note, velocity);
        var set = new HashSet<(int, int, int)>(resolved.Partials.Count);
        foreach (var partial in resolved.Partials)
        {
            set.Add((partial.Wave, partial.Descriptor.Region, partial.Descriptor.Loop));
        }

        return set;
    }

    private static int Int(Dictionary<string, string> row, string column) =>
        int.Parse(row[column], System.Globalization.CultureInfo.InvariantCulture);

    private static List<Dictionary<string, string>> ReadCsv(string path)
    {
        using var reader = new StreamReader(path);
        var header = reader.ReadLine()!.Split(',');
        var rows = new List<Dictionary<string, string>>();

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var fields = line.Split(',');
            var row = new Dictionary<string, string>(header.Length, StringComparer.Ordinal);
            for (var i = 0; i < header.Length && i < fields.Length; i++)
            {
                row[header[i]] = fields[i];
            }

            rows.Add(row);
        }

        return rows;
    }
}
