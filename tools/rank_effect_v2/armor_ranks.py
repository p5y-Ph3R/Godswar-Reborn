"""Role-aware AR10--AR14 mantle designs built from the coherent AR12 mesh.

The reviewed body effect has three independent slots.  Only slot 1 is
sculpted here: slot 0 remains the animated core and slot 2 remains the
animated rune.  Slot 1 is not one continuous model; it is forty disconnected
texture cards.  A rank design moves every card as a unit and applies only a
small local scale, preserving faces, UVs, colours, materials, and animation.
"""

from __future__ import annotations

from collections import Counter
from dataclasses import dataclass
import math
from types import MappingProxyType
from typing import Mapping

from xmodel_sculpt.mesh import MeshData, Vector3


ARMOR_SOURCE_RANK = 12
ARMOR_RANKS = (10, 11, 12, 13, 14)
SLOT_ROLES = ("animated-core", "outer-mantle", "animated-rune")

# Static structural facts measured from native AR12 slot 1 in both character
# roots and for both genders.  These fail closed if a different mesh is fed to
# the authoring transform.
EXPECTED_VERTICES = 848
EXPECTED_FACES = 768
EXPECTED_CARDS = 40
EXPECTED_CARD_SIZES = MappingProxyType({20: 16, 22: 24})
EXPECTED_BAND_COUNTS = MappingProxyType(
    {"anchor": 8, "inner": 8, "middle": 8, "upper": 8, "outer": 8}
)

Rgb = tuple[float, float, float]


@dataclass(frozen=True, slots=True)
class AtlasPalette:
    """Luminance-preserving colours for the native role atlas."""

    shadow: Rgb
    middle: Rgb
    highlight: Rgb
    strength: float = 0.88
    region: tuple[float, float, float, float] = (0.0, 1.0, 0.0, 0.52)


@dataclass(frozen=True, slots=True)
class CardPlacement:
    """Translation of a card centre without changing its texture mapping."""

    lift: float
    lateral_scale: float
    depth_shift: float = 0.0


@dataclass(frozen=True, slots=True)
class SilhouetteInvariant:
    """Permitted geometry change relative to native AR12 slot 1."""

    centroid_x_shift: tuple[float, float]
    lateral_span_ratio: tuple[float, float]
    depth_span_ratio: tuple[float, float]
    minimum_top_lift: float
    maximum_anchor_drift: float = 0.08
    maximum_lateral_centroid_drift: float = 0.01


@dataclass(frozen=True, slots=True)
class SilhouetteMetrics:
    centroid_x_shift: float
    lateral_span_ratio: float
    depth_span_ratio: float
    top_lift: float
    anchor_drift: float
    lateral_centroid_drift: float


@dataclass(frozen=True, slots=True)
class ArmorRankDesign:
    rank: int
    name: str
    intent: str
    palette: AtlasPalette
    placements: Mapping[str, CardPlacement]
    local_scale: Vector3
    invariant: SilhouetteInvariant


def _placements(
    anchor: tuple[float, float, float],
    inner: tuple[float, float, float],
    middle: tuple[float, float, float],
    upper: tuple[float, float, float],
    outer: tuple[float, float, float],
) -> Mapping[str, CardPlacement]:
    return MappingProxyType(
        {
            name: CardPlacement(*values)
            for name, values in (
                ("anchor", anchor),
                ("inner", inner),
                ("middle", middle),
                ("upper", upper),
                ("outer", outer),
            )
        }
    )


