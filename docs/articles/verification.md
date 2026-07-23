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

## Where this engine departs from the reference

One case, deliberately. The reference stops decoding one sample short of a loop's data end, so its
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
