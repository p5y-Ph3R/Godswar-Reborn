from __future__ import annotations

import json
import os
from pathlib import Path
import tempfile

from erebus_lion.common import InstallError, sha256_bytes
from erebus_lion.image_assets import (
    encode_png_rgba,
    encode_tga_image,
    image_to_display_rgba,
    parse_tga_image,
)

from .constants import (
    ASSET_ROOTS,
    EXPECTED_OUTPUT_HASHES,
    GENDERS,
    GENERATOR_VERSION,
    TRANSFORM_ID,
    WEAPONS,
)
from .transform import recolor_texture


def json_bytes(value: object) -> bytes:
    return json.dumps(value, indent=2, sort_keys=True).encode("utf-8") + b"\n"


def write_atomic(path: Path, data: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary_name = tempfile.mkstemp(
        prefix=f".{path.name}.", suffix=".tmp", dir=path.parent
    )
    temporary = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        if temporary.read_bytes() != data:
            raise InstallError(f"Staging write validation failed: {temporary}")
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def ensure_staging_is_separate(client_root: Path, output_root: Path) -> None:
    try:
        output_root.resolve().relative_to(client_root.resolve())
    except ValueError:
        return
    raise InstallError(
        "Output root cannot be inside the game client; this tool only stages assets"
    )


def read_source_texture(
    client_root: Path,
    asset_root: str,
    spec,
) -> tuple[Path, bytes]:
    copies: list[tuple[Path, bytes]] = []
    for gender in GENDERS:
        path = (
            client_root
            / asset_root
            / spec.texture_name(gender, spec.tier_three_id)
        )
        if not path.is_file():
            raise InstallError(f"Required Tier III texture is missing: {path}")
        copies.append((path, path.read_bytes()))
    if copies[0][1] != copies[1][1]:
        raise InstallError(
            f"Tier III male/female textures differ for {spec.class_name} "
            f"in {asset_root}; a shared transform is unsafe"
        )
    expected_hash = spec.source_hashes[asset_root]
    actual_hash = sha256_bytes(copies[0][1])
    if actual_hash != expected_hash:
        raise InstallError(
            f"Unexpected Tier III source hash for {spec.class_name} "
            f"in {asset_root}: {actual_hash}; expected {expected_hash}"
        )
    return copies[0]


def build_staged_outputs(
    client_root: Path,
    output_root: Path,
) -> tuple[dict[Path, bytes], dict[str, object]]:
    required_hash_keys = {
        f"{asset_root}:{spec.tier_four_id}"
        for spec in WEAPONS
        for asset_root in ASSET_ROOTS
    }
    if set(EXPECTED_OUTPUT_HASHES) != required_hash_keys:
        raise InstallError(
            "Pinned output hash coverage differs from the weapon/root matrix"
        )

    outputs: dict[Path, bytes] = {}
    entries: list[dict[str, object]] = []
    model_plan: list[dict[str, object]] = []

    for spec in WEAPONS:
        for asset_root in ASSET_ROOTS:
            source_path, source_data = read_source_texture(
                client_root, asset_root, spec
            )
            source = parse_tga_image(source_data, str(source_path))
            if (source.width, source.height) != spec.dimensions[asset_root]:
                raise InstallError(
                    f"Unexpected dimensions for {source_path}: "
                    f"{source.width}x{source.height}"
                )

            pixels, stats = recolor_texture(source, spec.palette)
            if stats.alpha_changes:
                raise InstallError(f"Alpha changed for {source_path}")
            minimum_changes = max(1, round(stats.visible_pixels * 0.60))
            if stats.changed_visible_pixels < minimum_changes:
                raise InstallError(
                    f"Too few visible pixels changed for {source_path}: "
                    f"{stats.changed_visible_pixels}/{stats.visible_pixels}"
                )

            generated = encode_tga_image(source, pixels)
            parsed = parse_tga_image(generated, f"generated {spec.class_name}")
            if (
                parsed.width != source.width
                or parsed.height != source.height
                or parsed.image_type != source.image_type
                or parsed.descriptor != source.descriptor
                or parsed.pixels != pixels
                or parsed.suffix != source.suffix
            ):
                raise InstallError(
                    f"Generated texture failed round-trip: {spec.class_name} "
                    f"in {asset_root}"
                )

            output_hash = sha256_bytes(generated)
            output_key = f"{asset_root}:{spec.tier_four_id}"
            pinned_hash = EXPECTED_OUTPUT_HASHES.get(output_key)
            if pinned_hash and output_hash != pinned_hash:
                raise InstallError(
                    f"Generated hash changed for {output_key}: {output_hash}; "
                    f"expected {pinned_hash}"
                )

            target_files: list[str] = []
            for gender in GENDERS:
                relative = Path(asset_root) / spec.texture_name(
                    gender, spec.tier_four_id
                )
                outputs[output_root / relative] = generated
                target_files.append(relative.as_posix())

                source_model = Path(asset_root) / spec.model_name(
                    gender, spec.tier_three_id
                )
                source_model_path = client_root / source_model
                if not source_model_path.is_file():
                    raise InstallError(
                        f"Required Tier III model is missing: {source_model_path}"
                    )
                model_plan.append(
                    {
                        "action": "copy_bytes_unchanged_during_later_install",
                        "class": spec.class_name,
                        "source": source_model.as_posix(),
                        "source_sha256": sha256_bytes(source_model_path.read_bytes()),
                        "target": (
                            Path(asset_root)
                            / spec.model_name(gender, spec.tier_four_id)
                        ).as_posix(),
                    }
                )

            preview_relative = (
                Path("previews")
                / f"{asset_root}-{spec.class_name.lower()}-{spec.tier_four_id}.png"
            )
            outputs[output_root / preview_relative] = encode_png_rgba(
                source.width,
                source.height,
                image_to_display_rgba(source, pixels),
            )
            entries.append(
                {
                    "asset_root": asset_root,
                    "class": spec.class_name,
                    "tier_three_id": spec.tier_three_id,
                    "tier_four_id": spec.tier_four_id,
                    "source": source_path.relative_to(client_root).as_posix(),
                    "source_sha256": sha256_bytes(source_data),
                    "output_sha256": output_hash,
                    "output_bytes": len(generated),
                    "dimensions": [source.width, source.height],
                    "image_type": source.image_type,
                    "descriptor": source.descriptor,
                    "visible_pixels": stats.visible_pixels,
                    "changed_visible_pixels": stats.changed_visible_pixels,
                    "alpha_changes": stats.alpha_changes,
                    "targets": target_files,
                    "preview": preview_relative.as_posix(),
                }
            )

    if len({entry["output_sha256"] for entry in entries}) != len(entries):
        raise InstallError("Two class/root outputs unexpectedly have identical hashes")

    manifest: dict[str, object] = {
        "schema_version": 1,
        "generator_version": GENERATOR_VERSION,
        "transform": TRANSFORM_ID,
        "installation_performed": False,
        "invariants": [
            "Tier III source hashes are pinned",
            "male and female source textures must be byte-identical",
            "dimensions, orientation, encoding type, and alpha are preserved",
            "only staged texture and PNG preview files are written",
            "JCS models are not modified or staged",
        ],
        "textures": entries,
        "future_model_copy_plan": model_plan,
    }
    outputs[output_root / "manifest.json"] = json_bytes(manifest)
    return outputs, manifest


def build_stock_audit(client_root: Path) -> dict[str, object]:
    entries: list[dict[str, object]] = []
    for spec in WEAPONS:
        for asset_root in ASSET_ROOTS:
            for gender in GENDERS:
                pairs = (
                    (
                        "texture",
                        spec.texture_name(gender, spec.tier_three_id),
                        spec.texture_name(gender, spec.tier_four_id),
                    ),
                    (
                        "model",
                        spec.model_name(gender, spec.tier_three_id),
                        spec.model_name(gender, spec.tier_four_id),
                    ),
                )
                for kind, source_name, target_name in pairs:
                    source = client_root / asset_root / source_name
                    target = client_root / asset_root / target_name
                    if not source.is_file():
                        raise InstallError(f"Audit source is missing: {source}")
                    source_data = source.read_bytes()
                    target_data = target.read_bytes() if target.is_file() else None
                    entries.append(
                        {
                            "asset_root": asset_root,
                            "class": spec.class_name,
                            "gender": gender,
                            "kind": kind,
                            "tier_three": source_name,
                            "tier_three_sha256": sha256_bytes(source_data),
                            "tier_four": target_name,
                            "tier_four_exists": target_data is not None,
                            "tier_four_sha256": (
                                sha256_bytes(target_data)
                                if target_data is not None
                                else None
                            ),
                            "tier_four_equals_tier_three": target_data == source_data,
                        }
                    )
    return {
        "schema_version": 1,
        "purpose": "pre-install Tier III/Tier IV duplication evidence",
        "entries": entries,
    }


def stage(
    client_root: Path,
    output_root: Path,
    check: bool,
    audit_stock: bool,
) -> int:
    client_root = client_root.resolve()
    output_root = output_root.resolve()
    if not client_root.is_dir():
        raise InstallError(f"Client root does not exist: {client_root}")
    ensure_staging_is_separate(client_root, output_root)

    outputs, manifest = build_staged_outputs(client_root, output_root)
    if audit_stock:
        outputs[output_root / "stock-duplication-audit.json"] = json_bytes(
            build_stock_audit(client_root)
        )

    mismatches = [
        path
        for path, expected in outputs.items()
        if not path.is_file() or path.read_bytes() != expected
    ]
    if check:
        if mismatches:
            raise InstallError(
                "Staged Class Suit IV outputs differ: "
                + ", ".join(str(path) for path in mismatches)
            )
        print(f"Verified {len(outputs)} staged files; client unchanged.")
        return 0

    for path in mismatches:
        write_atomic(path, outputs[path])

    print(
        f"Staged {len(manifest['textures'])} deterministic texture variants "
        f"across {len(WEAPONS)} classes."
    )
    print(f"Changed staged files: {len(mismatches)}")
    print(f"Output: {output_root}")
    print("Client files changed: 0")
    return 0
