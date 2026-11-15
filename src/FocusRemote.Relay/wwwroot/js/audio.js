// Opus audio playback via WebCodecs AudioDecoder + scheduled AudioBufferSourceNodes (docs/Protocol-vi.md §5.4).
// Robust by design: missing APIs, bad config or decoder errors only disable audio, never the session.

const START_DELAY_S = 0.06;   // jitter buffer on (re)start
const MAX_AHEAD_S = 0.4;      // scheduled further than this => drop and resync to keep latency low

export async function detectAudioCodecs() {
  if (typeof AudioDecoder === 'undefined') return [];
  try {
    const r = await AudioDecoder.isConfigSupported({ codec: 'opus', sampleRate: 48000, numberOfChannels: 2 });
    return r.supported ? ['opus'] : [];
  } catch {
    return [];
  }
}

export class AudioPlayer {
  constructor() {
    this.muted = true;
    this.config = null;
    this.decoder = null;
    this.ctx = null;
    this.nextTime = 0;
    this.gain = null;
    // Debug counters (exposed as window.__frStats, used by the browser E2E test).
    this.packetsReceived = 0;
    this.framesDecoded = 0;
    this.decoderErrors = 0;
  }

  get supported() { return typeof AudioDecoder !== 'undefined' && (globalThis.AudioContext || globalThis.webkitAudioContext); }

  /** Must be called from a user gesture to unmute (autoplay policy). */
  async setMuted(muted) {
    this.muted = muted;
    if (!muted) {
      if (!this.ctx) {
        const Ctx = globalThis.AudioContext || globalThis.webkitAudioContext;
        this.ctx = new Ctx({ latencyHint: 'interactive' });
        this.gain = this.ctx.createGain();
        this.gain.connect(this.ctx.destination);
      }
      if (this.ctx.state === 'suspended') await this.ctx.resume();
      this.nextTime = 0;
    } else if (this.ctx && this.ctx.state === 'running') {
      await this.ctx.suspend();
    }
  }

  /** Audio Config message. */
  configure(config) {
    if (!config || config.codec !== 'opus') { this.config = null; this._closeDecoder(); return; }
    this.config = { codec: 'opus', sampleRate: config.sampleRate | 0 || 48000, numberOfChannels: config.channels | 0 || 2 };
    this._closeDecoder();
    this.nextTime = 0;
  }

  /** Audio Packet message ({ timestampUs, data }). */
  push(packet) {
    this.packetsReceived++;
    if (this.muted || !this.config || !this.ctx || typeof AudioDecoder === 'undefined') return;
    if (!this.decoder && !this._openDecoder()) return;
    try {
      this.decoder.decode(new EncodedAudioChunk({ type: 'key', timestamp: packet.timestampUs, data: packet.data }));
    } catch (e) {
      console.warn('Audio decode failed', e);
      this._closeDecoder();
    }
  }

  stop() {
    this._closeDecoder();
    this.config = null;
    this.nextTime = 0;
  }

  _openDecoder() {
    try {
      this.decoder = new AudioDecoder({
        output: (d) => this._onOutput(d),
        error: (e) => { console.warn('Audio decoder error', e); this.decoderErrors++; this._closeDecoder(); },
      });
      this.decoder.configure(this.config);
      return true;
    } catch (e) {
      console.warn('AudioDecoder configure failed', e);
      this.decoder = null;
      this.config = null; // unsupported config: stop trying until the next Config
      return false;
    }
  }

  _closeDecoder() {
    if (this.decoder) {
      try { if (this.decoder.state !== 'closed') this.decoder.close(); } catch { /* ignore */ }
      this.decoder = null;
    }
  }

  _onOutput(data) {
    this.framesDecoded++;
    try {
      if (this.muted || !this.ctx) return;
      const channels = data.numberOfChannels, frames = data.numberOfFrames;
      const buffer = this.ctx.createBuffer(channels, frames, data.sampleRate);
      for (let c = 0; c < channels; c++) {
        const plane = new Float32Array(frames);
        data.copyTo(plane, { planeIndex: c, format: 'f32-planar' });
        buffer.copyToChannel(plane, c);
      }
      const now = this.ctx.currentTime;
      if (this.nextTime < now || this.nextTime - now > MAX_AHEAD_S) this.nextTime = now + START_DELAY_S;
      const src = this.ctx.createBufferSource();
      src.buffer = buffer;
      src.connect(this.gain);
      src.start(this.nextTime);
      this.nextTime += buffer.duration;
    } catch (e) {
      console.warn('Audio output failed', e);
    } finally {
      data.close();
    }
  }
}
