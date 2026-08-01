using System.Text.Json;
using TabulaSonora.Dsp;
using TabulaSonora.Patches;
using TabulaSonora.Rom;

namespace TabulaSonora.Tests;

/// <summary>
/// Pitch and LFO, checked against the reference. Both are claimed bit-exact against the engine's own
/// per-tick trace, so these assert exact integer equality rather than a tolerance — except the three
/// partials carrying the hardware's random start jitter, which are partitioned to the jitter's own
/// bound in <see cref="PitchEnvelopeSegmentMachineMatchesTheReferenceTickForTick"/>.
/// </summary>
public class PitchLfoDifferentialTests
{
    private static JsonElement Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", name);
        Skip.IfNot(File.Exists(path), $"Fixture '{name}' not found. Regenerate with tools/gen_fixtures.py.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.GetProperty("values").Clone();
    }

    private static (PatchDirectory Directory, LfoEngine Lfo, PitchChain Pitch) LoadChain()
    {
        var tables = TableSet.FromCacheDirectory(TestData.RequireTables());
        var envelope = new EnvelopeMachine(tables);
        return (new PatchDirectory(tables), new LfoEngine(tables), new PitchChain(tables, envelope));
    }

    [SkippableFact]
    public void EveryLfoWaveformMatchesTheReference()
    {
        var (_, lfo, _) = LoadChain();
        var checkedRows = 0;

        foreach (var row in Load("lfo_wave.json").EnumerateArray())
        {
            var waveform = row[0].GetInt32();
            var phase = row[1].GetInt32();
            var expected = row[2].GetInt32();

            var actual = lfo.Waveform(phase, waveform);
            Assert.True(expected == actual,
                $"Waveform({phase}, sel {waveform}) = {actual}, expected {expected}");
            checkedRows++;
        }

        Assert.True(checkedRows > 5000);

        // Selectors 1-3 are the Galois-LFSR random shapes; they need the engine's RNG state and are
        // deliberately silent rather than invented.
        Assert.Equal(0, lfo.Waveform(0x1234, 1));
        Assert.Equal(0, lfo.Waveform(0x1234, 2));
        Assert.Equal(0, lfo.Waveform(0x1234, 3));
    }

    [SkippableFact]
    public void ModWheelDepthMatchesTheReference()
    {
        foreach (var row in Load("lfo_modwheel.json").EnumerateArray())
        {
            var controller = row[0].GetInt32();
            var depth = row[1].GetInt32();
            var offset = row[2].GetInt32();
            var expected = row[3].GetInt32();

            var actual = LfoEngine.ModWheelDepth(controller, depth, offset);
            Assert.True(expected == actual,
                $"ModWheelDepth({controller}, {depth}, {offset}) = {actual}, expected {expected}");
        }

        // Full scale is six semitones, which is the GS figure.
        Assert.Equal(6000, LfoEngine.ModWheelDepth(127, 0x7F));
    }

    [SkippableFact]
    public void LfoConfigurationMatchesTheReferenceOnEveryRealPartial()
    {
        var (directory, lfo, _) = LoadChain();
        var checkedRows = 0;

        foreach (var row in Load("lfo_config.json").EnumerateArray())
        {
            var tone = row[0].GetInt32();
            var slot = row[1].GetInt32();

            var partial = directory.GetPartialBySlot(tone, slot);
            var (first, second) = lfo.Configure(tone, partial);

            AssertConfig(row[2], first, tone, slot, "LFO1");
            AssertConfig(row[3], second, tone, slot, "LFO2");
            checkedRows++;
        }

        Assert.True(checkedRows > 500, $"Only {checkedRows} configurations checked.");
    }

