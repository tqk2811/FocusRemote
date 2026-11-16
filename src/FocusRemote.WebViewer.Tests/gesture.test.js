// One-finger gesture recognizer (js/gesture.js) and two-finger scroll direction (js/input.js).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { TouchGesture } from '../FocusRemote.Relay/wwwroot/js/gesture.js';
import { twoFingerScrollDelta } from '../FocusRemote.Relay/wwwroot/js/input.js';

const kinds = (a) => a.map((x) => x.kind);

test('tap = immediate left click at the touch point', () => {
  const g = new TouchGesture();
  g.down(100, 200, 0);
  g.move(104, 203); // within slop
  assert.deepEqual(g.up(80), [{ kind: 'click', button: 'left', x: 100, y: 200 }]);
});

test('double tap: second click reuses the first point despite jitter', () => {
  const g = new TouchGesture();
  g.down(100, 200, 0); g.up(80);
  g.down(112, 190, 250);
  assert.deepEqual(g.up(320), [{ kind: 'click', button: 'left', x: 100, y: 200, second: true }]);
  // a third tap starts a new pair at its own point
  g.down(115, 195, 450);
  assert.deepEqual(g.up(500), [{ kind: 'click', button: 'left', x: 115, y: 195 }]);
});

test('second tap too late or too far is a fresh single click', () => {
  const g = new TouchGesture();
  g.down(100, 200, 0); g.up(80);
  g.down(101, 200, 700);
  assert.equal(g.up(760)[0].x, 101);
  g.down(200, 200, 800);
  assert.equal(g.up(850)[0].x, 200);
});

test('swipe = pan deltas, no click', () => {
  const g = new TouchGesture();
  g.down(0, 0, 0);
  assert.deepEqual(g.move(5, 0), []);
  assert.deepEqual(g.move(15, 0), [{ kind: 'pan', x: 15, y: 0, dx: 15, dy: 0 }]);
  assert.deepEqual(g.move(20, 4), [{ kind: 'pan', x: 20, y: 4, dx: 5, dy: 4 }]);
  assert.deepEqual(g.longPress(), []); // too late once panning
  assert.deepEqual(g.up(900), []);
});

test('long press -> release = right click at the touch point', () => {
  const g = new TouchGesture();
  g.down(50, 60, 0);
  assert.deepEqual(g.longPress(), [{ kind: 'longPress', x: 50, y: 60 }]);
  g.move(53, 62);
  assert.deepEqual(g.up(900), [{ kind: 'click', button: 'right', x: 50, y: 60 }]);
});

test('long press -> swipe = left drag from the touch point', () => {
  const g = new TouchGesture();
  g.down(50, 60, 0);
  g.longPress();
  assert.deepEqual(kinds(g.move(70, 60)), ['dragStart', 'dragMove']);
  assert.deepEqual(g.move(80, 65), [{ kind: 'dragMove', x: 80, y: 65, dx: 10, dy: 5 }]);
  assert.deepEqual(g.up(1500), [{ kind: 'dragEnd', x: 80, y: 65 }]);
});

test('cancel ends a drag and forgets the pending double tap', () => {
  const g = new TouchGesture();
  g.down(0, 0, 0); g.longPress(); g.move(30, 0);
  assert.deepEqual(kinds(g.cancel()), ['dragEnd']);
  assert.deepEqual(g.cancel(), []);
  g.down(0, 0, 0); g.up(50); g.down(0, 0, 100); g.cancel();
  g.down(0, 0, 200);
  assert.equal(g.up(250)[0].second, undefined);
});

test('two-finger scroll is natural: content follows the fingers', () => {
  const up = twoFingerScrollDelta(0, -10, 1);   // fingers move up
  assert.ok(up.dy < 0, 'fingers up -> scroll down (dy < 0)');
  assert.ok(twoFingerScrollDelta(0, 10, 1).dy > 0, 'fingers down -> scroll up');
  assert.ok(twoFingerScrollDelta(-10, 0, 1).dx > 0, 'fingers left -> scroll right');
  assert.ok(twoFingerScrollDelta(10, 0, 1).dx < 0, 'fingers right -> scroll left');
  assert.deepEqual(twoFingerScrollDelta(-2, 3, 4), { dx: 8, dy: 12 }); // both axes at once
});
