using TabulaSonora.Midi;
using TabulaSonora.Realtime;
using TabulaSonora.Rom;
using TabulaSonora.Voices;

namespace TabulaSonora.Tests;

/// <summary>
/// The block-based voice loop: that it agrees with the offline renderer, and that it does the things
/// the offline renderer cannot — hold a note of unknown length, run out of voices, and be seeked.
/// </summary>
public class RealtimeTests
{
    private const int Rate = ToneGenerator.SampleRate;

    [SkippableFact]
    public void StreamingAgreesWithTheOfflineRenderer()
    {
        using var rom = RomImage.Open(TestData.RequireSccore(), RomVerification.Quick);
        var midi = TestData.RequireMidi();

        var notes = new NoteRenderer(rom);
        var offline = new SequenceRenderer(notes).RenderFile(midi);
        var streamed = SequencePlayer
            .FromFile(new ToneGenerator(notes), midi)
            .RenderToEnd();

        Assert.Equal(offline.Left.Length, streamed.Left.Length);

        // The offline path counts every note it is handed, including ones that resolve to no partial
        // at all; this one counts notes that actually start a voice. So it reports fewer, never more.
        Assert.InRange(streamed.NoteCount, (int)(offline.NoteCount * 0.95), offline.NoteCount);

        // The two share their DSP but not their architecture: this path applies controllers at the
        // block boundary rather than per sample, latches a note's parameters strictly in event order,
        // and enforces the engine's 64-voice limit, which the offline path does not. What must hold
        // is that the same music comes out at the same level.
        var correlation = Correlation(offline.Left, streamed.Left);
        Assert.True(correlation > 0.99, $"Left correlation {correlation:F6} is too low.");

        correlation = Correlation(offline.Right, streamed.Right);
        Assert.True(correlation > 0.99, $"Right correlation {correlation:F6} is too low.");

        var ratio = (double)Rms(streamed.Left) / Rms(offline.Left);
        Assert.True(ratio is > 0.97 and < 1.03, $"Level ratio {ratio:F4} is outside the gate.");
    }

    [SkippableFact]
    public void OneNoteMatchesTheOfflineVoiceSampleForSample()
    {
        using var rom = RomImage.Open(TestData.RequireSccore(), RomVerification.Quick);
        var notes = new NoteRenderer(rom);

        const double hold = 1.0;
        const double tail = 1.0;
        var frames = (int)((hold + tail) * Rate);

        var offline = notes.RenderNote(program: 0, note: 60, velocity: 100, hold, tail);

        var generator = new ToneGenerator(notes, new ToneGeneratorOptions
        {
            Reverb = false,
            Chorus = false,
            Delay = false,
        });

        // Everything at full scale so the part's volume law is unity and only the voice remains.
        generator.SendChannel(0xB0, 7, 127);
        generator.SendChannel(0x90, 60, 100);

        var left = new float[frames];
        var right = new float[frames];
        var held = (int)(hold * Rate);

        generator.Render(left.AsSpan(0, held), right.AsSpan(0, held));
        generator.SendChannel(0x80, 60, 0);
        generator.Render(left.AsSpan(held), right.AsSpan(held));

        // With no controller movement and no polyphony pressure the two paths have nothing left to
        // disagree about, and they do not: what separates them is float rounding, not modelling. This
        // is the assertion that the block loop assembles the same voice rather than a similar one.
        var error = 0f;
        for (var i = 0; i < frames; i++)
        {
            error = Math.Max(error, Math.Abs(left[i] - offline.Left[i]));
        }

        Assert.True(error < 1e-6f, $"Worst sample error {error:E3} against the offline voice.");
    }

