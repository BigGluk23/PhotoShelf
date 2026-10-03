# Windows updater process lifecycle fixture

This project is a **test executable**, never part of the shipping PhotoShelf package. `scripts/verify-update-lifecycle.ps1` builds it twice with distinct actual assembly versions, then runs seven Windows scenarios. The release source SHA and release version are recorded in `updater-e2e.json`.

The fixture calls the production `UpdateInstaller`, `UpdateProcessHost`, request parser, signed package verification, active-pointer resolution, shortcut redirect and health receipt code. Normal fixture startup holds the production `UpdateStartupLease` through redirect and until it owns an exclusive synthetic catalog writer lease. A real parent holds Windows file handles for a synthetic original and a real SQLite file and explicitly creates a request. The runner starts a real helper and passes only its PID/start time to the parent; the parent itself calls `UpdateInstallationHandoff.AcceptWhenReadyAsync`. The test checks matching durable ready/accepted/consumed/committed records before draining the handles, then keeps the parent alive until a separate exit signal. The helper must wait for actual process exit. New programs are separately compiled executable processes and publish health only after passing startup admission and reading the synthetic catalog and original.

A fresh RSA private test key exists only in the runner's memory. No production key is read, generated, exported, transmitted or substituted. The fixture-only config carries the public key and exclusively owned synthetic root. It introduces no trust/root/fault override into the production helper or application. Existing installer checkpoint injection pauses a fixture helper before pointer publication; synchronous fixture progress pauses it after actual candidate launch and health receipt; the runner terminates only the process handle it created. Original media, SQLite bytes and operation journal hashes must remain identical, and SQLite must pass `integrity_check` after each scenario.

Within `helper-kill-before-activation`, three ordinary old-shortcut launches run after the parent has exited while the helper is paused before pointer publication. Each must report busy at startup admission, without opening the catalog or producing an application launch receipt. After helper termination, normal startup must work again from the retained pointer. This adds evidence within the existing seven-scenario report contract; fixture exit code 4 and busy receipts are test-only controls.

Required scenarios:

- `explicit-consent-and-parent-drain`
- `verified-two-version-install-and-health`
- `old-shortcut-launches-active-version`
- `used-request-replay-rejected`
- `downgrade-rejected`
- `helper-kill-before-activation`
- `helper-kill-after-activation`

This report **does not claim full WPF update UI coverage**. The console fixture implements the isolated resource-check child protocol; inert synthetic codec files satisfy package layout but are never decoded. Its SQLite schema checks readable preserved data, not catalog schema migration. Both assembly versions use current production source, not a historical app binary. Full Desktop consent/closing/view restoration and shipped WinForms helper UI require their own separate Windows checks. Process-kill tests do not prove resilience to power loss or physical disk failure.

Integration: invoke the PowerShell script after shipping artifacts have been published, with `-ResultsDirectory`, exact `-Commit`, canonical `-Version`, and optionally `-DotNet`. The script writes one compact `updater-e2e.json`; large fixture binaries and owned synthetic evidence stay in the system temporary directory and must not be included in release assets. Keep both normal and publish lock profiles committed and locked restore enabled.
