// Content script - runs on the AWS IIC/SSO portal (*.awsapps.com) and on the
// AWS Management Console (*.console.aws.amazon.com).
//
// Ported from the Firefox "AWS Login Helper" extension. The container-specific pieces
// (contextualIdentities lookups, per-tab cookieStoreId banners) are removed because Chrome
// has no equivalent API; those responsibilities now live in the native messaging host
// (see native-host/). Everything else - finding the per-role links on the IIC portal page,
// adding "open in isolated window/tab" actions next to them, and the recent-logins /
// recent-services list - works the same way it did on Firefox.

let invocationCounter = 0;
let addingTabAndWindowLinks = false;
const linksInfo = new Set();

const AWS_CONSOLE_URL = 'console.aws.amazon.com/';
const AWS_APPS_START_URL = '.awsapps.com/start/#/';
const AWS_CONSOLE_DEFAULT_REGION = 'us-east-2';
const COMPANION_APP_DOWNLOAD_URL = 'https://github.com/penchala-services-inc/aws-login-helper-chrome/releases/latest';

// Real, published Chrome Web Store id (lbnopneedeeomcfebahnkankbedjjlfk) - the /reviews
// suffix takes the user straight to "write a review" instead of the listing's main tab.
const EXTENSION_REVIEW_URL = 'https://chromewebstore.google.com/detail/lbnopneedeeomcfebahnkankbedjjlfk/reviews';

let accountInfo = { accountAlias: null, accountId: null };

// Populated (once, asynchronously) from chrome.storage.local - written by store.js right
// before the AWS federation redirect chain starts for an isolated account/role window opened
// through the native host (see native-host/CdpClient.cs and extension/store.js). This is
// PROFILE-scoped storage: this extension is also loaded, unpacked, into the native host's
// separate dedicated Chrome/Edge instance (one profile per AWS role), so each profile's own
// copy of this value only ever reflects that one role. On a normal console page visited
// outside that flow (e.g. the user's everyday Chrome), this stays null, which is exactly what
// makes extractAccountInfoAndUpdateTitle()'s AWS-session-data fallback below still apply
// there - nothing changes for ordinary console browsing.
let consoleLabelInfo = null;

function getDebugStatus() {
  return chrome.storage.local.get('enable-debug').then((result) => !!result['enable-debug']);
}

let IsDebug = false;
getDebugStatus().then((v) => { IsDebug = v; });

// Without this, toggling the options page's "Enable debug logging" checkbox only took effect
// on the next full page load of whatever AWS tab this content script is running in - not
// ideal when someone's mid-session trying to capture a specific problem. Reacting to the
// storage write directly makes it immediate.
chrome.storage.onChanged.addListener((changes, area) => {
  if (area === 'local' && 'enable-debug' in changes) {
    IsDebug = !!changes['enable-debug'].newValue;
  }
});

function log(...args) {
  if (IsDebug) console.log('[AWSLoginHelper]', ...args);
}

// Open the native messaging connection now, on ANY *.awsapps.com page - not just once the
// user has reached the specific account-list URL below, since they may sit on a redirect,
// SAML, or MFA page first. This gets the OS-level cost of launching (and, on a machine with
// cloud-delivered antivirus, getting scanned before being allowed to run)
// AWS-Login-Helper-Host-Chrome.exe out of the way while they're still working through sign-in, not at
// the moment they click an account/role link and are staring at the screen waiting for a
// window to appear. Unlike the earlier attempt at pre-warming (which tried to pre-launch a
// whole separate Chrome window and got reverted because that window wasn't actually
// hidden), this only opens a connectNative connection to our own headless native host - it
// has no window of its own at all, so there's nothing that can leak into view. The
// connection then stays open (background.js's keepalive alarm) right up until the user
// actually clicks a real link, so that click reuses this same already-launched,
// already-scanned process instead of paying for a new one.
if (window.location.hostname === 'awsapps.com' || window.location.hostname.endsWith('.awsapps.com')) {
  chrome.runtime.sendMessage({ action: 'prewarmNativeHost' });
}

