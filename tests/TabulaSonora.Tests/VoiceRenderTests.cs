using System.Buffers.Binary;
using System.Text.Json;
using TabulaSonora;
using TabulaSonora.Patches;
using TabulaSonora.Rom;

namespace TabulaSonora.Tests;

/// <summary>
/// The whole voice chain, checked note by note against the reference.
/// </summary>
/// <remarks>
/// <para>
/// A rendered note exercises the codec, the 4-tap resampler, the pitch chain, the filter, the
/// amplitude envelope, both LFOs and the pan law in one artifact, so this covers the synthesis path
/// end to end rather than a stage at a time.
/// </para>
/// <para>
/// This is deliberately <em>not</em> a sample-exact comparison. The reference stops decoding one
/// sample short of the data end, so its forward loop substitutes the loop's first sample for the
/// last one and plays it twice per pass. The real engine does not, and matching the reference there
/// measurably dulls the timbre — on a short single-cycle loop it moves the spectral centroid by well
/// over a kilohertz. This port follows the hardware instead, which leaves a small, bounded
/// divergence from the reference on looped waves and none at all elsewhere.
/// </para>
/// </remarks>
public class VoiceRenderTests
{
    /// <summary>Correlation floor: the shape must still track the reference almost perfectly.</summary>
    private const double MinimumCorrelation = 0.9999;

    /// <summary>How far the overall level may drift from the reference.</summary>
    private const double LevelTolerance = 0.001;

    /// <summary>Bound on any single sample's divergence, which the loop-end fix keeps small.</summary>
    private const double MaximumDivergence = 0.05;

    private static NoteRenderer? _renderer;

    // The image has to outlive the renderer: wave data is read on demand, not cached up front.
    private static RomImage? _rom;
    private static readonly Lock Gate = new();

    private static NoteRenderer Renderer()
    {
        lock (Gate)
        {
            if (_renderer is null)
            {
                _rom = RomImage.Open(TestData.RequireSccore(), RomVerification.Quick);
                _renderer = new NoteRenderer(_rom);
            }

            return _renderer;
        }
    }

    /// <summary>
    /// A sequence renderer over the shared note renderer, so other fixtures need not reload the ROM.
    /// </summary>
    /// <returns>The renderer.</returns>
    internal static SequenceRenderer SharedSequenceRenderer() => new(Renderer());

