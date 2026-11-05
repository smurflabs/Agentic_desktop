'use strict';
/* Agentic desktop layer. The window shows the Agentic web app itself (served through this app, which adds the API key);
   this script adds what only a desktop app can do:
   - Chat first, and the title bar follows the web app's light or dark theme
   - Claude Code skills and slash commands (/name) from this computer, usable in Chat
   - local MCP servers (stdio or HTTP) as chat tools
   - tools on this computer (files, commands, the Agentic API as you), with approval prompts like Claude Code
   - pages: This computer → Skills, Local MCP, Desktop settings */

const DK = {
  token: (document.querySelector('meta[name="desktop-token"]') || {}).content || '',
  version: (document.querySelector('meta[name="desktop-version"]') || {}).content || '',
  skills: [], skillsAt: 0, mcp: [], approvals: {}, always: {}, skillFilter: { q: '', source: '', kind: '' }
};
const DK_ICONS = {
  pc: '<path d="M3 5h18v11H3zM8 20h8M12 16v4"/>',
  skill: '<path d="M12 3l2.6 5.3 5.9.9-4.3 4.1 1 5.8L12 16.4 6.8 19.1l1-5.8L3.5 9.2l5.9-.9z"/>',
  plug: '<path d="M9 3v5M15 3v5M7 8h10v4a5 5 0 0 1-10 0zM12 17v4"/>',
  gear: '<circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z"/>',
  cmd: '<path d="M4 17l6-6-6-6M12 19h8"/>',
  folder: '<path d="M3 6h6l2 2h10v11H3z"/>',
  shield: '<path d="M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z"/>',
  camera: '<path d="M4 8h3l2-3h6l2 3h3v11H4z"/><circle cx="12" cy="13" r="3.5"/>'
};
/** This page is a pane of Chat's multitask mode (a frame of the Chat page): the page around it does the window's work. */
const DK_PANE = typeof IN_PANE !== 'undefined' && IN_PANE;
const dkIcon = (name, cls = '') => `<svg class="ic ${cls}" viewBox="0 0 24 24" aria-hidden="true">${DK_ICONS[name] || ''}</svg>`;

/** Calls the desktop endpoints (/api/local/*) with this launch's token. */
async function dapi(method, path, body, raw) {
  const opts = { method, headers: { 'X-Desktop-Token': DK.token } };
  if (raw !== undefined) { opts.body = raw; opts.headers['Content-Type'] = 'application/octet-stream'; }
  else if (body !== undefined) { opts.body = JSON.stringify(body); opts.headers['Content-Type'] = 'application/json'; }
  const res = await fetch(path, opts);
  const text = await res.text();
  let data = null; try { data = text ? JSON.parse(text) : null; } catch { data = null; }
  if (!res.ok) throw new Error(data && data.error ? (data.error.message || data.error) : res.statusText);
  return data;
}

// ------------------------------------------------------------------ shell: theme, navigation, menus
(function shell() {
  // The window's title bar follows the web app's theme.
  const effective = () => { const t = document.documentElement.dataset.theme; return t === 'dark' || t === 'light' ? t : (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'); };
  let last = null;
  const report = () => {
    const t = effective();
    if (t === last || DK_PANE) return;
    last = t;
    try { window.chrome && chrome.webview && chrome.webview.postMessage({ kind: 'theme', theme: t }); } catch { /* not in the desktop window */ }
  };
  new MutationObserver(report).observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
  matchMedia('(prefers-color-scheme: dark)').addEventListener('change', report);
  report();
  document.documentElement.classList.add('desktop-app');

  ROUTES.unshift([/^\/local\/skills$/, 'localSkills'], [/^\/local\/mcp$/, 'localMcp'], [/^\/local\/settings$/, 'localSettings']);

  document.addEventListener('DOMContentLoaded', () => {
    const chat = document.querySelector('#nav a[href="/chat"]');
    if (chat) chat.insertAdjacentHTML('afterend', `<div class="nav-group">This computer</div>
      <a href="/local/skills" data-nav><svg viewBox="0 0 24 24" aria-hidden="true">${DK_ICONS.skill}</svg>Skills</a>
      <a href="/local/mcp" data-nav><svg viewBox="0 0 24 24" aria-hidden="true">${DK_ICONS.plug}</svg>Local MCP<span class="pill hidden" id="nav-local-mcp"></span></a>
      <a href="/local/settings" data-nav><svg viewBox="0 0 24 24" aria-hidden="true">${DK_ICONS.gear}</svg>Desktop settings</a>`);
    const logout = document.querySelector('#user-menu [data-act="logout"]');
    if (logout) logout.outerHTML = `<button data-act="dk-settings" role="menuitem">${dkIcon('gear')}Desktop settings</button>`;
    const brand = document.querySelector('.brand-lockup.side');
    if (brand) brand.insertAdjacentHTML('beforeend', `<span class="desktop-pill">${dkIcon('pc')} Desktop${DK.version ? ' ' + esc(DK.version.split('.').slice(0, 2).join('.')) : ''}</span>`);
  });
  // The app signs in with its API key: there is no sign-in form; a refused key opens the connection screen.
  window.showLogin = () => { location.href = '/desktop/connect.html?reason=auth'; };
  ACTIONS['logout'] = () => navigate('/local/settings');
  ACTIONS['dk-settings'] = () => { $('#user-menu').classList.add('hidden'); navigate('/local/settings'); };
  setInterval(refreshMcpBadge, 15000);
  setTimeout(refreshMcpBadge, 2500);
})();

async function refreshMcpBadge() {
  try {
    const d = await dapi('GET', '/api/local/mcp');
    DK.mcp = d.servers;
    const on = d.servers.filter(s => s.enabled), up = on.filter(s => s.status.connected).length, pill = $('#nav-local-mcp');
    // A project's server waiting to be allowed shows as ! (the page asks).
    const waiting = on.filter(s => s.approval === 'ask').length;
    if (pill) { pill.textContent = waiting ? '!' : on.length ? `${up}/${on.length}` : ''; pill.classList.toggle('hidden', !on.length); pill.classList.toggle('warn', !!waiting || up < on.length); }
  } catch { /* the local API is not available (e.g. opened in a browser) */ }
}
async function loadSkills(force) {
  if (!force && Date.now() - DK.skillsAt < 30000 && DK.skills.length) return DK.skills;
  const d = await dapi('GET', '/api/local/skills');
  DK.skills = d.items; DK.roots = d.roots; DK.skillsAt = Date.now();
  return DK.skills;
}

// ------------------------------------------------------------------ chat: tools on this computer
DK.deviceId = (document.querySelector('meta[name="desktop-device"]') || {}).content || '';
DK.machine = (document.querySelector('meta[name="desktop-machine"]') || {}).content || 'Desktop';
function chatPrefs() {
  let p = null; try { p = JSON.parse(localStorage.getItem('desktop.chat') || 'null'); } catch { /* ignore */ }
  return Object.assign({ skills: true, local: true, mcp: null, disabledSkills: [] }, p);
}
function saveChatPrefs(p) { try { localStorage.setItem('desktop.chat', JSON.stringify(p)); } catch { /* ignore */ } pushCatalog(); }
function selectedMcp(p) {
  const enabled = DK.mcp.filter(s => s.enabled).map(s => s.status.id);
  return p.mcp === null ? enabled : p.mcp.filter(id => enabled.includes(id));
}
function pcCount() { const p = chatPrefs(); return (p.local ? 1 : 0) + selectedMcp(p).length; }
function skillCount() { const p = chatPrefs(); return p.skills ? DK.skills.filter(s => s.kind === 'skill' && !p.disabledSkills.includes(s.name)).length : 0; }
/** Asks whether a tool may use a path outside the workspace folders: 'file', 'folder' or null. */
function dkAskAccess(p, tool) {
  return new Promise(resolve => {
    const done = v => { delete ACTIONS['dk-access']; closeModal(); resolve(v); };
    ACTIONS['dk-access'] = el => done(el.dataset.choice || null);
    openModal('Outside the workspace', `<p>The model wants to use <code>${esc(p)}</code> (${esc(tool)}), which is outside your workspace folders.</p>
      <p class="muted small">Desktop settings can add folders, or let the tools use the whole computer.</p>`,
      `<button class="btn" data-act="dk-access" data-choice="">Don't allow</button><button class="btn" data-act="dk-access" data-choice="file">Allow this</button>
       <button class="btn primary" data-act="dk-access" data-choice="folder">Allow its folder</button>`, 'narrow');
  });
}
async function prepareLocal(content) {
  const p = chatPrefs();
  return dapi('POST', '/api/local/chat/prepare', { content, skills: p.skills, localTools: p.local, mcp: selectedMcp(p), disabledSkills: p.disabledSkills });
}
/** Tells the Agentic server what this computer offers, so chats started in the web app can use it while the app is open. */
let catalogTimer = 0;
function pushCatalog() {
  clearTimeout(catalogTimer);
  catalogTimer = setTimeout(async () => {
    if (!DK.deviceId) return;
    try {
      const d = await prepareLocal('');
      await fetch(`/api/chat/devices/${encodeURIComponent(DK.deviceId)}/catalog`, { method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name: DK.machine, clientTools: d.tools, clientInstructions: d.instructions }) });
    } catch { /* the server may be older or unreachable */ }
  }, 400);
}
// This page (the same when its event stream reconnects) and the streams it has had (the server's subscribers): a tool call
// for this computer is addressed to one stream, so two windows of the app never both run it.
const DK_PAGE = 'p' + Math.random().toString(36).slice(2, 12);
const DK_SUBS = new Set();
document.addEventListener('chathub-hello', ev => { const d = ev.detail || {}; if (d.subscriber) DK_SUBS.add(d.subscriber); });
function dkIsMine(target) {
  try { if (typeof CHAT !== 'undefined' && CHAT.hello && CHAT.hello.subscriber) DK_SUBS.add(CHAT.hello.subscriber); } catch { /* not the chat page */ }
  return !target || !DK_SUBS.size || DK_SUBS.has(target);
}
const DK_HANDLED = new Set();
window.chatClient = {
  deviceId: DK.deviceId,
  /** The / list below also offers the account's saved prompts, so the web app's own prompt list stays closed. */
  slashPrompts: true,
  // ! commands in the message box run on this computer (with its tools on); /feedback says which app and version sent it.
  get machineName() { return DK.machine + ' (desktop app)'; },
  appName: 'desktop app',
  get appVersion() { return DK.version || ''; },
  shellEnabled() { return !!chatPrefs().local; },
  logs() { return dapi('POST', '/api/local/chat/logs', {}).then(r => (r && r.text) || ''); },
  /** /plugin: Claude Code plugins on this computer. */
  plugins(body) { return dapi('POST', '/api/local/chat/plugins', body || {}); },
  hubQuery: () => `?device=${encodeURIComponent(DK.deviceId)}&name=${encodeURIComponent(DK.machine)}&page=${DK_PAGE}`,
  composerHtml() {
    const n = pcCount(), k = skillCount();
    return `${DK.features && DK.features.screenshots === false ? '' : `<button type="button" class="chip-toggle dk-shot-btn" data-act="dk-screenshot" aria-label="Screenshot"
        data-tip="Screenshot: drag over part of your screen to attach it${DK.keys && DK.keys.shot ? ` (${esc(DK.keys.shot)})` : ''}">${dkIcon('camera')}</button>`}
      <div class="tools-menu-wrap dk-skills-wrap"><button class="chip-toggle${k ? ' on' : ''}" data-act="dk-skills-menu" id="dk-skills-btn" data-tip="Claude Code skills and slash commands on this computer">${dkIcon('skill')} Skills${k ? ` <b>${k}</b>` : ''}</button>
        <div class="menu tools-menu dk-skills-menu hidden" id="dk-skills-menu"></div></div>
      <div class="tools-menu-wrap dk-pc-wrap"><button class="chip-toggle${n ? ' on' : ''}" data-act="dk-pc-menu" id="dk-pc-btn" data-tip="Files, commands and local MCP servers on this computer">${dkIcon('pc')} This PC${n ? ` <b>${n}</b>` : ''}</button>
        <div class="menu tools-menu hidden" id="dk-pc-menu"></div></div>`;
  },
  async prepare({ content }) {
    const d = await prepareLocal(content);
    pushCatalog();
    return { tools: d.tools, instructions: d.instructions, device: DK.deviceId, agents: d.agents || [], rules: d.rules || undefined };
  },
  /** Before a message is sent: SessionStart and UserPromptSubmit hooks (exit code 2 stops it; what they print goes with it). */
  checkPrompt({ content, chatId }) { return chatPrefs().local ? dapi('POST', '/api/local/chat/check-prompt', { content, chatId }) : Promise.resolve({}); },
  // /bashes, /monitors, /hooks, /skills, /agents, /worktree and /diff in the chat.
  jobs() { return dapi('POST', '/api/local/chat/jobs', {}); },
  stopJob(id) { return dapi('POST', '/api/local/chat/job-stop', { id }); },
  hooks() { return dapi('POST', '/api/local/chat/hooks', {}); },
  // The hooks editor of /hooks: add (the project's or your own .agentic/settings.json), remove, try one.
  saveHook(h) { return dapi('POST', '/api/local/chat/hook-save', h); },
  removeHook(h) { return dapi('POST', '/api/local/chat/hook-remove', h); },
  testHook(h) { return dapi('POST', '/api/local/chat/hook-test', h); },
  async skills() {
    const own = (await loadSkills(true)).map(s => ({ name: s.name, description: s.description, kind: s.kind === 'command' ? 'command' : 'skill' }));
    // The MCP servers' prompts run as /mcp__server__prompt.
    const prompts = await dapi('POST', '/api/local/chat/mcp-prompts', {}).catch(() => []);
    return [...own, ...(prompts || [])];
  },
  agents() { return dapi('POST', '/api/local/chat/agents', {}); },
  saveAgent(a) { return dapi('POST', '/api/local/chat/agent-save', a); },
  worktree(arg) { return dapi('POST', '/api/local/chat/worktree', { arg }); },
  diff() { return dapi('POST', '/api/local/chat/diff', {}); },
  /** Approvals are asked by the Agentic server (any of your windows can answer), so the tool simply runs here. */
  async runTool(call, conv) {
    // id and mode go to the hooks (tool_use_id, permission_mode).
    const r = await dapi('POST', '/api/local/chat/run', { name: call.name, arguments: call.arguments || '{}', approved: call.approved !== false, mode: call.mode || undefined,
      id: call.id || undefined, chatId: (conv && conv.id) || call.chatId || undefined });
    // image: a screenshot the model took, or a picture file it read; images: pictures an MCP tool made (a browser screenshot)
    if (r && r.needsAccess) {
      // A path outside the workspace: the user decides (this path, its folder, or no), and the call runs again when allowed.
      const choice = await dkAskAccess(r.needsAccess, call.name);
      if (!choice) return { ok: false, result: 'The user did not allow access to ' + r.needsAccess + ' (outside the workspace). Do not try it again; work inside the workspace or ask the user.' };
      await dapi('POST', '/api/local/chat/allow-path', { path: r.needsAccess, folder: choice === 'folder' });
      return window.chatClient.runTool(call, conv);
    }
    return { ok: r.ok, result: r.result, image: r.image, images: r.images || undefined };
  },
  /** Hooks of this computer for what happens in an answer this window runs (Notification, Pre/PostCompact, SubagentStart/Stop). */
  hookEvent({ event, chatId, payload }) { return chatPrefs().local ? dapi('POST', '/api/local/chat/hook-event', { event, chatId, payload }) : Promise.resolve({}); },
  /** PermissionRequest hooks may answer an approval for a tool on this computer: {decision: allow | deny, message}. */
  permissionRequest(a) { return chatPrefs().local ? dapi('POST', '/api/local/chat/permission-request', a) : Promise.resolve({}); },
  /** A file's text as it is now (an approval card shows an overwrite as a diff). */
  fileText(path) { return dapi('POST', '/api/local/chat/file-text', { path }); },
  // Rewind: files are saved before an answer's first change to them, under the question that started it.
  beginTurn({ chatId, messageId }) { dapi('POST', '/api/local/chat/turn', { chatId, messageId }).catch(() => { }); },
  checkpoints({ chatId }) { return dapi('POST', '/api/local/chat/checkpoints', { chatId }); },
  restoreFiles({ chatId, messageIds }) { return dapi('POST', '/api/local/chat/restore', { chatId, messageIds }); },
  // The project's AGENTIC.md (instructions every message from this app includes).
  agenticMd: {
    read() { return dapi('POST', '/api/local/chat/agentic', {}); },
    write({ content }) { return dapi('POST', '/api/local/chat/agentic', { content }); }
  },
  // @ in the message box, /add-dir, /doctor and Stop hooks.
  listFiles(query) { return chatPrefs().local ? dapi('POST', '/api/local/chat/files', { query }) : Promise.resolve([]); },
  addDir(path) { return dapi('POST', '/api/local/chat/add-dir', { path }); },
  /** /mcp: this computer's MCP servers page. */
  mcpPage() { navigate('/local/mcp'); },
  doctor() { return dapi('POST', '/api/local/chat/doctor', {}); },
  recordUsage({ chatId, message, cachedTokens }) { dapi('POST', '/api/local/chat/usage', { chatId, message, cachedTokens }).catch(() => { }); },
  afterAnswer({ chatId, text, active }) { return chatPrefs().local ? dapi('POST', '/api/local/chat/stop', { chatId, text, active }) : Promise.resolve({}); },
  /** A system notification (an answer finished, a chat needs you) while the window is in the background: Windows shows it
   *  from the app's notification-area icon, and clicking it brings the window back on that chat. */
  notify({ title, body, chatId }) {
    let wv = null;
    try { wv = (window.chrome && chrome.webview) || (window.parent !== window && window.parent.chrome && window.parent.chrome.webview) || null; } catch { /* a pane of another origin */ }
    if (wv) wv.postMessage({ kind: 'notify', title: String(title || 'Agentic').slice(0, 63), body: String(body || '').slice(0, 255), chatId: chatId || '' });
  },
  /** The Run chip in Chat (chatrun.js): runs the project on this computer; a web app shows in the preview pane next to the chat. */
  run: {
    async configs() { return (await dapi('GET', '/api/local/run/configs')).configs; },
    start(id) { return dapi('POST', '/api/local/run/start', { id }); },
    stop(id) { return dapi('POST', '/api/local/run/stop', { id }); },
    status() { return dapi('GET', '/api/local/run/status'); },
    log(id, from) { return dapi('POST', '/api/local/run/log', { id, from: from || 0 }); },
    preview(url, title) { return dapi('POST', '/api/local/run/preview', { url, title: title || '' }); },
    edit() { return dapi('POST', '/api/local/run/edit', {}); },
    // The Artifacts page: the workspace folders, and a project's website as a public link (the same link when shared again).
    projects() { return dapi('GET', '/api/local/run/projects'); },
    share(id) { return dapi('POST', '/api/local/run/share', { id }); }
  },
  /** A file the chat names (a tool card's path, a changed file, `src/app.js` in an answer) shows selected in its folder. */
  openFileLabel: 'Show in its folder:',
  openFile({ path }) { return dapi('POST', '/api/local/open', { target: 'file', path }); }
};
// The app asks for this after a notification is clicked: open that chat.
window.dkOpenChat = id => { if (typeof navigate === 'function' && /^[\w-]{1,64}$/.test(id || '')) navigate('/chat/' + encodeURIComponent(id)); };

