# Development (AWS Login Helper - Chrome)

Building from source, running an unpacked copy locally, and how the
extension + companion app work together under the hood. See
[README.md](README.md) for what the extension is and how to install it.

## Running it locally

For building from source, or testing before the extension is published.

### 1. Build the native host

Requires the .NET 8 SDK on a Windows machine.

```
cd native-host
dotnet publish -c Release
```

Produces `native-host/bin/Release/net8.0/win-x64/publish/AWS-Login-Helper-Host-Chrome.exe`,
a single self-contained `.exe`. Try it by hand:

```
AWS-Login-Helper-Host-Chrome.exe --version
AWS-Login-Helper-Host-Chrome.exe --register
```

`--register` writes `native-messaging-host.json` next to itself and a
`HKEY_CURRENT_USER\Software\Google\Chrome\NativeMessagingHosts\com.penchala_services_inc.awsloginhelper`
value pointing at it - both without admin rights.

### 2. Build and run the installer

Requires the WiX v4 CLI (`dotnet tool install --global wix`) plus the .NET SDK.

```
cd installer/AWS-Login-Helper-Host-Chrome
dotnet build -c Release
```

Produces `installer/AWS-Login-Helper-Host-Chrome/bin/Release/AWS-Login-Helper-Host-Chrome.msi`.
Double-clicking it (or `msiexec /i AWS-Login-Helper-Host-Chrome.msi`) installs
per-user: copies the exe + `extension-id.txt` to
`%LocalAppData%\AWS-Login-Helper-Host-Chrome\NativeHost\`, registers the native
messaging host with Chrome, and (if the extension isn't already installed)
opens the Chrome Web Store listing so it can be added with one click.

### 3. Load the extension unpacked

Chrome will run any folder as an extension in Developer mode - no store
review needed to test:

- Go to `chrome://extensions`, turn on **Developer mode**, click
  **Load unpacked**, and select this project's `extension/` folder.
- Chrome assigns it its own id (different from the published one) - copy it
  from the extension's card.
- Register that id with the native host - either at install time:
  ```
  msiexec /i AWS-Login-Helper-Host-Chrome.msi EXTENSIONID=<your unpacked extension's id>
  ```
  or, if already installed, without reinstalling:
  ```
  "%LocalAppData%\AWS-Login-Helper-Host-Chrome\NativeHost\AWS-Login-Helper-Host-Chrome.exe" --register <your unpacked extension's id>
  ```
  Either way this is additive - `extension-id.txt` keeps one id per line, so
  a locally-loaded copy and the published extension can both work side by
  side on the same machine without breaking each other.

### 4. Verify

- `chrome://extensions` -> the extension is installed and enabled.
- Open the extension's popup -> "Check companion app" reports OK.
- Sign into the AWS IIC portal, click an "Isolated (Window)" link -> a
  second, separate Chrome window opens, already signed into that
  account/role. `chrome://version` inside it shows a different profile path
  (under `AWS-Login-Helper-Host-Chrome\ChromeProfiles\...`) than your normal Chrome -
  that's expected (see "Development" below).

## Development

### The three pieces

```
extension/     Chrome MV3 extension - runs in the user's normal, everyday Chrome.
                Watches the AWS IIC/SSO portal, and asks the native host to open
                each account/role in its own isolated window.
native-host/   AWS-Login-Helper-Host-Chrome.exe - a small, self-contained C# program that
                Chrome launches via Native Messaging. It owns a SEPARATE,
                dedicated Chrome instance where each AWS role gets its own
                profile, and uses the Chrome DevTools Protocol (CDP) to copy
                the SSO cookie into the right one, open it, and label it.
installer/     A per-user MSI (WiX v4) that installs the native host and
                registers it with Chrome - no admin rights needed anywhere.
```

### Why a *separate* Chrome instance, not the user's real profiles

Chrome's remote debugging port (what CDP needs to set cookies in a profile
that isn't the one the extension runs in) can only be turned on when a
Chrome **process** starts - it can't be attached retroactively to the user's
already-running everyday Chrome, and Chrome collapses every profile under
one `--user-data-dir` into a single process anyway, so a flag passed on a
second launch gets silently forwarded to (and ignored by) the already-
running one.

