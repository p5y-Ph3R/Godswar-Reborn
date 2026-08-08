"""Filesystem and process guards for client-asset mutation."""

from __future__ import annotations

import ctypes
from ctypes import wintypes
import os
from pathlib import Path

from .errors import RankEffectError


_REPARSE_POINT = 0x400
_QUERY_LIMITED_INFORMATION = 0x1000


def is_reparse_point(path: Path) -> bool:
    try:
        attributes = getattr(os.lstat(path), "st_file_attributes", 0)
    except OSError as error:
        raise RankEffectError(f"Could not inspect path safety: {path}") from error
    return bool(attributes & _REPARSE_POINT) or path.is_symlink()


def require_plain_path(root: Path, path: Path, label: str) -> None:
    raw_root = Path(os.path.abspath(root))
    raw_path = Path(os.path.abspath(path))
    try:
        relative = raw_path.relative_to(raw_root)
    except ValueError as error:
        raise RankEffectError(f"{label} escapes its root: {raw_path}") from error
    resolved_root = raw_root.resolve()
    resolved_path = raw_path.resolve(strict=False)
    try:
        resolved_path.relative_to(resolved_root)
    except ValueError as error:
        raise RankEffectError(f"{label} resolves outside its root: {raw_path}") from error
    current = raw_root
    if is_reparse_point(current):
        raise RankEffectError(f"{label} root is a reparse point: {current}")
    for part in relative.parts:
        current /= part
        if current.exists() and is_reparse_point(current):
            raise RankEffectError(f"{label} crosses a reparse point: {current}")


def _windows_process_paths() -> tuple[Path, ...]:
    if os.name != "nt":
        return ()
    psapi = ctypes.WinDLL("psapi", use_last_error=True)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    psapi.EnumProcesses.argtypes = (
        ctypes.POINTER(wintypes.DWORD),
        wintypes.DWORD,
        ctypes.POINTER(wintypes.DWORD),
    )
    psapi.EnumProcesses.restype = wintypes.BOOL
    kernel32.OpenProcess.argtypes = (
        wintypes.DWORD,
        wintypes.BOOL,
        wintypes.DWORD,
    )
    kernel32.OpenProcess.restype = wintypes.HANDLE
    kernel32.QueryFullProcessImageNameW.argtypes = (
        wintypes.HANDLE,
        wintypes.DWORD,
        wintypes.LPWSTR,
        ctypes.POINTER(wintypes.DWORD),
    )
    kernel32.QueryFullProcessImageNameW.restype = wintypes.BOOL
    kernel32.CloseHandle.argtypes = (wintypes.HANDLE,)
    kernel32.CloseHandle.restype = wintypes.BOOL
    process_ids = (wintypes.DWORD * 16384)()
    needed = wintypes.DWORD()
    if not psapi.EnumProcesses(
        process_ids, ctypes.sizeof(process_ids), ctypes.byref(needed)
    ):
        raise RankEffectError("Could not enumerate processes before client mutation")
    count = needed.value // ctypes.sizeof(wintypes.DWORD)
    result: list[Path] = []
    for process_id in process_ids[:count]:
        handle = kernel32.OpenProcess(
            _QUERY_LIMITED_INFORMATION, False, process_id
        )
        if not handle:
            continue
        try:
            capacity = wintypes.DWORD(32768)
            buffer = ctypes.create_unicode_buffer(capacity.value)
            if kernel32.QueryFullProcessImageNameW(
                handle, 0, buffer, ctypes.byref(capacity)
            ):
                result.append(Path(buffer.value))
        finally:
            kernel32.CloseHandle(handle)
    return tuple(result)


def require_origin_closed(client_root: Path) -> None:
    expected = os.path.normcase(os.path.abspath(client_root / "Origin.exe"))
    for process_path in _windows_process_paths():
        actual = os.path.normcase(os.path.abspath(process_path))
        if actual == expected:
            raise RankEffectError(
                "Origin.exe is running from the target client. Close it before "
                "installing or restoring rank effects."
            )
