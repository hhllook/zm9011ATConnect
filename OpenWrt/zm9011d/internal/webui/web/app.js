'use strict';

const SVG_BUBBLE = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 3h16a3 3 0 0 1 3 3v8a3 3 0 0 1-3 3h-9l-5 5v-5H4a3 3 0 0 1-3-3V6a3 3 0 0 1 3-3z" fill="currentColor"/></svg>';
const SVG_BUBBLE_GRAY = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 3h16a3 3 0 0 1 3 3v8a3 3 0 0 1-3 3h-9l-5 5v-5H4a3 3 0 0 1-3-3V6a3 3 0 0 1 3-3z" fill="currentColor"/></svg>';
const SVG_SHIELD = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 1l10 3.5V11c0 5.5-4.2 10-10 12-5.8-2-10-6.5-10-12V4.5z" fill="#4D6BFE"/><path d="M7 12l3.5 3.5L17 8.5" stroke="#fff" stroke-width="2.2" fill="none" stroke-linecap="round" stroke-linejoin="round"/></svg>';

const PAGE_TITLES = {
  sms: '实时短信',
  device: '设备信息',
  network: '网络与信号',
  logs: '系统日志',
  settings: '设置与调试',
};

const state = {
  page: 'sms',
  messages: [],
  conversations: [],
  selected: null,
  serial: {},
  logs: [],
  config: {},
  toastTimer: null,
  atBusy: false,
};

const el = {};

document.addEventListener('DOMContentLoaded', init);

async function init() {
  cacheElements();
  bindNavigation();
  bindSms();
  bindPages();

  await refreshState();
  await loadMessages();
  await loadLogs();

  connectSSE();

  setInterval(refreshState, 5000);
  setInterval(loadMessages, 60000);

  const initial = location.hash ? location.hash.slice(1) : 'sms';
  switchPage(PAGE_TITLES[initial] ? initial : 'sms', false);
}

function cacheElements() {
  el.pageTitle = document.getElementById('pageTitle');
  el.statusDot = document.getElementById('statusDot');
  el.statusText = document.getElementById('statusText');
  el.sidebarStatus = document.getElementById('sidebarStatus');

  el.smsLayout = document.getElementById('smsLayout');
  el.inboxCount = document.getElementById('inboxCount');
  el.inboxEmpty = document.getElementById('inboxEmpty');
  el.conversationList = document.getElementById('conversationList');
  el.smsDot = document.getElementById('smsDot');
  el.smsStatus = document.getElementById('smsStatus');
  el.threadSender = document.getElementById('threadSender');
  el.thread = document.getElementById('thread');
  el.threadEmpty = document.getElementById('threadEmpty');
  el.backBtn = document.getElementById('backBtn');
  el.copyNumberBtn = document.getElementById('copyNumberBtn');
  el.replyInput = document.getElementById('replyInput');
  el.simBadge = document.getElementById('simBadge');
  el.sendBtn = document.getElementById('sendBtn');
  el.insertCodeBtn = document.getElementById('insertCodeBtn');

  el.deviceGrid = document.getElementById('deviceGrid');
  el.networkGrid = document.getElementById('networkGrid');
  el.refreshDevice = document.getElementById('refreshDevice');
  el.refreshNetwork = document.getElementById('refreshNetwork');
  el.logList = document.getElementById('logList');
  el.clearLogs = document.getElementById('clearLogs');
  el.settingsInfo = document.getElementById('settingsInfo');
  el.atInput = document.getElementById('atInput');
  el.atSend = document.getElementById('atSend');
  el.atOutput = document.getElementById('atOutput');
  el.toast = document.getElementById('toast');
}

async function api(path, options = {}) {
  const opts = Object.assign({}, options);
  opts.headers = Object.assign({ 'Content-Type': 'application/json' }, opts.headers || {});
  const res = await fetch(path, opts);
  const text = await res.text();
  let data = null;
  if (text) {
    try { data = JSON.parse(text); } catch (_) { data = null; }
  }
  if (!res.ok) {
    throw new Error((data && (data.error || data.message)) || (text || res.statusText));
  }
  return data;
}

