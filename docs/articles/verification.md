# Verification

The engine is checked against **two independent oracles**, and they are not equal in authority.

**The reference implementation** — the Python in the [spec repository](https://github.com/TabulaSonora/spec) —
is swept over whole input domains to produce expected values. This catches the sign-extension,
truncation-direction and integer-width mistakes that a port invites.

**The real `SCCore.dll`'s own captured internal state** — per-voice gain, filter registers, controller
sweeps, taken with the spec repo's `scdec` harness. Where the two disagree, **the hardware wins**.

## What is proven

| | result |
|---|---|
| static tables | all 48 byte-identical to the extracted cache |
| sample codec | bit-exact against the engine's own predictor |
| patch directory | 470 of 512 programs reproduce the engine's observed zones |
| per-voice gain | within **5.4e-05** of the engine's gain word |
| filter cutoff | **0.10%** mean error over a 2.4 s sweep |
| pitch and LFO | exact against the reference, tick for tick |
| pan law | within **3.0e-05** of a measured controller sweep |
| send effects | all 26 networks matched by impulse response |
| full song | ~1 LSB against the reference over 7.9 million samples |

## The goal is audible fidelity, not bit accuracy

Worth stating before the departures below, because it is what licenses them. Individual layers are
held to bit exactness where the hardware is deterministic — tables, codec, tick streams. The rendered
output is not, and the tests assert correlation, level and spectrum against stated tolerances instead.

Part of the residual is inaudible by construction. In a dense passage the few-millisecond amplitude
envelope is dominated by beating between simultaneous notes, which is chaotically sensitive: onestop's
harpsichord section correlates at 0.72 on a 4 ms envelope, rises to 0.91 as the window widens to
250 ms, and matches the DLL's spectrum within 0.5 dB in every band. Nothing there is wrong. A metric
that looks bad is a lead to investigate, not a defect in itself.

## Where this engine departs from the reference

Four cases, each deliberate, each because the hardware was measured and disagreed.

**The loop's last sample.** The reference stops decoding one sample short of a loop's data end, so its
forward loop substitutes the loop's *first* sample for the last and plays it twice per pass. The
hardware does not.

On long loops this is inaudible. On the SC-55 glockenspiel, whose loops are 60–161 samples, it was
audible as glitchiness — and it also dulled the timbre measurably:

| note | centroid before | after | real DLL |
|---|---|---|---|
| 91 | 6192 Hz | 7333 Hz | 7434 Hz |
| 96 | 7390 Hz | 8857 Hz | 8912 Hz |

This engine follows the hardware. The consequence is that voice-level comparisons against the
reference assert correlation and level rather than sample equality, and the reason is documented in
the test that does it.

**Note-off waits for the control tick.** The reference releases at the note-off sample; the engine
acts on it at its next 100 Hz tick. Measured by sweeping the hold past a boundary — note-off anywhere
in 1000–1008 ms produced the same release, which stepped a whole tick later at 1010 ms, and one
landing exactly on a tick still waited a full one. Releasing immediately runs the tail up to 10 ms
early: inaudible on a pad, most of a short release. Across seven patches this took the release-onset
error from 6.0 ms to 2.3 ms.

**The filter envelope's velocity response.** The reference feeds raw MIDI velocity to the depth
scaler. The engine feeds it through one of sixteen response curves selected by `block[0x2e]`. Row 0
is the identity, so most of the library agrees either way; 13.5% of filtered partials do not. Brass 1
selects row 1, which reads velocity 100 as 71 — on raw velocity its filter sits about a third of an
octave too open for the whole note, measuring +3.5 dB at 4–8 kHz and +6.3 dB above it.

**A note re-struck under the sustain pedal.** A note-off arriving with the damper down is parked
rather than acted on. The reference leaves that parked entry in place when the note is re-struck, so
the pedal's lift releases the strike the player is still holding. onestop.mid's harpsichord passage
rides the pedal every half second over constantly re-struck notes and loses 24 notes to it — all of
them in that passage, none elsewhere in the song, each cut 20–80 ms after sounding.

## Known limits

Stated plainly, because they are not covered by the numbers above:

- **Voice stealing is an approximation.** The original's allocator was located and named during
  reverse engineering but its selection rules were never traced. The policy here — free slot, then
  oldest releasing note, then oldest held note — is an invention, isolated so it can be replaced
  without touching any DSP.
- **The LFO has no hardware trace.** It is verified against the reference, which the spec project
  separately reports as bit-exact against the live engine. That is one link removed from the DLL.
- **Insertion EFX is out of scope**, as it is upstream. The 66-algorithm subsystem is not implemented.
- **Drum tones with the 4-partial layout are not reversed** upstream. General MIDI kits resolve to
  ordinary melodic tones, so the common path works; anything else throws rather than guessing.
- **LFO random waveforms** need the engine's own RNG state and return zero, as in the reference.

## Methodology worth borrowing

Two habits earned their place during development:

**Guard against passing vacuously.** Twelve of the twenty-six effect comparisons were once green
while testing nothing at all — the fixture windows were shorter than the delays, so both sides were
silent and agreed perfectly. A "produced no output" assertion caught it.

**A narrow test set hides broad defects.** One MIDI file exercised no mod wheel and fifteen pitch
bends; three others used hundreds of each and immediately exposed two real bugs. The spec project
records the same lesson three separate times.
