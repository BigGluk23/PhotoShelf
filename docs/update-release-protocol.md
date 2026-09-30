# Signed update release protocol

The application checks public stable GitHub Releases in `BigGluk23/PhotoShelf` without an account or embedded token. A release is not an automatic installation. Startup checks only fetch metadata; downloading and restarting remain separate explicit user actions. Network failures in a check must not be presented as "you have the latest version".

## Release pipeline

`.github/workflows/update-release.yml` is **workflow_dispatch only**, from `main`. A push does not create or publish a release. A trusted collaborator other than `BigGluk23` supplies the exact version tag, a completed Windows CI run ID, and `publish` (default `false`). The owner separately approves the protected job in GitHub. A release rerun is rejected: use a fresh manual dispatch, so an earlier run's approval cannot be reused.

The workflow has two jobs:

1. `validate` has a read-only token and **no environment**. GitHub evaluates whether a signing secret is available at repository/organization scope and passes only that boolean to the runner. If present, validation fails; no private key bytes are passed to this job.
2. The job reads the live `release` environment before queuing the protected job. It requires `BigGluk23` as the sole required reviewer, `prevent_self_review=true`, and exactly one selected deployment rule: branch `main`. Missing environment, missing rules, wildcard/tag policies, incomplete responses and API errors fail closed. This prevents the first run from silently creating an unprotected environment.
3. It requires the tag to equal `v` + `Directory.Build.props` Version and the checked-out SHA to equal the dispatch SHA. A completed successful `.github/workflows/windows-ci.yml` run must exist for that exact SHA on this repository's `main`, from `push` or `workflow_dispatch`. Both `windows` and `Same-host baseline/current performance` must pass. It reads jobs from the selected run's current attempt; incomplete, skipped, failed, fork and PR runs are rejected.
4. It downloads the existing `PhotoShelf-win-x64-<SHA>` artifact and matching Windows results. It **does not rebuild** the release package. The archive hash must equal `harness-summary.json`; the report must confirm extracted package verification, updater CLI self-test, and both updater lifecycle reports described below.
5. It validates bounded ZIP entries, Windows-safe paths, no links/case collisions, every inventory hash, exact source/version, the helper and bundled codec/licenses. It prepares an unsigned candidate manifest and records its exact hash and the ZIP hash for the owner to review.
6. `sign_publish` depends on successful validation and declares `environment: release`. GitHub's required-reviewer protection holds the job until independent owner approval. Before using the secret, it rechecks the live environment and the run's actual approval history: one approval by `BigGluk23` for the same environment ID. It downloads and validates the candidate again and requires the exact hashes approved before the job started.
7. Only the signing step receives the environment private key. It signs and verifies the approved manifest. The final step creates a draft release, uploads exactly the ZIP, manifest and signature, downloads all three back and verifies their hashes. Only the explicit `publish=true` dispatch choice makes it public/latest. A failed upload leaves an unpublished draft, never a partially public update.

Repository/organization fallback detection is a check at validation time, not a defense against a compromised administrator who can change secrets, protection settings or workflow code between jobs. Protect `main` and require owner review of workflow changes as described in [release-protection-setup.md](release-protection-setup.md). The existence of YAML does not mean these server-side rules have been configured.

Administrator bypass must be disabled in GitHub Settings. If the live response exposes `can_admins_bypass`, the validator rejects any value other than `false`. The documented REST response does not guarantee this field; when absent, the job explicitly reports that the owner must verify the UI setting. Absence is never reported as successful API verification of that setting.

The tag must be new and greater than every existing published stable PhotoShelf version. Existing tags/releases/assets are never moved or overwritten. If the workflow fails after creating a draft, inspect the retained draft and failure evidence before any manual recovery; the workflow does not delete it or silently replace assets.

Windows packaging publishes `PhotoShelf.Updater.exe` separately with its own locked publish graph, then copies only this self-contained helper into the Desktop package. This avoids overwriting Desktop dependencies. The extracted helper runs `--self-test` with a 15-second watchdog; this is a read-only CLI startup check, not proof of a complete Windows update/recovery cycle. Package inventory verification runs again after all smoke checks.