const serviceMapping = {
  bedrock: 'bedrock',
  console: 'console-home',
  states: 'step-functions',
  redshiftv2: 'redshift-home',
  sqlworkbench: 'redshift-query-editor',
};

document.addEventListener('DOMContentLoaded', () => {
  if (window.location.href.includes(AWS_CONSOLE_URL)) {
    extractAccountInfoAndUpdateTitle();
    applyConsoleBanner();
  }
});

window.addEventListener('load', () => {
  if (window.location.href.includes(AWS_CONSOLE_URL)) {
    extractAccountInfoAndUpdateTitle();
    applyConsoleBanner();
  }
  if (window.location.href.includes(AWS_APPS_START_URL)) {
    // Session-expiry rendering itself is wired up from the main portal-activation block
    // near the bottom of this file (it needs to run only once per page load, to avoid
    // registering more than one refresh interval) - nothing additional needed here.
  }
});

if (document.readyState === 'complete') {
  log('Document already fully loaded');
} else {
  document.addEventListener('readystatechange', () => {
    if (document.readyState === 'complete' && window.location.href.includes(AWS_CONSOLE_URL)) {
      extractAccountInfoAndUpdateTitle();
      applyConsoleBanner();
    }
  });
}

// ---------------------------------------------------------------------------
// Recent logins / recent services list on the IIC portal page
// ---------------------------------------------------------------------------

async function createLinksFromStorageEntries() {
  try {
    const { recentfivelogins: recentEntries = [] } = await chrome.storage.local.get('recentfivelogins');
    const { recentServicesByAccount = {} } = await chrome.storage.local.get('recentServicesByAccount');

    if (recentEntries.length === 0) return;

    let parentContainer = document.querySelector('.recent-logins-container');
    if (parentContainer) return; // already rendered

    parentContainer = document.createElement('div');
    parentContainer.className = 'recent-logins-container';
    document.body.prepend(parentContainer);
    parentContainer.style.display = 'flex';
    parentContainer.style.flexDirection = 'column';
    parentContainer.style.alignItems = 'center';
    parentContainer.style.paddingTop = '10px';

    const heading = document.createElement('div');
    heading.textContent = 'Recent Logins & Services:';
    heading.style.fontWeight = 'bold';
    heading.style.fontSize = '16px';
    heading.style.fontFamily = 'Amazon Ember';
    parentContainer.appendChild(heading);

    const separator = document.createElement('hr');
    separator.style.marginTop = '5px';
    separator.style.marginBottom = '10px';
    separator.style.width = '100%';
    separator.style.border = 'none';
    separator.style.borderTop = '1px solid black';
    parentContainer.appendChild(separator);

    const listEl = document.createElement('div');
    listEl.className = 'recent-links';
    parentContainer.appendChild(listEl);

    recentEntries.forEach((entry) => {
      const { href, account_name, account_id, role_name, openIn } = entry;

      const label = document.createElement('span');
      label.style.fontSize = '14px';
      label.style.marginBottom = '10px';
      label.style.padding = '10px';
      label.style.fontFamily = 'Amazon Ember';
      label.textContent = `${account_id} | ${account_name} | ${role_name} - Isolated (${openIn}): `;
      listEl.appendChild(label);

      // The label itself was plain text with no way to actually reopen that account/role -
      // only the per-service shortcuts below it (and only for accounts that had any) were
      // clickable. This link reopens the same account/role in its own isolated profile,
      // same as clicking it from the main account list would.
      const openLink = document.createElement('a');
      openLink.rel = 'noopener noreferrer';
      openLink.href = href;
      openLink.title = `Open ${account_name} | ${role_name} in its isolated ${openIn}`;
      openLink.textContent = 'Console';
      openLink.style.fontSize = '14px';
      openLink.style.marginRight = '10px';
      openLink.style.fontFamily = 'Amazon Ember';
      openLink.addEventListener('click', (event) => {
        event.preventDefault();
        chrome.runtime.sendMessage({
          action: 'openAccountRole',
          link: href,
          openIn,
          role_name,
          account_id,
          account_name,
        });
      });
      listEl.appendChild(openLink);

      const recentServices = recentServicesByAccount[account_id];
      if (Array.isArray(recentServices)) {
        recentServices.forEach((svc) => {
          const serviceLink = document.createElement('a');
          serviceLink.rel = 'noopener noreferrer';
          serviceLink.href = new URL(href, window.location.href.split('#')[0]).toString() + '&destination=' + svc.url;
          serviceLink.title = serviceMapping[svc.serviceName] || svc.serviceName;
          serviceLink.textContent = serviceLink.title;
          serviceLink.style.fontSize = '14px';
          serviceLink.style.marginBottom = '10px';
          serviceLink.style.padding = '10px';
          serviceLink.style.fontFamily = 'Amazon Ember';

          serviceLink.addEventListener('click', (event) => {
            event.preventDefault();
            chrome.runtime.sendMessage({
              action: 'openAccountRole',
              link: serviceLink.href,
              openIn,
              role_name,
              account_id,
              account_name,
            });
          });

          listEl.appendChild(serviceLink);
        });
      }

      listEl.appendChild(document.createElement('br'));
      listEl.appendChild(document.createElement('br'));
    });

    parentContainer.appendChild(document.createElement('br'));
    parentContainer.appendChild(document.createElement('br'));

    const targetDiv =
      document.getElementById('awsui-tabs-43-1710532311807-680-accounts-panel') ||
      document.getElementById('awsui_tabs-tab-label_14rmt_1ojt0_257');
    if (targetDiv) {
      targetDiv.appendChild(parentContainer);
    } else {
      console.warn('AWS Login Helper: target div not found for recent logins list.');
    }
  } catch (error) {
    console.error('Error creating links from recent entries:', error);
  }
}