# The authored progression moves from solar blue/gold through violet, green,
# crimson, and celestial blue/gold. The native atlas supplies luminance, alpha,
# and semantic regions; these rank hues are deliberate v2 design choices.
_DESIGNS = {
    10: ArmorRankDesign(
        10,
        "Solar Aegis",
        "compact round shoulder-guard: broad rays, low crown",
        AtlasPalette((0.015, 0.045, 0.16), (0.08, 0.48, 0.96), (1.0, 0.80, 0.24)),
        _placements(
            (0.00, 1.02, 0.00),
            (0.04, 1.05, -0.02),
            (0.10, 1.12, 0.02),
            (0.06, 1.18, 0.00),
            (-0.06, 1.22, 0.06),
        ),
        (0.96, 0.97, 0.96),
        SilhouetteInvariant((0.02, 0.08), (1.07, 1.12), (0.93, 1.01), -0.10),
    ),
    11: ArmorRankDesign(
        11,
        "Aether Veil",
        "narrow ascending veil: the upper cards float above the shoulders",
        AtlasPalette((0.035, 0.008, 0.15), (0.46, 0.15, 0.94), (0.84, 0.92, 1.0)),
        _placements(
            (0.00, 1.00, 0.00),
            (0.18, 0.94, 0.08),
            (0.36, 0.90, 0.04),
            (0.52, 0.84, 0.12),
            (0.64, 0.80, 0.16),
        ),
        (0.92, 0.93, 1.02),
        SilhouetteInvariant((0.30, 0.42), (0.82, 0.96), (0.96, 1.02), 0.42),
    ),
    12: ArmorRankDesign(
        12,
        "Gaia Laurel",
        "open living wreath: grounded centre with outward laurel branches",
        AtlasPalette((0.008, 0.075, 0.025), (0.06, 0.64, 0.22), (0.72, 1.0, 0.34)),
        _placements(
            (0.00, 1.04, 0.02),
            (0.08, 1.10, 0.00),
            (0.20, 1.06, -0.03),
            (0.26, 1.12, -0.02),
            (0.15, 1.20, 0.02),
        ),
        (0.96, 0.98, 0.98),
        SilhouetteInvariant((0.10, 0.18), (1.07, 1.11), (0.96, 1.03), 0.12),
    ),
    13: ArmorRankDesign(
        13,
        "Ares War Mantle",
        "wide angular war mantle: increasingly aggressive outer shoulders",
        AtlasPalette((0.12, 0.004, 0.006), (0.86, 0.025, 0.018), (1.0, 0.62, 0.10)),
        _placements(
            (0.00, 1.08, 0.00),
            (0.10, 1.14, -0.03),
            (0.16, 1.24, -0.02),
            (0.24, 1.32, 0.00),
            (0.32, 1.40, 0.04),
        ),
        (0.98, 1.02, 0.94),
        SilhouetteInvariant((0.13, 0.22), (1.18, 1.24), (0.92, 1.00), 0.20),
    ),
    14: ArmorRankDesign(
        14,
        "Olympian Plume",
        "cap-rank crown: tall inner plumes above a stable outer frame",
        AtlasPalette((0.025, 0.08, 0.22), (0.28, 0.72, 0.92), (1.0, 0.94, 0.72)),
        _placements(
            (0.00, 1.10, 0.00),
            (0.70, 0.95, 0.00),
            (0.30, 1.05, 0.00),
            (0.55, 0.90, 0.00),
            (0.10, 1.12, 0.00),
        ),
        (0.94, 0.94, 0.98),
        SilhouetteInvariant((0.25, 0.40), (1.01, 1.10), (0.96, 1.04), 0.30),
    ),
}

ARMOR_RANK_DESIGNS: Mapping[int, ArmorRankDesign] = MappingProxyType(_DESIGNS)


def design_for_rank(rank: int) -> ArmorRankDesign:
    try:
        return ARMOR_RANK_DESIGNS[rank]
    except KeyError as error:
        raise ValueError(f"Role-aware armor rank must be AR10..AR14, got {rank}") from error


