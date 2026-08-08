from __future__ import annotations

from math import cos, pi, sin
from random import Random

from .raster import Canvas, Color


GOLD: Color = (1.0, 0.62, 0.12)
PALE_GOLD: Color = (1.0, 0.92, 0.58)
WHITE: Color = (1.0, 1.0, 1.0)
VIOLET: Color = (0.54, 0.12, 1.0)
CYAN: Color = (0.0, 0.72, 1.0)
EMERALD: Color = (0.08, 1.0, 0.34)
LIME: Color = (0.62, 1.0, 0.08)
CRIMSON: Color = (1.0, 0.03, 0.08)
EMBER: Color = (1.0, 0.25, 0.02)
BLUE_WHITE: Color = (0.42, 0.78, 1.0)


def orbit(
    canvas: Canvas,
    center: tuple[float, float],
    radii: tuple[float, float],
    rotation: float,
    color: Color,
    width: float = 0.8,
    strength: float = 1.0,
    start: float = 0.0,
    end: float = 2.0 * pi,
) -> None:
    cx, cy = center
    rx, ry = radii
    points: list[tuple[float, float]] = []
    steps = max(8, round(56 * abs(end - start) / (2.0 * pi)))
    for step in range(steps + 1):
        angle = start + (end - start) * step / steps
        x = rx * cos(angle)
        y = ry * sin(angle)
        points.append(
            (
                cx + x * cos(rotation) - y * sin(rotation),
                cy + x * sin(rotation) + y * cos(rotation),
            )
        )
    canvas.polyline(points, width, color, strength)


def star(canvas: Canvas, x: float, y: float, color: Color, size: float, strength: float = 1.0) -> None:
    canvas.line((x - size, y), (x + size, y), 0.45, color, strength)
    canvas.line((x, y - size), (x, y + size), 0.45, color, strength)
    canvas.glow(x, y, max(0.5, size / 2.0), color, strength * 0.7)


def particles(canvas: Canvas, seed: int, color: Color, count: int, region: tuple[int, int, int, int]) -> None:
    random = Random(seed)
    left, top, right, bottom = region
    for _ in range(count):
        x = random.uniform(left, right)
        y = random.uniform(top, bottom)
        canvas.glow(x, y, random.uniform(0.3, 0.9), color, random.uniform(0.25, 0.75))


def armor_helios() -> Canvas:
    canvas = Canvas()
    canvas.ring((32, 31), (23, 23), 0.8, PALE_GOLD, 1.15)
    canvas.ring((32, 31), (16, 16), 0.65, GOLD, 0.9)
    canvas.ring((32, 31), (9, 9), 0.55, WHITE, 0.75)
    for index in range(16):
        angle = 2.0 * pi * index / 16
        inner = (32 + 24 * cos(angle), 31 + 24 * sin(angle))
        outer = (32 + (28 if index % 2 == 0 else 26) * cos(angle), 31 + (28 if index % 2 == 0 else 26) * sin(angle))
        canvas.line(inner, outer, 0.55, GOLD, 1.0)
    canvas.ring((32, 56), (23, 4), 0.75, PALE_GOLD, 0.9)
    star(canvas, 32, 7, WHITE, 3.0, 1.2)
    particles(canvas, 1010, PALE_GOLD, 16, (4, 3, 60, 59))
    return canvas


def armor_hecate() -> Canvas:
    canvas = Canvas()
    orbit(canvas, (31, 31), (22, 22), 0.0, VIOLET, 0.9, 1.1, -0.85 * pi, 0.75 * pi)
    orbit(canvas, (31, 34), (24, 8), 0.38, CYAN, 0.65, 0.85, 0.1 * pi, 1.9 * pi)
    orbit(canvas, (31, 34), (24, 8), -0.38, VIOLET, 0.7, 1.0, 0.25 * pi, 1.75 * pi)
    for index in range(9):
        angle = 2.0 * pi * index / 9
        x = 31 + 17 * cos(angle)
        y = 31 + 17 * sin(angle)
        star(canvas, x, y, CYAN if index % 2 else VIOLET, 1.0, 0.7)
    canvas.ring((31, 57), (21, 4), 0.65, VIOLET, 0.8, gaps=5, gap_fraction=0.18)
    canvas.glow(31, 33, 2.4, (0.3, 0.0, 0.7), 0.55)
    particles(canvas, 1011, (0.5, 0.15, 1.0), 18, (5, 5, 58, 59))
    return canvas


