"""Procedurally generates the wanted-poster paper texture as a PNG.

Everything here is layered the way real foxed paper actually goes wrong: uneven pulp tone first,
then fibres, then damp stains, then edge oxidation, then the torn boundary last so it cuts through
all of it.
"""
import numpy as np
from PIL import Image, ImageFilter, ImageDraw

W, H = 500, 260          # 2x the 250x130 CSS card, so it stays sharp on a retina display
SEED = 20260916


def fbm(w, h, rng, octaves=6, persistence=0.55, base=3):
    """Fractal noise in 0..1, built by stacking smoothly upscaled random grids."""
    total = np.zeros((h, w), np.float32)
    amp, norm = 1.0, 0.0
    for o in range(octaves):
        res = base * (2 ** o)
        gw = max(2, int(res * w / max(w, h)))
        gh = max(2, int(res * h / max(w, h)))
        grid = (rng.random((gh, gw)) * 255).astype(np.uint8)
        layer = np.asarray(
            Image.fromarray(grid, 'L').resize((w, h), Image.BICUBIC), np.float32) / 255.0
        total += layer * amp
        norm += amp
        amp *= persistence
    return total / norm


def normalise(a):
    lo, hi = float(a.min()), float(a.max())
    return (a - lo) / (hi - lo) if hi > lo else a * 0


def build():
    rng = np.random.default_rng(SEED)

    # ---- 1. Pulp tone -------------------------------------------------------
    # Cheap 19th-century stock was never one flat colour: the tone drifts in broad patches.
    tone = normalise(fbm(W, H, rng, octaves=5, persistence=0.62, base=2))

    light = np.array([246, 228, 184], np.float32)   # bleached highs
    dark = np.array([210, 173, 112], np.float32)    # dirtier lows
    img = light * tone[..., None] + dark * (1.0 - tone[..., None])

    # ---- 2. Fibres ----------------------------------------------------------
    # Stretched noise: paper fibres lie along the direction the pulp was rolled.
    fib = fbm(W // 2, H * 3, rng, octaves=4, persistence=0.5, base=8)
    fib = np.asarray(Image.fromarray((normalise(fib) * 255).astype(np.uint8), 'L')
                     .resize((W, H), Image.BILINEAR), np.float32) / 255.0
    img += (fib[..., None] - 0.5) * 14.0

    # ---- 3. Grain -----------------------------------------------------------
    img += (rng.random((H, W, 1)).astype(np.float32) - 0.5) * 13.0

    # ---- 4. Foxing ----------------------------------------------------------
    # The rusty freckling old paper gets. Sparse, soft-edged, warmer than the paper.
    stain_mask = np.zeros((H, W), np.float32)
    blotch = fbm(W, H, rng, octaves=3, persistence=0.5, base=4)
    yy0, xx0 = np.ogrid[:H, :W]
    # A handful of larger damp marks...
    for _ in range(14):
        cx, cy = rng.uniform(0, W), rng.uniform(0, H)
        r = rng.uniform(7, 18)
        d = ((xx0 - cx) / r) ** 2 + ((yy0 - cy) / (r * rng.uniform(0.5, 1.0))) ** 2
        stain_mask += np.clip(1.0 - d, 0, 1) ** 3.0 * rng.uniform(0.2, 0.5)
    # ...and the fine rust-coloured freckling that actually reads as age.
    for _ in range(320):
        cx, cy = rng.uniform(0, W), rng.uniform(0, H)
        r = rng.uniform(0.8, 3.2)
        d = ((xx0 - cx) / r) ** 2 + ((yy0 - cy) / r) ** 2
        stain_mask += np.clip(1.0 - d, 0, 1) ** 1.2 * rng.uniform(0.25, 0.7)
    stain_mask = np.clip(stain_mask * (0.5 + 0.85 * blotch), 0, 1)
    stain_colour = np.array([134, 82, 33], np.float32)
    img = img * (1 - stain_mask[..., None] * 0.40) + stain_colour * stain_mask[..., None] * 0.40

    # ---- 5. Edge oxidation --------------------------------------------------
    # Air and handling darken the margins first.
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    ex = np.minimum(xx, W - 1 - xx) / (W * 0.5)
    ey = np.minimum(yy, H - 1 - yy) / (H * 0.5)
    edge = np.clip(np.minimum(ex, ey) * 1.7, 0, 1)
    darken = (1.0 - edge) ** 4.5
    darken *= 0.55 + 0.9 * fbm(W, H, rng, octaves=4, base=3)   # uneven, not a clean vignette
    img *= (1.0 - np.clip(darken, 0, 1)[..., None] * 0.09)

    img = np.clip(img, 0, 255)

    # ---- 6. Printed rules ---------------------------------------------------
    pil = Image.fromarray(img.astype(np.uint8), 'RGB')
    ink = Image.new('RGBA', (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(ink)
    d.rectangle([26, 26, W - 27, H - 27], outline=(38, 25, 12, 240), width=5)
    d.rectangle([36, 36, W - 37, H - 37], outline=(38, 25, 12, 175), width=2)
    # Roughen the ink so it doesn't look laser-printed.
    ink_a = np.asarray(ink.split()[3], np.float32) / 255.0
    ink_a *= 0.80 + 0.42 * fbm(W, H, rng, octaves=5, base=12)
    ink_a = np.clip(ink_a, 0, 1)
    ink_rgb = np.array([38, 25, 12], np.float32)
    arr = np.asarray(pil, np.float32)
    arr = arr * (1 - ink_a[..., None]) + ink_rgb * ink_a[..., None]

    # Nail holes, top corners.
    for nx in (44, W - 44):
        yy2, xx2 = np.ogrid[:H, :W]
        d2 = ((xx2 - nx) / 4.0) ** 2 + ((yy2 - 42) / 4.0) ** 2
        hole = np.clip(1.0 - d2, 0, 1) ** 0.7
        arr = arr * (1 - hole[..., None] * 0.8) + np.array([40, 26, 12], np.float32) * hole[..., None] * 0.8

    # ---- 7. Torn edge -------------------------------------------------------
    # Two scales of noise eating into the distance-from-edge field. The coarse one makes the
    # outline wander in and out over the width of the poster; the fine one gives it teeth.
    # Punched-out circles were tried here and read as a hole punch - real tearing has no
    # characteristic radius, which is exactly what layered noise gives you for free.
    coarse = fbm(W, H, rng, octaves=2, persistence=0.5, base=2)
    fine = fbm(W, H, rng, octaves=5, persistence=0.55, base=14)
    dist = np.minimum(np.minimum(xx, W - 1 - xx), np.minimum(yy, H - 1 - yy))
    threshold = 1.0 + coarse * 17.0 + fine * 7.0
    alpha = np.clip((dist - threshold) / 1.1, 0, 1)
    alpha = np.clip(alpha, 0, 1)

    out = np.dstack([np.clip(arr, 0, 255), alpha * 255]).astype(np.uint8)
    return Image.fromarray(out, 'RGBA')


if __name__ == '__main__':
    import os
    import sys

    dest = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
        os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'wanted-poster.png')

    # Quantised to 256 colours: 30 KB instead of 180 KB, and the paper grain hides the banding
    # you would normally get away with nowhere else. FASTOCTREE because it is the only method
    # Pillow will apply to an image with an alpha channel, and the torn edge needs one.
    build().quantize(colors=256, method=Image.FASTOCTREE).save(dest, optimize=True)
    print('wrote %s (%d bytes)' % (dest, os.path.getsize(dest)))
