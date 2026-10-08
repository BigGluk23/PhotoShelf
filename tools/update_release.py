#!/usr/bin/env python3
"""Prepare a signed update from the exact ZIP already tested by Windows CI. Never rebuilds or publishes."""
import argparse
import hashlib
import json
import os
import re
import shutil
import stat
import subprocess
import tempfile
import zipfile
from pathlib import Path

import package_checks

REPOSITORY = "BigGluk23/PhotoShelf"
MAX_PACKAGE_BYTES = 2 * 1024 ** 3
MAX_UNPACKED_BYTES = 4 * 1024 ** 3
MANIFEST_NAME = "photoshelf-update.json"
SIGNATURE_NAME = "photoshelf-update.sig"
REQUIRED_JOBS = {"windows", "Same-host baseline/current performance"}
PROCESS_SCENARIOS = {"explicit-consent-and-parent-drain", "verified-two-version-install-and-health",
                     "old-shortcut-launches-active-version", "used-request-replay-rejected", "downgrade-rejected",
                     "helper-kill-before-activation", "helper-kill-after-activation"}
DESKTOP_ASSERTIONS = {"normalOldEntrypoint", "realInstallButton", "oldProcessExited", "helperLaunched",
                      "newProcessLaunched", "startupReadyReceipt", "viewRestored", "syntheticCatalogPreserved",
                      "syntheticOriginalsPreserved", "journalsPreserved", "oldShortcutRedirects", "noApplicationErrors",
                      "repeatedLaunchBeforePointerBlocked", "repeatedLaunchAfterPointerBlocked",
                      "repeatedLaunchBeforeHealthBlocked", "unconfirmedTransportPreserved", "confirmedTransportCleaned"}


def release_identity(version, commit, tag):
    package_checks.identity(version, commit)
    numeric = version.removesuffix("-ultra")
    if any(str(int(part)) != part or int(part) > 2147483647 for part in numeric.split(".")):
        raise ValueError("Version must have three canonical numeric components")
    if tag != "v" + version:
        raise ValueError("Release tag must equal v + Directory.Build.props Version")
    return numeric


def verify_ci(run, jobs, commit):
    if (run.get("repository", {}).get("full_name") != REPOSITORY or
            run.get("head_repository", {}).get("full_name") != REPOSITORY or
            run.get("head_sha") != commit or run.get("head_branch") != "main" or
            run.get("event") not in ("push", "workflow_dispatch") or
            run.get("path") != ".github/workflows/windows-ci.yml" or
            run.get("status") != "completed" or run.get("conclusion") != "success"):
        raise ValueError("Release requires a completed successful main Windows CI run at the exact source SHA")
    entries = jobs.get("jobs", [])
    if not entries or jobs.get("total_count") != len(entries):
        raise ValueError("Incomplete CI jobs response")
    names = [job.get("name") for job in entries]
    if len(set(names)) != len(names) or not REQUIRED_JOBS.issubset(names):
        raise ValueError("Both Windows and same-host performance gates are required")
    if any(job.get("status") != "completed" or job.get("conclusion") != "success" or
           job.get("head_sha") != commit or job.get("run_id") != run.get("id") for job in entries):
        raise ValueError("Every job in the selected CI attempt must have passed at the exact SHA")
    return {"runId": run["id"], "runAttempt": run["run_attempt"], "commit": commit}