def _cards(mesh: MeshData) -> tuple[tuple[int, ...], ...]:
    if len(mesh.vertices) != EXPECTED_VERTICES or len(mesh.faces) != EXPECTED_FACES:
        raise ValueError("Armor source is not the reviewed native AR12 mantle")
    adjacency = [set() for _ in mesh.vertices]
    for face in mesh.faces:
        for left, right in zip(face, face[1:] + face[:1]):
            adjacency[left].add(right)
            adjacency[right].add(left)
    found: list[tuple[int, ...]] = []
    visited: set[int] = set()
    for start in range(len(mesh.vertices)):
        if start in visited:
            continue
        pending = [start]
        visited.add(start)
        component: list[int] = []
        while pending:
            current = pending.pop()
            component.append(current)
            for neighbour in adjacency[current]:
                if neighbour not in visited:
                    visited.add(neighbour)
                    pending.append(neighbour)
        found.append(tuple(sorted(component)))
    if len(found) != EXPECTED_CARDS:
        raise ValueError("AR12 mantle no longer has the reviewed 40-card topology")
    if Counter(map(len, found)) != Counter(EXPECTED_CARD_SIZES):
        raise ValueError("AR12 mantle card sizes changed")
    return tuple(found)


def _card_band(center: Vector3) -> str:
    x, y, _z = center
    if x <= 0.10:
        return "anchor"
    if x <= 0.35:
        return "inner"
    if x <= 0.70:
        return "middle"
    if abs(y) <= 0.90:
        return "upper"
    return "outer"


class ArmorMantleTransform:
    """Topology-preserving slot-1 transform suitable for ``sculpt_xof_mszip``."""

    def __init__(self, design: int | ArmorRankDesign) -> None:
        self.design = design_for_rank(design) if isinstance(design, int) else design
        self._mesh: MeshData | None = None
        self._outputs: tuple[Vector3, ...] = ()

    def _plan(self, mesh: MeshData) -> tuple[Vector3, ...]:
        cards = _cards(mesh)
        centers: list[Vector3] = []
        vertex_card = [-1] * len(mesh.vertices)
        bands: list[str] = []
        for card_index, card in enumerate(cards):
            center = tuple(
                sum(mesh.vertices[index][axis] for index in card) / len(card)
                for axis in range(3)
            )
            centers.append(center)  # type: ignore[arg-type]
            bands.append(_card_band(center))  # type: ignore[arg-type]
            for vertex in card:
                vertex_card[vertex] = card_index
        if Counter(bands) != Counter(EXPECTED_BAND_COUNTS):
            raise ValueError("AR12 mantle card bands changed")

        scale = self.design.local_scale
        output: list[Vector3] = []
        for index, point in enumerate(mesh.vertices):
            card_index = vertex_card[index]
            center = centers[card_index]
            placement = self.design.placements[bands[card_index]]
            target = (
                center[0] + placement.lift,
                center[1] * placement.lateral_scale,
                center[2] + placement.depth_shift,
            )
            local = tuple(point[axis] - center[axis] for axis in range(3))
            output.append(
                tuple(target[axis] + local[axis] * scale[axis] for axis in range(3))
            )
        return tuple(output)  # type: ignore[return-value]

    def __call__(self, mesh: MeshData, index: int, _point: Vector3) -> Vector3:
        if mesh is not self._mesh:
            self._outputs = self._plan(mesh)
            self._mesh = mesh
        return self._outputs[index]


def measure_silhouette(
    source: MeshData, output: tuple[Vector3, ...]
) -> SilhouetteMetrics:
    """Return normalized metrics used by package and focused tests."""

    if len(output) != len(source.vertices):
        raise ValueError("Transformed mantle changed the vertex count")

    def bounds(points: tuple[Vector3, ...]) -> tuple[Vector3, Vector3]:
        low = tuple(min(point[axis] for point in points) for axis in range(3))
        high = tuple(max(point[axis] for point in points) for axis in range(3))
        return low, high  # type: ignore[return-value]

    source_points = tuple(source.vertices)
    source_low, source_high = bounds(source_points)
    output_low, output_high = bounds(output)
    source_centroid = tuple(
        sum(point[axis] for point in source_points) / len(source_points)
        for axis in range(3)
    )
    output_centroid = tuple(
        sum(point[axis] for point in output) / len(output) for axis in range(3)
    )
    anchor_drifts: list[float] = []
    for card in _cards(source):
        source_center = tuple(
            sum(source_points[index][axis] for index in card) / len(card)
            for axis in range(3)
        )
        if _card_band(source_center) != "anchor":  # type: ignore[arg-type]
            continue
        output_center = tuple(
            sum(output[index][axis] for index in card) / len(card)
            for axis in range(3)
        )
        anchor_drifts.append(
            math.sqrt(
                sum(
                    (output_center[axis] - source_center[axis]) ** 2
                    for axis in range(3)
                )
            )
        )
    if len(anchor_drifts) != EXPECTED_BAND_COUNTS["anchor"]:
        raise ValueError("AR12 mantle anchor-card set changed")
    return SilhouetteMetrics(
        output_centroid[0] - source_centroid[0],
        (output_high[1] - output_low[1]) / (source_high[1] - source_low[1]),
        (output_high[2] - output_low[2]) / (source_high[2] - source_low[2]),
        output_high[0] - source_high[0],
        max(anchor_drifts),
        abs(output_centroid[1] - source_centroid[1]),
    )