    [SkippableFact]
    public void LfoModulationMatchesTheReferenceTickForTick()
    {
        var (directory, lfo, _) = LoadChain();
        var checkedSeries = 0;

        foreach (var row in Load("lfo_modulation.json").EnumerateArray())
        {
            var tone = row[0].GetInt32();
            var slot = row[1].GetInt32();
            var destination = row[2].GetString() switch
            {
                "pitch" => LfoDestination.Pitch,
                "tvf" => LfoDestination.Tvf,
                "tva" => LfoDestination.Tva,
                var other => throw new InvalidOperationException($"Unknown destination '{other}'."),
            };

            var expected = row[3];
            var partial = directory.GetPartialBySlot(tone, slot);
            var actual = lfo.Modulation(tone, partial, expected.GetArrayLength(), destination);

            var tick = 0;
            foreach (var value in expected.EnumerateArray())
            {
                Assert.True(value.GetDouble() == actual[tick],
                    $"tone {tone} slot {slot} {destination} tick {tick}: {actual[tick]}, expected {value.GetDouble()}");
                tick++;
            }

            // The first tick is always zero: the LFO object is created after that tick's update.
            Assert.Equal(0.0, actual[0]);
            checkedSeries++;
        }

        Assert.True(checkedSeries > 1500, $"Only {checkedSeries} modulation series checked.");
    }

    [SkippableFact]
    public void PitchKeyFollowAndBasePitchMatchTheReference()
    {
        var (directory, _, pitch) = LoadChain();
        var checkedRows = 0;

        foreach (var row in Load("pitch_keyfollow.json").EnumerateArray())
        {
            var tone = row[0].GetInt32();
            var slot = row[1].GetInt32();
            var note = row[2].GetInt32();
            var keyCenter = row[3].GetInt32();
            var expectedKey = row[4].GetInt32();
            var expectedWeight = row[5].GetInt32();
            var expectedBase = row[6].GetInt32();

            var partial = directory.GetPartialBySlot(tone, slot);
            var (key, weight) = PitchChain.KeyFollowKey(partial, note, keyCenter);

            Assert.True(expectedKey == key,
                $"key(tone {tone} slot {slot}, note {note}) = {key}, expected {expectedKey}");
            Assert.True(expectedWeight == weight,
                $"weight(tone {tone} slot {slot}, note {note}) = {weight}, expected {expectedWeight}");

            var actualBase = pitch.BasePitchMilliSemitones(partial, note, keyCenter);
            Assert.True(expectedBase == actualBase,
                $"basePitch(tone {tone} slot {slot}, note {note}) = {actualBase}, expected {expectedBase}");
            checkedRows++;
        }

        Assert.True(checkedRows > 5000, $"Only {checkedRows} key-follow rows checked.");
    }

    [SkippableFact]
    public void PitchEnvelopeOffsetsMatchTheReference()
    {
        var (directory, _, pitch) = LoadChain();
        var withEnvelope = 0;
        var without = 0;

        foreach (var row in Load("pitch_env_offsets.json").EnumerateArray())
        {
            var tone = row[0].GetInt32();
            var slot = row[1].GetInt32();
            var velocity = row[2].GetInt32();
            var key = row[3].GetInt32();

            var partial = directory.GetPartialBySlot(tone, slot);
            var actual = pitch.EnvelopeOffsets(partial, key, velocity);

            if (row[4].ValueKind == JsonValueKind.Null)
            {
                Assert.True(actual is null, $"tone {tone} slot {slot}: expected no pitch envelope.");
                without++;
                continue;
            }

            Assert.True(actual is not null, $"tone {tone} slot {slot}: expected a pitch envelope.");
            var value = actual!.Value;
            var expected = row[4];

            Assert.Equal(expected[0].GetInt32(), value.Start);
            Assert.Equal(expected[2].GetInt32(), value.Release);

            var index = 0;
            foreach (var target in expected[1].EnumerateArray())
            {
                Assert.True(target.GetInt32() == value.Targets[index],
                    $"tone {tone} slot {slot} target {index}: {value.Targets[index]}, expected {target.GetInt32()}");
                index++;
            }

            index = 0;
            foreach (var time in expected[3].EnumerateArray())
            {
                Assert.True(Math.Abs(time.GetDouble() - value.Times[index]) < 1e-9,
                    $"tone {tone} slot {slot} time {index}: {value.Times[index]}, expected {time.GetDouble()}");
                index++;
            }

            Assert.True(Math.Abs(expected[4].GetDouble() - value.ReleaseMs) < 1e-9);
            withEnvelope++;
        }

        Assert.True(withEnvelope > 500, $"Only {withEnvelope} pitch envelopes checked.");
        Assert.True(without > 0, "Expected some partials to have no pitch envelope at all.");
    }

