# Architecture

## Four clock domains

Most of the design falls out of the fact that the original engine runs on four different clocks.

| Domain | Rate | What happens there |
|---|---|---|
| Event time | sample-accurate | MIDI ingest, parsing, SysEx |
| Control tick | **100 Hz** (320 samples) | envelopes, LFOs, coefficient recompute |
| Audio block | 32 kHz internal, **32-sample blocks** | samplers, filters, bus mix, effects |
| Host rate | whatever the host asks for | a 2× interpolating rate conversion at the very end |

```mermaid
flowchart LR
    E["Event time<br/><small>sample-accurate</small>"]
    C["Control tick<br/><b>100 Hz</b><br/><small>320 samples</small>"]
    B["Audio block<br/><b>32 kHz</b><br/><small>32 samples</small>"]
    H["Host rate<br/><small>2&times; interpolating SRC</small>"]

    E -->|"events land on the<br/>32-sample grid"| B
    C -->|"envelopes, LFOs,<br/>coefficients"| B
    B --> H

    style C stroke-dasharray: 4 3
```

The engine always renders at 32 kHz, the hardware's own rate. Everything upstream of the final
conversion is in that domain, which is why [`NoteRenderer.SampleRate`](xref:TabulaSonora.NoteRenderer.SampleRate)
is a constant rather than a setting.

Note that MIDI events land on the **32-sample** block grid, not the 100 Hz control tick. Reading a
controller at tick resolution is ten times too coarse and audibly smears a continuous pitch bend —
this was a real defect during development.

## Objects at the seams, flat arrays underneath

The library is object-oriented where dispatch is cheap and data-oriented where it is not.

Control-rate work — patch resolution, envelopes, effect selection — is ordinary C# with real types
and real polymorphism. At 100 Hz, or once per 32-sample block, a virtual call costs nothing
measurable.

Per-sample work is flat. [`VoicePool`](xref:TabulaSonora.Voices.VoicePool) holds its 64 voices as
parallel arrays rather than as objects, which is the shape the original uses too: it renders voices
in SIMD-friendly groups of four. [`Voice`](xref:TabulaSonora.Voices.Voice) is a handle — an index plus
control-rate operations — not a container for render state.

## The signal path

```mermaid
flowchart TD
    MIDI["MIDI file<br/><small>SmfReader, SequenceBuilder</small>"] --> DIR

    subgraph resolve["Patch resolution &mdash; control rate"]
        DIR["PatchDirectory<br/><small>three-level lookup, three tone spaces</small>"]
        TONE["tone &rarr; partial"]
        MS["multisample<br/><small>key and velocity zones</small>"]
        WD["wave descriptor<br/><small>ROM coordinates, root, loop</small>"]
        DIR --> TONE --> MS --> WD
    end

    subgraph voice["Per-voice render &mdash; audio rate"]
        CODEC["WaveCodec<br/><small>block-floating-point DPCM</small>"]
        INTERP["Interpolator<br/><small>4-tap FIR, 128 phases</small>"]
        SVF["StateVariableFilter<br/><small>Chamberlin, four taps</small>"]
        TVA["TvaChain<br/><small>log-domain level chain</small>"]
        PAN["PanLaw<br/><small>exact 128-entry table</small>"]
        CODEC --> INTERP --> SVF --> TVA --> PAN
    end

    subgraph control["Modulation &mdash; 100 Hz control tick"]
        PITCH["PitchChain<br/><small>absolute milli-semitones</small>"]
        LFO["LfoEngine<br/><small>two engines, three destinations</small>"]
        TVF["TvfChain<br/><small>cutoff envelope, f and q</small>"]
    end

    WD --> CODEC
    PITCH -.->|read rate| INTERP
    LFO -.->|pitch| INTERP
    LFO -.->|cutoff| SVF
    LFO -.->|amplitude| TVA
    TVF -.->|coefficients| SVF

    PAN --> BUS["bus accumulator<br/><small>dry, plus three sends</small>"]
    BUS --> FX["Reverb &bull; Chorus &bull; SystemDelay"]
    BUS --> OUT["stereo output"]
    FX --> OUT
```

