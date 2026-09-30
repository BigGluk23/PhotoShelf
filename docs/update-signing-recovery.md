# Update signing: recovery and rotation plan

Status: operational plan, **not an implemented remote revocation or key-rotation mechanism**. The current updater embeds one RSA public key, verifies one detached signature and retains that signed metadata with every installation. No production private key is needed to review this document or run synthetic updater tests.

## Current trust boundary

- `TrustedUpdateKey.pem` is public verification material. Only the separately authorized release signing step receives `PHOTOSHELF_UPDATE_SIGNING_KEY`.
- A valid signature authorizes a particular program package; it never authorizes deleting media, restoring a previous catalog, or executing an unfinished file-operation journal.
- Removing a GitHub Release, deleting an Actions secret or disabling the release workflow cannot revoke a package already downloaded by a client. The installed verifier does not fetch a reliable independent revocation list.
- A leaked signing key can produce apparently authentic packages. GitHub repository restrictions reduce distribution opportunities but do not repair a compromised cryptographic trust root.
- Changing the embedded public key alone is not a supported rotation. The old helper must accept the bridge package, and the new app must also accept its persisted active-installation manifest. Old shortcuts use the old verifier and can reject a later installation signed only by a new key.

## Private-key custody and recovery rehearsal

The repository owner should appoint the release owner and a separate recovery contact. Record the public-key SHA-256 fingerprint, creation date, authorized repository and key status in a recovery record accessible independently of the release repository. Define the fingerprint as SHA-256 of the DER SubjectPublicKeyInfo bytes; hashing PEM text is not equivalent.

Keep two encrypted, independently stored backups under the owner's control. Store the decryption material separately from each backup, limit access to the named custodians and test that a backup can actually be restored. An Actions secret is an execution credential, not a readable backup. Never place a private key in Git, a chat, a build artifact, a release asset, shell tracing or diagnostic output.

A recovery rehearsal takes place in an isolated temporary directory on a trusted machine:

1. Restore the encrypted backup with restrictive permissions. Do not upload it merely to test recovery.
2. Derive its public key and compare its DER SubjectPublicKeyInfo SHA-256 with both the independent recovery record and the repository's embedded public key. A mismatch is a stop condition, not permission to replace the application's trust key.
3. Sign a fresh disposable challenge and verify the detached signature with the **embedded public key**, using the same RSA-SHA256/PKCS#1 v1.5 scheme as the updater. Do not use a release manifest as the rehearsal challenge.
4. Record only the public fingerprint, time and success/failure. Preserve the encrypted backups. Remove only the rehearsal's owned temporary files when the owner authorizes that cleanup; ordinary file deletion is not a guarantee of secure erasure on SSDs.
5. If the key must be restored to Actions, obtain explicit authorization for the exact repository/secret, disable logging of secret material, and perform a non-public release-preparation verification before publication. Changing a secret alone is not a completed release verification.

Example fingerprint of the already public repository key:

```sh
openssl pkey -pubin -in src/PhotoShelf.Application/Updates/TrustedUpdateKey.pem -outform DER | openssl dgst -sha256
```

Restoring a backup is appropriate only when the key is still believed confidential. After a suspected leak, treat the key as compromised even if a backup remains available.

## Planned rotation while the old key is still trusted

The safe migration must be implemented and Windows-tested before scheduling retirement of the old key. The present single-key protocol cannot claim seamless rotation.

