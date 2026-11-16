// Local (viewer-side) zoom/pan of the video. Nothing is sent to the host.
// The canvas gets `transform: translate(tx, ty) scale(zoom)` with origin 0 0, so
// canvas.getBoundingClientRect() (and therefore VideoPlayer.getVideoRect / normalizePoint)
// already reflects the zoomed view and input keeps landing on the right remote pixel.
// All coordinates below are stage-local CSS pixels.

export const MIN_ZOOM = 1;
export const MAX_ZOOM = 5;

/** Rectangle where a vw x vh video sits inside a sw x sh box with object-fit: contain. */
export function containRect(sw, sh, vw, vh) {
  if (!sw || !sh || !vw || !vh) return null;
  const scale = Math.min(sw / vw, sh / vh);
  const w = vw * scale, h = vh * scale;
  return { left: (sw - w) / 2, top: (sh - h) / 2, width: w, height: h };
}

function clampAxis(t, zoom, start, size, stageSize) {
  const zs = start * zoom, zsize = size * zoom;
  if (zsize <= stageSize) return (stageSize - zsize) / 2 - zs; // smaller than the stage: centre it
  return Math.min(-zs, Math.max(stageSize - zs - zsize, t));    // larger: no empty band at either edge
}

/**
 * Clamps zoom to [MIN_ZOOM, MAX_ZOOM] and the translation so the video covers the stage.
 * @param {{zoom:number, tx:number, ty:number}} view
 * @param {{left:number, top:number, width:number, height:number}} base unzoomed video rect
 */
export function clampView(view, base, sw, sh) {
  const zoom = Math.max(MIN_ZOOM, Math.min(MAX_ZOOM, view.zoom));
  if (zoom === MIN_ZOOM) return { zoom, tx: 0, ty: 0 };
  return {
    zoom,
    tx: clampAxis(view.tx, zoom, base.left, base.width, sw),
    ty: clampAxis(view.ty, zoom, base.top, base.height, sh),
  };
}

/** Sets a new zoom keeping stage point (ax, ay) fixed on screen. */
export function zoomAt(view, newZoom, ax, ay, base, sw, sh) {
  const z = Math.max(MIN_ZOOM, Math.min(MAX_ZOOM, newZoom));
  const k = z / view.zoom;
  return clampView({ zoom: z, tx: ax - (ax - view.tx) * k, ty: ay - (ay - view.ty) * k }, base, sw, sh);
}

/** Pans by (dx, dy) stage pixels. */
export function panBy(view, dx, dy, base, sw, sh) {
  return clampView({ zoom: view.zoom, tx: view.tx + dx, ty: view.ty + dy }, base, sw, sh);
}

/** Maps a normalized video point (0..1) to stage coordinates under the view. */
export function videoToStage(view, base, nx, ny) {
  return {
    x: view.tx + (base.left + nx * base.width) * view.zoom,
    y: view.ty + (base.top + ny * base.height) * view.zoom,
  };
}

/** Pans the minimum amount so stage point (px, py) stays at least `margin` px inside the stage. */
export function ensureVisible(view, px, py, base, sw, sh, margin) {
  const m = Math.min(margin, sw / 2, sh / 2);
  let dx = 0, dy = 0;
  if (px < m) dx = m - px; else if (px > sw - m) dx = sw - m - px;
  if (py < m) dy = m - py; else if (py > sh - m) dy = sh - m - py;
  return dx || dy ? panBy(view, dx, dy, base, sw, sh) : view;
}

/** Owns the zoom state of one canvas inside one stage element. */
export class ViewZoom {
  constructor(stage, canvas) {
    this.stage = stage;
    this.canvas = canvas;
    this.view = { zoom: 1, tx: 0, ty: 0 };
    canvas.style.transformOrigin = '0 0';
  }

  get zoomed() { return this.view.zoom > MIN_ZOOM; }

  /** Stage size and unzoomed video rect, or null when nothing is displayed. */
  geometry() {
    const sw = this.stage.clientWidth, sh = this.stage.clientHeight;
    const base = containRect(sw, sh, this.canvas.width, this.canvas.height);
    return base ? { sw, sh, base } : null;
  }

  /** Client -> stage-local coordinates. */
  toStage(clientX, clientY) {
    const r = this.stage.getBoundingClientRect();
    return { x: clientX - r.left, y: clientY - r.top };
  }

  zoomAt(zoom, ax, ay) { this._update((v, g) => zoomAt(v, zoom, ax, ay, g.base, g.sw, g.sh)); }
  panBy(dx, dy) { this._update((v, g) => panBy(v, dx, dy, g.base, g.sw, g.sh)); }

  /** Keeps a normalized video point visible (mouse-mode cursor). */
  ensureVisible(nx, ny, margin) {
    if (!this.zoomed) return;
    this._update((v, g) => {
      const p = videoToStage(v, g.base, nx, ny);
      return ensureVisible(v, p.x, p.y, g.base, g.sw, g.sh, margin);
    });
  }

  /** Re-clamps after a layout change (resize, keyboard, sources panel). */
  refresh() { this._update((v, g) => clampView(v, g.base, g.sw, g.sh)); }

  reset() {
    this.view = { zoom: 1, tx: 0, ty: 0 };
    this._apply();
  }

  _update(fn) {
    const g = this.geometry();
    if (!g) return;
    this.view = fn(this.view, g);
    this._apply();
  }

  _apply() {
    const v = this.view;
    this.canvas.style.transform = v.zoom === 1 ? '' : `translate(${v.tx}px, ${v.ty}px) scale(${v.zoom})`;
  }
}