The native host sidesteps both problems by launching Chrome pointed at its
**own** `--user-data-dir` (`%LocalAppData%\AWS-Login-Helper-Host-Chrome\ChromeProfiles`),
completely separate from the user's normal profile data. Since the native
host always controls that instance's first launch, it can always turn on
`--remote-debugging-port`. Every AWS account/role becomes its own
`--profile-directory` inside it - its own cookie jar, its own window,
persists across restarts, functionally identical to a Firefox container.

The trade-off: these are windows in a second, separate Chrome instance, not
another entry in the user's normal profile switcher. They're still ordinary
Chrome windows (tabs, bookmarks, etc. all work normally) - just not mixed in
with daily browsing.

### How a click flows end to end

1. `content.js` (running in the user's normal Chrome) finds each account/role
   link on the IIC portal page and adds an "Isolated (Window)" link next to it.
2. Clicking it sends `openAccountRole` to `background.js`, which reads the
   current `.awsapps.com` cookies and hands them to the native host over a
   long-lived native-messaging connection.
3. The native host makes sure the dedicated Chrome instance is running,
   opens (or reuses) that role's `--profile-directory` on a tab, sets the SSO
   cookie(s) over that tab's own CDP WebSocket (`Network.setCookie`), then
   navigates it straight to the real AWS console URL.
4. The user sees a normal Chrome window, already authenticated, for that
   specific account/role.

### How the account/role banner and title are shown

A colored banner pinned to the bottom of the window (`account_id |
account_name | role_name`, red for Prod, green for NonProd/sandbox/test/dev,
dark blue otherwise) plus the account/role prefixed onto the tab title. This
is injected directly by the native host over the same CDP connection - a
small script evaluated in the page (`CdpClient.BuildConsoleBannerScript`),
right after the cookie sync. There is no extension loaded in the dedicated
instance at all (Chrome removed the `--load-extension` flag entirely in
Chrome 137, June 2025, which is what an earlier version of this relied on),
so a lightweight watcher (`CdpClient.RegisterProfileWindowAsync`/its instance
watcher loop) polls for navigation - and for brand new tabs opened by hand in
that same window, e.g. ctrl-clicking a service in AWS's own console nav -
re-injecting the script and reporting the service visited back to the
extension (so "Recent Logins & Services" on the portal page stays accurate)
each time, without holding a debugger session attached the whole time (which
was tried first and caused AWS's own session-refresh flow to kick the user
out - see the comments in `CdpClient.cs` for the full history). A plain
refresh of a console tab (F5) keeps the same URL, so that alone can't tell
the watcher the page reloaded and wiped the banner - it also periodically
checks (`CdpClient.IsBannerPresentAsync`) whether the banner div is still
actually there and re-injects if not, without double-reporting it as a new
service visit.

Session expiry is shown in two places, both cookie-driven: the portal page
(`content.js`, reading the `x-amz-sso_authn` cookie) and the console banner
(the native host, reading the `aws-creds` cookie over the same CDP
connection right after the sync).

### Keeping already-open isolated windows refreshable

Cookies normally only get copied into a profile the moment someone clicks
its "Isolated (Window)" link. If the portal's own IIC/SSO session later
expires and the person signs back in, every isolated window they already
had open is still holding that original, now-stale cookie - refreshing one
of those windows (F5) would fail AWS's own silent session-refresh redirect
instead of picking up the new session, since nothing ever told that
profile's cookie jar about it.

`content.js` fixes this by firing `syncCookiesToOpenProfiles` to
`background.js` every time the portal page finishes loading, which reads
the current `.awsapps.com` cookies and sends them to the native host as
`syncOpenProfiles`. `CdpClient.SyncCookiesToOpenProfileWindowsAsync` pushes
them into every profile that currently has a window open - and ONLY those -
by reusing one of that window's own already-open CDP targets
(`Network.setCookie`, no navigation, no banner, no window focus). Nothing
new ever opens or closes.

This is deliberately narrower than the older `syncAllProfiles` native
action (`Program.HandleSyncAllProfiles`, wired to `background.js`'s
`copyCookiesToAllProfiles`), which loops through every profile directory
ever created on disk and briefly opens/closes a tab in each just to set its
cookie - fine for an explicit, occasional "sync all" action, but it used to
also fire automatically on every portal page load (mirroring the Firefox
extension's `copyCookiesToAllContainers`) and was reverted for exactly that
reason: once more than a couple of profiles exist, it looked like a burst
of blank Chrome windows opening and closing on every reload. That old path
is still there, just not called automatically.

**Why this didn't actually work until v1.0.5.** The windowId -> account/
role mapping this all depends on (`CdpClient._profileWindows`) is recorded
once, when `RegisterProfileWindowAsync` first opens or forwards into a
profile's window, and lives only in that native host process's own memory.
That process does NOT reliably live as long as the browser windows it's
tracking do: `background.js` keeps its single `chrome.runtime.connectNative`
connection open only for as long as the extension's own background service
worker is alive, and a periodic keepalive ping (`getNativePort`/
`ensureKeepAlive`) only staves off that connection dying for as long as
something keeps scheduling it - a browser restart, an extension
reload/update, or any other gap where the service worker fully unloads all
kill the native host process and wipe this map clean, even though the
dedicated Chrome instance and every isolated window it owns keep right on
running the whole time. A window left open long enough to actually have its
session expire has almost always outlived the process that originally
registered it, so `SyncCookiesToOpenProfileWindowsAsync` found nothing in
`_profileWindows` to iterate over and silently synced zero profiles -
confirmed by testing (debug-logging the host showed a `syncOpenProfiles`
request completing in a few ms with no per-profile log line at all) and by
navigating straight to the isolated window's own federation link by hand,
which still asked to sign in because the cookie had genuinely never
arrived.

Fixed by also persisting `_profileWindows` to `profile-windows.json` (same
root folder as `cdp-port.txt`/`profile-labels.json`) every time
`RegisterProfileWindowAsync` records a window, and having
`SyncCookiesToOpenProfileWindowsAsync` call
`ReconcileProfileWindowsFromDiskAsync` first: it asks the dedicated
instance which windowIds are ACTUALLY open right now (resolving each
current CDP target's window, same `Browser.getWindowForTarget` call
`RegisterProfileWindowAsync` itself uses), then pulls a matching identity
back in from disk for any of those confirmed-live windowIds this process
doesn't already know about. A disk entry is only ever revived if a live CDP
check just confirmed its windowId is genuinely still open right now, so a
stale entry for a long-closed window (or a same-numbered window in some
later, fully-restarted dedicated instance) can never be wrongly revived.

### Noting AWS's own "You have been signed out" modal

If the IIC/SSO session expires while someone is sitting in an isolated
console window, AWS's own frontend shows its own real-time "You have been
signed out" modal there, with a "Sign in again" button. Clicking that button
re-authenticates DIRECTLY in that isolated profile - bypassing the portal
page entirely, which means re-entering MFA right there instead of getting
the one-click refresh this extension exists to provide.

`CdpClient.BuildConsoleBannerScript`'s injected script already polls a
countdown (`updateExpiryText`, driven by the `aws-creds` cookie's expiry).
Once that countdown reaches zero, it also checks the page for AWS's modal -
matched by its visible heading text ("you have been signed out"), not its
css-module class names (those are a build-time hash baked in by AWS's own
frontend tooling and can change on any console deploy with no notice; the
English copy it shows the user is far more stable to depend on). If the
modal is showing, a banner is added pinned to the very top of the viewport
(above the modal's own backdrop, via the same max z-index the account/role
banner uses) telling the user not to click "Sign in again" there. The check
only fires once our own countdown has also hit zero, so a transient false
match on the modal's text can't trigger it early, and it's a no-op if the
note is already showing (keyed by its own element id), consistent with every
other piece of the injected script being safe to run more than once against
the same still-open page.

**Why F5-after-portal-reauth (v1.0.4/v1.0.5) wasn't enough, and what v1.0.6
changed.** The original note told the user to re-auth on the portal page,
then come back and refresh (F5) - reasoning that `syncCookiesToOpenProfiles`
would have just pushed a fresh `.awsapps.com` cookie into this profile,
so the refresh would pick up the new session. In practice this still failed:
a plain F5 of an already-expired console URL never invokes AWS's SSO
federation flow at all (confirmed by testing - AWS just redirects to a
generic sign-in page regardless of cookie freshness), and even manually
navigating straight to the account/role's federation URL with a
confirmed-fresh synced cookie still failed. A side-by-side DevTools cookie
comparison (a working, freshly-opened window vs. this same window after
expiry + portal reauth) explained why: the expired window's
`console.aws.amazon.com`-domain cookie jar was missing `aws-creds` (the
real session cookie) entirely, but still full of OTHER long-lived
console-side cookies left over from the original sign-in (`aws-userInfo`,
`aws-userInfo-signed`, `last-sign-in-session`, `awsc-rac`, the `noflush_*`
family, and others) - these never expire on their own inside this
long-running dedicated instance, and were found to conflict with a fresh
federation attempt, causing AWS's backend to reject it and bounce back to
"session expired" instead of minting a new `aws-creds`.

v1.0.6 fixes this with no F5 and no portal round-trip required from the
user beyond re-authenticating there once: the moment `checkForSignedOutNote`
first shows the note (same idempotency guard as before), the injected script
also sets `document.title` to a one-shot marker
(`CdpClient.SignedOutTitleMarkerPrefix`) - the only channel available at all
from page JS running in this dedicated, extension-less instance, since no
extension is loaded into it for `chrome.runtime.sendMessage` to reach.
`RunInstanceWatcherLoopAsync`'s existing 1.5s poll of `/json/list` already
reads each target's title; seeing that marker (matched with `Contains`, not
`StartsWith`, since `ensureTitle()` may re-prepend the account/role title
prefix on top of it before the next poll tick) triggers
`CdpClient.ClearStaleSessionCookiesAsync` exactly once - a brief CDP
connect/`Network.clearBrowserCookies`/disconnect against just that profile's
own window, clearing every one of those stale leftover cookies. The note
itself now also shows a plain `<a href>` straight to the federation URL,
rebuilt entirely host-side from the portal origin+path, account id, and role
name that were captured once, when the window was first opened
(`content.js`'s `portalBaseUrl` capture -> `background.js` ->
`Program.cs`'s `HandleSyncAndOpen` -> `CdpClient.ProfileWindowIdentity.
PortalBaseUrl`, persisted to `profile-windows.json` alongside everything
else `RegisterProfileWindowAsync` already persists). Clicking that link is
an ordinary top-level navigation - no click handler, no JS action from this
extension at all - that now succeeds because by the time the user clicks,
the stale cookies are already gone (cleared above) and a fresh
`.awsapps.com` cookie has already landed too, via the existing automatic
`syncCookiesToOpenProfiles` the next time the portal page reloads - exactly
the same cookie state a freshly-opened profile would have. A window opened
before this field existed has nothing to rebuild a link from
(`PortalBaseUrl` empty) and still gets the old plain-text "go to the portal
and press F5" guidance as a fallback.

### Multiple isolated profiles

Each account/role's dedicated Chrome profile can be listed and deleted from
the extension's options page (`chrome://extensions` -> Details -> Extension
options) - useful for cleaning up old/unused ones. Deleting removes that
profile's own folder (cookies, session) under `ChromeProfiles\`; nothing else
is affected.

## Known limitations / things to revisit

- **Cookie sync is `.awsapps.com` cookies only** - if AWS ever moves session
  state elsewhere, this needs updating.
- **First role opened after a reboot** takes an extra second or two, since
  that's when the dedicated Chrome instance has to actually start.
- **No uninstall of the extension itself** - the MSI only manages the
  companion app. Removing the extension is done from `chrome://extensions`
  like any other extension.
- **Chrome must be installed** somewhere `ChromeManager.FindChromeExe()` can
  find it (Program Files, per-user `%LocalAppData%`, or the registered
  "App Paths" registry key).
- **Intermittent CDP port-check delays** can happen on a machine where
  antivirus/EDR intercepts the native host's loopback HTTP calls - needs a
  Defender/EDR exclusion from IT, not a code fix, if it shows up.

## Release builds and versioning

See [CLAUDE.md](../CLAUDE.md) at the repo root for the versioning convention
(extension, native host, and installer all move together as one number) and
[../build-release.sh](../build-release.sh) for building release MSI/tarball
artifacts for all four (browser x OS) combinations in one pass. Each release
is logged in [releases.md](releases.md).
