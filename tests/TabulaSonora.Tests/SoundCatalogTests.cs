using TabulaSonora;
using TabulaSonora.Midi;
using TabulaSonora.Patches;
using TabulaSonora.Realtime;
using TabulaSonora.Rom;

namespace TabulaSonora.Tests;

/// <summary>
/// The enumeration of the whole sound set.
/// </summary>
/// <remarks>
/// One property matters more than any count here: what the catalog calls playable must be exactly
/// what the engine will sound. Everything else it reports — how many banks a vintage has, how many
/// tones they reach — is a number nobody can check by ear, so the tests that pin it are the tests
/// that keep it honest.
/// </remarks>
public class SoundCatalogTests
{
    private static PatchDirectory Load() =>
        new(TableSet.FromCacheDirectory(TestData.RequireTables()));

    [SkippableTheory]
    [InlineData(ToneMap.Sc55)]
    [InlineData(ToneMap.Sc88)]
    [InlineData(ToneMap.Sc88Pro)]
    [InlineData(ToneMap.Sc8820)]
    public void EveryVintageDefinesTheWholeCapitalBank(ToneMap map)
    {
        var catalogue = SoundCatalog.Build(Load(), map);

        var capital = catalogue.Banks[0];
        Assert.Equal(0, capital.Bank);
        Assert.Equal(128, capital.Programs.Count);

        // GM's own guarantee, and the one thing every vintage has in common: bank 0 has no holes.
        Assert.Equal(128, capital.NativeCount);
        Assert.All(capital.Programs, entry => Assert.Equal(CatalogEntryKind.Native, entry.Kind));
    }

    [SkippableTheory]
    [InlineData(ToneMap.Sc55)]
    [InlineData(ToneMap.Sc88)]
    [InlineData(ToneMap.Sc88Pro)]
    [InlineData(ToneMap.Sc8820)]
    public void EveryPlayableSlotIsOneTheEngineWouldSound(ToneMap map)
    {
        var directory = Load();
        var catalogue = SoundCatalog.Build(directory, map);

        foreach (var bank in catalogue.Banks)
        {
            foreach (var entry in bank.Programs)
            {
                // ProgramTones is the engine's own path on a note-on, and it applies the capital
                // fallback -- so both playable kinds have to come back with a defined tone through
                // it, and neither unplayable kind may.
                var tones = directory.ProgramTones(entry.Program, map, bank.Bank);
                var sounds = tones.Count > 0 && directory.GetTone(tones[0]) is { IsDefined: true };

                Assert.Equal(entry.Playable, sounds);
            }
        }
    }

    [SkippableTheory]
    [InlineData(ToneMap.Sc55)]
    [InlineData(ToneMap.Sc88)]
    [InlineData(ToneMap.Sc88Pro)]
    [InlineData(ToneMap.Sc8820)]
    public void ANativeSlotNamesTheToneItResolvesTo(ToneMap map)
    {
        var directory = Load();
        var catalogue = SoundCatalog.Build(directory, map);

        foreach (var bank in catalogue.Banks)
        {
            foreach (var entry in bank.Programs)
            {
                if (entry.Kind != CatalogEntryKind.Native)
                {
                    continue;
                }

                Assert.Equal(directory.ProgramToTone(entry.Program, map, bank.Bank), entry.Tone);
                Assert.Equal(directory.GetTone(entry.Tone)!.Name.Trim(), entry.Name);
            }
        }
    }

    [SkippableFact]
    public void ABankIsListedOnlyWhenItDefinesSomethingItself()
    {
        var directory = Load();
        var catalogue = SoundCatalog.Build(directory, ToneMap.Sc8820);

        Assert.All(catalogue.Banks, bank => Assert.True(bank.NativeCount > 0));

        // The converse: every bank left out really is empty. Without this the list could quietly drop
        // a whole variation and every other assertion here would still pass.
        var listed = catalogue.Banks.Select(b => b.Bank).ToHashSet();
        for (var bank = 0; bank < 128; bank++)
        {
            if (listed.Contains(bank))
            {
                continue;
            }

            for (var program = 0; program < 128; program++)
            {
                var raw = directory.Lut3Raw(program, ToneMap.Sc8820, bank);
                Assert.True(raw is null || raw == PatchDirectory.Unassigned);
            }
        }
    }

