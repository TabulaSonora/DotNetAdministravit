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
    private static JsonElement LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "smf_canyon.json");
        Skip.IfNot(File.Exists(path),
            "Fixture 'smf_canyon.json' not found. Regenerate with tools/gen_midi_fixtures.py.");

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.Clone();
    }

    private static string RequireMidi() => TestData.RequireMidi();

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

    /// <summary>
    /// Every note, matched to the reference by where it starts rather than by position in the list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reference releases a note at the sustain pedal's lift even when the note was struck
    /// <em>after</em> its own note-off was parked there — a re-strike leaves the parked entry behind,
    /// and the lift then closes the note the player is still holding. This port drops the parked entry
    /// on the re-strike, so those notes ring on to their real note-off.
    /// </para>
    /// <para>
    /// That changes the order notes close in, so an index-by-index comparison would report every note
    /// after the first affected one as wrong. Matching on (channel, note, on) instead keeps the
    /// comparison exact on all thirteen latched fields and isolates the difference to the one field it
    /// can touch: a note may end <em>later</em> than the reference says, never earlier, and nothing
    /// else may move. On canyon.mid that is 23 notes of 2,774.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public void ExtractsEveryNoteWithTheSameLatchedParameters()
    {
        var fixture = LoadFixture();
        var sequence = SequenceBuilder.Build(SmfReader.Read(RequireMidi()));
        var expected = fixture.GetProperty("notes");

        Assert.Equal(expected.GetArrayLength(), sequence.Notes.Count);

        // Several notes can share a start, so each key holds a list and matches are consumed.
        var byStart = new Dictionary<(int Channel, int Note, int On), List<JsonElement>>();
        foreach (var row in expected.EnumerateArray())
        {
            var key = (row[0].GetInt32(), row[1].GetInt32(), row[3].GetInt32());
            if (!byStart.TryGetValue(key, out var list))
            {
                byStart[key] = list = [];
            }

            list.Add(row);
        }

        var names = new[]
        {
            "channel", "note", "velocity", "on", "off", "program", "bank",
            "pan", "volume", "expression", "reverbSend", "chorusSend", "delaySend",
        };

        var compared = 0;
        var heldLonger = 0;

        foreach (var n in sequence.Notes)
        {
            var key = (n.Channel, n.Note, (int)n.On);
            Assert.True(byStart.TryGetValue(key, out var candidates) && candidates.Count > 0,
                $"note {compared}: channel {n.Channel} note {n.Note} at {n.On} is not in the reference.");

            // Prefer the row that agrees on the end, so an unaffected note never consumes the slot
            // belonging to one the pedal moved.
            var slot = candidates!.FindIndex(r => r[4].GetInt32() == (int)n.Off);
            var row = candidates[slot < 0 ? 0 : slot];
            candidates.RemoveAt(slot < 0 ? 0 : slot);

            var fields = new[]
            {
                n.Channel, n.Note, n.Velocity, (int)n.On, (int)n.Off, n.Program, n.Bank,
                n.Pan, n.Volume, n.Expression, n.ReverbSend, n.ChorusSend, n.DelaySend,
            };

            for (var f = 0; f < fields.Length; f++)
            {
                if (f == 4 && fields[f] != row[f].GetInt32())
                {
                    Assert.True(fields[f] > row[f].GetInt32(),
                        $"note {compared} ends at {fields[f]}, before the reference's {row[f].GetInt32()}; " +
                        "clearing a parked pedal entry can only let a note ring longer.");
                    heldLonger++;
                    continue;
                }

                Assert.True(row[f].GetInt32() == fields[f],
                    $"note {compared} {names[f]} = {fields[f]}, expected {row[f].GetInt32()}");
            }

            compared++;
        }

        Assert.True(compared > 2000, $"Only {compared} notes compared.");

        // A wholesale reordering would otherwise pass field-by-field; this bounds the divergence to
        // the handful of notes the pedal case can reach.
        Assert.True(heldLonger < compared / 20,
            $"{heldLonger} of {compared} notes outlive the reference, which is more than the sustain " +
            "pedal's re-strike case can explain.");
    }

    /// <summary>
    /// A note struck while the pedal is down outlives the lift, because its own note-off has not come.
    /// </summary>
    /// <remarks>
    /// The pedal parks a note-off rather than acting on it. Re-striking the same note must discard
    /// that parked entry — otherwise the lift releases the strike the player is still holding, and the
    /// note vanishes 20–80 ms after it sounds. onestop.mid's harpsichord passage rides the pedal every
    /// half second over constantly re-struck notes and loses 24 notes to it, which is audible as
    /// notes cutting off.
    /// </remarks>
    [Fact]
    public void ARestrikeUnderThePedalSurvivesTheLift()
    {
        const int Damper = 64;
        var grid = SmfReader.BlockGrid;

        // strike, pedal down, release (parked), strike again, pedal up, then a real note-off
        var sequence = SequenceBuilder.Build(
        [
            new MidiEvent { Position = 0 * grid, Status = 0x90, Data1 = 60, Data2 = 100 },
            new MidiEvent { Position = 10 * grid, Status = 0xB0, Data1 = Damper, Data2 = 127 },
            new MidiEvent { Position = 20 * grid, Status = 0x80, Data1 = 60, Data2 = 0 },
            new MidiEvent { Position = 30 * grid, Status = 0x90, Data1 = 60, Data2 = 100 },
            new MidiEvent { Position = 40 * grid, Status = 0xB0, Data1 = Damper, Data2 = 0 },
            new MidiEvent { Position = 50 * grid, Status = 0x80, Data1 = 60, Data2 = 0 },
        ]);

        Assert.Equal(2, sequence.Notes.Count);

        // The first strike ends where the second one takes its voice.
        Assert.Equal(0 * grid, sequence.Notes[0].On);
        Assert.Equal(30 * grid, sequence.Notes[0].Off);

        // The second survives the lift at 40 and ends at its own note-off.
        Assert.Equal(30 * grid, sequence.Notes[1].On);
        Assert.Equal(50 * grid, sequence.Notes[1].Off);
    }

    /// <summary>A note-off parked by the pedal and never re-struck still releases at the lift.</summary>
    [Fact]
    public void APedalledNoteOffStillReleasesAtTheLift()
    {
        const int Damper = 64;
        var grid = SmfReader.BlockGrid;

        var sequence = SequenceBuilder.Build(
        [
            new MidiEvent { Position = 0 * grid, Status = 0x90, Data1 = 60, Data2 = 100 },
            new MidiEvent { Position = 10 * grid, Status = 0xB0, Data1 = Damper, Data2 = 127 },
            new MidiEvent { Position = 20 * grid, Status = 0x80, Data1 = 60, Data2 = 0 },
            new MidiEvent { Position = 40 * grid, Status = 0xB0, Data1 = Damper, Data2 = 0 },
        ]);

        var note = Assert.Single(sequence.Notes);
        Assert.Equal(40 * grid, note.Off);
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
