# AWS Login Helper (Chrome)

Sign into AWS IAM Identity Center once, then open any account/role in its
own isolated Chrome window - already signed in, no re-entering MFA per
account. A Chrome port of the original Firefox "AWS Login Helper" extension
(Firefox used **containers** for this; Chrome has no equivalent extension
API, so this port gets the same result via a small companion app instead -
see "Development" below).

> **Windows only, for now.** The companion app (the piece that actually
> opens the isolated windows) is a native Windows program (`.exe` + a
> per-user `.msi` installer) - it does not run on macOS or Linux. The
> extension itself will install fine on any OS Chrome supports, but without
> the companion app running on Windows, clicking an account/role link will
> not do anything.

> **Open source, nothing hidden.** The companion app's entire source is
> right here in this repo, under `native-host/` - there's no separate,
> closed-source binary being downloaded from anywhere else. All it does is
> launch a separate, dedicated Chrome instance for each AWS account/role and
> copy your AWS SSO session cookie into it, so you don't have to sign in
> again (see "Development" below for exactly how). No telemetry, no data
> leaving your machine to anywhere other than AWS itself. Read the code
> yourself before you install it if you'd like:
> https://github.com/penchala-services-inc/aws-login-helper-chrome

## Install

Two pieces, both required:

1. **Extension** - [Chrome Web Store listing](https://chrome.google.com/webstore)
   *(not yet published - see "Running it locally" below to load it unpacked
   in the meantime)*.
2. **Companion app** - [download the installer](https://github.com/penchala-services-inc/aws-login-helper-chrome/releases/latest)
   and run the `.msi`. It installs per-user - no admin rights, no UAC prompt.

The extension alone can't open isolated windows without the companion app
running, and the companion app needs to know the extension's ID (handled
automatically once the extension is published; see "Running it locally" if
you're using an unpacked copy).

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

Produces `installer/AwsLoginHelperNative/bin/Release/AWS-Login-Helper-Host-Chrome.msi`.
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
  that's expected (see "Development").

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
so a lightweight watcher (`CdpClient.StartBannerWatcher`) polls for
navigation and re-injects the script every time the user browses to a new
console page in that window, keeping the banner and title showing up
consistently without holding a debugger session attached the whole time
(which was tried first and caused AWS's own session-refresh flow to kick the
user out - see the comments in `CdpClient.cs` for the full history).

Session expiry is shown in two places, both cookie-driven: the portal page
(`content.js`, reading the `x-amz-sso_authn` cookie) and the console banner
(the native host, reading the `aws-creds` cookie over the same CDP
connection right after the sync).

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
