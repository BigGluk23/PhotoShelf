#!/usr/bin/env python3
"""Fail closed on missing, skipped, partial or unbound Windows acceptance evidence."""
import argparse
import json
import math
from pathlib import Path


def validate(report, commit):
    if report.get("schema") != 1 or report.get("status") != "passed" or report.get("commit") != commit:
        raise ValueError("Acceptance must pass at the exact source commit")
    if report.get("scope") != "production-WPF-startup-in-owned-compile-time-test-copy":
        raise ValueError("Unknown acceptance scope")
    for field in ("sourceCopyInstrumented", "originalHashesSizesAndTimesPreserved", "companionsPreserved",
                  "catalogIntegrityPassed", "assetIdsAndFavoritesPreserved", "restartCacheVerified", "singleChangedFileVerified", "legacyUnsupportedWebpRecovered"):
        if report.get(field) is not True:
            raise ValueError("Missing acceptance assertion: " + field)
    if report.get("shippingEntrypointChanged") is not False or report.get("syntheticCatalogRows") != 100000:
        raise ValueError("Shipping code or catalog scope mismatch")
    if type(report.get("originalsChecked")) is not int or report["originalsChecked"] < 202:
        raise ValueError("Incomplete media and companion fixtures")
    phases = report.get("phases")
    if not isinstance(phases, list) or len(phases) != 3 or [p.get("phase") for p in phases] != ["first", "restart", "changed"]:
        raise ValueError("All three sequential process phases are required")
    pids = []
    for phase in phases:
        if phase.get("status") != "passed":
            raise ValueError("Failed/skipped acceptance phase")
        for field in ("gracefulExit", "noApplicationErrors", "actualCatalogStartup"):
            if phase.get(field) is not True:
                raise ValueError("Missing process lifecycle assertion")
        if phase.get("videoPlaybackTested") is not False or phase.get("physicalSleepTested") is not False:
            raise ValueError("Unexpected hardware/playback coverage claim")
        pid = phase.get("processId")
        if type(pid) is not int or pid <= 0:
            raise ValueError("Process identity missing")
        pids.append(pid)
        gap = phase.get("maxDispatcherGapMs")
        if type(gap) not in (int, float) or not math.isfinite(gap) or not 0 < gap < 5000:
            raise ValueError("Dispatcher measurements incomplete or over budget")
        if phase.get("decoderFailures") != []:
            raise ValueError("Real fixture decoder failed or its failure evidence is missing: " + str(phase.get("decoderFailures")))
        reads = phase.get("reads")
        if not isinstance(reads, list) or len(reads) > 512:
            raise ValueError("Unbounded/missing reader evidence")
        names = []
        for entry in reads:
            name = entry.get("name")
            if not isinstance(name, str) or name.startswith(("/", "\\")) or ":" in name or ".." in name.replace("\\", "/").split("/"):
                raise ValueError("Reader name is not an owned relative fixture")
            if type(entry.get("count")) is not int or entry["count"] <= 0:
                raise ValueError("Invalid reader counter")
            names.append(name.replace("\\", "/"))
        if len(set(names)) != len(names) or phase.get("decoderCalls") != sum(e["count"] for e in reads):
            raise ValueError("Reader totals inconsistent")
    if len(set(pids)) != 3:
        raise ValueError("Restart must use distinct real processes")
    first, restart, changed = phases
    if restart["decoderCalls"] != 0 or changed["decoderCalls"] != 1 or len(changed["reads"]) != 1 or changed["reads"][0]["name"].replace("\\", "/") != "A/group-00-copy.png":
        raise ValueError("Persisted cache or single-file refresh failed")
    if first.get("scrollsDuringIndexing", 0) < 8 or not 0 < first.get("maxCachedPages", 0) <= 12 or not 0 <= first.get("maxQueue", -1) <= 256:
        raise ValueError("Missing bounded large-view/indexing overlap")
    warm, peak = first.get("warmPrivateBytes"), first.get("maxPrivateBytes")
    if type(warm) is not int or type(peak) is not int or not 0 < warm <= peak or peak - warm > 256 * 1024 * 1024:
        raise ValueError("Missing/over-budget warm memory measurement")
    formats = {entry["name"].replace("\\", "/") for entry in first["reads"]}
    if not {"B/fixture.jpg", "B/fixture.webp", "B/fixture.heic"}.issubset(formats) or first["decoderCalls"] < 199:
        raise ValueError("Mixed real decoders were not exercised")
    for field, minimum in (("decoderTimes", 199), ("sqliteWriteTimes", 1), ("uiPageTimes", 24)):
        metric = first.get(field, {})
        if type(metric.get("count")) is not int or metric["count"] < minimum:
            raise ValueError("Missing separated measurement: " + field)
        for key in ("p95Ms", "maxMs"):
            value = metric.get(key)
            if type(value) not in (int, float) or not math.isfinite(value) or value < 0:
                raise ValueError("Nonfinite/incomplete measurement")
    if first["uiPageTimes"]["p95Ms"] >= 5000:
        raise ValueError("Large-view page preparation exceeded its regression budget")
    scopes = first.get("duplicateScopes")
    if not isinstance(scopes, list) or len(scopes) != 2:
        raise ValueError("Both real duplicate scopes must complete")
    for scope, expected in zip(scopes, (("whole-library", 34, 162), ("current-folder", 18, 146))):
        if (scope.get("scope"), scope.get("groups"), scope.get("bulkMarked")) != expected:
            raise ValueError("Duplicate scope/count mismatch")
        if any(scope.get(key) is not True for key in ("keeperProtected", "groupPagingVerified", "memberPagingVerified")):
            raise ValueError("Missing real duplicate UI assertion")
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report", type=Path)
    parser.add_argument("--commit", required=True)
    args = parser.parse_args()
    if args.report.is_symlink() or args.report.stat().st_size > 1024 * 1024:
        raise SystemExit("FAILED bounded acceptance report")
    validate(json.loads(args.report.read_text(encoding="utf-8-sig")), args.commit)
    print("OK Windows acceptance: real restart/cache, one changed original, duplicate UI and large view during indexing")
