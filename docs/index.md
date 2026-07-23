---
_layout: landing
---

# Tabula Sonora .NET

A standalone C# implementation of the Roland Sound Canvas VA synth voice. It reads the wave ROM and
synth tables out of `SCCore.dll` **as a data file** — the DLL is never loaded as code — so the engine
is fully managed and portable.

MIDI file in, audio out, at roughly 15× realtime on one core.

```csharp
using var rom = RomImage.Open(@"C:\Program Files\Roland VS\SOUND Canvas VA\SCCore.dll");
var renderer = SequenceRenderer.Create(rom);

var result = renderer.RenderFile("song.mid", new RenderOptions
{
    Map = ToneMap.Sc55,          // the vintage to resolve programs against
    TailSeconds = 2.2,
});
```

## Where to start

| | |
|---|---|
| [Architecture](articles/architecture.md) | How a note becomes sound, and where the clock domains sit |
| [Getting started](articles/getting-started.md) | Supplying the DLL, building the effect coefficients, first render |
| [Verification](articles/verification.md) | What is proven, how, and against which oracle |
| [API reference](xref:TabulaSonora) | Every public type |

## You must supply the DLL

This engine is inert on its own. Roland's wave ROM, tables and effect coefficients are not
redistributed here, and cannot be — see the repository's `NOTICE.md`. You need a legally obtained
`SCCore.dll` from a Sound Canvas VA installation, pinned to one exact build:

| field | value |
|---|---|
| size | 27,347,456 bytes |
| SHA-256 | `117E6AA1…C620BDB1` |
| PE timestamp | 2019-10-30 |

A different build moves every table offset, so [`RomImage`](xref:TabulaSonora.Rom.RomImage)
refuses to open one.

## Why it exists

Sound Canvas VA was withdrawn from sale in September 2024. This is a preservation and
interoperability effort: music written for the Sound Canvas should keep playing after the software
that played it stops being available, on platforms the original plugin never supported.
