# Signed update release protocol

The application checks public stable GitHub Releases in `BigGluk23/PhotoShelf` without an account or embedded token. A release is not an automatic installation. Startup checks only fetch metadata; downloading and restarting remain separate explicit user actions. Network failures in a check must not be presented as "you have the latest version".

## Release pipeline

`.github/workflows/update-release.yml` is **workflow_dispatch only**, from `main`. A push does not create or publish a release. The operator supplies the exact version tag, a completed Windows CI run ID, and `publish` (default `false`). The workflow:

1. Requires the tag to equal `v` + `Directory.Build.props` Version and the checked-out SHA to equal the dispatch SHA.
2. Requires a completed successful `.github/workflows/windows-ci.yml` run for that exact SHA on this repository's `main`, from `push` or `workflow_dispatch`. Both `windows` and `Same-host baseline/current performance` must pass; incomplete, skipped, failed, fork and PR runs are rejected. It reads jobs from the selected run's current attempt, not a mixture chosen from different runs.
3. Downloads the existing `PhotoShelf-win-x64-<SHA>` artifact and matching Windows results. It does **not rebuild** the package. The archive hash must equal `harness-summary.json`; the report must confirm extracted package verification and the updater CLI self-test.
4. Validates bounded ZIP entries, Windows-safe paths, no links/case collisions, every inventory hash, exact source/version, the helper and bundled codec/licenses. Generates and signs the update manifest below.
5. Creates a draft release, uploads exactly the ZIP, manifest and signature, downloads all three back and verifies their hashes. Only `publish=true` makes it public/latest. A failed upload leaves an unpublished draft, never a partially public update.

The tag must be new and greater than every existing published stable PhotoShelf version. Existing tags/releases/assets are never moved or overwritten. If the workflow fails after creating a draft, inspect the retained draft and failure evidence before any manual recovery; the workflow does not delete it or silently replace assets.

Windows packaging publishes `PhotoShelf.Updater.exe` separately with its own locked publish graph, then copies only this self-contained helper into the Desktop package. This avoids overwriting Desktop dependencies. The extracted helper runs `--self-test` with a 15-second watchdog; this is a read-only CLI startup check, not proof of a complete Windows update/recovery cycle. Package inventory verification runs again after all smoke checks.

## Trust material

The public RSA key is `src/PhotoShelf.Application/Updates/TrustedUpdateKey.pem`, embedded in the application. The corresponding PEM private key belongs only in the repository Actions secret `PHOTOSHELF_UPDATE_SIGNING_KEY`. Configure that secret separately with the repository owner's authorization. No private key belongs in Git, CI artifacts, release assets, application settings or logs.

The signing step passes the secret to OpenSSL through stdin and removes it from the child process environment. It does not write a key file. The publication step does not receive the signing secret. The generated signature is verified with the committed public key before any release is created; missing/mismatched keys block release preparation. A compromised signing key requires a separate trust-rotation design; simply replacing the public key would strand already installed versions.

## Protocol v1

Required assets:

- `PhotoShelf-v<version>-ultra-win-x64.zip`
- `photoshelf-update.json`: UTF-8 without BOM, signed **as exact raw bytes**, not parsed/reserialized
- `photoshelf-update.sig`: raw detached RSA-SHA256 PKCS#1 v1.5 signature, not base64 text

Example manifest shape (hash values below are illustrative, not a release):

```json
{
  "protocolVersion": 1,
  "version": "1.11.0",
  "runtime": "win-x64",
  "packageUrl": "https://github.com/BigGluk23/PhotoShelf/releases/download/v1.11.0-ultra/PhotoShelf-v1.11.0-ultra-win-x64.zip",
  "packageSha256": "<64 lowercase hex characters>",
  "packageManifestSha256": "<SHA256 of exact package-manifest.json bytes>",
  "packageBytes": 123456789,
  "unpackedBytes": 234567890,
  "minCatalogSchema": 5,
  "maxCatalogSchema": 5,
  "releaseNotesUrl": "https://github.com/BigGluk23/PhotoShelf/releases/tag/v1.11.0-ultra"
}
```

The visible Ultra suffix is product branding. The application/release title displays **PhotoShelf Ultra v1.11** for technical version `1.11.0-ultra`; only a zero patch is omitted from display. Signed `version` always has three canonical numeric components; tags/package inventory keep `-ultra`. Hashes and sizes refer to the already tested final ZIP and its actual contents, including the inner package inventory. The inventory hash binds later extracted-file validation to the signature without trusting an unsigned replacement manifest. Maximum compressed size: 2 GiB; unpacked: 4 GiB; files: 1024. Catalog schema range is deliberately fixed to 5 in the initial release policy; do not widen it without migration and rollback tests.

Signing metadata does not authorize modifying media or restoring an old SQLite snapshot. An updater rollback must respect current catalog compatibility and leave journals and originals intact. Compatible user-provided LGPL decoder replacements are a runtime policy distinct from validation of the original distributed package.

## Local tooling checks

`python3 -B tools/test_update_release.py` uses synthetic packages and disposable generated test keys. It checks archive identity, hashes, missing helper, traversal/aliases/links, size limits, CI status/SHA/run scope and real detached signing verification. The signing roundtrip is Linux/macOS-only because production signing runs on the Linux release runner; Windows packaging runs the other checks. `tools/test_package_checks.py` separately rejects a missing helper. These checks do not publish a release or open a user's library.

API contracts: [GitHub workflow runs](https://docs.github.com/en/rest/actions/workflow-runs), [GitHub releases](https://docs.github.com/en/rest/releases/releases), [Actions secrets](https://docs.github.com/en/actions/how-tos/write-workflows/choose-what-workflows-do/use-secrets).