function esc(value) {
  return String(value == null ? '' : value).replace(/[&<>"']/g, ch => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[ch]));
}

function hhmm(value) {
  if (!value || value.length < 16) return value || '';
  return value.slice(11, 16);
}

function toast(message) {
  el.toast.textContent = message;
  el.toast.hidden = false;
  clearTimeout(state.toastTimer);
  state.toastTimer = setTimeout(() => { el.toast.hidden = true; }, 2600);
}

/* ================= 导航 ================= */
function bindNavigation() {
  document.querySelectorAll('.nav-item').forEach(btn => {
    btn.addEventListener('click', () => switchPage(btn.dataset.page));
  });
}

function switchPage(page, updateHash = true) {
  if (!PAGE_TITLES[page]) page = 'sms';
  state.page = page;

  document.querySelectorAll('.nav-item').forEach(btn => {
    btn.classList.toggle('active', btn.dataset.page === page);
  });
  document.querySelectorAll('.page').forEach(section => {
    section.classList.toggle('active', section.id === 'page-' + page);
  });
  el.pageTitle.textContent = PAGE_TITLES[page];

  if (updateHash) {
    history.replaceState(null, '', '#' + page);
  }

  if (page === 'device') loadDevice();
  if (page === 'network') loadNetwork();
  if (page === 'logs') renderLogs();
  if (page === 'settings') loadSettings();
  if (page === 'sms') {
    renderInbox();
    renderThread();
    ensureSelection();
  }
}

/* ================= 状态 ================= */
async function refreshState() {
  try {
    const data = await api('/api/state');
    state.serial = (data && data.serial) || {};
    state.config.deleteAfterRead = data ? data.deleteAfterRead : state.config.deleteAfterRead;
    state.config.version = data ? data.version : state.config.version;
    state.config.addr = data ? data.addr : state.config.addr;
    renderStatus();
  } catch (err) {
    el.statusDot.classList.remove('on');
    el.statusText.textContent = '无法连接后台服务';
    el.sidebarStatus.textContent = '后台服务不可用';
  }
}

function renderStatus() {
  const serial = state.serial || {};
  const connected = !!serial.connected;
  const realtime = !!serial.realtime;

  let text = '未连接（自动重连中）';
  if (connected) {
    text = '已连接 ' + serial.device + ' @ ' + serial.baud;
  }
  el.statusDot.classList.toggle('on', connected);
  el.statusText.textContent = text;
  el.sidebarStatus.textContent = text;

  if (!connected) {
    el.smsStatus.textContent = '串口未连接';
    el.smsDot.classList.remove('on');
  } else if (realtime) {
    el.smsStatus.textContent = '实时接收已开启（PDU 模式）';
    el.smsDot.classList.add('on');
  } else {
    el.smsStatus.textContent = '实时接收未开启（自动重试中）';
    el.smsDot.classList.remove('on');
  }

  if (serial.simSlot) {
    el.simBadge.textContent = 'SIM ' + serial.simSlot;
    el.simBadge.hidden = false;
  } else {
    el.simBadge.hidden = true;
  }
}

/* ================= 短信 ================= */
function bindSms() {
  el.conversationList.addEventListener('click', evt => {
    const item = evt.target.closest('.conv');
    if (!item) return;
    selectConversation(item.dataset.sender);
  });

  el.thread.addEventListener('click', evt => {
    const copy = evt.target.closest('[data-copy]');
    if (copy) {
      copyText(copy.dataset.copy, '密码已复制');
      return;
    }
    const raw = evt.target.closest('[data-raw]');
    if (raw) {
      const pre = raw.nextElementSibling;
      if (!pre) return;
      pre.hidden = !pre.hidden;
      raw.textContent = pre.hidden ? '查看原文' : '收起原文';
    }
  });

  el.backBtn.addEventListener('click', () => {
    el.smsLayout.classList.remove('thread-open');
  });

  el.copyNumberBtn.addEventListener('click', () => {
    if (!state.selected) return;
    copyText(state.selected, '号码已复制');
  });

  el.replyInput.addEventListener('input', updateComposer);
  el.replyInput.addEventListener('keydown', evt => {
    if (evt.key === 'Enter') {
      evt.preventDefault();
      sendReply();
    }
  });
  el.sendBtn.addEventListener('click', sendReply);
  el.insertCodeBtn.addEventListener('click', insertLatestCode);

  window.addEventListener('resize', () => {
    if (window.innerWidth > 860) {
      el.smsLayout.classList.remove('thread-open');
      ensureSelection();
    }
  });
}

async function loadMessages() {
  try {
    const data = await api('/api/messages');
    state.messages = Array.isArray(data) ? data : [];
    groupConversations();
    renderInbox();
    if (state.selected) renderThread();
    ensureSelection();
  } catch (err) {
    toast('加载短信失败：' + err.message);
  }
}

function groupConversations() {
  const map = new Map();
  for (const msg of state.messages) {
    const sender = msg.sender || '未知号码';
    if (!map.has(sender)) {
      map.set(sender, { sender, messages: [], unread: 0, last: null });
    }
    map.get(sender).messages.push(msg);
  }

  const conversations = [];
  for (const conv of map.values()) {
    conv.messages.sort((a, b) => (a.sortKey || 0) - (b.sortKey || 0));
    conv.unread = conv.messages.filter(m => !m.read && !m.outgoing).length;
    conv.last = conv.messages[conv.messages.length - 1] || null;
    conversations.push(conv);
  }
  conversations.sort((a, b) => ((b.last && b.last.sortKey) || 0) - ((a.last && a.last.sortKey) || 0));
  state.conversations = conversations;

  if (state.selected && !conversations.some(c => c.sender === state.selected)) {
    state.selected = null;
  }
}

function renderInbox() {
  el.inboxCount.textContent = String(state.conversations.length);
  el.inboxEmpty.hidden = state.conversations.length > 0;

  el.conversationList.innerHTML = state.conversations.map(conv => {
    const active = conv.sender === state.selected ? ' active' : '';
    const unread = conv.unread > 0 ? `<span class="unread">${conv.unread > 99 ? '99+' : conv.unread}</span>` : '';
    const preview = conv.last ? conv.last.body : '';
    const time = conv.last ? hhmm(conv.last.time) : '';
    return `<button class="conv${active}" data-sender="${esc(conv.sender)}">
      <span class="conv-avatar">${SVG_BUBBLE}</span>
      <span class="conv-main">
        <span class="conv-sender">${esc(conv.sender)}</span>
        <span class="conv-preview">${esc(preview)}</span>
      </span>
      <span class="conv-meta">
        <span class="conv-time">${esc(time)}</span>
        ${unread}
      </span>
    </button>`;
  }).join('');

  updateComposer();
}

function selectConversation(sender) {
  const conv = state.conversations.find(c => c.sender === sender);
  if (!conv) return;

  state.selected = sender;

  const unreadIds = conv.messages.filter(m => !m.read && !m.outgoing).map(m => m.id);
  if (unreadIds.length > 0) {
    conv.messages.forEach(m => { if (unreadIds.includes(m.id)) m.read = true; });
    conv.unread = 0;
    api('/api/messages/read', { method: 'POST', body: JSON.stringify({ ids: unreadIds }) }).catch(() => {});
  }

  renderInbox();
  renderThread();
  el.smsLayout.classList.add('thread-open');
  updateComposer();
}

function ensureSelection() {
  if (state.page !== 'sms') return;

  // 深链：?sender=1069... 直接打开指定会话（手机端会进入聊天视图）
  const wanted = new URLSearchParams(location.search).get('sender');
  if (wanted && state.conversations.some(c => c.sender === wanted)) {
    if (state.selected !== wanted) selectConversation(wanted);
    return;
  }

  if (state.selected && state.conversations.some(c => c.sender === state.selected)) return;
  if (window.innerWidth <= 860) return;
  if (state.conversations.length > 0) {
    selectConversation(state.conversations[0].sender);
  }
}

function renderThread() {
  const conv = state.conversations.find(c => c.sender === state.selected);
  if (!conv) {
    el.thread.innerHTML = '';
    el.threadEmpty.hidden = false;
    el.threadSender.textContent = '选择会话';
    el.copyNumberBtn.disabled = true;
    updateComposer();
    return;
  }

  el.threadEmpty.hidden = true;
  el.threadSender.textContent = conv.sender;
  el.copyNumberBtn.disabled = false;
  el.thread.innerHTML = conv.messages.map(messageHtml).join('');
  scrollThreadToBottom();
  updateComposer();
}

function messageHtml(msg) {
  const outgoing = !!msg.outgoing;
  const verification = !outgoing && !!msg.code;
  const classes = ['msg-card'];
  if (outgoing) classes.push('outgoing');
  if (verification) classes.push('verification');

  let head = '';
  if (verification) {
    head = `<div class="msg-head">
      <span class="msg-icon">${SVG_BUBBLE}</span>
      <span>
        <span class="msg-title">验证消息</span>
        ${msg.tag ? `<span class="tag">${esc(msg.tag)}</span>` : ''}
      </span>
    </div>`;
  } else if (!outgoing) {
    head = `<div class="msg-head">
      <span class="msg-icon gray">${SVG_BUBBLE_GRAY}</span>
      <span class="msg-title">短信</span>
    </div>`;
  }

  const code = verification ? `<div class="msg-code">${esc(msg.code)}</div>` : '';
  const copy = verification
    ? `<button class="copy-btn" data-copy="${esc(msg.code)}">复制${esc(msg.tag || '验证码')}</button>`
    : '';
  const shield = verification
    ? `<div class="shield">${SVG_SHIELD}<span>已开启验证码保护，禁止第三方应用读取</span></div>`
    : '';

  const rawText = msg.raw || msg.body || '';
  const raw = `<button class="link-btn" data-raw>查看原文</button><pre class="raw-text" hidden>${esc(rawText)}</pre>`;

  return `<div class="msg-row${outgoing ? ' outgoing' : ''}">
    <div class="msg-time">${esc(hhmm(msg.time))}</div>
    <div class="${classes.join(' ')}">
      ${head}${code}
      <div class="msg-body">${esc(msg.body)}</div>
      ${copy}${raw}${shield}
    </div>
  </div>`;
}

function updateComposer() {
  const hasSelection = !!state.selected;
  const hasText = !!(el.replyInput.value || '').trim();
  el.sendBtn.disabled = !hasSelection || !hasText;
  el.insertCodeBtn.disabled = !latestCode();
}

function latestCode() {
  const conv = state.conversations.find(c => c.sender === state.selected);
  if (!conv) return '';
  for (let i = conv.messages.length - 1; i >= 0; i--) {
    if (conv.messages[i].code) return conv.messages[i].code;
  }
  return '';
}

function insertLatestCode() {
  const code = latestCode();
  if (!code) return;
  el.replyInput.value = code;
  updateComposer();
  el.replyInput.focus();
}

async function sendReply() {
  const text = (el.replyInput.value || '').trim();
  if (!text || !state.selected) return;

  el.sendBtn.disabled = true;
  try {
    await api('/api/sms/send', {
      method: 'POST',
      body: JSON.stringify({ sender: state.selected, text }),
    });
    el.replyInput.value = '';
    toast('短信已发送');
    await loadMessages();
  } catch (err) {
    toast('发送失败：' + err.message);
  } finally {
    updateComposer();
  }
}

function scrollThreadToBottom() {
  requestAnimationFrame(() => {
    el.thread.scrollTop = el.thread.scrollHeight;
  });
}

function handleIncoming(msg) {
  if (!msg || state.messages.some(m => m.id === msg.id)) return;

  state.messages.push(msg);
  groupConversations();

  if (state.selected === msg.sender && !msg.outgoing) {
    msg.read = true;
    api('/api/messages/read', { method: 'POST', body: JSON.stringify({ ids: [msg.id] }) }).catch(() => {});
  }

  renderInbox();

  if (state.page === 'sms') {
    if (state.selected === msg.sender) {
      renderThread();
    } else if (!state.selected && window.innerWidth > 860) {
      selectConversation(msg.sender);
    }
  }
}

async function copyText(text, successMessage) {
  if (!text) return;
  try {
    if (navigator.clipboard && window.isSecureContext) {
      await navigator.clipboard.writeText(text);
    } else {
      const area = document.createElement('textarea');
      area.value = text;
      area.style.position = 'fixed';
      area.style.opacity = '0';
      document.body.appendChild(area);
      area.select();
      document.execCommand('copy');
      area.remove();
    }
    toast(successMessage || '已复制');
  } catch (err) {
    toast('复制失败：' + err.message);
  }
}

/* ================= 设备 / 网络 ================= */
function bindPages() {
  el.refreshDevice.addEventListener('click', loadDevice);
  el.refreshNetwork.addEventListener('click', loadNetwork);
  el.clearLogs.addEventListener('click', () => {
    state.logs = [];
    el.logList.innerHTML = '';
  });
  el.atSend.addEventListener('click', sendAtCommand);
  el.atInput.addEventListener('keydown', evt => {
    if (evt.key === 'Enter') {
      evt.preventDefault();
      sendAtCommand();
    }
  });
}

async function loadDevice() {
  await loadInfo(el.deviceGrid, '/api/device');
}

async function loadNetwork() {
  await loadInfo(el.networkGrid, '/api/network');
}

async function loadInfo(container, path) {
  container.innerHTML = '<div class="empty">查询中…</div>';
  try {
    const data = await api(path);
    const items = (data && data.items) || [];
    if (items.length === 0) {
      container.innerHTML = '<div class="empty">暂无数据</div>';
      return;
    }
    container.innerHTML = items.map(item => {
      const output = item.ok ? (item.output || 'OK') : (item.error || '命令失败');
      return `<div class="info-card${item.ok ? '' : ' error'}">
        <div class="info-head"><span>${esc(item.name)}</span><code>${esc(item.command)}</code></div>
        <pre>${esc(output)}</pre>
      </div>`;
    }).join('');
  } catch (err) {
    container.innerHTML = `<div class="empty">查询失败：${esc(err.message)}</div>`;
  }
}

/* ================= 日志 ================= */
async function loadLogs() {
  try {
    const data = await api('/api/logs');
    state.logs = Array.isArray(data) ? data.slice().reverse() : [];
    renderLogs();
  } catch (err) {
    toast('加载日志失败：' + err.message);
  }
}

function renderLogs() {
  if (!el.logList) return;
  el.logList.innerHTML = state.logs.map(logHtml).join('');
}

function logHtml(entry) {
  const level = (entry.level || 'info').toLowerCase();
  const label = level === 'error' ? 'ERR' : level.toUpperCase();
  return `<div class="log-row">
    <span class="log-time">${esc(entry.time || '')}</span>
    <span class="log-level ${esc(level)}">${esc(label)}</span>
    <span class="log-text">${esc(entry.text || '')}</span>
  </div>`;
}

function addLog(entry) {
  state.logs.unshift(entry);
  if (state.logs.length > 500) state.logs.pop();
  if (state.page === 'logs' && el.logList) {
    el.logList.insertAdjacentHTML('afterbegin', logHtml(entry));
    while (el.logList.children.length > 500) {
      el.logList.removeChild(el.logList.lastElementChild);
    }
  }
}

/* ================= 设置 / AT 调试 ================= */
async function loadSettings() {
  try {
    const config = await api('/api/config');
    state.config = Object.assign(state.config, config);
    renderStatus();

    const serial = state.serial || {};
    const rows = [
      ['串口设备', serial.device || (config.device === 'auto' ? '自动探测中' : config.device)],
      ['波特率', String(config.baud || serial.baud || '')],
      ['连接状态', serial.connected ? '已连接' : '未连接（自动重连）'],
      ['实时接收', serial.realtime ? '已开启（PDU 模式）' : '未开启'],
      ['SIM 卡槽', serial.simSlot ? 'SIM ' + serial.simSlot : '未知'],
      ['读取后清理模组短信', config.deleteAfterRead ? '是' : '否'],
      ['监听地址', (config.listen || '') + ':' + (config.port || '')],
      ['版本', config.version || ''],
    ];
    el.settingsInfo.innerHTML = rows.map(([k, v]) =>
      `<span class="k">${esc(k)}</span><span>${esc(v)}</span>`
    ).join('');
  } catch (err) {
    toast('加载设置失败：' + err.message);
  }
}

async function sendAtCommand() {
  const command = (el.atInput.value || '').trim();
  if (!command || state.atBusy) return;

  state.atBusy = true;
  el.atSend.disabled = true;
  el.atOutput.textContent = '发送中…';

  try {
    const res = await api('/api/at', {
      method: 'POST',
      body: JSON.stringify({ command }),
    });
    const parts = [];
    if (res.lines && res.lines.length) parts.push(res.lines.join('\n'));
    if (res.final) parts.push(res.final);
    if (res.error) parts.push('错误：' + res.error);
    el.atOutput.textContent = parts.length ? parts.join('\n') : '无响应';
  } catch (err) {
    el.atOutput.textContent = '请求失败：' + err.message;
  } finally {
    state.atBusy = false;
    el.atSend.disabled = false;
  }
}

/* ================= SSE ================= */
function connectSSE() {
  if (!window.EventSource) return;
  // 静态预览模式：截图/调试时可跳过长连接，避免浏览器等待网络空闲
  if (new URLSearchParams(location.search).has('static')) return;

  const source = new EventSource('/api/events');

  source.addEventListener('sms', evt => {
    try {
      handleIncoming(JSON.parse(evt.data));
    } catch (_) {
      // 忽略非法事件
    }
  });

  source.addEventListener('log', evt => {
    try {
      addLog(JSON.parse(evt.data));
    } catch (_) {
      // 忽略非法事件
    }
  });
}