Two additional reports are mandatory before a candidate can be signed. Both must identify the exact source commit and technical release version, `platform: windows`, `status: passed`, and their explicit test scope/limitations. `harness-summary.json` must contain the corresponding `Verified=true` value and SHA-256 of the exact report bytes:

- `updater-e2e.json`: `updaterProcessLifecycleVerified` / `updaterProcessLifecycleReportSha256`. Scope is `production-updater-process-lifecycle-with-fixture-applications`. All seven scenarios are required: consent/parent drain; two-version install/health; old shortcut; replay rejection; downgrade rejection; helper termination before activation; helper termination after activation. Original hashes and SQLite integrity must be preserved. This report exercises production updater components with synthetic application processes; it is not the real Desktop UI test.
- `updater-desktop-e2e.json`: `updaterDesktopLifecycleVerified` / `updaterDesktopLifecycleReportSha256`. Scope is `production-desktop-update-lifecycle-with-test-trust`. It must record two increasing test versions and all real Desktop assertions: normal old entrypoint, install button, old process exit, helper/new process start, health receipt, restored view, preserved synthetic catalog/originals/journals, old-shortcut redirection, and no new application errors. Its source copy uses disposable test trust and test assembly versions; the release package's production trust is not modified.

Missing reports, mismatched hashes, another commit/version, skipped scenarios or false assertions block signing. These tests do not claim physical power-loss coverage, all Windows configurations, or a live public-network update signed with the production private key.

For cross-host lockfile maintenance, pass `-p:RuntimeIdentifier=win-x64` explicitly together with `-p:SelfContained=true -p:PublishSingleFile=true -p:PhotoShelfPublish=true` to `dotnet restore --force-evaluate`. The restore CLI `-r` option alone adds to the RID list and can retain the host RID for self-contained projects. Verify the result with the actual locked `dotnet publish -r win-x64` command; never disable locked restore in CI to bypass a mismatch.

## Trust material

The public RSA key is `src/PhotoShelf.Application/Updates/TrustedUpdateKey.pem`, embedded in the application. The corresponding PEM private key belongs only in the **environment secret** `PHOTOSHELF_UPDATE_SIGNING_KEY` within the protected **release** environment. A repository or organization secret with that name is forbidden. Configure the environment secret separately after owner authorization and verification of all protection settings. No private key belongs in Git, CI artifacts, release assets, application settings or logs.

The signing step passes the secret to OpenSSL through stdin and removes it from the child process environment. It does not write a key file. The publication step does not receive the signing secret. The generated signature is verified with the committed public key before any release is created; missing/mismatched keys block release preparation. A compromised signing key requires the recovery procedure in [update-signing-recovery.md](update-signing-recovery.md); simply replacing the public key would strand already installed versions. An encrypted local backup is not independent off-device recovery until the owner has verified restoration with a separately held password and copy.

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

`python3 -B tools/test_update_release.py` uses synthetic packages and disposable generated test keys. It checks archive identity, hashes, missing helper, traversal/aliases/links, size limits, CI status/SHA/run scope, both lifecycle report gates, unsigned candidate preparation, owner/environment controls, and real detached signing verification. The signing roundtrip is Linux/macOS-only because production signing runs on the Linux release runner; Windows packaging runs the other checks. `tools/test_package_checks.py` separately rejects a missing helper. These checks do not publish a release or open a user's library.

`tools/release_security.py` only validates public control-plane metadata and the non-environment secret-presence boolean. Its workflow API calls require `actions: read`; release creation additionally uses `contents: write` in the protected job. Configuring environments/selected branch rules needs repository administration rights, which the ordinary workflow token does not provide. API authorization failure stops release preparation; no extra PAT or weaker policy is substituted.

API contracts: [GitHub workflow runs and approval history](https://docs.github.com/en/rest/actions/workflow-runs), [environment metadata](https://docs.github.com/en/rest/deployments/environments), [deployment branch policies](https://docs.github.com/en/rest/deployments/branch-policies), [GitHub releases](https://docs.github.com/en/rest/releases/releases), [Actions secrets](https://docs.github.com/en/actions/how-tos/write-workflows/choose-what-workflows-do/use-secrets).
