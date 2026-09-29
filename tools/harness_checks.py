#!/usr/bin/env python3
"""Dependency-free repository checks and strict TRX accounting for both harnesses."""
import argparse
import json
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path, PurePosixPath

ROOT = Path(__file__).resolve().parent.parent
PROJECTS = ("Domain", "Application", "Infrastructure.Sqlite", "Desktop")
GENERATED_PARTS = {"bin", "obj", "artifacts", "releases", "testresults", ".backups", "thumb-cache", ".vs", ".idea", "__pycache__"}


def git(*args):
    return subprocess.check_output(["git", "-C", str(ROOT), *args]).decode("utf-8", "surrogateescape")


def forbidden(path):
    p = PurePosixPath(path.lower())
    return (bool(set(p.parts) & GENERATED_PARTS) or p.name == ".ds_store"
            or p.suffix in {".zip", ".db", ".sqlite", ".sqlite3", ".pyc"}
            or re.search(r"\.(?:db|sqlite|sqlite3)-(?:wal|shm|journal)$", p.name)
            or p.name == "writer.lock" or p.name.startswith(("photoshelf.catalog.db", "catalog-v1.json"))
            or any(part.startswith("thumb-cache-v") for part in p.parts))


def repository():
    if Path(git("rev-parse", "--show-toplevel").strip()).resolve() != ROOT:
        raise ValueError("Run from a real PhotoShelf Git checkout, not an enclosing repository.")
    if git("ls-files", "-u").strip():
        raise ValueError("Unmerged Git entries remain.")
    candidates = set(filter(None, git("ls-files", "--cached", "--others", "--exclude-standard", "-z").split("\0")))
    bad = sorted(path for path in candidates if forbidden(path))
    if bad:
        raise ValueError("Generated/private library files are tracked or candidates for commit: " + ", ".join(bad))
    for name in candidates:
        path = ROOT / name
        if not path.is_file() or path.is_symlink():
            continue
        # Images are legitimate source resources; never scan binary bytes as text.
        try:
            content = path.read_text(encoding="utf-8")
        except UnicodeDecodeError:
            continue
        if re.search(r"(?m)^(?:<{7} .+|={7}|>{7} .+)$", content):
            raise ValueError(f"Unresolved conflict marker: {name}")
    required = ["PhotoShelf.sln", "global.json", "Directory.Build.props", "README.md", "HANDOFF.md", "AGENTS.md",
                "src/PhotoShelf.Application/Files/SAFETY.md", "src/PhotoShelf.Desktop/Assets/PhotoShelf.ico",
                "src/PhotoShelf.Desktop/Assets/giraffe-icon.png", "scripts/test-startup-package.ps1"]
    required += [f"src/PhotoShelf.{part}/PhotoShelf.{part}.csproj" for part in PROJECTS]
    required += [f"tests/PhotoShelf.{part}.Tests/PhotoShelf.{part}.Tests.csproj" for part in PROJECTS]
    missing = [name for name in required if not (ROOT / name).is_file()]
    if missing:
        raise ValueError("Missing required files: " + ", ".join(missing))
    props = ET.parse(ROOT / "Directory.Build.props").getroot()
    version = props.findtext(".//Version") or ""
    if not re.fullmatch(r"\d+\.\d+\.\d+-ultra", version):
        raise ValueError("Directory.Build.props must contain an explicit x.y.z-ultra Version.")
    sdk = json.loads((ROOT / "global.json").read_text())["sdk"]["version"]
    tfm = props.findtext(".//TargetFramework")
    major = sdk.split(".")[0]
    if tfm != f"net{major}.0":
        raise ValueError(f"SDK {sdk} does not match default framework {tfm}.")
    for part in ("src/PhotoShelf.Desktop/PhotoShelf.Desktop.csproj", "tests/PhotoShelf.Desktop.Tests/PhotoShelf.Desktop.Tests.csproj"):
        if ET.parse(ROOT / part).getroot().findtext(".//TargetFramework") != f"net{major}.0-windows":
            raise ValueError(f"WPF target framework mismatch: {part}")
    label = "Ultra v" + version.removesuffix("-ultra")
    code = (ROOT / "src/PhotoShelf.Desktop/MainWindow.xaml.cs").read_text(encoding="utf-8")
    if f'VersionLabel = "{label}"' not in code:
        raise ValueError("MainWindow.VersionLabel differs from Directory.Build.props.")
    xaml = ET.parse(ROOT / "src/PhotoShelf.Desktop/MainWindow.xaml").getroot()
    title = next((node.attrib.get("Text") for node in xaml.iter()
                  if node.attrib.get("{http://schemas.microsoft.com/winfx/2006/xaml}Name") == "AppTitleText"), None)
    if title != "PhotoShelf " + label:
        raise ValueError("MainWindow XAML title differs from Directory.Build.props.")
    if (ROOT / "README.md").read_text(encoding="utf-8").splitlines()[0] != "# PhotoShelf " + label:
        raise ValueError("README heading differs from Directory.Build.props.")
    if (ROOT / "src/PhotoShelf.Desktop/RUNNING.txt").read_text(encoding="utf-8").splitlines()[0] != "PhotoShelf Ultra — Windows x64":
        raise ValueError("RUNNING source heading must be version-neutral; packaging generates the release version.")
    print(f"OK repository: {len(candidates)} source candidates; version {version}; SDK {sdk}; no generated files/conflicts")


