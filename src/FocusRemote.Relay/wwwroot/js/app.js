// WebViewer UI wiring: login, source selection, stream control and session lifecycle.

import {
  Channel, ControlType, VideoType, AudioType, InputType, MIN_PASSWORD_LENGTH,
  isValidId, isValidPassword, decodeJson, buildPing, parsePing, parseVideoFrame,
  parseAudioConfig, parseAudioPacket,
} from './protocol.js';
import { deriveSalt, deriveRootKey } from './crypto.js';
import { ViewerConnection } from './connection.js';
import { VideoPlayer, detectVideoCodecs } from './video.js';
import { AudioPlayer, detectAudioCodecs } from './audio.js';
import { InputController } from './input.js';
import { TerminalPanel } from './terminal.js';
import { ViewZoom } from './viewzoom.js';

const STORAGE_KEY = 'focusremote.login';
const TOUCH_MODE_KEY = 'focusremote.touchMode'; // 'touch' | 'mouse'
const PING_INTERVAL_MS = 2000;
const MAX_FPS = 30;

const $ = (id) => document.getElementById(id);
const el = {
  loginView: $('loginView'), loginForm: $('loginForm'), relay: $('relayInput'), id: $('idInput'),
  password: $('passwordInput'), loginError: $('loginError'), loginStatus: $('loginStatus'),
  connect: $('connectButton'), envWarning: $('envWarning'),
  sessionView: $('sessionView'), sourcesButton: $('sourcesButton'), hostName: $('hostName'), latency: $('latency'),
  touchMode: $('touchModeSelect'), keyboard: $('keyboardButton'), mute: $('muteButton'),
  fullscreen: $('fullscreenButton'), disconnect: $('disconnectButton'),
  sourcesPanel: $('sourcesPanel'), refreshSources: $('refreshSourcesButton'), bitrate: $('bitrateSelect'),
  sourceList: $('sourceList'), stopStream: $('stopStreamButton'),
  stage: $('stage'), canvas: $('videoCanvas'), cursor: $('cursor'), stageMessage: $('stageMessage'),
  softInput: $('softInput'), overlay: $('overlay'), overlayText: $('overlayText'),
  reconnect: $('reconnectButton'), back: $('backButton'),
  terminalButton: $('terminalButton'), terminalView: $('terminalView'), terminalTabs: $('terminalTabs'),
  terminalNew: $('terminalNewButton'), terminalShellMenu: $('terminalShellMenu'),
  terminalMessage: $('terminalMessage'), terminalHost: $('terminalHost'),
};

const isTouchDevice = matchMedia('(pointer: coarse)').matches || navigator.maxTouchPoints > 0;
const isNarrow = () => matchMedia('(max-width: 720px)').matches;

const state = {
  conn: null,
  credentials: null,   // { relayUrl, id, rootKey } kept in memory only, for reconnect
  caps: null,          // { videoCodecs, codecStrings, audioCodecs }
  sources: [],
  activeSourceId: null,
  streaming: false,
  pingTimer: 0,
};

// ---------- storage (relay + id only, never the password) ----------

function loadSaved() {
  try { return JSON.parse(localStorage.getItem(STORAGE_KEY) || 'null'); } catch { return null; }
}

function save(relay, id) {
  try { localStorage.setItem(STORAGE_KEY, JSON.stringify({ relay, id })); } catch { /* storage unavailable */ }
}

function defaultRelayUrl() {
  // Same origin and same directory as the page, so the viewer also works under a sub-path or on the LAN host.
  const u = new URL('.', location.href);
  u.protocol = u.protocol === 'https:' ? 'wss:' : 'ws:';
  u.search = '';
  u.hash = '';
  return u.toString();
}

// ---------- players ----------