// ---------------------------------------------------------------------------
// Per-role "open isolated" links on the IIC portal account list
// ---------------------------------------------------------------------------

// AWS renders the account list as a flat table: one <tr aria-level="1"> per account,
// followed by one <tr aria-level="2"> per role under it - NOT nested inside each other.
// So finding "the account this role belongs to" means walking backwards through preceding
// <tr> siblings until we hit the nearest aria-level="1" row, then reading its
// [data-testid="account-list-cell"] cell - not walking up ancestors looking for a sibling
// button, which is what this used to do back when AWS's markup put a <button> there
// directly. AWS now renders that cell as a <div role="button">, and previous marketing/UI
// refreshes have already changed this once - so this walk-backwards-by-aria-level approach
// is deliberately based on the account/role table's semantic structure (aria-level,
// data-testid) rather than exact tag names or hashed classes, to hold up better next time.
function extractAccountNameFromRoleElement(element) {
  const roleRow = element.closest('tr[aria-level="2"]') || element.closest('tr');
  if (!roleRow) return null;

  let row = roleRow.previousElementSibling;
  while (row) {
    if (row.tagName === 'TR' && row.getAttribute('aria-level') === '1') {
      const cell = row.querySelector('[data-testid="account-list-cell"]');
      return cell ? cell.textContent.trim() : null;
    }
    row = row.previousElementSibling;
  }
  return null;
}

function extractAccountIdAndRoleName(href) {
  const params = new URLSearchParams(href.split('?')[1]);
  return { account_id: params.get('account_id'), role_name: params.get('role_name') };
}

// Tracks DOM ELEMENTS we've already added isolated-open links next to, not just href strings.
// AWS's account list unmounts/remounts fresh <a> elements (with the same href) whenever a
// row group is collapsed and re-expanded; keying off href alone (as the original Firefox
// extension did) meant those re-rendered elements were seen as "already processed" and
// silently never got their links back. A WeakSet keyed by element fixes that, and also lets
// removed elements get garbage collected instead of leaking memory over a long session.
const processedElements = new WeakSet();

