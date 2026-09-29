#!/usr/bin/env python3
"""Compare an ABBA experiment, not unrelated CI machines or dispatcher timer gaps."""
import argparse
import json
import math
import re
import statistics
from collections import defaultdict
from pathlib import Path


def percentile(values, fraction):
    ordered = sorted(values)
    return ordered[max(0, math.ceil(len(ordered) * fraction) - 1)]


def summarize(values):
    center = statistics.median(values)
    return {"n": len(values), "p50": center, "p95": percentile(values, .95),
            "max": max(values), "mad": statistics.median(abs(x - center) for x in values)}


def validate_catalog_integrity(report):
    integrity = report.get("catalogDataIntegrity", {})
    before, after = integrity.get("before"), integrity.get("after")
    if not isinstance(before, dict) or not isinstance(after, dict) or integrity.get("unchanged") is not True:
        raise ValueError("Missing or failed catalog media-row integrity check.")
    for snapshot in (before, after):
        digest = snapshot.get("sha256")
        if snapshot.get("rowCount") != report["count"] or not isinstance(digest, str) or not re.fullmatch(r"[0-9A-Fa-f]{64}", digest):
            raise ValueError("Invalid catalog media-row integrity snapshot.")
    # The fixture paths differ per process: compare each catalog only with its own pre-workload snapshot.
    if before["sha256"].lower() != after["sha256"].lower():
        raise ValueError("Catalog media-row integrity changed during the UI query workload.")


def projection_diagnostics(reports, metric_names):
    stages = {"PreparationMs", "DebounceMs", "GroupsMs", "AnchorMs", "PrimeMs", "PublishMs", "TotalMs"}
    scenarios = {name.removesuffix(".first_data_page") for name in metric_names if name.endswith(".first_data_page")}
    grouped, availability = defaultdict(list), defaultdict(set)
    for report in reports:
        samples = report.get("projectionTimings")
        if not isinstance(samples, list):
            raise ValueError("Missing projection diagnostics availability record.")
        expected = {(scenario, iteration) for scenario in scenarios for iteration in range(report["repetitions"])}
        seen, operation_ids = set(), set()
        for sample in samples:
            key = (sample.get("scenario"), sample.get("iteration"))
            if key not in expected or key in seen or type(sample.get("available")) is not bool:
                raise ValueError("Invalid or repeated projection diagnostic sample.")
            seen.add(key)
            available = sample["available"]
            availability[report["label"]].add(available)
            if not available:
                if any(sample.get(name) is not None for name in ("operationId", "outcome", "durations")):
                    raise ValueError("Unavailable projection diagnostics contain invented stage data.")
                continue
            operation_id, durations = sample.get("operationId"), sample.get("durations")
            if type(operation_id) is not int or operation_id <= 0 or operation_id in operation_ids or sample.get("outcome") != "published":
                raise ValueError("Projection diagnostics did not identify a unique published operation.")
            operation_ids.add(operation_id)
            if not isinstance(durations, dict) or set(durations) != stages:
                raise ValueError("Incomplete projection stage diagnostics.")
            for stage, value in durations.items():
                if type(value) not in (int, float) or not math.isfinite(value) or value < 0:
                    raise ValueError("Invalid projection stage duration.")
                grouped[(report["label"], key[0], stage)].append(value)
        if seen != expected:
            raise ValueError("Projection diagnostic samples are incomplete.")
    result = {"scope": "Internal projection stages after the primary timers stop; diagnostic only, excluded from comparisons and gates."}
    for label in ("baseline", "current"):
        if len(availability[label]) != 1:
            raise ValueError("Projection diagnostics availability changed within one revision.")
        result[label] = {"available": True in availability[label], "stages": [
            {"scenario": scenario, "stage": stage, "milliseconds": summarize(values)}
            for (side, scenario, stage), values in sorted(grouped.items()) if side == label]}
    return result


def phase_diagnostics(reports):
    grouped = defaultdict(list)
    for index, report in enumerate(reports, 1):
        phases = report.get("phaseTimings", [])
        if not isinstance(phases, list):
            raise ValueError("Invalid coarse phase diagnostics.")
        seen = set()
        for phase in phases:
            name, value = phase.get("name"), phase.get("milliseconds")
            if not isinstance(name, str) or not name or name in seen or type(value) not in (int, float) or not math.isfinite(value) or value < 0:
                raise ValueError("Invalid coarse phase diagnostic duration or name.")
            if phase.get("outcome") not in ("completed", "interrupted"):
                raise ValueError("Invalid coarse phase diagnostic outcome.")
            seen.add(name)
            grouped[name].append({"run": index, "label": report["label"], "milliseconds": value, "outcome": phase["outcome"]})
    return {"scope": "Coarse phase wall times include warmups and waits where applicable; excluded from comparable samples and regression gates.",
            "available": bool(grouped), "phases": [{"name": name, "runs": runs} for name, runs in sorted(grouped.items())]}


