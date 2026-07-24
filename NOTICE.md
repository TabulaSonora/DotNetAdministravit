# Notice on third-party rights

## What this project licenses

The BSD 3-Clause licence in `LICENSE` covers **this repository's own contents**: the C# source, the
tests, the tooling, the documentation, and `src/TabulaSonora/Rom/manifest.json`. All of it is original
work, written from published reverse-engineering notes. No decompiler output and no transcribed
Roland source is present.

That licence does **not**, and cannot, grant you any right in Roland's software or data.

## What you must supply yourself

This engine is inert without `SCCore.dll` from a Roland SOUND Canvas VA installation — specifically the
build shipped in **SOUND Canvas VA 1.1.6**, which is the only one the table offsets are valid for.
That file — and everything derived from it — remains Roland Corporation's:

- the 24 MB wave ROM embedded in it, which is the literal Sound Canvas hardware mask ROM
- the synth curve, key-follow and patch-directory tables
- the reverb and chorus coefficients read out of the running engine
- any audio decoded or rendered from the above

With two exceptions, stated below, none of that is committed here and none of it is redistributed.
`.gitignore` excludes each category and names the command that regenerates it locally.
`manifest.json` is tracked because it is a map of *where* those tables live, not the tables
themselves — the same distinction the upstream
[TabulaSonora spec](https://github.com/TabulaSonora/spec) draws.

## The first exception: `Effects/presets.json`

`src/TabulaSonora/Effects/presets.json` — about 27 KB of reverb and chorus coefficients, plus the
delay preset table — **is** committed, and it is Roland-derived. That is a deliberate departure from
the rule above, and the reason is that no rule-abiding alternative exists for every host.

Those coefficients are not stored in the DLL. The engine computes them at start-up from the GS macro
parameters, so the only way to obtain them is to run `SCCore.dll` — a 64-bit Windows binary — and read
its state. The browser build in `src/TabulaSonora.Web` cannot do that, and neither can anyone on a
machine that is not Windows x64 without borrowing one. Shipping the file is what lets the engine have
its effects at all on those hosts.

Nothing else changes. The wave ROM, the tables and any rendered audio remain excluded, and the engine
is still inert without a DLL you supply yourself. If you are redistributing this repository and would
rather not carry that file, delete it: the library treats missing presets as a run-time condition with
instructions attached, and the web app accepts one uploaded by the user instead.

## The second exception: the drum kit names

`src/TabulaSonora.Web/Services/DrumKitNames.cs` carries **62 kit names** — "STANDARD 1", "ROOM",
"TR-808" and the rest — transcribed from the kit-name pages of `SCVSC.drf`, one of the tone files
that ships beside `SCCore.dll`. They are names, not data the engine reads: nothing here loads that
file, and the engine's behaviour is identical without them.

They are committed for the same reason instrument and patch name lists ship with sequencers
generally — Cakewalk instrument definitions, Logic and Cubase patch scripts, and the MIDI
Manufacturers Association's own MIDI Name Document format all exist to carry exactly this. A drum
kit called "ROOM" is a label for program 9, published in the owner's manual of every module that has
one.

Only the two pages the engine can actually reach are included: the SC-8820 and SC-88Pro kit lists,
which are what the drum program map's two rows hold. One kit is deliberately left unnamed — the
CM-64/32L set at program 128, which the ROM defines and neither of those lists names.

If you are redistributing this repository and would rather not carry them, replace the two
dictionaries in that file with empty ones: kits then show the program number that selects them, and
nothing else changes.

## Obtaining the DLL

Obtain the DLL from your own licensed installation. Sound Canvas VA was discontinued in September
2024.

## Purpose

This is a preservation and interoperability effort on a discontinued product. It exists so that music
written for the Sound Canvas can still be played, on platforms the original plugin never supported and
after it has stopped being sold.

## Compatibility note

BSD 3-Clause is GPL-compatible, so this code can be incorporated into GPL-licensed projects —
including [Cog](https://github.com/losnoco/Cog) (GPL-2.0) — without a separate grant or exception.
