"""Verify deterministic effect-0004 construction and the repository-only CLI."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import tempfile


TOOLS = Path(__file__).resolve().parent
REPOSITORY = TOOLS.parent
ARTIFACTS = REPOSITORY / "artifacts"
CLIENT = Path(r"C:\Godswar Origin")
SOURCE = CLIENT / "Characters_New" / "PetUniteEffect" / "e_he_0003_all.gwm"
PNG = REPOSITORY / "assets" / "pet-owner-merge" / "e_he_0004_a-black-octagram.png"
CANONICAL = REPOSITORY / "assets" / "pet-owner-merge" / "e_he_0004_all.gwm"
BUILDER = TOOLS / "BuildClientPetOwnerMergeOctagramEffect.py"
LIVE_EXE = CLIENT / "Origin.exe"
LIVE_XML = tuple(
    CLIENT / "Localization" / locale / "Settings" / "Sys" / "Pet.xml"
    for locale in ("en_us", "zh_cn")
)
LIVE_ASSET = CLIENT / "Characters" / "PetUniteEffect" / "e_he_0004_all.gwm"
LIVE_PROBE = CLIENT / "Characters" / "PetUniteEffect" / "__octagram_builder_test.gwm"
sys.path.insert(0, str(TOOLS))

from erebus_lion.model_codec import expand_xof_mszip  # noqa: E402
from pet_owner_merge_effect import build_octagram_gwm  # noqa: E402
from pet_owner_merge_effect.octagram_gwm import (  # noqa: E402
    LEGACY_CROSS_SCANLINE_GWM_SHA256,
    SOURCE_GWM_SHA256,
    SOURCE_PNG_SHA256,
    STRUCTURAL_FINGERPRINT,
    TARGET_GWM_SHA256,
)
from rank_effect_packages.formats import (  # noqa: E402
    extract_texture_references,
    structural_fingerprint,
    validate_tga_texture,
)


assertions = 0


def check(condition: bool, label: str) -> None:
    global assertions
    assertions += 1
    if not condition:
        raise AssertionError(label)


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


def snapshot(paths: tuple[Path, ...]) -> dict[Path, bytes | None]:
    return {path: path.read_bytes() if path.exists() else None for path in paths}


def run_cli(*arguments: str, success: bool = True) -> subprocess.CompletedProcess[str]:
    result = subprocess.run(
        [sys.executable, str(BUILDER), *arguments],
        cwd=REPOSITORY,
        capture_output=True,
        text=True,
        check=False,
    )
    check((result.returncode == 0) == success, f"CLI success={success}: {result.stderr}")
    return result


def decode_rle(data: bytes) -> tuple[list[bytes], int, int, int]:
    cursor = 18 + data[0]
    footer = len(data) - 26
    ordered: list[bytes] = []
    packets = 0
    scanline_crossings = 0
    valid = True
    while len(ordered) < 128 * 128:
        if cursor >= footer:
            valid = False
            break
        packet = data[cursor]
        cursor += 1
        packets += 1
        count = (packet & 0x7F) + 1
        if len(ordered) + count > 128 * 128:
            valid = False
            break
        if len(ordered) // 128 != (len(ordered) + count - 1) // 128:
            scanline_crossings += 1
        if packet & 0x80:
            if cursor + 4 > footer:
                valid = False
                break
            sample = data[cursor : cursor + 4]
            cursor += 4
            ordered.extend([sample] * count)
        else:
            length = count * 4
            if cursor + length > footer:
                valid = False
                break
            ordered.extend(
                data[cursor + index * 4 : cursor + (index + 1) * 4]
                for index in range(count)
            )
            cursor += length
    check(valid, "all RLE packet bounds are valid")
    rows = [ordered[y * 128 : (y + 1) * 128] for y in range(128)]
    rows.reverse()
    return (
        [pixel for row in rows for pixel in row],
        cursor,
        packets,
        scanline_crossings,
    )


live_paths = (LIVE_EXE, *LIVE_XML, LIVE_ASSET, LIVE_PROBE, SOURCE)
live_before = snapshot(live_paths)
live_exe = live_before[LIVE_EXE]
live_xml = tuple(live_before[path] for path in LIVE_XML)
live_asset = live_before[LIVE_ASSET]
check(live_exe is not None and all(data is not None for data in live_xml), "live files exist")
reverted = (
    sha(live_exe) == "318BA84B9F7720E827D91F658387D6FA2C9F61E8E05D5901647F54EE525208DF"
    and all(
        sha(data) == "E55050B49BB5DBED6F6A4A8D2BBB78237177A6FDA065155522034462C479748C"
        for data in live_xml
    )
    and live_asset is None
)
applied = (
    sha(live_exe) == "FFCC3508FA48DCCEF1135BD92194BD46A95872B4CED914FE5B025801C9C5AFD5"
    and all(
        sha(data) == "A6BBB855D8DC1092B867A9DED096C42348C991D847AB0EBB93C3127D9A8A96BE"
        for data in live_xml
    )
    and live_asset is not None
    and sha(live_asset)
    in {TARGET_GWM_SHA256, LEGACY_CROSS_SCANLINE_GWM_SHA256}
)
check(reverted or applied, "installed octagram state is exact and coherent")

source = SOURCE.read_bytes()
png = PNG.read_bytes()
canonical = CANONICAL.read_bytes()
check(sha(source) == SOURCE_GWM_SHA256, "exact source GWM hash")
check(sha(png) == SOURCE_PNG_SHA256, "exact reviewed PNG hash")
check(sha(canonical) == TARGET_GWM_SHA256, "exact canonical GWM hash")
check(len(canonical) == 20816, "exact canonical GWM length")

first = build_octagram_gwm(source, png)
second = build_octagram_gwm(source, png)
check(first.gwm == second.gwm == canonical, "builder is deterministic and canonical")
check(first.texture == second.texture, "embedded texture is deterministic")
check(first.report == second.report, "build report is deterministic")
expected_report = {
    "targetGwmSha256": TARGET_GWM_SHA256,
    "targetGwmBytes": 20816,
    "targetModelBytes": 1637,
    "targetTextureBytes": 18727,
    "textureReference": "e_he_0004_a.tga",
    "textureReferenceRewriteCount": 1,
    "textureFormat": (128, 128, 32, 10, 8, 26),
    "sourcePngNonzeroBounds": (4, 0, 91, 90),
    "targetNonzeroRgbBounds": (4, 0, 91, 90),
    "targetNonzeroRgbPixels": 4092,
    "targetRgbEnergy": 343631,
    "alphaValues": (255,),
    "expandedModelBytes": 6292,
    "expandedModelDifferentBytes": 1,
    "structuralFingerprint": STRUCTURAL_FINGERPRINT,
    "metadataBytes": 428,
    "trailerHex": "0000000000000000",
}
for key, value in expected_report.items():
    check(first.report[key] == value, f"exact report field {key}")

count, unknown, model_length, texture_length = struct.unpack_from("<IIII", canonical)
check((count, unknown, model_length, texture_length) == (1, 0, 1637, 18727), "GWM header")
model = canonical[16 : 16 + model_length]
texture_offset = 16 + model_length
texture = canonical[texture_offset : texture_offset + texture_length]
metadata_offset = texture_offset + texture_length
metadata = canonical[metadata_offset : metadata_offset + 428]
trailer = canonical[metadata_offset + 428 :]
check(len(canonical) == metadata_offset + 436, "GWM record framing")
check(model.startswith(b"xof 0303bzip0032"), "compressed-X signature")
check(extract_texture_references(model, "canonical") == (b"e_he_0004_a.tga",), "model texture ref")

source_model_length, source_texture_length = struct.unpack_from("<II", source, 8)
source_model = source[16 : 16 + source_model_length]
source_texture_offset = 16 + source_model_length
source_texture = source[
    source_texture_offset : source_texture_offset + source_texture_length
]
source_metadata_offset = source_texture_offset + source_texture_length
source_metadata = source[source_metadata_offset : source_metadata_offset + 428]
_, source_pixel_end, source_packets, source_crossings = decode_rle(source_texture)
check(source_pixel_end == len(source_texture) - 26, "stock RLE ends at footer")
check((source_packets, source_crossings) == (542, 0), "stock RLE is row-bounded")
source_expanded = expand_xof_mszip(source_model, "source")
target_expanded = expand_xof_mszip(model, "canonical")
expanded_differences = [
    index
    for index, pair in enumerate(zip(source_expanded, target_expanded))
    if pair[0] != pair[1]
]
check(len(source_expanded) == len(target_expanded) == 6292, "expanded model length")
check(len(expanded_differences) == 1, "expanded model one-byte delta")
check(
    (source_expanded[expanded_differences[0]], target_expanded[expanded_differences[0]])
    == (ord("3"), ord("4")),
    "expanded delta is only texture identity",
)
check(
    structural_fingerprint(source_model, "source")
    == structural_fingerprint(model, "canonical")
    == STRUCTURAL_FINGERPRINT,
    "geometry/animation/topology/UV structure is preserved",
)

info = validate_tga_texture(texture, "canonical texture")
check(
    (
        info.width,
        info.height,
        info.bits_per_pixel,
        info.image_type,
        info.descriptor,
        info.suffix_bytes,
    )
    == (128, 128, 32, 10, 8, 26),
    "TGA format",
)
check(texture[:18] == source_texture[:18], "TGA header is exact source format")
check(texture[-26:] == source_texture[-26:], "TGA footer is exact source format")
pixels, pixel_end, packets, scanline_crossings = decode_rle(texture)
check(pixel_end == len(texture) - 26, "RLE stream ends at footer")
check(len(pixels) == 16384, "RLE stream decodes full atlas")
check((packets, scanline_crossings) == (623, 0), "target RLE is row-bounded")
check(set(pixel[3] for pixel in pixels) == {255}, "alpha is forced to stock 255")
nonzero = [(i % 128, i // 128) for i, pixel in enumerate(pixels) if any(pixel[:3])]
check(len(nonzero) == 4092, "nonzero RGB count")
check(
    (min(x for x, _ in nonzero), min(y for _, y in nonzero), max(x for x, _ in nonzero), max(y for _, y in nonzero))
    == (4, 0, 91, 90),
    "nonzero art stays in stock UV tile",
)
check(sum(sum(pixel[:3]) for pixel in pixels) == 343631, "premultiplied RGB energy")

metadata_differences = [
    index
    for index, pair in enumerate(zip(source_metadata, metadata))
    if pair[0] != pair[1]
]
check(len(metadata) == len(source_metadata) == 428, "metadata length")
check(len(metadata_differences) == 2, "metadata has two identity deltas")
check(
    all((source_metadata[index], metadata[index]) == (ord("3"), ord("4")) for index in metadata_differences),
    "metadata deltas are only identity bytes",
)
check(metadata.count(b"e_he_0004_all") == 1, "metadata internal model identity")
check(metadata.count(b"e_he_0004_a.tga") == 1, "metadata texture identity")
check(b"e_he_0003" not in canonical, "canonical package has no source identity")
check(trailer == bytes(8), "GWM trailer is eight zeros")

ARTIFACTS.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(prefix="octagram-gwm-test-", dir=ARTIFACTS) as temporary:
    root = Path(temporary)
    output = root / "e_he_0004_all.gwm"
    built = run_cli("--output", str(output))
    check(json.loads(built.stdout)["status"] == "Built", "CLI reports Built")
    check(output.read_bytes() == canonical, "CLI writes exact canonical bytes")
    checked = run_cli("--output", str(output), "--check")
    check(json.loads(checked.stdout)["status"] == "Verified", "CLI read-only check")
    repeated = run_cli("--output", str(output))
    check(json.loads(repeated.stdout)["status"] == "AlreadyBuilt", "CLI is idempotent")
    check(not list(root.glob("*.stage")), "CLI leaves no stages")

    output.write_bytes(canonical[:-1] + b"\x01")
    conflict = run_cli("--output", str(output), success=False)
    check("Refusing to overwrite changed output" in conflict.stderr, "changed output is refused")

    bad_png = root / "bad.png"
    bad_png.write_bytes(png[:-1] + bytes([png[-1] ^ 1]))
    rejected_png = run_cli(
        "--source-png", str(bad_png), "--output", str(root / "bad-png.gwm"), success=False
    )
    check("PNG hash changed" in rejected_png.stderr, "changed PNG is refused")
    bad_gwm = root / "bad-source.gwm"
    bad_gwm.write_bytes(source[:-1] + bytes([source[-1] ^ 1]))
    rejected_gwm = run_cli(
        "--source-gwm", str(bad_gwm), "--output", str(root / "bad-source-output.gwm"), success=False
    )
    check("GWM hash changed" in rejected_gwm.stderr, "changed source GWM is refused")

check(not LIVE_PROBE.exists(), "live refusal probe starts absent")
live_rejected = run_cli("--output", str(LIVE_PROBE), success=False)
check("refuses installed-client output" in live_rejected.stderr, "builder refuses live output")
check(not LIVE_PROBE.exists(), "builder never creates live refusal probe")

verified = run_cli("--check")
check(json.loads(verified.stdout)["status"] == "Verified", "canonical CLI verification")
check(snapshot(live_paths) == live_before, "builder test preserves the complete live snapshot")
for script in (
    BUILDER,
    TOOLS / "pet_owner_merge_effect" / "octagram_gwm.py",
    Path(__file__),
):
    check(script.stat().st_size < 20000, f"{script.name} remains below 20 KB")
    check(len(script.read_text(encoding="utf-8").splitlines()) < 600, f"{script.name} remains below 600 lines")

print(f"Owner-Merge octagram GWM passed: {assertions} assertions.")