def compare(paths):
    reports = [json.loads(Path(path).read_text(encoding="utf-8-sig")) for path in paths]
    if len(reports) != 4 or [r["label"] for r in reports] != ["baseline", "current", "current", "baseline"]:
        raise ValueError("Exactly four fresh-process reports in baseline/current/current/baseline order are required.")
    reference = reports[0]
    for report in reports:
        if report["status"] != "passed" or report.get("error"):
            raise ValueError("A benchmark process failed; incomplete measurements cannot be compared.")
        for key in ("schema", "count", "repetitions", "warmups", "fixture", "syntheticFileBytes", "scope", "machine", "clockFrequency"):
            if report[key] != reference[key]:
                raise ValueError(f"Incomparable benchmark property: {key}")
        # Legacy reports remain reproducible together; they cannot be mixed with the
        # new explicitly recorded seed cache profile or any other setup profile.
        if report.get("setupProfile") != reference.get("setupProfile"):
            raise ValueError("Incomparable benchmark property: setupProfile")
        if "setupProfile" in report and (not isinstance(report["setupProfile"], dict) or not report["setupProfile"].get("name")):
            raise ValueError("Invalid setupProfile metadata.")
        if report["confirmedSupersessions"] != 5:
            raise ValueError("UI cancellation/supersession verification was incomplete.")
        if report["warmups"] < 2 or report["repetitions"] < 5:
            raise ValueError("Warmups and repeated samples are required.")
        validate_catalog_integrity(report)
    if reports[0]["revision"] != reports[3]["revision"] or reports[1]["revision"] != reports[2]["revision"]:
        raise ValueError("Each side must use one exact revision.")
    metrics = []
    runs = []
    for report in reports:
        groups = defaultdict(list)
        for sample in report["samples"]:
            value = sample["milliseconds"]
            if not math.isfinite(value) or value < 0:
                raise ValueError("Invalid timing sample.")
            groups[sample["name"]].append(value)
        if any(len(v) != report["repetitions"] for v in groups.values()):
            raise ValueError("A metric has missing/repeated samples.")
        runs.append(groups)
    if not runs[0] or any(set(run) != set(runs[0]) for run in runs):
        raise ValueError("Metric sets differ between runs.")
    diagnostics = projection_diagnostics(reports, runs[0])
    phases = phase_diagnostics(reports)
    # SQL correctness is part of the comparison: no faster-but-different query result is accepted.
    for name in (n for n in runs[0] if n.startswith("sql.")):
        fingerprints = {s["fingerprint"] for r in reports for s in r["samples"] if s["name"] == name}
        if len(fingerprints) != 1:
            raise ValueError(f"SQL result identity changed for {name}.")
    for name in sorted(runs[0]):
        baseline = summarize(runs[0][name] + runs[3][name])
        current = summarize(runs[1][name] + runs[2][name])
        # Require a meaningful absolute change, a relative change, and a change larger than noise.
        # Both current process medians must also exceed the slower baseline process.
        floor = 50.0 if name.startswith("ui.") else 20.0
        noise_allowance = max(floor, baseline["p50"] * .30, 6 * baseline["mad"], 6 * current["mad"])
        delta = current["p50"] - baseline["p50"]
        process_regression = min(statistics.median(runs[i][name]) for i in (1, 2)) > max(
            statistics.median(runs[i][name]) for i in (0, 3)) + floor
        goal = 100 if name.endswith(".ack") else 500 if name.endswith(".first_data_page") else None
        metrics.append({"name": name, "baselineMs": baseline, "currentMs": current,
                        "deltaP50Ms": delta, "deltaP50Percent": 100 * delta / baseline["p50"] if baseline["p50"] else None,
                        "noiseAllowanceMs": noise_allowance, "regression": delta > noise_allowance and process_regression,
                        "goalP95Ms": goal, "goalMet": current["p95"] <= goal if goal else None})
    memory = {}
    for label in ("baseline", "current"):
        observations = [m for r in reports if r["label"] == label for m in r["memory"]]
        if not observations:
            raise ValueError("Missing memory observations.")
        memory[label] = {key: max(m[key] for m in observations) for key in
                         ("workingSetBytes", "privateBytes", "managedBytes", "cachedPages", "cachedRows", "pendingPages")}
    return {"schema": 1, "status": "regression" if any(m["regression"] for m in metrics) else "compared",
            "baselineRevision": reference["revision"], "currentRevision": reports[1]["revision"],
            "count": reference["count"], "machine": reference["machine"], "scope": reference["scope"],
            "runOrder": [r["label"] for r in reports], "metrics": metrics, "memoryPeaks": memory,
            "catalogDataIntegrity": {"allRunsUnchanged": True, "table": "desktop_media_items", "rowsPerSnapshot": reference["count"], "snapshotCount": 8},
            "projectionDiagnostics": diagnostics,
            "setupProfile": reference.get("setupProfile", {"name": "legacy-unrecorded"}), "phaseDiagnostics": phases,
            "limitations": ["Same host and same job; hosted-runner CPU/storage interference can still occur.",
                            "Warm query/UI projection workload; no cold storage, thumbnail decode, ingestion, hardware input or manual UX claim.",
                            "100 ms acknowledgement / 500 ms first page are p95 targets, reported honestly rather than advertised guarantees.",
                            "Regression gate compares medians with 30% relative, 20 ms SQL / 50 ms UI absolute, 6 MAD noise allowances and both process medians.",
                            "Memory is sampled process/cache memory, not a continuous maximum or a declared whole-process budget.",
                            "Fixture seed cache is connection-local, not a production setting. OS cache and retained allocator memory can reflect setup; only this same-profile paired experiment is comparable.",
                            "Internal stage diagnostics are unavailable in the unmodified baseline and do not affect comparable samples, goals or regression gates.",
                            "Media-row fingerprints prove unchanged synthetic catalog data during UI queries; settings/caches and original media file bytes are outside this check."]}


