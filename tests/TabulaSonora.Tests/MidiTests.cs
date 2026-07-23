using System.Text.Json;
using TabulaSonora.Midi;

namespace TabulaSonora.Tests;

/// <summary>
/// SMF parsing and note extraction, checked against the reference on a real multi-track file.
/// </summary>
/// <remarks>
/// Parsing is where a port diverges quietly: running status, the tempo map, sysex framing and the
/// render-grid quantisation all have to agree exactly, or every downstream comparison is measuring
/// the wrong notes at the wrong times and still looks plausible.
/// </remarks>
public class MidiTests
{
    /// <summary>
    /// Where the test MIDI file might live. Relative to the repository so no local layout is baked in.
    /// </summary>
    private static readonly string[] MidiCandidates =
    [
        Path.Combine(TestData.RepositoryRoot, "canyon.mid"),
        Path.Combine(TestData.RepositoryRoot, "..", "SauceForYourEars", "canyon.mid"),
        @"C:\Windows\Media\town.mid",
    ];

    private static JsonElement LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "smf_canyon.json");
        Skip.IfNot(File.Exists(path),
            "Fixture 'smf_canyon.json' not found. Regenerate with tools/gen_midi_fixtures.py.");

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.Clone();
    }

    private static string RequireMidi()
    {
        var path = MidiCandidates.FirstOrDefault(File.Exists);
        Skip.If(path is null, "canyon.mid not found.");
        return path!;
    }

    [SkippableFact]
    public void ParsesEveryEventExactlyAsTheReferenceDoes()
    {
        var fixture = LoadFixture();
        var events = SmfReader.Read(RequireMidi());
        var expected = fixture.GetProperty("events");

        Assert.Equal(expected.GetArrayLength(), events.Count);

        var index = 0;
        foreach (var row in expected.EnumerateArray())
        {
            var e = events[index];
            Assert.True(row[0].GetInt64() == e.Position,
                $"event {index}: position {e.Position}, expected {row[0].GetInt64()}");

            if (row[1].GetString() == "sx")
            {
                Assert.Equal(MidiEventKind.SysEx, e.Kind);
                var bytes = row[2].EnumerateArray().Select(b => (byte)b.GetInt32()).ToArray();
                Assert.Equal(bytes, e.SysEx);

                // The stored payload omits the leading F0; a reader that forgets to restore it
                // produces frames that no SysEx handler will recognise.
                Assert.Equal(0xF0, e.SysEx![0]);
            }
            else
            {
                Assert.Equal(MidiEventKind.Channel, e.Kind);
                Assert.True(row[2].GetInt32() == e.Status,
                    $"event {index}: status 0x{e.Status:X2}, expected 0x{row[2].GetInt32():X2}");
                Assert.Equal(row[3].GetInt32(), e.Data1);
                Assert.Equal(row[4].GetInt32(), e.Data2);
            }

            index++;
        }

        Assert.True(index > 5000, $"Only {index} events compared.");
    }

    [SkippableFact]
    public void ExtractsEveryNoteWithTheSameLatchedParameters()
    {
        var fixture = LoadFixture();
        var sequence = SequenceBuilder.Build(SmfReader.Read(RequireMidi()));
        var expected = fixture.GetProperty("notes");

        Assert.Equal(expected.GetArrayLength(), sequence.Notes.Count);

        var index = 0;
        foreach (var row in expected.EnumerateArray())
        {
            var n = sequence.Notes[index];
            var fields = new[]
            {
                n.Channel, n.Note, n.Velocity, (int)n.On, (int)n.Off, n.Program, n.Bank,
                n.Pan, n.Volume, n.Expression, n.ReverbSend, n.ChorusSend, n.DelaySend,
            };

            var names = new[]
            {
                "channel", "note", "velocity", "on", "off", "program", "bank",
                "pan", "volume", "expression", "reverbSend", "chorusSend", "delaySend",
            };

            for (var f = 0; f < fields.Length; f++)
            {
                Assert.True(row[f].GetInt32() == fields[f],
                    $"note {index} {names[f]} = {fields[f]}, expected {row[f].GetInt32()}");
            }

            index++;
        }

        Assert.True(index > 2000, $"Only {index} notes compared.");
    }

    [SkippableFact]
    public void EveryEventLandsOnTheRenderBlockGrid()
    {
        // Events are applied on a 32-sample boundary, which is what lets an offline render line up
        // with the real engine's own output rather than drifting by a fraction of a block.
        foreach (var e in SmfReader.Read(RequireMidi()))
        {
            Assert.Equal(0, e.Position % SmfReader.BlockGrid);
        }
    }

    [Fact]
    public void GsBlockNumbersAreNotChannelNumbers()
    {
        // Block 0 is the drum part on channel 9; blocks 1-9 are channels 0-8. Getting this wrong
        // sends a part parameter to the neighbouring channel, which is easy to miss by ear.
        Assert.Equal(9, SequenceBuilder.ChannelFromBlock(0));
        Assert.Equal(0, SequenceBuilder.ChannelFromBlock(1));
        Assert.Equal(8, SequenceBuilder.ChannelFromBlock(9));
        Assert.Equal(10, SequenceBuilder.ChannelFromBlock(10));
        Assert.Equal(15, SequenceBuilder.ChannelFromBlock(15));
    }

    [Fact]
    public void DamperDefersReleaseUntilThePedalLifts()
    {
        var events = new List<MidiEvent>
        {
            new(0, MidiEventKind.Channel, 0xB0, 64, 127, null),      // damper down
            new(32, MidiEventKind.Channel, 0x90, 60, 100, null),     // note on
            new(64, MidiEventKind.Channel, 0x80, 60, 0, null),       // note off, held by the pedal
            new(3200, MidiEventKind.Channel, 0xB0, 64, 0, null),     // pedal up
        };

        var note = Assert.Single(SequenceBuilder.Build(events).Notes);
        Assert.Equal(32, note.On);
        Assert.Equal(3200, note.Off);
    }

    [Fact]
    public void RestrikingAnOpenNoteClosesThePreviousOne()
    {
        var events = new List<MidiEvent>
        {
            new(0, MidiEventKind.Channel, 0x90, 60, 100, null),
            new(320, MidiEventKind.Channel, 0x90, 60, 90, null),
            new(640, MidiEventKind.Channel, 0x80, 60, 0, null),
        };

        var notes = SequenceBuilder.Build(events).Notes;
        Assert.Equal(2, notes.Count);
        Assert.Equal((0L, 320L), (notes[0].On, notes[0].Off));
        Assert.Equal((320L, 640L), (notes[1].On, notes[1].Off));
    }

    [Fact]
    public void NoteOnWithZeroVelocityIsANoteOff()
    {
        var events = new List<MidiEvent>
        {
            new(0, MidiEventKind.Channel, 0x90, 60, 100, null),
            new(320, MidiEventKind.Channel, 0x90, 60, 0, null),
        };

        var note = Assert.Single(SequenceBuilder.Build(events).Notes);
        Assert.Equal(320, note.Off);
    }

    [Fact]
    public void ParametersAreLatchedAtNoteOnNotAtNoteOff()
    {
        var events = new List<MidiEvent>
        {
            new(0, MidiEventKind.Channel, 0xB0, 7, 90, null),       // volume 90
            new(32, MidiEventKind.Channel, 0x90, 60, 100, null),    // note on
            new(64, MidiEventKind.Channel, 0xB0, 7, 20, null),      // volume drops mid-note
            new(320, MidiEventKind.Channel, 0x80, 60, 0, null),
        };

        var note = Assert.Single(SequenceBuilder.Build(events).Notes);
        Assert.Equal(90, note.Volume);
    }

    [Fact]
    public void PartDelaySendArrivesOnlyOverSysEx()
    {
        // There is no Control Change for the delay send at all. The address is 40 1x 2C, where x is
        // the GS block number rather than the channel.
        byte[] sysEx = [0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x12, 0x2C, 0x64, 0x00, 0xF7];

        var events = new List<MidiEvent>
        {
            new(0, MidiEventKind.SysEx, 0, 0, 0, sysEx),
            new(32, MidiEventKind.Channel, 0x91, 60, 100, null),    // channel 1 = block 2
            new(320, MidiEventKind.Channel, 0x81, 60, 0, null),
        };

        var note = Assert.Single(SequenceBuilder.Build(events).Notes);
        Assert.Equal(1, note.Channel);
        Assert.Equal(0x64, note.DelaySend);
    }

    [Fact]
    public void GsMacrosSelectTheEffectAlgorithms()
    {
        byte[] reverb = [0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x01, 0x30, 0x05, 0x00, 0xF7];
        byte[] chorus = [0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x01, 0x38, 0x03, 0x00, 0xF7];
        byte[] delay = [0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x01, 0x50, 0x09, 0x00, 0xF7];

        var sequence = SequenceBuilder.Build(
        [
            new(0, MidiEventKind.SysEx, 0, 0, 0, reverb),
            new(0, MidiEventKind.SysEx, 0, 0, 0, chorus),
            new(0, MidiEventKind.SysEx, 0, 0, 0, delay),
        ]);

        Assert.Equal(5, sequence.ReverbType.ValueAt(0, -1));
        Assert.Equal(3, sequence.ChorusType.ValueAt(0, -1));
        Assert.Equal(9, sequence.DelayType.ValueAt(0, -1));
    }
}
