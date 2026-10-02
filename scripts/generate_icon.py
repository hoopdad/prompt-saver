"""Generates the Prompt Saver application icon.

Design concept:
  - Rounded-square tile with an indigo -> violet diagonal gradient
    (an "AI" visual cue common to modern dev-tool branding).
  - A white chat/speech bubble in the center representing a "prompt".
  - A four-point sparkle inside the bubble representing AI generation.
  - A solid bookmark ribbon tag in the top-right corner representing
    "saving" the prompt for reuse.

Run with: python scripts/generate_icon.py
Outputs: src/PromptSaver.Desktop/Resources/AppIcon.ico (multi-resolution)
         src/PromptSaver.Desktop/Resources/AppIcon.png (1024x1024 master)
"""

from __future__ import annotations

import math
import os

from PIL import Image, ImageDraw, ImageFilter

SIZE = 1024
OUT_DIR = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
    "src",
    "PromptSaver.Desktop",
    "Resources",
)


def lerp_color(c1, c2, t):
    return tuple(int(c1[i] + (c2[i] - c1[i]) * t) for i in range(3))


def draw_gradient_rounded_square(size, radius, top_color, bottom_color):
    base = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    gradient = Image.new("RGB", (size, size))
    px = gradient.load()
    for y in range(size):
        # diagonal gradient: blend based on (x + y) progress
        for x in range(0, size, 4):
            t = (x + y) / (2 * size)
            color = lerp_color(top_color, bottom_color, t)
            for dx in range(4):
                if x + dx < size:
                    px[x + dx, y] = color

    mask = Image.new("L", (size, size), 0)
    mdraw = ImageDraw.Draw(mask)
    mdraw.rounded_rectangle([(0, 0), (size - 1, size - 1)], radius=radius, fill=255)

    base.paste(gradient, (0, 0), mask)
    return base


def draw_bookmark_tag(draw, size, color):
    """Draws a solid bookmark ribbon tag tucked into the top-right corner."""
    tag_w = size * 0.225
    tag_h = size * 0.30
    right = size * 0.86
    left = right - tag_w
    top = -size * 0.02
    bottom = top + tag_h
    notch = tag_h * 0.26

    points = [
        (left, top),
        (right, top),
        (right, bottom),
        ((left + right) / 2, bottom - notch),
        (left, bottom),
    ]
    draw.polygon(points, fill=color)


def draw_speech_bubble(draw, size):
    margin = size * 0.20
    bubble_w = size - margin * 2
    bubble_h = bubble_w * 0.74
    left = margin
    top = size * 0.21
    right = left + bubble_w
    bottom = top + bubble_h
    radius = bubble_h * 0.32

    draw.rounded_rectangle(
        [(left, top), (right, bottom)], radius=radius, fill=(255, 255, 255, 240)
    )

    tail = [
        (size * 0.40, bottom - 2),
        (size * 0.46, bottom + size * 0.085),
        (size * 0.53, bottom - 2),
    ]
    draw.polygon(tail, fill=(255, 255, 255, 240))
    return left, top, right, bottom


def draw_sparkle(draw, cx, cy, r, color):
    """Four-point sparkle/star (classic 'AI generated' glyph)."""
    outer = r
    inner = r * 0.34
    pts = []
    for i in range(8):
        angle = math.pi / 4 * i - math.pi / 2
        radius = outer if i % 2 == 0 else inner
        pts.append((cx + radius * math.cos(angle), cy + radius * math.sin(angle)))
    draw.polygon(pts, fill=color)

    # small companion sparkle, upper-right
    sr = r * 0.32
    scx, scy = cx + r * 1.35, cy - r * 1.05
    pts2 = []
    for i in range(8):
        angle = math.pi / 4 * i - math.pi / 2
        radius = sr if i % 2 == 0 else sr * 0.34
        pts2.append((scx + radius * math.cos(angle), scy + radius * math.sin(angle)))
    draw.polygon(pts2, fill=color)


def build_icon():
    os.makedirs(OUT_DIR, exist_ok=True)

    indigo = (76, 59, 196)
    violet = (168, 85, 247)
    accent = (124, 58, 237)

    tile = draw_gradient_rounded_square(
        SIZE, radius=int(SIZE * 0.22), top_color=indigo, bottom_color=violet
    )

    # subtle inner glow / sheen for depth
    sheen = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    sdraw = ImageDraw.Draw(sheen)
    sdraw.ellipse(
        [(-SIZE * 0.35, -SIZE * 0.45), (SIZE * 0.95, SIZE * 0.55)],
        fill=(255, 255, 255, 40),
    )
    sheen = sheen.filter(ImageFilter.GaussianBlur(SIZE * 0.06))
    mask = Image.new("L", (SIZE, SIZE), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [(0, 0), (SIZE - 1, SIZE - 1)], radius=int(SIZE * 0.22), fill=255
    )
    tile = Image.composite(Image.alpha_composite(tile, sheen), tile, mask)

    draw = ImageDraw.Draw(tile)
    left, top, right, bottom = draw_speech_bubble(draw, SIZE)

    cx = (left + right) / 2
    cy = (top + bottom) / 2 - SIZE * 0.015
    draw_sparkle(draw, cx, cy, r=SIZE * 0.145, color=accent)

    # clip the bookmark tag to the tile's rounded-square silhouette
    corner_mask = Image.new("L", (SIZE, SIZE), 0)
    ImageDraw.Draw(corner_mask).rounded_rectangle(
        [(0, 0), (SIZE - 1, SIZE - 1)], radius=int(SIZE * 0.22), fill=255
    )
    tag_layer = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    draw_bookmark_tag(ImageDraw.Draw(tag_layer), SIZE, (255, 214, 10, 255))
    tile = Image.composite(
        Image.alpha_composite(tile, tag_layer), tile, corner_mask
    )

    png_path = os.path.join(OUT_DIR, "AppIcon.png")
    tile.save(png_path, format="PNG")

    ico_path = os.path.join(OUT_DIR, "AppIcon.ico")
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    tile.save(
        ico_path,
        format="ICO",
        sizes=[(s, s) for s in sizes],
    )
    print(f"Wrote {png_path}")
    print(f"Wrote {ico_path}")


if __name__ == "__main__":
    build_icon()
