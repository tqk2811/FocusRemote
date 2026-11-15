// FocusRemote key derivation and Sealed frames (docs/Protocol-vi.md §3).
// Environment-agnostic: needs only globalThis.crypto (browsers in a secure context, Node >= 19).

import {
  FrameKind, COUNTER_LENGTH, TAG_LENGTH, PBKDF2_ITERATIONS, SALT_PREFIX, SESSION_INFO,
  utf8, concatBytes, buildPlaintext, parsePlaintext,
} from './protocol.js';

function subtle() {
  const s = globalThis.crypto && globalThis.crypto.subtle;
  if (!s) throw new Error('WebCrypto is unavailable (the page must be served over HTTPS or localhost).');
  return s;
}

export function randomBytes(length) {
  const out = new Uint8Array(length);
  globalThis.crypto.getRandomValues(out);
  return out;
}

/** salt = SHA-256(UTF8("FocusRemote/v1/salt/" + id))[0..16] */
export async function deriveSalt(id) {
  const digest = new Uint8Array(await subtle().digest('SHA-256', utf8(SALT_PREFIX + id)));
  return digest.slice(0, 16);
}

/** rootKey = PBKDF2-HMAC-SHA256(UTF8(password), salt, iterations, 32 bytes) */
export async function deriveRootKey(password, salt, iterations = PBKDF2_ITERATIONS) {
  const base = await subtle().importKey('raw', utf8(password), 'PBKDF2', false, ['deriveBits']);
  const bits = await subtle().deriveBits({ name: 'PBKDF2', hash: 'SHA-256', salt, iterations }, base, 256);
  return new Uint8Array(bits);
}

/** okm = HKDF-SHA256(rootKey, nonceV || nonceH, "FocusRemote/v1/session", 64); returns raw key bytes. */
export async function deriveSessionKeys(rootKey, nonceV, nonceH) {
  const ikm = await subtle().importKey('raw', rootKey, 'HKDF', false, ['deriveBits']);
  const okm = new Uint8Array(await subtle().deriveBits(
    { name: 'HKDF', hash: 'SHA-256', salt: concatBytes(nonceV, nonceH), info: utf8(SESSION_INFO) }, ikm, 512));
  return { keyH2V: okm.slice(0, 32), keyV2H: okm.slice(32, 64) };
}

export function buildNonce(counter) {
  const nonce = new Uint8Array(12);
  new DataView(nonce.buffer).setBigUint64(0, BigInt(counter), true);
  return nonce;
}

const AAD = new Uint8Array([FrameKind.Sealed]);
const HEADER_LENGTH = 1 + COUNTER_LENGTH;

/**
 * Encrypted session for one role ('viewer' or 'host'). Counters are assigned synchronously at call
 * time and each direction is processed through a promise chain, so frames leave/arrive strictly in
 * order even though WebCrypto is asynchronous.
 */
export class SecureSession {
  static async create(keys, role = 'viewer') {
    const sendRaw = role === 'host' ? keys.keyH2V : keys.keyV2H;
    const recvRaw = role === 'host' ? keys.keyV2H : keys.keyH2V;
    const [sendKey, recvKey] = await Promise.all([
      subtle().importKey('raw', sendRaw, 'AES-GCM', false, ['encrypt']),
      subtle().importKey('raw', recvRaw, 'AES-GCM', false, ['decrypt']),
    ]);
    return new SecureSession(sendKey, recvKey);
  }

  constructor(sendKey, recvKey) {
    this._sendKey = sendKey;
    this._recvKey = recvKey;
    this._sendCounter = 0n;
    this._lastReceived = -1n;
    this._sendChain = Promise.resolve();
    this._recvChain = Promise.resolve();
  }

  get nextSendCounter() { return this._sendCounter; }

  /** Seals one message; resolves to the full Sealed frame (0x10 || counter || ciphertext || tag). */
  seal(channel, type, payload) {
    const counter = this._sendCounter++;
    const plain = buildPlaintext(channel, type, payload);
    const job = this._sendChain.then(async () => {
      const ct = new Uint8Array(await subtle().encrypt(
        { name: 'AES-GCM', iv: buildNonce(counter), additionalData: AAD, tagLength: TAG_LENGTH * 8 }, this._sendKey, plain));
      const frame = new Uint8Array(HEADER_LENGTH + ct.length);
      frame[0] = FrameKind.Sealed;
      new DataView(frame.buffer).setBigUint64(1, counter, true);
      frame.set(ct, HEADER_LENGTH);
      return frame;
    });
    this._sendChain = job.catch(() => {});
    return job;
  }

  /**
   * Opens a Sealed frame. Resolves to {channel, type, payload} or null when the frame is malformed,
   * fails authentication or replays a counter (the caller must then close the session).
   * Calls are processed in order; the replay window is updated only for authentic frames.
   */
  open(frame) {
    const job = this._recvChain.then(async () => {
      if (frame.length < HEADER_LENGTH + 2 + TAG_LENGTH || frame[0] !== FrameKind.Sealed) return null;
      const counter = new DataView(frame.buffer, frame.byteOffset, frame.byteLength).getBigUint64(1, true);
      if (counter <= this._lastReceived) return null;
      let plain;
      try {
        plain = new Uint8Array(await subtle().decrypt(
          { name: 'AES-GCM', iv: buildNonce(counter), additionalData: AAD, tagLength: TAG_LENGTH * 8 },
          this._recvKey, frame.subarray(HEADER_LENGTH)));
      } catch {
        return null;
      }
      this._lastReceived = counter;
      return parsePlaintext(plain);
    });
    this._recvChain = job.catch(() => {});
    return job;
  }
}
