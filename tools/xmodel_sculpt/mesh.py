"""Structural reader for standard binary-X ``Mesh`` objects."""

from __future__ import annotations

from dataclasses import dataclass
import math

from .binary_x import (
    TOKEN_CBRACE,
    TOKEN_FLOAT_LIST,
    TOKEN_INTEGER_LIST,
    TOKEN_NAME,
    TOKEN_OBRACE,
    Token,
    XModelError,
    float_list,
    integer_list,
    parse_tokens,
)


Vector3 = tuple[float, float, float]
Face = tuple[int, ...]


@dataclass(frozen=True, slots=True)
class MeshData:
    index: int
    name: bytes
    vertices: tuple[Vector3, ...]
    faces: tuple[Face, ...]
    position_token: Token
    face_token: Token
    normals: tuple[Vector3, ...]
    normal_faces: tuple[Face, ...]
    normal_token: Token | None
    normal_face_token: Token | None
    open_token_index: int
    close_token_index: int

    @property
    def bounds(self) -> tuple[Vector3, Vector3]:
        if not self.vertices:
            zero = (0.0, 0.0, 0.0)
            return zero, zero
        return (
            tuple(min(point[axis] for point in self.vertices) for axis in range(3)),
            tuple(max(point[axis] for point in self.vertices) for axis in range(3)),
        )  # type: ignore[return-value]


def _object_header(
    tokens: tuple[Token, ...], index: int
) -> tuple[bytes, int] | None:
    cursor = index + 1
    name = b""
    if cursor < len(tokens) and tokens[cursor].kind == TOKEN_NAME:
        value = tokens[cursor].value
        name = value if isinstance(value, bytes) else b""
        cursor += 1
    if cursor >= len(tokens) or tokens[cursor].kind != TOKEN_OBRACE:
        return None
    return name, cursor


def _matching_close(tokens: tuple[Token, ...], open_index: int) -> int:
    depth = 0
    for index in range(open_index, len(tokens)):
        if tokens[index].kind == TOKEN_OBRACE:
            depth += 1
        elif tokens[index].kind == TOKEN_CBRACE:
            depth -= 1
            if depth == 0:
                return index
            if depth < 0:
                break
    raise XModelError(
        f"Binary X object at byte {tokens[open_index].start} is not closed"
    )


def _vectors(data: bytes, token: Token, expected_count: int, label: str) -> tuple[Vector3, ...]:
    if token.kind != TOKEN_FLOAT_LIST or token.item_count != expected_count * 3:
        raise XModelError(
            f"{label} has {token.item_count} floats; expected {expected_count * 3}"
        )
    values = float_list(data, token)
    if any(not math.isfinite(value) for value in values):
        raise XModelError(f"{label} contains a non-finite coordinate")
    return tuple(
        (values[offset], values[offset + 1], values[offset + 2])
        for offset in range(0, len(values), 3)
    )


def _faces(
    data: bytes,
    token: Token,
    vertex_count: int,
    label: str,
    *,
    max_face_vertices: int = 64,
) -> tuple[Face, ...]:
    if token.kind != TOKEN_INTEGER_LIST:
        raise XModelError(f"{label} is not an integer list")
    values = integer_list(data, token)
    if not values:
        raise XModelError(f"{label} does not contain a face count")
    face_count = values[0]
    cursor = 1
    result: list[Face] = []
    for face_index in range(face_count):
        if cursor >= len(values):
            raise XModelError(f"{label} ends before face {face_index}")
        count = values[cursor]
        cursor += 1
        if count < 3 or count > max_face_vertices:
            raise XModelError(
                f"{label} face {face_index} has unsupported width {count}"
            )
        end = cursor + count
        if end > len(values):
            raise XModelError(f"{label} face {face_index} is truncated")
        face = tuple(values[cursor:end])
        if any(index >= vertex_count for index in face):
            raise XModelError(f"{label} face {face_index} has an invalid index")
        result.append(face)
        cursor = end
    if cursor != len(values):
        raise XModelError(f"{label} contains trailing face-index data")
    return tuple(result)


