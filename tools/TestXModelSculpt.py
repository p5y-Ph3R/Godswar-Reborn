"""Offline structural, determinism, and adversarial checks for xmodel_sculpt."""

from __future__ import annotations

import struct

from erebus_lion.model_codec import compress_xof_mszip, expand_xof_mszip
from xmodel_sculpt.binary_x import XModelError, parse_tokens
from xmodel_sculpt.mesh import discover_meshes
from xmodel_sculpt.profiles import profile_transform
from xmodel_sculpt.sculpt import sculpt_xof_mszip


def _name(value: bytes) -> bytes:
    return struct.pack("<HI", 1, len(value)) + value


def _token(kind: int) -> bytes:
    return struct.pack("<H", kind)


def _integers(values: list[int]) -> bytes:
    return struct.pack(f"<HI{len(values)}I", 6, len(values), *values)


def _floats(values: list[float]) -> bytes:
    return struct.pack(f"<HI{len(values)}f", 7, len(values), *values)


def _fixture() -> bytes:
    vertices = [
        -0.20, 0.0, -0.15,
        0.20, 0.0, -0.15,
        0.0, 0.0, 0.20,
        0.0, 2.0, 0.0,
    ]
    faces = [
        4,
        3, 0, 1, 2,
        3, 0, 3, 1,
        3, 1, 3, 2,
        3, 2, 3, 0,
    ]
    normals = [
        0.0, -1.0, 0.0,
        0.0, 0.0, -1.0,
        1.0, 0.0, 0.0,
        -1.0, 0.0, 0.0,
    ]
    normal_faces = [
        4,
        3, 0, 0, 0,
        3, 1, 1, 1,
        3, 2, 2, 2,
        3, 3, 3, 3,
    ]
    matrix = [
        1.0, 0.0, 0.0, 0.0,
        0.0, 1.0, 0.0, 0.0,
        0.0, 0.0, 1.0, 0.0,
        5.0, 6.0, 7.0, 1.0,
    ]
    return b"".join(
        (
            _name(b"Frame"),
            _name(b"FixtureRoot"),
            _token(10),
            _name(b"FrameTransformMatrix"),
            _token(10),
            _floats(matrix),
            _token(11),
            _name(b"Mesh"),
            _name(b"FixtureMesh"),
            _token(10),
            _integers([4]),
            _floats(vertices),
            _integers(faces),
            _name(b"MeshNormals"),
            _name(b""),
            _token(10),
            _integers([4]),
            _floats(normals),
            _integers(normal_faces),
            _token(11),
            _name(b"MeshTextureCoords"),
            _name(b""),
            _token(10),
            _integers([4]),
            _floats([0.0, 0.0, 1.0, 0.0, 0.5, 1.0, 0.5, 0.5]),
            _token(11),
            _token(11),
            _token(11),
        )
    )


def _expect_error(action, label: str) -> None:
    try:
        action()
    except XModelError:
        return
    raise AssertionError(f"Expected XModelError for {label}")


def main() -> int:
    checks = 0
    expanded = _fixture()
    tokens = parse_tokens(expanded)
    mesh = discover_meshes(expanded, tokens)[0]
    assert len(mesh.vertices) == 4 and len(mesh.faces) == 4
    assert len(mesh.normals) == 4 and len(mesh.normal_faces) == 4
    checks += 1

    encoded = compress_xof_mszip(expanded, "fixture.jcs")
    assert expand_xof_mszip(encoded, "fixture.jcs") == expanded
    checks += 1

    transform = lambda _mesh, _index, point: (
        point[0] * (1.5 if point[1] > 1.0 else 1.0),
        point[1] * (1.1 if point[1] > 1.0 else 1.0),
        point[2],
    )
    first = sculpt_xof_mszip(encoded, transform, label="fixture.jcs")
    second = sculpt_xof_mszip(encoded, transform, label="fixture.jcs")
    assert first.encoded == second.encoded
    assert first.changed_vertices == 1
    assert first.before[0].faces == first.after[0].faces
    assert first.before[0].normal_faces == first.after[0].normal_faces
    assert first.before[0].normals != first.after[0].normals
    checks += 1

    identity = sculpt_xof_mszip(
        encoded,
        lambda _mesh, _index, point: point,
        label="fixture.jcs",
        recompute_mesh_normals=False,
    )
    assert identity.expanded == expanded and identity.encoded == encoded
    checks += 1

    profile = sculpt_xof_mszip(
        encoded,
        profile_transform(1035),
        label="fixture.jcs",
    )
    assert profile.changed_vertices > 0
    checks += 1

    _expect_error(lambda: parse_tokens(expanded[:-1]), "truncated payload")
    checks += 1

    corrupted = bytearray(expanded)
    face_offset = mesh.face_token.payload_offset
    assert face_offset is not None
    struct.pack_into("<I", corrupted, face_offset + (mesh.face_token.item_count - 1) * 4, 99)
    _expect_error(lambda: discover_meshes(bytes(corrupted)), "out-of-range face index")
    checks += 1

    non_finite = lambda _mesh, _index, point: (float("nan"), point[1], point[2])
    _expect_error(
        lambda: sculpt_xof_mszip(encoded, non_finite, label="fixture.jcs"),
        "non-finite transform",
    )
    checks += 1

    print(f"PASS: {checks} offline xmodel_sculpt checks")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
