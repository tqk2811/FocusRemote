// Viewer side of the relay connection and the handshake (docs/Protocol-vi.md §1, §4).

import {
  FrameKind, Channel, ControlType, NONCE_LENGTH, PROBE_TEXT,
  utf8, buildHello, parseHello, decodeJson,
} from './protocol.js';
import { randomBytes, deriveSessionKeys, SecureSession } from './crypto.js';

// Host backs off up to 30s between failed probes, so allow a bit more before giving up.
const AUTH_TIMEOUT_MS = 40000;

const CLOSE_REASONS = {
  4000: 'Id không hợp lệ.',
  4004: 'Host với Id này chưa trực tuyến.',
  4009: 'Host đang có người xem khác.',
  4029: 'Thử quá nhiều lần, hãy chờ một phút.',
};

/** Builds ws(s)://<relay>/ws/viewer/<id>; relayUrl may be http(s) or ws(s), with or without a sub-path. */
export function buildViewerUrl(relayUrl, id) {
  const base = new URL(relayUrl.endsWith('/') ? relayUrl : relayUrl + '/');
  if (base.protocol === 'https:') base.protocol = 'wss:';
  else if (base.protocol === 'http:') base.protocol = 'ws:';
  return new URL('ws/viewer/' + encodeURIComponent(id), base).toString();
}

/**
 * One viewer session. Callbacks (all optional):
 *   onStatus(text), onAuthOk(info), onControl(type, payload), onVideo(type, payload),
 *   onAudio(type, payload), onTerminal(type, payload), onClosed({ reason, authFailed, peerLeft, wasAuthenticated }).
 */
export class ViewerConnection {
  constructor({ relayUrl, id, rootKey, handlers }) {
    this.relayUrl = relayUrl;
    this.id = id;
    this.rootKey = rootKey;
    this.h = handlers || {};
    this.ws = null;
    this.session = null;
    this.nonceV = null;
    this.state = 'idle'; // idle | connecting | paired | probing | authenticated | closed
    this._authTimer = 0;
    this._closedReported = false;
  }

  get isAuthenticated() { return this.state === 'authenticated'; }

  connect() {
    this.state = 'connecting';
    this._status('Đang kết nối relay…');
    let ws;
    try {
      ws = new WebSocket(buildViewerUrl(this.relayUrl, this.id));
    } catch (e) {
      this._finish({ reason: 'URL relay không hợp lệ: ' + e.message });
      return;
    }
    ws.binaryType = 'arraybuffer';
    this.ws = ws;
    ws.onopen = () => this._status('Đã kết nối relay, chờ ghép với host…');
    ws.onmessage = (ev) => {
      if (typeof ev.data === 'string') this._onText(ev.data);
      else this._onBinary(new Uint8Array(ev.data)).catch((e) => this._fail('Lỗi xử lý gói: ' + e.message));
    };
    ws.onclose = (ev) => {
      const reason = CLOSE_REASONS[ev.code] || (this.state === 'authenticated' ? 'Mất kết nối.' : `Kết nối đóng (mã ${ev.code}).`);
      this._finish({ reason });
    };
    ws.onerror = () => {};
    this._authTimer = setTimeout(() => {
      if (this.state !== 'authenticated') this._fail('Hết thời gian chờ xác thực.');
    }, AUTH_TIMEOUT_MS);
  }

  close(reason = 'Đã ngắt kết nối.') {
    this._finish({ reason });
  }

  /** Seals and sends one message; silently ignored before authentication or after close. */
  send(channel, type, payload) {
    if (!this.session || !this.ws || this.state === 'closed') return;
    const ws = this.ws;
    this.session.seal(channel, type, payload).then((frame) => {
      if (ws.readyState === WebSocket.OPEN) ws.send(frame);
    }, () => {});
  }

  sendControlJson(type, obj) { this.send(Channel.Control, type, utf8(JSON.stringify(obj))); }

  /** Bytes queued in the socket; used to skip non-essential sends under congestion. */
  get bufferedAmount() { return this.ws ? this.ws.bufferedAmount : 0; }

  _onText(text) {
    const msg = (() => { try { return JSON.parse(text); } catch { return null; } })();
    if (!msg) return;
    if (msg.type === 'paired' && this.state === 'connecting') {
      this.state = 'paired';
      this._status('Đã ghép với host, đang bắt tay…');
      this.nonceV = randomBytes(NONCE_LENGTH);
      this.ws.send(buildHello(FrameKind.HelloViewer, this.nonceV));
    } else if (msg.type === 'peer-left') {
      this._finish({ reason: 'Host đã rời phiên.', peerLeft: true });
    }
  }

  async _onBinary(frame) {
    if (frame.length === 0 || this.state === 'closed') return;
    switch (frame[0]) {
      case FrameKind.HelloHost: {
        if (this.state !== 'paired' || this.session) return;
        const nonceH = parseHello(frame, FrameKind.HelloHost);
        if (!nonceH) { this._fail('HelloHost không hợp lệ (khác phiên bản giao thức?).'); return; }
        this.state = 'probing';
        const keys = await deriveSessionKeys(this.rootKey, this.nonceV, nonceH);
        this.session = await SecureSession.create(keys, 'viewer');
        this.send(Channel.Control, ControlType.AuthProbe, utf8(PROBE_TEXT));
        this._status('Đang kiểm tra password…');
        return;
      }
      case FrameKind.AuthFail:
        if (this.state === 'probing' || this.state === 'paired')
          this._finish({ reason: 'Sai password.', authFailed: true });
        return;
      case FrameKind.Sealed: {
        if (!this.session) return; // before handshake: ignore
        const msg = await this.session.open(frame);
        if (this.state === 'closed') return;
        if (!msg) { this._fail('Gói mã hoá không hợp lệ hoặc bị phát lại; đóng phiên.'); return; }
        this._dispatch(msg);
        return;
      }
      default:
        return;
    }
  }

  _dispatch({ channel, type, payload }) {
    if (this.state === 'probing') {
      if (channel === Channel.Control && type === ControlType.AuthOk) {
        this.state = 'authenticated';
        clearTimeout(this._authTimer);
        this._status('Đã xác thực.');
        this.h.onAuthOk?.(decodeJson(payload) || {});
      }
      return; // anything else before AuthOk is ignored
    }
    if (this.state !== 'authenticated') return;
    switch (channel) {
      case Channel.Control: this.h.onControl?.(type, payload); break;
      case Channel.Video: this.h.onVideo?.(type, payload); break;
      case Channel.Audio: this.h.onAudio?.(type, payload); break;
      case Channel.Terminal: this.h.onTerminal?.(type, payload); break;
      default: break; // unknown channels are ignored
    }
  }

  _fail(reason) { this._finish({ reason }); }

  _finish(info) {
    const wasAuthenticated = this.state === 'authenticated';
    this.state = 'closed';
    clearTimeout(this._authTimer);
    if (this.ws) {
      const ws = this.ws;
      ws.onmessage = ws.onclose = ws.onopen = null;
      try { ws.close(1000); } catch { /* ignore */ }
    }
    this.session = null;
    if (this._closedReported) return;
    this._closedReported = true;
    this.h.onClosed?.({ authFailed: false, peerLeft: false, ...info, wasAuthenticated });
  }

  _status(text) { this.h.onStatus?.(text); }
}
