#!/usr/bin/env bash
set -euo pipefail

# AWS Login Helper - companion app installer (macOS)
#
# Copies the native host binary into place, makes it executable, and registers it with
# Chrome's native messaging - all under the current user's own home directory, no sudo/admin
# rights needed (same "no admin rights" goal as the Windows build's per-user MSI).
#
# Run this from inside the extracted release folder - the binary and extension-id.txt need to
# be sitting right next to this script, which is exactly how build.sh lays out each release
# tarball.
#
# Usage:
#   ./install.sh                        # normal install
#   ./install.sh --extension-id <id>    # also register a locally-loaded/unpacked extension's
#                                        # id (additive - never replaces the published id;
#                                        # see extension-id.txt once installed)

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BINARY_NAME="AWS-Login-Helper-Host-Chrome-Mac"
BINARY_SRC="$SCRIPT_DIR/$BINARY_NAME"
INSTALL_DIR="$HOME/Library/Application Support/AWS-Login-Helper-Host-Chrome/NativeHost"

EXTENSION_ID_OVERRIDE=""
if [[ "${1:-}" == "--extension-id" ]]; then
  EXTENSION_ID_OVERRIDE="${2:-}"
  if [[ -z "$EXTENSION_ID_OVERRIDE" ]]; then
    echo "error: --extension-id needs a value" >&2
    exit 1
  fi
fi

if [[ ! -f "$BINARY_SRC" ]]; then
  echo "error: could not find $BINARY_SRC" >&2
  echo "Run this script from inside the extracted release folder (it should sit right next to $BINARY_NAME)." >&2
  exit 1
fi

mkdir -p "$INSTALL_DIR"
cp "$BINARY_SRC" "$INSTALL_DIR/$BINARY_NAME"
chmod +x "$INSTALL_DIR/$BINARY_NAME"

# extension-id.txt ships next to the binary in the release tarball (see build.sh) - carried
# along if present so --register below picks up the real published extension id instead of
# falling back to the placeholder.
if [[ -f "$SCRIPT_DIR/extension-id.txt" ]]; then
  cp "$SCRIPT_DIR/extension-id.txt" "$INSTALL_DIR/extension-id.txt"
fi

echo "Installed to: $INSTALL_DIR/$BINARY_NAME"
echo "Registering native messaging host with Chrome..."

if [[ -n "$EXTENSION_ID_OVERRIDE" ]]; then
  "$INSTALL_DIR/$BINARY_NAME" --register "$EXTENSION_ID_OVERRIDE"
else
  "$INSTALL_DIR/$BINARY_NAME" --register
fi

echo ""
echo "Done."
echo ""
echo "If Chrome (or macOS itself) refuses to run this binary the first time - \"cannot be"
echo "opened because Apple cannot check it for malicious software\" - that's Gatekeeper"
echo "reacting to an unsigned, un-notarized build (see README.md in this folder). Clear it"
echo "once with:"
echo "  xattr -d com.apple.quarantine \"$INSTALL_DIR/$BINARY_NAME\""
echo "then try again."
