"""Structural role audits for v2 JCS models."""

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
from xmodel_sculpt.mesh import Vector3, discover_meshes


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
