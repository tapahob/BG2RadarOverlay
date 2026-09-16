"""Generates the WANTED poster background as an SVG.

Drawn by script rather than by hand because the effect depends on irregularity - a torn paper
edge with hand-placed points looks like a decorative zigzag rather than torn paper. A seeded RNG gives the randomness while keeping the file reproducible: rerun it and
you get the same poster back.
"""
import io
import random

W, H = 250.0, 110.0
random.seed(20260916)


def torn_edge_path():
    """A closed path around the poster whose edges wobble like torn paper."""
    pts = []

    def side(x0, y0, x1, y1, steps, amp):
        # Perpendicular jitter along the side, with the corners left almost clean so the poster
        # still reads as a rectangle rather than a blob.
        dx, dy = x1 - x0, y1 - y0
        nx, ny = -dy, dx
        length = (nx * nx + ny * ny) ** 0.5
        nx, ny = nx / length, ny / length
        for i in range(steps):
            t = i / float(steps)
            taper = min(t, 1 - t) * 2          # 0 at the corners, 1 mid-side
            j = random.uniform(-amp, amp) * (0.25 + 0.75 * taper)
            pts.append((x0 + dx * t + nx * j, y0 + dy * t + ny * j))

    m = 2.5                                     # inset so the wobble stays inside the viewBox
    side(m, m, W - m, m, 26, 1.7)               # top
    side(W - m, m, W - m, H - m, 14, 1.9)       # right
    side(W - m, H - m, m, H - m, 26, 1.7)       # bottom
    side(m, H - m, m, m, 14, 1.9)               # left

    d = "M %.2f %.2f " % pts[0]
    d += " ".join("L %.2f %.2f" % p for p in pts[1:])
    return d + " Z"


def creases():
    """Two faint fold lines - one light, one dark, as a crease catches the light on one side."""
    out = []
    for x in (W * 0.34, W * 0.71):
        jitter = random.uniform(-4, 4)
        out.append(
            '<path d="M %.1f 0 Q %.1f %.1f %.1f %.1f" stroke="#8a6633" stroke-width="0.7" '
            'fill="none" opacity="0.13"/>' % (x, x + jitter, H / 2, x, H))
        out.append(
            '<path d="M %.1f 0 Q %.1f %.1f %.1f %.1f" stroke="#fff6e2" stroke-width="0.7" '
            'fill="none" opacity="0.22"/>' % (x + 1.1, x + jitter + 1.1, H / 2, x + 1.1, H))
    return "\n    ".join(out)


svg = '''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W:.0f} {H:.0f}"
     preserveAspectRatio="none" role="img" aria-label="Aged wanted poster paper">
  <title>Wanted poster</title>
  <defs>
    <!-- Sun-bleached in the middle, dirtier towards the edges. -->
    <radialGradient id="paper" cx="45%" cy="38%" r="78%">
      <stop offset="0%"   stop-color="#f6e7c4"/>
      <stop offset="55%"  stop-color="#ecd7ab"/>
      <stop offset="100%" stop-color="#cdb184"/>
    </radialGradient>

    <!-- Paper tooth. fractalNoise rather than turbulence: turbulence gives a marbled, wet look,
         fractalNoise gives the flat speckle of cheap pulp. -->
    <filter id="grain" x="0" y="0" width="100%" height="100%">
      <feTurbulence type="fractalNoise" baseFrequency="0.9" numOctaves="4" seed="7" result="n"/>
      <feColorMatrix in="n" type="saturate" values="0"/>
      <feComponentTransfer>
        <feFuncA type="linear" slope="0.16"/>
      </feComponentTransfer>
    </filter>

    <!-- Softens the torn edge so it doesn't read as a crisp vector outline. -->
    <filter id="edgeblur" x="-10%" y="-10%" width="120%" height="120%">
      <feGaussianBlur stdDeviation="0.35"/>
    </filter>

    <clipPath id="sheet">
      <path d="{torn}"/>
    </clipPath>
  </defs>

  <!-- Shadow cast onto whatever sits behind the poster. -->
  <path d="{torn}" fill="#000" opacity="0.30" transform="translate(1.2 1.8)" filter="url(#edgeblur)"/>

  <g clip-path="url(#sheet)">
    <path d="{torn}" fill="url(#paper)" filter="url(#edgeblur)"/>
    {creases}
    <rect width="100%" height="100%" filter="url(#grain)" opacity="0.55"/>

    <!-- Double rule, the way a real bill is set: heavy outer, hairline inner. -->
    <rect x="5" y="5" width="{iw:.0f}" height="{ih:.0f}" fill="none" stroke="#3b2a15"
          stroke-width="2.2" opacity="0.85"/>
    <rect x="8.5" y="8.5" width="{iw2:.0f}" height="{ih2:.0f}" fill="none" stroke="#3b2a15"
          stroke-width="0.6" opacity="0.6"/>

    <!-- Nail holes, top corners only - the bottom of a poster curls away. -->
    <circle cx="14" cy="13" r="1.5" fill="#2a1c0c" opacity="0.45"/>
    <circle cx="14" cy="13" r="2.6" fill="none" stroke="#7a5a2e" stroke-width="0.5" opacity="0.3"/>
    <circle cx="{nx:.0f}" cy="13" r="1.5" fill="#2a1c0c" opacity="0.45"/>
    <circle cx="{nx:.0f}" cy="13" r="2.6" fill="none" stroke="#7a5a2e" stroke-width="0.5" opacity="0.3"/>
  </g>
</svg>
'''.format(W=W, H=H, torn=torn_edge_path(), creases=creases(),
           iw=W - 10, ih=H - 10, iw2=W - 17, ih2=H - 17, nx=W - 14)

import os
path = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                    'wanted-poster.svg')
io.open(path, 'w', encoding='utf-8', newline='\n').write(svg)
print("written %s (%d bytes)" % (path, len(svg.encode('utf-8'))))
