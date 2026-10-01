// Background service worker (Chrome MV3)
//
// Firefox's version of this extension used contextualIdentities ("containers") to give
// each AWS account/role its own cookie jar inside the SAME browser window. Chrome has no
// equivalent extension API - there is no way for an extension to create a new Chrome
// *profile*, and an extension only ever sees cookies for the profile it runs in.
//
// To get the same "one isolated, persistent identity per account/role" behavior, this
// extension talks to a native messaging host (a small Windows program installed alongside
// the extension). The native host:
//   1. Launches/maintains a SEPARATE, dedicated Chrome instance (its own --user-data-dir),
//      independent of the user's everyday Chrome windows.
//   2. Inside that dedicated instance, one Chrome "profile directory" == one AWS account/role.
//   3. Uses the Chrome DevTools Protocol (CDP) to inject the AWS IIC session cookie into the
//      right profile and navigate it to the console URL - functionally identical to what
//      browser.cookies.set + cookieStoreId did on Firefox.
//
// This file only handles the SSO portal side: reading cookies from the tab where the user
// is signed in, and asking the native host to open a role in its isolated window.

const NATIVE_HOST = 'com.penchala_services_inc.awsloginhelper';
const MAX_RECENT_ENTRIES = 5;

function getDebugStatus() {
  return chrome.storage.local.get('enable-debug').then((result) => !!result['enable-debug']);
}

let IsDebug = false;
getDebugStatus().then((v) => { IsDebug = v; });

// Without this, toggling the options page's "Enable debug logging" checkbox only took
// effect after this service worker next got torn down and recycled (MV3 workers are killed
// on idle, so it usually "worked" eventually - just not right when someone flipped it trying
// to capture a specific problem). Reacting to the storage write directly makes it immediate.
chrome.storage.onChanged.addListener((changes, area) => {
  if (area === 'local' && 'enable-debug' in changes) {
    IsDebug = !!changes['enable-debug'].newValue;
  }
});

function log(...args) {
  if (IsDebug) console.log('[AWSLoginHelper]', ...args);
}

// Opens a one-time "get started" tab right after a fresh install, pointing straight at the
// companion app download - the extension is genuinely useless without it (clicking an
// account/role link just does nothing), and someone who only read the Chrome Web Store
// listing has no other way to discover that a second install step exists at all. Deliberately
// scoped to reason === 'install' only: firing this again on every update (reason === 'update')
// would be an unwelcome surprise re-opening a tab the user didn't ask for just because a new
// version shipped.
chrome.runtime.onInstalled.addListener((details) => {
  if (details.reason === 'install') {
    chrome.tabs.create({ url: chrome.runtime.getURL('welcome.html') });
  }
});

// ---------------------------------------------------------------------------
// Native host bridge
// ---------------------------------------------------------------------------
//
// This used to be a single chrome.runtime.sendNativeMessage call per request, which
// launches a brand-new AWS-Login-Helper-Host-Chrome.exe process every single time. On a machine
// running cloud-delivered antivirus protection, EVERY one of those fresh launches got
// scanned before being allowed to run - several extra seconds, on every single click, with
// no way around it from inside the host's own code (the scan happens before the process
// even starts). Switching to chrome.runtime.connectNative keeps ONE long-lived connection
// (and therefore one already-launched, already-scanned host process) open and reuses it for
// every request made while this service worker is alive, instead of paying that cost per
// click. See Program.cs's RunNativeMessagingLoop for the host side of this.
//
// Multiple requests can now be in flight on the same connection at once (e.g. the user
// clicks two different account/role links close together), so each request gets a small
// correlation id that the host echoes back, used to match a response to the right pending
// Promise.

let nativePort = null;
let nextRequestId = 1;
const pendingRequests = new Map(); // requestId -> { resolve, reject }

function rejectAllPending(reason) {
  for (const { reject } of pendingRequests.values()) {
    reject(reason instanceof Error ? reason : new Error(String(reason)));
  }
  pendingRequests.clear();
}

