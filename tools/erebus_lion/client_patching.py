from __future__ import annotations

from pathlib import Path
import re

from InstallLevel5ForgeIcons import (
    display_pixel_index,
    parse_tga,
    patch_atlas,
    validate_generated_atlas,
)

from .common import InstallError, sha256_bytes
from .constants import (
    EXPECTED_ICON2_SHA256,
    EXPECTED_SOURCE_MODEL_SHA256,
    EXPECTED_TEXTURE_SHA256,
    ICON_SIZE,
    ITEM_BASE_ID,
    ITEM_COUNT,
    LEGACY_RIDE_STATUS_IDS,
    LOCALES,
    MOUNT_DESCRIPTION,
    MOUNT_LEVELS,
    MOUNT_MAX_HP,
    MOUNT_NAME,
    MOUNT_SPEEDS,
    RIDE_SECTION_ID,
    RIDE_STATUS_ID,
    SOURCE_ICON_X,
    SOURCE_ICON_Y,
    SOURCE_MODEL_NAME,
    SOURCE_TEXTURE_NAME,
    TARGET_ICON_X,
    TARGET_ICON_Y,
    TARGET_MODEL_NAME,
    TARGET_TEXTURE_NAME,
)
from .image_assets import (
    atlas_cell_bgra,
    encode_png_rgba,
    encode_tga_image,
    image_to_display_rgba,
    parse_tga_image,
    recolor_pixels,
    sprite_bgra_to_rgba,
)
from .model_codec import enlarge_erebus_model


def repeated(value: str | int) -> str:
    return ",".join([str(value)] * 20)


def mount_element(offset: int) -> str:
    item_id = ITEM_BASE_ID + offset
    speed = f"{MOUNT_SPEEDS[offset]:.2f}"
    return (
        f'<Ride{item_id} ID="{item_id}" Type="mount" '
        'Texture="./Localization/en_us/UI/Texture/Icon4.gwo" '
        f'Icon="{TARGET_ICON_X},{TARGET_ICON_Y}" Random="0" Distribution="0,0" '
        f'Speed="{repeated(speed)}" MaxHP="{repeated(MOUNT_MAX_HP[offset])}" '
        'Money="0" Overlap="1" Equip="1" Use="1" SkillFlag="20" '
        f'Class="0,1,2,3" PlayLv="{MOUNT_LEVELS[offset]},200" />'
    )


def set_xml_item(
    text: str,
    item_id: int,
    anchor_id: int,
    element: str,
) -> str:
    pattern = re.compile(rf'<[A-Za-z_][\w]*\s+ID="{item_id}"[^<>]*/>')
    matches = list(pattern.finditer(text))
    if len(matches) > 1:
        raise InstallError(f"Duplicate ItemBaseAttribute ID {item_id}")
    if matches:
        match = matches[0]
        return text[: match.start()] + element + text[match.end() :]

    anchor_pattern = re.compile(
        rf'<[A-Za-z_][\w]*\s+ID="{anchor_id}"[^<>]*/>'
    )
    anchors = list(anchor_pattern.finditer(text))
    if len(anchors) != 1:
        raise InstallError(
            f"Expected one ItemBaseAttribute anchor ID {anchor_id}; "
            f"found {len(anchors)}"
        )
    anchor = anchors[0]
    line_start = text.rfind("\n", 0, anchor.start()) + 1
    indent_match = re.match(r"^[ \t]*", text[line_start : anchor.start()])
    indent = indent_match.group(0) if indent_match else ""
    line_formatted = (
        anchor.end() >= len(text)
        or text[anchor.end()] in "\r\n"
    )
    newline = "\r\n" if "\r\n" in text else "\n"
    separator = newline + indent if line_formatted else ""
    insert = anchor.group(0) + separator + element
    return text[: anchor.start()] + insert + text[anchor.end() :]


def set_localized_line(
    text: str,
    key: str,
    value: str,
    anchor_key: str,
) -> str:
    line = f"{key}\t{value}"
    pattern = re.compile(rf"(?m)^{re.escape(key)}\t[^\r\n]*(?=\r?$)")
    matches = list(pattern.finditer(text))
    if len(matches) > 1:
        raise InstallError(f"Duplicate localization key {key}")
    if matches:
        match = matches[0]
        return text[: match.start()] + line + text[match.end() :]

    anchor_pattern = re.compile(
        rf"(?m)^{re.escape(anchor_key)}\t[^\r\n]*(?=\r?$)"
    )
    anchors = list(anchor_pattern.finditer(text))
    if len(anchors) != 1:
        raise InstallError(
            f"Expected one localization anchor {anchor_key}; "
            f"found {len(anchors)}"
        )
    anchor = anchors[0]
    newline = "\r\n" if "\r\n" in text else "\n"
    insert = anchor.group(0) + newline + line
    return text[: anchor.start()] + insert + text[anchor.end() :]


