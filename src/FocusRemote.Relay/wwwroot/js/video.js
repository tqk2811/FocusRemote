// WebCodecs video decoding (H.264/H.265 Annex B) and canvas rendering (docs/Protocol-vi.md §5.2).

const H264_CANDIDATES = ['avc1.42E01F', 'avc1.4D401F', 'avc1.640028', 'avc1.42001F'];
const H265_CANDIDATES = ['hvc1.1.6.L120.90', 'hev1.1.6.L120.90', 'hvc1.1.6.L93.B0', 'hev1.1.6.L93.B0'];
// Too many chunks waiting in the decoder means we cannot keep up: skip to the next keyframe.
const MAX_DECODE_QUEUE = 8;

async function firstSupported(candidates) {
  for (const codec of candidates) {
    try {
      const r = await VideoDecoder.isConfigSupported({ codec, optimizeForLatency: true });
      if (r.supported) return codec;
    } catch { /* try next */ }
  }
  return null;
}

/** Returns { videoCodecs: ['h264', 'h265'?], codecStrings: { h264, h265 } } for the Capabilities message. */
export async function detectVideoCodecs() {
  if (typeof VideoDecoder === 'undefined') return { videoCodecs: [], codecStrings: {} };
  const [h264, h265] = await Promise.all([firstSupported(H264_CANDIDATES), firstSupported(H265_CANDIDATES)]);
  const videoCodecs = [];
  const codecStrings = {};
  if (h264) { videoCodecs.push('h264'); codecStrings.h264 = h264; }
  if (h265) { videoCodecs.push('h265'); codecStrings.h265 = h265; }
  return { videoCodecs, codecStrings };
}

/** Builds avc1.PPCCLL from the first SPS NAL unit in an Annex B buffer, or null. */
export function avcCodecFromAnnexB(data) {
  for (let i = 0; i + 4 < data.length; i++) {
    if (data[i] === 0 && data[i + 1] === 0 && data[i + 2] === 1) {
      const nal = i + 3;
      if ((data[nal] & 0x1f) === 7 && nal + 3 < data.length) {
        const hex = (b) => b.toString(16).padStart(2, '0').toUpperCase();
        return 'avc1.' + hex(data[nal + 1]) + hex(data[nal + 2]) + hex(data[nal + 3]);
      }
      i = nal - 1;
    }
  }
  return null;
}

export class VideoPlayer {
  /**
   * @param {HTMLCanvasElement} canvas
   * @param {{ codecStrings: object, onNeedKeyframe: () => void, onStats?: (s) => void, onFirstFrame?: () => void }} options
   */
  constructor(canvas, options) {
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d');
    this.opts = options;
    this.decoder = null;
    this.codec = null;
    this.configuredCodec = null;
    this.waitingKeyframe = true;
    this.pendingFrame = null;
    this.rafId = 0;
    this.framesDecoded = 0;
    this.hasFrame = false;
  }

  /** Called on StreamStarted. */
  start(codec) {
    this.stop();
    this.codec = codec;
    this.waitingKeyframe = true;
  }

  stop() {
    this._closeDecoder();
    this.codec = null;
    this.configuredCodec = null;
    if (this.pendingFrame) { this.pendingFrame.close(); this.pendingFrame = null; }
    if (this.rafId) { cancelAnimationFrame(this.rafId); this.rafId = 0; }
    this.hasFrame = false;
  }

  clear() {
    this.ctx.clearRect(0, 0, this.canvas.width, this.canvas.height);
  }

  /** Feeds one parsed Video Frame message ({ isKeyframe, timestampUs, data }). */
  push(frame) {
    if (!this.codec || typeof VideoDecoder === 'undefined') return;
    if (this.waitingKeyframe) {
      if (!frame.isKeyframe) return;
      if (!this._ensureDecoder(frame.data)) return;
      this.waitingKeyframe = false;
    }
    if (this.decoder.decodeQueueSize > MAX_DECODE_QUEUE && !frame.isKeyframe) {
      this.waitingKeyframe = true;
      this.opts.onNeedKeyframe();
      return;
    }
    try {
      this.decoder.decode(new EncodedVideoChunk({
        type: frame.isKeyframe ? 'key' : 'delta',
        timestamp: frame.timestampUs,
        data: frame.data,
      }));
    } catch (e) {
      this._onDecoderError(e);
    }
  }

  /** The rectangle (client coordinates) where the video is actually drawn inside the canvas element. */
  getVideoRect() {
    const r = this.canvas.getBoundingClientRect();
    const vw = this.canvas.width, vh = this.canvas.height;
    if (!this.hasFrame || !vw || !vh || !r.width || !r.height) return null;
    const scale = Math.min(r.width / vw, r.height / vh);
    const w = vw * scale, h = vh * scale;
    return { left: r.left + (r.width - w) / 2, top: r.top + (r.height - h) / 2, width: w, height: h };
  }

  _ensureDecoder(keyframeData) {
    let codecString;
    if (this.codec === 'h265') codecString = this.opts.codecStrings.h265 || 'hvc1.1.6.L120.90';
    else codecString = avcCodecFromAnnexB(keyframeData) || this.opts.codecStrings.h264 || 'avc1.42E01F';
    if (this.decoder && this.decoder.state === 'configured' && this.configuredCodec === codecString) return true;
    this._closeDecoder();
    try {
      this.decoder = new VideoDecoder({
        output: (f) => this._onOutput(f),
        error: (e) => this._onDecoderError(e),
      });
      // No description => the decoder expects Annex B input.
      this.decoder.configure({ codec: codecString, optimizeForLatency: true, hardwareAcceleration: 'no-preference' });
      this.configuredCodec = codecString;
      return true;
    } catch (e) {
      console.warn('VideoDecoder configure failed', codecString, e);
      this._closeDecoder();
      return false;
    }
  }

  _closeDecoder() {
    if (this.decoder) {
      const d = this.decoder;
      this.decoder = null;
      try { if (d.state !== 'closed') d.close(); } catch { /* ignore */ }
    }
    this.configuredCodec = null;
  }

  _onDecoderError(e) {
    console.warn('Video decoder error', e);
    this._closeDecoder();
    this.waitingKeyframe = true;
    this.opts.onNeedKeyframe();
  }

  _onOutput(frame) {
    if (this.pendingFrame) this.pendingFrame.close(); // only the newest frame is drawn
    this.pendingFrame = frame;
    this.framesDecoded++;
    if (!this.rafId) this.rafId = requestAnimationFrame(() => this._draw());
  }

  _draw() {
    this.rafId = 0;
    const frame = this.pendingFrame;
    this.pendingFrame = null;
    if (!frame) return;
    const w = frame.displayWidth, h = frame.displayHeight;
    if (this.canvas.width !== w || this.canvas.height !== h) {
      this.canvas.width = w;
      this.canvas.height = h;
    }
    this.ctx.drawImage(frame, 0, 0, w, h);
    frame.close();
    if (!this.hasFrame) {
      this.hasFrame = true;
      this.opts.onFirstFrame?.();
    }
  }
}
