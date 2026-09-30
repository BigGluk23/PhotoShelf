"""Create/verify a release inventory. Runtime LGPL replacements are not gated by these hashes."""
import argparse
import hashlib
import json
import re
import stat
from pathlib import Path, PurePosixPath

MANIFEST = "package-manifest.json"
MAX_FILES = 1024
MAX_MANIFEST_BYTES = 256 * 1024
REQUIRED = {
    "PhotoShelf.exe", "PhotoShelf.Updater.exe", "RUNNING.txt", "codecs/heif/PhotoShelf.HeifWorker.exe",
    "codecs/heif/heif.dll", "codecs/heif/libde265.dll", "codecs/heif/VERSION.txt",
    "codecs/heif/sources/sources.json",
}


def safe_name(name):
    if not isinstance(name, str) or not name or "\\" in name or ":" in name or "\x00" in name:
        raise ValueError("Unsafe manifest path")
    parts = name.split("/")
    reserved = {"CON", "PRN", "AUX", "NUL", *(f"COM{i}" for i in range(1, 10)), *(f"LPT{i}" for i in range(1, 10))}
    if PurePosixPath(name).is_absolute() or any(
            part in ("", ".", "..") or part.endswith((".", " ")) or
            part.split(".")[0].upper() in reserved or any(ord(c) < 32 or c in '<>"|?*' for c in part)
            for part in parts):
        raise ValueError("Unsafe manifest path: " + name)
    return name


def inventory(root):
    root = Path(root)
    entries = {}
    seen = set()
    for path in root.rglob("*"):
        info = path.lstat()
        if path.is_symlink() or getattr(info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 1024):
            raise ValueError("Links/reparse points are not release files: " + str(path))
        if path.is_dir():
            continue
        name = safe_name(path.relative_to(root).as_posix())
        if name == MANIFEST:
            continue
        if not stat.S_ISREG(info.st_mode) or name.casefold() in seen:
            raise ValueError("Nonregular or case-colliding release file: " + name)
        seen.add(name.casefold())
        if len(entries) >= MAX_FILES:
            raise ValueError("Release file count exceeds limit")
        digest = hashlib.sha256()
        with path.open("rb") as source:
            while chunk := source.read(1024 * 1024):
                digest.update(chunk)
        entries[name] = {"path": name, "length": info.st_size, "sha256": digest.hexdigest()}
    if REQUIRED - entries.keys():
        raise ValueError("Missing required release files: " + ", ".join(sorted(REQUIRED - entries.keys())))
    if not any(name.startswith("licenses/") for name in entries) or not any(name.startswith("codecs/heif/licenses/") for name in entries):
        raise ValueError("Missing release licenses")
    return entries


def identity(version, commit):
    if not re.fullmatch(r"\d+\.\d+\.\d+-ultra", version) or not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("Invalid release version/commit")


def create(root, version, commit):
    identity(version, commit)
    root = Path(root)
    manifest = root / MANIFEST
    if manifest.exists():
        raise ValueError("A release manifest already exists; use a new publish directory")
    running = root / "RUNNING.txt"
    lines = running.read_text(encoding="utf-8-sig").splitlines()
    if not lines or lines[0] != "PhotoShelf Ultra — Windows x64":
        raise ValueError("RUNNING.txt must use the version-neutral source heading")
    lines[0] = f"PhotoShelf Ultra v{version.removesuffix('-ultra')} — Windows x64"
    running.write_text("\n".join(lines) + "\n", encoding="utf-8")
    data = {"schema": 1, "product": "PhotoShelf Ultra", "version": version, "commit": commit,
            "purpose": "Integrity of the distributed archive; compatible trusted LGPL replacements remain allowed at runtime.",
            "files": sorted(inventory(root).values(), key=lambda entry: entry["path"])}
    with manifest.open("x", encoding="utf-8") as output:
        json.dump(data, output, indent=2, ensure_ascii=False)
        output.write("\n")
    return verify(root, version, commit)


def verify(root, version, commit):
    identity(version, commit)
    root = Path(root)
    manifest = root / MANIFEST
    if manifest.is_symlink() or manifest.stat().st_size > MAX_MANIFEST_BYTES:
        raise ValueError("Invalid release manifest file")
    data = json.loads(manifest.read_text(encoding="utf-8-sig"))
    if data.get("schema") != 1 or data.get("product") != "PhotoShelf Ultra" or data.get("version") != version or data.get("commit") != commit:
        raise ValueError("Release manifest identity mismatch")
    files = data.get("files")
    if not isinstance(files, list) or not 1 <= len(files) <= MAX_FILES:
        raise ValueError("Invalid manifest file count")
    expected = {}
    seen = set()
    for entry in files:
        name = safe_name(entry.get("path"))
        if name.casefold() in seen or name.casefold() == MANIFEST.casefold():
            raise ValueError("Repeated manifest path")
        if type(entry.get("length")) is not int or entry["length"] < 0 or not re.fullmatch(r"[0-9a-f]{64}", entry.get("sha256", "")):
            raise ValueError("Invalid manifest file metadata")
        seen.add(name.casefold())
        expected[name] = entry
    actual = inventory(root)
    if expected.keys() != actual.keys():
        raise ValueError("Release file set mismatch (missing or stale files)")
    for name, entry in actual.items():
        if entry != expected[name]:
            raise ValueError("Release file checksum/length mismatch: " + name)
    heading = (root / "RUNNING.txt").read_text(encoding="utf-8-sig").splitlines()[0]
    if heading != f"PhotoShelf Ultra v{version.removesuffix('-ultra')} — Windows x64":
        raise ValueError("Packaged instructions have a different version")
    return {"status": "passed", "version": version, "commit": commit, "filesVerified": len(actual),
            "manifestSha256": hashlib.sha256(manifest.read_bytes()).hexdigest()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("create", "verify"))
    parser.add_argument("directory", type=Path)
    parser.add_argument("--version", required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    try:
        result = (create if args.mode == "create" else verify)(args.directory, args.version, args.commit)
        if args.report:
            with args.report.open("x", encoding="utf-8") as output:
                json.dump(result, output, indent=2)
        print(f"OK release {args.mode}: {result['filesVerified']} files, {args.version}, {args.commit}")
        return 0
    except (ValueError, OSError, TypeError, KeyError, AttributeError) as error:
        print("FAILED release inventory: " + str(error))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