def set_ini_section(text: str, section_id: int, body: str) -> str:
    newline = "\r\n" if "\r\n" in text else "\n"
    canonical = (
        f"[{section_id}]"
        + newline
        + body.replace("\n", newline).rstrip()
        + newline
    )
    pattern = re.compile(
        rf"(?ms)^\[{section_id}\]\r?\n.*?(?=^\[[^\]\r\n]+\]\r?$|\Z)"
    )
    matches = list(pattern.finditer(text))
    if len(matches) > 1:
        raise InstallError(f"Duplicate INI section [{section_id}]")
    if matches:
        match = matches[0]
        return text[: match.start()] + canonical + text[match.end() :]
    return text.rstrip("\r\n") + newline + newline + canonical


def remove_legacy_erebus_status_sections(text: str) -> str:
    for section_id in LEGACY_RIDE_STATUS_IDS:
        pattern = re.compile(
            rf"(?ms)^\[{section_id}\]\r?\n.*?"
            rf"(?=^\[[^\]\r\n]+\]\r?$|\Z)"
        )
        matches = list(pattern.finditer(text))
        if len(matches) > 1:
            raise InstallError(
                f"Duplicate legacy INI section [{section_id}]"
            )
        if not matches:
            continue
        match = matches[0]
        section = match.group(0)
        if (
            f"RideId={RIDE_SECTION_ID}" not in section
            or "Kind=110" not in section
        ):
            continue
        text = text[: match.start()] + text[match.end() :]
    return text


def patch_item_base(data: bytes) -> bytes:
    text = data.decode("utf-8-sig")
    anchor = 16199
    for offset in range(ITEM_COUNT):
        item_id = ITEM_BASE_ID + offset
        text = set_xml_item(
            text,
            item_id,
            anchor,
            mount_element(offset),
        )
        anchor = item_id
    return b"\xef\xbb\xbf" + text.encode("utf-8")


def patch_localized_mount_text(
    data: bytes,
    locale: str,
    description: bool,
) -> bytes:
    encoding = "utf-16" if locale == "en_us" else "cp936"
    text = data.decode(encoding)
    # The special Owl item 16199 intentionally reuses the Ride16198 text key.
    anchor = "Ride16198"
    for offset in range(ITEM_COUNT):
        key = f"Ride{ITEM_BASE_ID + offset}"
        if description:
            value = (
                MOUNT_DESCRIPTION
                if locale == "en_us"
                else "\u8bde\u751f\u4e8e\u5384\u745e\u73bb\u65af\u9ed1"
                "\u6697\u4e2d\u7684\u96c4\u72ee\uff0c\u6f06\u9ed1\u5982"
                "\u65e0\u6708\u4e4b\u591c\u3002"
            )
        else:
            value = (
                MOUNT_NAME
                if locale == "en_us"
                else "\u5384\u745e\u73bb\u65af\u4e4b\u72ee"
            )
        text = set_localized_line(text, key, value, anchor)
        anchor = key
    return text.encode(encoding)


def patch_ride_ini(data: bytes) -> bytes:
    # Ride.ini contains legacy mixed-codepage labels in some client builds.
    # The syntax and our new values are ASCII, so Latin-1 provides a strict
    # byte-preserving round trip for all existing content.
    text = data.decode("latin-1")
    count_pattern = re.compile(r"(?m)^Count=\d+\r?$")
    matches = list(count_pattern.finditer(text))
    if len(matches) != 1:
        raise InstallError(
            f"Expected one Ride.ini Count; found {len(matches)}"
        )
    text = count_pattern.sub("Count=118", text)
    body = "\n".join(
        (
            f"Name={MOUNT_NAME}",
            f"StatuID={RIDE_STATUS_ID}",
            f"XFile={TARGET_MODEL_NAME}",
            f"TextureFile={TARGET_TEXTURE_NAME}",
            "ActionPlayerIdle=_Lion_ride_stand_00",
            "ActionPlayerRun=_Lion_ride_run_01",
            "AlphaRefVal=0",
            "SrcBlendFactor=1",
            "DestBlendFactor=0",
            "ZWrite=1",
            "CullingMode=2",
            "ZBuffer=1",
            "AlphaComFun=4",
            "ConstBlendValue=-1",
            "HeadDisplayHeight=43",
        )
    )
    return set_ini_section(
        text,
        RIDE_SECTION_ID,
        body,
    ).encode("latin-1")


