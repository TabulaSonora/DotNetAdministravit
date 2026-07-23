# Getting started

## What you need

A legally obtained `SCCore.dll` from a Sound Canvas VA installation. The library pins one exact
build and refuses any other, because a different build moves every table offset:

| field | value |
|---|---|
| size | 27,347,456 bytes |
| SHA-256 | `117E6AA147A96FBDE5E10D2CAF16C89965ACC1E44235FD245992216CC620BDB1` |
| PE timestamp | 2019-10-30 |

## Prepare, once

One command derives everything from the DLL. No other repository is involved.

```
dotnet run -c Release --project src/TabulaSonora.Tools -- \
    prepare "<path>/SCCore.dll" --tables tables
```

It verifies the build, extracts the 48 static tables, reads the delay presets, and harvests the
reverb and chorus coefficients, writing `presets.json`. The build copies that next to the assembly;
the library also honours `TABULASONORA_PRESETS`, or a host can supply presets directly with
[`EffectPresets.Use`](xref:TabulaSonora.Effects.EffectPresets.Use*).

Without it the first effect render fails with a message explaining how to fix it — the build itself
still succeeds.

> [!IMPORTANT]
> `prepare` needs **Windows x64**, for one step only.
>
> The reverb and chorus coefficients are computed by the engine at start-up from the GS macro
> parameters rather than stored in the DLL — neither the tap positions nor the delay lengths behind
> them appear anywhere in the file. Obtaining them means letting the engine compute them and reading
> its state, which requires executing `SCCore.dll`.
>
> This is the only place in the project that loads the DLL as code; the library never does, and
> rendering is fully managed and cross-platform. The result carries no machine state, so a
> `presets.json` made on any Windows host works everywhere — run `prepare` once there and copy the
> 28 KB file. Every other step works on any platform.

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
