import copy
import json
import os
import stat
import subprocess
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest import mock

import package_checks
import update_release as release
import release_security as security


class ReleaseTests(unittest.TestCase):
    version = "1.2.3-ultra"
    tag = "v1.2.3-ultra"
    commit = "a" * 40

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="photoshelf-release-test-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.package = self.root / "package"
        for name in package_checks.REQUIRED | {"licenses/test.txt", "codecs/heif/licenses/test.txt"}:
            path = self.package / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"synthetic test fixture")
        (self.package / "RUNNING.txt").write_text("PhotoShelf Ultra — Windows x64\nFixture only.\n", encoding="utf-8")
        package_checks.create(self.package, self.version, self.commit)
        self.archive = self.root / f"PhotoShelf-v{self.version}-win-x64.zip"
        self.repack()

    def repack(self):
        with zipfile.ZipFile(self.archive, "w", zipfile.ZIP_DEFLATED) as archive:
            for path in self.package.rglob("*"):
                if path.is_file():
                    archive.write(path, path.relative_to(self.package).as_posix())

    def lifecycle_reports(self):
        shared = {"schema": 1, "status": "passed", "commit": self.commit, "version": self.version,
                  "platform": "windows", "limitations": ["Isolated synthetic fixtures and a test signing key."]}
        process = dict(shared, scope="production-updater-process-lifecycle-with-fixture-applications",
                       originalHashesPreserved=True, sqliteIntegrityPassed=True,
                       scenarios=[{"name": name, "status": "passed"} for name in sorted(release.PROCESS_SCENARIOS)])
        desktop = dict(shared, scope="production-desktop-update-lifecycle-with-test-trust",
                       sourcePublicKeySubstituted=True, sourceFaultCheckpointsInserted=True,
                       testVersions=["1.2.3", "1.2.4"],
                       assertions={name: True for name in release.DESKTOP_ASSERTIONS})
        return json.dumps(process).encode(), json.dumps(desktop).encode()

    def summary(self):
        process, desktop = self.lifecycle_reports()
        return {"status": "passed", "commit": self.commit, "version": self.version,
                "archiveSha256": release.sha256(self.archive), "extractedPackageVerified": True,
                "updaterSelfTestVerified": True, "updaterProcessLifecycleVerified": True,
                "updaterProcessLifecycleReportSha256": release.hashlib.sha256(process).hexdigest(),
                "updaterDesktopLifecycleVerified": True,
                "updaterDesktopLifecycleReportSha256": release.hashlib.sha256(desktop).hexdigest()}

    def prepare(self, summary=None):
        return release.prepare_manifest(self.archive, self.version, self.commit, self.tag, summary or self.summary(),
                                        *self.lifecycle_reports())

    def test_manifest_matches_exact_archive_inventory_and_protocol(self):
        encoded = self.prepare()
        data = json.loads(encoded)
        self.assertFalse(encoded.startswith(b"\xef\xbb\xbf"))
        self.assertEqual({"protocolVersion", "version", "runtime", "packageUrl", "packageSha256",
                          "packageManifestSha256", "packageBytes", "unpackedBytes", "minCatalogSchema",
                          "maxCatalogSchema", "releaseNotesUrl"}, set(data))
        self.assertEqual("1.2.3", data["version"])
        self.assertEqual(1, data["protocolVersion"])
        self.assertEqual("win-x64", data["runtime"])
        self.assertEqual(release.sha256(self.package / package_checks.MANIFEST), data["packageManifestSha256"])
        self.assertEqual(self.archive.stat().st_size, data["packageBytes"])
        self.assertEqual(sum(p.stat().st_size for p in self.package.rglob("*") if p.is_file()), data["unpackedBytes"])
        self.assertEqual(f"https://github.com/BigGluk23/PhotoShelf/releases/download/{self.tag}/{self.archive.name}", data["packageUrl"])

    def test_wrong_ci_package_sha_commit_version_or_missing_smoke_blocks_signing(self):
        for field, value in [("archiveSha256", "0" * 64), ("commit", "b" * 40), ("version", "1.2.4-ultra"),
                             ("status", "failed"), ("extractedPackageVerified", False), ("updaterSelfTestVerified", False)]:
            with self.subTest(field=field):
                report = self.summary()
                report[field] = value
                with self.assertRaisesRegex(ValueError, "selected Windows CI"):
                    self.prepare(report)

    def test_tampered_or_incomplete_inner_inventory_is_rejected(self):
        (self.package / "PhotoShelf.Updater.exe").write_bytes(b"different bytes")
        self.repack()
        with self.assertRaisesRegex(ValueError, "checksum/length"):
            self.prepare()
        (self.package / "PhotoShelf.Updater.exe").unlink()
        self.repack()
        with self.assertRaisesRegex(ValueError, "Missing required"):
            self.prepare()

    def test_archive_identity_cannot_point_to_another_version_or_tag(self):
        with self.assertRaisesRegex(ValueError, "tag"):
            release.prepare_manifest(self.archive, self.version, self.commit, "v1.2.4-ultra", self.summary(), *self.lifecycle_reports())
        for version in ("01.2.3-ultra", "1.2.2147483648-ultra", "1.2.3-preview", "1.2.3.4-ultra"):
            with self.subTest(version=version), self.assertRaises(ValueError):
                release.release_identity(version, self.commit, "v" + version)

    def test_zip_paths_and_links_cannot_escape_or_alias(self):
        for name in ("../outside", "C:/outside", "AUX.txt", "photoshelf.EXE", "PhotoShelf.exe/child"):
            with self.subTest(name=name):
                self.repack()
                with zipfile.ZipFile(self.archive, "a") as archive:
                    archive.writestr(name, b"untrusted")
                with self.assertRaises(ValueError):
                    self.prepare()
        self.repack()
        with zipfile.ZipFile(self.archive, "a") as archive:
            info = zipfile.ZipInfo("linked")
            info.create_system = 3
            info.external_attr = (stat.S_IFLNK | 0o777) << 16
            archive.writestr(info, b"outside")
        with self.assertRaisesRegex(ValueError, "linked"):
            self.prepare()
        self.assertFalse((self.root / "outside").exists())

    def test_archive_bounds_reject_before_extraction(self):
        with mock.patch.object(release, "MAX_PACKAGE_BYTES", 1), self.assertRaisesRegex(ValueError, "size/type"):
            self.prepare()
        with mock.patch.object(release, "MAX_UNPACKED_BYTES", 1), self.assertRaisesRegex(ValueError, "limits"):
            self.prepare()

    def test_missing_signing_secret_is_fail_closed(self):
        with mock.patch.dict(os.environ, {}, clear=True), self.assertRaisesRegex(ValueError, "not configured"):
            release.sign_manifest(self.root / "manifest", self.root / "signature", self.root / "public")

    def test_unsigned_validation_cli_requires_evidence_without_reading_a_signing_secret(self):
        process, desktop = self.lifecycle_reports()
        (self.root / "process.json").write_bytes(process)
        (self.root / "desktop.json").write_bytes(desktop)
        (self.root / "summary.json").write_text(json.dumps(self.summary()))
        environment = {name: value for name, value in os.environ.items() if name != "PHOTOSHELF_UPDATE_SIGNING_KEY"}
        result = subprocess.run([os.sys.executable, "-B", str(Path(release.__file__)), "validate",
                                 "--archive", str(self.archive), "--ci-summary", str(self.root / "summary.json"),
                                 "--process-report", str(self.root / "process.json"),
                                 "--desktop-report", str(self.root / "desktop.json"),
                                 "--version", self.version, "--commit", self.commit, "--tag", self.tag,
                                 "--output", str(self.root / "candidate")], capture_output=True, text=True, env=environment)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual([release.MANIFEST_NAME], [path.name for path in (self.root / "candidate").iterdir()])
        self.assertEqual(self.prepare(), (self.root / "candidate" / release.MANIFEST_NAME).read_bytes())

    def test_missing_mismatched_or_failed_updater_evidence_blocks_release(self):
        process, desktop = self.lifecycle_reports()
        for field, value in (("updaterProcessLifecycleVerified", False), ("updaterDesktopLifecycleVerified", False),
                             ("updaterProcessLifecycleReportSha256", "0" * 64), ("updaterDesktopLifecycleReportSha256", None)):
            with self.subTest(field=field):
                summary = dict(self.summary(), **{field: value})
                with self.assertRaisesRegex(ValueError, "lifecycle evidence"):
                    release.verify_lifecycle_evidence(summary, process, desktop, self.version, self.commit)

    def test_lifecycle_report_identity_and_every_safety_assertion_are_required(self):
        original = self.lifecycle_reports()
        for report_index in (0, 1):
            for field, value in (("commit", "b" * 40), ("version", "1.2.4-ultra"), ("platform", "macos"),
                                 ("status", "failed"), ("scope", "self-test"), ("limitations", [])):
                with self.subTest(report=report_index, field=field):
                    values = list(original)
                    report = dict(json.loads(values[report_index]), **{field: value})
                    values[report_index] = json.dumps(report).encode()
                    summary = self.summary()
                    prefix = "updaterProcessLifecycle" if report_index == 0 else "updaterDesktopLifecycle"
                    summary[prefix + "ReportSha256"] = release.hashlib.sha256(values[report_index]).hexdigest()
                    with self.assertRaises(ValueError):
                        release.verify_lifecycle_evidence(summary, *values, self.version, self.commit)
        for assertion in release.DESKTOP_ASSERTIONS:
            with self.subTest(assertion=assertion):
                report = json.loads(original[1])
                report["assertions"][assertion] = False
                changed = json.dumps(report).encode()
                summary = self.summary()
                summary["updaterDesktopLifecycleReportSha256"] = release.hashlib.sha256(changed).hexdigest()
                with self.assertRaisesRegex(ValueError, "every required assertion"):
                    release.verify_lifecycle_evidence(summary, original[0], changed, self.version, self.commit)
        process = json.loads(original[0])
        process["scenarios"][0]["status"] = "skipped"
        changed = json.dumps(process).encode()
        summary = self.summary()
        summary["updaterProcessLifecycleReportSha256"] = release.hashlib.sha256(changed).hexdigest()
        with self.assertRaisesRegex(ValueError, "safety scenarios"):
            release.verify_lifecycle_evidence(summary, changed, original[1], self.version, self.commit)

    def test_test_versions_must_show_an_actual_increasing_two_version_transition(self):
        process, desktop = self.lifecycle_reports()
        for versions in (("1.2.3", "1.2.3"), ("1.2.4", "1.2.3"), ("1.2.3",), ("invalid", "1.2.4")):
            with self.subTest(versions=versions):
                report = json.loads(desktop)
                report["testVersions"] = versions
                changed = json.dumps(report).encode()
                summary = self.summary()
                summary["updaterDesktopLifecycleReportSha256"] = release.hashlib.sha256(changed).hexdigest()
                with self.assertRaisesRegex(ValueError, "two distinct"):
                    release.verify_lifecycle_evidence(summary, process, changed, self.version, self.commit)

    def test_previous_happy_path_report_cannot_substitute_for_relaunch_race_evidence(self):
        process, desktop = self.lifecycle_reports()
        report = json.loads(desktop)
        for name in ("repeatedLaunchBeforePointerBlocked", "repeatedLaunchAfterPointerBlocked",
                     "repeatedLaunchBeforeHealthBlocked"):
            del report["assertions"][name]
        del report["sourceFaultCheckpointsInserted"]
        changed = json.dumps(report).encode()
        summary = self.summary()
        summary["updaterDesktopLifecycleReportSha256"] = release.hashlib.sha256(changed).hexdigest()
        with self.assertRaisesRegex(ValueError, "every required assertion"):
            release.verify_lifecycle_evidence(summary, process, changed, self.version, self.commit)

    def test_relaunch_assertions_require_instrumented_checkpoint_evidence(self):
        process, desktop = self.lifecycle_reports()
        for value in (None, False):
            with self.subTest(checkpointEvidence=value):
                report = json.loads(desktop)
                if value is None:
                    del report["sourceFaultCheckpointsInserted"]
                else:
                    report["sourceFaultCheckpointsInserted"] = value
                changed = json.dumps(report).encode()
                summary = self.summary()
                summary["updaterDesktopLifecycleReportSha256"] = release.hashlib.sha256(changed).hexdigest()
                with self.assertRaisesRegex(ValueError, "every required assertion"):
                    release.verify_lifecycle_evidence(summary, process, changed, self.version, self.commit)

    @unittest.skipIf(os.name == "nt", "Release signing executes on the Linux release runner, not Windows packaging")
    def test_real_signature_roundtrip_and_wrong_trust_key(self):
        # These are disposable synthetic keys, never the production trust material.
        key = subprocess.check_output(["openssl", "genpkey", "-algorithm", "RSA", "-pkeyopt", "rsa_keygen_bits:2048"], stderr=subprocess.DEVNULL)
        public = subprocess.check_output(["openssl", "pkey", "-pubout"], input=key, stderr=subprocess.DEVNULL)
        public_path = self.root / "public.pem"
        public_path.write_bytes(public)
        manifest_path = self.root / "update.json"
        manifest_path.write_bytes(self.prepare())
        signature_path = self.root / "update.sig"
        with mock.patch.dict(os.environ, {"PHOTOSHELF_UPDATE_SIGNING_KEY": key.decode("ascii")}):
            release.sign_manifest(manifest_path, signature_path, public_path)
            self.assertEqual(256, signature_path.stat().st_size)
            other_key = subprocess.check_output(["openssl", "genpkey", "-algorithm", "RSA", "-pkeyopt", "rsa_keygen_bits:2048"], stderr=subprocess.DEVNULL)
            other_public = subprocess.check_output(["openssl", "pkey", "-pubout"], input=other_key, stderr=subprocess.DEVNULL)
            public_path.write_bytes(other_public)
            with self.assertRaisesRegex(ValueError, "does not match"):
                release.sign_manifest(manifest_path, signature_path, public_path)
            public_path.write_text("untrusted public key", encoding="ascii")
            with self.assertRaisesRegex(ValueError, "does not match"):
                release.sign_manifest(manifest_path, signature_path, public_path)


