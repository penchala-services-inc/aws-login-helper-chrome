const statusEl = document.getElementById('status');
const profilesEl = document.getElementById('profiles');
const selectAllCheckbox = document.getElementById('select-all-profiles');
const deleteSelectedButton = document.getElementById('delete-selected-profiles');

function setStatus(text, ok) {
  statusEl.textContent = text;
  statusEl.className = ok ? 'ok' : 'err';
}

// Promisified chrome.runtime.sendMessage - makes it easy to fire several deletes at once
// (Promise.all, for "Delete selected" below) instead of nesting callbacks per profile.
function sendMessageAsync(message) {
  return new Promise((resolve) => {
    chrome.runtime.sendMessage(message, (response) => {
      if (chrome.runtime.lastError) {
        resolve({ ok: false, error: chrome.runtime.lastError.message });
        return;
      }
      resolve(response || { ok: false, error: 'No response' });
    });
  });
}

async function load() {
  const { 'default-region': region, 'enable-debug': debug } = await chrome.storage.local.get([
    'default-region',
    'enable-debug',
  ]);
  document.getElementById('default-region').value = region || 'us-east-2';
  document.getElementById('enable-debug').checked = !!debug;
}

document.getElementById('save').addEventListener('click', async () => {
  const region = document.getElementById('default-region').value.trim() || 'us-east-2';
  const debug = document.getElementById('enable-debug').checked;
  await chrome.storage.local.set({ 'default-region': region, 'enable-debug': debug });
  setStatus('Saved.', true);
});

document.getElementById('check-host').addEventListener('click', async () => {
  setStatus('Checking companion app...', true);
  chrome.runtime.sendMessage({ action: 'checkNativeHost' }, (response) => {
    if (chrome.runtime.lastError || !response || response.ok !== true) {
      setStatus(
        'Companion app not reachable. Make sure the AWS Login Helper installer has been run on this PC.',
        false
      );
      return;
    }
    setStatus(`Companion app OK (version ${response.version || 'unknown'}).`, true);
  });
});

// New capability - the Firefox extension never had this; a Firefox container was always
// deleted through Firefox's own container-management UI instead. Deletes the isolated
// Chrome profile's own folder (cookies, its own session) via the native host - see
// ChromeManager.DeleteProfileAsync.
async function deleteOneProfile(profileKey, displayLabel, button, li) {
  button.disabled = true;
  button.textContent = 'Deleting...';
  const response = await sendMessageAsync({ action: 'deleteProfile', profileKey });
  if (!response || response.ok !== true) {
    setStatus((response && response.error) || `Could not delete "${displayLabel}".`, false);
    button.disabled = false;
    button.textContent = 'Delete';
    return false;
  }
  li.remove();
  return true;
}

function buildProfileRow(p) {
  const displayLabel = p.displayLabel || p.profileKey;

  const li = document.createElement('li');
  li.style.display = 'flex';
  li.style.alignItems = 'center';
  li.style.justifyContent = 'space-between';
  li.style.gap = '8px';
  // Rows were sitting flush against each other with no visual separation - a bit of
  // vertical breathing room plus a hairline divider makes each profile easier to pick out
  // at a glance, especially once there are several.
  li.style.padding = '8px 4px';
  li.style.borderBottom = '1px solid #ddd';

  const left = document.createElement('span');
  left.style.display = 'flex';
  left.style.alignItems = 'center';
  left.style.gap = '8px';
  left.style.overflow = 'hidden';

  const checkbox = document.createElement('input');
  checkbox.type = 'checkbox';
  checkbox.className = 'profile-checkbox';
  checkbox.dataset.profileKey = p.profileKey;
  checkbox.dataset.displayLabel = displayLabel;
  left.appendChild(checkbox);

  const label = document.createElement('span');
  label.textContent = displayLabel;
  left.appendChild(label);

  li.appendChild(left);

  const deleteButton = document.createElement('button');
  deleteButton.textContent = 'Delete';
  deleteButton.title = 'Remove this isolated profile permanently (its own separate cookies/session)';
  deleteButton.addEventListener('click', () => {
    if (!confirm(`Delete the isolated profile for "${displayLabel}"?\n\nThis removes its own separate cookies/session - you'll just sign in again next time you open it.`)) {
      return;
    }
    deleteOneProfile(p.profileKey, displayLabel, deleteButton, li);
  });
  li.appendChild(deleteButton);

  return li;
}

document.getElementById('list-profiles').addEventListener('click', async () => {
  profilesEl.innerHTML = '<li>Loading...</li>';
  selectAllCheckbox.checked = false;
  chrome.runtime.sendMessage({ action: 'listProfiles' }, (response) => {
    if (chrome.runtime.lastError || !response || response.ok !== true) {
      profilesEl.innerHTML = '<li>Could not reach companion app.</li>';
      return;
    }
    const profiles = response.profiles || [];
    if (profiles.length === 0) {
      profilesEl.innerHTML = '<li>No isolated profiles created yet.</li>';
      return;
    }
    profilesEl.innerHTML = '';
    profiles.forEach((p) => profilesEl.appendChild(buildProfileRow(p)));
  });
});

selectAllCheckbox.addEventListener('change', () => {
  profilesEl.querySelectorAll('.profile-checkbox').forEach((cb) => {
    cb.checked = selectAllCheckbox.checked;
  });
});

deleteSelectedButton.addEventListener('click', async () => {
  const checked = Array.from(profilesEl.querySelectorAll('.profile-checkbox:checked'));
  if (checked.length === 0) {
    setStatus('No profiles selected.', false);
    return;
  }

  const labels = checked.map((cb) => cb.dataset.displayLabel);
  if (!confirm(`Delete ${checked.length} isolated profile${checked.length !== 1 ? 's' : ''}?\n\n${labels.join('\n')}\n\nEach removes its own separate cookies/session - you'll just sign in again next time you open it.`)) {
    return;
  }

  deleteSelectedButton.disabled = true;
  deleteSelectedButton.textContent = 'Deleting...';

  // Independent, cheap file operations (see DeleteProfileAsync) - firing them together
  // instead of one at a time is safe and makes deleting a big batch feel instant rather
  // than a visible one-by-one crawl.
  const results = await Promise.all(
    checked.map(async (cb) => {
      const li = cb.closest('li');
      const response = await sendMessageAsync({ action: 'deleteProfile', profileKey: cb.dataset.profileKey });
      if (response && response.ok === true) {
        li.remove();
        return { ok: true };
      }
      cb.checked = false;
      return { ok: false, displayLabel: cb.dataset.displayLabel, error: (response && response.error) || 'failed' };
    })
  );

  deleteSelectedButton.disabled = false;
  deleteSelectedButton.textContent = 'Delete selected';
  selectAllCheckbox.checked = false;

  const failures = results.filter((r) => !r.ok);
  const succeededCount = results.length - failures.length;
  if (failures.length === 0) {
    setStatus(`Deleted ${succeededCount} profile${succeededCount !== 1 ? 's' : ''}.`, true);
  } else {
    setStatus(
      `Deleted ${succeededCount}, failed ${failures.length}: ${failures.map((f) => `${f.displayLabel} (${f.error})`).join('; ')}`,
      false
    );
  }
});

load();