function extractLoginLinksAndAddTabAndWindowUrls() {
  addingTabAndWindowLinks = true;
  const elements = document.querySelectorAll('a[href^="#/console?account_id="][href*="&role_name="]');

  for (const element of elements) {
    if (processedElements.has(element)) continue;

    const href = element.getAttribute('href');
    if (!href) continue;
    processedElements.add(element);

    const { account_id, role_name } = extractAccountIdAndRoleName(href);
    if (!account_id || !role_name) {
      console.error('AWS Login Helper: could not extract account_id/role_name from', href);
      continue;
    }

    const account_name = extractAccountNameFromRoleElement(element);
    if (!account_name) {
      console.error('AWS Login Helper: could not extract account name from DOM near', href);
      continue;
    }

    linksInfo.add({ href, account_id, account_name, role_name });

    const uniqueClassWindow = `link-${account_id}-${role_name}-${account_name}-Window`;
    if (document.getElementsByClassName(uniqueClassWindow).length > 0) continue;

    chrome.storage.local.get('default-region').then((result) => {
      const defaultRegion = result['default-region'] || AWS_CONSOLE_DEFAULT_REGION;
      let account_role_url = new URL(href, window.location.href.split('#')[0]).toString();

      if (defaultRegion !== AWS_CONSOLE_DEFAULT_REGION) {
        const consoleUrl = `https://${defaultRegion}.console.aws.amazon.com/console/home?region=${defaultRegion}#`;
        const urlObj = new URL(account_role_url);
        if (!urlObj.searchParams.has('destination')) {
          account_role_url = `${account_role_url}&destination=${consoleUrl}`;
        }
      }

      const makeLink = (className, title, text, openIn) => {
        const a = document.createElement('a');
        a.className = className;
        a.rel = 'noopener noreferrer';
        a.href = account_role_url;
        a.title = title;
        a.textContent = text;
        a.addEventListener('click', (event) => {
          event.preventDefault();
          chrome.runtime.sendMessage({
            action: 'openAccountRole',
            link: account_role_url,
            openIn,
            role_name,
            account_id,
            account_name,
          });
        });
        return a;
      };

      const newLinkWindow = makeLink(
        uniqueClassWindow,
        `${role_name}-Isolated (Window)`,
        `| ${role_name}-Isolated (Window)`,
        'Window'
      );
      element.insertAdjacentElement('afterend', newLinkWindow);
    });
  }

  log('Extracted links information:', linksInfo);
  addingTabAndWindowLinks = false;
}

// NOTE: this used to only re-scan the page when a mutation touched an element carrying one
// of a few specific, auto-generated CSS class names (e.g. "ggXus_2CiDezyLYZFJdc") copied from
// a one-time inspection of the AWS IIC portal's DOM. Those hashed class names are an
// implementation detail of AWS's frontend build and can (and did) change on a later AWS
// deploy, silently breaking extraction entirely - nothing else on the page needed to be
// wrong. Debouncing a rescan on ANY DOM mutation instead is slightly more wasteful but not
// dependent on AWS's internal build output; extractLoginLinksAndAddTabAndWindowUrls() is
// already idempotent (skips elements it's already handled), so calling it too often is safe.
let rescanTimer = null;
function scheduleRescan() {
  if (rescanTimer) return;
  rescanTimer = setTimeout(() => {
    rescanTimer = null;
    if (!addingTabAndWindowLinks) extractLoginLinksAndAddTabAndWindowUrls();
  }, 250);
}

// ---------------------------------------------------------------------------
// Portal page: session expiry
// ---------------------------------------------------------------------------
//
// Ported from the Firefox extension's AppendSessionExpiryTime. That version tracked expiry
// for both the portal AND the console page from the same content script, because Firefox
// containers let one script see cookies across containers. Here the console-page half isn't
// possible from a content script at all - the console page opens in a separate, extension-
// less dedicated profile (see native-host/CdpClient.cs for that half, done via CDP instead).
// This half only needs the portal's own cookie, readable by chrome.cookies from the
// background service worker (content scripts can't call chrome.cookies directly), so it's a
// plain message round-trip to background.js's 'getPortalSessionExpiry' handler.

