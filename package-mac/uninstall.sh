#!/usr/bin/env bash
set -euo pipefail

# AWS Login Helper - companion app uninstaller (macOS)
#
# Removes everything install.sh created: the Chrome native-messaging-host manifest, the
# installed binary/extension-id.txt, and (on confirmation) this host's own data directory -
# logs, and every dedicated/isolated Chrome profile it ever created.
#
# Does NOT touch the extension itself - same note as the Windows build: remove that the
# normal way from chrome://extensions.

INSTALL_ROOT="$HOME/Library/Application Support/AWS-Login-Helper-Host-Chrome"
BINARY="$INSTALL_ROOT/NativeHost/AWS-Login-Helper-Host-Chrome-Mac"
MANIFEST="$HOME/Library/Application Support/Google/Chrome/NativeMessagingHosts/com.penchala_services_inc.awsloginhelper.json"

if [[ -x "$BINARY" ]]; then
  "$BINARY" --unregister || true
elif [[ -f "$MANIFEST" ]]; then
  echo "Binary already gone - removing the manifest file directly."
  rm -f "$MANIFEST"
else
  echo "No manifest or binary found - nothing to unregister."
fi

if [[ -d "$INSTALL_ROOT" ]]; then
  read -r -p "Remove \"$INSTALL_ROOT\" (binary, logs, and every isolated Chrome profile this tool created)? [y/N] " answer
  if [[ "$answer" =~ ^[Yy]$ ]]; then
    rm -rf "$INSTALL_ROOT"
    echo "Removed $INSTALL_ROOT."
  else
    echo "Left $INSTALL_ROOT in place."
  fi
fi

echo "Done. The Chrome extension itself isn't touched by this script - remove it from chrome://extensions if you want it gone too."