// ------------------------------------------------------------------ keys
/** A key press is one of the web app's shortcuts (Keyboard shortcuts, keys.js); with an older server, the usual keys. */
function dkKeyIs(ev, id, ...usual) { return typeof keyIs === 'function' ? keyIs(ev, id) : usual.includes(ev.key); }
const DK_KEY_NAMES = { Semicolon: 'OemSemicolon', Equal: 'Oemplus', Comma: 'Oemcomma', Minus: 'OemMinus', Period: 'OemPeriod', Slash: 'OemQuestion',
  Backquote: 'Oemtilde', BracketLeft: 'OemOpenBrackets', Backslash: 'OemPipe', BracketRight: 'OemCloseBrackets', Quote: 'OemQuotes', Space: 'Space',
  Backspace: 'Back', ArrowUp: 'Up', ArrowDown: 'Down', ArrowLeft: 'Left', ArrowRight: 'Right', Escape: 'Esc' };
/** The pressed keys written the way Settings stores them ("Ctrl+Alt+S", "Enter", "Ctrl+OemSemicolon"); '' for a modifier alone. */
function dkKeyText(ev) {
  if (['Control', 'Shift', 'Alt', 'Meta', 'AltGraph', 'CapsLock', 'OS'].includes(ev.key)) return '';
  const c = ev.code || '';
  let m, name = DK_KEY_NAMES[c];
  if (!name && (m = /^Key([A-Z])$/.exec(c))) name = m[1];
  if (!name && (m = /^Digit(\d)$/.exec(c))) name = m[1];
  if (!name && (m = /^Numpad(\d)$/.exec(c))) name = 'NumPad' + m[1];
  if (!name) name = ev.key.length === 1 ? ev.key.toUpperCase() : ev.key;
  return (ev.ctrlKey ? 'Ctrl+' : '') + (ev.altKey ? 'Alt+' : '') + (ev.shiftKey ? 'Shift+' : '') + (ev.metaKey ? 'Win+' : '') + name;
}
// Shortcut boxes in Settings record what you press (Backspace or Delete alone empties the box; Tab still moves on).
document.addEventListener('keydown', ev => {
  const input = ev.target;
  if (!input || !input.matches || !input.matches('input[data-capture-key]')) return;
  if (ev.key === 'Tab' && !ev.ctrlKey && !ev.altKey && !ev.metaKey) return;
  ev.preventDefault(); ev.stopPropagation();
  if ((ev.key === 'Backspace' || ev.key === 'Delete') && !ev.ctrlKey && !ev.altKey && !ev.shiftKey && !ev.metaKey) { input.value = ''; return; }
  const text = dkKeyText(ev);
  if (text) input.value = text;
}, true);

// ------------------------------------------------------------------ screenshots and the quick-entry shortcut
/** Lets the user drag over part of the screen (the window steps aside) and attaches the picture to the next message. */
ACTIONS['dk-screenshot'] = async () => {
  // Chat in multitask mode: the picture goes to the current pane's message box.
  const pane = !DK_PANE && window.mtFocusedWindow && window.mtFocusedWindow();
  if (pane && pane.ACTIONS_SHOT) return pane.ACTIONS_SHOT();
  if (typeof attachFiles !== 'function' || !$('#composer')) { navigate('/chat'); await new Promise(r => setTimeout(r, 600)); }
  let res;
  try { res = await fetch('/api/local/screenshot', { method: 'POST', headers: { 'X-Desktop-Token': DK.token } }); }
  catch (e) { toast('Screenshot failed: ' + e.message, 'bad'); return; }
  if (!res.ok) { let m = res.statusText; try { m = (await res.json()).error.message || m; } catch { /* not JSON */ } toast('Screenshot failed: ' + m, 'bad'); return; }
  if (!(res.headers.get('content-type') || '').startsWith('image/')) return; // cancelled with Esc
  const blob = await res.blob();
  const name = `screenshot-${new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-')}.png`;
  if (typeof attachFiles === 'function' && $('#composer')) { attachFiles([new File([blob], name, { type: 'image/png' })]); $('#chat-input')?.focus(); }
};
// Called by the window for its global shortcuts (Desktop settings → Shortcuts).
window.dkScreenshot = () => ACTIONS['dk-screenshot']();
window.ACTIONS_SHOT = () => ACTIONS['dk-screenshot']();
window.dkQuickEntry = () => {
  const pane = window.mtFocusedWindow && window.mtFocusedWindow();
  if (pane && pane.navigate) {
    pane.navigate('/chat');
    setTimeout(() => { try { pane.focus(); pane.document.getElementById('chat-input')?.focus(); } catch { /* closed */ } }, 350);
    return;
  }
  if (location.pathname !== '/chat' || (typeof CHAT !== 'undefined' && CHAT.conv)) navigate('/chat');
  setTimeout(() => $('#chat-input')?.focus(), 350);
};
dapi('GET', '/api/local/settings').then(s => {
  // What this computer's app can do (the Linux build has no global shortcuts or notification-area icon).
  DK.platform = s.platform || 'windows';
  DK.features = s.features || { hotkeys: true, tray: true, screenshots: true };
  DK.keys = DK.features.hotkeys ? { quick: s.quickEntryHotkey, shot: s.screenshotHotkey } : null;
  document.documentElement.classList.toggle('dk-no-shots', DK.features.screenshots === false);
}).catch(() => { });

// Tool calls for this computer from chats started in another window (the web app): run them and send the result.
document.addEventListener('chathub', async ev => {
  const m = ev.detail || {};
  if (m.type !== 'client_tool' || !m.data || m.data.device !== DK.deviceId) return;
  // Only calls addressed to this window's stream, and each once (the server may send it again after a reconnect).
  if (!dkIsMine(m.data.target) || DK_HANDLED.has(m.data.id)) return;
  DK_HANDLED.add(m.data.id);
  if (DK_HANDLED.size > 500) DK_HANDLED.delete(DK_HANDLED.values().next().value);
  let r;
  try { r = await window.chatClient.runTool(m.data, { id: m.chatId }); } catch (e) { r = { ok: false, result: e.message }; }
  // The answer waits for this result: retry a lost request instead of leaving it waiting until it times out.
  for (let attempt = 1; attempt <= 4; attempt++) {
    try {
      const res = await fetch(`/api/chat/conversations/${encodeURIComponent(m.chatId)}/tool-results`, { method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ id: m.data.id, ok: !!r.ok, result: String(r.result ?? ''), image: r.image || undefined, images: r.images || undefined }) });
      if (res.ok || res.status === 404) return;
    } catch { /* network: try again */ }
    await new Promise(res => setTimeout(res, 1500 * attempt));
  }
});
// Something waits for you (approval or question): flash the taskbar button when the window is in the background.
document.addEventListener('chat:approval', () => { try { if (window.chrome && chrome.webview) chrome.webview.postMessage({ kind: 'attention' }); } catch { /* ignore */ } });
document.addEventListener('DOMContentLoaded', () => setTimeout(() => {
  if (typeof ensureChatHub === 'function') { ensureChatHub(); if (!DK_PANE) { pushCatalog(); setInterval(pushCatalog, 120000); } }
  else toast('Update the Agentic server to use skills and tools from this PC in Chat.', 'bad');
}, 1500));

// ------------------------------------------------------------------ updates (the server hands out new builds; see Updater.vb)
DK.update = { base: null, dismissed: '' };
function dkUpdateText(u) {
  if (!u) return '';
  switch (u.state) {
    case 'ready': return `Version ${esc(u.latestVersion)} is installed — restart the app to use it.`;
    case 'downloading': return `Downloading version ${esc(u.latestVersion || '')}… ${u.progress || 0}%`;
    case 'checking': return 'Looking for updates…';
    case 'current': return `Up to date${u.lastCheckUtc ? ' · checked ' + esc(ago(u.lastCheckUtc)) : ''}.`;
    case 'available': return `Version ${esc(u.latestVersion)} is available.`;
    case 'none': return 'The server does not hand out desktop app builds yet.';
    case 'off': return 'Automatic updates are off.';
    case 'dev': return 'Development build: it does not update itself.';
    case 'error': return `The last check failed: ${esc(u.error || 'unknown error')}`;
    default: return 'Not checked yet.';
  }
}
function dkUpdateBanner(u) {
  let bar = document.getElementById('dk-update');
  const reload = DK.update.base !== null && u.filesUpdated > DK.update.base && u.state !== 'ready';
  const key = u.state + ':' + (u.latestVersion || '') + ':' + u.filesUpdated;
  const show = (u.state === 'ready' || reload) && DK.update.dismissed !== key;
  if (!show) { if (bar) bar.remove(); return; }
  if (!bar) { bar = document.createElement('div'); bar.id = 'dk-update'; bar.className = 'dk-update'; bar.setAttribute('role', 'status'); document.body.appendChild(bar); }
  bar.dataset.key = key;
  bar.innerHTML = u.state === 'ready'
    ? `<span><b>Agentic Desktop ${esc(u.latestVersion)}</b> is ready.</span><button class="btn small primary" data-act="dk-update-restart">Restart now</button><button class="btn small ghost" data-act="dk-update-later">Later</button>`
    : `<span>The desktop features were updated.</span><button class="btn small primary" data-act="dk-update-reload">Reload</button><button class="btn small ghost" data-act="dk-update-later">Later</button>`;
}
async function dkUpdatePoll() {
  try {
    const u = await dapi('GET', '/api/local/update');
    if (DK.update.base === null) DK.update.base = u.filesUpdated || 0;   // what the start-up sync did is already on this page
    DK.update.last = u;
    dkUpdateBanner(u);
    const out = document.getElementById('dk-update-state');
    if (out) out.innerHTML = dkUpdateText(u);
    return u;
  } catch { return null; }
}
if (!DK_PANE) document.addEventListener('DOMContentLoaded', () => { setTimeout(dkUpdatePoll, 2500); setInterval(dkUpdatePoll, 10 * 60 * 1000); });
ACTIONS['dk-update-restart'] = async () => { toast('Restarting…', 'ok'); await dapi('POST', '/api/local/update/restart'); };
ACTIONS['dk-update-reload'] = () => location.reload();
ACTIONS['dk-update-later'] = () => { const bar = document.getElementById('dk-update'); if (bar) { DK.update.dismissed = bar.dataset.key; bar.remove(); } };
ACTIONS['dk-update-check'] = async () => {
  const out = document.getElementById('dk-update-state');
  if (out) out.innerHTML = '<span class="spinner"></span> Looking for updates…';
  const poll = setInterval(dkUpdatePoll, 1000);
  try { await dapi('POST', '/api/local/update/check'); } catch (e) { toast(e.message, 'bad'); } finally { clearInterval(poll); }
  const u = await dkUpdatePoll();
  if (u && u.state === 'current') toast('Agentic Desktop is up to date', 'ok');
  rerender();
};
ACTIONS['dk-update-install'] = async () => {
  const poll = setInterval(dkUpdatePoll, 1000);
  try { await dapi('POST', '/api/local/update/install'); } catch (e) { toast(e.message, 'bad'); } finally { clearInterval(poll); }
  await dkUpdatePoll(); rerender();
};

