// Terminal channel codecs (docs/Protocol-vi.md §5.5); hex expectations match the C# side.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  Channel, TerminalType, toHex, fromHex, utf8, encodeJson,
  buildTerminalData, parseTerminalData, buildTerminalResize, buildTerminalClose,
  parseShellList, parseTerminalEvent,
} from '../FocusRemote.Relay/wwwroot/js/protocol.js';

test('terminal constants', () => {
  assert.equal(Channel.Terminal, 4);
  assert.equal(TerminalType.ListShells, 0x01);
  assert.equal(TerminalType.ShellList, 0x02);
  assert.equal(TerminalType.Open, 0x10);
  assert.equal(TerminalType.Opened, 0x11);
  assert.equal(TerminalType.Closed, 0x12);
  assert.equal(TerminalType.Input, 0x20);
  assert.equal(TerminalType.Output, 0x21);
  assert.equal(TerminalType.Resize, 0x22);
  assert.equal(TerminalType.Close, 0x23);
});

test('buildTerminalData matches C# BuildData', () => {
  assert.equal(toHex(buildTerminalData(0x01020304, new Uint8Array([0x6c, 0x73, 0x0d]))), '040302016c730d');
});

test('parseTerminalData round trip and short payload', () => {
  const parsed = parseTerminalData(fromHex('040302016c730d'));
  assert.equal(parsed.terminalId, 0x01020304);
  assert.equal(toHex(parsed.data), '6c730d');
  assert.equal(parseTerminalData(new Uint8Array(3)), null);
  assert.equal(parseTerminalData(new Uint8Array(4)).data.length, 0);
});

test('parseTerminalData honours byteOffset of a subarray', () => {
  const buf = fromHex('ffff0700000041');
  const parsed = parseTerminalData(buf.subarray(2));
  assert.equal(parsed.terminalId, 7);
  assert.equal(toHex(parsed.data), '41');
});

test('buildTerminalResize matches C# BuildResize', () => {
  assert.equal(toHex(buildTerminalResize(7, 120, 30)), '0700000078001e00');
});

test('buildTerminalClose', () => {
  assert.equal(toHex(buildTerminalClose(7)), '07000000');
});

test('input data keeps UTF-8 bytes', () => {
  assert.equal(toHex(buildTerminalData(1, utf8('é'))), '01000000c3a9');
});

test('parseShellList', () => {
  const ok = parseShellList(encodeJson({ enabled: true, shells: [{ id: 'pwsh', name: 'PowerShell' }, { id: 'cmd' }, { name: 'bad' }] }));
  assert.deepEqual(ok, { enabled: true, shells: [{ id: 'pwsh', name: 'PowerShell' }, { id: 'cmd', name: 'cmd' }] });
  assert.deepEqual(parseShellList(encodeJson({ enabled: false, shells: [] })), { enabled: false, shells: [] });
  assert.deepEqual(parseShellList(encodeJson({ enabled: true })), { enabled: true, shells: [] });
  assert.equal(parseShellList(encodeJson({ shells: [] })), null);
  assert.equal(parseShellList(utf8('not json')), null);
});

test('parseTerminalEvent', () => {
  const v = parseTerminalEvent(encodeJson({ terminalId: 3, reason: 'exited', exitCode: 0, message: '' }));
  assert.equal(v.terminalId, 3);
  assert.equal(v.exitCode, 0);
  assert.equal(parseTerminalEvent(encodeJson({ reason: 'x' })), null);
});

test('sanitizeHostText strips ESC and C0/C1 control characters', async () => {
  const { sanitizeHostText } = await import('../FocusRemote.Relay/wwwroot/js/terminal.js');
  assert.equal(sanitizeHostText('a\x1b[31mred\x07\r\nb\x7f\u009bc'), 'a[31mredbc');
  assert.equal(sanitizeHostText('Xin chào ✓'), 'Xin chào ✓');
  assert.equal(sanitizeHostText(undefined), '');
});

test('closedText sanitizes host message and prefers exit code', async () => {
  const { closedText } = await import('../FocusRemote.Relay/wwwroot/js/terminal.js');
  assert.equal(closedText({ exitCode: 3, message: 'x' }), '[Tiến trình đã kết thúc (mã 3)]');
  assert.equal(closedText({ message: 'bad\x1b]0;t\x07' }), '[Terminal đã đóng: bad]0;t]');
  assert.equal(closedText({}), '[Tiến trình đã kết thúc]');
});

test('applyCtrl maps the first character like a Ctrl chord', async () => {
  const { applyCtrl } = await import('../FocusRemote.Relay/wwwroot/js/terminal.js');
  assert.equal(applyCtrl('c'), '\x03');
  assert.equal(applyCtrl('C'), '\x03');
  assert.equal(applyCtrl('['), '\x1b');
  assert.equal(applyCtrl(' '), '\x00');
  assert.equal(applyCtrl('?'), '\x7f');
  assert.equal(applyCtrl('dx'), '\x04x');
  assert.equal(applyCtrl('1'), '1');
  assert.equal(applyCtrl(''), '');
});