    [SkippableFact]
    public void TheOutputDoesNotDependOnHowLargeABlockIsAskedFor()
    {
        using var rom = RomImage.Open(TestData.RequireSccore(), RomVerification.Quick);
        var notes = new NoteRenderer(rom);

        // A voice counts its control tick in whole blocks, so a caller asking for a length that is
        // not a multiple of one must not shift the modulation. 1000 is deliberately awkward.
        var whole = Awkward(notes, ToneGenerator.BlockSize * 8);
        var awkward = Awkward(notes, 1000);

        for (var i = 0; i < whole.Length; i++)
        {
            Assert.Equal(whole[i], awkward[i]);
        }

        static float[] Awkward(NoteRenderer notes, int chunk)
        {
            var generator = new ToneGenerator(notes, new ToneGeneratorOptions { Reverb = false, Chorus = false });
            generator.SendChannel(0xC0, 48, 0);
            generator.SendChannel(0x90, 60, 100);

            var left = new float[Rate];
            var right = new float[Rate];

            for (var at = 0; at < left.Length; at += chunk)
            {
                var count = Math.Min(chunk, left.Length - at);
                generator.Render(left.AsSpan(at, count), right.AsSpan(at, count));
            }

            return left;
        }
    }

    [SkippableFact]
    public void ANoteSoundsUntilItIsReleasedHoweverLongThatIs()
    {
        using var rom = RomImage.Open(TestData.RequireSccore(), RomVerification.Quick);
        var generator = ToneGenerator.Create(rom, new ToneGeneratorOptions { Reverb = false, Chorus = false });

        // Program 48, Strings: it sustains, so the level after five seconds is the note still being
        // held rather than a decay that has not finished.
        generator.SendChannel(0xC0, 48, 0);
        generator.SendChannel(0x90, 60, 100);

        var early = Rms(Render(generator, Rate / 2));
        var late = Rms(Render(generator, Rate / 2, skipSeconds: 4.5));
        Assert.True(early > 1e-3, "The note produced no sound.");
        Assert.True(late > early * 0.5, $"A held note decayed from {early:F4} to {late:F4} in five seconds.");

        generator.SendChannel(0x80, 60, 0);
        var released = Rms(Render(generator, Rate * 3));
        Assert.True(released < late * 0.5, $"The release left {released:F4} against {late:F4}.");
        Assert.Equal(0, generator.ActiveVoices);
    }

    [SkippableFact]
    public void PolyphonyIsBoundedAndStealingLeavesNoRemnants()
    {
        using var rom = RomImage.Open(TestData.RequireSccore(), RomVerification.Quick);
        var generator = ToneGenerator.Create(rom);

        // Far more notes than there are voices, spread across parts so the allocator has to steal
        // rather than merely re-strike.
        for (var channel = 0; channel < 8; channel++)
        {
            for (var note = 36; note < 84; note++)
            {
                generator.SendChannel(0x90 | channel, note, 100);
            }
        }

        Render(generator, ToneGenerator.BlockSize);

        Assert.True(generator.Voices.ActiveCount <= VoicePool.MaxVoices,
            $"{generator.Voices.ActiveCount} voices are sounding against a limit of {VoicePool.MaxVoices}.");

        var groups = generator.Voices.Active().Select(v => v.NoteGroup).ToList();
        foreach (var group in groups.Distinct())
        {
            var count = groups.Count(g => g == group);
            Assert.True(count is 1 or 2, $"Note group {group} has {count} voices, which is a remnant.");
        }

        // Stolen voices keep sounding while they fade, so they outnumber the slots for a moment.
        Assert.True(generator.ActiveVoices >= generator.Voices.ActiveCount);
    }