function formatDurationHM(totalSeconds) {
  const totalMinutes = Math.floor(totalSeconds / 60);
  const hours = Math.floor(totalMinutes / 60);
  const minutes = totalMinutes % 60;
  return `${hours} hour${hours !== 1 ? 's' : ''} ${minutes} min${minutes !== 1 ? 's' : ''}`;
}

let portalExpiryIntervalStarted = false;
let portalExpiryFastRetryTimer = null;

function renderPortalSessionExpiry() {
  chrome.runtime.sendMessage({ action: 'getPortalSessionExpiry' }, (response) => {
    const expirationDate = response && response.expirationDate;
    if (!expirationDate) return;

    const nowSeconds = Date.now() / 1000;
    if (expirationDate <= nowSeconds) return;

    // Same anchor the Firefox extension used: the portal page's own "AWS access portal"
    // text. If AWS has since changed this markup, this just quietly finds nothing and
    // renders nothing - the rest of the page is unaffected.
    const spanElement = Array.from(document.getElementsByTagName('span'))
      .find((span) => span.textContent.includes('AWS access portal'));
    if (!spanElement) return; // not rendered yet - the fast retry loop below tries again shortly

    // Found it - the fast retry loop (if still running) has done its job, stop polling.
    if (portalExpiryFastRetryTimer) {
      clearInterval(portalExpiryFastRetryTimer);
      portalExpiryFastRetryTimer = null;
    }

    let expiryText = spanElement.querySelector('.awslh-session-expiry');
    if (!expiryText) {
      expiryText = document.createElement('span');
      expiryText.className = 'awslh-session-expiry';
      spanElement.appendChild(expiryText);
    }
    expiryText.textContent = ' (session expires in ' + formatDurationHM(expirationDate - nowSeconds) + ')';

    // Placed right next to the expiry text (not somewhere the user has to go looking for it)
    // since this is the one bit of the portal page we've already added something to - anyone
    // reading the expiry timer has already noticed the extension is there. Added once; never
    // re-created on the interval tick above.
    if (!spanElement.querySelector('.awslh-rate-link')) {
      const rateLink = document.createElement('a');
      rateLink.className = 'awslh-rate-link';
      rateLink.href = EXTENSION_REVIEW_URL;
      rateLink.target = '_blank';
      rateLink.rel = 'noopener noreferrer';
      rateLink.textContent = 'Rate AWS Login Helper Extension';
      rateLink.style.cssText = 'margin-left:10px;color:#0972d3;text-decoration:underline;';
      // Match whatever size AWS is actually rendering "AWS access portal" at, rather than a
      // guessed fixed px value - reads it live off spanElement so this stays correct even if
      // AWS changes its own portal styling later.
      rateLink.style.fontSize = window.getComputedStyle(spanElement).fontSize;
      spanElement.appendChild(rateLink);
    }
  });

  // Guarded so re-entering this function (it's called from more than one place below) never
  // registers a second interval - each content-script injection only wants exactly one.
  if (!portalExpiryIntervalStarted) {
    portalExpiryIntervalStarted = true;
    setInterval(renderPortalSessionExpiry, 60000);

    // The portal's "AWS access portal" anchor span is rendered by AWS's own client-side SPA,
    // which can still be mid-render when this content script's own immediate call (below,
    // where this function is first invoked on page load) runs - that call can easily land
    // before the span exists yet, silently finding nothing (see the early return above) with
    // nothing to retry it until the slow 60s interval eventually catches up. That's exactly
    // why the expiry text was taking up to a full minute to show up. This fast retry (every
    // second, for up to 15s) catches it as soon as the SPA actually finishes rendering -
    // typically within a second or two - instead of making the user wait for the 60s
    // interval. Stops itself either once it succeeds (see the early-clear above) or after 15
    // attempts, at which point the normal 60s interval is still there as a fallback.
    let fastRetryCount = 0;
    portalExpiryFastRetryTimer = setInterval(() => {
      fastRetryCount += 1;
      if (fastRetryCount > 15) {
        clearInterval(portalExpiryFastRetryTimer);
        portalExpiryFastRetryTimer = null;
        return;
      }
      renderPortalSessionExpiry();
    }, 1000);
  }
}

