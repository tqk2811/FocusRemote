// Mouse, keyboard and touch input (docs/Protocol-vi.md §5.3).
// Coordinates are normalized to u16 against the rectangle where the video is displayed.

import {
  InputType, MouseButton,
  buildMouseMove, buildMouseButton, buildMouseWheel, buildKey, buildText,
} from './protocol.js';
import { TouchGesture, LONG_PRESS_MS, TAP_SLOP_PX } from './gesture.js';

const TOUCH_SCROLL_FACTOR = 4;      // wheel units per CSS pixel of two-finger movement
const MOUSE_MODE_SPEED = 1.6;       // relative cursor speed multiplier in 'mouse' touch mode
const PINCH_THRESHOLD_PX = 24;      // finger distance change that turns a two-finger gesture into a pinch
const CURSOR_EDGE_MARGIN_PX = 40;   // zoomed mouse-mode view pans to keep the cursor this far from the edge
const SOFT_SENTINEL = ' ';     // kept in the hidden input so Backspace always fires an input event

// DOM MouseEvent.button -> protocol button
const DOM_BUTTON = { 0: MouseButton.Left, 1: MouseButton.Middle, 2: MouseButton.Right, 3: MouseButton.X1, 4: MouseButton.X2 };

/**
 * Character a key press types, for the host's message input mode (posted WM_KEYDOWN cannot carry Shift state):
 * e.key when it is a single code point and no Ctrl/Alt/Meta chord is held (AltGr counts as typing). Null otherwise.
 */
export function printableKeyText(e) {
  const key = e?.key;
  if (typeof key !== 'string' || [...key].length !== 1) return null;
  const code = key.codePointAt(0);
  if (code < 0x20 || code === 0x7f) return null;
  const altGr = typeof e.getModifierState === 'function' && e.getModifierState('AltGraph');
  if (!altGr && (e.ctrlKey || e.altKey)) return null;
  if (e.metaKey) return null;
  return key;
}

export function normalizePoint(clientX, clientY, rect) {
  const nx = (clientX - rect.left) / rect.width;
  const ny = (clientY - rect.top) / rect.height;
  return {
    x: Math.round(Math.max(0, Math.min(1, nx)) * 65535),
    y: Math.round(Math.max(0, Math.min(1, ny)) * 65535),
    inside: nx >= 0 && nx <= 1 && ny >= 0 && ny <= 1,
  };
}

/** Converts a WheelEvent delta to WHEEL_DELTA units (120 per notch, positive = up/right). */
export function wheelToUnits(delta, deltaMode) {
  const perUnit = deltaMode === 1 ? 40 : deltaMode === 2 ? 800 : 1.2; // line / page / pixel (100px ≈ 1 notch)
  return -delta * perUnit;
}

/**
 * Classifies a two-finger gesture: 'pinch' when the finger distance changed by more than the
 * threshold, 'scroll' when the midpoint moved (parallel movement), otherwise null (undecided).
 */
export function classifyTwoFinger(startDist, dist, startMid, mid, pinchThreshold = PINCH_THRESHOLD_PX, slop = TAP_SLOP_PX) {
  if (Math.abs(dist - startDist) > pinchThreshold) return 'pinch';
  if (Math.hypot(mid.x - startMid.x, mid.y - startMid.y) > slop) return 'scroll';
  return null;
}

/**
 * Two-finger scroll, "natural" direction (content follows the fingers), both axes at once.
 * Finger movement (CSS px) -> wheel units (protocol: dy > 0 = up, dx > 0 = right):
 * fingers up -> scroll down, fingers left -> scroll right.
 */
export function twoFingerScrollDelta(fingerDx, fingerDy, factor = TOUCH_SCROLL_FACTOR) {
  return { dx: -fingerDx * factor, dy: fingerDy * factor };
}

export class InputController {
  /**
   * @param {{ surface: HTMLElement, cursor: HTMLElement, softInput: HTMLTextAreaElement,
   *           getVideoRect: () => DOMRect|null, send: (type: number, payload: Uint8Array) => void,
   *           isActive: () => boolean, zoom?: import('./viewzoom.js').ViewZoom }} options
   */
  constructor(options) {
    this.o = options;
    this.mode = 'touch'; // touch mode: 'touch' (tap at the finger) | 'mouse' (relative cursor)
    this.textKeys = false; // host message mode: printable keys go as Text instead of Key
    this.pressedKeys = new Set();
    this.pressedButtons = new Set();
    this.pendingMove = null;
    this.moveRaf = 0;
    this.wheelAcc = { x: 0, y: 0 };
    this.cursor = { x: 0.5, y: 0.5 }; // mouse-mode cursor, normalized 0..1
    this.touch = null;
    this.gesture = new TouchGesture();
    this.dragging = false;      // left button held by a long-press drag
    this.lastTouchPoint = null; // client point of the last touch-mode finger: pinch anchor
    this.ring = null;           // long-press feedback ring, created lazily
    this._bind();
  }

