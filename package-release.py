#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import re
import subprocess
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent
DIST = ROOT / "dist"
INSTALLER = DIST / "CHENMU-Browser-Setup-x64.exe"
PORTABLE_ZIP = DIST / "CHENMU-Browser-Windows-x64.zip"
SOURCE_ZIP = DIST / "CHENMU-Browser-source.zip"
CHECKSUMS = DIST / "SHA256SUMS.txt"
EXPECTED_ASSETS = (INSTALLER, PORTABLE_ZIP, SOURCE_ZIP)
EXCLUDED_NAMES = {path.name for path in (*EXPECTED_ASSETS, CHECKSUMS)}
SHA256_LINE = re.compile(r"^[0-9a-fA-F]{64}  ([^\r\n ]+)$")


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def require_nonempty_file(path: Path, description: str) -> None:
    if not path.is_file():
        raise SystemExit(f"{description} not found: {path}")
    if path.stat().st_size == 0:
        raise SystemExit(f"{description} is empty: {path}")


def require_pe_asset(path: Path, description: str) -> None:
    require_nonempty_file(path, description)
    with path.open("rb") as stream:
        if stream.read(2) != b"MZ":
            raise SystemExit(f"{description} is not a valid Windows PE asset: {path}")


def verify_zip(path: Path, description: str) -> None:
    require_nonempty_file(path, description)
    try:
        with zipfile.ZipFile(path, "r") as archive:
            names = archive.namelist()
            if not names:
                raise SystemExit(f"{description} contains no files: {path}")
            if len(names) != len(set(names)):
                raise SystemExit(f"{description} contains duplicate entries: {path}")
            bad = archive.testzip()
            if bad is not None:
                raise SystemExit(f"{description} contains corrupted entry {bad!r}: {path}")
            for info in archive.infolist():
                if info.is_dir():
                    continue
                with archive.open(info, "r") as stream:
                    while stream.read(1024 * 1024):
                        pass
    except zipfile.BadZipFile as error:
        raise SystemExit(f"{description} is not a readable ZIP: {path} ({error})") from error


def verify_checksums() -> None:
    require_nonempty_file(CHECKSUMS, "SHA256SUMS.txt")
    try:
        lines = CHECKSUMS.read_text(encoding="ascii").splitlines()
    except (UnicodeDecodeError, OSError) as error:
        raise SystemExit(f"SHA256SUMS.txt is not valid ASCII text: {error}") from error

    expected_names = {path.name for path in EXPECTED_ASSETS}
    if len(lines) != len(EXPECTED_ASSETS) or any(not line for line in lines):
        raise SystemExit("SHA256SUMS.txt must contain exactly three non-empty lines.")
    seen: set[str] = set()
    declared: dict[str, str] = {}
    for line in lines:
        match = SHA256_LINE.fullmatch(line)
        if match is None:
            raise SystemExit(f"Invalid SHA-256 line in SHA256SUMS.txt: {line!r}")
        name = match.group(1)
        if name not in expected_names:
            raise SystemExit(f"Unexpected asset in SHA256SUMS.txt: {name}")
        if name in seen:
            raise SystemExit(f"Duplicate asset in SHA256SUMS.txt: {name}")
        seen.add(name)
        declared[name] = line[:64].lower()
    if seen != expected_names:
        missing = ", ".join(sorted(expected_names - seen))
        raise SystemExit(f"SHA256SUMS.txt is missing expected asset(s): {missing}")
    for path in EXPECTED_ASSETS:
        actual = sha256(path)
        if declared[path.name] != actual:
            raise SystemExit(f"SHA-256 mismatch for {path.name}: manifest has {declared[path.name]}, actual is {actual}")


def main() -> None:
    require_pe_asset(INSTALLER, "Installer")
    require_pe_asset(DIST / "WebView2Loader.dll", "WebView2Loader.dll")
    require_pe_asset(DIST / "CHENMU Browser.exe", "CHENMU Browser.exe")

    for artifact in (PORTABLE_ZIP, SOURCE_ZIP, CHECKSUMS):
        artifact.unlink(missing_ok=True)

    with zipfile.ZipFile(PORTABLE_ZIP, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        files = sorted(path for path in DIST.rglob("*") if path.is_file())
        for path in files:
            if path.name in EXCLUDED_NAMES or path.suffix.lower() == ".pdb":
                continue
            archive.write(path, path.relative_to(DIST).as_posix())
    verify_zip(PORTABLE_ZIP, "Portable ZIP")

    tracked_files = subprocess.check_output(["git", "ls-files", "-z"], cwd=ROOT).split(b"\0")
    with zipfile.ZipFile(SOURCE_ZIP, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for raw_path in tracked_files:
            if not raw_path:
                continue
            path = ROOT / raw_path.decode("utf-8")
            if path.is_file():
                archive.write(path, path.relative_to(ROOT).as_posix())
    verify_zip(SOURCE_ZIP, "Source ZIP")

    lines = [f"{sha256(path)}  {path.name}" for path in EXPECTED_ASSETS]
    CHECKSUMS.write_text("\n".join(lines) + "\n", encoding="ascii")
    verify_checksums()

    print("Release assets created and verified:")
    for path in (*EXPECTED_ASSETS, CHECKSUMS):
        print(f"  {path} ({path.stat().st_size:,} bytes)")


if __name__ == "__main__":
    main()