Solid arrows are the audio path; dotted arrows are control-rate parameter flow.

Partials **sum**. Each is an independent voice dispatched into one accumulation buffer; there is no
divide-by-count anywhere, and averaging would silently halve every two-partial patch.

## Two ways to drive it

The same signal path is driven by two renderers, which differ in *when* they know things rather than
in what they compute.

| | [`SequenceRenderer`](xref:TabulaSonora.SequenceRenderer) | [`ToneGenerator`](xref:TabulaSonora.Realtime.ToneGenerator) |
|---|---|---|
| unit of work | one whole note | one 32-sample block |
| note length | known before rendering | discovered when note-off arrives |
| polyphony | unbounded | 64 voices, stolen when full |
| controllers | latched at note-on, curves per sample | live, re-read every block |
| effect type | one per song | changes when the file says so |
| speed | ~15× realtime | ~75× realtime |

Neither is a reimplementation of the other. The envelopes, the sampler, the filter and the tables are
one set of objects that both drive; the offline path fills arrays from them and the block loop steps
them. That is why a single note with no controller movement comes out **identical to float epsilon**
through either — asserted in the test suite, not asserted by hand.

### The block loop

```mermaid
flowchart TD
    EV["events due this block<br/><small>already on the 32-sample grid</small>"] --> ALLOC
    ALLOC["VoicePool<br/><small>allocate, release, steal</small>"] --> TICK

    subgraph tick["every 10th block &mdash; the 100 Hz control tick"]
        TICK["pitch envelope, both LFOs,<br/>filter coefficients"]
    end

    TICK --> VOICES["for each sounding partial<br/><small>sampler &rarr; filter &rarr; amplitude</small>"]
    VOICES --> MIX["pan, part volume, three send levels"]
    MIX --> DRY["dry L/R"]
    MIX --> SENDS["reverb / chorus / delay buses"]
    SENDS --> FXB["effects"] --> DRY
    DRY --> BLOCK["32 samples out"]
```

A block never straddles a control tick: voices start on the block grid and the tick is ten blocks
long, so the coefficient refresh always lands on a boundary.

Two details are load-bearing. A stolen voice is **faded, not cut** — 4 ms at level and 6 ms down,
which is what the engine does when a drum choke group fires, and a hard stop instead is an audible
click. And the filter's coefficients are taken from the cutoff envelope's **mean over the block they
will serve**, not from its value at the tick: the envelope can cross several segments inside one
10 ms tick, and a single sample point costs about 1.7% of peak on a piano attack.

## Things that are easy to get wrong

These are all asserted in the test suite, because each one is silent when wrong:

- **Looping is decided by whether a sustain region exists**, not by the descriptor's loop flag — that
  flag reads zero for piano, so trusting it makes held notes run out as one-shots.
- **The loop period is inclusive of the data end.** Off by one is inaudible on a long loop and detunes
  a single-cycle one by 27 cents.
- **Drums take a different pitch route.** The note selects the kit entry, not the pitch: the tone
  sounds at key 60 and the kit's coarse plane scales it at *half* strength.
- **Levels are amplitude-squared** throughout, and `g_amp_curve_hi[0]` is 4 rather than 0, so a level
  that decays past the floor must be forced to true silence rather than clamped into the table.
- **The velocity level-scale is split** — one byte for the first two envelope segments, another for
  the rest. Sharing one makes later segments run about 1.45× too fast.

## Fixed-point

The original's control path is exclusively 16-bit fixed point, and several expressions depend on
wrapping or on truncation direction. The port uses `int` as the universal intermediate, makes every
width truncation explicit, and widens to `long` at the three sites where a product exceeds 32 bits —
the amplitude curve, the part volume, and the filter's exponential decode.

A fourth such site was found by ear rather than by inspection: the chorus tap offset reaches 4.3e10
and wrapped silently, putting one channel at the wrong delay.