const video = new VideoPlayer(el.canvas, {
  codecStrings: {},
  onNeedKeyframe: () => requestKeyframe(),
  onFirstFrame: () => { el.stageMessage.hidden = true; input.refreshOverlay(); },
});
const audio = new AudioPlayer();
const viewZoom = new ViewZoom(el.stage, el.canvas);
const input = new InputController({
  zoom: viewZoom,
  surface: el.stage,
  cursor: el.cursor,
  softInput: el.softInput,
  getVideoRect: () => video.getVideoRect(),
  send: (type, payload) => state.conn?.send(Channel.Input, type, payload),
  // The terminal view owns the keyboard while visible, so remote input is suspended.
  isActive: () => !!state.conn?.isAuthenticated && state.streaming && video.hasFrame && !terminal.visible,
});
const terminal = new TerminalPanel({
  view: el.terminalView, tabs: el.terminalTabs, newButton: el.terminalNew, shellMenu: el.terminalShellMenu,
  message: el.terminalMessage, host: el.terminalHost, toggleButton: el.terminalButton,
  send: (type, payload) => state.conn?.send(Channel.Terminal, type, payload),
  isConnected: () => !!state.conn?.isAuthenticated,
  onBeforeShow: () => input.releaseAll(), // still active here: releases keys/buttons held on the remote side
  onVisibilityChange: () => input.refreshOverlay(),
});

let lastKeyframeRequest = 0;
function requestKeyframe() {
  const now = performance.now();
  if (now - lastKeyframeRequest < 500) return; // collapse bursts of decoder errors
  lastKeyframeRequest = now;
  state.conn?.sendControlJson(ControlType.RequestKeyframe, {});
}

// ---------- login ----------

function showLoginError(text) {
  el.loginError.textContent = text;
  el.loginError.hidden = !text;
}

function checkEnvironment() {
  const problems = [];
  if (!globalThis.isSecureContext || !globalThis.crypto?.subtle) problems.push('Trang cần mở qua HTTPS (hoặc localhost) để dùng WebCrypto/WebCodecs.');
  if (typeof VideoDecoder === 'undefined') problems.push('Trình duyệt không hỗ trợ WebCodecs (cần Chrome/Edge mới, Safari ≥ 16.4).');
  el.envWarning.textContent = problems.join(' ');
  el.envWarning.hidden = problems.length === 0;
  return problems.length === 0;
}

async function onLoginSubmit(e) {
  e.preventDefault();
  showLoginError('');
  const relayUrl = el.relay.value.trim();
  const id = el.id.value.trim();
  const password = el.password.value;
  try { new URL(relayUrl); } catch { showLoginError('URL relay không hợp lệ.'); return; }
  if (!isValidId(id)) { showLoginError('Id gồm 3–64 ký tự A-Z, a-z, 0-9, "_" hoặc "-".'); return; }
  if (!isValidPassword(password)) { showLoginError(`Password tối thiểu ${MIN_PASSWORD_LENGTH} ký tự.`); return; }
  if (!checkEnvironment()) { showLoginError('Trình duyệt/kết nối hiện tại không đủ điều kiện.'); return; }

  save(relayUrl, id);
  el.connect.disabled = true;
  el.loginStatus.textContent = 'Đang tạo khoá từ password…';
  try {
    const [rootKey, caps] = await Promise.all([
      deriveSalt(id).then((salt) => deriveRootKey(password, salt)),
      state.caps ? Promise.resolve(state.caps) : detectCapabilities(),
    ]);
    state.caps = caps;
    if (!caps.videoCodecs.includes('h264')) throw new Error('Trình duyệt không giải được H.264.');
    video.opts.codecStrings = caps.codecStrings;
    state.credentials = { relayUrl, id, rootKey };
    el.password.value = '';
    startConnection();
  } catch (err) {
    showLoginError(err.message || String(err));
  } finally {
    el.connect.disabled = false;
    el.loginStatus.textContent = '';
  }
}

async function detectCapabilities() {
  const [v, audioCodecs] = await Promise.all([detectVideoCodecs(), detectAudioCodecs()]);
  return { ...v, audioCodecs };
}

// ---------- connection lifecycle ----------

function startConnection() {
  stopConnection();
  const { relayUrl, id, rootKey } = state.credentials;
  showSession();
  setOverlay(null);
  el.hostName.textContent = id;
  setStageMessage('Đang kết nối…');
  const conn = new ViewerConnection({
    relayUrl, id, rootKey,
    handlers: {
      onStatus: (text) => { if (!conn.isAuthenticated) setStageMessage(text); },
      onAuthOk: (info) => onAuthOk(info),
      onControl: (type, payload) => onControl(type, payload),
      onVideo: (type, payload) => onVideo(type, payload),
      onAudio: (type, payload) => onAudio(type, payload),
      onTerminal: (type, payload) => terminal.handle(type, payload),
      onClosed: (info) => onClosed(conn, info),
    },
  });
  state.conn = conn;
  conn.connect();
}