def patch_status_ini(data: bytes, locale: str) -> bytes:
    text = data.decode("utf-16")
    text = remove_legacy_erebus_status_sections(text)
    name = f"Travelling by {MOUNT_NAME}"
    note = f"You are riding an {MOUNT_NAME}."
    if locale == "zh_cn":
        name = "\u4e58\u9a91\u5384\u745e\u73bb\u65af\u4e4b\u72ee"
        note = (
            "\u4f60\u6b63\u5728\u4e58\u9a91\u5384\u745e\u73bb\u65af"
            "\u4e4b\u72ee\u3002"
        )
    body = "\n".join(
        (
            f"Name={name}",
            "Style=1",
            "Kind=110",
            "Priority=1",
            "Effect=33",
            "Values=1",
            "Interval=0",
            "Time=-1",
            f"Note={note}",
            "IconPos=360,0",
            "IconSize=36,36",
            "EffectDisplay=-1",
            f"RideId={RIDE_SECTION_ID}",
            "Action=0",
        )
    )
    return set_ini_section(
        text,
        RIDE_STATUS_ID,
        body,
    ).encode("utf-16")


def ensure_icon_cell_unclaimed(item_base_data: bytes) -> None:
    text = item_base_data.decode("utf-8-sig")
    pattern = re.compile(
        rf'<([A-Za-z_][\w]*)\s+ID="(\d+)"[^<>]*'
        rf'Texture="\./Localization/en_us/UI/Texture/Icon4\.gwo"[^<>]*'
        rf'Icon="{TARGET_ICON_X},{TARGET_ICON_Y}"[^<>]*/>'
    )
    claims = [
        (match.group(1), int(match.group(2)))
        for match in pattern.finditer(text)
        if not ITEM_BASE_ID
        <= int(match.group(2))
        < ITEM_BASE_ID + ITEM_COUNT
    ]
    if claims:
        raise InstallError(
            f"Icon4 cell {TARGET_ICON_X},{TARGET_ICON_Y} is "
            f"already claimed: {claims}"
        )