class ReleaseProtectionTests(unittest.TestCase):
    def fixtures(self):
        return ({"id": 42, "name": "release", "url": "https://api.github.com/repos/BigGluk23/PhotoShelf/environments/release",
                 "can_admins_bypass": False,
                 "protection_rules": [{"type": "required_reviewers", "prevent_self_review": True,
                                       "reviewers": [{"type": "User", "reviewer": {"login": "BigGluk23", "id": security.OWNER_ID}}]}],
                 "deployment_branch_policy": {"protected_branches": False, "custom_branch_policies": True}},
                {"total_count": 1, "branch_policies": [{"id": 5, "name": "main", "type": "branch"}]})

    def verify(self, environment=None, branches=None, secret_present=False, actor="Khrumium", attempt=1):
        valid_environment, valid_branches = self.fixtures()
        return security.verify_environment(environment if environment is not None else valid_environment,
                                           branches if branches is not None else valid_branches,
                                           secret_present, actor, actor, attempt)

    def test_expected_owner_gate_and_exact_main_are_accepted(self):
        self.assertEqual(42, self.verify()["environmentId"])

    def test_repo_secret_and_self_approval_and_rerun_are_rejected(self):
        for changes in ({"secret_present": True}, {"secret_present": None}, {"actor": "BigGluk23"}, {"attempt": 2}):
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                self.verify(**changes)

    def test_missing_owner_extra_reviewers_and_self_review_permission_are_rejected(self):
        for reviewers in ([], [{"type": "Team", "reviewer": {"login": "BigGluk23"}}],
                          [{"type": "User", "reviewer": {"login": "Khrumium"}}],
                          [{"type": "User", "reviewer": {"login": "BigGluk23"}}, {"type": "User", "reviewer": {"login": "someone"}}]):
            with self.subTest(reviewers=reviewers):
                environment, _ = self.fixtures()
                environment["protection_rules"][0]["reviewers"] = reviewers
                with self.assertRaises(ValueError):
                    self.verify(environment=environment)
        environment, _ = self.fixtures()
        environment["protection_rules"][0]["prevent_self_review"] = False
        with self.assertRaises(ValueError):
            self.verify(environment=environment)
        environment, _ = self.fixtures()
        environment["protection_rules"][0]["reviewers"][0]["reviewer"]["id"] = security.OWNER_ID + 1
        with self.assertRaises(ValueError):
            self.verify(environment=environment)

    def test_branch_wildcards_tags_and_incomplete_results_are_rejected(self):
        for branches in ({"total_count": 0, "branch_policies": []},
                         {"total_count": 2, "branch_policies": [{"name": "main", "type": "branch"}]},
                         {"total_count": 1, "branch_policies": [{"name": "main", "type": "tag"}]},
                         {"total_count": 1, "branch_policies": [{"name": "*", "type": "branch"}]}):
            with self.subTest(branches=branches), self.assertRaises(ValueError):
                self.verify(branches=branches)

    def test_admin_bypass_is_rejected_when_exposed_and_absence_is_not_claimed_as_verified(self):
        environment, _ = self.fixtures()
        environment["can_admins_bypass"] = True
        with self.assertRaisesRegex(ValueError, "Administrator bypass"):
            self.verify(environment=environment)
        del environment["can_admins_bypass"]
        self.assertFalse(self.verify(environment=environment)["administratorBypassVerifiedByApi"])

    def test_only_one_actual_owner_approval_of_this_environment_is_accepted(self):
        approval = {"state": "approved", "user": {"login": "BigGluk23", "id": security.OWNER_ID}, "environments": [{"id": 42, "name": "release"}]}
        self.assertEqual("BigGluk23", security.verify_owner_approval([approval], 42)["approvedBy"])
        for history in ([], [dict(approval, state="rejected")], [dict(approval, user={"login": "Khrumium"})],
                        [dict(approval, user={"login": "BigGluk23", "id": security.OWNER_ID + 1})],
                        [dict(approval, environments=[{"id": 43, "name": "release"}])], [approval, approval]):
            with self.subTest(history=history), self.assertRaises(ValueError):
                security.verify_owner_approval(history, 42)


