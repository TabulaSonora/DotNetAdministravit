# Getting started

## What you need

A legally obtained `SCCore.dll` from a Sound Canvas VA installation. The library pins one exact
build and refuses any other, because a different build moves every table offset:

| field | value |
|---|---|
| size | 27,347,456 bytes |
| SHA-256 | `117E6AA147A96FBDE5E10D2CAF16C89965ACC1E44235FD245992216CC620BDB1` |
| PE timestamp | 2019-10-30 |

## Build the effect coefficients

The reverb and chorus coefficients are read out of the running engine and are Roland's, so they are
not shipped. Build them once from your own DLL:

```
dotnet run -c Release --project src/TabulaSonora.Tools -- \
    bake-presets "<path>/SCCore.dll" "<spec-repo>/tables" src/TabulaSonora/Effects/presets.json
```

The `tables` directory holds the `scdec revdump` / `chodump` output from the
[spec repository](https://github.com/TabulaSonora/spec). The generated file is copied next to the
assembly at build time; the library also honours `TABULASONORA_PRESETS`, or a host can supply them
directly with [`EffectPresets.Use`](xref:TabulaSonora.Effects.EffectPresets.Use*).

Without it, the first effect render fails with a message explaining how to build it — the build
itself still succeeds.

## Render a file

```
dotnet run -c Release --project src/TabulaSonora.Tools -- \
    render "<path>/SCCore.dll" song.mid out.wav --map 4
```

| option | meaning |
|---|---|
| `--map 1..4` | SC-55, SC-88, SC-88Pro, SC-8820 — the same program resolves to different tones |
| `--mute 1,2` / `--solo 5,6` | channels as a mixer labels them, 1–16 |
| `--tail SEC`, `--end SEC` | release tail, and truncation |
| `--no-reverb`, `--no-chorus`, `--no-delay` | effects are on by default, as the module has them |

## From code

```csharp
using TabulaSonora;
using TabulaSonora.Patches;
using TabulaSonora.Rom;

// The image must stay open: wave data is read on demand, not cached up front.
using var rom = RomImage.Open(dllPath);

var renderer = SequenceRenderer.Create(rom);
var result = renderer.RenderFile("song.mid", new RenderOptions { Map = ToneMap.Sc8820 });

// result.Left / result.Right are float, at result.SampleRate (32 kHz).
```

To mute or solo parts — for a mixer UI, say — hold a
[`ChannelMask`](xref:TabulaSonora.ChannelMask) and mutate it freely. It is safe to toggle from
another thread while a render runs, and the renderer snapshots it once so a mid-render change cannot
make some notes of a part sound and others not.

```csharp
var channels = new ChannelMask();
channels.SetMuted(9, true);                       // drop the drum part
var dry = renderer.Render(sequence, options with { Channels = channels });
```

## Rendering one note

[`NoteRenderer`](xref:TabulaSonora.NoteRenderer) exposes the voice directly, which is useful for
analysis or for driving your own scheduler:

```csharp
var notes = new NoteRenderer(rom);
var voice = notes.RenderNote(program: 73, note: 72, velocity: 100,
                             holdSeconds: 1.0, tailSeconds: 1.8);

Console.WriteLine(voice.Name);   // "Flute"
```
