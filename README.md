# Tabula Sonora .NET

A standalone C# implementation of the Roland Sound Canvas VA synth voice, built to the specification
in [TabulaSonora](https://github.com/TabulaSonora/spec). It reads the wave ROM and synth tables out of
`SCCore.dll` **as a data file** — the DLL is never loaded as code — so the engine is fully managed.

MIDI file in, audio out, at roughly 15× realtime on one core.

## What is and is not in this repository

Everything here is original work: the C# is written from the reverse-engineering notes, not
transcribed from decompiler output.

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

```
dotnet build -c Release

# One-time: build the effect coefficients from your own DLL.
# The reverb/chorus dumps come from the spec repo's scdec harness (scdec revdump / chodump).
dotnet run -c Release --project src/TabulaSonora.Tools -- \
    bake-presets "<path>/SCCore.dll" "<spec-repo>/tables" src/TabulaSonora/Effects/presets.json

# Render.
dotnet run -c Release --project src/TabulaSonora.Tools -- \
    render "<path>/SCCore.dll" song.mid out.wav --map 4
```

`presets.json` is copied next to the assembly at build time. The library also honours
`TABULASONORA_PRESETS`, or a host can call `EffectPresets.Use(...)` directly. Without it, the first
effect render fails with a message saying how to build it.

### Render options

`--map 1..4` selects the vintage — SC-55, SC-88, SC-88Pro, SC-8820. The same program resolves to
different tone numbers and pulls samples from different parts of the ROM.

`--mute 1,2` / `--solo 5,6` take channels the way a mixer labels them, 1–16. `--tail`, `--end`, and
`--no-reverb` / `--no-chorus` / `--no-delay` do what they say; the effects are on by default because
the module always has them.

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
| `src/TabulaSonora` | the library — `Rom`, `Patches`, `Dsp`, `Voices`, `Effects`, `Midi` |
| `src/TabulaSonora.Tools` | CLI: `render`, `bake-presets`, `extract-tables`, `info` |
| `tests/TabulaSonora.Tests` | conformance and differential tests |
| `docs/` | DocFX sources; `docs/_site` and `docs/api` are generated |
| `tools/*.py` | fixture generators — see below |

## On the Python

The build and run path is pure C#. The only remaining Python is the differential fixture generators
in `tools/`, and they stay Python on purpose: they sweep the spec repo's own reference implementation
to produce the expected values the tests assert against. Porting them to C# would make them compare
this engine against itself, which proves nothing.

They are needed only to regenerate fixtures, and they run through `uv`:

```
uv run --with numpy python tools/gen_fixtures.py
```

Tests that need generated data skip cleanly when it is absent, so a fresh clone builds and runs the
pure-logic tests without any of it.

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

## Licence

BSD 3-Clause — see [`LICENSE`](LICENSE). That covers this repository's own code only; see
[`NOTICE.md`](NOTICE.md) for what remains Roland's and must be supplied from your own installation.

BSD 3-Clause is GPL-compatible, so this can be incorporated into GPL projects such as
[Cog](https://github.com/losnoco/Cog) (GPL-2.0) directly.
