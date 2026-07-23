# Tabula Sonora .NET

A standalone C# implementation of the Roland Sound Canvas VA synth voice, built to the specification
in [TabulaSonora](https://github.com/TabulaSonora/spec). It reads the wave ROM and synth tables out of
`SCCore.dll` **as a data file** — the DLL is never loaded as code — so the engine is fully managed.

MIDI file in, audio out, at roughly 15× realtime on one core.

## What is and is not in this repository

Everything here is original work: the C# is written from the reverse-engineering notes, not
transcribed from decompiler output. It was written by an AI, and the target is audible fidelity
rather than bit accuracy — both are worth knowing before you rely on it, and both are set out in
[what "faithful" means here](#what-faithful-means-here-and-who-wrote-it).

**Nothing Roland-derived is committed or shipped.** The library assembly embeds exactly one data
file, `Rom/manifest.json` — the offset *map*, not the data, which the upstream spec repo tracks for
the same reason. The effect coefficients, the extracted tables, the decoded audio and the test
fixtures are all generated locally from your own DLL and are gitignored.

You need a legally obtained `SCCore.dll` from a Sound Canvas VA installation. The build pins one
exact version:

| field | value |
|---|---|
| size | 27,347,456 bytes |
| SHA-256 | `117E6AA147A96FBDE5E10D2CAF16C89965ACC1E44235FD245992216CC620BDB1` |
| PE timestamp | 2019-10-30 |

A different build moves every table offset, so `RomImage` refuses to open one.

## Getting it running

Two commands. Nothing else is needed — no other repository, no Python.

```
dotnet build -c Release

# One-time setup: everything the engine needs, from the DLL alone.
dotnet run -c Release --project src/TabulaSonora.Tools -- \
    prepare "<path>/SCCore.dll" --tables tables

# Render.
dotnet run -c Release --project src/TabulaSonora.Tools -- \
    render "<path>/SCCore.dll" song.mid out.wav --map 4
```

`prepare` verifies the build, extracts the 48 static tables, reads the delay presets, and harvests
the reverb and chorus coefficients. It writes `presets.json`, which the build copies next to the
assembly. The library also honours `TABULASONORA_PRESETS`, or a host can call
`EffectPresets.Use(...)`. Without it the first effect render fails with a message saying how to fix it.

### One platform caveat, stated plainly

`prepare` needs **Windows x64** — but only for the reverb and chorus coefficients, and only once.

Those coefficients are *computed by the engine at start-up* from the GS macro parameters; they are
not stored in the DLL. Searching the file finds neither the tap positions nor the delay lengths they
derive from. Short of re-deriving Roland's coefficient maths, the only way to obtain them is to let
the engine compute them and read its state — which means executing `SCCore.dll`, a 64-bit Windows
binary.

This is the **only** place in the project that loads the DLL as code. The library never does, and
rendering is fully managed and cross-platform.

The output does not depend on the machine: a `presets.json` produced on any Windows host is valid
everywhere. On macOS or Linux, run `prepare` once on Windows and copy that one 28 KB file across.
Everything else `prepare` does works on any platform.

### Playing

```
dotnet run -c Release --project src/TabulaSonora.Player -- \
    "<path>/SCCore.dll" song.mid --map 1
```

A terminal player with a progress bar, peak meters, pause, and seeking. `--list-devices` enumerates
outputs; `--device` picks one by name or index.

Playback starts immediately: the song is synthesised through the block loop as it plays, at about
seventy times realtime on one core. `--prerender` renders it in full first instead, which makes
seeking exact and lets the meters look ahead rather than behind.

Audio goes out through [OwnAudioSharp](https://github.com/modernmube/ownaudiosharp) at **32 kHz** —
the engine's own rate, so nothing resamples on the way to the device. On macOS the host is always
**CoreAudio**, the one host the platform has. On Windows it is **WASAPI**: PortAudio's fallback there
is MME, which is far too coarse for smooth playback, hence the explicit default; override it with
`--host`.

If it stutters, raise `--latency` (default 150 ms). The send loop is paced from managed code against
the device's own frame counter, and a `Thread.Sleep(1)` on Windows routinely lasts 15 ms, so the lead
has to cover the scheduler's worst nap rather than its average one.

### Two renderers, one signal path

`SequenceRenderer` renders each note whole and sums them; `Realtime.ToneGenerator` is the engine's own
block loop — MIDI in, 32 samples out, nothing known in advance. They are not two implementations. The
envelopes, sampler, filter and tables are one set of objects that both drive, so a single note with no
controller movement comes out **identical to float epsilon** through either, which the test suite
asserts.

What the block loop adds is everything that needs a running engine: a note of unknown length, live
controllers, GS effect types that change mid-song, and the hardware's own 64-voice limit with stealing
— which the offline path, having no notion of *now*, cannot express. `render --stream` renders a file
through it for comparison.

```csharp
var engine = ToneGenerator.Create(rom);
engine.SendChannel(0x90, 60, 100);       // note on
engine.Render(left, right);              // hold it for as long as you like
engine.SendChannel(0x80, 60, 0);         // note off, whenever
```

### Render options

`--map 1..4` selects the vintage — SC-55, SC-88, SC-88Pro, SC-8820. The same program resolves to
different tone numbers and pulls samples from different parts of the ROM.

`--mute 1,2` / `--solo 5,6` take channels the way a mixer labels them, 1–16. `--tail`, `--end`, and
`--no-reverb` / `--no-chorus` / `--no-delay` do what they say; the effects are on by default because
the module always has them. `--stream` renders through the real-time block loop instead of note by
note — about five times faster, and bounded to 64 voices.

## Documentation

The API reference and articles are built with [DocFX](https://dotnet.github.io/docfx/), pinned as a
local tool so the output is reproducible:

```
dotnet tool restore
dotnet docfx docs/docfx.json          # writes docs/_site
dotnet docfx docs/docfx.json --serve  # and previews it at http://localhost:8080
```

It reads the XML doc comments straight from the library, which are complete — the build treats a
missing one as an error. Diagrams are Mermaid, rendered by the template.

No Roland data is involved: documentation generation never runs the engine, so it works on a clean
clone with no DLL present. Pushing to `main` publishes the site via GitHub Actions.

## Layout

| path | what |
|---|---|
| `src/TabulaSonora` | the library — `Rom`, `Patches`, `Dsp`, `Voices`, `Effects`, `Midi`, `Realtime` |
| `src/TabulaSonora.Tools` | CLI: `prepare`, `render`, `extract-tables`, `info` |
| `src/TabulaSonora.Player` | terminal MIDI player, audio out via OwnAudioSharp |
| `tests/TabulaSonora.Tests` | conformance and differential tests |
| `docs/` | DocFX sources; `docs/_site` and `docs/api` are generated |
| `tools/*.py` | fixture generators — see below |

## What this repository does *not* need

Building, preparing, rendering and the conformance tests need nothing but this repository, .NET, and
your own `SCCore.dll`. There is no dependency on the spec repository and none on Python.

Two things are optional, and both are for *re-deriving* test expectations rather than for using the
engine. Tests that need them skip cleanly when they are absent:

- **The differential fixtures.** The generators in `tools/` are Python on purpose: they sweep the
  [spec repository's](https://github.com/TabulaSonora/spec) reference implementation to produce the
  values the tests assert against. Porting them to C# would make the engine compare against itself,
  which proves nothing. Run through `uv`:

  ```
  uv run --with numpy python tools/gen_fixtures.py
  ```

- **The golden engine traces** — controller sweeps and per-tick voice-state captures taken from the
  real DLL with the spec repo's `scdec` harness. A handful of conformance tests use them.

## Verification

The engine is checked against two independent oracles: the Python reference (differentially, over
whole input domains) and the real `SCCore.dll`'s own captured internal state.

Highlights, all asserted in the test suite or measured directly:

- every static table extracted from the DLL is byte-identical to the reference cache
- the sample codec is bit-exact against the engine's own predictor
- per-voice gain reproduced from static tables to within 5.4e-05 of the engine's gain word
- the runtime filter cutoff tracks the engine to 0.10% mean over a 2.4 s sweep
- the pan law reproduces a measured controller sweep to 3.0e-05
- all 26 send-effect networks match by impulse response
- a full song render matches the reference to ~1 LSB

Where the reference and the hardware disagree, this engine follows the hardware. One case is
documented in the tests: the reference stops decoding one sample short of a loop's end, which dulls
short single-cycle loops audibly.

## What "faithful" means here, and who wrote it

**The target is audible fidelity, not bit accuracy.** Some layers are bit-exact and are held to it —
the static tables, the sample codec, the pitch and LFO tick streams. The rendered output is not, and
is not trying to be. Three reasons, all of them deliberate:

- **Where the reference implementation and the hardware disagree, this engine follows the hardware.**
  There are several such places, each documented in the test that tolerates it. Matching the
  reference there would mean sounding *less* like a Sound Canvas.
- **Some of the remaining difference is inaudible and chasing it is wasted effort.** The engine is
  float DSP; in a dense passage the few-millisecond amplitude structure is dominated by beating
  between simultaneous notes, which is chaotically sensitive to differences far below hearing. One
  passage here correlates at 0.72 on a 4 ms envelope while its spectrum matches the DLL within 0.5 dB
  in every band.
- **A few behaviours were never traced** — voice stealing most of all. See the known limits in
  [the verification article](docs/articles/verification.md).

So the tests assert correlation, level and spectrum with stated tolerances, and the tolerances have
reasons written next to them. A number that looks bad is a lead, not a verdict.

### The code was written by an AI, and the AI has no ears

The C# here was written by Claude, from the reverse-engineering notes and from measurements taken
against the DLL. That is worth knowing for a specific reason: **an AI can measure this engine but
cannot hear it.** Every statement in this README about how something *sounds* came from a human
listening to it.

This is not a formality. Two of the bugs fixed here were found precisely at that boundary:

- A filter defect survived a full sweep of green release metrics and a render matching the reference
  to 0.03%. What surfaced it was a person saying *it still sounds wrong* — and the trail led to the
  filter envelope reading raw MIDI velocity instead of the patch's own response curve, leaving it
  about a third of an octave too open on 13.5% of the library.
- In the other direction, a 0.72 correlation looked like an open defect long after the audible fault
  in that passage — notes cut off by the sustain pedal — had been fixed. A person listening said it
  was fine, and the measurement agreed once asked the right question.

Measurement finds what it is pointed at. Deciding where to point it, and knowing when a residual
stops mattering, has needed a human in every case so far.

## Licence

BSD 3-Clause — see [`LICENSE`](LICENSE). That covers this repository's own code only; see
[`NOTICE.md`](NOTICE.md) for what remains Roland's and must be supplied from your own installation.

BSD 3-Clause is GPL-compatible, so this can be incorporated into GPL projects such as
[Cog](https://github.com/losnoco/Cog) (GPL-2.0) directly.