  setTouchMode(mode) {
    this._cancelTouch();
    this.mode = mode === 'mouse' ? 'mouse' : 'touch';
    this._updateCursorOverlay();
  }

  /** Host message input mode on a window source: send printable key presses as Text (see printableKeyText). */
  setTextKeys(on) {
    this.textKeys = !!on;
  }

  releaseAll() {
    if (this.pressedKeys.size || this.pressedButtons.size || this.touch || this.dragging) {
      this.pressedKeys.clear();
      this.pressedButtons.clear();
      this._cancelTouch();
    }
    this._send(InputType.ReleaseAll, new Uint8Array(0));
  }

  openSoftKeyboard() {
    const el = this.o.softInput;
    el.value = SOFT_SENTINEL;
    el.focus({ preventScroll: true });
    el.setSelectionRange(el.value.length, el.value.length);
  }

  // ---------- plumbing ----------

  _send(type, payload) {
    if (this.o.isActive()) this.o.send(type, payload);
  }

  _point(clientX, clientY) {
    const rect = this.o.getVideoRect();
    return rect ? normalizePoint(clientX, clientY, rect) : null;
  }

  _bind() {
    const s = this.o.surface;
    s.addEventListener('contextmenu', (e) => e.preventDefault());
    s.addEventListener('mousemove', (e) => this._onMouseMove(e));
    s.addEventListener('mousedown', (e) => this._onMouseButton(e, true));
    window.addEventListener('mouseup', (e) => this._onMouseButton(e, false));
    s.addEventListener('wheel', (e) => this._onWheel(e), { passive: false });
    s.addEventListener('auxclick', (e) => e.preventDefault());

    s.addEventListener('touchstart', (e) => this._onTouchStart(e), { passive: false });
    s.addEventListener('touchmove', (e) => this._onTouchMove(e), { passive: false });
    s.addEventListener('touchend', (e) => this._onTouchEnd(e), { passive: false });
    s.addEventListener('touchcancel', () => this._cancelTouch(), { passive: false });

    window.addEventListener('keydown', (e) => this._onKey(e, true));
    window.addEventListener('keyup', (e) => this._onKey(e, false));
    window.addEventListener('blur', () => this.releaseAll());
    document.addEventListener('visibilitychange', () => { if (document.hidden) this.releaseAll(); });

    const soft = this.o.softInput;
    soft.addEventListener('input', (e) => this._onSoftInput(e));
    soft.addEventListener('compositionend', (e) => {
      if (e.data) this._send(InputType.Text, buildText(e.data));
      this._resetSoft();
    });
  }

  // ---------- mouse ----------

  _onMouseMove(e) {
    if (!this.o.isActive()) return;
    this.pendingMove = { x: e.clientX, y: e.clientY };
    if (!this.moveRaf) {
      this.moveRaf = requestAnimationFrame(() => {
        this.moveRaf = 0;
        const m = this.pendingMove;
        this.pendingMove = null;
        const p = m && this._point(m.x, m.y);
        if (p && (p.inside || this.pressedButtons.size)) this._send(InputType.MouseMove, buildMouseMove(p.x, p.y));
      });
    }
  }

  _onMouseButton(e, down) {
    if (!this.o.isActive()) return;
    const button = DOM_BUTTON[e.button];
    if (button === undefined) return;
    if (down) {
      const p = this._point(e.clientX, e.clientY);
      if (!p || !p.inside) return;
      e.preventDefault();
      this.o.surface.focus?.({ preventScroll: true });
      this.pressedButtons.add(button);
      this._send(InputType.MouseButton, buildMouseButton(button, true, p.x, p.y));
    } else {
      if (!this.pressedButtons.has(button)) return;
      this.pressedButtons.delete(button);
      const p = this._point(e.clientX, e.clientY) || { x: 0, y: 0 };
      this._send(InputType.MouseButton, buildMouseButton(button, false, p.x, p.y));
    }
  }

  _onWheel(e) {
    if (!this.o.isActive()) return;
    e.preventDefault();
    const p = this._point(e.clientX, e.clientY);
    if (!p) return;
    this._sendWheel(wheelToUnits(e.deltaX, e.deltaMode) * -1, wheelToUnits(e.deltaY, e.deltaMode), p);
  }

