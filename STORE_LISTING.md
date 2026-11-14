# Chrome Web Store listing copy

**Published.** Live listing: https://chromewebstore.google.com/detail/aws-login-helper/cpjgfffnippggeadnapfeggfifkmomag
(reviews tab: add `/reviews`). This is the same id used by the README's
`EXTENSIONID`/`extension-id.txt` instructions, and by the "Rate AWS Login
Helper Extension" links in `content.js`, `options.html`, and the console
banner (`CdpClient.cs`).

Paste these into the Developer Dashboard's **Store listing** tab when
publishing. Search visibility on the Chrome Web Store depends mostly on
relevance + engagement (installs, ratings, low uninstall rate), not keyword
density - Google's Web Store policy explicitly disallows keyword stuffing,
so this copy leads with the real search terms (AWS, SSO, IAM Identity
Center / IIC, login, multi-account) once, in plain language, rather than
repeating them.

## Title

The listing title is **not** a separate dashboard field - it's pulled
straight from `extension/manifest.json`'s `"name"` field. Update that if you
want this title to show:

```
AWS Login Helper - Multi-Account SSO & IIC Login
```

(48 characters - well under the 75-character limit. Currently set to plain
`AWS Login Helper` - same name as the Edge build, so if both ever end up
installed on the same machine, `chrome://extensions` won't visually
distinguish them by name alone.)

## Summary (short description, 132-character limit)

Shown under the title in search results and the listing header.

```
Sign in once, open every AWS account & role in its own isolated window - fast multi-account IAM Identity Center (SSO/IIC) access.
```

(129 characters.)

## Detailed description

The first sentence or two double as the search-result snippet, so the core
keywords are up front.

```
AWS Login Helper makes working across many AWS accounts through AWS IAM Identity Center (successor to AWS SSO, still commonly called IIC) fast and frustration-free.

Sign into your AWS IAM Identity Center (SSO / IIC) access portal once. From then on, every account and role on your access list opens in its own separate, already-authenticated Chrome window - no re-entering your password or MFA code every time you switch accounts, and no accidentally acting in the wrong account because two sessions got mixed up in the same browser profile.

Why teams use it:

• One login, every account - click any account/role on your AWS access portal and it opens instantly, already signed in.
• True isolation - each account/role gets its own separate browser profile and cookie jar, so sessions never bleed into each other.
• Session expiry at a glance - see exactly how long your SSO session and each console session have left, right on the page.
• One click to resume after expiry - if a session does expire, re-auth on the AWS access portal, come back, and one click on the window's note picks up right where you left off - no re-entering MFA in that window.
• Clean up anytime - manage and remove old isolated account profiles from the extension's options page.
• No manual profile switching - skip Chrome's built-in profile switcher entirely; this handles it for you.

Requires the free AWS Login Helper companion app (a small, per-user installer - no admin rights needed). The download link and setup steps are on the extension's options page after you install it.

Built for engineers, SREs, and admins who work across many AWS accounts through AWS Organizations + IAM Identity Center SSO every day.
```

## Notes

- Chrome Web Store review checks that requested permissions match what the
  description says the extension does - the permissions list above
  (`cookies`, `storage`, `tabs`, `nativeMessaging`, `alarms`) all map
  directly to something described here (reading the SSO cookie, remembering
  settings, opening windows, talking to the companion app, keeping that
  connection warm). Keep new permissions and new description copy in sync if
  either changes.
- Category: **Productivity** or **Developer Tools** fits best.
- Screenshots/promo images aren't covered here - the dashboard requires at
  least one 1280x800 or 640x400 screenshot before you can publish.