def sha256(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as source:
        while chunk := source.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def validate_archive(archive, version, commit):
    archive = Path(archive)
    if archive.is_symlink() or not 0 < archive.stat().st_size <= MAX_PACKAGE_BYTES:
        raise ValueError("Invalid update archive size/type")
    unpacked = 0
    with zipfile.ZipFile(archive) as source, tempfile.TemporaryDirectory(prefix="photoshelf-release-check-") as temp:
        entries = source.infolist()
        if not 1 <= len(entries) <= package_checks.MAX_FILES * 2:
            raise ValueError("Invalid ZIP entry count")
        names = set()
        file_names = set()
        directory_names = set()
        files = []
        for entry in entries:
            name = package_checks.safe_name(entry.filename.rstrip("/") if entry.is_dir() else entry.filename)
            folded = name.casefold()
            mode = entry.external_attr >> 16
            if (folded in names or entry.flag_bits & 1 or
                    stat.S_IFMT(mode) not in (0, stat.S_IFREG, stat.S_IFDIR) or
                    stat.S_ISDIR(mode) != entry.is_dir() and stat.S_IFMT(mode) != 0):
                raise ValueError("Unsafe, duplicate, linked or encrypted ZIP entry")
            names.add(folded)
            parents = {"/".join(folded.split("/")[:index]) for index in range(1, len(folded.split("/")))}
            if parents & file_names or (not entry.is_dir() and folded in directory_names):
                raise ValueError("ZIP file/directory path collision")
            directory_names.update(parents)
            if entry.is_dir():
                if folded in file_names:
                    raise ValueError("ZIP file/directory path collision")
                directory_names.add(folded)
                continue
            file_names.add(folded)
            files.append((entry, name))
            unpacked += entry.file_size
            if len(files) > package_checks.MAX_FILES or unpacked > MAX_UNPACKED_BYTES:
                raise ValueError("Unpacked update exceeds limits")
        for entry, name in files:
            destination = Path(temp) / name
            destination.parent.mkdir(parents=True, exist_ok=True)
            with source.open(entry) as contents, destination.open("xb") as target:
                copied = 0
                while chunk := contents.read(1024 * 1024):
                    copied += len(chunk)
                    if copied > entry.file_size:
                        raise ValueError("ZIP entry exceeds declared size")
                    target.write(chunk)
                if copied != entry.file_size:
                    raise ValueError("Truncated ZIP entry")
        package_checks.verify(temp, version, commit)
        inventory_digest = sha256(Path(temp) / package_checks.MANIFEST)
    return unpacked, inventory_digest


def verify_lifecycle_evidence(ci_summary, process_bytes, desktop_bytes, version, commit):
    reports = []
    for raw, field, scope in (
            (process_bytes, "updaterProcessLifecycle", "production-updater-process-lifecycle-with-fixture-applications"),
            (desktop_bytes, "updaterDesktopLifecycle", "production-desktop-update-lifecycle-with-test-trust")):
        if (not isinstance(raw, bytes) or not 0 < len(raw) <= 512 * 1024 or
                ci_summary.get(field + "Verified") is not True or
                ci_summary.get(field + "ReportSha256") != hashlib.sha256(raw).hexdigest()):
            raise ValueError("Missing or mismatched Windows updater lifecycle evidence")
        report = json.loads(raw)
        if (report.get("schema") != 1 or report.get("status") != "passed" or report.get("commit") != commit or
                report.get("version") != version or report.get("platform") != "windows" or report.get("scope") != scope or
                not isinstance(report.get("limitations"), list) or not report["limitations"]):
            raise ValueError("Windows updater lifecycle evidence has the wrong source, platform or scope")
        reports.append(report)
    process, desktop = reports
    scenarios = process.get("scenarios")
    if (not isinstance(scenarios, list) or len(scenarios) != len(PROCESS_SCENARIOS) or
            {scenario.get("name") for scenario in scenarios} != PROCESS_SCENARIOS or
            any(scenario.get("status") != "passed" for scenario in scenarios) or
            process.get("originalHashesPreserved") is not True or process.get("sqliteIntegrityPassed") is not True):
        raise ValueError("Windows updater process safety scenarios did not all pass")
    assertions = desktop.get("assertions")
    if (not isinstance(assertions, dict) or not DESKTOP_ASSERTIONS.issubset(assertions) or
            any(assertions[name] is not True for name in DESKTOP_ASSERTIONS) or
            desktop.get("sourcePublicKeySubstituted") is not True or
            desktop.get("sourceFaultCheckpointsInserted") is not True):
        raise ValueError("The real Desktop update lifecycle did not pass every required assertion")
    versions = desktop.get("testVersions")
    if (not isinstance(versions, list) or len(versions) != 2 or
            any(not isinstance(item, str) or not re.fullmatch(r"\d+\.\d+\.\d+", item) for item in versions) or
            tuple(map(int, versions[0].split("."))) >= tuple(map(int, versions[1].split(".")))):
        raise ValueError("Desktop lifecycle must exercise two distinct increasing test versions")


def prepare_manifest(archive, version, commit, tag, ci_summary, process_bytes, desktop_bytes):
    numeric = release_identity(version, commit, tag)
    verify_lifecycle_evidence(ci_summary, process_bytes, desktop_bytes, version, commit)
    archive = Path(archive)
    if archive.name != f"PhotoShelf-v{version}-win-x64.zip":
        raise ValueError("Unexpected package filename")
    digest = sha256(archive)
    if (ci_summary.get("status") != "passed" or ci_summary.get("commit") != commit or
            ci_summary.get("version") != version or ci_summary.get("archiveSha256") != digest or
            ci_summary.get("extractedPackageVerified") is not True or
            ci_summary.get("updaterSelfTestVerified") is not True):
        raise ValueError("ZIP differs from the package verified by the selected Windows CI run")
    unpacked, inventory_digest = validate_archive(archive, version, commit)
    # Schema 5 is the only supported update/rollback boundary in protocol v1.
    # Changing this is a release-policy change that requires new migration tests.
    manifest = {"protocolVersion": 1, "version": numeric, "runtime": "win-x64",
                "packageUrl": f"https://github.com/{REPOSITORY}/releases/download/{tag}/{archive.name}",
                "packageSha256": digest, "packageManifestSha256": inventory_digest,
                "packageBytes": archive.stat().st_size, "unpackedBytes": unpacked,
                "minCatalogSchema": 5, "maxCatalogSchema": 5,
                "releaseNotesUrl": f"https://github.com/{REPOSITORY}/releases/tag/{tag}"}
    return (json.dumps(manifest, ensure_ascii=False, indent=2) + "\n").encode("utf-8")


def sign_manifest(manifest, signature, public_key):
    private_key = os.environ.get("PHOTOSHELF_UPDATE_SIGNING_KEY", "")
    if not private_key:
        raise ValueError("PHOTOSHELF_UPDATE_SIGNING_KEY is not configured; unsigned updates are forbidden")
    # Pipe the secret directly to OpenSSL; never put it in argv, files, artifacts or output.
    child_env = {name: value for name, value in os.environ.items() if name != "PHOTOSHELF_UPDATE_SIGNING_KEY"}
    result = subprocess.run(["openssl", "dgst", "-sha256", "-sigopt", "rsa_padding_mode:pkcs1",
                             "-sign", "/dev/stdin", "-out", str(signature), str(manifest)],
                            input=private_key.encode("utf-8"), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                            env=child_env, check=False, timeout=30)
    if result.returncode != 0 or not Path(signature).is_file() or not 256 <= Path(signature).stat().st_size <= 1024:
        raise ValueError("RSA-SHA256 signing failed")
    verification = subprocess.run(["openssl", "dgst", "-sha256", "-sigopt", "rsa_padding_mode:pkcs1", "-verify", str(public_key),
                                   "-signature", str(signature), str(manifest)], stdout=subprocess.DEVNULL,
                                  stderr=subprocess.DEVNULL, env=child_env, check=False, timeout=30)
    if verification.returncode != 0:
        raise ValueError("Signing key does not match the public key shipped in the application")


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def read_lifecycle_report(path):
    path = Path(path)
    if not 0 < path.stat().st_size <= 512 * 1024:
        raise ValueError("Windows lifecycle report exceeds supported bounds")
    return path.read_bytes()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    gate = commands.add_parser("gate")
    gate.add_argument("--run", type=Path, required=True)
    gate.add_argument("--jobs", type=Path, required=True)
    gate.add_argument("--commit", required=True)
    for command in ("validate", "prepare"):
        prepare = commands.add_parser(command)
        for name in ("archive", "ci-summary", "process-report", "desktop-report", "output"):
            prepare.add_argument("--" + name, type=Path, required=True)
        if command == "prepare":
            prepare.add_argument("--public-key", type=Path, required=True)
        for name in ("version", "commit", "tag"):
            prepare.add_argument("--" + name, required=True)
    args = parser.parse_args()
    try:
        if args.command == "gate":
            verified = verify_ci(read_json(args.run), read_json(args.jobs), args.commit)
            print(f"OK completed Windows/performance run {verified['runId']}, attempt {verified['runAttempt']}, {args.commit}")
        else:
            manifest = prepare_manifest(args.archive, args.version, args.commit, args.tag, read_json(args.ci_summary),
                                        read_lifecycle_report(args.process_report), read_lifecycle_report(args.desktop_report))
            args.output.mkdir(parents=True, exist_ok=False)
            manifest_path = args.output / MANIFEST_NAME
            manifest_path.write_bytes(manifest)
            if args.command == "validate":
                print(f"OK unsigned release candidate and Windows lifecycle evidence: {args.tag}, {args.commit}")
                return 0
            sign_manifest(manifest_path, args.output / SIGNATURE_NAME, args.public_key)
            shutil.copyfile(args.archive, args.output / args.archive.name)
            if sha256(args.output / args.archive.name) != json.loads(manifest)["packageSha256"]:
                raise ValueError("Package changed while preparing release assets")
            print(f"OK signed release assets: {args.tag}, {args.commit}")
        return 0
    except (ValueError, OSError, KeyError, TypeError, AttributeError, zipfile.BadZipFile, subprocess.TimeoutExpired):
        # Avoid echoing subprocess/secret contents, even if an invalid key was supplied.
        print("FAILED update release validation/signing; no release was published")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
