using System.Text.Json;
using TabulaSonora.Dsp;
using TabulaSonora.Patches;
using TabulaSonora.Rom;

namespace TabulaSonora.Tests;

/// <summary>
/// Differential tests against the Python reference implementation.
/// </summary>
/// <remarks>
/// <para>
/// The golden traces only cover the handful of values the harness could read out of the live engine.
/// These fixtures instead sweep every ported fixed-point routine over its whole input domain, which
/// is what catches the sign-extension, truncation-direction and integer-width mistakes a
/// transliteration from Python invites — Python's <c>//</c> floors where C's <c>/</c> truncates, and
/// several of these products overflow 32 bits.
/// </para>
/// <para>
/// The fixtures are regenerated with <c>uv run --with numpy python tools/gen_fixtures.py</c>, so the
/// oracle stays live without making Python a test dependency. Where the reference and the real engine
/// disagree, the engine wins — see the trace-conformance tests.
/// </para>
/// </remarks>
public class DifferentialTests
{
    private static readonly string FixtureDirectory =
        Path.Combine(AppContext.BaseDirectory, "fixtures");

    private static JsonElement Load(string name)
    {
        var path = Path.Combine(FixtureDirectory, name);
        Skip.IfNot(File.Exists(path),
            $"Fixture '{name}' not found. Regenerate with tools/gen_fixtures.py.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.GetProperty("values").Clone();
    }

    private static EnvelopeMachine Envelope() =>
        new(TableSet.FromCacheDirectory(TestData.RequireTables()));

    private static (PatchDirectory Directory, TvaChain Tva) LoadChain()
    {
        var tables = TableSet.FromCacheDirectory(TestData.RequireTables());
        return (new PatchDirectory(tables), new TvaChain(tables, new EnvelopeMachine(tables)));
    }

    [SkippableFact]
    public void RateScaleMatchesTheReferenceAcrossItsWholeDomain()
    {
        var envelope = Envelope();
        var values = Load("env_rate_scale.json");

        var baseRate = 0;
        foreach (var row in values.EnumerateArray())
        {
            var modifier = 0;
            foreach (var expected in row.EnumerateArray())
            {
                var actual = envelope.RateScale(baseRate, modifier);
                Assert.True(expected.GetInt32() == actual,
                    $"RateScale({baseRate}, {modifier}) = {actual}, expected {expected.GetInt32()}");
                modifier++;
            }

            baseRate++;
        }

        Assert.Equal(256, baseRate);
    }

    [SkippableFact]
    public void LevelScaleMatchesTheReferenceAcrossItsWholeDomain()
    {
        var envelope = Envelope();
        var values = Load("env_level_scale.json");

        var level = 0;
        foreach (var row in values.EnumerateArray())
        {
            var modifier = 0;
            foreach (var expected in row.EnumerateArray())
            {
                var actual = envelope.LevelScale(level, modifier);
                Assert.True(expected.GetInt32() == actual,
                    $"LevelScale({level}, {modifier}) = {actual}, expected {expected.GetInt32()}");
                modifier++;
            }

            level++;
        }

        Assert.Equal(256, level);
    }

    [SkippableFact]
    public void SegmentTimingMatchesTheReference()
    {
        var envelope = Envelope();
        var values = Load("seg_ms.json");
        var checkedRows = 0;

        foreach (var row in values.EnumerateArray())
        {
            var rateByte = row[0].GetInt32();
            var rateMultiplier = row[1].GetInt32();
            var velocityMultiplier = row[2].GetInt32();
            var expected = row[3].GetDouble();

            var actual = envelope.SegmentMilliseconds(rateByte, rateMultiplier, velocityMultiplier);
            Assert.True(Math.Abs(expected - actual) < 1e-9,
                $"SegmentMilliseconds({rateByte}, {rateMultiplier}, {velocityMultiplier}) = {actual}, expected {expected}");
            checkedRows++;
        }

        Assert.Equal(256 * 4 * 3, checkedRows);
    }

    [SkippableFact]
    public void AmplitudeCurveMatchesTheReference()
    {
        var (_, tva) = LoadChain();
        var values = Load("amp_of.json");
        var checkedRows = 0;

        foreach (var row in values.EnumerateArray())
        {
            var level = row[0].GetInt32();
            var expected = row[1].GetDouble();
            var actual = tva.AmpOf(level);

            // The product exceeds 32 bits before the shift; a narrow intermediate shows up here.
            Assert.True(Math.Abs(expected - actual) < 1e-12,
                $"AmpOf({level}) = {actual}, expected {expected}");
            checkedRows++;
        }

        Assert.True(checkedRows > 3000);
    }

    [SkippableFact]
    public void SegmentShapeMatchesTheReference()
    {
        var envelope = Envelope();
        var values = Load("seg_curve.json");
        var checkedRows = 0;

        foreach (var row in values.EnumerateArray())
        {
            var linear = row[0].GetInt32() != 0;
            var start = row[1].GetDouble();
            var target = row[2].GetDouble();
            var position = row[3].GetDouble();
            var expected = row[4].GetDouble();

            var actual = envelope.SegmentCurve(position, start, target, linear);
            Assert.True(Math.Abs(expected - actual) < 1e-12,
                $"SegmentCurve({position}, {start}, {target}, linear:{linear}) = {actual}, expected {expected}");
            checkedRows++;
        }

        Assert.Equal(2 * 4 * 257, checkedRows);
    }

    [SkippableFact]
    public void PartVolumeScaleMatchesTheReference()
    {
        var values = Load("part_volume.json");
        foreach (var row in values.EnumerateArray())
        {
            var volume = row[0].GetInt32();
            var expression = row[1].GetInt32();
            var master = row[2].GetInt32();
            var expected = row[3].GetDouble();

            var actual = TvaChain.PartVolumeScale(volume, expression, master);
            Assert.True(Math.Abs(expected - actual) < 1e-12,
                $"PartVolumeScale({volume}, {expression}, {master}) = {actual}, expected {expected}");
        }
    }

    [SkippableFact]
    public void PartialLevelMatchesTheReferenceOnEveryRealPartial()
    {
        var (directory, tva) = LoadChain();
        var values = Load("partial_level.json");
        var checkedRows = 0;

        foreach (var row in values.EnumerateArray())
        {
            var tone = row[0].GetInt32();
            var slot = row[1].GetInt32();
            var velocity = row[2].GetInt32();
            var expected = row[3].GetInt32();

            var partial = directory.GetPartialBySlot(tone, slot);
            var actual = tva.PartialLevel(partial, velocity) ?? -1;

            Assert.True(expected == actual,
                $"PartialLevel(tone {tone} slot {slot}, vel {velocity}) = {actual}, expected {expected}");
            checkedRows++;
        }

        Assert.True(checkedRows > 5000, $"Only {checkedRows} partial-level rows checked.");
    }

    [SkippableFact]
    public void TvaBaseLevelMatchesTheReferenceOnEveryRealPartial()
    {
        var (directory, tva) = LoadChain();
        var values = Load("tva_base_level.json");
        var checkedRows = 0;

        foreach (var row in values.EnumerateArray())
        {
            var tone = row[0].GetInt32();
            var slot = row[1].GetInt32();
            var velocity = row[2].GetInt32();
            var key = row[3].GetInt32();
            var expected = row[4].GetInt32();

            var partial = directory.GetPartialBySlot(tone, slot);
            var partialLevel = tva.PartialLevel(partial, velocity);
            Assert.NotNull(partialLevel);

            var actual = tva.BaseLevel(partial, partialLevel!.Value, key, zoneLevel: 127, toneLevel: 127);
            Assert.True(expected == actual,
                $"BaseLevel(tone {tone} slot {slot}, vel {velocity}, key {key}) = {actual}, expected {expected}");
            checkedRows++;
        }

        Assert.True(checkedRows > 20000, $"Only {checkedRows} base-level rows checked.");
    }
}
