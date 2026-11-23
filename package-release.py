#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import subprocess
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent
DIST = ROOT / "dist"
INSTALLER = DIST / "CHENMU-Browser-Setup-x64.exe"
PORTABLE_ZIP = DIST / "CHENMU-Browser-Windows-x64.zip"
SOURCE_ZIP = DIST / "CHENMU-Browser-source.zip"
CHECKSUMS = DIST / "SHA256SUMS.txt"
EXCLUDED_NAMES = {
    INSTALLER.name,
    PORTABLE_ZIP.name,
    SOURCE_ZIP.name,
    CHECKSUMS.name,
}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> None:
    if not INSTALLER.is_file():
        raise SystemExit("Installer not found. Run build-windows.ps1 first.")
    if not (DIST / "WebView2Loader.dll").is_file():
        raise SystemExit("WebView2Loader.dll is missing from the x64 build output.")
    if not (DIST / "CHENMU Browser.exe").is_file():
        raise SystemExit("CHENMU Browser.exe is missing from the build output.")

    for artifact in (PORTABLE_ZIP, SOURCE_ZIP, CHECKSUMS):
        artifact.unlink(missing_ok=True)

    with zipfile.ZipFile(PORTABLE_ZIP, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        files = sorted(path for path in DIST.rglob("*") if path.is_file())
        for path in files:
            if path.name in EXCLUDED_NAMES or path.suffix.lower() == ".pdb":
                continue
            archive.write(path, path.relative_to(DIST).as_posix())

    tracked_files = subprocess.check_output(["git", "ls-files", "-z"], cwd=ROOT).split(b"\0")
    with zipfile.ZipFile(SOURCE_ZIP, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for raw_path in tracked_files:
            if not raw_path:
                continue
            path = ROOT / raw_path.decode("utf-8")
            if path.is_file():
                archive.write(path, path.relative_to(ROOT).as_posix())

    assets = (INSTALLER, PORTABLE_ZIP, SOURCE_ZIP)
    lines = [f"{sha256(path)}  {path.name}" for path in assets]
    CHECKSUMS.write_text("\n".join(lines) + "\n", encoding="ascii")
    print("Release assets created:")
    for path in (*assets, CHECKSUMS):
        print(f"  {path} ({path.stat().st_size:,} bytes)")


if __name__ == "__main__":
    main()
