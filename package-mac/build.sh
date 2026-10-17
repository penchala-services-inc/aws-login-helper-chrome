#!/usr/bin/env bash
set -euo pipefail

# Builds both macOS release artifacts (Apple Silicon + Intel) and stages each one into a
# ready-to-ship folder alongside install.sh/uninstall.sh/extension-id.txt - the same shape
# install.sh itself expects next to it.
#
# Needs the .NET 8 SDK. Does NOT need to run ON a Mac - the osx-arm64/osx-x64 runtime packs
# publish fine cross-platform - but the resulting binary still needs an actual Mac to run (and
# to codesign/notarize on, if that's ever added - see README.md in this folder for why that's
# deliberately not done here).

cd "$(dirname "${BASH_SOURCE[0]}")"
NATIVE_HOST_DIR="../native-host-mac"
DIST_DIR="dist"

rm -rf "$DIST_DIR"

for rid in osx-arm64 osx-x64; do
  echo "== Publishing $rid =="
  dotnet publish "$NATIVE_HOST_DIR" -c Release -r "$rid" --self-contained -p:PublishSingleFile=true

  publish_dir="$NATIVE_HOST_DIR/bin/Release/net8.0/$rid/publish"
  out="$DIST_DIR/$rid"
  mkdir -p "$out"
  cp "$publish_dir/AWS-Login-Helper-Host-Chrome-Mac" "$out/"
  cp "$NATIVE_HOST_DIR/extension-id.txt" "$out/"
  cp install.sh "$out/"
  cp uninstall.sh "$out/"
  chmod +x "$out/AWS-Login-Helper-Host-Chrome-Mac" "$out/install.sh" "$out/uninstall.sh"

  tarball="$DIST_DIR/aws-login-helper-host-chrome-mac-$rid.tar.gz"
  tar -czf "$tarball" -C "$DIST_DIR" "$rid"
  echo "-> $tarball"
done

echo ""
echo "Done. Upload both .tar.gz files from $DIST_DIR/ to the GitHub release, same as the"
echo "Windows build's .msi - one per Mac architecture, so users grab the one matching their chip."
