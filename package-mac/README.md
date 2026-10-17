# AWS Login Helper - macOS companion app packaging

The macOS counterpart to the Windows build's `installer/` (WiX MSI). Deliberately much
simpler than that, because there's genuinely less to do on this platform: no registry to
write to, and no GUI installer format to build - just a binary, a small JSON manifest, and a
shell script.

## What's here

- `build.sh` - publishes the native host (`../native-host-mac/`) for both Mac architectures
  (`osx-arm64`, `osx-x64`) and stages each into a ready-to-ship folder under `dist/`.
- `install.sh` - run by the end user, from inside the extracted release folder. Copies the
  binary into `~/Library/Application Support/AWS-Login-Helper-Host-Chrome/NativeHost/`, makes
  it executable, and registers it with Chrome - no sudo, same "no admin rights" goal as the
  Windows MSI.
- `uninstall.sh` - removes everything `install.sh` put in place.

## Why no `.pkg`, no code signing, no notarization

Keeping this simple on purpose. A `.pkg`/`.dmg` (via `pkgbuild`/`productbuild`) would still
hit the exact same Gatekeeper wall described below - a GUI installer wrapper doesn't avoid
that, it just adds packaging work on top of it for no real benefit here. A shell script does
the same job with far less to maintain, and is exactly how plenty of other native-messaging
companion apps distribute themselves outside the Mac App Store.

**The one real rough edge, and it's unavoidable without an Apple Developer account:** macOS
Gatekeeper will warn about (or outright refuse to run) a downloaded binary that isn't signed
with an Apple Developer ID and notarized by Apple. `install.sh` prints a one-line workaround
(`xattr -d com.apple.quarantine <path>` clears the download's quarantine flag) for testing
this locally. For a real, trustable, no-scary-warning distribution to other people, you'd
want to add `codesign` and `xcrun notarytool` steps to `build.sh` - that needs a paid Apple
Developer Program membership ($99/yr) and a Developer ID certificate, which is an account and
process decision for whoever owns releases, not something a script can decide on its own.
Left out here so this stays simple until that decision actually gets made; everything else in
this folder works the same with or without it.

## Install (end user)

1. Download the `.tar.gz` matching your Mac's chip (Apple Silicon or Intel) from the latest
   GitHub release.
2. Extract it, `cd` into the extracted folder.
3. Run `./install.sh`.
4. If Gatekeeper blocks the first launch, run the `xattr` command `install.sh` printed, then
   try again.

## Uninstall

```
./uninstall.sh
```

## Extension

No separate macOS build needed. The same published Chrome Web Store extension
(`cpjgfffnippggeadnapfeggfifkmomag`) that already works on Windows Chrome works unchanged on
Mac Chrome - nothing under `extension/` is platform-specific, only the companion app is.

## Build from source

Requires the .NET 8 SDK (does not need to run on an actual Mac - the `osx-arm64`/`osx-x64`
runtime packs publish fine cross-platform; the output still needs a real Mac to run on):

```
cd package-mac
./build.sh
```

Produces `dist/aws-login-helper-host-chrome-mac-osx-arm64.tar.gz` and the `osx-x64`
equivalent, each already containing the binary, `extension-id.txt`, `install.sh`, and
`uninstall.sh` - ready to attach to a GitHub release as-is.

## Data this companion app keeps on disk

All under `~/Library/Application Support/AWS-Login-Helper-Host-Chrome/`:

- `NativeHost/` - the installed binary and `extension-id.txt`.
- `host.log` - written only when "Enable debug logging" is checked on the extension's options
  page (or always, for CLI commands run by hand).
- `cdp-port.txt`, `profile-labels.json` - small bookkeeping files.
- `ChromeProfiles/` - the dedicated, separate Chrome instance this tool launches, with one
  `--profile-directory` per AWS account/role. This is NOT the user's everyday Chrome profile -
  see the top-level README's "Why a separate Chrome instance" section.
