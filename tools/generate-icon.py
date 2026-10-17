#!/usr/bin/env python3
"""
SmartMacro icon generator: "Cybernetic Vision Core + Hyper-Velocity Cursor".

Renders the app icon into Assets/SmartMacro.ico with PNG-compressed frames for
16, 24, 32, 48, 64, 128 and 256 px, plus Assets/SmartMacro-256.png / -1024.png previews.

Every frame is drawn from vector shapes at a high resolution (at least 1024 px), then scaled down
with a Lanczos filter (anti-aliased, high-quality resampling). The small frames (16 / 24 / 32 px) use a
simplified "pixel-optimized" variant: thicker, shorter scanner brackets, a bigger and bolder cursor, a
simpler bolt and no hair-thin details, so they stay sharp in the taskbar, tray and File Explorer.

Usage (from the repository root):   python tools/generate-icon.py
Needs: Python 3.9+, Pillow, NumPy      (pip install pillow numpy)
"""

from __future__ import annotations

import io
import os
import struct
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(ROOT, "Assets")
SIZES = [16, 24, 32, 48, 64, 128, 256]

# ---------------- Palette ----------------
MIDNIGHT = (0x05, 0x0B, 0x14)
INDIGO = (0x17, 0x15, 0x3B)
INDIGO_GLOW = (0x26, 0x22, 0x5E)
CYAN = (0x22, 0xD3, 0xEE)
SKY = (0x38, 0xBD, 0xF8)
VIOLET = (0xA8, 0x55, 0xF7)
LAVENDER = (0xC0, 0x84, 0xFC)
INDIGO_EDGE = (0x81, 0x8C, 0xF8)
WHITE = (0xFF, 0xFF, 0xFF)
TITANIUM_HI = (0xF8, 0xFA, 0xFC)
TITANIUM_MID = (0xCB, 0xD5, 0xE1)
TITANIUM_LO = (0x7C, 0x8A, 0xA0)
OUTLINE = (0x06, 0x0A, 0x18)


# ---------------- Small raster helpers (all work on float RGBA arrays, 0..1) ----------------

def blank(size: int) -> np.ndarray:
    return np.zeros((size, size, 4), dtype=np.float32)


def mask_from(draw_fn, size: int) -> np.ndarray:
    """Runs draw_fn(ImageDraw, size) on an 8-bit mask and returns it as 0..1 floats."""
    m = Image.new("L", (size, size), 0)
    draw_fn(ImageDraw.Draw(m), size)
    return np.asarray(m, dtype=np.float32) / 255.0


def blur(mask: np.ndarray, radius: float) -> np.ndarray:
    img = Image.fromarray(np.clip(mask * 255, 0, 255).astype(np.uint8), "L")
    return np.asarray(img.filter(ImageFilter.GaussianBlur(radius)), dtype=np.float32) / 255.0


def linear(size: int, p0, p1, stops) -> np.ndarray:
    """Linear gradient from p0 to p1 (unit coords 0..1) through [(t, (r, g, b))] stops → H×W×3 floats."""
    ys, xs = np.mgrid[0:size, 0:size].astype(np.float32) / size
    dx, dy = p1[0] - p0[0], p1[1] - p0[1]
    t = ((xs - p0[0]) * dx + (ys - p0[1]) * dy) / (dx * dx + dy * dy)
    return ramp(np.clip(t, 0, 1), stops)


def radial(size: int, center, radius, stops) -> np.ndarray:
    ys, xs = np.mgrid[0:size, 0:size].astype(np.float32) / size
    t = np.sqrt((xs - center[0]) ** 2 + (ys - center[1]) ** 2) / radius
    return ramp(np.clip(t, 0, 1), stops)


def ramp(t: np.ndarray, stops) -> np.ndarray:
    out = np.zeros(t.shape + (3,), dtype=np.float32)
    positions = [s[0] for s in stops]
    for c in range(3):
        values = [s[1][c] / 255.0 for s in stops]
        out[..., c] = np.interp(t, positions, values)
    return out


