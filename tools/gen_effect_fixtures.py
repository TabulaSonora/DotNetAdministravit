#!/usr/bin/env python
"""Dump impulse responses of the three send effects from the Python reference.

Each effect is a recursive network, so an impulse response pins the whole thing: taps, gains,
feedback and the delay-line geometry all show up in it. Written as raw little-endian float32,
interleaved stereo, one file per effect and type.

Run with uv:

    uv run --with numpy python tools/gen_effect_fixtures.py [spec-repo] [output-dir]
"""
import os
import sys

import numpy as np

SPEC = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else
                       os.path.join(os.path.dirname(__file__), "..", "..", "TabulaSonora"))
OUT = os.path.abspath(sys.argv[2] if len(sys.argv) > 2 else
                      os.path.join(os.path.dirname(__file__), "..",
                                   "tests", "TabulaSonora.Tests", "fixtures"))
sys.path.insert(0, SPEC)

import scvx_reverb as R    # noqa: E402
import scvx_chorus as C    # noqa: E402
import scvx_delay as DL    # noqa: E402

os.makedirs(OUT, exist_ok=True)

# The Delay and PanDelay reverb types zero their diffusers and collapse to a single tap that does
# not arrive until 3,600 samples in, so a short window would leave those two types silent.
REVERB_SAMPLES = 8000
CHORUS_SAMPLES = 2000

# The delay times run to 31,920 samples and the fixed pre-delay adds 1,920 on top, so a short window
# captures nothing at all and the comparison passes vacuously.
DELAY_SAMPLES = 40000


def dump(name, stereo):
    path = os.path.join(OUT, name)
    np.asarray(stereo, dtype="<f4").tofile(path)
    print(f"  {name}: {os.path.getsize(path):,} bytes")


print("generating effect impulse responses from", SPEC)

for t in range(8):
    x = np.zeros(REVERB_SAMPLES)
    x[0] = 1.0
    dump(f"reverb_type_{t}.f32", R.reverb(x, R.type_dump(t)))

for t in range(8):
    x = np.zeros(CHORUS_SAMPLES)
    x[0] = 1.0
    dump(f"chorus_type_{t}.f32", C.chorus(x, C.type_dump(t)))

for t in range(10):
    x = np.zeros(DELAY_SAMPLES)
    x[0] = 1.0
    dump(f"delay_type_{t}.f32", DL.delay(x, DL.type_params(t)))

print("done")
