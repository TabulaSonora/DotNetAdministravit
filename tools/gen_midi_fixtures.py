#!/usr/bin/env python
"""Dump the reference's SMF parse and note extraction for a MIDI file.

Parsing is where a port silently diverges: running status, the tempo map, sysex framing and the
render-grid quantisation all have to agree exactly or every downstream comparison is measuring
the wrong notes.

Run with uv:

    uv run --with numpy python tools/gen_midi_fixtures.py [midi-file] [spec-repo] [output-dir]
"""
import json
import os
import sys

MIDI = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else
                       os.path.join(os.path.dirname(__file__), "..", "..",
                                    "SauceForYourEars", "canyon.mid"))
SPEC = os.path.abspath(sys.argv[2] if len(sys.argv) > 2 else
                       os.path.join(os.path.dirname(__file__), "..", "..", "TabulaSonora"))
OUT = os.path.abspath(sys.argv[3] if len(sys.argv) > 3 else
                      os.path.join(os.path.dirname(__file__), "..",
                                   "tests", "TabulaSonora.Tests", "fixtures"))
sys.path.insert(0, SPEC)

import scvx_sequencer as S    # noqa: E402

os.makedirs(OUT, exist_ok=True)

events = S.parse_smf(MIDI)

flat = []
for e in events:
    if e[1] == "sx":
        flat.append([int(e[0]), "sx", list(e[2])])
    else:
        flat.append([int(e[0]), "m", int(e[2]), int(e[3]), int(e[4])])

parts, master, notes, fx = S.build_parts(events)

note_rows = [[n["ch"], n["note"], n["vel"], int(n["on"]), int(n["off"]), n["prog"], n["bank"],
              n["pan"], n["cc7"], n["cc11"], n["cc91"], n["cc93"], n["cdly"]] for n in notes]

payload = {
    "source": os.path.basename(MIDI),
    "description": "events: [pos,'m',status,d1,d2] or [pos,'sx',bytes]; "
                   "notes: [ch,note,vel,on,off,prog,bank,pan,cc7,cc11,cc91,cc93,delaySend]",
    "events": flat,
    "notes": note_rows,
    "master": [[int(p), int(v)] for p, v in master],
    "reverbType": [[int(p), int(v)] for p, v in fx["rev_type"]],
    "chorusType": [[int(p), int(v)] for p, v in fx["cho_type"]],
    "delayType": [[int(p), int(v)] for p, v in fx["dly_type"]],
}

path = os.path.join(OUT, "smf_canyon.json")
with open(path, "w", encoding="utf-8") as f:
    json.dump(payload, f, separators=(",", ":"))

print(f"wrote {path}: {os.path.getsize(path):,} bytes")
print(f"  {len(flat)} events, {len(note_rows)} notes, {len(payload['master'])} master changes")