// ---------------------------------------------------------------------------
// Portal page: companion app (native messaging host) installed check
// ---------------------------------------------------------------------------
//
// Clicking an account/role link does nothing useful without the companion app installed
// (background.js's 'openAccountRole' just fails silently into the console, since there's no
// good place to surface an error from a fire-and-forget native-messaging call triggered by a
// click). Checking once, right when the user lands on the account list, catches the missing-
// install case up front instead of leaving them to click a link and wonder why nothing
// happened. Reuses the exact same 'checkNativeHost' round trip the options page's "Check
// companion app" button already uses (see background.js) - a real ping over
// chrome.runtime.connectNative, which resolves ok:false (not installed/registered) or
// rejects (host process failed to start) either way if the companion app isn't there.
let companionAppDialogShown = false;

function showCompanionAppMissingDialog() {
  if (companionAppDialogShown || document.getElementById('awslh-companion-app-dialog')) return;
  companionAppDialogShown = true;

  const overlay = document.createElement('div');
  overlay.id = 'awslh-companion-app-dialog';
  overlay.style.cssText = 'position:fixed;inset:0;z-index:2147483647;background:rgba(0,0,0,0.5);' +
    'display:flex;align-items:center;justify-content:center;font-family:Arial,sans-serif;';

  const box = document.createElement('div');
  box.style.cssText = 'background:#fff;color:#232f3e;max-width:380px;padding:20px 24px;' +
    'border-radius:6px;box-shadow:0 4px 24px rgba(0,0,0,0.35);text-align:left;';

  const title = document.createElement('div');
  title.textContent = 'AWS Login Helper companion app not found';
  title.style.cssText = 'font-size:16px;font-weight:bold;margin-bottom:10px;';
  box.appendChild(title);

  const message = document.createElement('div');
  message.textContent =
    "Opening an account/role in its own isolated window needs a small companion app " +
    "installed on this PC, and it doesn't look like it's running right now. Install it, " +
    'then reload this page.';
  message.style.cssText = 'font-size:13px;margin-bottom:16px;line-height:1.4;';
  box.appendChild(message);

  const actions = document.createElement('div');
  actions.style.cssText = 'display:flex;justify-content:flex-end;gap:10px;';

  const dismissButton = document.createElement('button');
  dismissButton.textContent = 'Dismiss';
  dismissButton.style.cssText =
    'padding:6px 12px;cursor:pointer;background:#fff;color:#232f3e;border:1px solid #ccc;border-radius:4px;';
  dismissButton.addEventListener('click', () => overlay.remove());
  actions.appendChild(dismissButton);

  const downloadLink = document.createElement('a');
  downloadLink.href = COMPANION_APP_DOWNLOAD_URL;
  downloadLink.target = '_blank';
  downloadLink.rel = 'noopener noreferrer';
  downloadLink.textContent = 'Download companion app';
  downloadLink.style.cssText =
    'padding:6px 12px;cursor:pointer;background:#ec7211;color:#fff;border-radius:4px;' +
    'text-decoration:none;font-weight:bold;font-size:13px;';
  actions.appendChild(downloadLink);

  box.appendChild(actions);
  overlay.appendChild(box);
  document.body.appendChild(overlay);
}

