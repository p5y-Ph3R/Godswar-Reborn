"""Byte-preserving mesh-coordinate sculpting and invariant validation."""

from __future__ import annotations

from dataclasses import dataclass
import hashlib
import math
import struct
from typing import Callable

from erebus_lion.model_codec import compress_xof_mszip, expand_xof_mszip

from .binary_x import Token, XModelError, parse_tokens
from .mesh import Face, MeshData, Vector3, discover_meshes


VertexTransform = Callable[[MeshData, int, Vector3], Vector3]


@dataclass(frozen=True, slots=True)
class SculptResult:
    encoded: bytes
    expanded: bytes
    before: tuple[MeshData, ...]
    after: tuple[MeshData, ...]
    changed_vertices: int
    source_sha256: str
    result_sha256: str


def _sub(left: Vector3, right: Vector3) -> Vector3:
    return (
        left[0] - right[0],
        left[1] - right[1],
        left[2] - right[2],
    )


def _cross(left: Vector3, right: Vector3) -> Vector3:
    return (
        left[1] * right[2] - left[2] * right[1],
        left[2] * right[0] - left[0] * right[2],
        left[0] * right[1] - left[1] * right[0],
    )


def _add(left: Vector3, right: Vector3) -> Vector3:
    return (
        left[0] + right[0],
        left[1] + right[1],
        left[2] + right[2],
    )


def _normalise(value: Vector3, fallback: Vector3) -> Vector3:
    length = math.sqrt(sum(component * component for component in value))
    if length <= 1e-12:
        return fallback
    return tuple(component / length for component in value)  # type: ignore[return-value]


def recompute_normals(
    vertices: tuple[Vector3, ...],
    faces: tuple[Face, ...],
    normal_faces: tuple[Face, ...],
    existing: tuple[Vector3, ...],
) -> tuple[Vector3, ...]:
    """Recompute values while retaining the original normal-index topology."""

    if not existing:
        return ()
    if len(faces) != len(normal_faces):
        raise XModelError("Cannot recompute normals with mismatched face lists")
    totals: list[Vector3] = [(0.0, 0.0, 0.0) for _ in existing]
    for face_index, (face, normal_face) in enumerate(zip(faces, normal_faces)):
        if len(face) != len(normal_face):
            raise XModelError(f"Normal face {face_index} width does not match its mesh face")
        origin = vertices[face[0]]
        area_vector = (0.0, 0.0, 0.0)
        for corner in range(1, len(face) - 1):
            edge_a = _sub(vertices[face[corner]], origin)
            edge_b = _sub(vertices[face[corner + 1]], origin)
            area_vector = _add(area_vector, _cross(edge_a, edge_b))
        for normal_index in set(normal_face):
            totals[normal_index] = _add(totals[normal_index], area_vector)
    return tuple(
        _normalise(total, existing[index])
        for index, total in enumerate(totals)
    )


def _write_vectors(target: bytearray, token: Token, vectors: tuple[Vector3, ...]) -> None:
    if token.payload_offset is None or token.item_count != len(vectors) * 3:
        raise XModelError("Vector token shape changed while sculpting")
    cursor = token.payload_offset
    for vector in vectors:
        if any(not math.isfinite(value) or abs(value) > 100_000.0 for value in vector):
            raise XModelError("Sculpt transform produced an invalid coordinate")
        struct.pack_into("<3f", target, cursor, *vector)
        cursor += 12


def _mutable_ranges(meshes: tuple[MeshData, ...]) -> tuple[tuple[int, int], ...]:
    ranges: list[tuple[int, int]] = []
    for mesh in meshes:
        for token in (mesh.position_token, mesh.normal_token):
            if token is None or token.payload_offset is None:
                continue
            ranges.append((token.payload_offset, token.payload_offset + token.item_count * 4))
    return tuple(sorted(ranges))


def immutable_sha256(data: bytes, meshes: tuple[MeshData, ...]) -> str:
    """Hash every byte except coordinate and normal float payloads."""

    digest = hashlib.sha256()
    cursor = 0
    for start, end in _mutable_ranges(meshes):
        if start < cursor:
            raise XModelError("Mutable Mesh ranges overlap")
        digest.update(data[cursor:start])
        digest.update(struct.pack("<QQ", start, end - start))
        cursor = end
    digest.update(data[cursor:])
    return digest.hexdigest()


