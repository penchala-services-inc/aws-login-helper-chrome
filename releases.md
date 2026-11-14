# Releases (AWS Login Helper - Chrome)

Covers the extension (`extension/`) and the companion native host
(`native-host/`, `native-host-mac/`) together - both are versioned and
released as one unit. For the original Firefox extension's own, much longer
release history, see `../RELEASES.md` at the repo root.

## v1.0.3 (10/31/2026)

- One-click reauth: if an isolated window's session expires, sign in again on the AWS
  access portal, come back to that window, and one click on the note resumes exactly
  where you left off - no re-entering MFA, no getting stuck on AWS's sign-in page.

## v1.0.2 (10/16/2026)

Bug fixes, found through hands-on testing on real Chrome:

- Recent Logins & Services links sometimes did nothing when clicked.
- Opening a service (e.g. S3) by hand inside an isolated window wasn't showing up on the
  portal page's Recent Logins & Services list.
- The Recent Logins & Services list didn't update right away when a new service was visited.
- Refreshing an isolated console window (F5) could make the account/role banner disappear
  for good.

## v1.0.1

- Fixed a bug that was closing the isolated windows.

## v1.0.0

- Initial release.