function getNativePort() {
  if (nativePort) return nativePort;

  nativePort = chrome.runtime.connectNative(NATIVE_HOST);
  ensureKeepAlive();

  nativePort.onMessage.addListener((response) => {
    const requestId = response && response.__requestId;
    if (requestId == null) {
      log('Native host message with no __requestId (ignored):', response);
      return;
    }
    const pending = pendingRequests.get(requestId);
    if (!pending) return; // already resolved/rejected (e.g. after a disconnect), or unknown
    pendingRequests.delete(requestId);
    const { __requestId, ...rest } = response;
    pending.resolve(rest);
  });

  nativePort.onDisconnect.addListener(() => {
    const err = chrome.runtime.lastError;
    log('Native host port disconnected:', err && err.message);
    nativePort = null;
    // Anything still waiting on this connection never will get an answer now - fail it
    // instead of hanging forever. The NEXT sendNative call transparently reconnects.
    rejectAllPending(new Error((err && err.message) || 'Native host disconnected'));
  });

  return nativePort;
}

// Sends one message to the native host over the shared long-lived connection and resolves
// with its response. Reconnects transparently if the connection had dropped (e.g. the host
// process exited, or this is the very first call).
function sendNative(message) {
  return new Promise((resolve, reject) => {
    const requestId = nextRequestId++;
    pendingRequests.set(requestId, { resolve, reject });
    try {
      // Rides along on every request rather than a separate "set debug mode" message to the
      // host - see Logger.cs's own doc comment on the host side for how this is used there
      // (Program.HandleOneRequest reads it per request, so a checkbox flip takes effect on
      // the very next call, no reconnect needed).
      getNativePort().postMessage({ ...message, __requestId: requestId, enableDebug: IsDebug });
    } catch (err) {
      pendingRequests.delete(requestId);
      console.error('Native host error:', err.message);
      reject(err);
    }
  });
}

// MV3 service workers get torn down after ~30s idle, which would silently drop the native
// messaging connection along with it. Worse, Chrome enforces this deliberately even against
// keepalive tricks - a service worker that's ONLY staying alive because of a repeating
// alarm can still get recycled by Chrome after a while, on purpose, specifically to stop
// extensions from pinning themselves in memory forever. So this can't actually guarantee
// one host process stays open, untouched, for a full 15 minutes.
//
// What it CAN guarantee: chrome.alarms are scheduled by Chrome itself, independent of the
// service worker's own lifetime - a repeating alarm keeps firing (waking the service worker
// back up as needed) even across any number of service-worker recycles in between, for as
// long as the alarm itself stays scheduled. So instead of treating "the port I remember is
// gone" as a sign to give up, every tick actively reconnects (a real ping, which forces the
// host to actually launch if it isn't already running) rather than just checking whether a
// connection happens to still exist. The practical effect the user actually cares about -
// never more than about 24 seconds of "cold" staleness before a real click - holds either
// way: on the (likely common) ticks where the service worker/port survived, this is a
// cheap no-op reusing the existing connection; on the ticks where it didn't, this quietly
// relaunches (and re-scans) the host in the background well before any real click needs it,
// rather than leaving that cost to be paid at the moment the user actually clicks something.
const KEEPALIVE_ALARM = 'native-port-keepalive';

chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name !== KEEPALIVE_ALARM) return;
  sendNative({ action: 'ping' })
    .then(() => log('Keepalive ping ok'))
    .catch((err) => log('Keepalive ping failed (will retry on next tick):', err));
});

function ensureKeepAlive() {
  chrome.alarms.get(KEEPALIVE_ALARM, (existing) => {
    if (existing) return;
    chrome.alarms.create(KEEPALIVE_ALARM, { periodInMinutes: 0.4 }); // ~24s, under the 30s idle cutoff
  });
}

// Builds a filesystem/URL-safe key that identifies one AWS account+role pairing.
// This is what the native host uses as the Chrome --profile-directory name in its
// dedicated instance, so it must stay stable across sessions.
function makeProfileKey(accountId, accountName, roleName) {
  const raw = `${accountId}_${accountName}_${roleName}`;
  return raw.replace(/[^a-zA-Z0-9_-]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 100);
}

