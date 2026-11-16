#!/usr/bin/env python3
"""Analytic model of the SB03 / PARTIAL-14 / START-MIN / INNER case (Helivanta 2.8.583 runtime audit fixture).
All lengths in cm. Reproduces: face length 34.004 mm (matches the log), the ring-vs-plane knife edge,
the sphere clearance and the cut-off sliver volume. Requires numpy."""
import math
import numpy as np

t42 = math.tan(math.radians(42.0))
R0, pitch = 1.0, 0.2
r_low = 17.835 / 20.0                     # GOST M20x2 internal minor radius used by the matrix
ov = min(0.1 * pitch, 0.5 * min(R0, r_low))
r_high = R0 + ov                          # join collar
Rs, cx = 5.2, 0.20                        # lower sphere (material above it)
zc = -1.25 + math.sqrt(Rs ** 2 - (R0 - cx) ** 2)

def z_lower(x, y):                        # sphere boundary
    return zc - np.sqrt(Rs ** 2 - (x - cx) ** 2 - y ** 2)

def z_upper(x, y):                        # inclined plane 42 deg (material below it)
    return 1.25 + t42 * x

th = np.linspace(0, 2 * np.pi, 7201)
zmin = z_lower(np.cos(th), np.sin(th)).min()
zmax = z_upper(np.cos(th), np.sin(th)).max()
print(f"face length on the bore: {(zmax - zmin) * 10:.4f} mm   (runtime log: 34.0040404 mm)")

a0, a1 = 0.2, 1.6                         # offset 2 mm, length 14 mm, start MIN
z0, z1 = zmin + a0, zmin + a1
print(f"ring axial range {a0*10:.1f}..{a1*10:.1f} mm  (z = {z0:.4f}..{z1:.4f} cm)")
for r in (R0, r_high):
    zp = 1.25 - t42 * r
    print(f"plane lowest point at r={r:.4f}: z={zp:.6f}; ring end exceeds it by {(z1 - zp) * 10:+.4f} mm")

rs = np.linspace(r_low, r_high, 60)
T, Rr = np.meshgrid(th, rs)
X, Y = Rr * np.cos(T), Rr * np.sin(T)
print(f"sphere clearance to ring start: {(z0 - z_lower(X, Y).max()) * 10:.3f} mm  (> 0 => sphere not reached)")
h = np.clip(z1 - z_upper(X, Y), 0, None)
vol = (h * Rr).sum() * (rs[1] - rs[0]) * (th[1] - th[0])
ring_vol = math.pi * (r_high ** 2 - r_low ** 2) * (z1 - z0)
print(f"sliver beyond the plane: height <= {h.max()*10:.3f} mm, volume {vol:.3e} cm3 ({vol/ring_vol:.2e} of ring {ring_vol:.4f} cm3)")
print(f"sliver radial range starts at r = {rs[(h > 0).any(axis=1)].min():.5f} cm (bore radius 1.0) -> lies entirely in the join collar")
