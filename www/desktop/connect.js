'use strict';
/* Connection screen: shown when no API key is set, the server refuses the key, or the server cannot be reached.
   The app connects to the main Agentic instance unless "Use a different server" is switched on. */
(function () {
  const token = document.querySelector('meta[name="desktop-token"]').content;
  const form = document.getElementById('connect');
  const result = document.getElementById('result');
  const custom = document.getElementById('custom');
  const serverField = document.getElementById('server-field');
  const testBtn = document.getElementById('test');
  const params = new URLSearchParams(location.search);
  const next = params.get('next') && params.get('next').startsWith('/') && !params.get('next').startsWith('//') ? params.get('next') : '/';
  const reasons = {
    nokey: ['info', 'Connect the desktop app to Agentic with your API key.'],
    auth: ['bad', 'The server did not accept the API key. Create a new key in the web app (user menu → API key) and paste it here.'],
    offline: ['warn', 'The Agentic server cannot be reached. Check that it is running and try again.']
  };
  const why = reasons[params.get('reason')];
  if (why) { const r = document.getElementById('reason'); r.className = 'notice ' + why[0]; r.textContent = why[1]; }

  const call = async (method, path, body) => {
    const res = await fetch(path, { method, headers: { 'X-Desktop-Token': token, 'Content-Type': 'application/json' }, body: body ? JSON.stringify(body) : undefined });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error((data.error && data.error.message) || res.statusText);
    return data;
  };
  const show = (ok, text) => { result.className = 'result ' + (ok ? 'ok' : 'bad'); result.textContent = text; };
  const busy = (btn, on) => { btn.disabled = on; btn.classList.toggle('busy', on); };
  // The server address and the Test button only matter for a different server.
  const syncCustom = () => {
    serverField.classList.toggle('hidden', !custom.checked);
    testBtn.classList.toggle('hidden', !custom.checked);
    if (custom.checked) form.serverBaseUrl.focus();
  };
  custom.addEventListener('change', syncCustom);

  call('GET', '/api/local/settings').then(s => {
    custom.checked = !!s.useCustomServer;
    form.serverBaseUrl.value = s.serverBaseUrl || '';
    form.serverBaseUrl.placeholder = s.defaultServer || form.serverBaseUrl.placeholder;
    if (s.hasKey) { form.apiKey.placeholder = s.apiKeyMasked + '  (saved)'; document.getElementById('key-help').textContent = 'Leave empty to keep the saved key.'; }
    serverField.classList.toggle('hidden', !custom.checked);
    testBtn.classList.toggle('hidden', !custom.checked);
    form.apiKey.focus();
  }).catch(e => show(false, e.message));

  const test = async () => {
    const r = await call('POST', '/api/local/settings/test', { useCustomServer: custom.checked, serverBaseUrl: form.serverBaseUrl.value, apiKey: form.apiKey.value });
    if (r.ok) show(true, `Connected as ${r.user} (${r.role}).`); else show(false, r.message);
    return r.ok;
  };
  testBtn.addEventListener('click', async () => {
    busy(testBtn, true);
    try { await test(); } catch (e) { show(false, e.message); } finally { busy(testBtn, false); }
  });
  form.addEventListener('submit', async ev => {
    ev.preventDefault();
    const btn = document.getElementById('save');
    busy(btn, true);
    try {
      if (!await test()) return;
      const body = { useCustomServer: custom.checked };
      if (custom.checked) body.serverBaseUrl = form.serverBaseUrl.value.trim();
      if (form.apiKey.value.trim()) body.apiKey = form.apiKey.value.trim();
      await call('POST', '/api/local/settings', body);
      show(true, 'Connected — opening Agentic…');
      setTimeout(() => { location.href = next === '/' ? '/chat' : next; }, 400);
    } catch (e) { show(false, e.message); } finally { busy(btn, false); }
  });
})();