  /** Sends accumulated wheel movement; fractional remainders carry over to the next event. */
  _sendWheel(dx, dy, p) {
    this.wheelAcc.x += dx;
    this.wheelAcc.y += dy;
    const sx = Math.trunc(this.wheelAcc.x), sy = Math.trunc(this.wheelAcc.y);
    if (!sx && !sy) return;
    this.wheelAcc.x -= sx;
    this.wheelAcc.y -= sy;
    this._send(InputType.MouseWheel, buildMouseWheel(sx, sy, p.x, p.y));
  }

  // ---------- keyboard ----------

  _onKey(e, down) {
    if (!this.o.isActive()) return;
    const t = e.target;
    if (t === this.o.softInput) {
      // Physical keys pressed while the soft keyboard has focus: printable text arrives via 'input'.
      if (!['Enter', 'Backspace', 'Tab', 'Escape', 'ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Delete'].includes(e.code)) return;
      if (e.key === 'Unidentified' || e.keyCode === 229) return;
    } else if (t && (t.tagName === 'INPUT' || t.tagName === 'SELECT' || t.tagName === 'TEXTAREA')) {
      return; // typing into the viewer's own controls
    }
    if (!e.code || !/^[\x20-\x7e]{1,255}$/.test(e.code)) return;
    e.preventDefault();
    if (down && this.textKeys && t !== this.o.softInput) {
      const text = printableKeyText(e);
      // Not tracked as pressed, so its keyup is dropped too.
      if (text !== null) { this._send(InputType.Text, buildText(text)); return; }
    }
    if (down) this.pressedKeys.add(e.code);
    else if (!this.pressedKeys.delete(e.code)) return;
    this._send(InputType.Key, buildKey(down, e.code));
  }

  _tapKey(code) {
    this._send(InputType.Key, buildKey(true, code));
    this._send(InputType.Key, buildKey(false, code));
  }

  _onSoftInput(e) {
    if (e.isComposing) return;
    const type = e.inputType || '';
    if (type === 'deleteContentBackward') this._tapKey('Backspace');
    else if (type === 'deleteContentForward') this._tapKey('Delete');
    else if (type === 'insertLineBreak' || type === 'insertParagraph') this._tapKey('Enter');
    else if (type.startsWith('insert')) {
      const text = e.data ?? this.o.softInput.value.replace(SOFT_SENTINEL, '');
      if (text) this._send(InputType.Text, buildText(text));
    }
    this._resetSoft();
  }

  _resetSoft() {
    const el = this.o.softInput;
    el.value = SOFT_SENTINEL;
    el.setSelectionRange(el.value.length, el.value.length);
  }

  // ---------- touch ----------
  // One finger goes through the pure TouchGesture recognizer (js/gesture.js); its actions are
  // mapped per mode in _applyGesture. Two fingers, both modes: pinch = local zoom, parallel swipe =
  // natural remote scroll, two-finger tap (no movement) = right click (kept: harmless, handy).

  _onTouchStart(e) {
    if (!this.o.isActive()) return;
    e.preventDefault();
    const touches = e.touches;
    if (!this.touch) {
      const t = touches[0];
      this.touch = { maxFingers: 1, startX: t.clientX, startY: t.clientY, moved: false, scrollMid: null };
      if (this.mode === 'touch') this.lastTouchPoint = { x: t.clientX, y: t.clientY };
      this._applyGesture(this.gesture.down(t.clientX, t.clientY, performance.now()));
      this.touch.timer = setTimeout(() => this._applyGesture(this.gesture.longPress()), LONG_PRESS_MS);
    }
    if (touches.length >= 2) {
      clearTimeout(this.touch.timer);
      this._applyGesture(this.gesture.cancel());
      this.touch.maxFingers = Math.max(this.touch.maxFingers, touches.length);
      this.touch.scrollMid = midpoint(touches);
      this._startTwoFinger(touches);
    }
  }

  _startTwoFinger(touches) {
    const st = this.touch;
    const zoom = this.o.zoom;
    st.gesture = st.gesture === 'pinch' ? 'pinch' : null; // a pinch stays a pinch when fingers change
    st.startDist = distance(touches);
    st.startMid = midpoint(touches);
    st.pinchMid = st.startMid;
    st.startZoom = zoom ? zoom.view.zoom : 1;
    // Touch mode zooms around the last touch point; fall back to the pinch centre.
    const a = this.lastTouchPoint || st.startMid;
    st.touchAnchor = zoom ? zoom.toStage(a.x, a.y) : null;
  }