def paint(canvas: np.ndarray, mask: np.ndarray, color, opacity: float = 1.0) -> None:
    """Source-over: paints color (H×W×3 array or an (r, g, b) tuple) through mask onto canvas."""
    if isinstance(color, tuple):
        color = np.broadcast_to(np.array(color, dtype=np.float32) / 255.0, canvas.shape[:2] + (3,))
    a = np.clip(mask * opacity, 0, 1)[..., None]
    out_a = a + canvas[..., 3:4] * (1 - a)
    safe = np.where(out_a > 0, out_a, 1)
    canvas[..., :3] = (color * a + canvas[..., :3] * canvas[..., 3:4] * (1 - a)) / safe
    canvas[..., 3:4] = out_a


def add_light(canvas: np.ndarray, mask: np.ndarray, color, strength: float) -> None:
    """Additive glow (screen-like), kept inside the existing alpha (glow never leaks outside the squircle)."""
    c = np.array(color, dtype=np.float32) / 255.0
    light = np.clip(mask * strength, 0, 1)[..., None] * c
    canvas[..., :3] = 1 - (1 - canvas[..., :3]) * (1 - light)


def to_image(canvas: np.ndarray) -> Image.Image:
    return Image.fromarray(np.clip(canvas * 255 + 0.5, 0, 255).astype(np.uint8), "RGBA")


# ---------------- Geometry (design space = 256 × 256 units) ----------------

def scaled(points, s):
    return [(x * s, y * s) for x, y in points]


# Precision arrow cursor: tip top-left, a sharp tail notch. Units of the 256 design grid.
CURSOR = [(80, 68), (80, 172), (105, 149), (121, 186), (141, 177), (125, 142), (159, 142)]
CURSOR_SMALL = [(68, 56), (68, 180), (98, 153), (118, 197), (141, 186), (122, 144), (163, 144)]

# Neural lightning bolt, trailing behind the cursor towards the bottom right.
BOLT = [(168, 58), (126, 136), (156, 136), (132, 204), (202, 116), (170, 116), (194, 58)]
BOLT_CORE = [(176, 72), (142, 130), (166, 130), (152, 178), (188, 124), (160, 124), (182, 72)]
BOLT_SMALL = [(172, 52), (122, 140), (158, 140), (134, 214), (214, 108), (178, 108), (204, 52)]


def bracket_lines(inset: float, arm: float):
    """Four L-shaped scanner brackets as polylines (corner point in the middle)."""
    a, b = inset, 256 - inset
    return [
        [(a, a + arm), (a, a), (a + arm, a)],          # ⌜
        [(b - arm, a), (b, a), (b, a + arm)],          # ⌝
        [(a, b - arm), (a, b), (a + arm, b)],          # ⌞
        [(b - arm, b), (b, b), (b, b - arm)],          # ⌟
    ]


def draw_polyline(d: ImageDraw.ImageDraw, pts, width: float):
    d.line(pts, fill=255, width=max(1, int(round(width))), joint="curve")
    r = width / 2
    for x, y in (pts[0], pts[-1]):  # round caps
        d.ellipse((x - r, y - r, x + r, y + r), fill=255)


# ---------------- The icon ----------------