def validate_silhouette(
    source: MeshData,
    output: tuple[Vector3, ...],
    design: int | ArmorRankDesign,
) -> SilhouetteMetrics:
    """Enforce the reviewed numerical envelope for one authored rank."""

    selected = design_for_rank(design) if isinstance(design, int) else design
    metrics = measure_silhouette(source, output)
    invariant = selected.invariant
    checks = (
        (
            invariant.centroid_x_shift[0]
            <= metrics.centroid_x_shift
            <= invariant.centroid_x_shift[1],
            "vertical centroid shift",
        ),
        (
            invariant.lateral_span_ratio[0]
            <= metrics.lateral_span_ratio
            <= invariant.lateral_span_ratio[1],
            "lateral span ratio",
        ),
        (
            invariant.depth_span_ratio[0]
            <= metrics.depth_span_ratio
            <= invariant.depth_span_ratio[1],
            "depth span ratio",
        ),
        (metrics.top_lift >= invariant.minimum_top_lift, "top lift"),
        (metrics.anchor_drift <= invariant.maximum_anchor_drift, "anchor drift"),
        (
            metrics.lateral_centroid_drift
            <= invariant.maximum_lateral_centroid_drift,
            "lateral symmetry",
        ),
    )
    failed = [name for valid, name in checks if not valid]
    if failed:
        raise ValueError(f"AR{selected.rank} silhouette invariant failed: {', '.join(failed)}")
    return metrics


def validate_design_catalogue() -> None:
    """Fail early on incomplete or unsafe authoring constants."""

    if tuple(ARMOR_RANK_DESIGNS) != ARMOR_RANKS:
        raise ValueError("Armor design catalogue must cover AR10 through AR14")
    for rank, design in ARMOR_RANK_DESIGNS.items():
        if rank != design.rank or set(design.placements) != set(EXPECTED_BAND_COUNTS):
            raise ValueError(f"Incomplete role-aware armor design: AR{rank}")
        colours = (*design.palette.shadow, *design.palette.middle, *design.palette.highlight)
        if any(not math.isfinite(channel) or channel < 0.0 or channel > 1.0 for channel in colours):
            raise ValueError(f"AR{rank} palette channel is outside 0..1")
        if not math.isfinite(design.palette.strength) or not (
            0.5 <= design.palette.strength <= 1.0
        ):
            raise ValueError(f"AR{rank} palette strength is unsafe")
        minimum_u, maximum_u, minimum_v, maximum_v = design.palette.region
        if (
            any(not math.isfinite(value) for value in design.palette.region)
            or not 0.0 <= minimum_u <= maximum_u <= 1.0
            or not 0.0 <= minimum_v <= maximum_v <= 1.0
        ):
            raise ValueError(f"AR{rank} palette region is invalid")
        if any(
            not math.isfinite(scale) or scale < 0.90 or scale > 1.05
            for scale in design.local_scale
        ):
            raise ValueError(f"AR{rank} local card scale is too destructive")
        for placement in design.placements.values():
            values = (placement.lift, placement.lateral_scale, placement.depth_shift)
            if any(not math.isfinite(value) for value in values):
                raise ValueError(f"AR{rank} card placement is not finite")


validate_design_catalogue()
