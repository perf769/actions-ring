"""Render the project-native Actions Ring SVG geometry to PNG and multi-size ICO."""

from __future__ import annotations

import math
from pathlib import Path

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
APP_ASSETS = ROOT / "src" / "ActionsRing.App" / "Assets"
PURPLE = (130, 78, 249, 255)
MINT = (101, 217, 176, 255)
GRAPHITE = (27, 29, 34, 255)
WHITE = (255, 255, 255, 255)


def render(size: int, supersample: int = 4) -> Image.Image:
    canvas = size * supersample
    ratio = canvas / 512
    image = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    node_radius = 34 * ratio
    ring_radius = 184 * ratio
    center = 256 * ratio
    for index in range(8):
        angle = -math.pi / 2 + index * math.pi / 4
        x = center + math.cos(angle) * ring_radius
        y = center + math.sin(angle) * ring_radius
        color = MINT if index == 0 else PURPLE
        draw.ellipse(
            (x - node_radius, y - node_radius, x + node_radius, y + node_radius),
            fill=color,
        )

    cursor = [
        (205, 165),
        (205, 350),
        (254, 301),
        (300, 393),
        (339, 373),
        (292, 284),
        (361, 276),
    ]
    cursor = [(round(x * ratio), round(y * ratio)) for x, y in cursor]
    outline = max(1, round(14 * ratio))
    draw.polygon(cursor, fill=GRAPHITE)
    draw.line(cursor + [cursor[0]], fill=WHITE, width=outline, joint="curve")
    draw.polygon(cursor, fill=GRAPHITE)

    return image.resize((size, size), Image.Resampling.LANCZOS)


def main() -> None:
    APP_ASSETS.mkdir(parents=True, exist_ok=True)
    render(1024).save(APP_ASSETS / "ActionsRingLogo.png", optimize=True)

    sizes = (16, 20, 24, 32, 40, 48, 64, 128, 256)
    images = [render(size, supersample=8 if size <= 32 else 4) for size in sizes]
    images[-1].save(
        APP_ASSETS / "ActionsRing.ico",
        format="ICO",
        append_images=images[:-1],
        sizes=[(size, size) for size in sizes],
    )


if __name__ == "__main__":
    main()
