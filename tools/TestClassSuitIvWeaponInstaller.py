"""Offline transaction and path-safety checks for the Tier IV installer."""

from __future__ import annotations

from pathlib import Path
import tempfile

from InstallClassSuitIvWeaponAssets import (
    AssetInstallError,
    DEFAULT_MODEL_STAGE,
    DEFAULT_TEXTURE_STAGE,
    install,
    load_all_assets,
    restore_backup,
    safe_relative,
    verify_installed,
)


def expect_error(action, label: str) -> None:
    try:
        action()
    except AssetInstallError:
        return
    raise AssertionError(f"Expected AssetInstallError: {label}")


def main() -> int:
    checks = 0
    assets = load_all_assets(DEFAULT_MODEL_STAGE, DEFAULT_TEXTURE_STAGE)
    assert len(assets) == 32
    assert sum(path.suffix == ".jcs" for path in assets) == 16
    assert sum(path.suffix == ".gwo" for path in assets) == 16
    checks += 1

    expect_error(lambda: safe_relative("../Origin.exe"), "parent traversal")
    expect_error(lambda: safe_relative(r"C:\Origin.exe"), "absolute target")
    expect_error(lambda: safe_relative("Localization/file.gwo"), "wrong root")
    checks += 1

    with tempfile.TemporaryDirectory(prefix="reborn-tier4-install-") as folder:
        root = Path(folder)
        client = root / "client"
        backups = root / "backups"
        client.mkdir()
        originals: dict[Path, bytes | None] = {}
        for index, relative in enumerate(sorted(assets, key=str)):
            original = (
                f"stock:{relative.as_posix()}".encode("utf-8")
                if index % 2 == 0
                else None
            )
            originals[relative] = original
            if original is not None:
                target = client / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(original)

        backup = install(client.resolve(), backups, assets)
        verify_installed(client.resolve(), assets)
        assert (backup / "manifest.json").is_file()
        checks += 1

        restore_backup(client.resolve(), backup)
        for relative, original in originals.items():
            target = client / relative
            if original is None:
                assert not target.exists()
            else:
                assert target.read_bytes() == original
        checks += 1

        other_client = root / "other-client"
        other_client.mkdir()
        expect_error(
            lambda: restore_backup(other_client.resolve(), backup),
            "backup client-root mismatch",
        )
        checks += 1

    print(f"PASS: {checks} offline Class Suit IV installer checks")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
