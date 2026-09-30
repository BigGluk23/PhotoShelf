#!/usr/bin/env bash
# Portable gate: never launches WPF and never opens the user's photo catalog.
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
trap 'result=$?; echo "FAILED portable harness (exit $result), line $LINENO" >&2; exit "$result"' ERR
dotnet_cmd="${DOTNET:-dotnet}"
python_cmd="${PYTHON:-python3}"
scale=false
checks_only=false
for argument in "$@"; do
  case "$argument" in
    --scale) scale=true ;;
    --checks-only) checks_only=true ;;
    --help|-h) echo 'Usage: bash tools/harness.sh [--scale] [--checks-only]'; echo 'Overrides: DOTNET=/path/to/dotnet PYTHON=/path/to/python3'; exit 0 ;;
    *) echo "Unknown argument: $argument" >&2; exit 2 ;;
  esac
done
"$python_cmd" -B tools/test_harness_checks.py
"$python_cmd" -B tools/test_package_checks.py
"$python_cmd" -B tools/test_update_release.py
"$python_cmd" -B tools/test_signing_key_backup.py
"$python_cmd" tools/harness_checks.py repository
if "$checks_only"; then
  echo 'OK repository checks only; no build, tests, or Windows runtime verification requested.'
  exit 0
fi
"$dotnet_cmd" --version
mkdir -p TestResults
results="$(mktemp -d "$repo_root/TestResults/portable-XXXXXXXX")"
filter=(--filter 'Category!=CatalogScale')
if "$scale"; then filter=(); fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
"$dotnet_cmd" restore PhotoShelf.sln --locked-mode --force --disable-parallel
"$dotnet_cmd" list PhotoShelf.sln package --include-transitive --no-restore --format json > "$results/dependencies.json"
"$dotnet_cmd" list PhotoShelf.sln package --vulnerable --include-transitive --no-restore --format json > "$results/dependency-audit.json"
for project in Domain Application Infrastructure.Sqlite; do
  echo "Checking $project..."
  "$dotnet_cmd" test "tests/PhotoShelf.$project.Tests/PhotoShelf.$project.Tests.csproj" \
    -c Release -m:1 -nr:false -p:UseSharedCompilation=false \
    --results-directory "$results" --logger "trx;LogFileName=$project.trx" "${filter[@]}"
done
"$python_cmd" tools/harness_checks.py trx "$results" --projects Domain Application Infrastructure.Sqlite \
  --portable --summary "$results/summary.json"
"$python_cmd" tools/harness_checks.py repository
if ! "$scale"; then echo 'NOT RUN: CatalogScale (opt in with --scale).'; fi
echo 'NOT RUN: WPF UI, published Windows EXE, native Windows runtime checks. Run tools/harness.ps1 on Windows.'
echo "OK portable harness. Results: $results"
