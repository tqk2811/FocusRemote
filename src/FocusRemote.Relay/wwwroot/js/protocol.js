// FocusRemote protocol v1 constants and payload codecs (docs/Protocol-vi.md).
// Environment-agnostic: no DOM, no WebSocket; works in browsers and Node.

export const PROTOCOL_VERSION = 1;
export const NONCE_LENGTH = 32;
export const TAG_LENGTH = 16;
export const COUNTER_LENGTH = 8;
export const PBKDF2_ITERATIONS = 600000;
export const SALT_PREFIX = 'FocusRemote/v1/salt/';
export const SESSION_INFO = 'FocusRemote/v1/session';
export const PROBE_TEXT = 'FocusRemote/v1/probe';
export const MIN_PASSWORD_LENGTH = 10;
export const ID_PATTERN = /^[A-Za-z0-9_-]{3,64}$/;

export const FrameKind = Object.freeze({ HelloViewer: 0x01, HelloHost: 0x02, AuthFail: 0x03, Busy: 0x04, Sealed: 0x10 });
export const Channel = Object.freeze({ Control: 0, Video: 1, Input: 2, Audio: 3, Terminal: 4 });
export const ControlType = Object.freeze({
  AuthProbe: 0x01, AuthOk: 0x02, Capabilities: 0x03,
  ListSources: 0x10, SourceList: 0x11, SelectSource: 0x12, StreamStarted: 0x13,
  StopStream: 0x14, StreamStopped: 0x15, RequestKeyframe: 0x16, SetInputMode: 0x17,
  Ping: 0x20, Pong: 0x21, Error: 0x30,
});
export const VideoType = Object.freeze({ Frame: 0x01 });
export const InputType = Object.freeze({ MouseMove: 0x01, MouseButton: 0x02, MouseWheel: 0x03, Key: 0x04, Text: 0x05, ReleaseAll: 0x06 });
export const AudioType = Object.freeze({ Config: 0x01, Packet: 0x02 });
export const TerminalType = Object.freeze({
  ListShells: 0x01, ShellList: 0x02, Open: 0x10, Opened: 0x11, Closed: 0x12,
  Input: 0x20, Output: 0x21, Resize: 0x22, Close: 0x23,
});
export const MAX_TERMINALS = 8;
export const MouseButton = Object.freeze({ Left: 0, Right: 1, Middle: 2, X1: 3, X2: 4 });

const utf8Encoder = new TextEncoder();
const utf8Decoder = new TextDecoder();

export function utf8(text) { return utf8Encoder.encode(text); }
export function fromUtf8(bytes) { return utf8Decoder.decode(bytes); }

export function toHex(bytes) {
  let s = '';
  for (const b of bytes) s += b.toString(16).padStart(2, '0');
  return s;
}

export function fromHex(hex) {
  const out = new Uint8Array(hex.length / 2);
  for (let i = 0; i < out.length; i++) out[i] = parseInt(hex.substr(i * 2, 2), 16);
  return out;
}

export function concatBytes(...parts) {
  let len = 0;
  for (const p of parts) len += p.length;
  const out = new Uint8Array(len);
  let off = 0;
  for (const p of parts) { out.set(p, off); off += p.length; }
  return out;
}

export function isValidId(id) { return typeof id === 'string' && ID_PATTERN.test(id); }
export function isValidPassword(password) { return typeof password === 'string' && password.length >= MIN_PASSWORD_LENGTH; }

// ---- Handshake frames (§2) ----

export function buildHello(kind, nonce) {
  if (nonce.length !== NONCE_LENGTH) throw new Error('nonce must be 32 bytes');
  const out = new Uint8Array(2 + NONCE_LENGTH);
  out[0] = kind;
  out[1] = PROTOCOL_VERSION;
  out.set(nonce, 2);
  return out;
}

/** Returns the 32-byte nonce of a HelloHost/HelloViewer frame, or null if malformed. */
export function parseHello(frame, expectedKind) {
  if (frame.length < 2 + NONCE_LENGTH || frame[0] !== expectedKind || frame[1] !== PROTOCOL_VERSION) return null;
  return frame.slice(2, 2 + NONCE_LENGTH);
}

// ---- Plaintext (§5) ----

export function buildPlaintext(channel, type, payload) {
  const out = new Uint8Array(2 + payload.length);
  out[0] = channel;
  out[1] = type;
  out.set(payload, 2);
  return out;
}

export function parsePlaintext(plain) {
  if (plain.length < 2) return null;
  return { channel: plain[0], type: plain[1], payload: plain.subarray(2) };
}

// ---- Control (§5.1) ----

export function encodeJson(obj) { return utf8(JSON.stringify(obj)); }

/** Parses a JSON payload; returns null on malformed JSON or non-object (packet is then dropped). */
export function decodeJson(payload) {
  try {
    const v = JSON.parse(fromUtf8(payload));
    return v !== null && typeof v === 'object' ? v : null;
  } catch {
    return null;
  }
}

export function buildPing(timestampMs) {
  const out = new Uint8Array(8);
  new DataView(out.buffer).setBigUint64(0, BigInt(Math.floor(timestampMs)), true);
  return out;
}