ACTIONS['dk-pc-menu'] = async () => {
  const m = $('#dk-pc-menu');
  if (!m.classList.contains('hidden')) { m.classList.add('hidden'); return; }
  $('#dk-skills-menu')?.classList.add('hidden');
  await refreshMcpBadge();
  const p = chatPrefs(), sel = selectedMcp(p);
  let info = {}; try { info = await dapi('GET', '/api/local/info'); } catch { /* ignore */ }
  const off = info.localToolsMode === 'off';
  m.innerHTML = `<div class="menu-title">On this computer</div>
    <label class="tool-opt${off ? ' off' : ''}"><input type="checkbox" data-change="dk-pc" data-key="local"${p.local && !off ? ' checked' : ''}${off ? ' disabled' : ''}>
      <span><b>${dkIcon('cmd')} Files &amp; commands</b> <span class="muted small">${off ? 'switched off in Desktop settings' : 'approvals follow the permission mode'}</span><span class="cell-sub">Read and edit files in ${esc((info.workspaces || []).join('; ') || 'your workspace')}, run commands, use the Agentic API as you.</span></span></label>
    <hr><div class="menu-title">Local MCP servers</div>` +
    (DK.mcp.length ? DK.mcp.map(s => `<label class="tool-opt${s.status.connected && s.enabled ? '' : ' off'}"><input type="checkbox" data-change="dk-pc-mcp" value="${esc(s.status.id)}"${sel.includes(s.status.id) ? ' checked' : ''}${s.enabled ? '' : ' disabled'}>
        <span><b>${esc(s.status.name)}</b> <span class="muted small">${!s.enabled ? 'off' : s.status.connected ? num(s.status.toolCount) + ' tools' : s.status.connecting ? 'connecting…' : 'not connected'}</span>${s.requireApproval ? '<span class="cell-sub">always asks before each tool call</span>' : ''}</span></label>`).join('')
      : `<div class="muted small dk-menu-empty">None yet. <a href="/local/mcp" data-nav>Add a server</a></div>`);
  m.classList.remove('hidden');
  setTimeout(() => document.addEventListener('click', function close(ev) { if (!ev.target.closest('.dk-pc-wrap')) { m.classList.add('hidden'); document.removeEventListener('click', close); } }), 0);
};
function updateChips() {
  const b = $('#dk-pc-btn'), k = $('#dk-skills-btn');
  if (b) { const n = pcCount(); b.classList.toggle('on', n > 0); b.innerHTML = `${dkIcon('pc')} This PC${n ? ` <b>${n}</b>` : ''}`; }
  if (k) { const n = skillCount(); k.classList.toggle('on', n > 0); k.innerHTML = `${dkIcon('skill')} Skills${n ? ` <b>${n}</b>` : ''}`; }
}
CHANGES['dk-pc'] = el => { const p = chatPrefs(); p[el.dataset.key] = el.checked; saveChatPrefs(p); updateChips(); if (typeof bounce === 'function') bounce($('#dk-pc-btn')); };
CHANGES['dk-pc-mcp'] = el => {
  const p = chatPrefs(); const set = new Set(selectedMcp(p));
  if (el.checked) set.add(el.value); else set.delete(el.value);
  p.mcp = [...set]; saveChatPrefs(p); updateChips();
};

// ------------------------------------------------------------------ chat: the Skills panel (turn skills on or off, run one)
function skillsMenuHtml(q) {
  const p = chatPrefs(), query = (q || '').toLowerCase();
  const match = s => !query || s.name.toLowerCase().includes(query) || (s.description || '').toLowerCase().includes(query);
  const skills = DK.skills.filter(s => s.kind === 'skill' && match(s)), cmds = DK.skills.filter(s => s.kind === 'command' && match(s));
  const row = (s, i) => `<div class="dk-sk-row${s.kind === 'skill' && (p.disabledSkills.includes(s.name) || !p.skills) ? ' off' : ''}" data-i="${Math.min(i, 30)}">
      ${s.kind === 'skill' ? `<label class="dk-switch" data-tip="${p.disabledSkills.includes(s.name) ? 'Off: the model will not load it' : 'On: the model loads it when your request matches'}"><input type="checkbox" data-change="dk-skill-on" value="${esc(s.name)}"${p.disabledSkills.includes(s.name) ? '' : ' checked'}${p.skills ? '' : ' disabled'}><span></span></label>` : `<span class="dk-sk-cmd">${dkIcon('cmd')}</span>`}
      <div class="dk-sk-text"><b>/${esc(s.name)}</b><span>${esc((s.description || '').slice(0, 150))}</span></div>
      <button type="button" class="btn xs" data-act="dk-skill-insert" data-name="${esc(s.name)}" data-tip="Run it in this message">Use</button></div>`;
  return `<div class="dk-sk-head"><label class="dk-switch dk-sk-master"><input type="checkbox" data-change="dk-skills-master"${p.skills ? ' checked' : ''}><span></span><b>Skills in this chat</b></label>
      <a class="small" href="/local/skills" data-nav>Manage</a></div>
    <div class="chat-search dk-sk-search">${icon('search')}<input id="dk-sk-q" placeholder="Search ${num(DK.skills.length)} skills and commands" value="${esc(q || '')}" autocomplete="off"></div>
    <div class="dk-sk-list" id="dk-sk-list">${skills.length ? `<div class="menu-title">Skills · loaded when your request matches</div>${skills.map(row).join('')}` : ''}
      ${cmds.length ? `<div class="menu-title">Slash commands · run with /name</div>${cmds.map((s, i) => row(s, skills.length + i)).join('')}` : ''}
      ${!skills.length && !cmds.length ? `<div class="muted small dk-menu-empty">${query ? 'Nothing matches.' : 'No skills yet.'} <a href="/local/skills" data-nav>Add skills</a></div>` : ''}</div>`;
}
ACTIONS['dk-skills-menu'] = async () => {
  const m = $('#dk-skills-menu');
  if (!m.classList.contains('hidden')) { m.classList.add('hidden'); return; }
  $('#dk-pc-menu')?.classList.add('hidden');
  await loadSkills().catch(() => []);
  m.innerHTML = skillsMenuHtml('');
  applyRich && applyRich(m);
  m.classList.remove('hidden');
  const q = $('#dk-sk-q');
  q.addEventListener('input', () => { $('#dk-sk-list').outerHTML = new DOMParser().parseFromString(`<div>${skillsMenuHtml(q.value)}</div>`, 'text/html').querySelector('#dk-sk-list').outerHTML; applyRich && applyRich(m); });
  q.focus();
  setTimeout(() => document.addEventListener('click', function close(ev) { if (!ev.target.closest('.dk-skills-wrap')) { m.classList.add('hidden'); document.removeEventListener('click', close); } }), 0);
};
CHANGES['dk-skills-master'] = el => {
  const p = chatPrefs(); p.skills = el.checked; saveChatPrefs(p); updateChips();
  const m = $('#dk-skills-menu'); m.innerHTML = skillsMenuHtml($('#dk-sk-q') ? $('#dk-sk-q').value : ''); applyRich && applyRich(m);
};
CHANGES['dk-skill-on'] = el => {
  const p = chatPrefs(), set = new Set(p.disabledSkills);
  if (el.checked) set.delete(el.value); else set.add(el.value);
  p.disabledSkills = [...set]; saveChatPrefs(p); updateChips();
  el.closest('.dk-sk-row').classList.toggle('off', !el.checked);
};
ACTIONS['dk-skill-insert'] = el => {
  const input = $('#chat-input');
  $('#dk-skills-menu').classList.add('hidden');
  input.value = '/' + el.dataset.name + ' ' + input.value.replace(/^\/\S*\s*/, '');
  input.dispatchEvent(new Event('input'));
  input.focus();
  input.setSelectionRange(input.value.length, input.value.length);
};

// ------------------------------------------------------------------ chat: /skill and /command completion
document.addEventListener('chat:mounted', () => {
  const input = $('#chat-input'), composer = $('#composer');
  if (!input || input.dataset.dk) return;
  input.dataset.dk = '1';
  loadSkills().then(updateChips).catch(() => { });
  refreshMcpBadge().then(updateChips);
  const prefill = sessionStorage.getItem('desktop.prefill');
  if (prefill) { sessionStorage.removeItem('desktop.prefill'); input.value = prefill; input.dispatchEvent(new Event('input')); input.focus(); }
  let pop = null, items = [], active = 0, prompts = [];
  // The account's saved prompts (Chat → saved prompts, from the web app's chatx.js when the server has them).
  const loadSaved = () => { if (typeof loadPrompts === 'function') loadPrompts(true).then(p => { prompts = p || []; }).catch(() => { }); };
  loadSaved();
  document.addEventListener('click', ev => { if (ev.target.closest('[data-act="prompt-save"], [data-act="prompt-del"]')) setTimeout(loadSaved, 800); });
  const close = () => { if (pop) { pop.remove(); pop = null; } };
  const choose = i => {
    const s = items[i]; if (!s) return;
    // Built-in chat commands (/goal, /plan, /model …, from the web app's chatcmds.js) run or wait for their words.
    if (s.kind === 'builtin') { close(); input.focus(); window.pickChatCommand(s.cmd); return; }
    input.value = s.kind === 'prompt' ? s.content : '/' + s.name + ' ';
    close(); input.focus(); input.dispatchEvent(new Event('input'));
  };
  const render = () => {
    const m = input.value.match(/^\/([\w:.-]*)$/);
    if (!m) { close(); return; }
    const q = m[1].toLowerCase();
    const saved = prompts.filter(p => p.command.startsWith(q) || (q && (p.title || '').toLowerCase().includes(q)))
      .map(p => ({ name: p.command, description: p.title || p.content, kind: 'prompt', content: p.content }));
    // A Claude Code chat lists Claude's own commands and skills (as it reported them), which go to it as typed.
    const claudeChat = typeof isExternal === 'function' && isExternal(CHAT.model);
    const pool = claudeChat && DK.claude.commands.length ? DK.claude.commands.map(n => ({ name: n, description: 'Claude Code command or skill', kind: 'claude' })) : DK.skills;
    const skills = pool.filter(s => s.name.toLowerCase().includes(q) || (s.description || '').toLowerCase().includes(q))
      .sort((a, b) => (a.name.toLowerCase().startsWith(q) ? 0 : 1) - (b.name.toLowerCase().startsWith(q) ? 0 : 1));
    const builtins = typeof window.chatCommandHits === 'function' && window.pickChatCommand
      ? window.chatCommandHits(q).slice(0, 6).map(c => ({ name: c.name, description: c.desc, argumentHint: c.args, kind: 'builtin', cmd: c })) : [];
    // A skill or command of this computer with a built-in's name is listed after it (the built-in runs when typed).
    items = builtins.concat(saved, skills).slice(0, 12);
    if (!items.length) { close(); return; }
    active = Math.min(active, items.length - 1);
    if (!pop) { pop = document.createElement('div'); pop.className = 'dk-slash'; composer.parentElement.insertBefore(pop, composer); }
    pop.innerHTML = `<div class="dk-slash-head">${dkIcon('skill')} ${builtins.length ? 'Commands, ' : ''}${saved.length ? 'saved prompts, skills &amp; commands' : 'skills &amp; commands'} <span class="muted">↑↓ choose · Enter / Tab insert · Esc close</span></div>` +
      items.map((s, i) => `<button class="dk-slash-item${i === active ? ' active' : ''}" data-i="${i}"><span class="dk-slash-name">/${esc(s.name)}${s.argumentHint ? ` <span class="muted">${esc(s.argumentHint)}</span>` : ''}</span>
        <span class="dk-slash-desc">${esc((s.description || '').slice(0, 140))}</span><span class="badge ${s.kind === 'skill' ? 'accent' : s.kind === 'builtin' ? 'ok' : s.kind === 'prompt' ? 'info' : 'outline'}">${s.kind === 'prompt' ? 'saved prompt' : s.kind === 'builtin' ? 'chat' : s.kind}</span></button>`).join('');
    $$('.dk-slash-item', pop).forEach(b => b.addEventListener('mousedown', ev => { ev.preventDefault(); choose(Number(b.dataset.i)); }));
  };
  input.addEventListener('input', () => { active = 0; render(); });
  input.addEventListener('blur', () => setTimeout(close, 150));
  // Capture on the composer so the keys never reach the "Enter sends" handler while the list is open.
  composer.addEventListener('keydown', ev => {
    if (!pop || ev.target !== input) return;
    const down = dkKeyIs(ev, 'list.next', 'ArrowDown');
    if (down || dkKeyIs(ev, 'list.prev', 'ArrowUp')) { ev.preventDefault(); ev.stopPropagation(); active = (active + (down ? 1 : items.length - 1)) % items.length; render(); }
    else if (dkKeyIs(ev, 'list.pick', 'Enter', 'Tab')) { ev.preventDefault(); ev.stopPropagation(); choose(active); }
    else if (dkKeyIs(ev, 'list.close', 'Escape')) { ev.stopPropagation(); close(); }
  }, true);
});

