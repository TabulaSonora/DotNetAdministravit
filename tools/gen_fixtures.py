#!/usr/bin/env python
"""Generate differential-test fixtures from the Tabula Sonora Python reference.

The reference is the executable spec for the fixed-point routines. This sweeps each ported
function over a wide input grid and writes the results to JSON, so the C# tests can assert
against them without needing Python at test time.

Run with uv (a bare `python` on this machine is only the Windows Store stub):

    uv run --with numpy python tools/gen_fixtures.py [spec-repo] [output-dir]

Defaults: spec repo ../TabulaSonora, output tests/TabulaSonora.Tests/fixtures.
"""
import json
import os
import sys

SPEC = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else
                       os.path.join(os.path.dirname(__file__), "..", "..", "TabulaSonora"))
OUT = os.path.abspath(sys.argv[2] if len(sys.argv) > 2 else
                      os.path.join(os.path.dirname(__file__), "..",
                                   "tests", "TabulaSonora.Tests", "fixtures"))
sys.path.insert(0, SPEC)

import numpy as np                      # noqa: E402
import scvx_engine as E                 # noqa: E402
import scvx_partials as P               # noqa: E402
import scvx_directory as D              # noqa: E402

os.makedirs(OUT, exist_ok=True)


def write(name, obj):
    path = os.path.join(OUT, name)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(obj, f, separators=(",", ":"))
    print(f"  {name}: {os.path.getsize(path):,} bytes")


print("generating fixtures from", SPEC)

# --- envelope rate/level scaling: the two 8.8 converters, over their whole input domain ---
write("env_rate_scale.json", {
    "description": "env_rate_scale(base, mod) -> 8.8 multiplier; base 0..255, mod 0..127",
    "values": [[E.env_rate_scale(b, m) for m in range(128)] for b in range(256)],
})

write("env_level_scale.json", {
    "description": "env_level_scale(level, mod) -> 8.8 multiplier; level 0..255 (signed use), mod 0..127",
    "values": [[E.env_level_scale(a, m) for m in range(128)] for a in range(256)],
})

# --- segment timing ---
seg = []
for rate in range(256):
    for rate_mult in (0x40, 0x100, 0x200, 0x3ff):
        for vel_mult in (0x80, 0x100, 0x1c0):
            seg.append([rate, rate_mult, vel_mult, E._seg_ms(rate, rate_mult, vel_mult)])
write("seg_ms.json", {"description": "[rateByte, rateMult, velMult, milliseconds]", "values": seg})

# --- amplitude curve ---
amp = [[lv, float(E.amp_of(np.array([lv]))[0])] for lv in range(0, 0x10000, 17)]
write("amp_of.json", {"description": "[level16, gain]", "values": amp})

# --- segment shape ---
curve = []
for linear in (False, True):
    for start, target in ((0.0, 1.0), (1.0, 0.0), (0.25, 0.75), (0.9, 0.1)):
        for i in range(0, 257):
            u = i / 256.0
            curve.append([int(linear), start, target, u,
                          float(E._seg_curve(np.array([u]), start, target, linear=linear)[0])])
write("seg_curve.json", {"description": "[linear, start, target, u, value]", "values": curve})

# --- part volume ---
pv = []
for cc7 in range(0, 128, 3):
    for cc11 in (32, 64, 100, 127):
        pv.append([cc7, cc11, 127, float(E.part_volume_scale(cc7, cc11, 127))])
write("part_volume.json", {"description": "[cc7, cc11, master, scale]", "values": pv})

# --- partial level + TVA base level, over real tones ---
levels = []
bases = []
for tone_no in range(0, 512):
    tn = D.tone(tone_no)
    if not tn or len(tn["name"]) < 2:
        continue
    for pi in range(2):
        raw = P._block(tone_no, pi)
        if len(raw) < 0x6e or (raw[2] | (raw[3] << 8)) == 0xffff:
            continue
        for vel in range(1, 128, 9):
            plv = P.partial_level(raw, vel)
            levels.append([tone_no, pi, vel, -1 if plv is None else int(plv)])
            if plv is None:
                continue
            for key in (24, 48, 60, 84, 108):
                bases.append([tone_no, pi, vel, key,
                              int(E.tva_base_level(raw, plv, key, 127, 127))])
write("partial_level.json", {"description": "[tone, slot, velocity, level or -1]", "values": levels})
write("tva_base_level.json", {"description": "[tone, slot, velocity, key, base16]", "values": bases})

# --- TVF: the cutoff chain, its coefficients, and the response mapping ---
write("tvf_tap.json", {
    "description": "filter type byte -> 'lp'|'bp'|'hp'|'notch'|null(bypass); index is the byte",
    "values": [E.tvf_tap(t) for t in range(256)],
})

units = []
for cutoff in range(0, 0x8000, 61):
    for reso in (4, 0x20, 0x40, 0x60, 0x7f):
        units.append([cutoff, reso, E.tvf_cutoff_units(float(cutoff), reso)])
write("tvf_cutoff_units.json", {"description": "[cutoff15, resoByte, units]", "values": units})

fco = [[u, float(E.tvf_f_coef(u))] for u in range(0, 0x40000, 97)]
write("tvf_f_coef.json", {"description": "[units, f]", "values": fco})

