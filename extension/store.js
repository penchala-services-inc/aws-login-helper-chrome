// Loaded only as an intermediate, one-time navigation target - the native host (see
// native-host/CdpClient.cs's SetCookiesAndNavigateAsync) navigates a freshly-opened AWS
// role window here FIRST, before the real AWS sign-in link.
//
// Its only job: get the account/role label into THIS PROFILE's own chrome.storage.local
// (which content.js's console-page banner logic reads on every later page load in this
// profile - see consoleLabelInfo there) before the AWS federation redirect chain starts.
// Appending the label to the federation URL itself doesn't survive those redirects (AWS's
// own backend constructs each hop's URL itself, discarding whatever query params we tacked
// on to the first one) - chrome.storage.local does, since it's completely decoupled from
// the URL.
//
// This page is part of the same extension package used for the AWS IIC portal/console
// integration (see manifest.json) - it's just never linked from anywhere a real user would
// click; only the native host navigates here directly, and only inside its own separate,
// dedicated Chrome/Edge instance (see ChromeManager.cs's --load-extension flag).
(function () {
  const params = new URLSearchParams(location.search);
  const fullLabel = params.get('fullLabel');
  const titleLabel = params.get('titleLabel') || fullLabel;
  const next = params.get('next');

  const labelEl = document.getElementById('label');
  if (labelEl) {
    labelEl.textContent = fullLabel || '';
  }

  function goToRealDestination() {
    if (next) {
      location.replace(next);
    }
  }

  if (fullLabel) {
    chrome.storage.local.set(
      { awslh_console_label: { fullLabel, titleLabel } },
      goToRealDestination
    );
  } else {
    // No label given (e.g. a syncAllProfiles background refresh, which never wants a visible
    // banner) - nothing to store, just continue straight on to the real destination.
    goToRealDestination();
  }
})();
