// Pure one-finger gesture recognizer (no DOM, no timers): fed with synthetic events, returns actions.
// The caller owns the long-press timer and maps actions to the active touch mode (js/input.js).
//
//   tap                    -> { kind: 'click', button: 'left', x, y }  (sent immediately, no wait)
//   second tap close/soon  -> same, but at the FIRST tap's point, so Windows sees a double click
//                             even if the finger jittered
//   long press             -> { kind: 'longPress', x, y }            (feedback only)
//   long press -> release  -> { kind: 'click', button: 'right', x, y }
//   long press -> swipe    -> dragStart {x,y} / dragMove {x,y,dx,dy} / dragEnd {x,y}
//   swipe                  -> pan {x,y,dx,dy} per move (beyond the tap slop)

export const LONG_PRESS_MS = 550;
export const TAP_SLOP_PX = 10;
export const DOUBLE_TAP_MS = 400;
export const DOUBLE_TAP_SLOP_PX = 30;

export class TouchGesture {
  constructor(opts = {}) {
    this.slop = opts.slop ?? TAP_SLOP_PX;
    this.doubleTapMs = opts.doubleTapMs ?? DOUBLE_TAP_MS;
    this.doubleTapSlop = opts.doubleTapSlop ?? DOUBLE_TAP_SLOP_PX;
    this.s = null;        // current gesture
    this.lastTap = null;  // { x, y, t } of the previous single tap, for double tap
  }

  get active() { return this.s !== null; }

  down(x, y, t) {
    this.s = { startX: x, startY: y, lastX: x, lastY: y, phase: 'pending' }; // pending | pan | long | drag
    return [];
  }

  /** Call when LONG_PRESS_MS elapsed since down (caller's timer). */
  longPress() {
    const s = this.s;
    if (!s || s.phase !== 'pending') return [];
    s.phase = 'long';
    return [{ kind: 'longPress', x: s.startX, y: s.startY }];
  }

  move(x, y) {
    const s = this.s;
    if (!s) return [];
    const out = [];
    const beyond = Math.hypot(x - s.startX, y - s.startY) > this.slop;
    if (s.phase === 'pending' && beyond) s.phase = 'pan';
    else if (s.phase === 'long' && beyond) {
      s.phase = 'drag';
      out.push({ kind: 'dragStart', x: s.startX, y: s.startY });
    }
    const dx = x - s.lastX, dy = y - s.lastY;
    if (s.phase === 'pan') out.push({ kind: 'pan', x, y, dx, dy });
    else if (s.phase === 'drag') out.push({ kind: 'dragMove', x, y, dx, dy });
    if (s.phase === 'pan' || s.phase === 'drag') { s.lastX = x; s.lastY = y; }
    return out;
  }

  up(t) {
    const s = this.s;
    if (!s) return [];
    this.s = null;
    if (s.phase === 'drag') return [{ kind: 'dragEnd', x: s.lastX, y: s.lastY }];
    if (s.phase === 'long') { this.lastTap = null; return [{ kind: 'click', button: 'right', x: s.startX, y: s.startY }]; }
    if (s.phase !== 'pending') return [];
    const lt = this.lastTap;
    if (lt && t - lt.t <= this.doubleTapMs && Math.hypot(s.startX - lt.x, s.startY - lt.y) <= this.doubleTapSlop) {
      this.lastTap = null; // a third tap starts a new pair
      return [{ kind: 'click', button: 'left', x: lt.x, y: lt.y, second: true }];
    }
    this.lastTap = { x: s.startX, y: s.startY, t };
    return [{ kind: 'click', button: 'left', x: s.startX, y: s.startY }];
  }

  /** A second finger arrived or the touch was cancelled: abandons the gesture (ends a drag). */
  cancel() {
    const s = this.s;
    this.s = null;
    this.lastTap = null;
    return s && s.phase === 'drag' ? [{ kind: 'dragEnd', x: s.lastX, y: s.lastY }] : [];
  }
}
