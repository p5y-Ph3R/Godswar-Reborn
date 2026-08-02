"""Deterministic Class Suit IV weapon silhouette profiles."""

from __future__ import annotations

from dataclasses import dataclass
import math

from .binary_x import XModelError
from .mesh import MeshData, Vector3


@dataclass(frozen=True, slots=True)
class SculptProfile:
    item_id: int
    name: str
    preserve_until: float
    blend_until: float
    width_keys: tuple[tuple[float, float], ...]
    thickness_keys: tuple[tuple[float, float], ...]
    tip_extension: float


@dataclass(frozen=True, slots=True)
class MeshBasis:
    origin: Vector3
    longitudinal: Vector3
    width: Vector3
    thickness: Vector3
    minimum_l: float
    span_l: float
    maximum_radius: float


PROFILES: dict[int, SculptProfile] = {
    1035: SculptProfile(
        1035,
        "ares_winged_falcata",
        0.32,
        0.42,
        (
            (0.42, 1.10),
            (0.50, 0.72),
            (0.60, 0.88),
            (0.68, 0.74),
            (0.78, 1.12),
            (0.88, 1.38),
            (0.96, 0.80),
            (1.0, 0.15),
        ),
        (
            (0.42, 1.08),
            (0.55, 1.12),
            (0.72, 1.08),
            (0.88, 1.12),
            (1.0, 0.55),
        ),
        0.08,
    ),
    1435: SculptProfile(
        1435,
        "celestial_spear",
        0.50,
        0.61,
        ((0.61, 1.65), (0.72, 0.82), (0.84, 1.75), (0.94, 0.72), (1.0, 0.10)),
        ((0.61, 1.05), (0.72, 0.92), (0.84, 1.20), (0.94, 0.85), (1.0, 0.35)),
        0.10,
    ),
    1735: SculptProfile(
        1735,
        "divine_scepter",
        0.54,
        0.64,
        ((0.64, 0.82), (0.78, 1.45), (0.89, 1.85), (1.0, 0.55)),
        ((0.64, 1.0), (0.78, 0.82), (0.89, 0.82), (1.0, 0.82)),
        0.06,
    ),
    1835: SculptProfile(
        1835,
        "astral_wand",
        0.58,
        0.69,
        ((0.69, 1.20), (0.80, 1.75), (0.91, 1.45), (1.0, 0.42)),
        ((0.69, 1.0), (0.80, 0.80), (0.91, 0.80), (1.0, 0.80)),
        0.07,
    ),
}


def _dot(left: Vector3, right: Vector3) -> float:
    return sum(a * b for a, b in zip(left, right))


def _add(left: Vector3, right: Vector3) -> Vector3:
    return tuple(a + b for a, b in zip(left, right))  # type: ignore[return-value]


def _scale(value: Vector3, factor: float) -> Vector3:
    return tuple(component * factor for component in value)  # type: ignore[return-value]


def _sub(left: Vector3, right: Vector3) -> Vector3:
    return tuple(a - b for a, b in zip(left, right))  # type: ignore[return-value]


def _cross(left: Vector3, right: Vector3) -> Vector3:
    return (
        left[1] * right[2] - left[2] * right[1],
        left[2] * right[0] - left[0] * right[2],
        left[0] * right[1] - left[1] * right[0],
    )


def _normalise(value: Vector3) -> Vector3:
    length = math.sqrt(_dot(value, value))
    if length < 1e-12:
        raise XModelError("Weapon mesh has a degenerate PCA axis")
    return _scale(value, 1.0 / length)


def _mat_vec(matrix: tuple[Vector3, Vector3, Vector3], value: Vector3) -> Vector3:
    return tuple(_dot(row, value) for row in matrix)  # type: ignore[return-value]


def _project_perpendicular(value: Vector3, axis: Vector3) -> Vector3:
    return _sub(value, _scale(axis, _dot(value, axis)))