function stopConnection() {
  clearInterval(state.pingTimer);
  state.pingTimer = 0;
  if (state.conn) {
    const c = state.conn;
    state.conn = null;
    c.close();
  }
  resetStream();
  terminal.onDisconnect();
  state.sources = [];
  renderSources();
}

function onAuthOk(info) {
  terminal.onConnected();
  el.hostName.textContent = info.hostName || state.credentials.id;
  setStageMessage('Chọn một cửa sổ hoặc màn hình để xem.');
  state.conn.sendControlJson(ControlType.Capabilities, {
    videoCodecs: state.caps.videoCodecs,
    audioCodecs: state.caps.audioCodecs,
  });
  sendPing();
  state.pingTimer = setInterval(sendPing, PING_INTERVAL_MS);
  setSourcesOpen(true);
}

function onClosed(conn, info) {
  if (state.conn !== conn) return; // an older connection we replaced
  state.conn = null;
  clearInterval(state.pingTimer);
  state.pingTimer = 0;
  resetStream();
  terminal.onDisconnect();
  el.latency.textContent = '– ms';
  if (info.authFailed) {
    state.credentials = null;
    showLogin();
    showLoginError('Sai password.');
    return;
  }
  setOverlay(info.reason || 'Mất kết nối.');
}

function resetStream() {
  state.streaming = false;
  state.activeSourceId = null;
  video.stop();
  video.clear();
  audio.stop();
  input.resetZoom();
  el.stopStream.disabled = true;
  renderSources();
}

// ---------- control channel ----------

function onControl(type, payload) {
  switch (type) {
    case ControlType.SourceList: {
      const msg = decodeJson(payload);
      if (!msg || !Array.isArray(msg.sources)) return;
      state.sources = msg.sources.filter((s) => s && typeof s.id === 'string');
      renderSources();
      return;
    }
    case ControlType.StreamStarted: {
      const msg = decodeJson(payload);
      if (!msg || typeof msg.codec !== 'string') return;
      state.streaming = true;
      state.activeSourceId = msg.sourceId ?? state.activeSourceId;
      video.start(msg.codec);
      input.resetZoom();
      setStageMessage('Đang chờ khung hình đầu tiên…');
      el.stopStream.disabled = false;
      renderSources();
      if (isNarrow()) setSourcesOpen(false);
      return;
    }
    case ControlType.StreamStopped: {
      const msg = decodeJson(payload);
      if (!msg) return;
      resetStream();
      setStageMessage(`Đã dừng xem${msg.reason ? ` (${msg.reason})` : ''}. Chọn nguồn khác.`);
      setSourcesOpen(true);
      return;
    }
    case ControlType.Ping:
      // Echo the 8 bytes back unchanged.
      if (payload.length >= 8) state.conn?.send(Channel.Control, ControlType.Pong, payload.slice(0, 8));
      return;
    case ControlType.Pong: {
      const sent = parsePing(payload);
      if (sent !== null) el.latency.textContent = `${Math.max(0, Date.now() - sent)} ms`;
      return;
    }
    case ControlType.Error: {
      const msg = decodeJson(payload);
      if (msg) setStageMessage(`Lỗi từ host: ${msg.message || msg.code || 'không rõ'}`);
      return;
    }
    default:
      return; // unknown control types are ignored
  }
}

function onVideo(type, payload) {
  if (type !== VideoType.Frame || !state.streaming) return;
  const frame = parseVideoFrame(payload);
  if (frame) video.push(frame);
}

function onAudio(type, payload) {
  if (type === AudioType.Config) audio.configure(parseAudioConfig(payload));
  else if (type === AudioType.Packet) {
    const packet = parseAudioPacket(payload);
    if (packet) audio.push(packet);
  }
}

function sendPing() {
  if (state.conn?.isAuthenticated) state.conn.send(Channel.Control, ControlType.Ping, buildPing(Date.now()));
}

