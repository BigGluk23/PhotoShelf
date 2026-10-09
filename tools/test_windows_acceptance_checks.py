import copy
import unittest
from windows_acceptance_checks import validate


def evidence():
    phase = dict(status="passed", gracefulExit=True, noApplicationErrors=True, actualCatalogStartup=True,
                 videoPlaybackTested=False, physicalSleepTested=False, maxDispatcherGapMs=120,
                 reads=[], decoderCalls=0, decoderFailures=[])
    first = dict(phase, phase="first", processId=10, scrollsDuringIndexing=10, maxCachedPages=2, maxQueue=3, warmPrivateBytes=100000000, maxPrivateBytes=120000000,
                 reads=[dict(name=n, count=1) for n in ["B/fixture.jpg", "B/fixture.webp", "B/fixture.heic"]] +
                 [dict(name=f"A/{i}.png", count=1) for i in range(196)], decoderCalls=199,
                 decoderTimes=dict(count=199, p95Ms=2, maxMs=5), sqliteWriteTimes=dict(count=7, p95Ms=2, maxMs=5),
                 uiPageTimes=dict(count=24, p95Ms=120, maxMs=150), duplicateScopes=[
                     dict(scope=s, groups=g, bulkMarked=m, keeperProtected=True, groupPagingVerified=True, memberPagingVerified=True)
                     for s, g, m in (("whole-library", 34, 162), ("current-folder", 18, 146))])
    return dict(schema=1, status="passed", commit="a" * 40, scope="production-WPF-startup-in-owned-compile-time-test-copy",
                sourceCopyInstrumented=True, shippingEntrypointChanged=False, originalHashesSizesAndTimesPreserved=True,
                companionsPreserved=True, catalogIntegrityPassed=True, assetIdsAndFavoritesPreserved=True,
                restartCacheVerified=True, singleChangedFileVerified=True, syntheticCatalogRows=100000, originalsChecked=202,
                phases=[first, dict(phase, phase="restart", processId=11),
                        dict(phase, phase="changed", processId=12, reads=[dict(name="A/group-00-copy.png", count=1)], decoderCalls=1)])


class AcceptanceGateTests(unittest.TestCase):
    def test_complete_evidence_is_accepted(self):
        self.assertEqual("passed", validate(evidence(), "a" * 40)["status"])

    def test_missing_assertions_and_wrong_commit_are_rejected(self):
        for key in ("companionsPreserved", "originalHashesSizesAndTimesPreserved", "restartCacheVerified", "singleChangedFileVerified"):
            with self.subTest(key=key):
                report = evidence(); del report[key]
                with self.assertRaises(ValueError): validate(report, "a" * 40)
        with self.assertRaises(ValueError): validate(evidence(), "b" * 40)

    def test_skipped_missing_duplicate_or_reused_process_cannot_pass(self):
        for mutation in (lambda r: r["phases"][1].update(status="skipped"),
                         lambda r: r["phases"].pop(),
                         lambda r: r["phases"][2].update(processId=11),
                         lambda r: r["phases"][0]["duplicateScopes"].pop()):
            report = evidence(); mutation(report)
            with self.assertRaises(ValueError): validate(report, "a" * 40)

    def test_partial_counter_claim_cannot_hide_redecoding(self):
        report = evidence(); report["phases"][1]["reads"] = [dict(name="B/fixture.heic", count=1)]
        with self.assertRaises(ValueError): validate(report, "a" * 40)
        report["phases"][1]["decoderCalls"] = 1
        with self.assertRaises(ValueError): validate(report, "a" * 40)

    def test_nonfinite_measurement_and_no_overlap_cannot_pass(self):
        for mutation in (lambda r: r["phases"][0].update(maxDispatcherGapMs=float("nan")),
                         lambda r: r["phases"][0].update(scrollsDuringIndexing=0),
                         lambda r: r["phases"][0].update(decoderFailures=[dict(name="B/fixture.webp", errorType="NotSupportedException")]),
                         lambda r: r["phases"][0].update(warmPrivateBytes=None),
                         lambda r: r["phases"][0].update(maxPrivateBytes=500000000),
                         lambda r: r["phases"][0]["sqliteWriteTimes"].update(count=0),
                         lambda r: r["phases"][0]["uiPageTimes"].update(p95Ms=5000)):
            report = evidence(); mutation(report)
            with self.assertRaises(ValueError): validate(report, "a" * 40)

    def test_relative_fixture_paths_only(self):
        for path in ("C:\\Users\\person\\photo.jpg", "../photo.jpg", "/private/photo.jpg"):
            report = copy.deepcopy(evidence()); report["phases"][2]["reads"][0]["name"] = path
            with self.assertRaises(ValueError): validate(report, "a" * 40)


if __name__ == "__main__":
    unittest.main()