qs = []
for u in range(0, 0x40000, 3499):
    for reso in (4, 0x20, 0x40, 0x60, 0x7f):
        for ftype in (0, 1, 2, 4, 6):
            qs.append([u, reso, ftype, float(E.tvf_q(u, reso, ftype))])
write("tvf_q.json", {"description": "[units, resoByte, filterType, q]", "values": qs})

env = []
for tone_no in range(0, 512):
    tn = D.tone(tone_no)
    if not tn or len(tn["name"]) < 2:
        continue
    for pi in range(2):
        raw = P._block(tone_no, pi)
        if len(raw) < 0x6e or (raw[2] | (raw[3] << 8)) == 0xffff:
            continue
        for vel in (16, 64, 110, 127):
            for key in (36, 60, 84):
                peak, segs, rel = E.tvf_env_offsets(raw, key, vel)
                env.append([tone_no, pi, vel, key, int(peak),
                            [int(s) for s in segs], int(rel)])
write("tvf_env_offsets.json",
      {"description": "[tone, slot, velocity, key, peak, [4 segment offsets], release]", "values": env})

# --- LFO: waveforms, configuration, and the per-tick modulation ---
waves = []
for sel in range(0x20):
    for phase in range(0, 0x10000, 419):
        waves.append([sel, phase, E.lfo_wave(phase, sel)])
write("lfo_wave.json", {"description": "[waveform, phase, value]", "values": waves})

write("lfo_modwheel.json", {
    "description": "[cc1, depth, offset, milliSemitones]",
    "values": [[cc, d, 0, E.mod_wheel_depth_ms(cc, d, 0)]
               for cc in range(0, 128, 3) for d in (0, 0x0a, 0x40, 0x7f)],
})

cfgs = []
mods = []
TICKS = 60
for tone_no in range(0, 400):
    tn = D.tone(tone_no)
    if not tn or len(tn["name"]) < 2:
        continue
    for pi in range(2):
        raw = P._block(tone_no, pi)
        if len(raw) < 0x6e or (raw[2] | (raw[3] << 8)) == 0xffff:
            continue
        c = E.lfo_config(tone_no, pi)
        cfgs.append([tone_no, pi,
                     [c["lfo1"][k] for k in ("wave", "phase0", "inc", "delay", "fade", "pitch", "tvf", "tva")],
                     [c["lfo2"][k] for k in ("wave", "phase0", "inc", "delay", "fade", "pitch", "tvf", "tva")]])
        for dest in ("pitch", "tvf", "tva"):
            series = E.lfo_mod_ms(tone_no, pi, TICKS, dest)
            mods.append([tone_no, pi, dest, [float(x) for x in series]])
write("lfo_config.json",
      {"description": "[tone, slot, lfo1[wave,phase0,inc,delay,fade,pitch,tvf,tva], lfo2[...]]",
       "values": cfgs})
write("lfo_modulation.json",
      {"description": f"[tone, slot, destination, {TICKS} per-tick values]", "values": mods})

# --- pitch: key-follow, base pitch, and the envelope ---
kf = []
for tone_no in range(0, 400):
    tn = D.tone(tone_no)
    if not tn or len(tn["name"]) < 2:
        continue
    for pi in range(2):
        raw = P._block(tone_no, pi)
        if len(raw) < 0x6e or (raw[2] | (raw[3] << 8)) == 0xffff:
            continue
        pp = P.partial_params(tone_no, pi)
        kc = pp["keyCenter"] if pp else 0x3c
        for note in (0, 24, 36, 48, 60, 72, 84, 108, 127):
            key, weight = E.keyfollow_key(raw, note, kc)
            kf.append([tone_no, pi, note, kc, int(key), int(weight),
                       int(E.base_pitch_ms(raw, note, kc))])
write("pitch_keyfollow.json",
      {"description": "[tone, slot, note, keyCenter, key, weight, basePitchMilliSemitones]", "values": kf})

penv = []
for tone_no in range(0, 400):
    tn = D.tone(tone_no)
    if not tn or len(tn["name"]) < 2:
        continue
    for pi in range(2):
        raw = P._block(tone_no, pi)
        if len(raw) < 0x6e or (raw[2] | (raw[3] << 8)) == 0xffff:
            continue
        for vel in (32, 100, 127):
            for key in (36, 60, 84):
                r = E.pitch_env_offsets(raw, key, vel)
                if r is None:
                    penv.append([tone_no, pi, vel, key, None])
                    continue
                start, targets, rel, times, rel_ms = r
                penv.append([tone_no, pi, vel, key,
                             [int(start), [int(t) for t in targets], int(rel),
                              [float(t) for t in times], float(rel_ms)]])
write("pitch_env_offsets.json",
      {"description": "[tone, slot, velocity, key, null | [start,[targets],release,[times],releaseMs]]",
       "values": penv})

pticks = []
for tone_no in range(0, 400):
    tn = D.tone(tone_no)
    if not tn or len(tn["name"]) < 2:
        continue
    for pi in range(2):
        raw = P._block(tone_no, pi)
        if len(raw) < 0x6e or (raw[2] | (raw[3] << 8)) == 0xffff:
            continue
        series = E.pitch_env_ticks(raw, 60, 100, 0.5, 80)
        if series is None:
            continue
        pticks.append([tone_no, pi, [float(x) for x in series]])
write("pitch_env_ticks.json",
      {"description": "[tone, slot, 80 per-tick milli-semitone offsets at key 60 vel 100 hold 0.5s]",
       "values": pticks})

print("done")