    [SkippableFact]
    public void LaterVintagesCarryEverythingTheEarlierOnesReached()
    {
        var directory = Load();

        var counts = new[] { ToneMap.Sc55, ToneMap.Sc88, ToneMap.Sc88Pro, ToneMap.Sc8820 }
            .Select(map => SoundCatalog.Build(directory, map))
            .ToArray();

        // Each map is the previous module's sound set plus what its own generation added, which is
        // the whole point of a vintage selector: an SC-55 file plays on an SC-8820.
        for (var i = 1; i < counts.Length; i++)
        {
            Assert.True(
                counts[i].Banks.Count >= counts[i - 1].Banks.Count,
                $"{counts[i].Map} lists {counts[i].Banks.Count} banks, fewer than {counts[i - 1].Map}.");
        }

        Assert.True(counts[0].Banks.Count < counts[^1].Banks.Count);
    }

    [SkippableFact]
    public void TheIndirectOnlyMarkerIsOffEveryDirectlySelectablePath()
    {
        var tables = TableSet.FromCacheDirectory(TestData.RequireTables());
        var directory = new PatchDirectory(tables);

        // The table really does carry flagged words -- 1 164 of them -- so the classification is not
        // dead code by oversight.
        Assert.Contains(tables.DirLut3, word => word >= PatchDirectory.IndirectOnlyFlag &&
            word != PatchDirectory.Unassigned);

        // But not one of them is reachable by a bank select and a program change on any of the four
        // vintages. They sit in LUT3 rows that only PatchDirectory.Dereference visits, through an
        // alternate-articulation entry naming a map outside this enum. That is exactly why reading
        // the word signed was a bug in the dereference and invisible in a browse: the browse never
        // sees one.
        foreach (var map in new[] { ToneMap.Sc55, ToneMap.Sc88, ToneMap.Sc88Pro, ToneMap.Sc8820 })
        {
            var catalogue = SoundCatalog.Build(directory, map);
            Assert.All(
                catalogue.Banks.SelectMany(bank => bank.Programs),
                entry => Assert.NotEqual(CatalogEntryKind.IndirectOnly, entry.Kind));
        }
    }

    [SkippableTheory]
    // Each generation adds banks to the one before it. The counts are the measurement, and pinning
    // them is what would catch a lookup silently resolving against the wrong LUT1 row.
    [InlineData(ToneMap.Sc55, 15)]
    [InlineData(ToneMap.Sc88, 24)]
    [InlineData(ToneMap.Sc88Pro, 45)]
    [InlineData(ToneMap.Sc8820, 51)]
    public void EachVintageDefinesTheBanksItsModuleHad(ToneMap map, int expected)
    {
        Assert.Equal(expected, SoundCatalog.Build(Load(), map).Banks.Count);
    }

    [SkippableFact]
    public void AFallbackSlotCarriesTheCapitalToneItSounds()
    {
        var directory = Load();
        var catalogue = SoundCatalog.Build(directory, ToneMap.Sc8820);
        var capital = catalogue.Banks[0];

        var fallbacks = catalogue.Banks
            .SelectMany(bank => bank.Programs)
            .Where(entry => entry.Kind == CatalogEntryKind.CapitalFallback)
            .ToList();

        Assert.NotEmpty(fallbacks);
        Assert.All(fallbacks, entry =>
        {
            Assert.Equal(capital.Programs[entry.Program].Tone, entry.Tone);
            Assert.Equal(capital.Programs[entry.Program].Name, entry.Name);
        });
    }

