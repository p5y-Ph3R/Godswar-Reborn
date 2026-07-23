"""Backward-compatible command-line entry point for the Erebus Lion installer."""

from __future__ import annotations

from erebus_lion.cli import build_parser, main

__all__ = ["build_parser", "main"]


if __name__ == "__main__":
    raise SystemExit(main())