def _mesh_structure(mesh: MeshData) -> tuple[object, ...]:
    return (
        mesh.index,
        mesh.name,
        len(mesh.vertices),
        mesh.faces,
        len(mesh.normals),
        mesh.normal_faces,
        mesh.position_token.start,
        mesh.position_token.end,
        mesh.face_token.start,
        mesh.face_token.end,
        None if mesh.normal_token is None else mesh.normal_token.start,
        None if mesh.normal_token is None else mesh.normal_token.end,
    )


def assert_sculpt_invariants(
    source: bytes,
    result: bytes,
    source_tokens: tuple[Token, ...],
    result_tokens: tuple[Token, ...],
    before: tuple[MeshData, ...],
    after: tuple[MeshData, ...],
) -> None:
    """Prove that only positions and normal values changed."""

    if len(source) != len(result):
        raise XModelError("Sculpting changed the expanded model length")
    if source_tokens != result_tokens:
        raise XModelError("Sculpting changed binary X token boundaries or metadata")
    if tuple(map(_mesh_structure, before)) != tuple(map(_mesh_structure, after)):
        raise XModelError("Sculpting changed Mesh topology or index data")
    if _mutable_ranges(before) != _mutable_ranges(after):
        raise XModelError("Sculpting moved coordinate payload boundaries")
    if immutable_sha256(source, before) != immutable_sha256(result, after):
        raise XModelError(
            "Sculpting changed UVs, materials, weights, frames, or other immutable bytes"
        )


def sculpt_expanded(
    source: bytes,
    transform: VertexTransform,
    *,
    recompute_mesh_normals: bool = True,
) -> tuple[bytes, tuple[MeshData, ...], tuple[MeshData, ...], int]:
    source_tokens = parse_tokens(source)
    before = discover_meshes(source, source_tokens)
    target = bytearray(source)
    changed_vertices = 0
    for mesh in before:
        transformed: list[Vector3] = []
        for vertex_index, point in enumerate(mesh.vertices):
            output = transform(mesh, vertex_index, point)
            if len(output) != 3:
                raise XModelError("Sculpt transform did not return a 3D coordinate")
            output_vector = (float(output[0]), float(output[1]), float(output[2]))
            transformed.append(output_vector)
            if struct.pack("<3f", *output_vector) != struct.pack("<3f", *point):
                changed_vertices += 1
        transformed_tuple = tuple(transformed)
        _write_vectors(target, mesh.position_token, transformed_tuple)
        if recompute_mesh_normals and mesh.normal_token is not None:
            normals = recompute_normals(
                transformed_tuple,
                mesh.faces,
                mesh.normal_faces,
                mesh.normals,
            )
            _write_vectors(target, mesh.normal_token, normals)

    expanded = bytes(target)
    result_tokens = parse_tokens(expanded)
    after = discover_meshes(expanded, result_tokens)
    assert_sculpt_invariants(
        source,
        expanded,
        source_tokens,
        result_tokens,
        before,
        after,
    )
    return expanded, before, after, changed_vertices


def sculpt_xof_mszip(
    source: bytes,
    transform: VertexTransform,
    *,
    label: str = "weapon.jcs",
    recompute_mesh_normals: bool = True,
) -> SculptResult:
    """Sculpt an MSZIP binary-X model and validate its exact round trip."""

    expanded_source = expand_xof_mszip(source, label)
    expanded, before, after, changed = sculpt_expanded(
        expanded_source,
        transform,
        recompute_mesh_normals=recompute_mesh_normals,
    )
    encoded = compress_xof_mszip(expanded, label)
    if expand_xof_mszip(encoded, label) != expanded:
        raise XModelError("Sculpted MSZIP model failed exact decode verification")
    return SculptResult(
        encoded=encoded,
        expanded=expanded,
        before=before,
        after=after,
        changed_vertices=changed,
        source_sha256=hashlib.sha256(source).hexdigest(),
        result_sha256=hashlib.sha256(encoded).hexdigest(),
    )
