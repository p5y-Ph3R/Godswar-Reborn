"""Role audit and conservative mantle sculpting for prototype JCS models."""

from __future__ import annotations

from dataclasses import dataclass

from erebus_lion.model_codec import expand_xof_mszip
from rank_effect_packages.formats import extract_texture_references
from xmodel_sculpt.binary_x import (
    TOKEN_FLOAT_LIST,
    TOKEN_INTEGER_LIST,
    TOKEN_NAME,
    float_list,
    integer_list,
    parse_tokens,
)
from xmodel_sculpt.mesh import MeshData, Vector3, discover_meshes


@dataclass(frozen=True, slots=True)
class ModelAudit:
    vertices: int
    faces: int
    material_face_counts: tuple[int, ...]
    uv_bounds: tuple[float, float, float, float]
    animation_keys: int
    texture_references: tuple[str, ...]
    bounds: tuple[Vector3, Vector3]
    centroid: Vector3


def _safe_reference(value: bytes) -> str:
    try:
        return "ascii:" + value.decode("ascii")
    except UnicodeDecodeError:
        return "hex:" + value.hex()


def audit_model(encoded: bytes, label: str) -> ModelAudit:
    expanded = expand_xof_mszip(encoded, label)
    tokens = parse_tokens(expanded)
    meshes = discover_meshes(expanded, tokens)
    if len(meshes) != 1:
        raise ValueError(f"Prototype JCS must contain one Mesh: {label}")
    mesh = meshes[0]
    material_counts: tuple[int, ...] | None = None
    uv_bounds: tuple[float, float, float, float] | None = None
    animations = 0
    for index, token in enumerate(tokens):
        if token.kind != TOKEN_NAME:
            continue
        if token.value == b"AnimationKey":
            animations += 1
        elif token.value == b"MeshMaterialList":
            entry = next(
                item
                for item in tokens[index : index + 10]
                if item.kind == TOKEN_INTEGER_LIST
            )
            values = integer_list(expanded, entry)
            material_count, face_count = values[:2]
            indexes = values[2:]
            if len(indexes) != face_count:
                raise ValueError(f"Bad material face list: {label}")
            material_counts = tuple(indexes.count(value) for value in range(material_count))
        elif token.value == b"MeshTextureCoords":
            entry = next(
                item
                for item in tokens[index : index + 10]
                if item.kind == TOKEN_FLOAT_LIST
            )
            values = float_list(expanded, entry)
            u_values, v_values = values[0::2], values[1::2]
            uv_bounds = (
                min(u_values), max(u_values), min(v_values), max(v_values)
            )
    if material_counts is None or uv_bounds is None:
        raise ValueError(f"Prototype JCS has no material/UV contract: {label}")
    return ModelAudit(
        len(mesh.vertices),
        len(mesh.faces),
        material_counts,
        uv_bounds,
        animations,
        tuple(_safe_reference(value) for value in extract_texture_references(encoded, label)),
        mesh.bounds,
        tuple(
            sum(point[axis] for point in mesh.vertices) / len(mesh.vertices)
            for axis in range(3)
        ),  # type: ignore[arg-type]
    )


class MantleTransform:
    """Re-space intact AR12 mesh cards into a taller Olympian mantle.

    Body-effect coordinates use X vertically, Y across the character, and Z
    for depth.  Moving each disconnected card as one unit avoids stretching
    its sampled atlas detail.  The low anchor stays in place while the inner
    cards rise into a visibly different cap-rank plume silhouette.
    """

    def __init__(self) -> None:
        self._outputs: dict[int, tuple[Vector3, ...]] = {}

    @staticmethod
    def _plan(mesh: MeshData) -> tuple[Vector3, ...]:
        adjacency = [set() for _ in mesh.vertices]
        for face in mesh.faces:
            for left, right in zip(face, face[1:] + face[:1]):
                adjacency[left].add(right)
                adjacency[right].add(left)

        components: list[list[int]] = []
        vertex_component: dict[int, int] = {}
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
            component_index = len(components)
            components.append(component)
            for vertex in component:
                vertex_component[vertex] = component_index

        if len(components) != 40 or len(vertex_component) != len(mesh.vertices):
            raise ValueError("AR12 mantle no longer has the reviewed 40-card layout")

        centers: list[Vector3] = []
        targets: list[Vector3] = []
        for component in components:
            center = tuple(
                sum(mesh.vertices[index][axis] for index in component) / len(component)
                for axis in range(3)
            )
            centers.append(center)  # type: ignore[arg-type]
            x, y, z = center
            radial = abs(y)
            if x > 0.70 and radial > 0.90:
                lift, width = 0.10, 1.12  # outer upper plume
            elif x > 0.70:
                lift, width = 0.55, 0.90  # tall inner plume
            elif x > 0.35:
                lift, width = 0.30, 1.05  # middle plume
            elif x > 0.10:
                lift, width = 0.70, 0.95  # highest inner crest
            else:
                lift, width = 0.0, 1.10  # keep the low attachment anchored
            targets.append((x + lift, y * width, z))

        output: list[Vector3] = []
        for index, point in enumerate(mesh.vertices):
            component = vertex_component[index]
            center = centers[component]
            target = targets[component]
            local = tuple(point[axis] - center[axis] for axis in range(3))
            output.append(
                (
                    target[0] + local[0] * 0.94,
                    target[1] + local[1] * 0.94,
                    target[2] + local[2] * 0.98,
                )
            )
        return tuple(output)

    def __call__(self, mesh: MeshData, index: int, _point: Vector3) -> Vector3:
        outputs = self._outputs.get(mesh.index)
        if outputs is None:
            outputs = self._plan(mesh)
            self._outputs[mesh.index] = outputs
        return outputs[index]