    [SkippableFact]
    public void StealingFadesRatherThanCuts()
    {
        using var rom = RomImage.Open(TestData.RequireSccore(), RomVerification.Quick);
        var generator = ToneGenerator.Create(rom, new ToneGeneratorOptions { Reverb = false, Chorus = false });

        generator.SendChannel(0xC0, 48, 0);
        for (var note = 24; note < 100; note++)
        {
            generator.SendChannel(0x90, note, 100);
        }

        var block = Render(generator, Rate / 4);

        // A hard stop on a stolen voice steps the output to zero, which clicks. The fade is 10 ms, so
        // no adjacent pair of samples may jump by anything like a full-scale step.
        var worst = 0f;
        for (var i = 1; i < block.Length; i++)
        {
            worst = Math.Max(worst, Math.Abs(block[i] - block[i - 1]));
        }

        Assert.True(worst < 0.5f, $"Largest sample-to-sample step {worst:F4} looks like a cut, not a fade.");
    }

    [SkippableFact]
    public void SeekingCarriesTheStateTheFileWouldHaveSet()
    {
        using var rom = RomImage.Open(TestData.RequireSccore(), RomVerification.Quick);

        // A program change well before the seek target, then a note after it. Seeking past the change
        // must still leave the part on that program.
        var events = new List<MidiEvent>
        {
            new(0, MidiEventKind.Channel, 0xC0, 48, 0, null),
            new(Rate, MidiEventKind.Channel, 0xB0, 7, 40, null),
            new(2 * Rate, MidiEventKind.Channel, 0x90, 60, 100, null),
        };

        var generator = ToneGenerator.Create(rom);
        var player = new SequencePlayer(generator, events);

        player.Seek(Rate + (Rate / 2));
        Assert.Equal(48, generator.Parts[0].Program);
        Assert.Equal(40, generator.Parts[0].Volume);
        Assert.Equal(Rate + (Rate / 2), player.Position);

        // And the note that follows the seek point still plays.
        var left = new float[Rate];
        var right = new float[Rate];
        player.Render(left, right);
        Assert.True(Rms(left) > 1e-4, "Nothing sounded after seeking.");
    }

    [SkippableFact]
    public void TheDamperHoldsNotesUntilItLifts()
    {
        using var rom = RomImage.Open(TestData.RequireSccore(), RomVerification.Quick);
        var generator = ToneGenerator.Create(rom, new ToneGeneratorOptions { Reverb = false, Chorus = false });

        generator.SendChannel(0xB0, 64, 127);
        generator.SendChannel(0x90, 60, 100);
        Render(generator, ToneGenerator.BlockSize);

        generator.SendChannel(0x80, 60, 0);
        Render(generator, ToneGenerator.BlockSize);

        // The pedal is down, so the note-off waits.
        Assert.True(generator.ActiveVoices > 0, "The damper did not hold the note.");
        Assert.All(generator.Voices.Active(), v => Assert.Equal(VoiceState.Held, v.State));

        generator.SendChannel(0xB0, 64, 0);
        Render(generator, ToneGenerator.BlockSize);
        Assert.All(generator.Voices.Active(), v => Assert.Equal(VoiceState.Releasing, v.State));
    }

    [SkippableFact]
    public void MutingAChannelSilencesItWhileItKeepsRunning()
    {
        using var rom = RomImage.Open(TestData.RequireSccore(), RomVerification.Quick);

        var channels = new ChannelMask();
        var generator = ToneGenerator.Create(rom, new ToneGeneratorOptions
        {
            Channels = channels,
            Reverb = false,
            Chorus = false,
        });

        generator.SendChannel(0xC0, 48, 0);
        generator.SendChannel(0x90, 60, 100);
        Assert.True(Rms(Render(generator, Rate / 4)) > 1e-3);

        channels.SetMuted(0, true);
        Assert.Equal(0f, Rms(Render(generator, Rate / 4)));

        // The voice is still running underneath, so unmuting is instant rather than waiting for the
        // next note.
        channels.SetMuted(0, false);
        Assert.True(Rms(Render(generator, Rate / 4)) > 1e-3, "Unmuting did not restore the sound.");
    }

