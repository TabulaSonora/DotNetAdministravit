# Notice on third-party rights

## What this project licenses

The BSD 3-Clause licence in `LICENSE` covers **this repository's own contents**: the C# source, the
tests, the tooling, the documentation, and `src/TabulaSonora/Rom/manifest.json`. All of it is original
work, written from published reverse-engineering notes. No decompiler output and no transcribed
Roland source is present.

That licence does **not**, and cannot, grant you any right in Roland's software or data.

## What you must supply yourself

This engine is inert without `SCCore.dll` from a Roland SOUND Canvas VA installation. That file — and
everything derived from it — remains Roland Corporation's:

- the 24 MB wave ROM embedded in it, which is the literal Sound Canvas hardware mask ROM
- the synth curve, key-follow and patch-directory tables
- the reverb and chorus coefficients read out of the running engine
- any audio decoded or rendered from the above

None of that is committed here, and none of it is redistributed. `.gitignore` excludes each category
and names the command that regenerates it locally. `manifest.json` is tracked because it is a map of
*where* those tables live, not the tables themselves — the same distinction the upstream
[TabulaSonora spec](https://github.com/TabulaSonora/spec) draws.

Obtain the DLL from your own licensed installation. Sound Canvas VA was discontinued in September
2024.

## Purpose

This is a preservation and interoperability effort on a discontinued product. It exists so that music
written for the Sound Canvas can still be played, on platforms the original plugin never supported and
after it has stopped being sold.

## Compatibility note

BSD 3-Clause is GPL-compatible, so this code can be incorporated into GPL-licensed projects —
including [Cog](https://github.com/losnoco/Cog) (GPL-2.0) — without a separate grant or exception.
