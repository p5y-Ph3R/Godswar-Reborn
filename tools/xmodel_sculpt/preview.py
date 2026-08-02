"""Dependency-free orthographic SVG previews for mesh comparisons."""

from __future__ import annotations

from html import escape
from pathlib import Path

from .mesh import MeshData, Vector3
from .profiles import MeshBasis, pca_basis


def _dot(left: Vector3, right: Vector3) -> float:
    return sum(a * b for a, b in zip(left, right))


def _sub(left: Vector3, right: Vector3) -> Vector3:
    return tuple(a - b for a, b in zip(left, right))  # type: ignore[return-value]


def _project(point: Vector3, basis: MeshBasis, side: bool) -> tuple[float, float]:
    centered = _sub(point, basis.origin)
    horizontal = basis.thickness if side else basis.width
    return _dot(centered, horizontal), _dot(centered, basis.longitudinal)


def _project_meshes(
    meshes: tuple[MeshData, ...],
    bases: tuple[MeshBasis, ...],
    side: bool,
) -> tuple[tuple[tuple[float, float], ...], ...]:
    return tuple(
        tuple(_project(point, basis, side) for point in mesh.vertices)
        for mesh, basis in zip(meshes, bases)
    )


def _bounds(
    groups: tuple[tuple[tuple[float, float], ...], ...],
) -> tuple[float, float, float, float]:
    points = [point for group in groups for point in group]
    if not points:
        return -1.0, 1.0, -1.0, 1.0
    return (
        min(point[0] for point in points),
        max(point[0] for point in points),
        min(point[1] for point in points),
        max(point[1] for point in points),
    )


def _panel_paths(
    meshes: tuple[MeshData, ...],
    points: tuple[tuple[tuple[float, float], ...], ...],
    bounds: tuple[float, float, float, float],
    x: float,
    y: float,
    width: float,
    height: float,
    colour: str,
) -> str:
    minimum_x, maximum_x, minimum_y, maximum_y = bounds
    span_x = max(maximum_x - minimum_x, 1e-8)
    span_y = max(maximum_y - minimum_y, 1e-8)
    scale = min((width - 40.0) / span_x, (height - 40.0) / span_y)
    center_x = (minimum_x + maximum_x) * 0.5
    center_y = (minimum_y + maximum_y) * 0.5

    def screen(point: tuple[float, float]) -> tuple[float, float]:
        return (
            x + width * 0.5 + (point[0] - center_x) * scale,
            y + height * 0.5 - (point[1] - center_y) * scale,
        )

    output: list[str] = []
    for mesh, mesh_points in zip(meshes, points):
        for face in mesh.faces:
            coordinates = [screen(mesh_points[index]) for index in face]
            commands = " ".join(
                ("M" if index == 0 else "L") + f"{px:.2f},{py:.2f}"
                for index, (px, py) in enumerate(coordinates)
            )
            output.append(
                f'<path d="{commands} Z" fill="none" stroke="{colour}" '
                'stroke-width="1" stroke-linejoin="round"/>'
            )
    return "\n".join(output)


def comparison_svg(
    before: tuple[MeshData, ...],
    after: tuple[MeshData, ...],
    title: str,
) -> str:
    if len(before) != len(after):
        raise ValueError("Preview Mesh counts differ")
    bases = tuple(pca_basis(mesh) for mesh in before)
    front_before = _project_meshes(before, bases, False)
    front_after = _project_meshes(after, bases, False)
    side_before = _project_meshes(before, bases, True)
    side_after = _project_meshes(after, bases, True)
    front_bounds = _bounds(front_before + front_after)
    side_bounds = _bounds(side_before + side_after)
    panels = (
        (before, front_before, front_bounds, 20, 80, "BEFORE — FRONT", "#8290a6"),
        (after, front_after, front_bounds, 620, 80, "AFTER — FRONT", "#efb643"),
        (before, side_before, side_bounds, 20, 490, "BEFORE — SIDE", "#8290a6"),
        (after, side_after, side_bounds, 620, 490, "AFTER — SIDE", "#efb643"),
    )
    body: list[str] = []
    for meshes, points, bounds, x, y, label, colour in panels:
        body.append(
            f'<rect x="{x}" y="{y}" width="560" height="360" rx="8" '
            'fill="#111722" stroke="#334157"/>'
        )
        body.append(
            f'<text x="{x + 18}" y="{y + 28}" fill="#dce5f2" '
            f'font-size="18" font-family="Segoe UI, sans-serif">{escape(label)}</text>'
        )
        body.append(
            _panel_paths(meshes, points, bounds, x, y, 560, 360, colour)
        )
    return (
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        '<svg xmlns="http://www.w3.org/2000/svg" width="1200" height="870" '
        'viewBox="0 0 1200 870">\n'
        '<rect width="1200" height="870" fill="#090d14"/>\n'
        f'<text x="24" y="42" fill="#f3f6fb" font-size="26" '
        f'font-family="Segoe UI, sans-serif">{escape(title)}</text>\n'
        + "\n".join(body)
        + "\n</svg>\n"
    )


def write_comparison_svg(
    path: Path,
    before: tuple[MeshData, ...],
    after: tuple[MeshData, ...],
    title: str,
) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(comparison_svg(before, after, title), encoding="utf-8", newline="\n")