1. Generate the replacement key in a trusted environment, establish independent fingerprint records and verified encrypted backups. Do not replace the production trust file yet.
2. Design an explicit transition verifier which can accept a bounded, allowlisted pair of keys during a defined overlap period. Key identifiers are routing metadata, not authority: only successful verification with an embedded trusted key grants trust. Unknown algorithms, keys and malformed signature envelopes fail closed.
3. Release a bridge **signed by the old key** while that key remains confidential. Its application, helper, stage verifier and active-installation resolver must understand both old and new signatures and retained installation metadata. Existing clients must still understand the assets used to reach the bridge; placing a new signature-envelope format at the old raw-signature filename would strand them.
4. Test the full chain old → bridge → new-key release with real application/helper processes. Include prepared stages from before rotation, interrupted activation, old shortcuts, offline clients and catalog compatibility. Test that an unrelated key and a key removed from the trust set are rejected.
5. Publish later releases under the new key only after the bridge is available and the legacy-entrypoint policy is explicit. An old EXE contains an immutable old verifier: updating `active-v1.json` to a manifest signed only by the new key can make that old shortcut fail closed. Provide and validate a new trusted launcher/shortcut or require a manual trusted installation for those entrypoints; do not promise that all historical EXEs keep redirecting automatically.
6. Retire the old key from the new verifier and signing workflow after the compatibility decision is made. A client which never received the bridge may then require a manual trusted installation. Retain old releases and diagnostic records; retaining an old key in every future verifier indefinitely is not key retirement.

The bridge design must specify which signatures are accepted for download, installation and already active installation receipts. Do not solve old-receipt compatibility by accepting any unverified manifest or by silently falling back to an old executable against a newer catalog.

## Suspected or confirmed key compromise

This is a break-glass incident, not ordinary rotation:

1. Stop further signing/publication with the affected key. Under explicit owner authorization, disable the release workflow and remove the affected Actions secret; inspect repository access and recent releases through a trusted administration session. Preserve evidence and do not overwrite or delete published artifacts to hide the incident.
2. Determine the potentially affected versions, package hashes and time window. Previously downloaded packages cannot be remotely recalled by the current client. Publish clear guidance through an independently trusted communication route; a checksum obtained from the same compromised distribution channel does not establish authenticity.
3. Advise users to stop installing pending updates until the trusted replacement is identified. Automatic checking can be disabled, but that setting is not revocation and does not invalidate an already prepared package.
4. Create a new trust root in a clean environment. Distribute a clean installer through an independently authenticated route and verify its identity before execution. Use an established code-signing identity if available; do not claim PhotoShelf currently provides Authenticode protection unless that protection has actually been configured and verified.
5. Install the clean program alongside retained versions. Address any untrusted active-program selector deliberately so the clean launcher cannot redirect to an untrusted executable. Preserve the selector and suspect program files for investigation; do not delete media, catalog generations, backups, receipts or file-operation journals.
6. Validate catalog compatibility and pending operations before resuming work. **Never restore an older SQLite snapshot to roll back a program update.** File changes may already have happened after that snapshot. If an untrusted executable ran, potential damage extends beyond the updater's guarantees and requires separate incident assessment and independently verified backups.

A bridge signed only by the compromised old key does not establish trustworthy recovery: an attacker holding the same key can sign a competing bridge. Do not label that route safe or automatic.

## Lost key without evidence of compromise

First attempt a controlled restore from a verified encrypted backup. Confirm the fingerprint and signing challenge before authorizing restoration to Actions. If every usable private-key copy is lost, the old key cannot sign a trusted bridge. Recovery then requires a manually obtained, independently authenticated installation with a new trust root, just as the distribution part of the break-glass procedure describes. Loss of the signing key must not trigger a catalog reset or deletion of existing photographs.

## Required evidence before a rotation/recovery feature is released

- Real Windows old → bridge → new process chain and old-entrypoint behavior, with exact tested source/package identities.
- Rejection of wrong keys, tampered packages, altered prepared stages and removed keys at every relevant trust boundary.
- Interruption before and after pointer publication; no automatic catalog rollback, no reuse of stale install consent and no loss of synthetic originals or journals.
- Backup-restore rehearsal with the recorded public fingerprint and an independent signing challenge.
- Explicit limitations: process-kill tests do not simulate power loss or disk failure; synthetic tests do not establish the integrity of a user's real media collection.