def markdown(result):
    lines = ["# PhotoShelf: same-host performance comparison", "",
             f"Baseline: `{result['baselineRevision']}`. Current: `{result['currentRevision']}`.",
             f"Synthetic records: **{result['count']:,}**. Order: **A–B–B–A**, fresh process each time, two warmups per metric.",
             "", result["scope"], "", "| Metric | Before p50 / p95, ms | After p50 / p95, ms | Δ p50 | Verdict |",
             "|---|---:|---:|---:|---|"]
    for metric in result["metrics"]:
        before, after = metric["baselineMs"], metric["currentMs"]
        delta = metric["deltaP50Percent"]
        verdict = "REGRESSION" if metric["regression"] else "within regression tolerance"
        if metric["goalMet"] is not None:
            verdict += f"; p95 goal {metric['goalP95Ms']} ms: {'met' if metric['goalMet'] else 'not met'}"
        change = f"{delta:+.1f}%" if delta is not None else "n/a"
        lines.append(f"| {metric['name']} | {before['p50']:.2f} / {before['p95']:.2f} | {after['p50']:.2f} / {after['p95']:.2f} | {change} | {verdict} |")
    lines += ["", "SQL result fingerprints matched across all four processes. Five superseded UI projections were canceled and the final search verified in every process.",
              "All media-table columns and row counts matched before/after the UI workload in each process; streaming SHA-256 scans ran outside timing samples. Settings/caches are excluded, and no original media files are used.", "",
              "| Sampled peak | Before | After |", "|---|---:|---:|"]
    for key in ("workingSetBytes", "privateBytes", "managedBytes"):
        lines.append(f"| {key} | {result['memoryPeaks']['baseline'][key] / 1048576:.1f} MiB | {result['memoryPeaks']['current'][key] / 1048576:.1f} MiB |")
    lines += ["", "Internal projection diagnostics (separate from the comparison and its gates):", ""]
    for label in ("baseline", "current"):
        diagnostic = result["projectionDiagnostics"][label]
        if not diagnostic["available"]:
            lines.append(f"{label}: unavailable in this source revision; no reconstructed timings.")
            continue
        lines += [f"{label} only:", "", "| Scenario | Stage | p50 / p95, ms |", "|---|---|---:|"]
        for stage in diagnostic["stages"]:
            value = stage["milliseconds"]
            lines.append(f"| {stage['scenario']} | {stage['stage']} | {value['p50']:.2f} / {value['p95']:.2f} |")
        lines.append("")
    lines += ["", "Fixture setup profile (not a production application setting):", "", "```json",
              json.dumps(result["setupProfile"], indent=2), "```", "",
              "Coarse phase wall times (diagnostic only; include warmups/waits, and are not additional performance gates):", ""]
    if not result["phaseDiagnostics"]["available"]:
        lines.append("Unavailable in these legacy reports; setup duration cannot be reconstructed from action samples.")
    else:
        lines += ["| Phase | Baseline process seconds | Current process seconds |", "|---|---:|---:|"]
        for phase in result["phaseDiagnostics"]["phases"]:
            def render(label):
                return " / ".join(f"{run['milliseconds'] / 1000:.2f}" + (" (interrupted)" if run["outcome"] != "completed" else "")
                                  for run in phase["runs"] if run["label"] == label) or "unavailable"
            lines.append(f"| {phase['name']} | {render('baseline')} | {render('current')} |")
    lines += ["", "Limits:", ""] + ["- " + s for s in result["limitations"]]
    lines += ["", "Machine:", "", "```json", json.dumps(result["machine"], indent=2), "```", ""]
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("reports", nargs=4)
    parser.add_argument("--json", required=True)
    parser.add_argument("--markdown", required=True)
    parser.add_argument("--enforce", action="store_true")
    args = parser.parse_args()
    result = compare(args.reports)
    with open(args.json, "x", encoding="utf-8") as output:
        json.dump(result, output, indent=2)
    with open(args.markdown, "x", encoding="utf-8") as output:
        output.write(markdown(result))
    print(result["status"])
    return 1 if args.enforce and result["status"] == "regression" else 0


if __name__ == "__main__":
    raise SystemExit(main())
