// Input mode helpers (Control SetInputMode).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { InputMode, normalizeInputMode, isWindowSource } from '../FocusRemote.Relay/wwwroot/js/inputmode.js';
import { ControlType } from '../FocusRemote.Relay/wwwroot/js/protocol.js';
import { printableKeyText } from '../FocusRemote.Relay/wwwroot/js/input.js';

test('SetInputMode control type matches the C# enum', () => {
  assert.equal(ControlType.SetInputMode, 0x17);
});

test('normalizeInputMode defaults to sendinput', () => {
  assert.equal(normalizeInputMode('message'), InputMode.Message);
  assert.equal(normalizeInputMode('sendinput'), InputMode.SendInput);
  assert.equal(normalizeInputMode(null), InputMode.SendInput);
  assert.equal(normalizeInputMode('MESSAGE?'), InputMode.SendInput);
});

test('isWindowSource follows the active source kind', () => {
  const sources = [{ id: 'm:0', kind: 'monitor' }, { id: 'w:42', kind: 'window' }];
  assert.equal(isWindowSource(sources, 'w:42'), true);
  assert.equal(isWindowSource(sources, 'm:0'), false);
  assert.equal(isWindowSource(sources, null), false);
  assert.equal(isWindowSource([], 'w:7'), true); // list not refreshed yet: fall back to the id prefix
  assert.equal(isWindowSource([], 'm:1'), false);
});

test('printableKeyText sends typed characters, not chords or control keys', () => {
  const k = (key, mods = {}) => ({ key, ctrlKey: false, altKey: false, metaKey: false, ...mods });
  assert.equal(printableKeyText(k('a')), 'a');
  assert.equal(printableKeyText(k('A', { shiftKey: true })), 'A');
  assert.equal(printableKeyText(k('!')), '!');
  assert.equal(printableKeyText(k(' ')), ' ');
  assert.equal(printableKeyText(k('\u{1F600}')), '\u{1F600}'); // one code point, two UTF-16 units
  assert.equal(printableKeyText(k('Enter')), null);
  assert.equal(printableKeyText(k('ArrowLeft')), null);
  assert.equal(printableKeyText(k('c', { ctrlKey: true })), null);
  assert.equal(printableKeyText(k('f', { altKey: true })), null);
  assert.equal(printableKeyText(k('v', { metaKey: true })), null);
  const altGr = { ...k('@', { ctrlKey: true, altKey: true }), getModifierState: (m) => m === 'AltGraph' };
  assert.equal(printableKeyText(altGr), '@');
  assert.equal(printableKeyText(k('Dead')), null);
});
