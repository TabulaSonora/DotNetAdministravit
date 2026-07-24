# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A managed C# reimplementation of the Roland Sound Canvas VA synth voice. It reads the wave ROM and
synth tables out of `SCCore.dll` **as a data file** — the library never loads it as code. MIDI in,
audio out, fully cross-platform.

Every offset is pinned to one build: the `SCCore.dll` shipped in **SOUND Canvas VA 1.1.6**. That file
carries no version resource, so 1.1.6 is provenance recorded in `Rom/manifest.json` and nothing
verifies it — identity is the SHA-256, the PE timestamp and the size.

The goal is fidelity to the hardware, not merely plausible synthesis. Almost every constant here was
recovered by measurement, and changing one on aesthetic grounds is a regression even when it sounds
nicer.

## Commands

```bash
dotnet build -c Release
dotnet test -c Release --no-build

# One test, or one class
dotnet test -c Release --no-build --filter "FullyQualifiedName~VoiceRenderTests"
dotnet test -c Release --no-build --filter "Name=EveryMelodicNoteMatchesTheReferenceSampleForSample"
```

One-time setup — extracts the 48 static tables, reads delay presets, harvests reverb/chorus
coefficients, writes `presets.json`. **Needs Windows x64** because those coefficients are computed by
the engine at start-up and exist nowhere in the file; this is the only place the DLL is executed. The
output is machine-independent, so on macOS/Linux run it once on Windows and copy `presets.json`.

```bash
dotnet run -c Release --project src/TabulaSonora.Tools -- prepare "<path>/SCCore.dll" --tables tables
```

Rendering and playback:

```bash
dotnet run -c Release --project src/TabulaSonora.Tools -- render "<path>/SCCore.dll" song.mid out.wav --map 4
dotnet run -c Release --project src/TabulaSonora.Tools -- render-note "<path>/SCCore.dll" <prog> <note> <vel> <holdSec> out.f32 4
dotnet run -c Release --project src/TabulaSonora.Player -- "<path>/SCCore.dll" song.mid --map 1
```

`--map 1..4` picks the vintage (SC-55/88/88Pro/8820) and changes which tones and ROM regions resolve.
`--stream` renders through the real-time block loop instead of note-by-note. `render-note` is the
fastest way to A/B a single patch against the DLL.

The browser build. **Publish, do not `dotnet run`** — AOT happens at publish, and without it the
engine runs at about 1× realtime and the audio starves; with it, 10.9×. The transport shows the
measured figure, so check that before theorising about dropouts.

```bash
dotnet workload install wasm-tools      # once; AOT needs it
dotnet publish -c Release src/TabulaSonora.Web
# then serve bin/Release/net10.0/publish/wwwroot as static files
```

Fully client-side: no back end, no `HttpClient` registered, and the user's DLL is cached in IndexedDB
and never leaves the machine. `docs/articles/web.md` covers the audio path and why blocks cross to
JavaScript as `byte[]` rather than `float[]`.

Docs (DocFX is pinned as a local tool; never runs the engine, so it works on a clean clone):

```bash
dotnet tool restore && dotnet docfx docs/docfx.json
```

Python is only ever used to regenerate test fixtures, through `uv`:

```bash
uv run --with numpy python tools/gen_fixtures.py
```

## Test data and why tests skip

Nothing Roland-derived is committed. `TestData` resolves assets from environment variables first,
then a candidate list including sibling checkouts, and tests `Skip` cleanly when they are absent — a
clone with no DLL still builds and runs the pure-logic tests.

| variable | what |
|---|---|
| `TABULASONORA_SCCORE` | the pinned `SCCore.dll` — the SOUND Canvas VA **1.1.6** build, exactly 27,347,456 bytes; `RomImage` refuses others |
| `TABULASONORA_TABLES` | extracted `tables/*.bin` |
| `TABULASONORA_TRACES` | golden per-tick captures from the real DLL |

Traces default to the sibling `../DeconstructingTheSauce` checkout, which holds the Python reference
model (`scvx_engine.py`), its reverse-engineering notes (`docs/FINDINGS.md` — read this before
changing any DSP constant), and the `scdec` harness that captures the DLL's own internal state
(`tvftrace`, `ampramp`, `holdnote`, `seq`, `predtrace`).