    [SkippableFact]
    public void TheGmKitNamesItsKeysFromTheMelodicToneTable()
    {
        using var rom = RomImage.Open(TestData.RequireSccore());
        var renderer = new NoteRenderer(rom);

        var kits = SoundCatalog.BuildDrums(renderer.Directory, renderer.Drums, row: 0);

        var standard = kits.Kits[0];
        Assert.Contains(0, standard.Programs);

        // The kit dumped by name in the reverse-engineering notes: keys 36 and 38 are the kick and
        // the snare, and they are ordinary melodic tones rather than entries of the drum tone table.
        var kick = standard.Keys.Single(k => k.Note == 36);
        var snare = standard.Keys.Single(k => k.Note == 38);

        Assert.True(kick.Tone < PatchDirectory.MelodicSpaceEnd);
        Assert.Contains("Drum", kick.Name, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Snare", snare.Name, StringComparison.OrdinalIgnoreCase);

        // Every key is named, because an unnamed one is dropped rather than listed blank.
        Assert.All(standard.Keys, key => Assert.NotEqual(string.Empty, key.Name));
    }

    [SkippableFact]
    public void TheSecondDrumMapRowReachesKitsTheFirstDoesNot()
    {
        using var rom = RomImage.Open(TestData.RequireSccore());
        var renderer = new NoteRenderer(rom);

        var a = SoundCatalog.BuildDrums(renderer.Directory, renderer.Drums, row: 0);
        var b = SoundCatalog.BuildDrums(renderer.Directory, renderer.Drums, row: 1);

        Assert.NotEmpty(b.Kits);
        Assert.NotEqual(
            a.Kits.Select(k => k.Kit).ToArray(),
            b.Kits.Select(k => k.Kit).ToArray());
    }

    [SkippableFact]
    public void TheDrumMapRowDecidesWhichKitAProgramChangeSelects()
    {
        using var rom = RomImage.Open(TestData.RequireSccore());
        var renderer = new NoteRenderer(rom);

        // A program the two rows disagree about; there is no point asserting on one they share.
        var program = Enumerable.Range(0, 128).First(p =>
            renderer.Drums.KitForProgram(p, row: 0) != renderer.Drums.KitForProgram(p, row: 1) &&
            renderer.Drums.KitForProgram(p, row: 1) is not null);

        var engine = new ToneGenerator(renderer);
        engine.SendChannel(0xC9, program, 0);
        var onA = engine.DrumKit;

        engine.DrumMapRow = 1;
        engine.SendChannel(0xC9, program, 0);

        Assert.NotEqual(onA, engine.DrumKit);
        Assert.Equal(renderer.Drums.KitForProgram(program, row: 1), engine.DrumKit);
    }

    [SkippableFact]
    public void TheOfflineRendererTakesTheMapRowToo()
    {
        using var rom = RomImage.Open(TestData.RequireSccore());
        var renderer = new NoteRenderer(rom);

        // A program and a key where the two rows really do land on different tones. Picked rather
        // than assumed: two kits can share a snare, and a test that happened to strike one would pass
        // whatever the row did.
        var (program, note) = FindDisagreement(renderer);

        var events = new List<MidiEvent>
        {
            new(0, MidiEventKind.Channel, 0xC9, program, 0, null),
            new(0, MidiEventKind.Channel, 0x99, note, 100, null),
            new(NoteRenderer.SampleRate / 4, MidiEventKind.Channel, 0x89, note, 0, null),
        };

        var sequence = SequenceBuilder.Build(events);
        var offline = new SequenceRenderer(renderer);

        var a = offline.Render(sequence, new RenderOptions { DrumMapRow = 0 });
        var b = offline.Render(sequence, new RenderOptions { DrumMapRow = 1 });

        // The row has to reach the note-by-note path as well as the block loop, or the same file
        // would render as two different arrangements depending on which renderer was asked.
        Assert.NotEqual(Rms(a.Left), Rms(b.Left), precision: 4);
    }

    private static (int Program, int Note) FindDisagreement(NoteRenderer renderer)
    {
        for (var program = 0; program < 128; program++)
        {
            if (renderer.Drums.KitForProgram(program, 0) is not { } a ||
                renderer.Drums.KitForProgram(program, 1) is not { } b ||
                a == b)
            {
                continue;
            }

            for (var note = 0; note < DrumKitTable.KeyCount; note++)
            {
                if (renderer.Drums.Key(note, a).Tone != renderer.Drums.Key(note, b).Tone &&
                    renderer.Directory.GetTone(renderer.Drums.Key(note, a).Tone) is { IsDefined: true } &&
                    renderer.Directory.GetTone(renderer.Drums.Key(note, b).Tone) is { IsDefined: true })
                {
                    return (program, note);
                }
            }
        }

        Skip.If(true, "No drum program resolves to different tones on the two map rows.");
        return default;
    }

    private static double Rms(float[] samples)
    {
        var sum = 0.0;
        foreach (var sample in samples)
        {
            sum += sample * (double)sample;
        }

        return Math.Sqrt(sum / samples.Length);
    }

    [SkippableFact]
    public void ResetLeavesTheMapRowAloneAndTheKitBehind()
    {
        using var rom = RomImage.Open(TestData.RequireSccore());
        var renderer = new NoteRenderer(rom);

        var engine = new ToneGenerator(renderer) { DrumMapRow = 1 };
        var program = Enumerable.Range(1, 127).First(p => renderer.Drums.KitForProgram(p, row: 1) is > 0);

        engine.SendChannel(0xC9, program, 0);
        Assert.NotEqual(0, engine.DrumKit);

        engine.Reset();

        // The kit is what a program change selected, so a reset undoes it; the row is configuration
        // the host set, in the same class as the tone map, so it survives.
        Assert.Equal(0, engine.DrumKit);
        Assert.Equal(1, engine.DrumMapRow);
    }
}