// ------------------------------------------------------------------ page: Skills
function skillCard(s, i) {
  const hay = `${s.name} ${s.description} ${s.source} ${s.kind}`.toLowerCase();
  return `<button class="dk-skill" data-act="dk-skill-open" data-id="${esc(s.id)}" data-s="${esc(hay)}" data-source="${esc(s.source)}" data-kind="${esc(s.kind)}" data-i="${Math.min(i, 24)}">
    <div class="dk-skill-top"><span class="dk-skill-icon ${s.kind === 'skill' ? 'k-skill' : 'k-cmd'}">${dkIcon(s.kind === 'skill' ? 'skill' : 'cmd')}</span>
      <span class="dk-skill-name">/${esc(s.name)}</span><span class="spacer"></span><span class="badge ${s.kind === 'skill' ? 'accent' : 'outline'}">${s.kind}</span></div>
    <p class="dk-skill-desc">${esc(s.description || 'No description.')}</p>
    <div class="dk-skill-foot"><span class="badge outline">${esc(s.source)}</span>${s.kind === 'skill' && s.fileCount ? `<span class="muted small">${num(s.fileCount)} file${s.fileCount === 1 ? '' : 's'}</span>` : ''}${s.writable ? '' : '<span class="muted small">read-only</span>'}</div></button>`;
}
VIEWS.localSkills = {
  async render() {
    setCrumbs([['This computer', ''], ['Skills', '/local/skills']]);
    const items = await loadSkills(true);
    const skills = items.filter(s => s.kind === 'skill'), commands = items.filter(s => s.kind === 'command');
    const sources = [...new Set(items.map(s => s.source))];
    const f = DK.skillFilter;
    return pageHead('Skills', {
      subtitle: 'Claude Code skills and slash commands on this computer. In Chat the model loads a skill when your request matches it, and you can run one yourself by typing <b>/name</b>.',
      actions: `<button class="btn" data-act="dk-skill-import">${icon('download')} Import .zip</button><input type="file" accept=".zip" id="dk-skill-zip" data-change="dk-skill-zip" hidden>
        <button class="btn" data-act="dk-skill-new" data-kind="command">${dkIcon('cmd')} New command</button><button class="btn primary" data-act="dk-skill-new" data-kind="skill">${icon('plus')} New skill</button>`
    }) +
      `<div class="grid kpis">${kpi('Skills', num(skills.length), 'loaded when a request matches')}${kpi('Slash commands', num(commands.length), 'run with /name')}${kpi('Sources', num(sources.length), esc(sources.join(' · ') || 'none'))}${kpi('Claude Code', DK.roots && DK.roots.some(r => r.source === 'Claude Code' && r.exists) ? 'Linked' : 'Not found', 'skills in %USERPROFILE%\\.claude')}</div>` +
      card('Library', `<div class="dk-toolbar"><div class="chat-search dk-search">${icon('search')}<input id="dk-skill-q" placeholder="Search skills and commands" value="${esc(f.q)}" autocomplete="off"></div>
          <select id="dk-skill-source" data-change="dk-skill-filter"><option value="">All sources</option>${sources.map(s => `<option${f.source === s ? ' selected' : ''}>${esc(s)}</option>`).join('')}</select>
          <select id="dk-skill-kind" data-change="dk-skill-filter"><option value="">Skills and commands</option><option value="skill"${f.kind === 'skill' ? ' selected' : ''}>Skills</option><option value="command"${f.kind === 'command' ? ' selected' : ''}>Commands</option></select>
          <span class="spacer"></span><button class="btn small ghost" data-act="dk-skill-refresh">Reload</button></div>
        ${items.length ? `<div class="dk-skill-grid" id="dk-skill-grid">${items.map(skillCard).join('')}</div>` : emptyState('No skills yet', 'Create one, import a .zip, or add Claude Code skills to %USERPROFILE%\\.claude\\skills.')}`, { cls: 'section' }) +
      card('Where skills come from', table([
        { h: 'Source', f: r => `<b>${esc(r.source)}</b>` }, { h: 'Kind', f: r => esc(r.kind) },
        { h: 'Folder', f: r => `<code>${esc(r.path)}</code>` }, { h: '', cls: 'nw', f: r => r.exists ? badge('found', 'ok') : badge('missing', 'outline') }
      ], DK.roots || []), { cls: 'section', hint: 'add folders in Desktop settings', actions: `<button class="btn xs" data-act="dk-open" data-target="skills">Open skills folder</button><button class="btn xs" data-act="dk-open" data-target="claude">Open .claude</button>` });
  },
  after() {
    applyRich && applyRich($('#main'));
    const q = $('#dk-skill-q'); if (q) q.addEventListener('input', () => { DK.skillFilter.q = q.value; filterSkills(); });
    filterSkills();
  }
};
function filterSkills() {
  const f = DK.skillFilter, q = (f.q || '').toLowerCase().trim();
  $$('#dk-skill-grid .dk-skill').forEach(c => c.classList.toggle('hidden', (q && !c.dataset.s.includes(q)) || (f.source && c.dataset.source !== f.source) || (f.kind && c.dataset.kind !== f.kind)));
}
CHANGES['dk-skill-filter'] = () => { DK.skillFilter.source = $('#dk-skill-source').value; DK.skillFilter.kind = $('#dk-skill-kind').value; filterSkills(); };
ACTIONS['dk-skill-refresh'] = async () => { await dapi('GET', '/api/local/skills/refresh'); DK.skillsAt = 0; rerender(); toast('Skills reloaded', 'ok'); };
ACTIONS['dk-open'] = async el => { await dapi('POST', '/api/local/open', { target: el.dataset.target, id: el.dataset.id }); };
ACTIONS['dk-skill-open'] = async el => {
  const d = await dapi('GET', '/api/local/skills/' + enc(el.dataset.id));
  const s = d.skill;
  const files = d.files || [];
  openModal('/' + s.name, `<div class="dk-skill-detail">
      <div class="dk-skill-meta"><span class="badge ${s.kind === 'skill' ? 'accent' : 'outline'}">${s.kind}</span><span class="badge outline">${esc(s.source)}</span>${s.version ? `<span class="badge outline">v${esc(s.version)}</span>` : ''}${s.argumentHint ? `<span class="muted small">arguments: ${esc(s.argumentHint)}</span>` : ''}</div>
      <p class="dk-lead">${esc(s.description || 'No description.')}</p>
      <div class="dk-path">${dkIcon('folder')} <code>${esc(s.location)}</code></div>
      ${(s.allowedTools || []).length ? `<div class="small">Allowed tools: ${chips(s.allowedTools)}</div>` : ''}
      ${files.length ? `<details class="dk-files"><summary>${num(files.length)} file${files.length === 1 ? '' : 's'} in the skill</summary><div class="dk-file-list">${files.map(f => `<button class="dk-file" data-act="dk-skill-file" data-id="${esc(s.id)}" data-path="${esc(f.path)}"><span>${esc(f.path)}</span><span class="muted small">${esc(fileSize(f.size))}</span></button>`).join('')}</div></details>` : ''}
      <div class="dk-skill-body md answer">${typeof chatMarkdown === 'function' ? chatMarkdown(d.body || '') : `<pre>${esc(d.body || '')}</pre>`}</div></div>`,
    `${s.source === 'Desktop' ? `<button class="btn danger" data-act="dk-skill-delete" data-id="${esc(s.id)}">Delete</button>` : ''}<span class="spacer"></span>
     <button class="btn" data-act="dk-open" data-target="skill" data-id="${esc(s.id)}">Open folder</button>
     ${s.writable ? `<button class="btn" data-act="dk-skill-edit" data-id="${esc(s.id)}">Edit</button>` : s.kind === 'skill' ? `<button class="btn" data-act="dk-skill-copy" data-id="${esc(s.id)}">Copy to my skills</button>` : ''}
     <button class="btn primary" data-act="dk-skill-use" data-name="${esc(s.name)}">Use in chat</button>`, 'wide');
  applyRich && applyRich($('#modal-card'));
};
ACTIONS['dk-skill-file'] = async el => {
  const res = await fetch(`/api/local/skills/${enc(el.dataset.id)}/file?path=${enc(el.dataset.path)}`, { headers: { 'X-Desktop-Token': DK.token } });
  const text = await res.text();
  const box = el.closest('.dk-files');
  const prev = $('.dk-file-view', box); if (prev) prev.remove();
  box.insertAdjacentHTML('beforeend', `<div class="dk-file-view"><div class="dk-file-view-head"><b>${esc(el.dataset.path)}</b></div><pre class="dk-code">${esc(text.length > 200000 ? text.slice(0, 200000) + '\n…' : text)}</pre></div>`);
};
ACTIONS['dk-skill-use'] = el => { closeModal(); sessionStorage.setItem('desktop.prefill', '/' + el.dataset.name + ' '); navigate('/chat'); };
ACTIONS['dk-skill-copy'] = async el => { const r = await dapi('POST', '/api/local/skills/' + enc(el.dataset.id) + '/copy'); closeModal(); DK.skillsAt = 0; toast('Copied to your skills: /' + r.skill.name, 'ok'); rerender(); };
ACTIONS['dk-skill-delete'] = async el => {
  if (!await confirmBox('Delete', 'Delete this skill or command from your computer?', { danger: true, confirm: 'Delete' })) return;
  await dapi('DELETE', '/api/local/skills/' + enc(el.dataset.id)); DK.skillsAt = 0; toast('Deleted', 'ok'); rerender();
};
function skillEditor(title, s, kind) {
  openModal(title, `<form class="stack dk-skill-form" data-form="dk-skill-save" data-id="${esc(s ? s.id : '')}" data-kind="${esc(kind)}">
      ${s ? '' : `<label>Name<input name="name" required placeholder="${kind === 'command' ? 'e.g. review-pr' : 'e.g. release-notes'}" pattern="[A-Za-z0-9][A-Za-z0-9 _-]*" autocomplete="off"><span class="help">Run it with /name. Lower-case words joined by hyphens.</span></label>`}
      <label>Description<input name="description" value="${esc(s ? s.description : '')}" placeholder="${kind === 'command' ? 'What the command does' : 'When the model should use this skill — be specific'}" autocomplete="off"></label>
      <label>${kind === 'command' ? 'Prompt' : 'Instructions'}<textarea name="body" rows="16" class="mono" placeholder="${kind === 'command' ? 'The prompt to send. $ARGUMENTS is replaced by what you type after the command.' : '# Steps\n1. …'}">${esc(s ? s.body : '')}</textarea></label>
      <div data-errors></div></form>`,
    `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="dk-skill-submit">${s ? 'Save' : 'Create'}</button>`, 'wide');
}
ACTIONS['dk-skill-submit'] = () => $('#modal-card form').requestSubmit();
ACTIONS['dk-skill-new'] = el => skillEditor(el.dataset.kind === 'command' ? 'New slash command' : 'New skill', null, el.dataset.kind);
ACTIONS['dk-skill-edit'] = async el => { const d = await dapi('GET', '/api/local/skills/' + enc(el.dataset.id)); skillEditor('Edit /' + d.skill.name, Object.assign({}, d.skill, { body: d.body }), d.skill.kind); };
FORMS['dk-skill-save'] = async (f, data) => {
  if (f.dataset.id) await dapi('PUT', '/api/local/skills/' + enc(f.dataset.id), { description: data.description, body: data.body });
  else await dapi('POST', '/api/local/skills', { kind: f.dataset.kind, name: data.name, description: data.description, body: data.body });
  closeModal(); DK.skillsAt = 0; toast('Saved', 'ok'); rerender();
};
ACTIONS['dk-skill-import'] = () => $('#dk-skill-zip').click();
CHANGES['dk-skill-zip'] = async el => {
  const file = el.files[0]; el.value = '';
  if (!file) return;
  try { const r = await dapi('POST', '/api/local/skills/import?name=' + enc(file.name), undefined, file); DK.skillsAt = 0; toast('Imported /' + r.skill.name, 'ok'); rerender(); }
  catch (e) { toast(e.message, 'bad'); }
};