def armor_gaia() -> Canvas:
    canvas = Canvas()
    spiral: list[tuple[float, float]] = []
    for step in range(65):
        y = 59 - step * 0.8
        x = 32 + 11 * sin(step * 0.34) * (0.45 + step / 115)
        spiral.append((x, y))
    canvas.polyline(spiral, 1.0, EMERALD, 1.2)
    canvas.polyline([(x + 1.4, y) for x, y in spiral], 0.45, PALE_GOLD, 0.55)
    for side in (-1, 1):
        branch: list[tuple[float, float]] = []
        for step in range(25):
            y = 47 - step * 1.55
            x = 32 + side * (9 + 0.34 * step)
            branch.append((x, y))
            if step % 3 == 0:
                direction = -side if step % 2 else side
                canvas.line((x, y), (x + direction * 3.2, y - 2.0), 0.8, LIME, 0.9)
                canvas.glow(x + direction * 3.2, y - 2.0, 0.75, LIME, 0.7)
        canvas.polyline(branch, 0.6, EMERALD, 0.85)
    canvas.ring((32, 58), (22, 4), 0.7, EMERALD, 0.85)
    particles(canvas, 1012, LIME, 24, (4, 3, 60, 58))
    return canvas


def armor_ares() -> Canvas:
    canvas = Canvas()
    canvas.ring((32, 29), (21, 21), 1.0, CRIMSON, 1.05, gaps=9, gap_fraction=0.22)
    canvas.ring((32, 29), (17, 17), 0.55, EMBER, 0.75, gaps=7, gap_fraction=0.34)
    for index in range(12):
        angle = 2.0 * pi * index / 12
        length = 9 if index % 3 == 0 else 5
        start = (32 + 22 * cos(angle), 29 + 22 * sin(angle))
        end = (32 + (22 + length) * cos(angle), 29 + (22 + length) * sin(angle))
        canvas.line(start, end, 0.75 if length == 9 else 0.5, EMBER, 1.0)
    canvas.line((32, 3), (32, 14), 0.8, PALE_GOLD, 0.9)
    canvas.line((29, 7), (32, 3), 0.65, CRIMSON, 0.9)
    canvas.line((35, 7), (32, 3), 0.65, CRIMSON, 0.9)
    canvas.ring((32, 57), (22, 4), 0.75, CRIMSON, 0.75, gaps=6, gap_fraction=0.2)
    particles(canvas, 1013, EMBER, 30, (2, 2, 61, 60))
    return canvas


def armor_olympian() -> Canvas:
    canvas = Canvas()
    for x, strength in ((28, 0.45), (32, 0.9), (36, 0.45)):
        canvas.line((x, 4), (x, 60), 2.0, BLUE_WHITE, strength)
    canvas.ring((32, 34), (25, 9), 0.7, GOLD, 0.9, gaps=7, gap_fraction=0.13)
    canvas.ring((32, 47), (19, 6), 0.6, CYAN, 0.8, gaps=5, gap_fraction=0.14)
    canvas.ring((32, 59), (23, 4), 0.75, PALE_GOLD, 1.0)
    crown = [(20, 15), (23, 7), (27, 13), (32, 3), (37, 13), (41, 7), (44, 15)]
    canvas.polyline(crown, 0.8, PALE_GOLD, 1.1)
    for x, y in ((8, 13), (14, 39), (50, 17), (56, 43), (21, 30), (44, 28)):
        star(canvas, x, y, WHITE if x % 2 else GOLD, 1.6, 1.0)
    particles(canvas, 1014, BLUE_WHITE, 28, (3, 2, 61, 61))
    return canvas