def _count_from_token(data: bytes, token: Token, label: str) -> int:
    if token.kind != TOKEN_INTEGER_LIST or token.item_count != 1:
        raise XModelError(f"{label} must be a one-item integer list")
    return integer_list(data, token)[0]


def _read_normals(
    data: bytes,
    tokens: tuple[Token, ...],
    start: int,
    end: int,
    mesh_faces: tuple[Face, ...],
) -> tuple[tuple[Vector3, ...], tuple[Face, ...], Token | None, Token | None]:
    candidates: list[int] = []
    for index in range(start, end):
        token = tokens[index]
        if token.kind == TOKEN_NAME and token.value == b"MeshNormals":
            header = _object_header(tokens, index)
            if header is not None:
                candidates.append(index)
    if not candidates:
        return (), (), None, None
    if len(candidates) != 1:
        raise XModelError("A Mesh contains more than one MeshNormals object")

    header = _object_header(tokens, candidates[0])
    assert header is not None
    _, open_index = header
    close_index = _matching_close(tokens, open_index)
    if close_index > end:
        raise XModelError("MeshNormals escapes its parent Mesh")
    body = open_index + 1
    if body + 2 >= close_index:
        raise XModelError("MeshNormals body is incomplete")
    normal_count = _count_from_token(data, tokens[body], "Mesh normal count")
    normals = _vectors(data, tokens[body + 1], normal_count, "Mesh normals")
    normal_faces = _faces(
        data,
        tokens[body + 2],
        normal_count,
        "Mesh normal faces",
    )
    if len(normal_faces) != len(mesh_faces):
        raise XModelError("Mesh and MeshNormals have different face counts")
    for face_index, (face, normal_face) in enumerate(zip(mesh_faces, normal_faces)):
        if len(face) != len(normal_face):
            raise XModelError(
                f"Mesh face {face_index} and its normal face have different widths"
            )
    return normals, normal_faces, tokens[body + 1], tokens[body + 2]


def discover_meshes(
    data: bytes,
    tokens: tuple[Token, ...] | None = None,
) -> tuple[MeshData, ...]:
    """Parse every structurally valid standard ``Mesh`` object in ``data``."""

    parsed_tokens = tokens or parse_tokens(data)
    meshes: list[MeshData] = []
    for token_index, token in enumerate(parsed_tokens):
        if token.kind != TOKEN_NAME or token.value != b"Mesh":
            continue
        header = _object_header(parsed_tokens, token_index)
        if header is None:
            continue
        name, open_index = header
        body = open_index + 1
        # Ignore template declarations and fields named Mesh. A real standard
        # Mesh always begins with one integer-list count and one float list.
        if (
            body + 2 >= len(parsed_tokens)
            or parsed_tokens[body].kind != TOKEN_INTEGER_LIST
            or parsed_tokens[body + 1].kind != TOKEN_FLOAT_LIST
            or parsed_tokens[body + 2].kind != TOKEN_INTEGER_LIST
        ):
            continue
        close_index = _matching_close(parsed_tokens, open_index)
        vertex_count = _count_from_token(data, parsed_tokens[body], "Mesh vertex count")
        vertices = _vectors(
            data,
            parsed_tokens[body + 1],
            vertex_count,
            "Mesh vertices",
        )
        faces = _faces(
            data,
            parsed_tokens[body + 2],
            vertex_count,
            "Mesh faces",
        )
        normals, normal_faces, normal_token, normal_face_token = _read_normals(
            data,
            parsed_tokens,
            body + 3,
            close_index,
            faces,
        )
        meshes.append(
            MeshData(
                index=len(meshes),
                name=name,
                vertices=vertices,
                faces=faces,
                position_token=parsed_tokens[body + 1],
                face_token=parsed_tokens[body + 2],
                normals=normals,
                normal_faces=normal_faces,
                normal_token=normal_token,
                normal_face_token=normal_face_token,
                open_token_index=open_index,
                close_token_index=close_index,
            )
        )
    if not meshes:
        raise XModelError("Binary X payload does not contain a standard Mesh object")
    return tuple(meshes)
