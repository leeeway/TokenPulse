#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
output=${1:-dist/ui-previews}
compiler_output=$(mktemp -d "${TMPDIR:-/tmp}/tokenpulse-ledger-preview.XXXXXX")
trap 'rm -rf -- "$compiler_output"' EXIT
# swift test builds the Core module used here; no additional scan or user data.
swiftc -parse-as-library -target "$(uname -m)-apple-macos14.0" -I .build/debug/Modules \
  Sources/TokenPulseApp/LedgerView.swift Sources/TokenPulseApp/LedgerViewModel.swift \
  scripts/render-mac-ledger.swift .build/debug/TokenPulseCore.build/*.o \
  -o "$compiler_output/render-ledger"
"$compiler_output/render-ledger" "$output"
