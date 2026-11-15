// Mouse, keyboard and touch input (docs/Protocol-vi.md §5.3).
// Coordinates are normalized to u16 against the rectangle where the video is displayed.

import {
  InputType, MouseButton,
  buildMouseMove, buildMouseButton, buildMouseWheel, buildKey, buildText,
} from './protocol.js';

const LONG_PRESS_MS = 550;
const TAP_SLOP_PX = 10;
const TOUCH_SCROLL_FACTOR = 4;      // wheel units per CSS pixel of two-finger movement
const TOUCHPAD_SPEED = 1.6;         // cursor speed multiplier in touchpad mode
const SOFT_SENTINEL = ' ';     // kept in the hidden input so Backspace always fires an input event

// DOM MouseEvent.button -> protocol button
const DOM_BUTTON = { 0: MouseButton.Left, 1: MouseButton.Middle, 2: MouseButton.Right, 3: MouseButton.X1, 4: MouseButton.X2 };

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

export class InputController {
  /**
   * @param {{ surface: HTMLElement, cursor: HTMLElement, softInput: HTMLTextAreaElement,
   *           getVideoRect: () => DOMRect|null, send: (type: number, payload: Uint8Array) => void,
   *           isActive: () => boolean }} options
   */
  constructor(options) {
    this.o = options;
    this.mode = 'direct'; // touch mode: 'direct' | 'touchpad'
    this.pressedKeys = new Set();
    this.pressedButtons = new Set();
    this.pendingMove = null;
    this.moveRaf = 0;
    this.wheelAcc = { x: 0, y: 0 };
    this.cursor = { x: 0.5, y: 0.5 }; // touchpad cursor, normalized 0..1
    this.touch = null;
    this._bind();
  }

  setTouchMode(mode) {
    this.mode = mode;
    this._updateCursorOverlay();
  }

