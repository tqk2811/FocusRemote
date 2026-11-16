// Local zoom/pan math and two-finger gesture classification (pure helpers, no DOM).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  MAX_ZOOM, containRect, clampView, zoomAt, panBy, videoToStage, ensureVisible,
} from '../FocusRemote.Relay/wwwroot/js/viewzoom.js';
import { normalizePoint, classifyTwoFinger } from '../FocusRemote.Relay/wwwroot/js/input.js';

const SW = 800, SH = 600;
const base = containRect(SW, SH, 1600, 900); // 800x450 letterboxed at top 75

const close = (a, b) => assert.ok(Math.abs(a - b) < 1e-6, `${a} != ${b}`);

/** Client rect of the zoomed video, as canvas.getBoundingClientRect + getVideoRect would give (stage at 0,0). */
function zoomedRect(v) {
  return { left: v.tx + base.left * v.zoom, top: v.ty + base.top * v.zoom, width: base.width * v.zoom, height: base.height * v.zoom };
}

test('containRect letterboxes', () => {
  assert.deepEqual(base, { left: 0, top: 75, width: 800, height: 450 });
  assert.equal(containRect(0, 10, 1, 1), null);
});

test('zoomAt keeps the anchor fixed on screen', () => {
  const v0 = { zoom: 1, tx: 0, ty: 0 };
  const ax = 300, ay = 250;
  const before = normalizePoint(ax, ay, zoomedRect(v0));
  const v1 = zoomAt(v0, 2, ax, ay, base, SW, SH);
  assert.equal(v1.zoom, 2);
  const after = normalizePoint(ax, ay, zoomedRect(v1));
  assert.equal(after.x, before.x);
  assert.equal(after.y, before.y);
});

test('zoom clamps to range and resets translation at 1x', () => {
  const v = zoomAt({ zoom: 1, tx: 0, ty: 0 }, 50, 400, 300, base, SW, SH);
  assert.equal(v.zoom, MAX_ZOOM);
  assert.deepEqual(zoomAt(v, 0.2, 10, 10, base, SW, SH), { zoom: 1, tx: 0, ty: 0 });
});

test('pan never shows empty space at the edges', () => {
  let v = zoomAt({ zoom: 1, tx: 0, ty: 0 }, 2, 400, 300, base, SW, SH);
  v = panBy(v, 10000, 10000, base, SW, SH);
  const r = zoomedRect(v);
  close(r.left, 0);            // left edge pinned to the stage
  close(r.top, 0);             // 900px tall > 600: top edge pinned
  v = panBy(v, -10000, -10000, base, SW, SH);
  const r2 = zoomedRect(v);
  close(r2.left + r2.width, SW);
  close(r2.top + r2.height, SH);
});

test('axis smaller than the stage stays centred', () => {
  const v = clampView({ zoom: 1.2, tx: 500, ty: 500 }, base, SW, SH); // height 540 < 600
  const r = zoomedRect(v);
  close(r.top + r.height / 2, SH / 2);
});

test('normalizePoint maps through zoom to the right remote pixel', () => {
  const v = zoomAt({ zoom: 1, tx: 0, ty: 0 }, 4, 0, 75, base, SW, SH); // zoom on the video's top-left corner
  const p = normalizePoint(400, 300, zoomedRect(v));
  // 4x shows a quarter of the width: the stage centre is 1/8 across and some way down.
  assert.ok(Math.abs(p.x / 65535 - 0.125) < 1e-4);
  assert.ok(p.inside);
});

test('videoToStage and ensureVisible keep the cursor on screen', () => {
  const v = zoomAt({ zoom: 1, tx: 0, ty: 0 }, 3, 0, 75, base, SW, SH);
  const far = videoToStage(v, base, 0.9, 0.9);
  assert.ok(far.x > SW);
  const v2 = ensureVisible(v, far.x, far.y, base, SW, SH, 40);
  const p = videoToStage(v2, base, 0.9, 0.9);
  close(p.x, SW - 40);
  close(p.y, SH - 40);
  const inside = videoToStage(v2, base, 0.85, 0.85);
  assert.equal(ensureVisible(v2, inside.x, inside.y, base, SW, SH, 40), v2); // no pan needed
});

test('classifyTwoFinger separates pinch from scroll', () => {
  const m = { x: 100, y: 100 };
  assert.equal(classifyTwoFinger(200, 205, m, { x: 103, y: 102 }), null);
  assert.equal(classifyTwoFinger(200, 230, m, m), 'pinch');
  assert.equal(classifyTwoFinger(200, 170, m, m), 'pinch');
  assert.equal(classifyTwoFinger(200, 204, m, { x: 100, y: 140 }), 'scroll');
});
