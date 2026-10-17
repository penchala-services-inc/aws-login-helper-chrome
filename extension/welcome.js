// Welcome page behavior: OS tabs + "Copy" buttons. Kept in its own file because Manifest V3
// extension pages can't run inline <script> blocks (default CSP is script-src 'self').
//
// Everything here is progressive enhancement: with no JavaScript, welcome.html still shows
// both the Windows and macOS instructions one after the other.

(function () {
  document.body.classList.add('js');

  const tabs = Array.from(document.querySelectorAll('.tab'));
  const panels = {
    windows: document.getElementById('platform-windows'),
    mac: document.getElementById('platform-mac'),
  };

  function select(platform) {
    tabs.forEach((tab) => {
      const active = tab.dataset.platform === platform;
      tab.classList.toggle('active', active);
      tab.setAttribute('aria-selected', active ? 'true' : 'false');
    });
    Object.keys(panels).forEach((key) => {
      panels[key].classList.toggle('active', key === platform);
    });
  }

  tabs.forEach((tab) => {
    tab.addEventListener('click', () => select(tab.dataset.platform));
  });

  // Pre-select the tab for the OS the BROWSER is running on and mark it "Detected".
  // chrome.runtime.getPlatformInfo reports the real OS ('win', 'mac', 'linux', 'cros', ...)
  // rather than guessing from the user agent. Linux/ChromeOS/etc. have no companion app yet,
  // so those visitors land on Windows with a note explaining that.
  function applyDetectedOs(os) {
    const platform = os === 'mac' ? 'mac' : 'windows';
    const detected = os === 'mac' || os === 'win';

    tabs.forEach((tab) => {
      tab.classList.toggle('detected', detected && tab.dataset.platform === platform);
    });
    document.getElementById('os-note').hidden = detected;
    select(platform);
  }

  try {
    chrome.runtime.getPlatformInfo((info) => applyDetectedOs(info && info.os));
  } catch (err) {
    const ua = (navigator.userAgent || '').toLowerCase();
    applyDetectedOs(ua.includes('mac') ? 'mac' : ua.includes('windows') ? 'win' : 'other');
  }

  // "Copy" buttons inside the Terminal command blocks.
  document.querySelectorAll('.cmd .copy').forEach((button) => {
    button.addEventListener('click', async () => {
      const block = button.closest('.cmd');
      // Everything in the block except the button's own label.
      const text = Array.from(block.childNodes)
        .filter((node) => node !== button)
        .map((node) => node.textContent)
        .join('')
        .trim();
      try {
        await navigator.clipboard.writeText(text);
        button.textContent = 'Copied';
      } catch (err) {
        button.textContent = 'Copy failed';
      }
      setTimeout(() => { button.textContent = 'Copy'; }, 1500);
    });
  });
})();
