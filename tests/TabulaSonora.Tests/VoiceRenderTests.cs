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
/// <para>
/// The third is the filter's velocity response. The reference feeds raw MIDI velocity to the filter
/// envelope's depth scaler; the engine feeds it through the curve <c>block[0x2e]</c> selects (see
/// <see cref="Dsp.TvfChain.EffectiveVelocity"/>). Of the melodic fixtures only Piano 1 at note 60
/// selects a non-identity curve, and it is held to its own correlation floor below. Brass 1, which is
/// not a fixture, is the patch that showed why this matters: on raw velocity its filter sat about a
/// third of an octave too open, measuring +3.5 dB at 4–8 kHz and +6.3 dB above it against the DLL.
/// </para>
/// <para>
/// The second deliberate divergence is the release. The reference starts it at the note-off sample;
/// the engine only acts on note-off at its next control tick, so this port holds for the rest of that
/// tick first (see <see cref="Dsp.SegmentEnvelope.NoteOff"/>). That is worth up to 10 ms, which is
/// nothing on a pad and most of the tail on a short release. The comparison is therefore split at
/// note-off: everything up to it must still match the reference sample for sample, and the release is
/// held to its own tolerances. Measured against the DLL, the deferral takes the release-onset error
/// from about 6 ms to about 2 ms and improves the tail correlation on every patch tried.
/// </para>
/// <para>
/// The fourth is the release <em>rate</em>. The engine reads two different key-follow tables —
/// <c>g_kf_tvarate0</c> for the four main segments and <c>g_kf_tvarate1</c> for the release, indexed
/// by <c>block[0x65]</c> and <c>block[0x66]</c> respectively in <c>tva_compute_env_rates</c>. The
/// reference uses the first for both. Settled against the DLL rather than by reading, timing the decay
/// from note-off on Piano 1 at velocity 100 — milliseconds to each level below the note-off level:
/// </para>
/// <list type="table">
/// <item><description>note 60 — DLL 65/120/200/290/385, this port 65/125/200/295/375,
/// <c>g_kf_tvarate0</c> 80/145/245/355/455 at −6/−12/−20/−30/−40 dB.</description></item>
/// <item><description>note 36 — DLL 55/135/255/395/520, this port 20/125/220/370/510,
/// <c>g_kf_tvarate0</c> 65/190/340/540/695.</description></item>
/// </list>
/// <para>
/// So the release table is 23 ms out on average against the first table's 94 ms, and the old reading
/// ran 175 ms long on the low note. The tables disagree in 67% of their entries, so this moves most
/// releases; only these two fixtures cross the ordinary floors. The exemption is derived from the
/// tables rather than named, so it disappears if the divergence ever does.
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

    /// <summary>
    /// Correlation floor across the release, where the reference is up to a control tick ahead.
    /// </summary>
    /// <remarks>The worst of the six melodic fixtures is the Flute at 0.9931, whose whole release is
    /// only a few ticks long; the rest sit above 0.997.</remarks>
    private const double ReleaseCorrelation = 0.99;

    /// <summary>
    /// How far the release's level may run above the reference's.
    /// </summary>
    /// <remarks>
    /// One-sided by construction: holding for the rest of the tick can only leave this port with more
    /// energy in the release than the reference, never less. The Flute is again the extreme at 1.31.
    /// </remarks>
    private const double ReleaseLevelExcess = 0.4;

    /// <summary>
    /// Correlation floor for a fixture whose filter takes a non-identity velocity response curve,
    /// which the reference does not apply.
    /// </summary>
    /// <remarks>
    /// Only Piano 1 at note 60 qualifies, at 0.9885. The divergence is spectral — the filter sits
    /// somewhere else for the whole note — which also moves the overall level a little, by 0.27% here,
    /// so <see cref="VelocityCurveLevelTolerance"/> applies alongside it.
    /// </remarks>
    private const double VelocityCurveCorrelation = 0.98;

    /// <summary>Level tolerance for those same notes, where a moved filter shifts the level slightly.</summary>
    private const double VelocityCurveLevelTolerance = 0.01;

    /// <summary>
    /// Correlation floor across the release for a partial whose two rate key-follow tables disagree,
    /// where this port follows the engine and the reference does not.
    /// </summary>
    /// <remarks>
    /// Only Piano 1 at note 60 qualifies among the melodic fixtures, at 0.9888. Measured against the
    /// DLL, which is what settles it: releasing prog 0 note 60 velocity 100 and timing the decay from
    /// note-off, the engine reaches −40 dB at 385 ms and this port at 375 ms, against 455 ms when the
    /// release reads <c>g_kf_tvarate0</c> as the reference does.
    /// </remarks>
    private const double DivergentReleaseRateCorrelation = 0.98;

    /// <summary>
    /// How far below the reference's the release level may sit when the rate tables disagree.
    /// </summary>
    /// <remarks>
    /// The engine's release is the faster of the two, so this window loses energy against the
    /// reference rather than gaining it — the opposite sign to <see cref="ReleaseLevelExcess"/>, and
    /// the reason that bound cannot stay one-sided. Piano 1 sits at 0.9891 at note 60 and 0.9003 at
    /// note 36, the low note diverging much further because the two tables are furthest apart there
    /// (81 against 12 at row 1). Both were checked against the DLL before this bound was widened —
    /// see the class remarks for the timings.
    /// </remarks>
    private const double DivergentReleaseLevelDeficit = 0.15;

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
        var divergentReleases = 0;

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

            var curved = UsesVelocityCurve(renderer, program, note, velocity);
            var divergentRelease = UsesDivergentReleaseRate(renderer, program, note, velocity);
            AssertMatches(expected, voice, $"program {program} note {note}",
                noteOffFrame: (int)(hold * NoteRenderer.SampleRate),
                heldCorrelation: curved ? VelocityCurveCorrelation : MinimumCorrelation,
                heldLevelTolerance: curved ? VelocityCurveLevelTolerance : LevelTolerance,
                releaseCorrelation: divergentRelease
                    ? DivergentReleaseRateCorrelation
                    : ReleaseCorrelation,
                releaseLevelDeficit: divergentRelease
                    ? DivergentReleaseLevelDeficit
                    : LevelTolerance);

            checkedNotes++;
            if (divergentRelease)
            {
                divergentReleases++;
            }
        }

        Assert.True(checkedNotes >= 6, $"Only {checkedNotes} melodic notes checked.");

        // Counted, not merely tolerated: if the release stopped reading g_kf_tvarate1 this would fall
        // to zero and the relaxed floor would be hiding a regression rather than recording a finding.
        Assert.True(divergentReleases >= 1,
            "No fixture exercised the release rate table the reference does not read.");
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

    /// <summary>Correlation, level ratio and worst single-sample error over a frame range.</summary>
    private readonly record struct Comparison(double Correlation, double LevelRatio, double Worst, int WorstFrame);

    /// <summary>
    /// Compares a rendered note against the reference, splitting the comparison at note-off.
    /// </summary>
    /// <param name="expected">The reference render, interleaved.</param>
    /// <param name="voice">What this port produced.</param>
    /// <param name="what">Label for the failure message.</param>
    /// <param name="noteOffFrame">
    /// Frame the note was released at, or −1 for a render with no note-off of its own — a drum, whose
    /// ring the renderer ends on its own terms.
    /// </param>
    /// <summary>
    /// Whether any partial this note sounds takes a filter velocity curve the reference does not model.
    /// </summary>
    /// <param name="renderer">The shared renderer.</param>
    /// <param name="program">MIDI program.</param>
    /// <param name="note">MIDI note.</param>
    /// <param name="velocity">MIDI velocity.</param>
    /// <returns>Whether the reference is expected to diverge on this note.</returns>
    /// <summary>
    /// Whether a note's release rate comes out of the table the reference does not read.
    /// </summary>
    /// <param name="renderer">The shared renderer.</param>
    /// <param name="program">Program number.</param>
    /// <param name="note">MIDI note.</param>
    /// <param name="velocity">MIDI velocity.</param>
    /// <returns>True when the two rate key-follow tables disagree for a sounding partial.</returns>
    /// <remarks>
    /// Derived rather than listed, so the exemption cannot outlive the divergence: if the tables ever
    /// agreed for this partial, the fixture would go straight back to demanding
    /// <see cref="ReleaseCorrelation"/>.
    /// </remarks>
    private static bool UsesDivergentReleaseRate(NoteRenderer renderer, int program, int note, int velocity)
    {
        var directory = renderer.Directory;
        var tables = renderer.Tables;

        foreach (var tone in directory.ProgramTones(program, ToneMap.Sc8820, bank: 0))
        {
            foreach (var sounding in directory.Resolve(tone, note, velocity).Partials)
            {
                var partial = directory.GetPartialBySlot(tone, sounding.PartialIndex);
                var row = partial.Raw[0x66] * 0x80;
                var index = row + (note & 0x7F);

                if (tables.KfTvaRate0[index] != tables.KfTvaRate1[index])
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool UsesVelocityCurve(NoteRenderer renderer, int program, int note, int velocity)
    {
        var directory = renderer.Directory;
        foreach (var tone in directory.ProgramTones(program, ToneMap.Sc8820, bank: 0))
        {
            foreach (var sounding in directory.Resolve(tone, note, velocity).Partials)
            {
                var partial = directory.GetPartialBySlot(tone, sounding.PartialIndex);

                // A neutral 0x4a bypasses the scaler, so the curve cannot reach the output there.
                if (partial.Raw[0x4A] != 0x40
                    && renderer.Tvf.EffectiveVelocity(partial, velocity) != Math.Clamp(velocity, 0, 0x7F))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void AssertMatches(
        float[] expected, RenderedNote voice, string what, int noteOffFrame = -1,
        double heldCorrelation = MinimumCorrelation,
        double heldLevelTolerance = LevelTolerance,
        double releaseCorrelation = ReleaseCorrelation,
        double releaseLevelDeficit = LevelTolerance)
    {
        var frames = Math.Min(expected.Length / 2, voice.Left.Length);
        Assert.True(frames > 0, $"{what}: nothing rendered.");

        Comparison Compare(int from, int to)
        {
            var worst = 0.0;
            var worstFrame = -1;
            double ours = 0, theirs = 0, cross = 0;

            for (var i = from; i < to; i++)
            {
                for (var channel = 0; channel < 2; channel++)
                {
                    double a = channel == 0 ? voice.Left[i] : voice.Right[i];
                    double b = expected[(i * 2) + channel];

                    var error = Math.Abs(a - b);
                    if (error > worst)
                    {
                        worst = error;
                        worstFrame = i;
                    }

                    ours += a * a;
                    theirs += b * b;
                    cross += a * b;
                }
            }

            Assert.True(ours > 1e-9, $"{what}: rendered silence, so the comparison proves nothing.");
            return new Comparison(cross / Math.Sqrt(ours * theirs), Math.Sqrt(ours / theirs), worst, worstFrame);
        }

        // Held: still exact, bar the loop-end difference. Everything the release change touches is
        // after note-off, so a regression here means something else moved.
        var held = noteOffFrame is >= 0 && noteOffFrame < frames ? noteOffFrame : frames;
        var sustain = Compare(0, held);

        Assert.True(sustain.Correlation >= heldCorrelation,
            $"{what}: correlation {sustain.Correlation:F6} against the reference before note-off.");
        Assert.True(Math.Abs(sustain.LevelRatio - 1.0) < heldLevelTolerance,
            $"{what}: level ratio {sustain.LevelRatio:F6} against the reference before note-off.");
        Assert.True(sustain.Worst < MaximumDivergence,
            $"{what}: worst sample divergence {sustain.Worst:F5} at frame {sustain.WorstFrame}, which " +
            "is larger than the loop-end difference alone can explain.");

        if (held >= frames)
        {
            return;
        }

        // Release: the reference starts it up to a control tick early, so this window is held to its
        // own tolerances. The excess is one-sided -- more energy here than the reference, never less.
        var release = Compare(held, frames);

        Assert.True(release.Correlation >= releaseCorrelation,
            $"{what}: correlation {release.Correlation:F6} across the release.");
        // Ordinarily one-sided: deferring note-off can only add energy. A release whose rate comes out
        // of the other key-follow table runs faster than the reference's, which takes energy away, so
        // that case carries its own deficit bound instead.
        Assert.True(release.LevelRatio > 1.0 - releaseLevelDeficit,
            $"{what}: level ratio {release.LevelRatio:F6} across the release is further below the " +
            "reference's than the release rate can explain.");
        Assert.True(release.LevelRatio - 1.0 < ReleaseLevelExcess,
            $"{what}: level ratio {release.LevelRatio:F6} across the release is further above the " +
            "reference than one control tick of extra hold can explain.");
        Assert.True(release.Worst < MaximumDivergence,
            $"{what}: worst sample divergence {release.Worst:F5} at frame {release.WorstFrame} in the release.");
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