def weapon_ares() -> Canvas:
    canvas = Canvas()
    canvas.line((17, 58), (46, 7), 2.2, CRIMSON, 1.2)
    canvas.line((19, 57), (48, 8), 0.8, PALE_GOLD, 1.0)
    canvas.line((40, 13), (48, 6), 0.7, EMBER, 0.9)
    canvas.line((48, 6), (50, 16), 0.7, EMBER, 0.9)
    canvas.ring((45, 11), (11, 7), 0.65, CRIMSON, 0.8, gaps=6, gap_fraction=0.22)
    particles(canvas, 2010, EMBER, 28, (6, 2, 59, 61))
    return canvas


def weapon_zeus() -> Canvas:
    canvas = Canvas()
    points = [(14, 59), (24, 47), (20, 39), (35, 28), (30, 20), (49, 5)]
    canvas.polyline(points, 1.4, WHITE, 1.2)
    canvas.polyline([(x + 1.3, y) for x, y in points], 2.6, BLUE_WHITE, 0.75)
    for step in range(33):
        t = step / 32
        x = 15 + 34 * t + 5 * sin(t * 8 * pi)
        y = 59 - 54 * t
        canvas.glow(x, y, 0.7, CYAN, 0.7)
    canvas.glow(49, 6, 6.0, BLUE_WHITE, 0.5)
    particles(canvas, 2011, GOLD, 18, (4, 2, 60, 61))
    return canvas


def weapon_apollo() -> Canvas:
    canvas = Canvas()
    canvas.line((31, 59), (33, 12), 1.2, PALE_GOLD, 0.95)
    canvas.ring((33, 14), (13, 13), 0.8, PALE_GOLD, 1.0)
    canvas.ring((33, 14), (7, 7), 0.65, WHITE, 0.8)
    for side in (-1, 1):
        for step in range(7):
            y = 48 - step * 5
            x = 31 + side * (3 + step * 0.9)
            canvas.line((x, y), (x + side * 4, y - 2), 0.75, GOLD, 0.85)
    star(canvas, 33, 14, WHITE, 2.5, 1.1)
    particles(canvas, 2012, PALE_GOLD, 22, (4, 2, 60, 61))
    return canvas


def weapon_hecate() -> Canvas:
    canvas = Canvas()
    canvas.line((30, 60), (33, 10), 1.1, VIOLET, 0.8)
    orbit(canvas, (34, 16), (14, 14), 0.0, VIOLET, 0.85, 1.1, -0.85 * pi, 0.8 * pi)
    orbit(canvas, (32, 36), (18, 6), 0.45, CYAN, 0.7, 0.9)
    orbit(canvas, (32, 42), (18, 6), -0.45, VIOLET, 0.7, 0.9)
    for index in range(8):
        angle = 2 * pi * index / 8
        star(canvas, 34 + 12 * cos(angle), 16 + 12 * sin(angle), CYAN, 0.8, 0.7)
    particles(canvas, 2013, (0.55, 0.2, 1.0), 22, (4, 2, 60, 61))
    return canvas


ARMOR_DESIGNS = {
    10: ("helios-aegis", armor_helios, 32),
    11: ("hecates-veil", armor_hecate, 32),
    12: ("gaias-laurel", armor_gaia, 32),
    13: ("ares-eclipse", armor_ares, 24),
    14: ("olympian-apotheosis", armor_olympian, 24),
}

WEAPON_DESIGNS = {
    "warrior": ("ares-emberblade", weapon_ares),
    "champion": ("zeus-stormlance", weapon_zeus),
    "priest": ("apollo-radiance", weapon_apollo),
    "mage": ("hecates-aether", weapon_hecate),
}