def render(size: int) -> Image.Image:
    """One icon frame. Sizes ≤ 32 px use the simplified, pixel-optimized variant."""
    small = size <= 32
    tiny = size <= 24
    work = max(1024, size * 2)
    s = work / 256.0
    canvas = blank(work)

    # 1. Squircle base: deep-space radial gradient (indigo core → midnight slate edges).
    inset = 6 if small else 10
    radius = (256 - 2 * inset) * 0.24
    squircle = mask_from(lambda d, w: d.rounded_rectangle(
        (inset * s, inset * s, (256 - inset) * s, (256 - inset) * s), radius=radius * s, fill=255), work)
    base = radial(work, (0.5, 0.40), 0.78, [(0.0, INDIGO_GLOW), (0.35, INDIGO), (0.75, (0x0B, 0x10, 0x26)), (1.0, MIDNIGHT)])
    paint(canvas, squircle, base)

    # Faint top sheen (glass) and a soft cyan nebula behind the emblem.
    if not tiny:
        sheen = mask_from(lambda d, w: d.ellipse((-40 * s, -150 * s, 296 * s, 110 * s), fill=255), work) * squircle
        paint(canvas, blur(sheen, 18 * s), (0x9F, 0xB4, 0xFF), 0.07)
    nebula = mask_from(lambda d, w: d.ellipse((60 * s, 50 * s, 210 * s, 205 * s), fill=255), work)
    add_light(canvas, blur(nebula, 34 * s) * squircle, SKY, 0.22 if not tiny else 0.12)

    # Vision-HUD scan lines (only where they can be seen).
    if size >= 64:
        lines = mask_from(lambda d, w: [d.line(((inset + 4) * s, y * s, (252 - inset) * s, y * s), fill=255, width=max(1, int(s * 0.9)))
                                         for y in range(30, 230, 12)], work)
        paint(canvas, lines * squircle, SKY, 0.035)

    # 2. Target scanner brackets (cyan → neon violet, diagonal).
    if tiny:
        br_inset, br_arm, br_w = 30, 38, 22
    elif small:
        br_inset, br_arm, br_w = 32, 38, 15
    else:
        br_inset, br_arm, br_w = 44, 40, 9
    brackets = mask_from(lambda d, w: [draw_polyline(d, scaled(p, s), br_w * s) for p in bracket_lines(br_inset, br_arm)], work)
    bracket_color = linear(work, (0.15, 0.15), (0.85, 0.85), [(0.0, CYAN), (0.5, (0x81, 0x8C, 0xF8)), (1.0, VIOLET)])
    if not tiny:
        add_light(canvas, blur(brackets, 7 * s) * squircle, CYAN, 0.55)
    paint(canvas, brackets, bracket_color)

    # 3. Neural lightning bolt behind the cursor: outer glow, neon body, white-hot core.
    bolt_pts = BOLT_SMALL if small else BOLT
    bolt = mask_from(lambda d, w: d.polygon(scaled(bolt_pts, s), fill=255), work)
    if not tiny:
        add_light(canvas, blur(bolt, 16 * s) * squircle, LAVENDER, 0.85)
        add_light(canvas, blur(bolt, 5 * s) * squircle, SKY, 0.6)
    bolt_color = linear(work, (0.62, 0.15), (0.55, 0.85), [(0.0, SKY), (0.55, LAVENDER), (1.0, (0xE8, 0x79, 0xF9))])
    paint(canvas, bolt, bolt_color)
    if not small:
        core = mask_from(lambda d, w: d.polygon(scaled(BOLT_CORE, s), fill=255), work)
        paint(canvas, blur(core, 2.2 * s), WHITE, 0.95)
    else:
        # A single bright spine keeps the bolt readable without sub-pixel detail.
        spine = mask_from(lambda d, w: d.line(scaled([(190, 64), (146, 134), (176, 126), (150, 196)], s), fill=255, width=int(7 * s)), work)
        paint(canvas, spine * bolt, WHITE, 0.55)

    # 4. Precision arrow cursor: drop shadow, dark outline, titanium bevel.
    cursor_pts = CURSOR_SMALL if small else CURSOR
    outline_w = (14 if tiny else 11 if small else 8) * s
    outline = mask_from(lambda d, w: (d.polygon(scaled(cursor_pts, s), fill=255),
                                      d.line(scaled(cursor_pts + [cursor_pts[0]], s), fill=255, width=int(outline_w), joint="curve")), work)
    if not tiny:
        shadow = np.roll(np.roll(blur(outline, 7 * s), int(5 * s), axis=0), int(4 * s), axis=1)
        paint(canvas, shadow * squircle, (0, 0, 0), 0.55)
    paint(canvas, outline, OUTLINE)
    body = mask_from(lambda d, w: d.polygon(scaled(cursor_pts, s), fill=255), work)
    titanium = linear(work, (0.25, 0.18), (0.55, 0.72), [(0.0, TITANIUM_HI), (0.45, TITANIUM_MID), (0.8, TITANIUM_LO), (1.0, (0xA5, 0xB4, 0xC8))])
    paint(canvas, body, titanium)
    if not small:
        # Bevel: a bright inner edge on the left / top, a cool shade on the lower facet.
        tip, left = cursor_pts[0], cursor_pts[1]
        bevel = mask_from(lambda d, w: d.line(scaled([(tip[0] + 5, tip[1] + 14), (left[0] + 5, left[1] - 22)], s), fill=255, width=int(4 * s)), work)
        paint(canvas, blur(bevel, 1.2 * s) * body, WHITE, 0.9)
        facet = mask_from(lambda d, w: d.polygon(scaled([cursor_pts[2], cursor_pts[3], cursor_pts[4], cursor_pts[5]], s), fill=255), work)
        paint(canvas, facet * body, (0x5B, 0x6B, 0x86), 0.35)
        # Energy rim where the bolt meets the cursor.
        rim = mask_from(lambda d, w: d.line(scaled(cursor_pts[5:] + [cursor_pts[0]], s), fill=255, width=int(3 * s)), work)
        paint(canvas, blur(rim, 1.5 * s) * body, SKY, 0.7)

    # 5. Glowing 1 px inner border: cyan on top fading to indigo at the bottom.
    if not tiny:
        bw = max(1.0, (1.0 if size >= 128 else 1.4) * 256 / size)  # ≈ one output pixel wide
        ring = squircle - mask_from(lambda d, w: d.rounded_rectangle(
            ((inset + bw) * s, (inset + bw) * s, (256 - inset - bw) * s, (256 - inset - bw) * s),
            radius=(radius - bw) * s, fill=255), work)
        ring_color = linear(work, (0.5, 0.0), (0.5, 1.0), [(0.0, SKY), (0.5, (0x5B, 0x6E, 0xD6)), (1.0, INDIGO_EDGE)])
        ring_alpha = linear(work, (0.5, 0.0), (0.5, 1.0), [(0.0, (230, 230, 230)), (1.0, (130, 130, 130))])[..., 0]
        paint(canvas, np.clip(ring, 0, 1) * ring_alpha, ring_color)

    image = to_image(canvas).resize((size, size), Image.LANCZOS)
    if small:
        image = image.filter(ImageFilter.UnsharpMask(radius=0.6, percent=60, threshold=0))
    return image


