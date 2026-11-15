// Payload codecs and pure helpers of the WebViewer (docs/Protocol-vi.md §5).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  toHex, fromHex, isValidId, isValidPassword, buildMouseButton, buildMouseWheel, buildKey, buildText,
  parseAudioConfig, parseAudioPacket, parseVideoFrame, decodeJson, utf8, parsePlaintext, MouseButton,
} from '../FocusRemote.Relay/wwwroot/js/protocol.js';
import { avcCodecFromAnnexB } from '../FocusRemote.Relay/wwwroot/js/video.js';
import { normalizePoint, wheelToUnits } from '../FocusRemote.Relay/wwwroot/js/input.js';
import { buildViewerUrl } from '../FocusRemote.Relay/wwwroot/js/connection.js';

test('id and password validation', () => {
  assert.ok(isValidId('demo-host_01'));
  assert.ok(!isValidId('ab'));
  assert.ok(!isValidId('a'.repeat(65)));
  assert.ok(!isValidId('abc\n'));
  assert.ok(!isValidId('abc def'));
  assert.ok(isValidPassword('0123456789'));
  assert.ok(!isValidPassword('012345678'));
});

test('input payloads are little-endian and clamped', () => {
  assert.equal(toHex(buildMouseButton(MouseButton.Right, true, 1, 0x1234)), '010101003412');
  assert.equal(toHex(buildMouseWheel(-120, 120, 70000, -5)), '88ff7800ffff0000');
  assert.equal(toHex(buildKey(true, 'KeyA')), '01044b657941');
  assert.throws(() => buildKey(true, ''));
  assert.equal(toHex(buildText('é')), 'c3a9');
});

test('video/audio parsing ignores trailing data and rejects short payloads', () => {
  assert.equal(parseVideoFrame(new Uint8Array(8)), null);
  const f = parseVideoFrame(fromHex('fe0100000000000000aabb'));
  assert.equal(f.isKeyframe, false, 'unknown flag bits ignored');
  assert.equal(f.timestampUs, 1);
  assert.equal(parseAudioPacket(new Uint8Array(7)), null);
  assert.equal(toHex(parseAudioPacket(fromHex('0200000000000000ff')).data), 'ff');
  assert.deepEqual(parseAudioConfig(utf8('{"codec":"opus","sampleRate":48000,"channels":2,"x":1}')).channels, 2);
  assert.equal(parseAudioConfig(utf8('{"codec":"opus"}')), null);
  assert.equal(decodeJson(utf8('{bad')), null);
  assert.equal(parsePlaintext(new Uint8Array([0])), null);
});

test('avc codec string from SPS', () => {
  assert.equal(avcCodecFromAnnexB(fromHex('000000016764001fac')), 'avc1.64001F');
  assert.equal(avcCodecFromAnnexB(fromHex('0000000109f000000167428028')), 'avc1.428028');
  assert.equal(avcCodecFromAnnexB(fromHex('0000000165888')), null);
});

test('normalizePoint maps the displayed video rect to u16', () => {
  const rect = { left: 10, top: 20, width: 200, height: 100 };
  assert.deepEqual(normalizePoint(10, 20, rect), { x: 0, y: 0, inside: true });
  assert.deepEqual(normalizePoint(210, 120, rect), { x: 65535, y: 65535, inside: true });
  assert.equal(normalizePoint(0, 70, rect).x, 0);
  assert.equal(normalizePoint(0, 70, rect).inside, false);
});

test('wheel units: one Chrome notch (100px down) = -120', () => {
  assert.equal(wheelToUnits(100, 0), -120);
  assert.equal(wheelToUnits(-3, 1), 120);
});

test('viewer url keeps relay sub-path and maps http to ws', () => {
  assert.equal(buildViewerUrl('wss://relay.example/', 'abc'), 'wss://relay.example/ws/viewer/abc');
  assert.equal(buildViewerUrl('https://relay.example/remote', 'abc'), 'wss://relay.example/remote/ws/viewer/abc');
  assert.equal(buildViewerUrl('http://192.168.1.2:8443/', 'a_b-c'), 'ws://192.168.1.2:8443/ws/viewer/a_b-c');
});
