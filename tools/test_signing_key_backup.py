import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

import signing_key_backup as backup


class MemoryKeychain:
    def __init__(self):
        self.entries = {}

    def put(self, account, password):
        if account in self.entries:
            raise AssertionError("Must never overwrite a password")
        self.entries[account] = password

    def get(self, account):
        return self.entries[account]


class BackupTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.binary = next((p for p in ("/opt/homebrew/opt/openssl@3/bin/openssl", shutil.which("openssl")) if p and Path(p).is_file()), None)
        if os.name == "nt" or not cls.binary:
            raise unittest.SkipTest("Local backup uses POSIX pipes; run these tests on macOS/Linux with OpenSSL 3")

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="photoshelf-backup-test-")
        self.root = Path(self.temp.name).resolve()
        self.private, self.public = self.root / "test.pem", self.root / "test.pub"
        result = subprocess.run([self.binary, "genpkey", "-algorithm", "RSA", "-pkeyopt", "rsa_keygen_bits:2048"], capture_output=True, check=True)
        self.private.write_bytes(result.stdout)
        self.private.chmod(0o600)
        self.public.write_bytes(backup.openssl(self.binary, ["pkey", "-in", str(self.private), "-pubout"]))
        self.store = MemoryKeychain()
        self.destination = self.root / "backup"

    def tearDown(self):
        self.temp.cleanup()

    def create(self):
        return backup.create(self.private, self.public, self.destination, self.binary, self.store)

    def test_round_trip_is_verified_without_plaintext_key_or_password_files(self):
        original = self.private.read_bytes()
        result = self.create()
        self.assertEqual("verified", result["status"])
        self.assertFalse(result["offDeviceRecoveryVerified"])
        self.assertEqual(original, self.private.read_bytes())
        self.assertEqual(0o700, self.destination.stat().st_mode & 0o777)
        self.assertEqual(result, backup.verify(self.destination, self.public, self.binary, self.store))
        for p in self.destination.iterdir():
            self.assertEqual(0o600, p.stat().st_mode & 0o777)
            self.assertNotIn(original, p.read_bytes())
            self.assertNotIn(next(iter(self.store.entries.values())), p.read_bytes())

    def test_existing_backup_cannot_be_replaced(self):
        self.create()
        before = {p.name: p.read_bytes() for p in self.destination.iterdir()}
        with self.assertRaises(backup.BackupError):
            self.create()
        self.assertEqual(before, {p.name: p.read_bytes() for p in self.destination.iterdir()})

    def test_corrupted_ciphertext_is_rejected(self):
        self.create()
        p = self.destination / "signing-key.encrypted.pem"
        p.write_bytes(p.read_bytes()[:-30] + b"corruption")
        with self.assertRaisesRegex(backup.BackupError, "checksum"):
            backup.verify(self.destination, self.public, self.binary, self.store)

    def test_wrong_password_does_not_emit_private_material(self):
        self.create()
        for account in self.store.entries:
            self.store.entries[account] = b"wrong-password"
        with self.assertRaisesRegex(backup.BackupError, "no secret output"):
            backup.verify(self.destination, self.public, self.binary, self.store)

    def test_wrong_independent_public_key_is_rejected(self):
        self.create()
        result = subprocess.run([self.binary, "genpkey", "-algorithm", "RSA", "-pkeyopt", "rsa_keygen_bits:2048"], capture_output=True, check=True)
        other = self.root / "other.pub"
        other.write_bytes(backup.openssl(self.binary, ["pkey", "-pubout"], data=result.stdout))
        with self.assertRaisesRegex(backup.BackupError, "independently supplied"):
            backup.verify(self.destination, other, self.binary, self.store)

    def test_unsafe_source_permissions_and_symlinks_fail_before_keychain_write(self):
        self.private.chmod(0o644)
        with self.assertRaisesRegex(backup.BackupError, "permissions"):
            self.create()
        self.private.chmod(0o600)
        link = self.root / "link"
        link.symlink_to(self.private)
        with self.assertRaisesRegex(backup.BackupError, "Symlink"):
            backup.create(link, self.public, self.destination, self.binary, self.store)
        self.assertEqual({}, self.store.entries)
        self.assertFalse(self.destination.exists())


if __name__ == "__main__":
    unittest.main()