    [SkippableFact]
    public void ADrumRingsWithoutANoteOffAndFreesItsVoice()
    {
        using var rom = RomImage.Open(TestData.RequireSccore(), RomVerification.Quick);
        var generator = ToneGenerator.Create(rom, new ToneGeneratorOptions
        {
            Reverb = false,
            Chorus = false,
            DrumRingSeconds = 0.25,
        });

        // Note 36 is the bass drum on every kit. No note-off is ever sent.
        generator.SendChannel(0x99, 36, 110);
        Assert.True(Rms(Render(generator, Rate / 8)) > 1e-3, "The drum did not sound.");

        Render(generator, Rate);
        Assert.Equal(0, generator.ActiveVoices);
    }

    [SkippableFact]
    public void SostenutoHoldsOnlyTheNotesItCaptured()
    {
        using var rom = RomImage.Open(TestData.RequireSccore(), RomVerification.Quick);
        var notes = new NoteRenderer(rom);
        var generator = new ToneGenerator(notes, new ToneGeneratorOptions { Reverb = false, Chorus = false });

        // Program 48, Strings: it sustains, so anything still sounding is being held, not decaying.
        generator.SendChannel(0xC0, 48, 0);
        generator.SendChannel(0x90, 60, 100);
        Render(generator, Rate / 4);
        var captured = generator.ActiveVoices;

        generator.SendChannel(0xB0, 66, 127);   // sostenuto down captures note 60 —
        generator.SendChannel(0x90, 64, 100);   // — but not a note struck after it.
        Render(generator, Rate / 4);

        generator.SendChannel(0x80, 60, 0);
        generator.SendChannel(0x80, 64, 0);
        var held = Rms(Render(generator, Rate * 2, skipSeconds: 1.5));

        // The captured note is still sounding well after both note-offs; the uncaptured one has
        // taken its release and its voices are gone.
        Assert.True(held > 1e-3, "The captured note fell silent under the sostenuto pedal.");
        Assert.Equal(captured, generator.ActiveVoices);

        // Pedal up: the deferred release engages through the standard machinery.
        generator.SendChannel(0xB0, 66, 0);
        Render(generator, Rate * 3);
        Assert.Equal(0, generator.ActiveVoices);
    }

    [SkippableFact]
    public void PortamentoGlidesFromTheLastKeyAndArrivesInTune()
    {
        using var rom = RomImage.Open(TestData.RequireSccore(), RomVerification.Quick);

        // A sustaining lead, so what is measured is the glide and not a decay.
        static ToneGenerator Play(RomImage rom, bool portamento, int time)
        {
            var generator = ToneGenerator.Create(rom, new ToneGeneratorOptions { Reverb = false, Chorus = false });
            generator.SendChannel(0xC0, 80, 0);
            if (portamento)
            {
                generator.SendChannel(0xB0, 5, time);
                generator.SendChannel(0xB0, 65, 127);
                generator.SendChannel(0xB0, 126, 1);   // CC65 only arms when the part is quiet
            }

            generator.SendChannel(0x90, 48, 100);
            Render(generator, Rate / 4);
            generator.SendChannel(0x80, 48, 0);
            generator.SendChannel(0x90, 72, 100);
            return generator;
        }

        // Time 64 crosses an octave in about half a second, so two octaves is still well under way
        // a tenth of a second in, and comfortably over by the end of a second.
        var gliding = Play(rom, portamento: true, time: 64);
        var early = Render(gliding, Rate / 10);
        var settled = Render(gliding, Rate, skipSeconds: 1.0);

        var plain = Play(rom, portamento: false, time: 0);
        var plainEarly = Render(plain, Rate / 10);
        var plainSettled = Render(plain, Rate, skipSeconds: 1.0);

        // Early on the glide is still below the target, so its zero-crossing rate is lower.
        Assert.True(Crossings(early) < Crossings(plainEarly) * 0.9,
            $"Gliding note started at {Crossings(early)} crossings against {Crossings(plainEarly)}.");

        // By the end it has arrived: the two agree closely.
        var ratio = Crossings(settled) / (double)Math.Max(1, Crossings(plainSettled));
        Assert.True(ratio is > 0.95 and < 1.05, $"Glide settled at {ratio:F3} of the target pitch.");
    }