async function getAwsAppsCookies() {
  // Mirrors the Firefox extension: only the .awsapps.com (IIC/SSO) cookies need to travel
  // with the user into the isolated profile. AWS re-issues console.aws.amazon.com session
  // cookies itself once the SSO redirect succeeds there.
  const cookies = await chrome.cookies.getAll({ domain: 'awsapps.com' });
  return cookies.filter((c) => c.domain.includes('.awsapps.com') || c.domain.includes('awsapps.com'));
}

function serializeCookiesForHost(cookies) {
  return cookies.map((c) => ({
    domain: c.domain,
    name: c.name,
    value: c.value,
    path: c.path,
    secure: c.secure,
    httpOnly: c.httpOnly,
    sameSite: c.sameSite,
    session: c.session,
    expirationDate: c.expirationDate || null,
  }));
}

async function addRecentLoginEntryToStorage(link, account_name, account_id, role_name, openIn) {
  const href = link.split('&destination')[0];
  const { recentfivelogins: storedEntries } = await chrome.storage.local.get('recentfivelogins');
  let recentEntries = storedEntries || [];

  const existingIndex = recentEntries.findIndex((e) => e.href === href);
  const timestamp = new Date().toISOString().slice(0, 16).replace('T', ' ');

  if (existingIndex !== -1) {
    const existing = recentEntries[existingIndex];
    existing.account_name = account_name;
    existing.account_id = account_id;
    existing.role_name = role_name;
    existing.openIn = openIn;
    existing.timestamp = timestamp;
    recentEntries.splice(existingIndex, 1);
    recentEntries.unshift(existing);
  } else {
    recentEntries.unshift({ href, account_name, account_id, role_name, openIn, timestamp });
  }

  recentEntries = recentEntries.slice(0, MAX_RECENT_ENTRIES);
  await chrome.storage.local.set({ recentfivelogins: recentEntries });
}

async function addRecentServiceLinkToStorage(accountId, serviceName, url) {
  let { recentServicesByAccount } = await chrome.storage.local.get('recentServicesByAccount');
  recentServicesByAccount = recentServicesByAccount || {};

  let forAccount = recentServicesByAccount[accountId] || [];
  forAccount = forAccount.filter((e) => e.serviceName !== serviceName);
  forAccount.unshift({ serviceName, url });
  forAccount = forAccount.slice(0, 5);

  recentServicesByAccount[accountId] = forAccount;
  await chrome.storage.local.set({ recentServicesByAccount });
}

function extractServiceName(url) {
  const path = new URL(url).pathname;
  return path.split('/')[1];
}

function isAWSConsoleURL(url) {
  return /\.console\.aws\.amazon\.com\//i.test(url);
}