function checkCompanionAppInstalled() {
  chrome.runtime.sendMessage({ action: 'checkNativeHost' }, (response) => {
    if (chrome.runtime.lastError || !response || response.ok !== true) {
      log('Companion app check failed:', chrome.runtime.lastError || (response && response.error));
      showCompanionAppMissingDialog();
    }
  });
}

if (
  window.location.href.includes(AWS_APPS_START_URL) &&
  !window.location.href.includes('.awsapps.com/start/#/saml/custom/') &&
  !window.location.href.includes('.awsapps.com/start/#/console')
) {
  document.title = 'AWS IIC SSO';

  const observer = new MutationObserver(scheduleRescan);
  observer.observe(document, { childList: true, subtree: true });

  // Run once immediately too, in case the account list has already rendered by the time
  // this content script executes (run_at: document_idle can fire after that).
  extractLoginLinksAndAddTabAndWindowUrls();

  createLinksFromStorageEntries();

  renderPortalSessionExpiry();

  checkCompanionAppInstalled();

  // NOTE: this used to also fire a background 'copyCookiesToAllProfiles' resync here on
  // every portal page load, mirroring the Firefox extension's copyCookiesToAllContainers.
  // Removed: it loops through every isolated profile ever created and briefly opens a
  // window/tab in each one just to refresh its cookie, which - once more than a couple of
  // profiles exist - looks like a burst of blank Chrome windows opening and closing on every
  // reload. Cookies already get freshly synced at the moment a profile is actually opened
  // (see 'openAccountRole' below), so this wasn't load-bearing, just disruptive. Revisit as
  // an opt-in "sync all" button in the popup instead, if the background refresh turns out to
  // be worth the disruption for someone.
}

// ---------------------------------------------------------------------------
// Console page: title + recent service link tracking
// ---------------------------------------------------------------------------

function isAWSConsoleURL(url) {
  return /\.console\.aws\.amazon\.com\//i.test(url);
}

if (isAWSConsoleURL(window.location.href) && !window.location.href.includes('.awsapps.com/start/#/console')) {
  extractAccountInfoAndUpdateTitle();
  chrome.runtime.sendMessage({
    action: 'saveConsoleServiceLink',
    url: window.location.href,
    account_id: accountInfo.accountId,
  });

  // Async - see consoleLabelInfo's own comment above. Kicked off once per page load; every
  // retry point above (DOMContentLoaded/load/readystatechange) calls applyConsoleBanner()
  // too, so whichever of those fires AFTER this resolves is what actually shows it, same
  // "fire from several points, whichever lands" approach extractAccountInfoAndUpdateTitle
  // already used for the plain title case.
  chrome.storage.local.get('awslh_console_label').then((result) => {
    consoleLabelInfo = result && result['awslh_console_label'];
    if (!consoleLabelInfo) return;
    applyConsoleBanner();
    observeConsoleBannerPersistence();
  });
}

function extractAccountInfoAndUpdateTitle() {
  // Our own label (account_id | account_name | role_name, handed off via store.js right
  // before the AWS federation redirect chain starts for an isolated role window - see
  // consoleLabelInfo's comment above) is more complete than AWS's own accountAlias and wins
  // when present. Bailing out here instead of also applying the accountAlias prefix avoids
  // stacking both onto document.title.
  if (consoleLabelInfo) return;

  if (accountInfo && accountInfo.accountAlias) {
    if (!document.title.startsWith(accountInfo.accountAlias)) {
      document.title = accountInfo.accountAlias + ' - ' + document.title;
    }
    return;
  }

  const metaTag = document.querySelector('meta[name="awsc-session-data"]');
  if (!metaTag) return;

  try {
    const sessionData = JSON.parse(metaTag.getAttribute('content').replace(/&quot;/g, '"'));
    accountInfo.accountAlias = sessionData.accountAlias;
    accountInfo.accountId = sessionData.accountId;

    if (!document.title.startsWith(accountInfo.accountAlias)) {
      document.title = accountInfo.accountAlias + ' - ' + document.title;
    }
  } catch (error) {
    console.error('AWS Login Helper: failed to parse awsc-session-data', error);
  }
}