    private static JsonElement Index()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "voices.json");
        Skip.IfNot(File.Exists(path),
            "Fixture 'voices.json' not found. Regenerate with tools/gen_voice_fixtures.py.");

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.Clone();
    }

    [SkippableFact]
    public void EveryMelodicNoteMatchesTheReferenceSampleForSample()
    {
        var renderer = Renderer();
        var checkedNotes = 0;

        foreach (var entry in Index().GetProperty("melodic").EnumerateArray())
        {
            var program = entry.GetProperty("program").GetInt32();
            var note = entry.GetProperty("note").GetInt32();
            var velocity = entry.GetProperty("velocity").GetInt32();
            var hold = entry.GetProperty("hold").GetDouble();
            var expectedName = entry.GetProperty("name").GetString();
            var expected = LoadFixture(entry.GetProperty("file").GetString()!);

            var voice = renderer.RenderNote(program, note, velocity, hold, tailSeconds: 1.8);

            Assert.Equal(expectedName, voice.Name);
            AssertMatches(expected, voice, $"program {program} note {note}");
            checkedNotes++;
        }

        Assert.True(checkedNotes >= 6, $"Only {checkedNotes} melodic notes checked.");
    }

    [SkippableFact]
    public void EveryDrumHitMatchesTheReferenceSampleForSample()
    {
        var renderer = Renderer();
        var checkedNotes = 0;

        foreach (var entry in Index().GetProperty("drums").EnumerateArray())
        {
            var note = entry.GetProperty("note").GetInt32();
            var velocity = entry.GetProperty("velocity").GetInt32();
            var expected = LoadFixture(entry.GetProperty("file").GetString()!);

            var voice = renderer.RenderDrumNote(note, velocity, ringSeconds: 1.8);

            Assert.Equal(entry.GetProperty("name").GetString(), voice.Name);
            AssertMatches(expected, voice, $"drum note {note}");
            checkedNotes++;
        }

        Assert.True(checkedNotes >= 5, $"Only {checkedNotes} drum hits checked.");
    }

    [SkippableFact]
    public void DrumsTakeADifferentPitchRouteThanMelodicNotes()
    {
        // Drums do not transpose the sample: the tone resolves at key 60 and the kit's coarse-pitch
        // plane supplies the offset. Routing a drum through the melodic absolute-pitch chain gives
        // an audibly wrong kit -- it was worth about 0.12 of full scale on every hit.
        var renderer = Renderer();

        var low = renderer.RenderDrumNote(41, 110, ringSeconds: 0.5);
        var high = renderer.RenderDrumNote(43, 110, ringSeconds: 0.5);

        // Notes 41 and 43 share a tone and differ only by the coarse plane, at half strength.
        var key41 = renderer.Drums.Key(41);
        var key43 = renderer.Drums.Key(43);
        Assert.Equal(key41.Tone, key43.Tone);

        var ratio = DrumKitTable.CoarsePitchRatio(key43.Pitch) / DrumKitTable.CoarsePitchRatio(key41.Pitch);
        Assert.Equal(Math.Pow(2.0, (key43.Pitch - key41.Pitch) / 24.0), ratio, 9);

        Assert.NotEmpty(low.Mono);
        Assert.NotEmpty(high.Mono);
    }

    [SkippableFact]
    public void AVariationBankWithNoEntryFallsBackToTheCapitalTone()
    {
        // A bank select picks a variation. When that variation has no entry for the program, a Sound
        // Canvas does not fall silent -- it sounds the capital tone at bank 0. passport.mid depends on
        // this: its honky-tonk part selects bank 5, whose program-3 slot is empty, and every note would
        // otherwise be dropped. The fallback must land on exactly the capital tone, so an empty
        // variation is indistinguishable from bank 0.
        var renderer = Renderer();
        var capital = renderer.RenderNote(program: 0, note: 60, velocity: 100,
            holdSeconds: 0.1, tailSeconds: 0.1, ToneMap.Sc55, bank: 0);
        var variation = renderer.RenderNote(program: 0, note: 60, velocity: 100,
            holdSeconds: 0.1, tailSeconds: 0.1, ToneMap.Sc55, bank: 120);

        Assert.Contains(variation.Left, s => s != 0f);
        Assert.Equal(capital.Left, variation.Left);
        Assert.Equal(capital.Right, variation.Right);
    }

    [SkippableFact]
    public void AnUnresolvableProgramRendersSilenceRatherThanThrowing()
    {
        // An unmapped tone map resolves to no tone even after the capital-tone fallback, so it exercises
        // the graceful no-voice path the way an unassigned bank once did.
        var voice = Renderer().RenderNote(program: 0, note: 60, velocity: 100,
            holdSeconds: 0.1, tailSeconds: 0.1, (ToneMap)0, bank: 0);

        Assert.All(voice.Left, s => Assert.Equal(0f, s));
        Assert.All(voice.Right, s => Assert.Equal(0f, s));
    }

    private static void AssertMatches(float[] expected, RenderedNote voice, string what)
    {
        var frames = Math.Min(expected.Length / 2, voice.Left.Length);
        Assert.True(frames > 0, $"{what}: nothing rendered.");

        var worst = 0.0;
        var worstIndex = -1;
        double ours = 0, theirs = 0, cross = 0;

        for (var i = 0; i < frames; i++)
        {
            for (var channel = 0; channel < 2; channel++)
            {
                double a = channel == 0 ? voice.Left[i] : voice.Right[i];
                double b = expected[(i * 2) + channel];

                var error = Math.Abs(a - b);
                if (error > worst)
                {
                    worst = error;
                    worstIndex = i;
                }

                ours += a * a;
                theirs += b * b;
                cross += a * b;
            }
        }

        Assert.True(ours > 1e-9, $"{what}: rendered silence, so the comparison proves nothing.");

        var correlation = cross / Math.Sqrt(ours * theirs);
        var levelRatio = Math.Sqrt(ours / theirs);

        Assert.True(correlation >= MinimumCorrelation,
            $"{what}: correlation {correlation:F6} against the reference.");
        Assert.True(Math.Abs(levelRatio - 1.0) < LevelTolerance,
            $"{what}: level ratio {levelRatio:F6} against the reference.");
        Assert.True(worst < MaximumDivergence,
            $"{what}: worst sample divergence {worst:F5} at frame {worstIndex}, which is larger " +
            "than the loop-end difference alone can explain.");
    }

    private static float[] LoadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", name);
        Skip.IfNot(File.Exists(path),
            $"Fixture '{name}' not found. Regenerate with tools/gen_voice_fixtures.py.");

        var bytes = File.ReadAllBytes(path);
        var values = new float[bytes.Length / 4];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4, 4));
        }

        return values;
    }
}
