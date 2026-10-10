#!/usr/bin/env python3
"""Instrument an owned source copy only; shipping sources and trust stay unchanged."""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import tempfile


def prepare(source, work):
    source = Path(source).resolve()
    work = Path(work).resolve()
    token = work.name.removeprefix("PhotoShelf-acceptance-")
    if work.parent != Path(tempfile.gettempdir()).resolve() or len(token) != 32 or any(c not in "0123456789abcdef" for c in token):
        raise ValueError("Fresh workspace must have a generated acceptance identity")
    work.mkdir(parents=False, exist_ok=False)
    (work / "owner.json").write_text(json.dumps({"schema": 1, "token": token}), encoding="utf-8")
    copied = work / "source"
    copied.mkdir()
    names = subprocess.check_output(["git", "ls-files", "-z"], cwd=source).decode().split("\0")
    for name in filter(None, names):
        src = source / name
        if src.is_symlink() or not src.is_file():
            raise ValueError("Tracked source must be an ordinary file")
        dest = copied / name
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(src, dest)
    desktop = copied / "src/PhotoShelf.Desktop"
    program = desktop / "Program.cs"
    marker = "        UpdateStartupLease? startupLease = null;"
    encoded = program.read_text(encoding="utf-8")
    if encoded.count(marker) != 1:
        raise ValueError("Production entrypoint changed; review the test copy instrumentation")
    program.write_text(encoded.replace(marker,
        "        if (WindowsAcceptance.Enabled) return WindowsAcceptance.Run(args);\n" + marker), encoding="utf-8")
    template = (source / "tools/windows-acceptance/WindowsAcceptance.cs").read_text(encoding="utf-8")
    template = template.replace("__OWNED_ROOT__", json.dumps(str(work), ensure_ascii=True))
    template = template.replace("__OWNER_TOKEN__", json.dumps(token))
    commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=source).decode().strip()
    template = template.replace("__SOURCE_COMMIT__", commit)
    (desktop / "WindowsAcceptance.cs").write_text(template, encoding="utf-8")
    project = desktop / "PhotoShelf.Desktop.csproj"
    encoded = project.read_text(encoding="utf-8")
    if encoded.count("</Project>") != 1:
        raise ValueError("Desktop project changed; review fixture embedding")
    project.write_text(encoded.replace("</Project>", '<ItemGroup><EmbeddedResource Include="../../tools/windows-acceptance/synthetic.mp4" LogicalName="PhotoShelf.AcceptanceVideo.mp4" /></ItemGroup>\n</Project>'), encoding="utf-8")
    worker = desktop / "MainWindow.PerceptualFingerprints.cs"
    encoded = worker.read_text(encoding="utf-8")
    marker = "                    var committed = await _perceptualFingerprintStore.SaveObservedBatchAsync(batch, token);"
    if encoded.count(marker) != 1:
        raise ValueError("Fingerprint commit changed; review the timing observer")
    worker.write_text(encoded.replace(marker,
        "                    var acceptanceWriteStarted = Stopwatch.GetTimestamp();\n" + marker +
        "\n                    WindowsAcceptance.RecordWrite(Stopwatch.GetElapsedTime(acceptanceWriteStarted).TotalMilliseconds);"), encoding="utf-8")
    codec = source / "artifacts/heif-codec/win-x64"
    if codec.exists():
        shutil.copytree(codec, copied / "artifacts/heif-codec/win-x64")
    print("OK owned acceptance source copy; production checkout unchanged")
    return copied


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True)
    options = parser.parse_args()
    prepare(options.source, options.work)