## Architecture

### Four clock domains

Most of the design falls out of these. `docs/articles/architecture.md` has the diagrams.

- **Event time** — MIDI ingest, sample-accurate.
- **Control tick, 100 Hz / 320 samples** — envelopes, LFOs, coefficient recompute.
- **Audio block, 32 kHz / 32 samples** — samplers, filters, mix, effects.
- **Host rate** — a 2× interpolating conversion at the very end.

Everything upstream of the final conversion is at 32 kHz, which is why `NoteRenderer.SampleRate` is a
constant and not a setting. MIDI events land on the **32-sample block grid, not the control tick** —
tick resolution is ten times too coarse and smears a continuous pitch bend audibly.

### Two renderers, one signal path

`SequenceRenderer` renders each note whole; `Realtime.ToneGenerator` is the engine's own block loop.
They are not two implementations — the envelopes, sampler, filter and tables are **one set of
objects** that both drive, so a single note with no controller movement is identical to float epsilon
through either, which the suite asserts. Preserve that: a fix belongs in the shared object, not in
one path.

The block loop adds what needs a running engine — unknown note length, live controllers, mid-song
effect changes, and the 64-voice limit with stealing.

`SegmentEnvelope` is the shared TVA/TVF envelope. It is a pure function of sample index and note-off
position, advanced by nothing, which is what lets both paths read it.

### Object at the seams, flat underneath

Control-rate work is ordinary C# with real types. Per-sample work is flat: `VoicePool` holds 64
voices as parallel arrays, and `Voice` is a handle (an index plus control-rate operations), not a
container for render state.

### Fixed point

The original's control path is exclusively 16-bit fixed point and several expressions depend on
wrapping or truncation direction. `CheckForOverflowUnderflow` is **off project-wide and must stay
off** — that is load-bearing, not an optimisation. `int` is the universal intermediate, width
truncations are explicit, and three sites widen to `long` where a product exceeds 32 bits (amplitude
curve, part volume, filter exponential decode). A fourth was found by ear, not inspection.

## You cannot hear this engine

The goal is **audible fidelity, not bit accuracy** (README has the reasoning). You can measure the
output; you cannot listen to it. Two consequences, both of which have already cost real time here:

- **A bad-looking number is a lead, not a defect.** Before chasing a residual, check whether it is
  audible at all — widen the smoothing window, compare spectra by band, look at absolute level. One
  passage sits at 0.72 envelope correlation with a spectrum matching the DLL within 0.5 dB; nothing
  is wrong with it.
- **Green metrics do not mean it sounds right.** A filter bug survived a full sweep of passing release
  measurements and a render matching the reference to 0.03%. It was found because a person said it
  still sounded wrong.

So when the work is about how something *sounds*, render an audio file, say where in it to listen and
what to listen for, and ask. `renders/` is gitignored and is the place to put clips. Report what you
measured and what remains unverified rather than implying the two are the same.

## Verification, and what to do when oracles disagree

Two independent oracles: the Python reference (differentially, over whole input domains) and the real
DLL's captured internal state.

**Where the reference and the hardware disagree, follow the hardware.** Those divergences are
deliberate and are recorded in the tests rather than papered over — see the class remarks on
`VoiceRenderTests`, which lists each one and holds the affected window to its own documented bound
while the rest stays exact. Fixture assertions are partitioned, not loosened: the unaffected majority
still demands equality, and divergent rows are counted so a path cannot silently stop applying.

Do not port the fixture generators to C#. They are Python on purpose — they sweep the reference
implementation, and a C# generator would make the engine compare against itself.

When you change DSP behaviour, verify against the DLL directly (trace with `scdec`, or `render-note`
plus a spectrum/envelope comparison) before adjusting any test expectation.

## Conventions the build enforces

`TreatWarningsAsErrors` is on and `GenerateDocumentationFile` means **a missing XML doc comment is a
build error**. Public API needs `<summary>`, `<param>`, `<returns>`. Use `<remarks>` for the
measurement or decompiled behaviour that justifies the code — that is the house style, and comments
here explain *why a constant is what it is*, usually citing the measurement that established it.

Commit style is one focused change per commit, with a body explaining the evidence.
