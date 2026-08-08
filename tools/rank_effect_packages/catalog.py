"""Canonical client filenames and protected rank boundaries."""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import re

from .errors import RankEffectError


ASSET_ROOTS = ("Characters", "Characters_New")
GENDERS = ("female", "male")
ARMOR_RANKS = tuple(range(10, 15))
PROTECTED_ARMOR_RANK = 9
WEAPON_RANK = 10


@dataclass(frozen=True, slots=True)
class WeaponEffect:
    class_name: str
    family: str
    effect_id: int
    protected_effect_ids: tuple[int, ...]

    def stem(self, gender: str) -> str:
        return f"{gender}_{self.family}_effect_{self.effect_id:04d}"


# ItemBaseAttribute.xml maps logical WR10 to these effect IDs. The 0100 family
# exists in the client but is not selected by current forgeable weapon rows.
WEAPON_EFFECTS = {
    "warrior": WeaponEffect("warrior", "weapononehand", 9, (6, 8)),
    "champion": WeaponEffect("champion", "weapontwohand", 9, (6, 8)),
    "priest": WeaponEffect("priest", "weapononehand", 209, (206, 208)),
    "mage": WeaponEffect("mage", "weapontwohand", 59, (56, 58)),
}


def safe_asset_path(value: object) -> Path:
    if not isinstance(value, str) or not value or "\x00" in value:
        raise RankEffectError("Manifest contains an invalid asset path")
    normalized = value.replace("\\", "/")
    path = Path(normalized)
    if path.is_absolute() or ".." in path.parts or len(path.parts) != 3:
        raise RankEffectError(f"Asset path escapes the effect root: {value}")
    if path.parts[0] not in ASSET_ROOTS or path.parts[1] != "effect":
        raise RankEffectError(f"Asset path is outside a client effect tree: {value}")
    return path


def safe_protected_path(value: object) -> Path:
    """Accept a protected dependency in an effect folder or character root."""

    if not isinstance(value, str) or not value or "\x00" in value:
        raise RankEffectError("Protected baseline contains an invalid path")
    path = Path(value.replace("\\", "/"))
    if path.is_absolute() or ".." in path.parts or path.parts[0] not in ASSET_ROOTS:
        raise RankEffectError(f"Protected path escapes the character roots: {value}")
    if len(path.parts) == 2 or (len(path.parts) == 3 and path.parts[1] == "effect"):
        return path
    raise RankEffectError(f"Protected path is outside reviewed asset folders: {value}")


def armor_stem(gender: str, rank: int) -> str:
    _validate_gender(gender)
    if rank not in ARMOR_RANKS:
        raise RankEffectError(f"Armor package rank must be AR10..AR14, got {rank}")
    return f"{gender}_body_effect_{rank:04d}"


def weapon_stem(gender: str, class_name: str) -> str:
    _validate_gender(gender)
    try:
        return WEAPON_EFFECTS[class_name].stem(gender)
    except KeyError as error:
        raise RankEffectError(f"Unknown weapon class: {class_name}") from error


def _validate_gender(gender: str) -> None:
    if gender not in GENDERS:
        raise RankEffectError(f"Unknown effect gender: {gender}")


def expected_model_pattern(kind: str, stem: str) -> re.Pattern[str]:
    hand = "_right" if kind == "weapon" and "weapononehand" in stem else ""
    return re.compile(rf"^{re.escape(stem + hand)}_(\d+)\.jcs$")


def private_texture_pattern(kind: str, rank: int, gender: str, class_name: str | None) -> re.Pattern[str]:
    if kind == "armor":
        # Body-effect JCS files historically point at the female texture even
        # for male geometry, so a rank-private shared payload is deliberate.
        return re.compile(
            rf"^reborn_body_effect_{rank:04d}(?:_[a-z0-9_]+)?\.tga$"
        )
    else:
        prefix = f"reborn_wr{rank:02d}_{class_name}_{gender}_"
    return re.compile(rf"^{re.escape(prefix)}[a-z0-9_]+\.tga$")