  releaseAll() {
    if (this.pressedKeys.size || this.pressedButtons.size || this.touch) {
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

  _onTouchStart(e) {
    if (!this.o.isActive()) return;
    e.preventDefault();
    const touches = e.touches;
    if (!this.touch) {
      const t = touches[0];
      this.touch = {
        fingers: 1, maxFingers: 1, startX: t.clientX, startY: t.clientY, lastX: t.clientX, lastY: t.clientY,
        moved: false, dragging: false, longPressed: false, startTime: performance.now(), scrollMid: null,
      };
      this.touch.timer = setTimeout(() => this._onLongPress(), LONG_PRESS_MS);
    }
    if (touches.length >= 2) {
      clearTimeout(this.touch.timer);
      if (this.touch.dragging) this._endDrag();
      this.touch.fingers = touches.length;
      this.touch.maxFingers = Math.max(this.touch.maxFingers, touches.length);
      this.touch.scrollMid = midpoint(touches);
    }
  }

  _onTouchMove(e) {
    const st = this.touch;
    if (!st || !this.o.isActive()) return;
    e.preventDefault();
    const touches = e.touches;
    if (touches.length >= 2) {
      const mid = midpoint(touches);
      if (st.scrollMid) {
        const anchor = this._anchorPoint(mid);
        if (anchor) this._sendWheel(-(mid.x - st.scrollMid.x) * TOUCH_SCROLL_FACTOR, (mid.y - st.scrollMid.y) * TOUCH_SCROLL_FACTOR, anchor);
      }
      st.scrollMid = mid;
      st.moved = true;
      return;
    }
    if (st.maxFingers > 1) return; // lifted one finger of a two-finger gesture
    const t = touches[0];
    if (!st.moved && Math.hypot(t.clientX - st.startX, t.clientY - st.startY) > TAP_SLOP_PX) {
      st.moved = true;
      clearTimeout(st.timer);
      if (this.mode === 'direct' && !st.longPressed) {
        // press-and-move in direct mode drags with the left button
        const p = this._point(st.startX, st.startY);
        if (p) {
          this._send(InputType.MouseMove, buildMouseMove(p.x, p.y));
          this._send(InputType.MouseButton, buildMouseButton(MouseButton.Left, true, p.x, p.y));
          st.dragging = true;
        }
      }
    }
    if (st.moved) {
      if (this.mode === 'direct') {
        const p = this._point(t.clientX, t.clientY);
        if (p) this._send(InputType.MouseMove, buildMouseMove(p.x, p.y));
      } else {
        const rect = this.o.getVideoRect();
        if (rect) {
          this.cursor.x = clamp01(this.cursor.x + (t.clientX - st.lastX) / rect.width * TOUCHPAD_SPEED);
          this.cursor.y = clamp01(this.cursor.y + (t.clientY - st.lastY) / rect.height * TOUCHPAD_SPEED);
          this._sendCursorMove();
        }
      }
    }
    st.lastX = t.clientX;
    st.lastY = t.clientY;
  }

  _onTouchEnd(e) {
    const st = this.touch;
    if (!st) return;
    e.preventDefault();
    if (e.touches.length > 0) { st.fingers = e.touches.length; st.scrollMid = e.touches.length >= 2 ? midpoint(e.touches) : null; return; }
    clearTimeout(st.timer);
    this.touch = null;
    if (!this.o.isActive()) return;
    if (st.dragging) { this._endDrag(); return; }
    if (st.longPressed || st.moved) return;
    const twoFingerTap = st.maxFingers === 2;
    if (this.mode === 'direct') {
      const p = this._point(st.startX, st.startY);
      if (p && p.inside) this._click(twoFingerTap ? MouseButton.Right : MouseButton.Left, p);
    } else {
      this._click(twoFingerTap ? MouseButton.Right : MouseButton.Left, this._cursorPoint());
    }
  }

  _onLongPress() {
    const st = this.touch;
    if (!st || st.moved || st.maxFingers > 1) return;
    st.longPressed = true;
    navigator.vibrate?.(20);
    if (this.mode === 'direct') {
      const p = this._point(st.startX, st.startY);
      if (p && p.inside) this._click(MouseButton.Right, p);
    } else {
      // touchpad: long-press then move = drag with the left button
      const p = this._cursorPoint();
      this._send(InputType.MouseButton, buildMouseButton(MouseButton.Left, true, p.x, p.y));
      st.dragging = true;
      st.moved = true;
    }
  }

  _endDrag() {
    const p = this.mode === 'direct' && this.touch ? this._point(this.touch.lastX, this.touch.lastY) : this._cursorPoint();
    this._send(InputType.MouseButton, buildMouseButton(MouseButton.Left, false, p ? p.x : 0, p ? p.y : 0));
    if (this.touch) this.touch.dragging = false;
  }

  _cancelTouch() {
    if (!this.touch) return;
    clearTimeout(this.touch.timer);
    if (this.touch.dragging) this._endDrag();
    this.touch = null;
  }

  _click(button, p) {
    this._send(InputType.MouseMove, buildMouseMove(p.x, p.y));
    this._send(InputType.MouseButton, buildMouseButton(button, true, p.x, p.y));
    this._send(InputType.MouseButton, buildMouseButton(button, false, p.x, p.y));
  }

  _anchorPoint(mid) {
    return this.mode === 'touchpad' ? this._cursorPoint() : this._point(mid.x, mid.y);
  }

  _cursorPoint() {
    return { x: Math.round(this.cursor.x * 65535), y: Math.round(this.cursor.y * 65535), inside: true };
  }

  _sendCursorMove() {
    const p = this._cursorPoint();
    this._send(InputType.MouseMove, buildMouseMove(p.x, p.y));
    this._updateCursorOverlay();
  }

  /** Draws the touchpad cursor locally (the stream may not include the remote cursor). */
  _updateCursorOverlay() {
    const el = this.o.cursor;
    const rect = this.o.getVideoRect();
    if (this.mode !== 'touchpad' || !rect || !this.o.isActive()) { el.hidden = true; return; }
    const parent = el.offsetParent?.getBoundingClientRect() || { left: 0, top: 0 };
    el.hidden = false;
    el.style.transform = `translate(${rect.left - parent.left + this.cursor.x * rect.width}px, ${rect.top - parent.top + this.cursor.y * rect.height}px)`;
  }

  /** Call after layout changes (resize, new stream) so the local cursor stays on the video. */
  refreshOverlay() { this._updateCursorOverlay(); }
}

function midpoint(touches) {
  return { x: (touches[0].clientX + touches[1].clientX) / 2, y: (touches[0].clientY + touches[1].clientY) / 2 };
}

function clamp01(v) { return Math.max(0, Math.min(1, v)); }