def trx(directory, expected, portable, summary):
    reports = sorted(Path(directory).glob("*.trx"))
    expected_names = {f"{part}.trx" for part in expected}
    if {p.name for p in reports} != expected_names:
        raise ValueError(f"Expected TRX {sorted(expected_names)}; found {[p.name for p in reports]}")
    totals = {"passed": 0, "skipped": 0, "failed": 0}
    skipped = []
    failures = []
    ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    for path in reports:
        root = ET.parse(path).getroot()
        results = root.findall("./t:Results/t:UnitTestResult", ns)
        counts = root.find("./t:ResultSummary/t:Counters", ns)
        if counts is None or not results or int(counts.get("total", "0")) != len(results):
            raise ValueError(f"Empty or incomplete test report: {path.name}")
        if root.find("./t:ResultSummary", ns).get("outcome") not in {"Completed", "Passed"}:
            failures.append(f"{path.name}: test run did not complete successfully")
        passed_here = 0
        for result in results:
            outcome = result.get("outcome")
            name = result.get("testName", "unknown")
            if outcome == "Passed":
                totals["passed"] += 1
                passed_here += 1
            elif outcome == "NotExecuted":
                reason = result.findtext("./t:Output/t:ErrorInfo/t:Message", "", ns)
                totals["skipped"] += 1
                skipped.append({"test": name, "reason": reason})
                if not portable or not reason.startswith("Requires Windows:"):
                    failures.append(f"Unexpected skipped test: {name}: {reason}")
            else:
                totals["failed"] += 1
                failures.append(f"{name}: {outcome}")
        if not passed_here:
            failures.append(f"{path.name}: no passing tests")
    report = dict(totals, status="failed" if failures else "passed", scope="portable" if portable else "windows", skips=skipped, failures=failures)
    if summary:
        Path(summary).write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(f"Tests: {totals['passed']} passed, {totals['failed']} failed, {totals['skipped']} skipped")
    for item in skipped:
        print(f"SKIPPED {item['test']}: {item['reason']}")
    if failures:
        raise ValueError("; ".join(failures))
    print("OK TRX accounting (skipped tests are not passes)")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("repository")
    sub.add_parser("version")
    p = sub.add_parser("trx")
    p.add_argument("directory")
    p.add_argument("--projects", nargs="+", required=True, choices=PROJECTS)
    p.add_argument("--portable", action="store_true")
    p.add_argument("--summary")
    args = parser.parse_args()
    try:
        if args.command == "repository":
            repository()
        elif args.command == "version":
            print(ET.parse(ROOT / "Directory.Build.props").getroot().findtext(".//Version"))
        else:
            trx(args.directory, args.projects, args.portable, args.summary)
    except (ValueError, OSError, ET.ParseError, subprocess.CalledProcessError) as error:
        print(f"FAILED: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