// ---------------------------------------------------------------------------
// Console page: banner + title for an isolated account/role window
// ---------------------------------------------------------------------------
//
// Pure content-script logic - no CDP, no native host session of any kind. This replaced an
// earlier design that injected this same banner via the native host's CDP session
// (Page.addScriptToEvaluateOnNewDocument), which needed that debugger session to stay
// attached for the tab's entire life to keep re-applying on every navigation - fragile in
// practice (the session dying whenever this process's native messaging pipe recycled, and
// disabling the tab's back/forward cache while attached, which triggered AWS's own "you've
// been signed out" screen). A real content script has none of those problems: Chrome/Edge
// runs it on every matching page load on its own, for as long as this extension stays loaded
// in that profile - which, for the dedicated AWS Login Helper Chrome/Edge instance, is for
// the life of the browser process (see native-host/ChromeManager.cs's --load-extension flag).

function ensureConsoleTitle() {
  if (!consoleLabelInfo) return;
  const prefix = consoleLabelInfo.titleLabel + ' :: ';
  if (document.title.indexOf(prefix) !== 0) {
    document.title = prefix + document.title;
  }
}

function consoleBannerColorFor(label) {
  const lower = label.toLowerCase();
  // Check "nonprod" BEFORE "prod": "NonProd" contains "prod" as a plain substring, so the
  // naive version of this check flagged NonProd accounts with the same red used for real
  // Prod - the opposite of what a safety color-code is for.
  const isNonProd = lower.includes('nonprod') || lower.includes('non-prod') || lower.includes('non_prod');
  const isProd = !isNonProd && lower.includes('prod');
  if (isProd) return '#d13212';
  if (isNonProd || lower.includes('sandbox') || lower.includes('test') || lower.includes('dev')) return '#1d8102';
  return '#232f3e';
}

function insertConsoleBanner() {
  if (!consoleLabelInfo) return;
  if (!document.body) return;
  if (document.getElementById('awslh-profile-banner')) return;

  // Pinned to the BOTTOM, not the top - AWS's own fixed header lives at the top of this page
  // and fighting it for that space (collision, or content rendering behind ours) was a real
  // problem the first time this banner was built. Nothing at the bottom to collide with.
  const banner = document.createElement('div');
  banner.id = 'awslh-profile-banner';
  banner.textContent = consoleLabelInfo.fullLabel;
  banner.style.cssText = 'position:fixed;left:0;right:0;bottom:0;z-index:2147483647;' +
    'box-sizing:border-box;background:' + consoleBannerColorFor(consoleLabelInfo.fullLabel) + ';color:#fff;' +
    'text-align:center;padding:5px 8px;font-family:Arial,sans-serif;font-size:13px;font-weight:bold;';
  document.body.appendChild(banner);
}

function applyConsoleBanner() {
  if (!consoleLabelInfo) return;
  ensureConsoleTitle();
  insertConsoleBanner();
}

// The console's SPA occasionally wipes and rebuilds large chunks of body's own direct
// children well after the retry points above have already run, which can take the inserted
// banner out along with whatever else got replaced. Watching body's OWN child list (not the
// whole subtree) reinserts it immediately when that happens.
function observeConsoleBannerPersistence() {
  const attach = () => {
    if (!document.body) {
      setTimeout(attach, 50);
      return;
    }
    new MutationObserver(insertConsoleBanner).observe(document.body, { childList: true });
  };
  attach();

  // document.title specifically needs the same treatment: AWS's own SPA keeps setting it
  // itself as it finishes loading, well after our own retries, silently overwriting our
  // prefix.
  const attachTitleObserver = () => {
    const titleEl = document.querySelector('head > title');
    if (!titleEl) {
      setTimeout(attachTitleObserver, 50);
      return;
    }
    new MutationObserver(ensureConsoleTitle).observe(titleEl, { childList: true, characterData: true, subtree: true });
  };
  attachTitleObserver();
}