// ------------------------------------------------------------------ page: Local MCP
function mcpState(s) {
  if (!s.enabled) return badge('Off', 'outline');
  if (s.approval === 'ask') return badge('Waiting for you', 'warn');
  if (s.approval === 'denied') return badge('Not allowed', 'outline');
  if (s.status.needsSignIn) return badge('Sign in needed', 'warn');
  if (s.status.connecting) return badge('Connecting', 'info');
  if (s.status.connected) return badge('Connected', 'ok');
  return badge(s.status.err ? 'Error' : 'Not connected', s.status.err ? 'bad' : 'outline');
}
VIEWS.localMcp = {
  live: true,
  async render() {
    setCrumbs([['This computer', ''], ['Local MCP', '/local/mcp']]);
    let d = await dapi('GET', '/api/local/mcp');
    d = await seedRecommended(d);
    DK.mcp = d.servers;
    const up = d.servers.filter(s => s.enabled && s.status.connected);
    const tools = up.reduce((n, s) => n + s.status.toolCount, 0);
    return pageHead('Local MCP servers', {
      subtitle: 'MCP servers that run on this computer (stdio) or that this computer reaches over HTTP: this app\'s own, the Agentic CLI\'s, your workspaces\' <code>.mcp.json</code> (started once you allow them), and the ones you set up in Claude Code, its plugins or Claude Desktop (used as they are; Desktop settings can switch that off). Turn them on for a chat with <b>This PC</b> in the message box; the model calls their tools as <code>mcp__server__tool</code>. Hosted servers that need a sign-in show a Sign in button.',
      actions: `<button class="btn" data-act="dk-open" data-target="mcp">Open mcp.json</button><button class="btn" data-act="dk-mcp-import" data-tip="Servers set up in Claude Code, Claude Desktop, Visual Studio or VS Code">Import…</button><button class="btn primary" data-act="dk-mcp-edit">${icon('plus')} Add server</button>`
    }) +
      `<div class="grid kpis">${kpi('Servers', num(d.servers.length), `${num(d.servers.filter(s => s.enabled).length)} enabled`)}${kpi('Connected', num(up.length), up.length ? 'ready for Chat' : 'none connected')}${kpi('Tools', num(tools), 'from connected servers')}${kpi('Config', 'mcp.json', `<code class="small">${esc(d.configPath)}</code>`)}</div>` +
      (d.servers.length ? `<div class="dk-mcp-grid section">${d.servers.map((s, i) => mcpCard(s, i)).join('')}</div>`
        : card('', emptyState('No local MCP servers', 'Add one (for example npx -y @modelcontextprotocol/server-filesystem C:\\Projects), or import the servers you set up in Claude Code, Claude Desktop, Visual Studio or VS Code.'), { cls: 'section' })) +
      recommendedMcpSection(d.servers);
  }
};
// Well-known MCP servers, one click to add (or a filled-in form when they need a key or a folder). The same list is in
// the Agentic server's MCP page and in the CLI (agentic mcp catalog). Servers marked seed are added to mcp.json once,
// switched off, so they show in the list ready to turn on.
const DK_CATALOG = [
  {"id":"filesystem","name":"Filesystem","category":"Files & code","needs":"Node.js","blurb":"Read, search and edit files in the folders you allow.","command":"npx","args":["-y","@modelcontextprotocol/server-filesystem","{folder}"],"approval":true,"docs":"https://github.com/modelcontextprotocol/servers/tree/main/src/filesystem"},
  {"id":"git","name":"Git","category":"Files & code","needs":"uv (Python)","blurb":"Read a Git repository: status, diffs, log, branches and commits.","command":"uvx","args":["mcp-server-git","--repository","{folder}"],"approval":true,"docs":"https://github.com/modelcontextprotocol/servers/tree/main/src/git"},
  {"id":"github","name":"GitHub","category":"Files & code","needs":"a GitHub token","blurb":"GitHub's official server: repositories, issues, pull requests, code search and Actions.","url":"https://api.githubcopilot.com/mcp/","bearer":"GITHUB_TOKEN","approval":true,"env":[{"key":"GITHUB_TOKEN","hint":"a personal access token (github.com/settings/tokens)"}],"docs":"https://github.com/github/github-mcp-server"},
  {"id":"desktop-commander","name":"Desktop Commander","category":"Files & code","needs":"Node.js","blurb":"Terminal commands, processes and file editing on the computer it runs on.","command":"npx","args":["-y","@wonderwhy-er/desktop-commander"],"approval":true,"docs":"https://github.com/wonderwhy-er/DesktopCommanderMCP"},
  {"id":"playwright","name":"Playwright browser","category":"Browser & web","needs":"Node.js 20+","blurb":"Microsoft's browser automation: open pages, click, type, fill forms and read pages through the accessibility tree.","command":"npx","args":["-y","@playwright/mcp@latest"],"approval":true,"docs":"https://github.com/microsoft/playwright-mcp"},
  {"id":"chrome-devtools","name":"Chrome DevTools","category":"Browser & web","needs":"Node.js 20.19+ and Chrome","blurb":"Google's server for Chrome: drive the browser, read the console and network, record performance traces.","command":"npx","args":["-y","chrome-devtools-mcp@latest"],"approval":true,"docs":"https://github.com/ChromeDevTools/chrome-devtools-mcp"},
  {"id":"fetch","name":"Fetch","category":"Browser & web","needs":"uv (Python)","blurb":"Download a web page and turn it into Markdown for the model.","command":"uvx","args":["mcp-server-fetch"],"docs":"https://github.com/modelcontextprotocol/servers/tree/main/src/fetch"},
  {"id":"firecrawl","name":"Firecrawl","category":"Browser & web","needs":"Node.js 22+ and a Firecrawl API key","blurb":"Scrape, crawl and extract structured data from websites, including JavaScript-heavy ones.","command":"npx","args":["-y","firecrawl-mcp"],"env":[{"key":"FIRECRAWL_API_KEY","hint":"from firecrawl.dev"}],"docs":"https://github.com/mendableai/firecrawl-mcp-server"},
  {"id":"brave-search","name":"Brave Search","category":"Search","needs":"Node.js and a Brave Search API key","blurb":"Web, news, image and local search through Brave's independent index.","command":"npx","args":["-y","@brave/brave-search-mcp-server"],"env":[{"key":"BRAVE_API_KEY","hint":"from brave.com/search/api"}],"docs":"https://github.com/brave/brave-search-mcp-server"},
  {"id":"tavily","name":"Tavily","category":"Search","needs":"Node.js and a Tavily API key","blurb":"Search built for AI agents, with page extraction and site crawling.","command":"npx","args":["-y","tavily-mcp@latest"],"env":[{"key":"TAVILY_API_KEY","hint":"from tavily.com"}],"docs":"https://github.com/tavily-ai/tavily-mcp"},
  {"id":"exa","name":"Exa","category":"Search","needs":"Node.js 20+ and an Exa API key","blurb":"Neural web search: find pages by meaning, research papers, companies and code.","command":"npx","args":["-y","exa-mcp-server"],"env":[{"key":"EXA_API_KEY","hint":"from dashboard.exa.ai"}],"docs":"https://github.com/exa-labs/exa-mcp-server"},
  {"id":"context7","name":"Context7","category":"Documentation","needs":"Node.js 20+","blurb":"Up-to-date documentation and code examples for thousands of libraries, so code uses current APIs.","command":"npx","args":["-y","@upstash/context7-mcp"],"docs":"https://github.com/upstash/context7"},
  {"id":"microsoft-learn","name":"Microsoft Learn","category":"Documentation","needs":"nothing (hosted)","blurb":"Search and read official Microsoft documentation: .NET, Azure, Windows, Microsoft 365.","url":"https://learn.microsoft.com/api/mcp","docs":"https://github.com/MicrosoftDocs/mcp"},
  {"id":"deepwiki","name":"DeepWiki","category":"Documentation","needs":"nothing (hosted)","blurb":"Ask questions about any public GitHub repository and read its generated wiki.","url":"https://mcp.deepwiki.com/mcp","docs":"https://docs.devin.ai/work-with-devin/deepwiki-mcp"},
  {"id":"aws-docs","name":"AWS Documentation","category":"Documentation","needs":"uv (Python)","blurb":"Search and read AWS documentation and get recommendations for related pages.","command":"uvx","args":["awslabs.aws-documentation-mcp-server@latest"],"docs":"https://github.com/awslabs/mcp"},
  {"id":"notion","name":"Notion","category":"Productivity","needs":"Node.js and a Notion integration token","blurb":"Search, read and update pages and databases in your Notion workspace.","command":"npx","args":["-y","@notionhq/notion-mcp-server"],"approval":true,"env":[{"key":"NOTION_TOKEN","hint":"an internal integration token (notion.so/my-integrations)"}],"docs":"https://github.com/makenotion/notion-mcp-server"},
  {"id":"supabase","name":"Supabase","category":"Data","needs":"Node.js and a Supabase access token","blurb":"Query and manage Supabase projects: tables, SQL, migrations, logs (read-only by default here).","command":"npx","args":["-y","@supabase/mcp-server-supabase@latest","--read-only"],"approval":true,"env":[{"key":"SUPABASE_ACCESS_TOKEN","hint":"a personal access token (supabase.com/dashboard/account/tokens)"}],"docs":"https://github.com/supabase-community/supabase-mcp"},
  {"id":"memory","name":"Memory","category":"Thinking","needs":"Node.js","blurb":"A knowledge graph the model can store facts in and recall across sessions.","command":"npx","args":["-y","@modelcontextprotocol/server-memory"],"docs":"https://github.com/modelcontextprotocol/servers/tree/main/src/memory"},
  {"id":"sequential-thinking","name":"Sequential Thinking","category":"Thinking","needs":"Node.js","blurb":"Structured step-by-step reasoning the model can revise and branch for hard problems.","command":"npx","args":["-y","@modelcontextprotocol/server-sequential-thinking"],"docs":"https://github.com/modelcontextprotocol/servers/tree/main/src/sequentialthinking"},
  {"id":"time","name":"Time","category":"Thinking","needs":"uv (Python)","blurb":"The current time in any time zone, and conversions between zones.","command":"uvx","args":["mcp-server-time"],"docs":"https://github.com/modelcontextprotocol/servers/tree/main/src/time"},
  {"id":"rea","name":"REA — reverse engineering","category":"Security","needs":"Node.js 22+; optional Ghidra, Hopper or IDA","blurb":"Reverse-engineer native binaries, .NET assemblies, JS/Electron apps, Android APKs and websites: pseudocode, disassembly, modules and behaviour.","command":"npx","args":["-y","rea-agents@6.3.0","mcp"],"approval":true,"seed":true,"docs":"https://github.com/morluto/rea"}
];
const DK_RECOMMENDED = DK_CATALOG.filter(r => r.seed);
const catalogNeedsSetup = r => !!(r.env && r.env.length) || (r.args || []).includes('{folder}');
/** A catalog entry as this app's server settings; {folder} becomes the first workspace folder. */
function catalogConfig(r, folder) {
  const env = Object.fromEntries((r.env || []).filter(e => !r.bearer || e.key !== r.bearer).map(e => [e.key, '']));
  return r.url
    ? { id: r.id, name: r.name, transport: 'http', endpoint: r.url, headers: r.bearer ? { Authorization: 'Bearer <paste your ' + r.bearer + ' here>' } : {}, env, requireApproval: !!r.approval, timeoutSeconds: 60 }
    : { id: r.id, name: r.name, transport: 'stdio', command: r.command, args: r.args.map(a => a === '{folder}' ? (folder || '') : a), env, requireApproval: !!r.approval, timeoutSeconds: 60 };
}
// On first visit, add each seed server to mcp.json switched OFF, so it shows in the list ready to turn on (a disabled
// server never starts a process). Done once per server (a localStorage flag); removing it is remembered too, so it is
// not re-added, and it reappears in the catalog instead.
const recSeedKey = id => 'dk.rec.seeded.' + id;
function recSeeded(id) { try { return !!localStorage.getItem(recSeedKey(id)); } catch { return false; } }
function markRecSeeded(id) { try { localStorage.setItem(recSeedKey(id), '1'); } catch { /* ignore */ } }
async function seedRecommended(d) {
  const have = new Set((d.servers || []).map(s => s.status.id.toLowerCase()));
  let changed = false;
  for (const r of DK_RECOMMENDED) {
    if (have.has(r.id.toLowerCase()) || recSeeded(r.id)) continue;
    try {
      await dapi('POST', '/api/local/mcp', Object.assign(catalogConfig(r), { enabled: false }));
      markRecSeeded(r.id);
      changed = true;
    } catch { /* leave it for the catalog */ }
  }
  return changed ? await dapi('GET', '/api/local/mcp') : d;
}
function recommendedMcpSection(servers) {
  const have = new Set((servers || []).map(s => s.status.id.toLowerCase()));
  const recs = DK_CATALOG.filter(r => !have.has(r.id.toLowerCase()));
  if (!recs.length) return '';
  const groups = [...new Set(recs.map(r => r.category))];
  const item = r => `<div class="dk-rec">
      <div class="grow"><div class="cell-title">${esc(r.name)} ${r.url ? '<span class="badge outline">hosted</span>' : ''}</div><p class="small muted">${esc(r.blurb)}</p>
        <div class="dk-rec-cmd"><code>${esc(r.url || (r.command + ' ' + r.args.join(' ')))}</code></div>
        <div class="cell-sub">Needs ${esc(r.needs)} · <a href="${esc(r.docs)}" target="_blank" rel="noopener noreferrer">docs</a></div></div>
      <button class="btn ${catalogNeedsSetup(r) ? '' : 'primary '}dk-rec-add" data-act="dk-mcp-recommend" data-id="${esc(r.id)}">${catalogNeedsSetup(r) ? 'Set up…' : icon('plus') + ' Add'}</button></div>`;
  return card('Add a well-known server', groups.map(g => `<div class="menu-title dk-cat-title">${esc(g)}</div><div class="dk-rec-list">${recs.filter(r => r.category === g).map(item).join('')}</div>`).join(''),
    { cls: 'section', hint: 'Servers that change things ask before each tool call; turn that off per server once you trust it. Set up… opens the form to fill in a key or a folder.' });
}
ACTIONS['dk-mcp-recommend'] = async el => {
  const r = DK_CATALOG.find(x => x.id === el.dataset.id);
  if (!r) return;
  let folder = ''; try { folder = ((await dapi('GET', '/api/local/info')).workspaces || [])[0] || ''; } catch { /* none */ }
  if (catalogNeedsSetup(r)) { DK.preset = catalogConfig(r, folder); ACTIONS['dk-mcp-edit']({ dataset: {} }); return; }
  el.disabled = true; el.textContent = 'Adding…';
  try {
    await dapi('POST', '/api/local/mcp', Object.assign(catalogConfig(r, folder), { enabled: true }));
    toast(`Added ${r.name} — connecting${r.url ? '' : ' (the first run downloads it)'}`, 'ok'); refreshNow(); refreshMcpBadge();
  } catch (e) { el.disabled = false; el.innerHTML = `${icon('plus')} Add`; toast(e.message, 'bad'); }
};
function mcpCard(s, i) {
  const st = s.status, c = s.config;
  const target = c.transport === 'stdio' ? `${c.command || ''} ${(c.args || []).join(' ')}` : c.endpoint;
  return `<div class="card dk-mcp${s.enabled ? '' : ' off'}" data-i="${i}">
    <div class="dk-mcp-head"><span class="dk-mcp-icon">${dkIcon('plug')}</span><div class="grow"><div class="cell-title">${esc(st.name)}</div><code class="muted small">${esc(st.id)}</code> <span class="muted small">· ${esc(s.source || 'This app')}</span></div>${mcpState(s)}</div>
    ${s.approval === 'ask' || s.approval === 'denied' ? `<div class="notice warn small">This server comes from the project's files: allow it only if you trust the project. <button class="btn xs primary" data-act="dk-mcp-approve" data-id="${esc(st.id)}" data-allow="1">Allow</button>${s.approval === 'ask' ? ` <button class="btn xs ghost" data-act="dk-mcp-approve" data-id="${esc(st.id)}" data-allow="">Don't allow</button>` : ''}</div>` : ''}
    ${st.needsSignIn ? `<div class="notice warn small">This server needs you to sign in. <button class="btn xs primary" data-act="dk-mcp-login" data-id="${esc(st.id)}">Sign in</button></div>` : ''}
    <div class="dk-mcp-target"><span class="badge outline">${esc(c.transport)}</span><code>${esc(target)}</code></div>
    ${st.serverInfo ? `<div class="muted small">${esc(st.serverInfo)}${st.calls ? ' · ' + num(st.calls) + ' calls' : ''}${st.lastUsedUtc ? ' · used ' + esc(ago(st.lastUsedUtc)) : ''}</div>` : ''}
    ${st.err && s.enabled && !st.connected ? `<div class="notice bad small dk-mcp-err">${esc(st.err)}</div>` : ''}
    ${st.toolCount ? `<div class="dk-mcp-tools">${st.tools.slice(0, 8).map(t => `<span class="badge outline" data-tip="${esc((t.description || '').slice(0, 200))}">${esc(t.name)}</span>`).join('')}${st.toolCount > 8 ? `<span class="muted small">+${st.toolCount - 8}</span>` : ''}</div>` : ''}
    <div class="dk-mcp-actions">
      <label class="dk-switch" data-tip="Connect this server"><input type="checkbox" data-change="dk-mcp-toggle" data-id="${esc(st.id)}" data-key="enabled"${s.enabled ? ' checked' : ''}><span></span>On</label>
      <label class="dk-switch" data-tip="Ask before each tool call"><input type="checkbox" data-change="dk-mcp-toggle" data-id="${esc(st.id)}" data-key="requireApproval"${s.requireApproval ? ' checked' : ''}><span></span>Ask first</label>
      <span class="spacer"></span>
      ${st.toolCount ? `<button class="btn xs" data-act="dk-mcp-tools" data-id="${esc(st.id)}">Tools</button>` : ''}
      ${st.log && st.log.length ? `<button class="btn xs ghost" data-act="dk-mcp-log" data-id="${esc(st.id)}">Log</button>` : ''}
      <button class="btn xs ghost" data-act="dk-mcp-reconnect" data-id="${esc(st.id)}">Reconnect</button>
      ${c.transport !== 'stdio' && s.signedIn ? `<button class="btn xs ghost" data-act="dk-mcp-logout" data-id="${esc(st.id)}" data-tip="Forget this server's sign-in">Sign out</button>` : ''}
      ${s.editable === false ? '' : `<button class="btn xs ghost" data-act="dk-mcp-edit" data-id="${esc(st.id)}">Edit</button>
      <button class="btn xs ghost danger" data-act="dk-mcp-remove" data-id="${esc(st.id)}">Remove</button>`}</div></div>`;
}
const mcpById = id => DK.mcp.find(s => s.status.id === id);
CHANGES['dk-mcp-toggle'] = async el => {
  await dapi('PATCH', '/api/local/mcp/' + enc(el.dataset.id), { [el.dataset.key]: el.checked });
  toast(el.dataset.key === 'enabled' ? (el.checked ? 'Connecting…' : 'Server switched off') : (el.checked ? 'Will ask before each tool call' : 'Tools run without asking'), 'ok');
  refreshNow(); refreshMcpBadge();
};
ACTIONS['dk-mcp-approve'] = async el => { await dapi('POST', `/api/local/mcp/${enc(el.dataset.id)}/approve`, { allow: !!el.dataset.allow }); toast(el.dataset.allow ? 'Allowed — starting…' : 'Not allowed', 'ok'); refreshNow(); refreshMcpBadge(); };
ACTIONS['dk-mcp-login'] = async el => {
  el.disabled = true; el.textContent = 'Sign in in the browser…';
  try { await dapi('POST', `/api/local/mcp/${enc(el.dataset.id)}/login`); toast('Signed in', 'ok'); } catch (e) { toast(e.message, 'bad'); }
  refreshNow(); refreshMcpBadge();
};
ACTIONS['dk-mcp-logout'] = async el => { await dapi('POST', `/api/local/mcp/${enc(el.dataset.id)}/logout`); toast('Signed out', 'ok'); refreshNow(); refreshMcpBadge(); };
ACTIONS['dk-mcp-reconnect'] = async el => { el.textContent = 'Connecting…'; await dapi('POST', `/api/local/mcp/${enc(el.dataset.id)}/reconnect`); refreshNow(); refreshMcpBadge(); };
ACTIONS['dk-mcp-remove'] = async el => {
  if (!await confirmBox('Remove server', `Remove <b>${esc(el.dataset.id)}</b> from mcp.json?`, { danger: true, confirm: 'Remove' })) return;
  await dapi('DELETE', '/api/local/mcp/' + enc(el.dataset.id));
  // A recommended server the user removes is remembered as seeded, so it is not auto-added again (the card still offers it).
  if (DK_RECOMMENDED.some(r => r.id.toLowerCase() === el.dataset.id.toLowerCase())) markRecSeeded(el.dataset.id);
  toast('Removed', 'ok'); refreshNow(); refreshMcpBadge();
};
ACTIONS['dk-mcp-log'] = el => { const s = mcpById(el.dataset.id); openModal(`${s.status.name} — recent output`, `<pre class="dk-code dk-log">${esc((s.status.log || []).join('\n'))}</pre>`, '', 'wide'); };
ACTIONS['dk-mcp-tools'] = el => {
  const s = mcpById(el.dataset.id);
  openModal(`${s.status.name} — ${num(s.status.toolCount)} tools`, `<div class="dk-tool-list">${s.status.tools.map((t, i) => {
    const props = (t.schema && t.schema.properties) || {};
    const sample = Object.fromEntries(Object.entries(props).map(([k, v]) => [k, v.type === 'number' || v.type === 'integer' ? 0 : v.type === 'boolean' ? false : v.type === 'array' ? [] : v.type === 'object' ? {} : '']));
    return `<details class="dk-tool"><summary><code>${esc(t.name)}</code><span class="muted small">${esc((t.description || '').split('\n')[0].slice(0, 160))}</span></summary>
      <div class="dk-tool-body">${t.description ? `<p class="small">${esc(t.description)}</p>` : ''}
        <label class="small">Arguments (JSON)<textarea rows="5" class="mono" id="dk-try-${i}">${esc(JSON.stringify(sample, null, 2))}</textarea></label>
        <div class="row"><button class="btn small" data-act="dk-mcp-try" data-id="${esc(s.status.id)}" data-tool="${esc(t.name)}" data-i="${i}">Run tool</button></div><div class="dk-try-out" id="dk-try-out-${i}"></div></div></details>`;
  }).join('')}</div>`, '', 'wide');
};
ACTIONS['dk-mcp-try'] = async el => {
  const out = $('#dk-try-out-' + el.dataset.i);
  let args; try { args = JSON.parse($('#dk-try-' + el.dataset.i).value || '{}'); } catch { out.innerHTML = '<div class="notice bad small">The arguments are not valid JSON.</div>'; return; }
  out.innerHTML = '<div class="muted small"><span class="spinner"></span> Running…</div>';
  const r = await dapi('POST', `/api/local/mcp/${enc(el.dataset.id)}/call`, { tool: el.dataset.tool, arguments: args });
  out.innerHTML = `<div class="run-head">${r.ok ? badge('ok', 'ok') : badge('error', 'bad')}<span class="muted small">${num(r.durationMs)} ms</span></div><pre class="dk-code">${esc(r.err || r.content || '(no output)')}</pre>`;
};
ACTIONS['dk-mcp-edit'] = el => {
  // A catalog entry that needs a key or a folder opens this form filled in (DK.preset).
  const s = el.dataset.id ? mcpById(el.dataset.id) : null, preset = s ? null : DK.preset;
  DK.preset = null;
  const c = s ? s.config : preset || { transport: 'stdio', args: [], timeoutSeconds: 60 };
  const kv = (o, sep) => Object.entries(o || {}).map(([k, v]) => k + sep + v).join('\n');
  openModal(s ? 'Edit ' + s.status.name : 'Add an MCP server', `<form class="stack" data-form="dk-mcp-save" data-existing="${esc(s ? s.status.id : '')}">
      <div class="form-grid"><label>Name<input name="name" required value="${esc(c.name || '')}" placeholder="e.g. Filesystem"></label>
        <label>Id<input name="id" value="${esc(c.id || '')}" placeholder="filesystem" ${s ? 'readonly' : ''}><span class="help">Tools are named mcp__id__tool.</span></label></div>
      <label>Connection<select name="transport" data-change="dk-mcp-transport">
        <option value="stdio"${c.transport === 'stdio' ? ' selected' : ''}>Program on this computer (stdio)</option>
        <option value="http"${c.transport === 'http' ? ' selected' : ''}>HTTP (Streamable HTTP)</option>
        <option value="sse"${c.transport === 'sse' ? ' selected' : ''}>HTTP + SSE (older servers)</option></select></label>
      <div class="stack" data-show="stdio"><div class="form-grid"><label>Command<input name="command" value="${esc(c.command || '')}" placeholder="npx, uvx, node, python, C:\\tools\\server.exe"></label>
        <label>Working folder<input name="cwd" value="${esc(c.cwd || '')}" placeholder="optional"></label></div>
        <label>Arguments<textarea name="args" rows="3" class="mono" placeholder="one per line, e.g.&#10;-y&#10;@modelcontextprotocol/server-filesystem&#10;C:\\Projects">${esc((c.args || []).join('\n'))}</textarea></label></div>
      <div class="stack" data-show="http sse"><label>URL<input name="endpoint" value="${esc(c.endpoint || '')}" placeholder="https://example.com/mcp"></label>
        <label>Headers<textarea name="headers" rows="2" class="mono" placeholder="Authorization: Bearer …">${esc(kv(c.headers, ': '))}</textarea></label></div>
      <label>Environment variables<textarea name="env" rows="2" class="mono" placeholder="API_KEY=…  (one per line; %VARS% are expanded)">${esc(kv(c.env, '='))}</textarea></label>
      <div class="form-grid"><label>Timeout (seconds)<input name="timeoutSeconds" type="number" min="5" max="3600" value="${esc(c.timeoutSeconds || 60)}"></label>
        <label class="check"><input type="checkbox" name="requireApproval"${c.requireApproval ? ' checked' : ''}> Ask before each tool call</label></div>
      ${s ? '' : `<details class="dk-paste"><summary>Or paste a Claude config</summary><textarea id="dk-mcp-paste" rows="6" class="mono" placeholder='{ "mcpServers": { "github": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-github"], "env": { "GITHUB_TOKEN": "…" } } } }'></textarea>
        <div class="row"><button type="button" class="btn small" data-act="dk-mcp-paste">Add these servers</button></div></details>`}
      <div data-errors></div></form>`,
    `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="dk-mcp-submit">${s ? 'Save and reconnect' : 'Add and connect'}</button>`, 'wide');
  CHANGES['dk-mcp-transport']($('#modal-card select[name="transport"]'));
};
CHANGES['dk-mcp-transport'] = el => $$('#modal-card [data-show]').forEach(x => x.classList.toggle('hidden', !x.dataset.show.split(' ').includes(el.value)));
ACTIONS['dk-mcp-submit'] = () => $('#modal-card form').requestSubmit();
const lines = t => String(t || '').split('\n').map(x => x.trim()).filter(Boolean);
const pairs = (t, sep) => Object.fromEntries(lines(t).filter(l => l.includes(sep)).map(l => [l.slice(0, l.indexOf(sep)).trim(), l.slice(l.indexOf(sep) + sep.length).trim()]));
FORMS['dk-mcp-save'] = async (f, d) => {
  const s = f.dataset.existing ? mcpById(f.dataset.existing) : null;
  await dapi('POST', '/api/local/mcp', {
    id: d.id || d.name, name: d.name, transport: d.transport, command: d.command, args: lines(d.args), cwd: d.cwd, endpoint: d.endpoint,
    headers: pairs(d.headers, ':'), env: pairs(d.env, '='), timeoutSeconds: Number(d.timeoutSeconds) || 60, requireApproval: !!d.requireApproval, enabled: s ? s.enabled : true
  });
  closeModal(); toast('Saved — connecting', 'ok'); refreshNow(); refreshMcpBadge();
};
ACTIONS['dk-mcp-paste'] = async () => {
  let cfg; try { cfg = JSON.parse($('#dk-mcp-paste').value); } catch { toast('That is not valid JSON.', 'bad'); return; }
  const servers = cfg.mcpServers || (cfg.command || cfg.url ? { server: cfg } : cfg);
  let n = 0;
  for (const [id, c] of Object.entries(servers)) {
    if (!c || typeof c !== 'object') continue;
    const http = !!(c.url || c.endpoint);
    await dapi('POST', '/api/local/mcp', { id, name: id, transport: c.type === 'sse' ? 'sse' : http ? 'http' : 'stdio', command: c.command, args: c.args || [], endpoint: c.url || c.endpoint, headers: c.headers || {}, env: c.env || {}, enabled: true });
    n++;
  }
  closeModal(); toast(`Added ${n} server${n === 1 ? '' : 's'}`, 'ok'); refreshNow(); refreshMcpBadge();
};
ACTIONS['dk-mcp-import'] = async () => {
  const d = await dapi('GET', '/api/local/mcp/found');
  const icon = s => /^Visual Studio/.test(s) ? '<span class="dk-src vs">VS</span>' : /^VS Code/.test(s) ? '<span class="dk-src code">VS Code</span>' : '<span class="dk-src claude">Claude</span>';
  openModal('Import MCP servers', d.servers.length ? `<p class="muted">Servers set up in Claude Code (<code>~\\.claude.json</code>), Claude Desktop, Visual Studio (<code>~\\.mcp.json</code>, a solution's <code>.mcp.json</code> or <code>.vs\\mcp.json</code>) and VS Code (its user <code>mcp.json</code>, settings and a workspace's <code>.vscode\\mcp.json</code>). Workspace folders from Desktop settings are searched. Choose which to add:</p>
      <div class="dk-import-list">${d.servers.map(s => `<label class="tool-opt${s.exists ? ' off' : ''}"><input type="checkbox" class="dk-imp" value="${esc(s.id)}"${s.exists ? ' disabled' : ' checked'}>
        <span><b>${esc(s.name)}</b> <span class="badge outline">${esc(s.transport)}</span> ${icon(s.source)} <span class="muted small">${esc(s.source)}${s.exists ? ' · already added' : ''}</span><span class="cell-sub"><code>${esc(s.command)}</code></span>${s.note ? `<span class="cell-sub warn-text">${esc(s.note)}</span>` : ''}</span></label>`).join('')}</div>`
    : emptyState('Nothing found', 'No MCP servers are set up in Claude Code, Claude Desktop, Visual Studio or VS Code on this computer (workspace folders from Desktop settings were searched too).'),
    d.servers.some(s => !s.exists) ? `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="dk-mcp-import-go">Import</button>` : '', 'wide');
};
ACTIONS['dk-mcp-import-go'] = async () => {
  const ids = $$('#modal-card .dk-imp:checked').map(x => x.value);
  const r = await dapi('POST', '/api/local/mcp/import', { ids });
  closeModal(); toast(`Imported ${r.added.length} server${r.added.length === 1 ? '' : 's'} — connecting`, 'ok'); setTimeout(refreshNow, 800); refreshMcpBadge();
};

// ------------------------------------------------------------------ page: Desktop settings
VIEWS.localSettings = {
  async render() {
    setCrumbs([['This computer', ''], ['Desktop settings', '/local/settings']]);
    const [s, info, upd] = await Promise.all([dapi('GET', '/api/local/settings'), dapi('GET', '/api/local/info'), dapi('GET', '/api/local/update').catch(() => null)]);
    const pages = [['/chat', 'Chat'], ['/', 'Overview'], ['/agents', 'Agents'], ['/operations', 'Operations'], ['/results', 'Results'], ['/global-tasks', 'Global tasks']];
    const on = s.localToolsMode !== 'off';
    const feat = s.features || { hotkeys: true, tray: true, screenshots: true, keyProtection: 'dpapi' };
    const linux = s.platform === 'linux';
    const shellNames = { powershell: 'PowerShell', cmd: 'Command Prompt (cmd)', bash: 'bash', sh: 'sh (POSIX shell)' };
    return pageHead('Desktop settings', { subtitle: `Agentic Desktop ${esc(info.version)} on ${esc(info.machine)} · settings in <code>${esc(s.settingsFile)}</code>`,
      actions: `<button class="btn" data-act="dk-open" data-target="data">Open data folder</button>` }) +
      `<form class="stack dk-settings" data-form="dk-settings">
      ${card('Connection', `<div class="form-grid"><label>API key<input name="apiKey" type="password" placeholder="${esc(s.hasKey ? s.apiKeyMasked + ' (saved — leave empty to keep)' : 'agk_…')}" autocomplete="off"><span class="help">Create one in the web app: user menu → API key. ${feat.keyProtection === 'file' ? 'It is kept in a file only your account can read.' : 'It is stored encrypted for your Windows account.'}</span></label>
          <div class="stack"><label class="dk-switch dk-switch-row"><input type="checkbox" name="useCustomServer" data-change="dk-custom"${s.useCustomServer ? ' checked' : ''}><span></span>Use a different server</label>
            <label class="dk-reveal${s.useCustomServer ? '' : ' hidden'}" id="dk-server">Agentic server<input name="serverBaseUrl" value="${esc(s.serverBaseUrl || '')}" placeholder="${esc(s.defaultServer)}"></label>
            <div class="row dk-reveal${s.useCustomServer ? '' : ' hidden'}" id="dk-test-row"><button type="button" class="btn" data-act="dk-test">Test connection</button><span id="dk-test-out"></span></div></div></div>`, { hint: 'the app shows the server\'s web app and adds the key' })}
      ${card('Updates', `<div class="stack"><div class="row"><span>Agentic Desktop <b>${esc(info.version)}</b></span><span class="muted small" id="dk-update-state">${dkUpdateText(upd)}</span><span class="spacer"></span>
          ${upd && upd.state === 'ready' ? '<button type="button" class="btn primary" data-act="dk-update-restart">Restart to update</button>' : ''}
          ${upd && upd.state === 'available' ? '<button type="button" class="btn primary" data-act="dk-update-install">Install</button>' : ''}
          ${upd && upd.state !== 'dev' ? '<button type="button" class="btn" data-act="dk-update-check">Check now</button>' : ''}</div>
          ${upd && upd.notes && (upd.state === 'ready' || upd.state === 'available') ? `<div class="small">${esc(upd.notes)}</div>` : ''}
          <label class="dk-switch dk-switch-row"><input type="checkbox" name="autoUpdate"${s.autoUpdate ? ' checked' : ''}><span></span>Install updates from the server automatically</label>
          <span class="help">The app checks when it starts and every few hours, downloads the new version in the background and checks it against the server's SHA-256. The desktop features (${linux ? 'www/desktop' : 'www\\desktop'}) update at once; a new app version starts when you restart.</span></div>`, { cls: 'section', hint: 'from your Agentic server' })}
      ${!feat.hotkeys && !feat.tray ? '' : card('Shortcuts & window', `<p class="help">Click a box and press the keys (Backspace empties it).</p><div class="form-grid"><label>New chat from any app<input name="quickEntryHotkey" data-capture-key value="${esc(s.quickEntryHotkey || '')}" placeholder="Ctrl+Alt+Space" autocomplete="off"><span class="help">Brings Agentic up on a new chat, ready to type. Empty = off.</span></label>
          <label>Screenshot to chat<input name="screenshotHotkey" data-capture-key value="${esc(s.screenshotHotkey || '')}" placeholder="Ctrl+Alt+S" autocomplete="off"><span class="help">Drag over part of the screen (${esc(s.screenshotWholeScreenKey || 'Enter')}: the whole screen); it is attached to your next message. Empty = off.</span></label>
          ${feat.screenshots === false ? '' : `<label>Screenshot: whole screen<input name="screenshotWholeScreenKey" data-capture-key value="${esc(s.screenshotWholeScreenKey || 'Enter')}" placeholder="Enter" autocomplete="off"><span class="help">While you choose the area: takes the whole screen under the mouse. Empty = Enter.</span></label>
          <label>Screenshot: cancel<input name="screenshotCancelKey" data-capture-key value="${esc(s.screenshotCancelKey || 'Esc')}" placeholder="Esc" autocomplete="off"><span class="help">While you choose the area: cancels the screenshot. Empty = Esc.</span></label>`}</div>
          ${(s.hotkeyProblems || []).length ? `<div class="notice warn small">${s.hotkeyProblems.map(esc).join('<br>')}</div>` : ''}
          ${typeof ACTIONS !== 'undefined' && ACTIONS['keys-open'] ? `<p class="help">Shortcuts inside the app (send, new chat, the command palette …) are saved to your account: <button type="button" class="btn small" data-act="keys-open">Keyboard shortcuts</button></p>` : ''}
          <label class="dk-switch dk-switch-row"><input type="checkbox" name="closeToTray"${s.closeToTray ? ' checked' : ''}><span></span>Keep running in the notification area when the window is closed</label>
          <span class="help">The icon in the notification area opens Agentic, starts a new chat, takes a screenshot or quits.</span>`, { cls: 'section', hint: 'work from every app' })}
      ${feat.screenshots === false ? '' : card('Screenshots by the model', `<label class="dk-switch dk-switch-row"><input type="checkbox" name="modelScreenshots"${s.modelScreenshots !== false ? ' checked' : ''}><span></span>Let the model in Chat take screenshots of this computer's screen</label>
          <label class="dk-switch dk-switch-row"><input type="checkbox" name="computerUse"${s.computerUse ? ' checked' : ''}><span></span>Let the model use the mouse and keyboard (click, type, switch windows, the clipboard, open apps)</label>
          <span class="help">Computer use: the model looks at a screenshot, then acts on the screen; it gets a new screenshot after each action. Every action asks first in the chat (in Auto mode too) unless you choose Always allow there. Needs screenshots on.</span>
          <span class="help">The model can look at your screen when it helps (take_screenshot). Agentic asks you before every screenshot, in any permission mode, unless you choose Always allow in this chat; the picture is shown in the answer, so you see exactly what it saw.</span>`, { cls: 'section', hint: 'privacy' })}
      ${card('Token usage', `<label class="dk-switch dk-switch-row"><input type="checkbox" name="ccusageLog"${s.ccusageLog !== false ? ' checked' : ''}><span></span>Log each answer's tokens for <code>npx ccusage</code></label>
          <span class="help">Written in Claude Code's log format (projects/agentic-desktop-…), so ccusage reports Agentic answers next to Claude Code's.</span>`, { cls: 'section' })}
      ${card('Start', `<div class="form-grid"><label>Open at start<select name="startPage">${pages.map(([v, l]) => `<option value="${v}"${s.startPage === v ? ' selected' : ''}>${l}</option>`).join('')}</select></label>
          <label>Local port<input name="port" type="number" min="0" max="65535" value="${esc(s.port)}"><span class="help">Fixed so your layout and theme are remembered. Takes effect on the next start.</span></label></div>`, { cls: 'section' })}
      ${card('Files & commands on this computer', `<div class="dk-modes two">${[['off', 'Off', 'The model cannot touch this computer.'], ['ask', 'On', 'Chat can read and change files and run commands. Approvals follow the chat\'s permission mode (Manual, Accept edits or Auto).']].map(([v, l, h]) => `<label class="dk-mode${(v === 'off') === !on ? ' on' : ''}"><input type="radio" name="localToolsMode" value="${v}"${(v === 'off') === !on ? ' checked' : ''} data-change="dk-mode"><b>${l}</b><span>${h}</span></label>`).join('')}</div>
          <div class="form-grid"><label>Workspace folders<textarea name="workspaceFolders" rows="3" class="mono" placeholder="${esc(s.effectiveWorkspaces[0] || '')}">${esc((s.workspaceFolders || []).join('\n'))}</textarea><span class="help">One per line. Files outside them cannot be read or changed; commands start in the first. Empty = your user folder.</span></label>
          <div class="stack"><label>Shell<select name="shell">${(s.shells || ['powershell', 'cmd']).map(v => `<option value="${esc(v)}"${s.shell === v ? ' selected' : ''}>${esc(shellNames[v] || v)}</option>`).join('')}</select></label>
          <label>Command time limit (seconds)<input name="commandTimeoutSeconds" type="number" min="5" max="3600" value="${esc(s.commandTimeoutSeconds)}"></label></div></div>
          <label class="check"><input type="checkbox" name="includeClaudeUserHooks"${s.includeClaudeUserHooks ? ' checked' : ''}> Also run the hooks in my Claude Code settings (<code>${linux ? '~/.claude/settings.json' : '%USERPROFILE%\\.claude\\settings.json'}</code>)</label>
          <label class="check"><input type="checkbox" name="wholeComputer"${s.wholeComputer ? ' checked' : ''}> Let the tools use files anywhere on this computer (off: outside the workspace folders asks first)</label>
          <label class="check"><input type="checkbox" name="includeClaudeMcp"${s.includeClaudeMcp !== false ? ' checked' : ''}> Use the MCP servers I set up in Claude Code (and its plugins) and Claude Desktop, as they are there</label>
          <span class="help">Hooks in <code>.agentic/settings.json</code> and in a project's <code>.claude/settings.json</code> always run.</span>`, { cls: 'section', hint: 'used by Chat when "Files & commands" is on under This PC' })}
      ${card('Skills', `<div class="stack"><label class="check"><input type="checkbox" name="includeClaudeCodeSkills"${s.includeClaudeCodeSkills ? ' checked' : ''}> Use my Claude Code skills and commands (<code>${linux ? '~/.claude/skills' : '%USERPROFILE%\\.claude\\skills'}</code>, <code>${linux ? '/commands' : '\\commands'}</code>)</label>
          <label class="check"><input type="checkbox" name="includeClaudePlugins"${s.includeClaudePlugins ? ' checked' : ''}> Use skills and commands from installed Claude Code plugins</label>
          <label>More skill folders<textarea name="skillFolders" rows="3" class="mono" placeholder="${linux ? '~/projects/my-app  (its .claude/skills is used)' : 'C:\\Projects\\my-app  (its .claude\\skills is used)'}">${esc((s.skillFolders || []).join('\n'))}</textarea></label></div>`, { cls: 'section' })}
      <div class="row sticky-actions"><button class="btn primary">Save settings</button><span class="muted small">The window reloads when the server or key changes.</span></div></form>`;
  }
};
CHANGES['dk-mode'] = () => $$('.dk-mode').forEach(m => m.classList.toggle('on', $('input', m).checked));
CHANGES['dk-custom'] = el => { $('#dk-server').classList.toggle('hidden', !el.checked); $('#dk-test-row').classList.toggle('hidden', !el.checked); if (el.checked) $('#dk-server input').focus(); };
ACTIONS['dk-test'] = async () => {
  const f = $('form[data-form="dk-settings"]'), out = $('#dk-test-out');
  out.innerHTML = '<span class="spinner"></span>';
  const r = await dapi('POST', '/api/local/settings/test', { useCustomServer: f.useCustomServer.checked, serverBaseUrl: f.serverBaseUrl.value, apiKey: f.apiKey.value });
  out.innerHTML = r.ok ? `${badge('Connected', 'ok')} <span class="small">as <b>${esc(r.user)}</b> (${esc(r.role)})</span>` : `${badge('Failed', 'bad')} <span class="small">${esc(r.message)}</span>`;
};
FORMS['dk-settings'] = async (f, d) => {
  const before = await dapi('GET', '/api/local/settings');
  const custom = !!d.useCustomServer;
  const body = {
    useCustomServer: custom, startPage: d.startPage, port: Number(d.port) || 0, localToolsMode: d.localToolsMode, shell: d.shell,
    commandTimeoutSeconds: Number(d.commandTimeoutSeconds) || 120, workspaceFolders: lines(d.workspaceFolders), skillFolders: lines(d.skillFolders),
    includeClaudeCodeSkills: !!d.includeClaudeCodeSkills, includeClaudePlugins: !!d.includeClaudePlugins, autoUpdate: !!d.autoUpdate,
    includeClaudeUserHooks: !!d.includeClaudeUserHooks,
    includeClaudeMcp: !!d.includeClaudeMcp,
    wholeComputer: !!d.wholeComputer
  };
  if (f.querySelector('[name="modelScreenshots"]')) body.modelScreenshots = !!d.modelScreenshots;
  if (f.querySelector('[name="computerUse"]')) body.computerUse = !!d.computerUse;
  if (f.querySelector('[name="ccusageLog"]')) body.ccusageLog = !!d.ccusageLog;
  if (f.querySelector('[name="quickEntryHotkey"]')) Object.assign(body, { quickEntryHotkey: d.quickEntryHotkey || '', screenshotHotkey: d.screenshotHotkey || '', closeToTray: !!d.closeToTray });
  if (f.querySelector('[name="screenshotWholeScreenKey"]')) Object.assign(body, { screenshotWholeScreenKey: d.screenshotWholeScreenKey || '', screenshotCancelKey: d.screenshotCancelKey || '' });
  if (custom) body.serverBaseUrl = d.serverBaseUrl;
  if (d.apiKey) body.apiKey = d.apiKey;
  const after = await dapi('POST', '/api/local/settings', body);
  if (DK.keys) DK.keys = { quick: after.quickEntryHotkey, shot: after.screenshotHotkey };
  DK.skillsAt = 0;
  pushCatalog();
  if (d.apiKey || after.effectiveServer !== before.effectiveServer) { toast('Saved — reconnecting', 'ok'); setTimeout(() => location.reload(), 600); return; }
  toast('Settings saved', 'ok');
};

// ------------------------------------------------------------------ Claude Code in the Chat tab
/* When the claude CLI is installed, "Claude Code" is a model in Chat. A message to it runs claude on this computer (its own
   account, tools, MCP servers, skills, CLAUDE.md and settings) and continues the chat's Claude session; the exchange is
   then saved to the chat like any other. The Claude chip in the message box holds its options: model, effort, permission
   mode, working folder and more. Permission prompts and Claude's questions appear as Agentic's approval and question cards. */
const CLAUDE_ID = 'claude-code';
const CLAUDE_ICON = '<svg viewBox="0 0 24 24" class="ic"><path d="M12 2.5l1.6 5.2 5.4-1.6-3.6 4.3 4.3 3.6-5.4.2.4 5.5L12 15.9 9.3 19.7l.4-5.5-5.4-.2 4.3-3.6-3.6-4.3 5.4 1.6z"/></svg>';
DK.claude = { status: null, commands: [] };
(() => { try { DK.claude.status = JSON.parse(localStorage.getItem('dk.claude.status') || 'null'); } catch { /* none */ } })();
/** Whether claude is installed (remembered from the last check, so the model shows at once; checked again in the background). */
async function claudeStatus(force) {
  if (DK.claude.status && !force && DK.claude.fresh) return DK.claude.status;
  try {
    const s = await dapi('GET', '/api/local/claude');
    const was = !!(DK.claude.status && DK.claude.status.installed);
    DK.claude.status = s; DK.claude.fresh = true;
    try { localStorage.setItem('dk.claude.status', JSON.stringify(s)); } catch { /* ignore */ }
    // Installed or removed since last time: the model list and the open chat's controls follow.
    if (was !== !!s.installed && typeof CHAT !== 'undefined' && CHAT.meta && $('#composer-bar')) { $('#model-picker').innerHTML = modelButton(); $('#composer-bar').innerHTML = composerBarHtml(); }
  } catch { /* not in the desktop app */ }
  return DK.claude.status || { installed: false };
}
setTimeout(() => claudeStatus(true), 1200);

const CLAUDE_MODELS = [['default', 'Default', 'Your Claude Code setting'], ['opus', 'Opus', 'Most capable'], ['sonnet', 'Sonnet', 'Fast and capable'],
  ['haiku', 'Haiku', 'Fastest'], ['fable', 'Fable', 'Newest family'], ['opusplan', 'Opus plan', 'Opus to plan, Sonnet to work']];
const CLAUDE_EFFORTS = [['default', 'Default'], ['low', 'Low'], ['medium', 'Medium'], ['high', 'High'], ['xhigh', 'Extra high'], ['max', 'Max']];
const CLAUDE_MODES = [['default', 'Ask first', 'Asks before edits and commands (your Claude settings apply)'], ['acceptEdits', 'Accept edits', 'Edits files without asking; asks for commands'],
  ['plan', 'Plan', 'Looks around and plans; changes nothing'], ['auto', 'Auto', 'Claude decides what is safe to run'],
  ['dontAsk', "Don't ask", 'Anything that would ask is refused'], ['bypassPermissions', 'Bypass', 'Never asks - only for throwaway folders']];
function claudeDefaults() {
  let d = null; try { d = JSON.parse(localStorage.getItem('dk.claude.defaults') || 'null'); } catch { /* ignore */ }
  return Object.assign({ model: 'default', effort: 'default', permissionMode: 'default', cwd: '', addDirs: [], appendSystemPrompt: '', maxTurns: '', fallbackModel: '', allowedTools: '', disallowedTools: '', maxBudgetUsd: '' }, d);
}
function claudeOptions(chat) { return Object.assign(claudeDefaults(), chat.settings && chat.settings.claude || {}); }
/** Changes one option for this chat; the choice is also the default for new Claude Code chats (not the session). */
function setClaudeOption(chat, key, value) {
  const o = claudeOptions(chat);
  o[key] = value;
  chat.settings.claude = o;
  const d = Object.assign({}, o); delete d.sessionId;
  try { localStorage.setItem('dk.claude.defaults', JSON.stringify(d)); } catch { /* ignore */ }
  if (typeof saveSettings === 'function') saveSettings();
  const chip = $('#dk-claude-btn'); if (chip) chip.innerHTML = claudeChipText(chat);
}
function claudeChipText(chat) {
  const o = claudeOptions(chat), m = CLAUDE_MODELS.find(x => x[0] === o.model), mode = CLAUDE_MODES.find(x => x[0] === o.permissionMode);
  return `${CLAUDE_ICON} Claude · ${esc(m ? m[1] : o.model)}${o.permissionMode && o.permissionMode !== 'default' ? ` <b>${esc(mode ? mode[1] : o.permissionMode)}</b>` : ''}`;
}

Object.assign(window.chatClient, {
  externalModels() {
    const s = DK.claude.status;
    return s && s.installed ? [{ id: CLAUDE_ID, name: 'Claude Code', description: `claude ${s.version || ''} on this computer${s.source && s.source !== 'PATH' ? ' · ' + s.source : ''} · your own Claude account` }] : [];
  },
  externalComposerHtml(chat) {
    return `${DK.features && DK.features.screenshots === false ? '' : `<button type="button" class="chip-toggle dk-shot-btn" data-act="dk-screenshot" aria-label="Screenshot" data-tip="Screenshot: attach part of your screen">${dkIcon('camera')}</button>`}
      <div class="tools-menu-wrap dk-claude-wrap"><button class="chip-toggle on" data-act="dk-claude-menu" id="dk-claude-btn" data-tip="Claude Code: model, effort, permission mode, folder and session">${claudeChipText(chat)}</button>
        <div class="menu tools-menu dk-claude-menu hidden" id="dk-claude-menu"></div></div>`;
  },
  setExternalOption(chat, key, value) { setClaudeOption(chat, key, value); },
  runExternal: runClaude
});

ACTIONS['dk-claude-menu'] = async () => {
  const m = $('#dk-claude-menu');
  if (!m.classList.contains('hidden')) { m.classList.add('hidden'); return; }
  const st = await claudeStatus(), o = claudeOptions(CHAT);
  let folders = []; try { folders = (await dapi('GET', '/api/local/info')).workspaces || []; } catch { /* none */ }
  const opt = (list, v) => list.map(x => `<option value="${esc(x[0])}"${x[0] === v ? ' selected' : ''}>${esc(x[1])}${x[2] ? ' — ' + esc(x[2]) : ''}</option>`).join('');
  const custom = !CLAUDE_MODELS.some(x => x[0] === o.model);
  m.innerHTML = `<div class="menu-title">Claude Code ${st.version ? `<span class="muted">${esc(st.version)}</span>` : ''}</div>
    <div class="dk-cl-form">
      <label>Model<select data-change="dk-cl" data-key="model">${opt(CLAUDE_MODELS, custom ? '' : o.model)}<option value="__custom"${custom ? ' selected' : ''}>Other model name…</option></select></label>
      <label class="${custom ? '' : 'hidden'}" id="dk-cl-custom">Model name<input data-change="dk-cl" data-key="model" value="${custom ? esc(o.model) : ''}" placeholder="e.g. claude-opus-5-5"></label>
      <label>Effort<select data-change="dk-cl" data-key="effort">${opt(CLAUDE_EFFORTS, o.effort)}</select></label>
      <label>Permissions<select data-change="dk-cl" data-key="permissionMode">${opt(CLAUDE_MODES, o.permissionMode)}</select></label>
      <label>Folder<input data-change="dk-cl" data-key="cwd" list="dk-cl-folders" value="${esc(o.cwd)}" placeholder="${esc(folders[0] || 'your workspace folder')}"><datalist id="dk-cl-folders">${folders.map(f => `<option value="${esc(f)}">`).join('')}</datalist></label>
      <details class="dk-cl-more"><summary>More options</summary>
        <label>Also allow these folders <span class="muted">(one per line)</span><textarea rows="2" data-change="dk-cl" data-key="addDirs">${esc((o.addDirs || []).join('\n'))}</textarea></label>
        <label>Extra instructions <span class="muted">(added to Claude's system prompt)</span><textarea rows="2" data-change="dk-cl" data-key="appendSystemPrompt">${esc(o.appendSystemPrompt || '')}</textarea></label>
        <label>Allowed tools <span class="muted">(e.g. Bash(git *), Edit)</span><input data-change="dk-cl" data-key="allowedTools" value="${esc(o.allowedTools || '')}"></label>
        <label>Blocked tools<input data-change="dk-cl" data-key="disallowedTools" value="${esc(o.disallowedTools || '')}"></label>
        <div class="dk-cl-row"><label>Max turns<input type="number" min="1" data-change="dk-cl" data-key="maxTurns" value="${esc(o.maxTurns || '')}"></label>
          <label>Budget (USD)<input type="number" min="0" step="0.5" data-change="dk-cl" data-key="maxBudgetUsd" value="${esc(o.maxBudgetUsd || '')}"></label></div>
        <label>Fallback model <span class="muted">(when the model is overloaded)</span><input data-change="dk-cl" data-key="fallbackModel" value="${esc(o.fallbackModel || '')}" placeholder="e.g. sonnet"></label>
      </details>
      <div class="dk-cl-session">${o.sessionId ? `Session <code>${esc(o.sessionId.slice(0, 8))}</code> continues with each message.` : 'A new Claude session starts with the next message.'}
        ${o.sessionId ? '<button type="button" class="btn xs" data-act="dk-cl-new-session">New session</button>' : ''}</div>
      <div class="muted small">${esc(st.path || '')}</div>
    </div>`;
  m.classList.remove('hidden');
  setTimeout(() => document.addEventListener('click', function close(ev) { if (!ev.target.closest('.dk-claude-wrap')) { m.classList.add('hidden'); document.removeEventListener('click', close); } }), 0);
};
CHANGES['dk-cl'] = el => {
  const key = el.dataset.key;
  if (key === 'model' && el.tagName === 'SELECT') {
    const box = $('#dk-cl-custom');
    if (el.value === '__custom') { box.classList.remove('hidden'); $('input', box).focus(); return; }
    box.classList.add('hidden');
  }
  let v = el.value;
  if (key === 'addDirs') v = v.split('\n').map(x => x.trim()).filter(Boolean);
  if (key === 'permissionMode' && v === 'bypassPermissions') toast('Bypass: Claude Code will run anything without asking. Use it only in a folder you can throw away.', 'warn');
  setClaudeOption(CHAT, key, v);
};
ACTIONS['dk-cl-new-session'] = el => { setClaudeOption(CHAT, 'sessionId', null); el.closest('.dk-cl-session').innerHTML = 'A new Claude session starts with the next message.'; toast('The next message starts a new Claude session', 'ok'); };

async function fileAsBase64(uploadId) {
  const res = await fetch('/api/chat/files/' + encodeURIComponent(uploadId));
  if (!res.ok) throw new Error('Could not read an attached file.');
  const blob = await res.blob();
  const data = await new Promise((ok, bad) => { const r = new FileReader(); r.onload = () => ok(String(r.result).split(',')[1] || ''); r.onerror = bad; r.readAsDataURL(blob); });
  return { mediaType: blob.type || 'application/octet-stream', data };
}

/** One message to Claude Code: runs it, turns its events into the chat's (delta, reasoning, tool, status, route) and returns the answer. */
async function runClaude({ conv, content, attachments, settings, plan, signal, emit, approve, ask, history }) {
  const st = await claudeStatus();
  if (!st || !st.installed) throw new Error('Claude Code is not installed on this computer. Install it (npm install -g @anthropic-ai/claude-code, or the installer from claude.com/code) and try again.');
  const o = claudeOptions({ settings });
  const images = [], files = [];
  for (const a of attachments || []) {
    const f = await fileAsBase64(a.id);
    if (a.kind === 'image' && /^image\/(png|jpeg|gif|webp)$/.test(f.mediaType)) images.push(f); else files.push({ name: a.name, data: f.data });
  }
  // The first message of a Claude session in a chat that already has messages: Claude gets the conversation so far.
  let prompt = content || (images.length ? 'Look at the attached picture.' : 'See the attached files.');
  if (!o.sessionId && history && history.length) {
    const earlier = history.filter(m => m.role === 'user' || m.role === 'assistant').map(m => `${m.role === 'user' ? 'User' : 'Assistant'}: ${(m.content || '').slice(0, 4000)}`).join('\n\n');
    if (earlier.trim()) prompt = `Earlier in this conversation (it started in Agentic):\n\n${earlier.slice(-30000)}\n\n---\n\n${prompt}`;
  }
  const runId = (crypto.randomUUID ? crypto.randomUUID() : String(Date.now()) + Math.random()).replace(/[^a-z0-9]/gi, '');
  let reader = null, stopTimer = 0;
  const onAbort = () => {
    dapi('POST', '/api/local/claude/interrupt', { runId }).catch(() => { });
    stopTimer = setTimeout(() => { if (reader) reader.cancel().catch(() => { }); }, 8000);
  };
  signal.addEventListener('abort', onAbort);
  const res = await fetch('/api/local/claude/run', {
    method: 'POST', headers: { 'X-Desktop-Token': DK.token, 'Content-Type': 'application/json' },
    body: JSON.stringify({ runId, prompt, images, files, sessionId: o.sessionId || null, plan: !!plan, settings: o })
  });
  if (!res.ok) { let msg = res.statusText; try { msg = (await res.json()).error.message || msg; } catch { /* not JSON */ } throw new Error(msg); }
  const state = { text: '', reasoning: '', tools: new Map(), toolCalls: [], sessionId: null, model: null, result: null, error: null, cwd: '' };
  const respond = (requestId, response) => dapi('POST', '/api/local/claude/respond', { runId, requestId, response }).catch(e => console.warn('claude respond', e));
  const onControl = async m => {
    const r = m.request || {};
    if (r.subtype !== 'can_use_tool') { respond(m.request_id, {}); return; }
    if (r.tool_name === 'AskUserQuestion' && r.input && Array.isArray(r.input.questions)) {
      const qs = r.input.questions.map(q => ({ question: q.question, options: (q.options || []).map(x => typeof x === 'string' ? x : x.label).filter(Boolean).slice(0, 4), allowOther: true }));
      const answers = await ask(qs);
      if (!answers) { respond(m.request_id, { behavior: 'deny', message: 'The user did not answer here; they will reply in the chat.' }); return; }
      respond(m.request_id, { behavior: 'allow', updatedInput: Object.assign({}, r.input, { answers: Object.fromEntries(r.input.questions.map((q, i) => [q.question, answers[i]])) }) });
      return;
    }
    const choice = await approve({ requestId: m.request_id, toolName: r.display_name || r.tool_name, input: r.input, reason: r.decision_reason || r.description });
    if (choice === 'deny') respond(m.request_id, { behavior: 'deny', message: 'The user denied this.' });
    else respond(m.request_id, Object.assign({ behavior: 'allow', updatedInput: r.input }, choice === 'always' && r.permission_suggestions ? { updatedPermissions: r.permission_suggestions } : {}));
  };
  const handle = m => {
    switch (m.type) {
      case 'agentic_started': state.cwd = m.cwd; emit('status', { text: 'Starting Claude Code…' }); break;
      case 'agentic_error': state.error = m.message; break;
      case 'system':
        if (m.subtype === 'init') {
          state.sessionId = m.session_id; state.model = m.model;
          DK.claude.commands = (m.slash_commands || []).slice(0, 400);
          emit('route', { model: m.model, label: 'Claude Code', reason: `${m.permissionMode || 'default'} · ${m.cwd || state.cwd}` });
          emit('status', { text: 'Thinking…' });
        } else if (m.subtype === 'compact_boundary') emit('status', { text: 'Claude compacted the conversation…' });
        break;
      case 'stream_event': {
        if (m.parent_tool_use_id) { emit('status', { text: 'A subagent is working…' }); break; }
        const ev = m.event || {};
        if (ev.type === 'content_block_delta' && ev.delta) {
          if (ev.delta.type === 'text_delta' && ev.delta.text) { state.text += ev.delta.text; emit('delta', { text: ev.delta.text }); }
          else if (ev.delta.type === 'thinking_delta' && ev.delta.thinking) { state.reasoning += ev.delta.thinking; emit('reasoning', { text: ev.delta.thinking }); }
        } else if (ev.type === 'content_block_start' && ev.content_block && ev.content_block.type === 'tool_use') emit('status', { text: `Using ${ev.content_block.name}…` });
        break;
      }
      case 'assistant':
        if (m.parent_tool_use_id) break;
        for (const b of (m.message && m.message.content) || []) {
          if (b.type !== 'tool_use') continue;
          const call = { id: b.id, name: b.name, server: 'claude-code', arguments: JSON.stringify(b.input || {}) };
          state.tools.set(b.id, call);
          if (state.text && !state.text.endsWith('\n')) state.text += '\n\n';
          emit('tool', { phase: 'start', call });
        }
        break;
      case 'user':
        if (m.parent_tool_use_id) break;
        for (const b of (m.message && Array.isArray(m.message.content) ? m.message.content : [])) {
          if (b.type !== 'tool_result') continue;
          const call = state.tools.get(b.tool_use_id) || { id: b.tool_use_id, name: 'tool', server: 'claude-code', arguments: '{}' };
          const text = typeof b.content === 'string' ? b.content : (b.content || []).map(c => c.type === 'text' ? c.text : `[${c.type}]`).join('\n');
          const done = Object.assign({}, call, { ok: !b.is_error, result: String(text || '').slice(0, 12000) });
          state.toolCalls.push(done);
          emit('tool', { phase: 'end', call: done });
        }
        break;
      case 'control_request': onControl(m); break;
      case 'result': state.result = m; break;
      case 'rate_limit_event': if (m.rate_limit_info && m.rate_limit_info.status === 'rejected') emit('status', { text: 'Claude usage limit reached…' }); break;
    }
  };
  try {
    reader = res.body.getReader();
    const dec = new TextDecoder();
    let buf = '';
    for (; ;) {
      const { value, done } = await reader.read();
      if (done) break;
      buf += dec.decode(value, { stream: true });
      let cut;
      while ((cut = buf.indexOf('\n\n')) >= 0) {
        const raw = buf.slice(0, cut); buf = buf.slice(cut + 2);
        if (!raw.startsWith('data: ')) continue;
        let m; try { m = JSON.parse(raw.slice(6)); } catch { continue; }
        handle(m);
      }
    }
  } finally {
    clearTimeout(stopTimer);
    signal.removeEventListener('abort', onAbort);
  }
  const r = state.result, u = (r && r.usage) || {};
  const cost = r && typeof r.total_cost_usd === 'number' ? r.total_cost_usd : null;
  const failed = r && r.is_error;
  const answer = {
    content: (state.text.trim() || (r && !failed && typeof r.result === 'string' ? r.result : '')).trim(),
    reasoning: state.reasoning || null,
    toolCalls: state.toolCalls,
    routedModel: state.model,
    routeInfo: `Claude Code · ${(CLAUDE_MODES.find(x => x[0] === (plan ? 'plan' : o.permissionMode)) || ['', 'Ask first'])[1]}${cost !== null ? ` · $${cost.toFixed(cost < 0.1 ? 4 : 2)} on your Claude account` : ''}${r && r.num_turns ? ` · ${r.num_turns} turns` : ''}`,
    durationMs: r && r.duration_ms || 0,
    promptTokens: (u.input_tokens || 0) + (u.cache_creation_input_tokens || 0) + (u.cache_read_input_tokens || 0),
    completionTokens: u.output_tokens || 0,
    // Stopped with the stop button: Claude reports the interrupted turn as an error; it is a stop.
    finishReason: signal.aborted ? 'stopped' : !r ? 'error' : failed ? 'error' : r.stop_reason === 'max_tokens' ? 'length' : 'stop',
    error: signal.aborted ? 'Stopped.' : state.error || (failed ? (typeof r.result === 'string' && r.result ? r.result : `Claude Code ended with ${r.subtype || 'an error'}.`) : (!r && !signal.aborted ? 'Claude Code stopped without finishing the answer.' : null))
  };
  return { answer, settings: { claude: Object.assign({}, o, { sessionId: (r && r.session_id) || state.sessionId || o.sessionId || null }) } };
}

// "Install on my computer" from the web app's My MCP servers page: the server sends the MCP server to this app, which opens
// its add form filled in (nothing is added until the user clicks Add).
const dkInstalls = new Set();
document.addEventListener('chathub', ev => {
  const m = ev.detail || {};
  if (m.type !== 'mcp_install' || !m.data || (m.data.device && m.data.device !== DK.deviceId) || dkInstalls.has(m.data.id)) return;
  dkInstalls.add(m.data.id);
  const e = m.data.entry || {};
  const url = e.url || e.endpoint;
  DK.preset = { id: m.data.name, name: m.data.name, transport: url ? (e.type === 'sse' ? 'sse' : 'http') : 'stdio', command: e.command || '', args: e.args || [],
    endpoint: url || '', headers: e.headers || {}, env: e.env || {}, requireApproval: !!e.requireApproval, timeoutSeconds: 60 };
  navigate('/local/mcp');
  setTimeout(() => { ACTIONS['dk-mcp-edit']({ dataset: {} }); toast('An MCP server from the web app: check it, fill in any keys, then Add', 'ok'); }, 500);
});