def build_outputs(
    client_root: Path,
) -> tuple[dict[Path, bytes], dict[str, bytes]]:
    ride_root = client_root / "Ride"
    source_model_path = ride_root / SOURCE_MODEL_NAME
    source_texture_path = ride_root / SOURCE_TEXTURE_NAME
    if (
        not source_model_path.is_file()
        or not source_texture_path.is_file()
    ):
        raise InstallError("Required African Lion source assets are missing")

    source_model = source_model_path.read_bytes()
    source_texture_data = source_texture_path.read_bytes()
    if sha256_bytes(source_model) != EXPECTED_SOURCE_MODEL_SHA256:
        raise InstallError(
            f"Unexpected source model content: {source_model_path}"
        )
    if sha256_bytes(source_texture_data) != EXPECTED_TEXTURE_SHA256:
        raise InstallError(
            f"Unexpected source texture content: {source_texture_path}"
        )

    source_texture = parse_tga_image(
        source_texture_data,
        SOURCE_TEXTURE_NAME,
    )
    recolored_pixels, changed_pixels = recolor_pixels(
        source_texture.pixels
    )
    if changed_pixels < source_texture.width * source_texture.height // 5:
        raise InstallError(
            f"Fur recolour changed too few pixels: {changed_pixels}"
        )
    for position in range(3, len(recolored_pixels), 4):
        if recolored_pixels[position] != source_texture.pixels[position]:
            raise InstallError("Fur recolour changed the alpha channel")
    target_texture = encode_tga_image(
        source_texture,
        recolored_pixels,
    )
    parsed_target = parse_tga_image(
        target_texture,
        TARGET_TEXTURE_NAME,
    )
    if (
        parsed_target.width != source_texture.width
        or parsed_target.height != source_texture.height
        or parsed_target.descriptor != source_texture.descriptor
        or parsed_target.pixels != recolored_pixels
    ):
        raise InstallError(
            "Generated mount texture failed its round-trip validation"
        )

    target_model = enlarge_erebus_model(source_model)
    outputs: dict[Path, bytes] = {
        ride_root / TARGET_MODEL_NAME: target_model,
        ride_root / TARGET_TEXTURE_NAME: target_texture,
    }
    previews: dict[str, bytes] = {
        "african-lion-high-tier.png": encode_png_rgba(
            source_texture.width,
            source_texture.height,
            image_to_display_rgba(source_texture),
        ),
        "erebus-lion-texture.png": encode_png_rgba(
            source_texture.width,
            source_texture.height,
            image_to_display_rgba(source_texture, recolored_pixels),
        ),
    }

    source_icon_data: bytes | None = None
    source_icon_atlas = None
    icon_sprite: bytes | None = None
    original_icon_sprite: bytes | None = None

    for locale in LOCALES:
        locale_root = client_root / "Localization" / locale
        item_path = (
            locale_root / "Settings" / "Sys" / "ItemBaseAttribute.xml"
        )
        ride_path = locale_root / "Settings" / "Sys" / "Ride.ini"
        status_path = locale_root / "Settings" / "Sys" / "Status.ini"
        name_path = locale_root / "Text" / "EquipName.dat"
        description_path = (
            locale_root / "Text" / "EquipDescription.dat"
        )
        icon2_path = locale_root / "UI" / "Texture" / "Icon2.gwo"
        icon4_path = locale_root / "UI" / "Texture" / "Icon4.gwo"
        for path in (
            item_path,
            ride_path,
            status_path,
            name_path,
            description_path,
            icon2_path,
            icon4_path,
        ):
            if not path.is_file():
                raise InstallError(
                    f"Required client file is missing: {path}"
                )

        item_data = item_path.read_bytes()
        ensure_icon_cell_unclaimed(item_data)
        outputs[item_path] = patch_item_base(item_data)
        outputs[ride_path] = patch_ride_ini(ride_path.read_bytes())
        outputs[status_path] = patch_status_ini(
            status_path.read_bytes(),
            locale,
        )
        outputs[name_path] = patch_localized_mount_text(
            name_path.read_bytes(),
            locale,
            description=False,
        )
        outputs[description_path] = patch_localized_mount_text(
            description_path.read_bytes(),
            locale,
            description=True,
        )

        current_icon2_data = icon2_path.read_bytes()
        if sha256_bytes(current_icon2_data) != EXPECTED_ICON2_SHA256:
            raise InstallError(
                f"Unexpected source icon atlas content: {icon2_path}"
            )
        if source_icon_data is None:
            source_icon_data = current_icon2_data
            source_icon_atlas = parse_tga(
                current_icon2_data,
                str(icon2_path),
            )
            original_icon_sprite = atlas_cell_bgra(
                source_icon_atlas,
                SOURCE_ICON_X,
                SOURCE_ICON_Y,
                recolor=False,
            )
            icon_sprite = atlas_cell_bgra(
                source_icon_atlas,
                SOURCE_ICON_X,
                SOURCE_ICON_Y,
                recolor=True,
            )
            previews["african-lion-icon.png"] = encode_png_rgba(
                ICON_SIZE,
                ICON_SIZE,
                sprite_bgra_to_rgba(original_icon_sprite),
            )
            previews["erebus-lion-icon.png"] = encode_png_rgba(
                ICON_SIZE,
                ICON_SIZE,
                sprite_bgra_to_rgba(icon_sprite),
            )
        elif current_icon2_data != source_icon_data:
            raise InstallError(
                "Locale Icon2.gwo copies are not byte-identical"
            )

        assert icon_sprite is not None
        icon4 = parse_tga(icon4_path.read_bytes(), str(icon4_path))
        desired: dict[int, bytes] = {}
        for row in range(ICON_SIZE):
            for column in range(ICON_SIZE):
                destination_index = display_pixel_index(
                    icon4,
                    TARGET_ICON_X + column,
                    TARGET_ICON_Y + row,
                )
                source_offset = (row * ICON_SIZE + column) * 4
                desired[destination_index] = icon_sprite[
                    source_offset : source_offset + 4
                ]
        generated_icon4 = patch_atlas(icon4, desired)
        validate_generated_atlas(
            icon4,
            generated_icon4,
            desired,
            str(icon4_path),
        )
        outputs[icon4_path] = generated_icon4

    return outputs, previews
