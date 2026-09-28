#!/usr/bin/env python3
"""Compare an ABBA experiment, not unrelated CI machines or dispatcher timer gaps."""
import argparse
import json
import math
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
        if report["confirmedSupersessions"] != 5:
            raise ValueError("UI cancellation/supersession verification was incomplete.")
        if report["warmups"] < 2 or report["repetitions"] < 5:
            raise ValueError("Warmups and repeated samples are required.")
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
            "limitations": ["Same host and same job; hosted-runner CPU/storage interference can still occur.",
                            "Warm query/UI projection workload; no cold storage, thumbnail decode, ingestion, hardware input or manual UX claim.",
                            "100 ms acknowledgement / 500 ms first page are p95 targets, reported honestly rather than advertised guarantees.",
                            "Regression gate compares medians with 30% relative, 20 ms SQL / 50 ms UI absolute, 6 MAD noise allowances and both process medians.",
                            "Memory is sampled process/cache memory, not a continuous maximum or a declared whole-process budget."]}


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
    lines += ["", "SQL result fingerprints matched across all four processes. Five superseded UI projections were canceled and the final search verified in every process.", "",
              "| Sampled peak | Before | After |", "|---|---:|---:|"]
    for key in ("workingSetBytes", "privateBytes", "managedBytes"):
        lines.append(f"| {key} | {result['memoryPeaks']['baseline'][key] / 1048576:.1f} MiB | {result['memoryPeaks']['current'][key] / 1048576:.1f} MiB |")
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