def _power_axis(
    matrix: tuple[Vector3, Vector3, Vector3],
    seed: Vector3,
    perpendicular_to: Vector3 | None = None,
) -> Vector3:
    value = seed
    if perpendicular_to is not None:
        value = _project_perpendicular(value, perpendicular_to)
    value = _normalise(value)
    for _ in range(64):
        candidate = _mat_vec(matrix, value)
        if perpendicular_to is not None:
            candidate = _project_perpendicular(candidate, perpendicular_to)
        if _dot(candidate, candidate) < 1e-20:
            break
        candidate = _normalise(candidate)
        if _dot(candidate, value) < 0:
            candidate = _scale(candidate, -1.0)
        value = candidate
    return value


def pca_basis(mesh: MeshData) -> MeshBasis:
    if len(mesh.vertices) < 3:
        raise XModelError("Weapon Mesh needs at least three vertices")
    count = float(len(mesh.vertices))
    origin = tuple(
        sum(point[axis] for point in mesh.vertices) / count
        for axis in range(3)
    )
    centered = tuple(_sub(point, origin) for point in mesh.vertices)
    covariance = tuple(
        tuple(
            sum(point[row] * point[column] for point in centered) / count
            for column in range(3)
        )
        for row in range(3)
    )
    longitudinal = _power_axis(covariance, (0.0, 1.0, 0.0))
    if _dot(longitudinal, (0.0, 1.0, 0.0)) < 0:
        longitudinal = _scale(longitudinal, -1.0)
    width_seed = _project_perpendicular((1.0, 0.0, 0.0), longitudinal)
    if _dot(width_seed, width_seed) < 1e-12:
        width_seed = _project_perpendicular((0.0, 0.0, 1.0), longitudinal)
    width = _power_axis(covariance, width_seed, longitudinal)
    if _dot(width, (1.0, 0.0, 0.0)) < 0:
        width = _scale(width, -1.0)
    # With L approximately +Y and W approximately +X, W x L is +Z.
    thickness = _normalise(_cross(width, longitudinal))
    if _dot(thickness, (0.0, 0.0, 1.0)) < 0:
        thickness = _scale(thickness, -1.0)

    local = tuple(
        (
            _dot(point, width),
            _dot(point, longitudinal),
            _dot(point, thickness),
        )
        for point in centered
    )
    minimum_l = min(point[1] for point in local)
    maximum_l = max(point[1] for point in local)
    span_l = maximum_l - minimum_l
    if span_l < 1e-8:
        raise XModelError("Weapon Mesh has no usable longitudinal span")
    maximum_radius = max(math.hypot(point[0], point[2]) for point in local)
    return MeshBasis(
        origin=origin,
        longitudinal=longitudinal,
        width=width,
        thickness=thickness,
        minimum_l=minimum_l,
        span_l=span_l,
        maximum_radius=maximum_radius,
    )


def _smoothstep(start: float, end: float, value: float) -> float:
    if end <= start:
        return 1.0 if value >= end else 0.0
    ratio = min(1.0, max(0.0, (value - start) / (end - start)))
    return ratio * ratio * (3.0 - 2.0 * ratio)


def _key_value(keys: tuple[tuple[float, float], ...], value: float) -> float:
    if value <= keys[0][0]:
        return keys[0][1]
    for (left_t, left_value), (right_t, right_value) in zip(keys, keys[1:]):
        if value <= right_t:
            ratio = (value - left_t) / (right_t - left_t)
            return left_value + (right_value - left_value) * ratio
    return keys[-1][1]


_WARRIOR_CURVE_KEYS = (
    (0.42, 0.0),
    (0.52, 0.018),
    (0.62, 0.055),
    (0.72, 0.095),
    (0.84, 0.130),
    (0.93, 0.145),
    (1.0, 0.120),
)

_WARRIOR_SPINE_KEYS = (
    (0.42, 1.0),
    (0.54, 0.92),
    (0.62, 0.28),
    (0.70, 0.72),
    (0.80, 0.82),
    (0.90, 0.70),
    (0.97, 0.50),
    (1.0, 0.15),
)

_WARRIOR_EDGE_KEYS = (
    (0.42, 1.0),
    (0.54, 1.05),
    (0.62, 1.15),
    (0.72, 1.35),
    (0.84, 1.65),
    (0.92, 1.35),
    (1.0, 0.15),
)