def write_ico(path: str, frames: list[Image.Image]) -> None:
    """ICO container with one PNG-compressed frame per size (Windows Vista+ reads PNG frames at every size)."""
    blobs = []
    for frame in frames:
        buffer = io.BytesIO()
        frame.save(buffer, format="PNG", optimize=True)
        blobs.append(buffer.getvalue())

    header = struct.pack("<HHH", 0, 1, len(frames))
    offset = 6 + 16 * len(frames)
    entries = b""
    for frame, blob in zip(frames, blobs):
        w, h = frame.size
        entries += struct.pack("<BBBBHHII", w % 256, h % 256, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)

    with open(path, "wb") as f:
        f.write(header + entries + b"".join(blobs))


def main() -> int:
    os.makedirs(ASSETS, exist_ok=True)
    frames = [render(size) for size in SIZES]
    ico = os.path.join(ASSETS, "SmartMacro.ico")
    write_ico(ico, frames)
    frames[-1].save(os.path.join(ASSETS, "SmartMacro-256.png"), optimize=True)
    render(1024).save(os.path.join(ASSETS, "SmartMacro-1024.png"), optimize=True)
    print(f"Wrote {ico} ({os.path.getsize(ico):,} bytes) with frames: {', '.join(f'{s}x{s}' for s in SIZES)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
