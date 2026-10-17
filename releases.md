# Releases (AWS Login Helper - Chrome)

Covers the extension (`extension/`) and the companion native host
(`native-host/`, `native-host-mac/`) together - both are versioned and
released as one unit. For the original Firefox extension's own, much longer
release history, see `../RELEASES.md` at the repo root.

## v1.0.2 (10/16/2026)

Bug fixes, found through hands-on testing on real Chrome:

- Recent Logins & Services links did nothing when clicked. Chrome's MV3
  service worker can go idle and silently drop a `chrome.runtime.sendMessage`
  call sent well after the page loaded - `content.js` now retries once and
  surfaces a clear error if it still fails, instead of failing silently.
- Opening a service (e.g. S3) by hand inside an isolated account/role window
  never showed up back on the portal page's Recent Logins & Services list.
  The native host now watches for new/changed tabs inside each isolated
  window and reports what was visited back to the extension over native
  messaging.
- The Recent Logins & Services list didn't update after new services were
  recorded - fixed by rebuilding it from storage and listening for storage
  changes.
- Refreshing an isolated console window (F5) wiped the account/role banner
  and it never came back, since a plain refresh keeps the same URL and the
  watcher only re-injected the banner on a URL change. It now also checks
  whether the banner is still present and re-injects if it's missing.

## v1.0.1

- Fixed a bug that was closing the isolated windows.

## v1.0.0

- Initial release.
