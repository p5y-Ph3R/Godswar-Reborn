"""Offline format, package, baseline, transaction, and rollback checks."""

from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess
import tempfile
import time

from erebus_lion.model_codec import compress_xof_mszip
from rank_effect_packages.baseline import (
    create_baseline,
    shard_baseline,
    verify_baseline,
)
from rank_effect_packages.errors import RankEffectError
from rank_effect_packages.formats import (
    extract_texture_references,
    rewrite_texture_references,
    structural_fingerprint,
    validate_tga_texture,
)
from rank_effect_packages.installer import (
    install,
    installation_assets,
    restore_backup,
    verify_installed,
    verify_new_silhouettes,
)
from rank_effect_packages.package import EffectRecord, LoadedPackage, load_package
from rank_effect_packages.safety import require_origin_closed


def _string(value: bytes) -> bytes:
    return struct.pack("<HI", 2, len(value)) + value + struct.pack("<H", 20)


def _integer(value: int) -> bytes:
    return struct.pack("<HI", 3, value)


def _jcs(reference: bytes | None, marker: int) -> bytes:
    expanded = (_string(reference) if reference else b"") + _integer(marker)
    return compress_xof_mszip(expanded, f"fixture-{marker}.jcs")


def _tga(seed: int, bits: int = 24) -> bytes:
    width, height = 2, 2
    header = struct.pack(
        "<BBBHHBHHHHBB",
        0, 0, 2, 0, 0, 0, 0, 0, width, height, bits, 0 if bits == 24 else 8,
    )
    pixel_size = bits // 8
    pixels = bytes((seed + index) % 256 for index in range(width * height * pixel_size))
    return header + pixels + (b"\x00" * 8) + b"TRUEVISION-XFILE.\x00"


def _write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def _write_client_ar9(client: Path) -> None:
    for root_index, root in enumerate(("Characters", "Characters_New")):
        effect = client / root / "effect"
        effect.mkdir(parents=True)
        texture10, texture11 = _tga(10 + root_index), _tga(20 + root_index)
        (effect / "female_body_effect_0010.tga").write_bytes(texture10)
        (effect / "female_body_effect_0011.tga").write_bytes(texture11)
        for gender_index, gender in enumerate(("female", "male")):
            (effect / f"{gender}_body_effect_0009.gwo").write_bytes(_tga(30 + gender_index))
            for index in range(3):
                reference = (
                    b"female_body_effect_0011.tga"
                    if index == 1
                    else b"female_body_effect_0010.tga"
                )
                (effect / f"{gender}_body_effect_0009_{index}.jcs").write_bytes(
                    _jcs(reference, 90 + index)
                )


def _build_package(package_root: Path, client: Path) -> None:
    baseline = create_baseline(client, (10,), ())
    baseline_main, baseline_shards = shard_baseline(baseline)
    baseline_path = package_root / "generated" / "protected-stock.json"
    _write_json(baseline_path, baseline_main)
    for name, value in baseline_shards.items():
        _write_json(baseline_path.parent / name, value)

    files: list[dict[str, object]] = []
    effects: list[dict[str, object]] = []
    written: set[Path] = set()

    def add(target: Path, value: bytes) -> None:
        if target in written:
            return
        source = Path("generated") / "assets" / target
        path = package_root / source
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(value)
        files.append(
            {
                "source": source.as_posix(),
                "target": target.as_posix(),
                "sha256": hashlib.sha256(value).hexdigest(),
            }
        )
        written.add(target)

    for root_index, root in enumerate(("Characters", "Characters_New")):
        private = Path(root) / "effect" / "reborn_body_effect_0010.tga"
        texture = _tga(100 + root_index, 32)
        add(private, texture)
        for gender_index, gender in enumerate(("female", "male")):
            stem = f"{gender}_body_effect_0010"
            canonical = Path(root) / "effect" / f"{stem}.gwo"
            add(canonical, texture)
            models: list[str] = []
            for index in range(3):
                target = Path(root) / "effect" / f"{stem}_{index}.jcs"
                value = _jcs(private.name.encode("ascii") if index == 0 else None, 110 + index)
                add(target, value)
                models.append(target.as_posix())
            effects.append(
                {
                    "kind": "armor",
                    "rank": 10,
                    "asset_root": root,
                    "gender": gender,
                    "models": models,
                    "canonical_texture": canonical.as_posix(),
                    "private_textures": [private.as_posix()],
                }
            )

    shard = {
        "format": "reborn-rank-effect-package-shard-v1",
        "files": files,
        "effects": effects,
    }
    _write_json(package_root / "generated" / "manifests" / "armor-10.json", shard)
    manifest = {
        "format": "reborn-rank-effect-package-v1",
        "package_id": "offline-ar10-fixture-v1",
        "coverage": {"armor_ranks": [10], "weapon_classes": []},
        "protected_baseline": "generated/protected-stock.json",
        "armor_rank_9_compatibility": {
            "mode": "runtime_token_remap",
            "mappings": [
                {
                    "from": "female_body_effect_0010.tga",
                    "to": "legacy_body_effect_0010.tga",
                },
                {
                    "from": "female_body_effect_0011.tga",
                    "to": "legacy_body_effect_0011.tga",
                },
            ],
        },
        "effect_manifests": ["generated/manifests/armor-10.json"],
    }
    _write_json(package_root / "rank-effect-manifest.json", manifest)


