# AWS Login Helper (Chrome)

Sign into AWS IAM Identity Center once, then open any account/role in its
own isolated Chrome window - already signed in, no re-entering MFA per
account. A Chrome port of the original Firefox "AWS Login Helper" extension
(Firefox used **containers** for this; Chrome has no equivalent extension
API, so this port gets the same result via a small companion app instead -
see [development.md](development.md) for exactly how).

**We now support both Windows and macOS.** The companion app (the piece
that actually opens the isolated windows) is a native program -
`native-host/` + `installer/` for Windows (a per-user `.msi`),
`native-host-mac/` + `package-mac/` for macOS (a shell script, see
`package-mac/README.md`; unsigned/un-notarized for now, so Gatekeeper will
warn on first run). It does not run on Linux. The extension itself will
install fine on any OS Chrome supports, but without the companion app
running, clicking an account/role link will not do anything.

> **Open source, nothing hidden.** The companion app's entire source is
> right here in this repo, under `native-host/` - there's no separate,
> closed-source binary being downloaded from anywhere else. All it does is
> launch a separate, dedicated Chrome instance for each AWS account/role and
> copy your AWS SSO session cookie into it, so you don't have to sign in
> again. No telemetry, no data leaving your machine to anywhere other than
> AWS itself. Read the code yourself before you install it if you'd like:
> https://github.com/penchala-services-inc/aws-login-helper-chrome

## Screenshots

The AWS access portal, with the "Isolated (Window)" links next to each
account/role and the Recent Logins & Services panel at the top:

![AWS access portal with isolated-window links and Recent Logins & Services](images/portal_new.png)

An isolated console window, already signed in, with the account/role banner
pinned to the bottom:

![AWS console with the account/role banner](images/console%20page.png)

The extension's options page - default console region, companion app
status, and the list of isolated profiles:

![Extension options page with isolated profiles](images/options%20with%20profiles.png)

## Install

Two pieces, both required:

1. **Extension** - [Chrome Web Store listing](https://chromewebstore.google.com/detail/aws-login-helper/cpjgfffnippggeadnapfeggfifkmomag).
2. **Companion app**:
   - Windows - [download the installer](https://github.com/penchala-services-inc/aws-login-helper-chrome/releases/latest)
     and run the `AWS-Login-Helper-Host-Chrome.msi`. Installs per-user - no admin rights, no UAC prompt.
   - macOS - see `package-mac/README.md` for the download + `install.sh` steps.

The extension alone can't open isolated windows without the companion app
running, and the companion app already knows the published extension's ID
(baked into `extension-id.txt`/the installer's `EXTENSIONID` default - see
[development.md](development.md) only if you're testing an unpacked copy
instead).

> **The first click is slow - that's expected.** The very first account/role
> you open (after a reboot, or whenever the companion app's dedicated Chrome
> instance isn't already running) has to cold-start that instance before the
> window can appear - typically a couple of seconds, longer on a machine
> where antivirus scans the companion app's `.exe` on that first launch.
> Every click after that, for the rest of the session, is fast.

## How it works

The extension watches the AWS access portal and adds an "Isolated (Window)"
link next to every account/role. Clicking one hands your current AWS SSO
session cookie to the companion app, which opens that account/role in its
own dedicated, isolated browser window - already signed in - and labels it
with a colored banner (account id, account name, role) so it's always clear
which account you're in. Opening a service by hand inside that window (e.g.
clicking into S3) gets reported back too, so it shows up under "Recent
Logins & Services" on the portal page next time you look.

For the full mechanics - why a second browser instance, how a click flows
end to end, how the banner gets injected - see [development.md](development.md).

## Other AWS Login Helper builds

- [Firefox (original)](https://github.com/penchala-services-inc/aws-login-helper-firefox) - uses Firefox containers instead of a companion app.
- [Edge](https://github.com/penchala-services-inc/aws-login-helper-edge) - the Microsoft Edge port, same approach as this one.

## Releases

See [releases.md](releases.md) for the version history.