/** Reads the u64 ms timestamp of a Ping/Pong payload as a Number, or null when too short. */
export function parsePing(payload) {
  if (payload.length < 8) return null;
  return Number(new DataView(payload.buffer, payload.byteOffset, 8).getBigUint64(0, true));
}

// ---- Video (§5.2) ----

export function buildVideoFrame(isKeyframe, timestampUs, data) {
  const out = new Uint8Array(9 + data.length);
  out[0] = isKeyframe ? 1 : 0;
  new DataView(out.buffer).setBigUint64(1, BigInt(timestampUs), true);
  out.set(data, 9);
  return out;
}

export function parseVideoFrame(payload) {
  if (payload.length < 9) return null;
  const dv = new DataView(payload.buffer, payload.byteOffset, payload.byteLength);
  return {
    isKeyframe: (payload[0] & 1) !== 0,
    timestampUs: Number(dv.getBigUint64(1, true)),
    data: payload.subarray(9),
  };
}

// ---- Audio (§5.4) ----

export function parseAudioConfig(payload) {
  const v = decodeJson(payload);
  if (!v || typeof v.codec !== 'string' || !Number.isFinite(v.sampleRate) || !Number.isFinite(v.channels)) return null;
  return v;
}

export function parseAudioPacket(payload) {
  if (payload.length < 8) return null;
  const dv = new DataView(payload.buffer, payload.byteOffset, payload.byteLength);
  return { timestampUs: Number(dv.getBigUint64(0, true)), data: payload.subarray(8) };
}

// ---- Input (§5.3) ----

function clampU16(v) { return Math.max(0, Math.min(65535, Math.round(v))); }
function clampI16(v) { return Math.max(-32768, Math.min(32767, Math.round(v))); }

export function buildMouseMove(x, y) {
  const out = new Uint8Array(4);
  const dv = new DataView(out.buffer);
  dv.setUint16(0, clampU16(x), true);
  dv.setUint16(2, clampU16(y), true);
  return out;
}

export function buildMouseButton(button, down, x, y) {
  const out = new Uint8Array(6);
  const dv = new DataView(out.buffer);
  out[0] = button;
  out[1] = down ? 1 : 0;
  dv.setUint16(2, clampU16(x), true);
  dv.setUint16(4, clampU16(y), true);
  return out;
}

export function buildMouseWheel(deltaX, deltaY, x, y) {
  const out = new Uint8Array(8);
  const dv = new DataView(out.buffer);
  dv.setInt16(0, clampI16(deltaX), true);
  dv.setInt16(2, clampI16(deltaY), true);
  dv.setUint16(4, clampU16(x), true);
  dv.setUint16(6, clampU16(y), true);
  return out;
}

export function buildKey(down, code) {
  if (!/^[\x20-\x7e]{1,255}$/.test(code)) throw new Error('key code must be 1-255 printable ASCII chars');
  const out = new Uint8Array(2 + code.length);
  out[0] = down ? 1 : 0;
  out[1] = code.length;
  for (let i = 0; i < code.length; i++) out[2 + i] = code.charCodeAt(i);
  return out;
}

export function buildText(text) { return utf8(text); }

// ---- Terminal (§5.5) ----

/** Input (V->H) / Output (H->V) payload: [u32 LE terminalId][bytes]. */
export function buildTerminalData(terminalId, data) {
  const out = new Uint8Array(4 + data.length);
  new DataView(out.buffer).setUint32(0, terminalId >>> 0, true);
  out.set(data, 4);
  return out;
}

export function parseTerminalData(payload) {
  if (payload.length < 4) return null;
  const dv = new DataView(payload.buffer, payload.byteOffset, payload.byteLength);
  return { terminalId: dv.getUint32(0, true), data: payload.subarray(4) };
}

/** Resize payload: [u32 LE terminalId][u16 LE cols][u16 LE rows]. */
export function buildTerminalResize(terminalId, cols, rows) {
  const out = new Uint8Array(8);
  const dv = new DataView(out.buffer);
  dv.setUint32(0, terminalId >>> 0, true);
  dv.setUint16(4, clampU16(cols), true);
  dv.setUint16(6, clampU16(rows), true);
  return out;
}

/** Close payload: [u32 LE terminalId]. */
export function buildTerminalClose(terminalId) {
  const out = new Uint8Array(4);
  new DataView(out.buffer).setUint32(0, terminalId >>> 0, true);
  return out;
}

/** Returns { enabled, shells: [{ id, name }] } or null when malformed. */
export function parseShellList(payload) {
  const v = decodeJson(payload);
  if (!v || typeof v.enabled !== 'boolean') return null;
  const shells = Array.isArray(v.shells)
    ? v.shells.filter((s) => s && typeof s.id === 'string').map((s) => ({ id: s.id, name: typeof s.name === 'string' && s.name ? s.name : s.id }))
    : [];
  return { enabled: v.enabled, shells };
}

/** Opened / Closed payloads are JSON with a numeric terminalId; returns the object or null. */
export function parseTerminalEvent(payload) {
  const v = decodeJson(payload);
  return v && Number.isInteger(v.terminalId) ? v : null;
}