  _onTouchMove(e) {
    const st = this.touch;
    if (!st || !this.o.isActive()) return;
    e.preventDefault();
    const touches = e.touches;
    if (touches.length >= 2) {
      const mid = midpoint(touches);
      if (st.startDist === undefined) this._startTwoFinger(touches);
      if (!st.gesture) st.gesture = classifyTwoFinger(st.startDist, distance(touches), st.startMid, mid);
      if (st.gesture === 'pinch') {
        this._pinch(touches, mid);
        st.scrollMid = mid;
        st.moved = true;
        return;
      }
      if (st.gesture === 'scroll' && st.scrollMid) {
        const anchor = this._anchorPoint(mid);
        const d = twoFingerScrollDelta(mid.x - st.scrollMid.x, mid.y - st.scrollMid.y);
        if (anchor) this._sendWheel(d.dx, d.dy, anchor);
      }
      st.scrollMid = mid;
      if (st.gesture) st.moved = true; // undecided jitter still counts as a two-finger tap
      return;
    }
    if (st.maxFingers > 1) return; // lifted one finger of a two-finger gesture
    const t = touches[0];
    const actions = this.gesture.move(t.clientX, t.clientY);
    if (actions.length) clearTimeout(st.timer);
    this._applyGesture(actions);
  }

  _onTouchEnd(e) {
    const st = this.touch;
    if (!st) return;
    e.preventDefault();
    if (e.touches.length > 0) {
      st.scrollMid = e.touches.length >= 2 ? midpoint(e.touches) : null;
      if (e.touches.length >= 2) this._startTwoFinger(e.touches);
      return;
    }
    clearTimeout(st.timer);
    this.touch = null;
    if (!this.o.isActive()) { this._applyGesture(this.gesture.cancel()); return; }
    if (st.maxFingers === 1) {
      const t = e.changedTouches[0];
      if (this.mode === 'touch' && t) this.lastTouchPoint = { x: t.clientX, y: t.clientY };
      this._applyGesture(this.gesture.up(performance.now()));
    } else if (st.maxFingers === 2 && !st.moved) {
      const p = this.mode === 'mouse' ? this._cursorPoint() : this._point(st.startX, st.startY);
      if (p && p.inside) this._click(MouseButton.Right, p);
    }
  }

  /** Maps recognizer actions: 'touch' acts at the finger, 'mouse' at the relative virtual cursor. */
  _applyGesture(actions) {
    const mouse = this.mode === 'mouse';
    for (const a of actions) {
      switch (a.kind) {
        case 'longPress':
          navigator.vibrate?.(20);
          this._flashRing(mouse ? null : a);
          break;
        case 'click': {
          const p = mouse ? this._cursorPoint() : this._point(a.x, a.y);
          if (p && p.inside) this._click(a.button === 'right' ? MouseButton.Right : MouseButton.Left, p);
          break;
        }
        case 'pan':
          if (mouse) this._moveCursorBy(a.dx, a.dy);
          else { this.o.zoom?.panBy(a.dx, a.dy); this._updateCursorOverlay(); }
          break;
        case 'dragStart': {
          const p = mouse ? this._cursorPoint() : this._point(a.x, a.y);
          if (!p || !p.inside) break;
          this._send(InputType.MouseMove, buildMouseMove(p.x, p.y));
          this._send(InputType.MouseButton, buildMouseButton(MouseButton.Left, true, p.x, p.y));
          this.dragging = true;
          break;
        }
        case 'dragMove':
          if (!this.dragging) break;
          if (mouse) this._moveCursorBy(a.dx, a.dy);
          else { const p = this._point(a.x, a.y); if (p) this._send(InputType.MouseMove, buildMouseMove(p.x, p.y)); }
          break;
        case 'dragEnd': {
          if (!this.dragging) break;
          this.dragging = false;
          const p = (mouse ? this._cursorPoint() : this._point(a.x, a.y)) || { x: 0, y: 0 };
          this._send(InputType.MouseButton, buildMouseButton(MouseButton.Left, false, p.x, p.y));
          break;
        }
      }
    }
  }

  _moveCursorBy(dx, dy) {
    const rect = this.o.getVideoRect();
    if (!rect) return;
    this.cursor.x = clamp01(this.cursor.x + dx / rect.width * MOUSE_MODE_SPEED);
    this.cursor.y = clamp01(this.cursor.y + dy / rect.height * MOUSE_MODE_SPEED);
    this._sendCursorMove();
  }

