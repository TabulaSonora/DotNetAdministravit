using TabulaSonora;
using TabulaSonora.Dsp;
using TabulaSonora.Patches;
using TabulaSonora.Rom;

namespace TabulaSonora.Tests;

/// <summary>
/// Waves the engine plays backwards.
/// </summary>
/// <remarks>
/// <para>
/// These were skipped outright until they were measured — both renderers dropped the partial, so a
/// reverse cymbal or a reverse kick was silence rather than a wrong sound, which is the failure mode
/// that hides. 218 descriptors carry the flag and the drum kits reach 167 of them.
/// </para>
/// <para>
/// The tests assert the <em>shape</em> of the result rather than a sample-by-sample match, and the
/// shape is the point: a reverse sound swells, so its energy is back-loaded. That is what tells a
/// correct implementation from one playing the data forwards, and it is the thing a spectrum cannot
/// see — the magnitude spectrum of a hit is very nearly the same as that of its time-reverse.
/// </para>
/// </remarks>
public class ReverseWaveTests
{
    /// <summary>Program 57's kit, key 38: <c>Rev.PowerK1</c>, wave 4101.</summary>
    private const int ReverseKitProgram = 57;

    private const int ReverseNote = 38;

    /// <summary>Program 53's kit, key 91: <c>Hand Clap</c>, an ordinary forward one-shot.</summary>
    private const int ForwardKitProgram = 53;

    private const int ForwardNote = 91;

    private static NoteRenderer Load() => new(RomImage.Open(TestData.RequireSccore()));

    /// <summary>Where a signal's energy sits, as a fraction of its sounding length.</summary>
    private static double PeakPosition(float[] mono)
    {
        const int Window = 320;
        var frames = mono.Length / Window;
        var envelope = new double[frames];

        for (var f = 0; f < frames; f++)
        {
            var sum = 0.0;
            for (var i = f * Window; i < (f + 1) * Window; i++)
            {
                sum += mono[i] * (double)mono[i];
            }

            envelope[f] = Math.Sqrt(sum / Window);
        }

        var last = 0;
        var peak = 0;
        for (var f = 0; f < frames; f++)
        {
            if (envelope[f] > 1e-4)
            {
                last = f;
            }

            if (envelope[f] > envelope[peak])
            {
                peak = f;
            }
        }

        return last == 0 ? 0 : peak / (double)last;
    }

    private static float Peak(float[] mono)
    {
        var peak = 0f;
        foreach (var sample in mono)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        return peak;
    }

    [SkippableFact]
    public void AReverseWaveDecodesTurnedRound()
    {
        var renderer = Load();
        var key = renderer.Drums.Key(ReverseNote, renderer.Drums.KitForProgram(ReverseKitProgram)!.Value);
        var tone = renderer.Directory.GetTone(key.Tone)!;
        var partial = tone.Partials[0];
        var wave = renderer.Directory.MultisampleWave(partial.Multisample, partial.TransposedKey(60))!.Value;
        var descriptor = renderer.Directory.GetWave(wave)!.Value;

        Assert.True(descriptor.Reverse);

        var decoded = renderer.Sampler.Decode(descriptor)!;
        Assert.True(decoded.Reversed);

        // Always a one-shot. The engine reconfigures the registers to run downward once, which is why
        // the loop geometry the static descriptor carries does not decide this.
        Assert.Equal(SamplerMode.OneShot, decoded.Mode);
        Assert.False(decoded.IsLooping);
    }

    [SkippableFact]
    public void AReverseDrumSoundsAndSwells()
    {
        var renderer = Load();
        var kit = renderer.Drums.KitForProgram(ReverseKitProgram)!.Value;
        var voice = renderer.RenderDrumNote(ReverseNote, velocity: 100, ringSeconds: 1.6, kit: kit);

        // It used to be exactly zero, at any length, because both renderers skipped the partial.
        Assert.True(Peak(voice.Mono) > 0.01f, $"peak was {Peak(voice.Mono)}");

        // And it arrives at the end, which is what makes it a reverse sound rather than the same data
        // played the usual way round. Against the DLL's own capture this reads 97% through in both.
        Assert.True(PeakPosition(voice.Mono) > 0.8, $"peak sat {PeakPosition(voice.Mono):P0} through");
    }

    [SkippableFact]
    public void AForwardDrumStillDecaysFromItsAttack()
    {
        var renderer = Load();
        var kit = renderer.Drums.KitForProgram(ForwardKitProgram)!.Value;
        var voice = renderer.RenderDrumNote(ForwardNote, velocity: 100, ringSeconds: 1.6, kit: kit);

        Assert.True(Peak(voice.Mono) > 0.01f);

        // The control. Without it the swell assertion above would pass on a renderer that had simply
        // turned every wave round.
        Assert.True(PeakPosition(voice.Mono) < 0.2, $"peak sat {PeakPosition(voice.Mono):P0} through");
    }

    [SkippableFact]
    public void TheDrumKitsReachManyOfThem()
    {
        var renderer = Load();

        var reverse = 0;
        for (var wave = 0; wave < renderer.Directory.WaveCount; wave++)
        {
            if (renderer.Directory.GetWave(wave)!.Value.Reverse)
            {
                reverse++;
            }
        }

        // Pinned because the count moved once already: widening the short wave-descriptor slice from
        // 4096 records to 4259 is what made most of these reachable in the first place.
        Assert.Equal(218, reverse);
    }
}
