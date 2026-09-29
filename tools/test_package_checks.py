import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location("package_checks", Path(__file__).with_name("package_checks.py"))
checks = importlib.util.module_from_spec(spec)
spec.loader.exec_module(checks)


class PackageTests(unittest.TestCase):
    version = "1.2.3-ultra"
    commit = "a" * 40

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="photoshelf-package-test-")
        self.root = Path(self.temp.name)
        for name in checks.REQUIRED | {"licenses/example.txt", "codecs/heif/licenses/example.txt"}:
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("synthetic fixture", encoding="utf-8")
        (self.root / "RUNNING.txt").write_text("PhotoShelf Ultra — Windows x64\nFixture instructions.\n", encoding="utf-8")
        checks.create(self.root, self.version, self.commit)

    def tearDown(self):
        self.temp.cleanup()

    def verify(self):
        return checks.verify(self.root, self.version, self.commit)

    def test_complete_package_has_exact_identity_and_generated_instructions(self):
        self.assertEqual("passed", self.verify()["status"])
        self.assertIn("v1.2.3", (self.root / "RUNNING.txt").read_text(encoding="utf-8"))

    def test_missing_codec_blocks_verification(self):
        (self.root / "codecs/heif/heif.dll").unlink()
        with self.assertRaisesRegex(ValueError, "Missing required"):
            self.verify()

    def test_changed_bytes_and_stale_overlay_files_are_rejected(self):
        path = self.root / "PhotoShelf.exe"
        original = path.read_bytes()
        path.write_bytes(b"interrupted update")
        with self.assertRaisesRegex(ValueError, "checksum/length"):
            self.verify()
        path.write_bytes(original)
        (self.root / "previous-version.dll").write_bytes(b"old")
        with self.assertRaisesRegex(ValueError, "file set mismatch"):
            self.verify()

    def test_wrong_commit_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "identity mismatch"):
            checks.verify(self.root, self.version, "b" * 40)

    def test_manifest_paths_cannot_escape_or_use_windows_aliases(self):
        for path in ["../PhotoShelf.exe", "/outside", "C:/outside", "a\\b", "a//b", "a/./b", "AUX.txt", "a.", "a ", "a:stream", "a\x00b"]:
            with self.subTest(path=path), self.assertRaisesRegex(ValueError, "Unsafe"):
                checks.safe_name(path)

    def test_case_collisions_in_manifest_are_rejected(self):
        path = self.root / checks.MANIFEST
        data = json.loads(path.read_text(encoding="utf-8"))
        duplicate = dict(data["files"][0])
        duplicate["path"] = duplicate["path"].upper()
        data["files"].append(duplicate)
        path.write_text(json.dumps(data), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "Repeated"):
            self.verify()

    def test_manifest_cannot_claim_a_missing_file_set_as_a_complete_release(self):
        name = "codecs/heif/libde265.dll"
        (self.root / name).unlink()
        path = self.root / checks.MANIFEST
        data = json.loads(path.read_text(encoding="utf-8"))
        data["files"] = [entry for entry in data["files"] if entry["path"] != name]
        path.write_text(json.dumps(data), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "Missing required"):
            self.verify()


if __name__ == "__main__":
    unittest.main()
