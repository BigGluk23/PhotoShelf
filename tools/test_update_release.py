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

    def summary(self):
        return {"status": "passed", "commit": self.commit, "version": self.version,
                "archiveSha256": release.sha256(self.archive), "extractedPackageVerified": True,
                "updaterSelfTestVerified": True}

    def prepare(self, summary=None):
        return release.prepare_manifest(self.archive, self.version, self.commit, self.tag, summary or self.summary())

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
            release.prepare_manifest(self.archive, self.version, self.commit, "v1.2.4-ultra", self.summary())
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
