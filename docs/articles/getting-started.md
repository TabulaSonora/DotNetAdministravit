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

## Play a file

```
dotnet run -c Release --project src/TabulaSonora.Player -- \
    "<path>/SCCore.dll" song.mid --map 1
```

A terminal transport: progress bar, peak meters, elapsed and total time, and the live underrun
count. Space pauses, the arrow keys seek five seconds, `,` and `.` seek thirty, `Home` returns to the
start, `q` quits.

Playback starts immediately: the song is synthesised through the engine's block loop as it plays, at
around seventy times realtime on one core, so there is nothing to wait for and a long file costs no
more memory than a short one. `--prerender` renders the whole song first instead, which makes seeking
exact and lets the meters look ahead rather than behind.

Audio leaves through [OwnAudioSharp](https://github.com/modernmube/ownaudiosharp). The player takes
every render option above, plus:

| option | meaning |
|---|---|
| `--prerender` | render the whole song before playing instead of streaming it |
| `--list-devices` | enumerate outputs and exit |
| `--device NAME\|N` | pick an output by name fragment or index |
| `--host NAME` | PortAudio host API — `WASAPI` (default on Windows), `MME`, `DirectSound`, `WDMKS`, `ASIO`, `None` |
| `--latency MS` | how far ahead of the device to run; default 150 |
| `--rate HZ`, `--buffer FRAMES` | device rate and block size; default 32000 and 512 |
| `--gain G` | linear gain on the way out |

The default host is WASAPI rather than PortAudio's own Windows fallback, MME, which is too coarse for
smooth playback. The default rate is the engine's own 32 kHz, so nothing resamples on the way to the
device.

If it stutters, raise `--latency`. The send loop is paced from managed code against the device's own
frame counter, and a `Thread.Sleep(1)` on Windows routinely lasts 15 ms, so the lead has to cover the
scheduler's worst nap rather than its average one.

## Driving the engine live

[`ToneGenerator`](xref:TabulaSonora.Realtime.ToneGenerator) is the engine itself: MIDI in, blocks out,
nothing known in advance.

```csharp
using TabulaSonora.Realtime;
using TabulaSonora.Rom;

using var rom = RomImage.Open(dllPath);
var engine = ToneGenerator.Create(rom);

engine.SendChannel(0xC0, 48, 0);     // program change: strings
engine.SendChannel(0x90, 60, 100);   // note on

var left = new float[512];
var right = new float[512];
engine.Render(left, right);          // hold it for as long as you like

engine.SendChannel(0x80, 60, 0);     // note off, whenever
```

Send events between `Render` calls and they land on the block boundary, which is the grid the engine
itself applies them on. Polyphony is the hardware's own 64 voices; past that the allocator steals,
taking whole notes rather than half of one and fading what it takes.

To play a file rather than drive it by hand,
[`SequencePlayer`](xref:TabulaSonora.Realtime.SequencePlayer) dispatches a parsed
[`SmfReader`](xref:TabulaSonora.Midi.SmfReader) event list as it renders, and
[`Seek`](xref:TabulaSonora.Realtime.SequencePlayer.Seek*) replays the file's controllers up to a
position so that jumping into the middle sounds the way playing up to there would.

```csharp
var player = SequencePlayer.FromFile(engine, "song.mid");
player.Seek(60 * ToneGenerator.SampleRate);
player.Render(left, right);
```

> [!NOTE]
> One thing genuinely differs from [`SequenceRenderer`](xref:TabulaSonora.SequenceRenderer). The
> offline path latches a note's program, bank and pan by looking them up at the note's own position,
> which picks up a program change written *after* the note-on at the same tick. A running engine
> cannot: the note-on arrives first and plays whatever program was already selected — which is what
> the module does. Some files, `canyon.mid` among them, put their program changes last at tick 0, so
> the first few notes come out on a different patch here.

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
