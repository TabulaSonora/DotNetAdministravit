using TabulaSonora.Dsp;
using TabulaSonora.Patches;
using TabulaSonora.Rom;

namespace TabulaSonora.Tests;

/// <summary>
/// Half-damper: the CC64 value scaling the release rates on the piano tones.
/// </summary>
public class HalfDamperTests
{
    [SkippableFact]
    public void ExactlyThePianosCarryTheCapability()
    {
        var tables = TableSet.FromCacheDirectory(TestData.RequireTables());
        var directory = new PatchDirectory(tables);

        Assert.True(directory.HalfDamper(0));     // Piano 1
        Assert.True(directory.HalfDamper(8));     // Piano+Choir1
        Assert.False(directory.HalfDamper(44));   // Harpsi.o
        Assert.False(directory.HalfDamper(161));  // Nylon-str.Gt

        var count = 0;
        for (var tone = 0; tone < directory.ToneCount; tone++)
        {
            if (directory.HalfDamper(tone))
            {
                count++;
            }
        }

        // Tone header byte 0x0d bit 2 — the piano family, wall to wall. Pinned so a table-read
        // regression cannot silently widen the feature.
        Assert.Equal(57, count);
    }

    [SkippableFact]
    public void HalfPedalStretchesTheSegmentEnvelopeRelease()
    {
        var tables = TableSet.FromCacheDirectory(TestData.RequireTables());
        var machine = new EnvelopeMachine(tables);

        SegmentEnvelope Build() => new(
            machine,
            targets: [1.0, 1.0, 1.0, 1.0],
            segmentSamples: [320.0, 0, 0, 0],
            linear: [true, true, true, true],
            releaseTarget: 0.0,
            releaseSamples: 32000.0,
            releaseLinear: true,
            afterRelease: 0.0,
            controlTickSamples: 320);

        var full = Build();
        full.NoteOff(3200);

        var half = Build();
        half.NoteOff(3200, damper: 32);

        // rate x (0xffff - (32<<9))/0x10000 -> duration x 65536/49151.
        Assert.Equal(32000, full.ReleaseSamples);
        Assert.Equal((long)(32000.0 * 65536 / 49151), half.ReleaseSamples);

        // Same start, slower descent.
        var at = full.NoteOffSample + 16000;
        Assert.True(half.ValueAt(at) > full.ValueAt(at));
        Assert.False(half.IsFinished(full.NoteOffSample + 32000));
    }

    [Fact]
    public void HalfPedalStretchesThePitchRelease()
    {
        PitchEnvelopeRunner Build() => new(new PitchEnvelope(
            Start: 1000,
            Targets: [1000, 1000, 1000, 0],
            Release: -2000,
            Times: [0, 0, 0, 0],
            ReleaseMs: 1000.0));

        int TicksToRelease(PitchEnvelopeRunner runner)
        {
            for (var tick = 1; tick < 1000; tick++)
            {
                if (runner.Tick(released: true) == -2000)
                {
                    return tick;
                }
            }

            return -1;
        }

        var full = Build();
        var half = Build();
        half.SetReleaseDamper(32);

        var fullTicks = TicksToRelease(full);
        var halfTicks = TicksToRelease(half);

        // Rate 655 -> 491 under (0xffff - 0x4000) >> 16, so about a third longer.
        Assert.Equal(101, fullTicks);
        Assert.Equal(134, halfTicks);
    }
}
