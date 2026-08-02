"""Bounded token reader for the ``xof 0303bin 0032`` payload.

The GodsWar ``.jcs`` files wrap this token stream in MSZIP.  This reader does
not try to interpret every DirectX template; it only establishes safe token
boundaries so the mesh layer can locate the standard ``Mesh`` templates.
"""

from __future__ import annotations

from dataclasses import dataclass
import struct


TOKEN_NAME = 1
TOKEN_STRING = 2
TOKEN_INTEGER = 3
TOKEN_GUID = 5
TOKEN_INTEGER_LIST = 6
TOKEN_FLOAT_LIST = 7
TOKEN_OBRACE = 10
TOKEN_CBRACE = 11
TOKEN_TEMPLATE = 31

_PUNCTUATION_TOKENS = frozenset(range(10, 21))
_DECLARATION_TOKENS = frozenset(range(30, 53))


class XModelError(ValueError):
    """Raised when a binary X payload is malformed or unsupported."""


@dataclass(frozen=True, slots=True)
class Token:
    kind: int
    start: int
    end: int
    value: bytes | int | None = None
    payload_offset: int | None = None
    item_count: int = 0


def _require(data: bytes, offset: int, length: int, label: str) -> None:
    if offset < 0 or length < 0 or offset + length > len(data):
        raise XModelError(f"Truncated {label} at byte {offset}")


def _u16(data: bytes, offset: int, label: str) -> int:
    _require(data, offset, 2, label)
    return struct.unpack_from("<H", data, offset)[0]


def _u32(data: bytes, offset: int, label: str) -> int:
    _require(data, offset, 4, label)
    return struct.unpack_from("<I", data, offset)[0]


def parse_tokens(
    data: bytes,
    *,
    max_tokens: int = 2_000_000,
    max_name_bytes: int = 1_048_576,
    max_list_items: int = 16_000_000,
) -> tuple[Token, ...]:
    """Return validated token boundaries for an expanded binary-X payload."""

    if not data:
        raise XModelError("Binary X payload is empty")
    cursor = 0
    result: list[Token] = []
    while cursor < len(data):
        if len(result) >= max_tokens:
            raise XModelError(f"Binary X payload exceeds {max_tokens} tokens")
        start = cursor
        kind = _u16(data, cursor, "token")
        cursor += 2
        value: bytes | int | None = None
        payload_offset: int | None = None
        item_count = 0

        if kind in (TOKEN_NAME, TOKEN_STRING):
            length = _u32(data, cursor, "name length")
            cursor += 4
            if length > max_name_bytes:
                raise XModelError(
                    f"Binary X name at byte {start} exceeds {max_name_bytes} bytes"
                )
            _require(data, cursor, length, "name payload")
            value = data[cursor : cursor + length]
            cursor += length
            if kind == TOKEN_STRING:
                terminator = _u16(data, cursor, "string terminator")
                if terminator != 20:  # TOKEN_SEMICOLON
                    raise XModelError(
                        f"Invalid binary X string terminator {terminator} at byte {cursor}"
                    )
                cursor += 2
        elif kind == TOKEN_INTEGER:
            value = _u32(data, cursor, "integer")
            cursor += 4
        elif kind == TOKEN_GUID:
            _require(data, cursor, 16, "GUID")
            value = data[cursor : cursor + 16]
            cursor += 16
        elif kind in (TOKEN_INTEGER_LIST, TOKEN_FLOAT_LIST):
            item_count = _u32(data, cursor, "list length")
            cursor += 4
            if item_count > max_list_items:
                raise XModelError(
                    f"Binary X list at byte {start} exceeds {max_list_items} items"
                )
            payload_offset = cursor
            byte_count = item_count * 4
            _require(data, cursor, byte_count, "list payload")
            cursor += byte_count
        elif kind not in _PUNCTUATION_TOKENS | _DECLARATION_TOKENS:
            raise XModelError(f"Unsupported binary X token {kind} at byte {start}")

        result.append(
            Token(
                kind=kind,
                start=start,
                end=cursor,
                value=value,
                payload_offset=payload_offset,
                item_count=item_count,
            )
        )

    if cursor != len(data):
        raise XModelError("Binary X token stream did not end on a token boundary")
    return tuple(result)


def integer_list(data: bytes, token: Token) -> tuple[int, ...]:
    if token.kind != TOKEN_INTEGER_LIST or token.payload_offset is None:
        raise XModelError("Expected an integer-list token")
    if token.item_count == 0:
        return ()
    return struct.unpack_from(
        f"<{token.item_count}I", data, token.payload_offset
    )


def float_list(data: bytes, token: Token) -> tuple[float, ...]:
    if token.kind != TOKEN_FLOAT_LIST or token.payload_offset is None:
        raise XModelError("Expected a float-list token")
    if token.item_count == 0:
        return ()
    return struct.unpack_from(
        f"<{token.item_count}f", data, token.payload_offset
    )
