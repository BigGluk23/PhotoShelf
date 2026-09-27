#!/usr/bin/env python3
"""Verify an untouched extracted PhotoShelf handoff bundle using only Python stdlib."""
from pathlib import Path
import hashlib
import json
import sys

root = Path(sys.argv[1] if len(sys.argv) > 1 else ".").resolve()
manifest = json.loads((root / "PACKAGE-MANIFEST.json").read_text(encoding="utf-8"))
errors = []
for entry in manifest["files"]:
    relative = Path(entry["path"])
    if relative.is_absolute() or ".." in relative.parts:
        errors.append("Unsafe manifest path: " + str(relative))
        continue
    path = root / relative
    if path.is_symlink() or not path.is_file() or not path.resolve().is_relative_to(root):
        errors.append("Missing/unsafe file: " + str(relative))
        continue
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    if path.stat().st_size != entry["size"] or digest.hexdigest() != entry["sha256"]:
        errors.append("Mismatch: " + str(relative))
if errors:
    print("\n".join(errors))
    raise SystemExit(1)
print(f"Verified {len(manifest['files'])} files for {manifest['display_name']}")
