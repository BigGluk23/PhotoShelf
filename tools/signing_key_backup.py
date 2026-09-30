#!/usr/bin/env python3
"""Local, verified PKCS#8 backup. Never upload, print, or export the private key/password.

macOS stores the random encryption password in the default Keychain, separately from
the encrypted file. This protects against deletion of the original key; it is not
an independent off-device recovery copy. Export the password through Keychain
Access into the owner's separate password vault before relying on disaster recovery.
"""
import argparse
import ctypes
import ctypes.util
import hashlib
import json
import os
from pathlib import Path
import secrets
import subprocess
import sys
import tempfile
import uuid

SERVICE = "PhotoShelf release signing backup"


class BackupError(Exception):
    pass


class MacKeychain:
    def __init__(self):
        if sys.platform != "darwin":
            raise BackupError("Creating a backup requires the macOS default Keychain.")
        self.lib = ctypes.CDLL(ctypes.util.find_library("Security"))
        self.lib.SecKeychainAddGenericPassword.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_char_p,
            ctypes.c_uint32, ctypes.c_char_p, ctypes.c_uint32, ctypes.c_void_p, ctypes.c_void_p]
        self.lib.SecKeychainAddGenericPassword.restype = ctypes.c_int32
        self.lib.SecKeychainFindGenericPassword.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_char_p,
            ctypes.c_uint32, ctypes.c_char_p, ctypes.POINTER(ctypes.c_uint32), ctypes.POINTER(ctypes.c_void_p), ctypes.c_void_p]
        self.lib.SecKeychainFindGenericPassword.restype = ctypes.c_int32
        self.lib.SecKeychainItemFreeContent.argtypes = [ctypes.c_void_p, ctypes.c_void_p]

    def put(self, account, password):
        service, account = SERVICE.encode(), account.encode()
        status = self.lib.SecKeychainAddGenericPassword(None, len(service), service,
            len(account), account, len(password), ctypes.c_char_p(password), None)
        if status != 0:
            raise BackupError(f"Keychain refused creation (OSStatus {status}); no existing entry was replaced.")

    def get(self, account):
        service, account = SERVICE.encode(), account.encode()
        size, address = ctypes.c_uint32(), ctypes.c_void_p()
        status = self.lib.SecKeychainFindGenericPassword(None, len(service), service,
            len(account), account, ctypes.byref(size), ctypes.byref(address), None)
        if status != 0:
            raise BackupError(f"Keychain refused recovery (OSStatus {status}).")
        try:
            return ctypes.string_at(address, size.value)
        finally:
            self.lib.SecKeychainItemFreeContent(None, address)


def safe_path(path):
    path = Path(os.path.abspath(path))
    for part in (path, *path.parents):
        if part.is_symlink():
            raise BackupError("Symlink paths are not accepted.")
    return path


def openssl(binary, args, data=None, password=None, password_option=None):
    descriptor = None
    try:
        if password is not None:
            descriptor, writer = os.pipe()
            try:
                os.write(writer, password + b"\n")
            finally:
                os.close(writer)
            args = [*args, password_option, f"fd:{descriptor}"]
        result = subprocess.run([binary, *args], input=data, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, pass_fds=() if descriptor is None else (descriptor,), timeout=30)
        if result.returncode:
            # OpenSSL diagnostics are deliberately not echoed: secret handling must
            # never depend on an external program's error/redaction behaviour.
            raise BackupError("OpenSSL verification/encryption failed; no secret output was emitted.")
        return result.stdout
    finally:
        if descriptor is not None:
            os.close(descriptor)


def public_fingerprint(binary, path):
    public = openssl(binary, ["pkey", "-pubin", "-in", str(path), "-outform", "DER"])
    return hashlib.sha256(public).hexdigest()


def sync_directory(path):
    fd = os.open(path, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
    try:
        os.fsync(fd)
    finally:
        os.close(fd)


def write_new(path, data):
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "wb") as stream:
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())
    sync_directory(Path(path).parent)