function selectSource(source) {
  if (!state.conn?.isAuthenticated) return;
  const dpr = window.devicePixelRatio || 1;
  const longSide = Math.round(Math.max(screen.width, screen.height) * dpr);
  const shortSide = Math.round(Math.min(screen.width, screen.height) * dpr);
  const landscape = !(source.width > 0 && source.height > 0) || source.width >= source.height;
  resetStream();
  state.activeSourceId = source.id;
  renderSources();
  setStageMessage('Đang mở nguồn…');
  state.conn.sendControlJson(ControlType.SelectSource, {
    id: source.id,
    maxWidth: landscape ? longSide : shortSide,
    maxHeight: landscape ? shortSide : longSide,
    maxFps: MAX_FPS,
    bitrateKbps: Number(el.bitrate.value) || 4000,
    audio: state.caps.audioCodecs.includes('opus'),
  });
}

function renderSources() {
  el.sourceList.replaceChildren();
  if (!state.sources.length) {
    const li = document.createElement('li');
    li.className = 'meta';
    li.textContent = state.conn?.isAuthenticated ? 'Chưa có nguồn nào.' : '';
    el.sourceList.append(li);
    return;
  }
  const sorted = [...state.sources].sort((a, b) => (a.kind === b.kind ? 0 : a.kind === 'monitor' ? -1 : 1));
  for (const s of sorted) {
    const li = document.createElement('li');
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.dataset.sourceId = s.id;
    if (s.id === state.activeSourceId) btn.classList.add('active');
    const title = document.createElement('span');
    title.className = 'title';
    title.textContent = (s.kind === 'monitor' ? '🖥 ' : '🗔 ') + (s.title || s.id);
    const meta = document.createElement('span');
    meta.className = 'meta';
    meta.textContent = `${s.kind === 'monitor' ? 'Màn hình' : 'Cửa sổ'}${s.width && s.height ? ` · ${s.width}×${s.height}` : ''}`;
    btn.append(title, meta);
    btn.addEventListener('click', () => selectSource(s));
    li.append(btn);
    el.sourceList.append(li);
  }
}

// ---------- view helpers ----------

/**
 * Sizes the session view to the visual viewport so an on-screen keyboard (remote soft input or
 * the terminal's xterm textarea) pushes the bottom of the UI up instead of covering it.
 * Chrome Android with interactive-widget=resizes-content already shrinks the layout viewport,
 * so this is a no-op there; it covers older Chrome, Firefox and iOS Safari.
 */
function trackVisualViewport() {
  const vv = window.visualViewport;
  if (!vv) return;
  const root = document.documentElement;
  let raf = 0;
  const sync = () => {
    raf = 0;
    // Only while something covers the bottom (keyboard); otherwise keep the plain inset: 0 layout.
    const covered = vv.height < window.innerHeight - 1 || vv.offsetTop > 0;
    if (covered) {
      root.style.setProperty('--vv-top', `${vv.offsetTop}px`);
      root.style.setProperty('--vv-height', `${vv.height}px`);
    } else {
      root.style.removeProperty('--vv-top');
      root.style.removeProperty('--vv-height');
    }
    input.refreshOverlay();
  };
  const schedule = () => { if (!raf) raf = requestAnimationFrame(sync); };
  vv.addEventListener('resize', schedule);
  vv.addEventListener('scroll', schedule);
}

function showLogin() {
  terminal.setVisible(false);
  el.sessionView.hidden = true;
  el.loginView.hidden = false;
  if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
  el.password.focus();
}

function showSession() {
  el.loginView.hidden = true;
  el.sessionView.hidden = false;
}

function setStageMessage(text) {
  el.stageMessage.textContent = text;
  el.stageMessage.hidden = !text || video.hasFrame;
}

function setOverlay(text) {
  el.overlay.hidden = !text;
  el.overlayText.textContent = text || '';
}

function setSourcesOpen(open) {
  el.sourcesPanel.classList.toggle('collapsed', !open);
  input.refreshOverlay();
}

async function toggleMute() {
  try {
    await audio.setMuted(!audio.muted);
  } catch (e) {
    console.warn('Audio toggle failed', e);
  }
  el.mute.textContent = audio.muted ? '🔇' : '🔊';
}