class CiGateTests(unittest.TestCase):
    commit = "a" * 40

    def fixtures(self):
        run = {"repository": {"full_name": release.REPOSITORY}, "head_repository": {"full_name": release.REPOSITORY},
               "head_sha": self.commit, "head_branch": "main", "event": "push", "path": ".github/workflows/windows-ci.yml",
               "status": "completed", "conclusion": "success", "id": 123, "run_attempt": 2}
        jobs = {"total_count": 2, "jobs": [{"name": name, "status": "completed", "conclusion": "success",
                                          "head_sha": self.commit, "run_id": 123} for name in sorted(release.REQUIRED_JOBS)]}
        return run, jobs

    def test_both_exact_sha_completed_jobs_are_required(self):
        run, jobs = self.fixtures()
        self.assertEqual(2, release.verify_ci(run, jobs, self.commit)["runAttempt"])
        for value in ("skipped", "cancelled", "failure", None):
            with self.subTest(conclusion=value):
                broken = copy.deepcopy(jobs)
                broken["jobs"][0]["conclusion"] = value
                with self.assertRaises(ValueError):
                    release.verify_ci(run, broken, self.commit)

    def test_wrong_sha_fork_pull_request_or_incomplete_response_is_rejected(self):
        run, jobs = self.fixtures()
        for field, value in (("head_sha", "b" * 40), ("head_branch", "feature"), ("event", "pull_request"),
                             ("status", "in_progress"), ("path", ".github/workflows/other.yml"),
                             ("head_repository", {"full_name": "someone/PhotoShelf"})):
            with self.subTest(field=field):
                broken = dict(run, **{field: value})
                with self.assertRaises(ValueError):
                    release.verify_ci(broken, jobs, self.commit)
        jobs["jobs"].pop()
        with self.assertRaisesRegex(ValueError, "Incomplete"):
            release.verify_ci(run, jobs, self.commit)
        jobs["total_count"] = 1
        with self.assertRaisesRegex(ValueError, "Both"):
            release.verify_ci(run, jobs, self.commit)

    def test_each_required_job_must_belong_to_selected_run_and_commit(self):
        run, jobs = self.fixtures()
        for index in range(len(jobs["jobs"])):
            for field, value in (("head_sha", "b" * 40), ("run_id", 124),
                                 ("status", "in_progress"), ("conclusion", "skipped")):
                with self.subTest(job=jobs["jobs"][index]["name"], field=field):
                    broken = copy.deepcopy(jobs)
                    broken["jobs"][index][field] = value
                    with self.assertRaisesRegex(ValueError, "Every job"):
                        release.verify_ci(run, broken, self.commit)

    def test_successful_windows_job_does_not_cover_missing_or_failed_performance(self):
        run, jobs = self.fixtures()
        windows = next(job for job in jobs["jobs"] if job["name"] == "windows")
        with self.assertRaisesRegex(ValueError, "Both"):
            release.verify_ci(run, {"total_count": 1, "jobs": [windows]}, self.commit)
        performance = next(job for job in jobs["jobs"] if job["name"] != "windows")
        performance["conclusion"] = "failure"
        with self.assertRaisesRegex(ValueError, "Every job"):
            release.verify_ci(run, jobs, self.commit)


if __name__ == "__main__":
    unittest.main()