  /** Long-press feedback: a ring at the finger (touch mode) or at the virtual cursor (mouse mode). */
  _flashRing(at) {
    const parentEl = this.o.cursor.parentElement;
    if (!parentEl) return;
    if (!this.ring) {
      this.ring = document.createElement('div');
      this.ring.className = 'touch-ring';
      parentEl.appendChild(this.ring);
    }
    const parent = parentEl.getBoundingClientRect();
    let x, y;
    if (at) { x = at.x; y = at.y; } else {
      const rect = this.o.getVideoRect();
      if (!rect) return;
      x = rect.left + this.cursor.x * rect.width;
      y = rect.top + this.cursor.y * rect.height;
    }
    const r = this.ring;
    // Use `translate`, not `transform`: the keyframes animate `scale`, which CSS applies on top of
    // `transform` and would scale the offset too, sliding the ring diagonally.
    r.style.translate = `${x - parent.left}px ${y - parent.top}px`;
    r.classList.remove('is-on');
    void r.offsetWidth; // restart the animation
    r.classList.add('is-on');
  }

  /** Local zoom: mouse mode anchors on the virtual cursor, touch mode on the last touch point and pans with the fingers. */
  _pinch(touches, mid) {
    const st = this.touch;
    const zoom = this.o.zoom;
    if (!zoom || !st.startDist) return;
    const target = st.startZoom * distance(touches) / st.startDist;
    if (this.mode === 'mouse') {
      const g = zoom.geometry();
      if (g) {
        const v = zoom.view;
        zoom.zoomAt(target, v.tx + (g.base.left + this.cursor.x * g.base.width) * v.zoom,
          v.ty + (g.base.top + this.cursor.y * g.base.height) * v.zoom);
      }
    } else {
      zoom.zoomAt(target, st.touchAnchor.x, st.touchAnchor.y);
      zoom.panBy(mid.x - st.pinchMid.x, mid.y - st.pinchMid.y);
      st.touchAnchor = { x: st.touchAnchor.x + mid.x - st.pinchMid.x, y: st.touchAnchor.y + mid.y - st.pinchMid.y };
    }
    st.pinchMid = mid;
    this._updateCursorOverlay();
  }

  /** Call when the stream starts/stops: drops the local zoom. */
  resetZoom() {
    this.lastTouchPoint = null;
    this.o.zoom?.reset();
    this._updateCursorOverlay();
  }

  _cancelTouch() {
    if (this.touch) clearTimeout(this.touch.timer);
    this.touch = null;
    this._applyGesture(this.gesture.cancel());
  }

  _click(button, p) {
    this._send(InputType.MouseMove, buildMouseMove(p.x, p.y));
    this._send(InputType.MouseButton, buildMouseButton(button, true, p.x, p.y));
    this._send(InputType.MouseButton, buildMouseButton(button, false, p.x, p.y));
  }

  _anchorPoint(mid) {
    return this.mode === 'mouse' ? this._cursorPoint() : this._point(mid.x, mid.y);
  }

  _cursorPoint() {
    return { x: Math.round(this.cursor.x * 65535), y: Math.round(this.cursor.y * 65535), inside: true };
  }

  _sendCursorMove() {
    const p = this._cursorPoint();
    this._send(InputType.MouseMove, buildMouseMove(p.x, p.y));
    this.o.zoom?.ensureVisible(this.cursor.x, this.cursor.y, CURSOR_EDGE_MARGIN_PX);
    this._updateCursorOverlay();
  }

  /** Draws the mouse-mode cursor locally (the stream may not include the remote cursor). */
  _updateCursorOverlay() {
    const el = this.o.cursor;
    const rect = this.o.getVideoRect();
    if (this.mode !== 'mouse' || !rect || !this.o.isActive()) { el.hidden = true; return; }
    const parent = el.offsetParent?.getBoundingClientRect() || { left: 0, top: 0 };
    el.hidden = false;
    el.style.transform = `translate(${rect.left - parent.left + this.cursor.x * rect.width}px, ${rect.top - parent.top + this.cursor.y * rect.height}px)`;
  }

  /** Call after layout changes (resize, new stream) so the local cursor stays on the video. */
  refreshOverlay() {
    this.o.zoom?.refresh();
    this._updateCursorOverlay();
  }
}

function midpoint(touches) {
  return { x: (touches[0].clientX + touches[1].clientX) / 2, y: (touches[0].clientY + touches[1].clientY) / 2 };
}

function distance(touches) {
  return Math.hypot(touches[0].clientX - touches[1].clientX, touches[0].clientY - touches[1].clientY);
}

function clamp01(v) { return Math.max(0, Math.min(1, v)); }