function toggleFullscreen() {
  const root = document.documentElement;
  if (document.fullscreenElement || document.webkitFullscreenElement) {
    (document.exitFullscreen || document.webkitExitFullscreen).call(document)?.catch?.(() => {});
  } else {
    const req = root.requestFullscreen || root.webkitRequestFullscreen;
    if (req) Promise.resolve(req.call(root, { navigationUI: 'hide' })).catch(() => {});
  }
}

// ---------- init ----------

function init() {
  const saved = loadSaved();
  el.relay.value = saved?.relay || defaultRelayUrl();
  el.id.value = saved?.id || '';
  checkEnvironment();
  (el.id.value ? el.password : el.id).focus();

  el.loginForm.addEventListener('submit', onLoginSubmit);
  el.sourcesButton.addEventListener('click', () => setSourcesOpen(el.sourcesPanel.classList.contains('collapsed')));
  el.refreshSources.addEventListener('click', () => state.conn?.sendControlJson(ControlType.ListSources, {}));
  el.stopStream.addEventListener('click', () => {
    state.conn?.sendControlJson(ControlType.StopStream, {});
    input.releaseAll();
    resetStream();
    setStageMessage('Đã dừng xem. Chọn một cửa sổ hoặc màn hình để xem.');
  });
  el.disconnect.addEventListener('click', () => {
    input.releaseAll();
    stopConnection();
    state.credentials = null;
    showLogin();
  });
  el.reconnect.addEventListener('click', () => { if (state.credentials) startConnection(); else showLogin(); });
  el.back.addEventListener('click', () => { stopConnection(); state.credentials = null; setOverlay(null); showLogin(); });
  el.mute.addEventListener('click', toggleMute);
  el.fullscreen.addEventListener('click', toggleFullscreen);
  el.keyboard.addEventListener('click', () => input.openSoftKeyboard());
  try { const m = localStorage.getItem(TOUCH_MODE_KEY); if (m === 'touch' || m === 'mouse') el.touchMode.value = m; } catch { /* storage unavailable */ }
  input.setTouchMode(el.touchMode.value);
  el.touchMode.addEventListener('change', () => {
    input.setTouchMode(el.touchMode.value);
    try { localStorage.setItem(TOUCH_MODE_KEY, el.touchMode.value); } catch { /* storage unavailable */ }
  });
  if (!audio.supported) { el.mute.disabled = true; el.mute.title = 'Trình duyệt không hỗ trợ giải mã âm thanh'; }

  if (isTouchDevice) {
    el.touchMode.hidden = false;
    el.keyboard.hidden = false;
  }
  window.addEventListener('resize', () => input.refreshOverlay());
  trackVisualViewport();
  document.addEventListener('fullscreenchange', () => input.refreshOverlay());
  window.addEventListener('pagehide', () => { input.releaseAll(); stopConnection(); });
  // Keep a reference for debugging from the console.
  globalThis.focusRemote = { state, video, audio, input, terminal, InputType };
  // Read-only snapshot of decoder counters for debugging and the browser E2E test.
  Object.defineProperty(globalThis, '__frStats', {
    get: () => ({
      authenticated: !!state.conn?.isAuthenticated,
      streaming: state.streaming,
      activeSourceId: state.activeSourceId,
      sources: state.sources.map((s) => s.id),
      videoFramesReceived: video.framesReceived,
      videoFramesDecoded: video.framesDecoded,
      videoFramesDrawn: video.framesDrawn,
      videoDecoderErrors: video.decoderErrors,
      videoWidth: el.canvas.width,
      videoHeight: el.canvas.height,
      audioConfigured: !!audio.config,
      audioMuted: audio.muted,
      audioPacketsReceived: audio.packetsReceived,
      audioFramesDecoded: audio.framesDecoded,
      audioDecoderErrors: audio.decoderErrors,
      terminalVisible: terminal.visible,
      terminalCount: terminal.count,
      terminalOpenCount: terminal.openCount,
      terminalEnabled: terminal.shellInfo ? terminal.shellInfo.enabled : null,
    }),
  });
}

init();