// ---------------------------------------------------------------------------
// Message handling from content.js
// ---------------------------------------------------------------------------

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  log('Message received:', message.action);

  (async () => {
    try {
      switch (message.action) {
        case 'prewarmNativeHost': {
          // Fire-and-forget: just gets the connectNative connection (and therefore the host
          // process, and any AV scan that comes with launching it) established ahead of
          // time. A real 'ping' round trip, not just opening the port, since connectNative
          // may not actually spawn the process until the first message is sent on some
          // Chrome versions - better to force that now than assume it already happened.
          sendNative({ action: 'ping' })
            .then((res) => log('Prewarm ping ok:', res))
            .catch((err) => log('Prewarm ping failed (non-fatal):', err));

          // Also warm the dedicated Chrome instance itself, not just the native host process.
          // Without this, the FIRST real account/role click of a session still pays for
          // launching a whole new Chrome process from cold (LaunchChrome + WaitUntilAliveAsync,
          // plus the GetRunningCdpPortAsync -> null check that only shows up on a cold start) -
          // the host-process prewarm above never touched Chrome at all. The host opens a
          // hidden, off-screen placeholder window under its own throwaway profile to force
          // this, so by the time the user actually clicks something, the instance is already
          // alive and that click can take the fast "reuse the running instance" path instead.
          sendNative({ action: 'prewarmChrome' })
            .then((res) => log('Prewarm Chrome instance ok:', res))
            .catch((err) => log('Prewarm Chrome instance failed (non-fatal):', err));

          sendResponse({ ok: true });
          break;
        }

        case 'openAccountRole': {
          const { link, account_id, account_name, role_name, openIn } = message;
          const profileKey = makeProfileKey(account_id, account_name, role_name);
          const displayLabel = `${account_id} | ${account_name} | ${role_name}`;

          const cookies = serializeCookiesForHost(await getAwsAppsCookies());
          log('Sending syncAndOpen for', profileKey, 'with', cookies.length, 'cookies');

          // displayLabel (account_id | account_name | role_name) is the full banner label;
          // accountName alone prefixes the tab/window title. Neither is shown by THIS
          // extension's own content-script logic - the dedicated per-role Chrome/Edge
          // instance has no extension loaded into it at all (--load-extension was removed
          // from Chromium entirely starting Chrome 137, June 2025). Both are shown by a
          // script the native host injects directly via the Chrome DevTools Protocol - see
          // CdpClient.SetCookiesAndNavigateAsync and CdpClient.StartBannerWatcher for the
          // full mechanism (the watcher is what keeps it showing up on LATER navigations
          // within that same window too, not just this first one).
          const response = await sendNative({
            action: 'syncAndOpen',
            profileKey,
            displayLabel,
            accountName: account_name,
            url: link,
            openIn: openIn || 'Window',
            cookies,
          });

          if (!response || response.ok !== true) {
            console.error('Native host reported failure:', response && response.error);
          }

          await addRecentLoginEntryToStorage(link, account_name, account_id, role_name, openIn);
          sendResponse({ ok: true });
          break;
        }

        case 'copyCookiesToAllProfiles': {
          const cookies = serializeCookiesForHost(await getAwsAppsCookies());
          const response = await sendNative({ action: 'syncAllProfiles', cookies });
          sendResponse({ ok: !!(response && response.ok) });
          break;
        }

        case 'saveConsoleServiceLink': {
          if (isAWSConsoleURL(message.url) && message.account_id) {
            const serviceName = extractServiceName(message.url);
            await addRecentServiceLinkToStorage(message.account_id, serviceName, message.url);
          }
          sendResponse({ ok: true });
          break;
        }

        case 'checkNativeHost': {
          const response = await sendNative({ action: 'ping' });
          sendResponse(response || { ok: false });
          break;
        }

        case 'listProfiles': {
          const response = await sendNative({ action: 'listProfiles' });
          sendResponse(response || { ok: false, profiles: [] });
          break;
        }

        case 'getPortalSessionExpiry': {
          // Ported from the Firefox extension: the IIC/SSO portal's own session cookie is
          // named 'x-amz-sso_authn', scoped to a path containing '/start/' on .awsapps.com.
          // Its own expirationDate IS the portal session's expiry - no separate tracking
          // needed. content.js (running directly on the portal page, in the user's own
          // regular profile - no native host or CDP involved for this part at all) polls
          // this on an interval to render "session expires in Xh Ym" next to AWS's own
          // portal text. If AWS has since renamed this cookie, this just quietly finds
          // nothing and content.js shows nothing - nothing else breaks.
          const cookies = await getAwsAppsCookies();
          const cookie = cookies.find((c) => c.name === 'x-amz-sso_authn' && c.path && c.path.includes('/start/'));
          sendResponse({ ok: true, expirationDate: (cookie && cookie.expirationDate) || null });
          break;
        }

        case 'deleteProfile': {
          // New capability, not ported from Firefox - see ChromeManager.DeleteProfileAsync's
          // own doc comment for why there's no Firefox equivalent to defer to here. Just a
          // thin pass-through: the native host does the actual directory/registry cleanup and
          // reports back either ok, or a message the options page can show directly (e.g.
          // "close the open window first").
          const response = await sendNative({ action: 'deleteProfile', profileKey: message.profileKey });
          sendResponse(response || { ok: false, error: 'Native host unreachable' });
          break;
        }

        default:
          sendResponse({ ok: false, error: 'unknown action' });
      }
    } catch (err) {
      console.error('Error handling message', message.action, err);
      sendResponse({ ok: false, error: String(err) });
    }
  })();

  // We respond asynchronously above.
  return true;
});