def _expect_error(action, label: str) -> None:
    try:
        action()
    except RankEffectError:
        return
    raise AssertionError(f"Expected RankEffectError: {label}")


def _armor_effect(rank: int, structure: str) -> EffectRecord:
    stem = f"female_body_effect_{rank:04d}"
    root = Path("Characters") / "effect"
    return EffectRecord(
        f"armor:{rank}:body:Characters:female",
        "armor",
        rank,
        "Characters",
        "female",
        None,
        tuple(root / f"{stem}_{slot}.jcs" for slot in range(3)),
        root / f"{stem}.gwo",
        (root / f"reborn_body_effect_{rank:04d}.tga",),
        structure,
    )


def _adjacent_package(effects: tuple[EffectRecord, ...]) -> LoadedPackage:
    baseline = {
        "files": [
            {
                "path": f"Characters/effect/female_body_effect_0009_{slot}.jcs",
                "sha256": str(slot) * 64,
                "structural_sha256": str(slot + 1) * 64,
            }
            for slot in range(3)
        ]
    }
    return LoadedPackage(Path("."), {}, {}, effects, baseline)


def main() -> int:
    checks = 0
    for bits in (24, 32):
        info = validate_tga_texture(_tga(bits, bits), f"{bits}-bit fixture")
        assert info.width == 2 and info.bits_per_pixel == bits
    _expect_error(
        lambda: validate_tga_texture(_tga(1) + b"junk", "trailing junk"),
        "trailing TGA data",
    )
    checks += 1

    sequential = _adjacent_package(
        (_armor_effect(10, "a" * 64), _armor_effect(11, "b" * 64))
    )
    verify_new_silhouettes(sequential)
    _expect_error(
        lambda: verify_new_silhouettes(
            _adjacent_package(
                (_armor_effect(10, "a" * 64), _armor_effect(11, "a" * 64))
            )
        ),
        "adjacent armor silhouette reuse",
    )
    _expect_error(
        lambda: verify_new_silhouettes(
            _adjacent_package((_armor_effect(12, "c" * 64),))
        ),
        "missing preceding package armor rank",
    )
    checks += 1

    original = _jcs(b"female_body_effect_0010.tga", 1)
    rewritten, counts = rewrite_texture_references(
        original,
        {b"female_body_effect_0010.tga": b"reborn_body_effect_0010.tga"},
        "rewrite fixture",
    )
    assert counts[b"female_body_effect_0010.tga"] == 1
    assert extract_texture_references(rewritten, "rewritten") == (
        b"reborn_body_effect_0010.tga",
    )
    assert structural_fingerprint(original, "original") == structural_fingerprint(
        rewritten, "rewritten"
    )
    _expect_error(
        lambda: rewrite_texture_references(
            original, {b"missing.tga": b"reborn_missing.tga"}, "missing"
        ),
        "missing JCS mapping",
    )
    checks += 1

    with tempfile.TemporaryDirectory(prefix="reborn-rank-effect-test-") as folder:
        root = Path(folder)
        client, package_root = root / "client", root / "package"
        _write_client_ar9(client)
        _build_package(package_root, client)
        package = load_package(package_root)
        assert len(package.effects) == 4 and len(package.assets) == 18
        verify_new_silhouettes(package)
        targets = installation_assets(client, package)
        assert len(targets) == 34
        checks += 1

        backup = install(client, root / "backups", package)
        verify_installed(client, package)
        for tree in ("Characters", "Characters_New"):
            legacy = client / tree / "effect" / "legacy_body_effect_0010.tga"
            assert legacy.is_file()
            refs = extract_texture_references(
                (client / tree / "effect" / "female_body_effect_0009_0.jcs").read_bytes(),
                "installed AR9",
            )
            assert refs == (b"legacy_body_effect_0010.tga",)
        checks += 1

        restore_backup(client, backup)
        verify_baseline(client, package.baseline)
        assert not (
            client / "Characters" / "effect" / "legacy_body_effect_0010.tga"
        ).exists()
        checks += 1

        protected = client / "Characters" / "effect" / "male_body_effect_0009.gwo"
        protected.write_bytes(b"tampered")
        _expect_error(
            lambda: installation_assets(client, package),
            "protected-stock drift",
        )
        checks += 1

    if os.name == "nt":
        with tempfile.TemporaryDirectory(prefix="reborn-origin-guard-") as folder:
            client = Path(folder)
            origin = client / "Origin.exe"
            shutil.copy2(os.environ.get("ComSpec", r"C:\Windows\System32\cmd.exe"), origin)
            process = subprocess.Popen(
                [str(origin), "/c", "ping -n 6 127.0.0.1 >NUL"],
                creationflags=subprocess.CREATE_NO_WINDOW,
            )
            try:
                time.sleep(0.25)
                _expect_error(
                    lambda: require_origin_closed(client),
                    "running exact-root Origin.exe",
                )
            finally:
                process.terminate()
                process.wait(timeout=5)
        checks += 1

    print(f"PASS: {checks} rank-effect package framework checks")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
