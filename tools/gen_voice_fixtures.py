#!/usr/bin/env python
"""Dump rendered notes from the reference: the whole voice chain in one artifact.

A rendered note exercises the codec, the resampler, the pitch chain, the filter, the amplitude
envelope, both LFOs and the pan law together, so an exact match here covers the synthesis path
end to end. Melodic and drum notes take different pitch routes, so both are dumped.

Run with uv:

    uv run --with numpy python tools/gen_voice_fixtures.py [spec-repo] [output-dir]
"""
import json
import os
import sys

import numpy as np

SPEC = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else
                       os.path.join(os.path.dirname(__file__), "..", "..", "TabulaSonora"))
OUT = os.path.abspath(sys.argv[2] if len(sys.argv) > 2 else
                      os.path.join(os.path.dirname(__file__), "..",
                                   "tests", "TabulaSonora.Tests", "fixtures"))
sys.path.insert(0, SPEC)

import scvx_engine as E    # noqa: E402

os.makedirs(OUT, exist_ok=True)

# (program, note, velocity, hold) -- chosen to cover distinct behaviours: a two-partial piano,
# an alternate-articulation string patch, a looping flute, a resonant sweep pad, and a
# reduced-key-follow sound effect.
MELODIC = [
    (0, 60, 100, 1.0),
    (0, 36, 40, 0.5),
    (48, 60, 100, 1.0),
    (73, 72, 100, 1.0),
    (95, 48, 110, 1.2),
    (122, 33, 100, 0.8),
]

# Drum notes: kick, snare, low tom, closed hat, crash.
DRUMS = [(36, 110), (38, 110), (41, 110), (42, 110), (49, 110)]

index = {"melodic": [], "drums": []}

for prog, note, vel, hold in MELODIC:
    sig, name, mono = E.render_note(prog, note, vel, hold, tail=1.8, tone_map=4)
    name_safe = "voice_m_%d_%d_%d.f32" % (prog, note, vel)
    np.asarray(sig, dtype="<f4").tofile(os.path.join(OUT, name_safe))
    index["melodic"].append({"program": prog, "note": note, "velocity": vel,
                             "hold": hold, "name": name, "file": name_safe,
                             "frames": int(len(sig))})
    print("  %-28s %-16s %d frames" % (name_safe, name, len(sig)))

for note, vel in DRUMS:
    sig, name, mono = E.render_drum_note(note, vel, ring=1.8, kit=0)
    name_safe = "voice_d_%d_%d.f32" % (note, vel)
    np.asarray(sig, dtype="<f4").tofile(os.path.join(OUT, name_safe))
    index["drums"].append({"note": note, "velocity": vel, "ring": 1.8,
                           "name": name, "file": name_safe, "frames": int(len(sig))})
    print("  %-28s %-16s %d frames" % (name_safe, name, len(sig)))

with open(os.path.join(OUT, "voices.json"), "w", encoding="utf-8") as f:
    json.dump(index, f, indent=1)

print("done")