def verify(destination, expected_public, binary, keychain):
    destination, expected_public = safe_path(destination), safe_path(expected_public)
    manifest_path = safe_path(destination / "backup.json")
    if manifest_path.stat().st_size > 8192:
        raise BackupError("Invalid backup metadata size.")
    metadata = json.loads(manifest_path.read_text())
    if metadata.get("schema") != 1 or metadata.get("keychainService") != SERVICE:
        raise BackupError("Unsupported backup metadata.")
    account = metadata.get("keychainAccount", "")
    if str(uuid.UUID(account)) != account:
        raise BackupError("Invalid backup account identifier.")
    encrypted_path = safe_path(destination / "signing-key.encrypted.pem")
    if encrypted_path.stat().st_size > 16384:
        raise BackupError("Invalid encrypted key size.")
    encrypted = encrypted_path.read_bytes()
    if hashlib.sha256(encrypted).hexdigest() != metadata.get("encryptedSha256"):
        raise BackupError("Encrypted backup checksum differs.")
    expected = public_fingerprint(binary, expected_public)
    if expected != metadata.get("publicKeySha256"):
        raise BackupError("Backup does not match the independently supplied public key.")
    password = keychain.get(account)
    private_der = openssl(binary, ["pkcs8", "-in", str(encrypted_path), "-outform", "DER"],
        password=password, password_option="-passin")
    openssl(binary, ["pkey", "-inform", "DER", "-check", "-noout"], data=private_der)
    public_der = openssl(binary, ["pkey", "-inform", "DER", "-pubout", "-outform", "DER"], data=private_der)
    if hashlib.sha256(public_der).hexdigest() != expected:
        raise BackupError("Recovered key does not match the trusted public key.")
    # Prove signing works without producing any release-authorizing signature.
    # These temporary files contain only a random challenge and its public signature.
    with tempfile.TemporaryDirectory(prefix="signing-rehearsal-", dir=destination) as rehearsal:
        challenge, signature = Path(rehearsal) / "challenge", Path(rehearsal) / "signature"
        write_new(challenge, secrets.token_bytes(64))
        signed = openssl(binary, ["dgst", "-sha256", "-sigopt", "rsa_padding_mode:pkcs1",
            "-keyform", "DER", "-sign", "/dev/stdin", str(challenge)], data=private_der)
        write_new(signature, signed)
        openssl(binary, ["dgst", "-sha256", "-sigopt", "rsa_padding_mode:pkcs1", "-verify",
            str(expected_public), "-signature", str(signature), str(challenge)])
    return {"status": "verified", "publicKeySha256": expected, "encryptedSha256": metadata["encryptedSha256"],
        "backup": str(destination), "keychainService": SERVICE, "keychainAccount": account,
        "challengeSignatureVerified": True, "offDeviceRecoveryVerified": False}


def create(private, expected_public, destination, binary, keychain):
    private, expected_public, destination = map(safe_path, (private, expected_public, destination))
    if destination.exists():
        raise BackupError("Destination already exists; backups are never overwritten.")
    if not private.is_file() or private.stat().st_size > 16384:
        raise BackupError("Private key is missing or has an invalid size.")
    if private.stat().st_mode & 0o077:
        raise BackupError("Private key permissions must exclude group and other access.")
    expected = public_fingerprint(binary, expected_public)
    openssl(binary, ["pkey", "-in", str(private), "-check", "-noout"])
    actual = openssl(binary, ["pkey", "-in", str(private), "-pubout", "-outform", "DER"])
    if hashlib.sha256(actual).hexdigest() != expected:
        raise BackupError("Private key differs from the application's trusted public key.")
    password = secrets.token_urlsafe(48).encode("ascii")
    encrypted = openssl(binary, ["pkcs8", "-topk8", "-in", str(private), "-v2", "aes-256-cbc",
        "-v2prf", "hmacWithSHA256", "-iter", "600000"], password=password, password_option="-passout")
    if not encrypted.startswith(b"-----BEGIN ENCRYPTED PRIVATE KEY-----"):
        raise BackupError("OpenSSL did not return encrypted PKCS#8.")
    destination.mkdir(mode=0o700)  # Parent is chosen by the owner; never create a broad tree.
    sync_directory(destination.parent)
    account = str(uuid.uuid4())
    metadata = {"schema": 1, "keychainService": SERVICE, "keychainAccount": account,
        "publicKeySha256": expected, "encryptedSha256": hashlib.sha256(encrypted).hexdigest(),
        "format": "PKCS8-PBES2-AES256CBC-PBKDF2-HMACSHA256-600000",
        "offDeviceRecoveryVerified": False}
    write_new(destination / "backup.json", (json.dumps(metadata, indent=2) + "\n").encode())
    keychain.put(account, password)  # Create only; never replace an existing credential.
    write_new(destination / "signing-key.encrypted.pem", encrypted)
    receipt = verify(destination, expected_public, binary, keychain)
    write_new(destination / "verification.json", (json.dumps(receipt, indent=2) + "\n").encode())
    return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("create", "verify"))
    parser.add_argument("--private-key")
    parser.add_argument("--public-key", required=True)
    parser.add_argument("--destination", required=True)
    parser.add_argument("--openssl", default="openssl", help="OpenSSL 3 executable; macOS LibreSSL lacks required KDF options")
    args = parser.parse_args()
    try:
        if args.mode == "create":
            if not args.private_key:
                raise BackupError("Create requires --private-key.")
            result = create(args.private_key, args.public_key, args.destination, args.openssl, MacKeychain())
        else:
            result = verify(args.destination, args.public_key, args.openssl, MacKeychain())
        print(json.dumps(result, indent=2))
    except Exception as error:
        # Never print arbitrary subprocess output or secret-bearing exception data.
        print(str(error) if isinstance(error, BackupError) else f"Backup failed ({type(error).__name__}); existing files retained.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
