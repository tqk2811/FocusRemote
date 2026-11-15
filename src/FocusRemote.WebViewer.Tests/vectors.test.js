// Verifies the WebViewer crypto/codec modules against the shared TestVectors.json.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { deriveSalt, deriveRootKey, deriveSessionKeys, buildNonce, SecureSession } from '../FocusRemote.Relay/wwwroot/js/crypto.js';
import {
  FrameKind, Channel, ControlType, PBKDF2_ITERATIONS, SESSION_INFO, PROBE_TEXT,
  toHex, fromHex, utf8, buildHello, parseHello, buildPlaintext, parsePing, buildPing,
  parseVideoFrame, buildVideoFrame, buildMouseMove, decodeJson,
} from '../FocusRemote.Relay/wwwroot/js/protocol.js';

const here = dirname(fileURLToPath(import.meta.url));
const v = JSON.parse(readFileSync(join(here, '..', 'FocusRemote.Protocol.Tests', 'TestVectors.json'), 'utf8'));
const keys = { keyH2V: fromHex(v.keyH2VHex), keyV2H: fromHex(v.keyV2HHex) };

test('constants match vectors', () => {
  assert.equal(v.pbkdf2Iterations, PBKDF2_ITERATIONS);
  assert.equal(v.sessionInfo, SESSION_INFO);
});

test('salt', async () => {
  assert.equal(toHex(await deriveSalt(v.id)), v.saltHex);
});

test('rootKey (PBKDF2 600000)', async () => {
  const rootKey = await deriveRootKey(v.password, fromHex(v.saltHex), v.pbkdf2Iterations);
  assert.equal(toHex(rootKey), v.rootKeyHex);
});

test('hello frames', () => {
  assert.equal(toHex(buildHello(FrameKind.HelloViewer, fromHex(v.nonceVHex))), v.helloViewerHex);
  assert.equal(toHex(buildHello(FrameKind.HelloHost, fromHex(v.nonceHHex))), v.helloHostHex);
  assert.equal(toHex(parseHello(fromHex(v.helloHostHex), FrameKind.HelloHost)), v.nonceHHex);
  assert.equal(parseHello(fromHex(v.helloHostHex), FrameKind.HelloViewer), null);
  assert.equal(parseHello(fromHex(v.helloHostHex).slice(0, 20), FrameKind.HelloHost), null);
});

test('session keys (HKDF)', async () => {
  const k = await deriveSessionKeys(fromHex(v.rootKeyHex), fromHex(v.nonceVHex), fromHex(v.nonceHHex));
  assert.equal(toHex(k.keyH2V), v.keyH2VHex);
  assert.equal(toHex(k.keyV2H), v.keyV2HHex);
});

for (const [listName, role, peer] of [['sealedV2H', 'viewer', 'host'], ['sealedH2V', 'host', 'viewer']]) {
  test(`${listName}: seal as ${role}, open as ${peer}`, async () => {
    const sender = await SecureSession.create(keys, role);
    const receiver = await SecureSession.create(keys, peer);
    for (const item of v[listName]) {
      assert.equal(toHex(buildNonce(item.counter)), item.nonceHex);
      const payload = fromHex(item.payloadHex);
      assert.equal(toHex(buildPlaintext(item.channel, item.type, payload)), item.plaintextHex);
      assert.equal(sender.nextSendCounter, BigInt(item.counter));
      const frame = await sender.seal(item.channel, item.type, payload);
      assert.equal(toHex(frame), item.frameHex, item.note);
      const opened = await receiver.open(fromHex(item.frameHex));
      assert.ok(opened, item.note);
      assert.equal(opened.channel, item.channel);
      assert.equal(opened.type, item.type);
      assert.equal(toHex(opened.payload), item.payloadHex);
    }
  });
}

test('payload contents of vectors', () => {
  const [probe, caps, move] = v.sealedV2H;
  assert.equal(probe.payloadHex, toHex(utf8(PROBE_TEXT)));
  assert.equal(probe.type, ControlType.AuthProbe);
  assert.deepEqual(decodeJson(fromHex(caps.payloadHex)), { videoCodecs: ['h264'], audioCodecs: ['opus'] });
  assert.equal(move.channel, Channel.Input);
  assert.equal(toHex(buildMouseMove(32767, 32768)), move.payloadHex);
  const [, ping, video] = v.sealedH2V;
  assert.equal(parsePing(fromHex(ping.payloadHex)), 1700000000123);
  assert.equal(toHex(buildPing(1700000000123)), ping.payloadHex);
  const f = parseVideoFrame(fromHex(video.payloadHex));
  assert.equal(f.isKeyframe, true);
  assert.equal(f.timestampUs, 33333);
  assert.equal(toHex(f.data), '000000016742');
  assert.equal(toHex(buildVideoFrame(true, 33333, f.data)), video.payloadHex);
});

test('sends stay ordered when seal is called back-to-back', async () => {
  const sender = await SecureSession.create(keys, 'viewer');
  const receiver = await SecureSession.create(keys, 'host');
  const frames = await Promise.all(Array.from({ length: 20 }, (_, i) => sender.seal(2, 1, new Uint8Array(i * 100))));
  for (const f of frames) assert.ok(await receiver.open(f));
});

test('replay, reorder and tampering are rejected', async () => {
  const receiver = await SecureSession.create(keys, 'viewer');
  const frames = v.sealedH2V.map(i => fromHex(i.frameHex));
  assert.ok(await receiver.open(frames[1]));
  assert.equal(await receiver.open(frames[1]), null, 'replay');
  assert.equal(await receiver.open(frames[0]), null, 'older counter');
  const tampered = frames[2].slice();
  tampered[tampered.length - 1] ^= 1;
  assert.equal(await receiver.open(tampered), null, 'tampered');
  assert.ok(await receiver.open(frames[2]), 'tampered frame did not advance the window');
  assert.equal(await receiver.open(new Uint8Array([0x10, 1, 2])), null, 'too short');
});