def _warrior_falcata(
    width_value: float,
    longitudinal_value: float,
    thickness_value: float,
    t: float,
    basis: MeshBasis,
) -> tuple[float, float, float]:
    """Curve one edge around a notched spine and sweep the guard wings."""

    source_side = -1.0 if width_value < 0.0 else 1.0
    if t >= 0.42:
        side_scale = _key_value(
            _WARRIOR_SPINE_KEYS if source_side < 0.0 else _WARRIOR_EDGE_KEYS,
            t,
        )
        width_value *= side_scale
        width_value += _key_value(_WARRIOR_CURVE_KEYS, t) * basis.span_l

    # The two outer guard wings sweep in opposite longitudinal directions.
    # This changes their silhouette without touching the attachment/grip below.
    guard_window = _smoothstep(0.32, 0.37, t) * (
        1.0 - _smoothstep(0.40, 0.44, t)
    )
    radius = max(basis.maximum_radius, 1e-8)
    outer_weight = min(1.0, abs(width_value) / (radius * 0.55))
    longitudinal_value += (
        source_side
        * guard_window
        * outer_weight
        * 0.055
        * basis.span_l
    )

    # A slight face turn keeps the new outline readable from normal gameplay
    # cameras; this is intentionally subtle rather than a thickness increase.
    angle = math.radians(6.0) * _smoothstep(0.42, 0.72, t)
    cosine, sine = math.cos(angle), math.sin(angle)
    width_value, thickness_value = (
        width_value * cosine - thickness_value * sine,
        width_value * sine + thickness_value * cosine,
    )
    return width_value, longitudinal_value, thickness_value


class ProfileTransform:
    """Callable position transform with one cached PCA basis per Mesh."""

    def __init__(self, profile: SculptProfile):
        self.profile = profile
        self._bases: dict[int, MeshBasis] = {}

    def __call__(self, mesh: MeshData, _index: int, point: Vector3) -> Vector3:
        basis = self._bases.get(mesh.index)
        if basis is None:
            basis = pca_basis(mesh)
            self._bases[mesh.index] = basis
        centered = _sub(point, basis.origin)
        width_value = _dot(centered, basis.width)
        l_value = _dot(centered, basis.longitudinal)
        thickness_value = _dot(centered, basis.thickness)
        t = (l_value - basis.minimum_l) / basis.span_l
        if t <= self.profile.preserve_until:
            return point

        blend = _smoothstep(
            self.profile.preserve_until,
            self.profile.blend_until,
            t,
        )
        width_scale = 1.0 + blend * (
            _key_value(self.profile.width_keys, t) - 1.0
        )
        thickness_scale = 1.0 + blend * (
            _key_value(self.profile.thickness_keys, t) - 1.0
        )

        if self.profile.item_id == 1435 and t <= 0.72:
            # Do not hollow or bloat vertices forming the spear's central shaft.
            if math.hypot(width_value, thickness_value) <= basis.maximum_radius * 0.18:
                width_scale = 1.0
                thickness_scale = 1.0

        width_value *= width_scale
        thickness_value *= thickness_scale

        if self.profile.item_id == 1035:
            width_value, l_value, thickness_value = _warrior_falcata(
                width_value,
                l_value,
                thickness_value,
                t,
                basis,
            )
        elif self.profile.item_id == 1735:
            angle = math.radians(18.0) * _smoothstep(0.64, 1.0, t)
            cosine, sine = math.cos(angle), math.sin(angle)
            width_value, thickness_value = (
                width_value * cosine - thickness_value * sine,
                width_value * sine + thickness_value * cosine,
            )
        elif self.profile.item_id == 1835:
            rise = _smoothstep(0.62, 0.82, t)
            fall = 1.0 - _smoothstep(0.93, 1.0, t)
            width_value += 0.12 * basis.span_l * rise * fall

        extension_start = max(self.profile.blend_until, 0.76)
        l_value += (
            self.profile.tip_extension
            * basis.span_l
            * _smoothstep(extension_start, 1.0, t)
        )
        output = basis.origin
        output = _add(output, _scale(basis.width, width_value))
        output = _add(output, _scale(basis.longitudinal, l_value))
        output = _add(output, _scale(basis.thickness, thickness_value))
        return output


def profile_transform(item_id: int) -> ProfileTransform:
    try:
        return ProfileTransform(PROFILES[item_id])
    except KeyError as error:
        raise XModelError(f"No Class Suit IV sculpt profile for item {item_id}") from error
