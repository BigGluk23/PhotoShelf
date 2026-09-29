#!/usr/bin/env python3
"""Exercise acceptance/rejection of performance evidence without Windows or third-party modules."""
import copy
import json
import tempfile
import unittest
from pathlib import Path

from compare_performance import compare, markdown


class ComparisonTests(unittest.TestCase):
    def reports(self, baseline=10.0, current=10.0):
        result = []
        for label in ("baseline", "current", "current", "baseline"):
            value = baseline if label == "baseline" else current
            snapshot = {"rowCount": 100000, "sha256": "a" * 64}
            result.append({"schema": 1, "status": "passed", "error": None, "label": label,
                           "revision": label + "-sha", "count": 100000, "repetitions": 5, "warmups": 2,
                           "fixture": "fixed", "syntheticFileBytes": 0, "scope": "projection", "clockFrequency": 10000000,
                           "machine": {"name": "same-host"}, "confirmedSupersessions": 5,
                           "catalogDataIntegrity": {"unchanged": True, "before": copy.deepcopy(snapshot), "after": copy.deepcopy(snapshot)},
                           "projectionTimings": [{"scenario": "ui.date", "iteration": i, "available": False,
                                                  "operationId": None, "outcome": None, "durations": None} for i in range(5)],
                           "samples": [{"name": name, "iteration": i, "milliseconds": value, "fingerprint": "same"}
                                       for name in ("sql.page.first", "ui.date.ack", "ui.date.first_data_page") for i in range(5)],
                           "memory": [{"workingSetBytes": 100, "privateBytes": 100, "managedBytes": 50,
                                       "cachedPages": 2, "cachedRows": 48, "pendingPages": 0}]})
        return result

    def run_comparison(self, reports):
        with tempfile.TemporaryDirectory() as folder:
            paths = []
            for i, report in enumerate(reports):
                path = Path(folder) / f"{i}.json"
                path.write_text(json.dumps(report), encoding="utf-8")
                paths.append(path)
            return compare(paths)

    def test_small_absolute_noise_does_not_fail_even_when_relative_change_is_large(self):
        result = self.run_comparison(self.reports(0.1, 5))
        self.assertEqual("compared", result["status"])

    def test_repeatable_material_regression_fails(self):
        result = self.run_comparison(self.reports(100, 250))
        self.assertEqual("regression", result["status"])
        self.assertTrue(all(metric["regression"] for metric in result["metrics"]))

    def test_one_noisy_current_process_is_not_repeatable_regression(self):
        reports = self.reports(100, 250)
        reports[2] = copy.deepcopy(reports[0])
        reports[2].update(label="current", revision="current-sha")
        self.assertEqual("compared", self.run_comparison(reports)["status"])

    def test_targets_are_reported_honestly_without_conflating_existing_slowness_with_regression(self):
        result = self.run_comparison(self.reports(800, 800))
        self.assertEqual("compared", result["status"])
        self.assertIn("not met", markdown(result))

    def test_different_hosts_are_rejected(self):
        reports = self.reports()
        reports[2]["machine"]["name"] = "another-host"
        with self.assertRaisesRegex(ValueError, "machine"):
            self.run_comparison(reports)

    def test_fast_but_wrong_results_are_rejected(self):
        reports = self.reports()
        reports[1]["samples"][0]["fingerprint"] = "wrong"
        with self.assertRaisesRegex(ValueError, "identity"):
            self.run_comparison(reports)

    def test_missing_samples_are_rejected(self):
        reports = self.reports()
        reports[1]["samples"].pop()
        with self.assertRaisesRegex(ValueError, "missing/repeated"):
            self.run_comparison(reports)

    def test_incomplete_cancellation_is_rejected(self):
        reports = self.reports()
        reports[1]["confirmedSupersessions"] = 4
        with self.assertRaisesRegex(ValueError, "supersession"):
            self.run_comparison(reports)

    def test_missing_catalog_integrity_is_rejected(self):
        reports = self.reports()
        del reports[1]["catalogDataIntegrity"]
        with self.assertRaisesRegex(ValueError, "integrity"):
            self.run_comparison(reports)

    def test_mutated_catalog_is_rejected_even_when_unchanged_flag_is_true(self):
        reports = self.reports()
        reports[1]["catalogDataIntegrity"]["after"]["sha256"] = "b" * 64
        with self.assertRaisesRegex(ValueError, "integrity changed"):
            self.run_comparison(reports)

    def test_invalid_catalog_count_and_digest_are_rejected(self):
        for field, value in (("rowCount", 99999), ("sha256", "invalid")):
            reports = self.reports()
            reports[1]["catalogDataIntegrity"]["before"][field] = value
            with self.subTest(field=field), self.assertRaisesRegex(ValueError, "integrity snapshot"):
                self.run_comparison(reports)

    def test_each_catalog_compares_with_itself_not_other_process_paths(self):
        reports = self.reports()
        for i, report in enumerate(reports):
            for moment in ("before", "after"):
                report["catalogDataIntegrity"][moment]["sha256"] = str(i) * 64
        self.assertTrue(self.run_comparison(reports)["catalogDataIntegrity"]["allRunsUnchanged"])

    def with_current_diagnostics(self, reports):
        for report in reports:
            if report["label"] == "current":
                for i, sample in enumerate(report["projectionTimings"]):
                    sample.update(available=True, operationId=i + 1, outcome="published",
                                  durations={stage: 100000.0 for stage in (
                                      "PreparationMs", "DebounceMs", "GroupsMs", "AnchorMs", "PrimeMs", "PublishMs", "TotalMs")})
        return reports

    def test_current_only_stages_do_not_affect_metrics_or_gates(self):
        original = self.run_comparison(self.reports())
        result = self.run_comparison(self.with_current_diagnostics(self.reports()))
        self.assertEqual(original["metrics"], result["metrics"])
        self.assertEqual(original["status"], result["status"])
        self.assertFalse(result["projectionDiagnostics"]["baseline"]["available"])
        self.assertTrue(result["projectionDiagnostics"]["current"]["available"])
        self.assertEqual(7, len(result["projectionDiagnostics"]["current"]["stages"]))
        self.assertIn("baseline: unavailable", markdown(result))

    def test_invalid_stage_duration_is_rejected(self):
        reports = self.with_current_diagnostics(self.reports())
        reports[1]["projectionTimings"][0]["durations"]["GroupsMs"] = float("nan")
        with self.assertRaisesRegex(ValueError, "stage duration"):
            self.run_comparison(reports)

    def test_stale_or_cancelled_projection_diagnostics_are_rejected(self):
        for field, value in (("operationId", 2), ("outcome", "cancelled")):
            reports = self.with_current_diagnostics(self.reports())
            reports[1]["projectionTimings"][0][field] = value
            with self.subTest(field=field), self.assertRaisesRegex(ValueError, "unique published"):
                self.run_comparison(reports)

    def test_missing_projection_sample_is_rejected(self):
        reports = self.reports()
        reports[1]["projectionTimings"].pop()
        with self.assertRaisesRegex(ValueError, "diagnostic samples are incomplete"):
            self.run_comparison(reports)


if __name__ == "__main__":
    unittest.main()