    [SkippableFact]
    public void PitchEnvelopeSegmentMachineMatchesTheReferenceTickForTick()
    {
        var (directory, _, pitch) = LoadChain();
        var checkedSeries = 0;
        var jittered = 0;

        foreach (var row in Load("pitch_env_ticks.json").EnumerateArray())
        {
            var tone = row[0].GetInt32();
            var slot = row[1].GetInt32();
            var expected = row[2];

            var partial = directory.GetPartialBySlot(tone, slot);
            var actual = pitch.EnvelopeTicks(partial, key: 60, velocity: 100, holdSeconds: 0.5,
                tickCount: expected.GetArrayLength());

            Assert.True(actual is not null, $"tone {tone} slot {slot}: expected a pitch envelope.");

            // The reference does not model the block[0x1a] random start jitter the hardware
            // applies; the engine draws it on every note-on, so those rows are held to the jitter's
            // own bound (about [-10, +5] x depth, on the start level only) rather than exactness.
            var jitterDepth = partial.Raw[0x1A];
            var tolerance = jitterDepth != 0 ? (jitterDepth * 10) + 1e-9 : 1e-9;
            if (jitterDepth != 0)
            {
                jittered++;
            }

            var tick = 0;
            foreach (var value in expected.EnumerateArray())
            {
                Assert.True(Math.Abs(value.GetDouble() - actual![tick]) <= tolerance,
                    $"tone {tone} slot {slot} tick {tick}: {actual[tick]}, expected {value.GetDouble()}");
                tick++;
            }

            checkedSeries++;
        }

        Assert.True(checkedSeries > 50, $"Only {checkedSeries} pitch-envelope series checked.");

        // Tones 0-399, slots 0-1: Jazz Bass 2 (246, both partials) and Fretless Bs2 (261, partial
        // 0) are the only jitter carriers with an active envelope. Pinned so the jitter path cannot
        // silently widen or stop applying.
        Assert.Equal(3, jittered);
    }

    [SkippableFact]
    public void AbsolutePitchClampIsExpressible()
    {
        // Jetplane's first partial sits exactly on the clamp: a base of 24000 with an envelope
        // starting at -24000 lands on zero, five octaves below its sample's root. An offset-relative
        // model cannot represent that at all.
        Assert.Equal(0.0, PitchChain.Clamp(24000 - 24000));
        Assert.Equal(0.0, PitchChain.Clamp(-5000));
        Assert.Equal(PitchChain.MaxPitchMilliSemitones, PitchChain.Clamp(200_000));

        // The clamp is what fixes the unit: 0x1f018 is exactly 127 semitones x 1000, so pitch is in
        // milli-semitones rather than cents.
        Assert.Equal(127_000, PitchChain.MaxPitchMilliSemitones);
    }

    [SkippableFact]
    public void BendIsLinearInTheWheelValue()
    {
        Assert.Equal(0.0, PitchChain.BendOffsetMilliSemitones(8192));
        Assert.Equal(2000.0, PitchChain.BendOffsetMilliSemitones(16384, 2), 6);
        Assert.Equal(-2000.0, PitchChain.BendOffsetMilliSemitones(0, 2), 6);
        Assert.Equal(12000.0, PitchChain.BendOffsetMilliSemitones(16384, 12), 6);
    }

    private static void AssertConfig(JsonElement expected, LfoConfig actual, int tone, int slot, string which)
    {
        var fields = new[]
        {
            actual.Waveform, actual.InitialPhase, actual.Increment, actual.DelayRate,
            actual.FadeRate, actual.PitchDepth, actual.TvfDepth, actual.TvaDepth,
        };

        var names = new[] { "wave", "phase0", "inc", "delay", "fade", "pitch", "tvf", "tva" };

        for (var i = 0; i < fields.Length; i++)
        {
            Assert.True(expected[i].GetInt32() == fields[i],
                $"tone {tone} slot {slot} {which}.{names[i]} = {fields[i]}, expected {expected[i].GetInt32()}");
        }
    }
}