    [SkippableFact]
    public void PortamentoDoesNotGlideOverARingingNote()
    {
        using var rom = RomImage.Open(TestData.RequireSccore(), RomVerification.Quick);
        var generator = ToneGenerator.Create(rom, new ToneGeneratorOptions { Reverb = false, Chorus = false });
        generator.SendChannel(0xC0, 48, 0);
        generator.SendChannel(0xB0, 5, 64);
        generator.SendChannel(0xB0, 65, 127);

        // Poly, with the first note still sounding: the engine does not arm the glide here, and
        // measuring the DLL is what settled it.
        generator.SendChannel(0x90, 48, 100);
        Render(generator, Rate / 4);
        generator.SendChannel(0x90, 72, 100);
        var overlapped = Crossings(Render(generator, Rate / 10));

        var plain = ToneGenerator.Create(rom, new ToneGeneratorOptions { Reverb = false, Chorus = false });
        plain.SendChannel(0xC0, 48, 0);
        plain.SendChannel(0x90, 48, 100);
        Render(plain, Rate / 4);
        plain.SendChannel(0x90, 72, 100);
        var reference = Crossings(Render(plain, Rate / 10));

        Assert.Equal(reference, overlapped);
    }

    [SkippableFact]
    public void PortamentoControlGlidesOneNoteOnly()
    {
        using var rom = RomImage.Open(TestData.RequireSccore(), RomVerification.Quick);
        var generator = ToneGenerator.Create(rom, new ToneGeneratorOptions { Reverb = false, Chorus = false });
        generator.SendChannel(0xC0, 80, 0);
        generator.SendChannel(0xB0, 5, 64);

        // CC#84 names the source key without turning portamento on, and is consumed by one note.
        generator.SendChannel(0xB0, 84, 48);
        generator.SendChannel(0x90, 72, 100);
        var glided = Crossings(Render(generator, Rate / 10));

        generator.SendChannel(0x80, 72, 0);
        generator.SendChannel(0x90, 72, 100);
        var second = Crossings(Render(generator, Rate / 10));

        Assert.True(glided < second * 0.9,
            $"The controlled glide started at {glided} crossings, the next note at {second}.");
    }

    private static int Crossings(float[] samples)
    {
        var count = 0;
        for (var i = 1; i < samples.Length; i++)
        {
            if ((samples[i - 1] < 0f) != (samples[i] < 0f))
            {
                count++;
            }
        }

        return count;
    }

    private static float[] Render(ToneGenerator generator, int frames, double skipSeconds = 0.0)
    {
        if (skipSeconds > 0)
        {
            var skip = (int)(skipSeconds * Rate);
            generator.Render(new float[skip], new float[skip]);
        }

        var left = new float[frames];
        var right = new float[frames];
        generator.Render(left, right);
        return left;
    }

    private static float Rms(float[] samples)
    {
        var sum = 0.0;
        foreach (var sample in samples)
        {
            sum += sample * (double)sample;
        }

        return (float)Math.Sqrt(sum / Math.Max(1, samples.Length));
    }

    private static double Correlation(float[] a, float[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        double sumA = 0, sumB = 0;

        for (var i = 0; i < n; i++)
        {
            sumA += a[i];
            sumB += b[i];
        }

        var meanA = sumA / n;
        var meanB = sumB / n;
        double covariance = 0, varianceA = 0, varianceB = 0;

        for (var i = 0; i < n; i++)
        {
            var da = a[i] - meanA;
            var db = b[i] - meanB;
            covariance += da * db;
            varianceA += da * da;
            varianceB += db * db;
        }

        return covariance / Math.Sqrt((varianceA * varianceB) + 1e-30);
    }
}
