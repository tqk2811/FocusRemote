// Real-browser end-to-end test: Relay (or LAN host) + headless host harness + Google Chrome over CDP.
//
//   node src/FocusRemote.E2E/browser/run-e2e.mjs --mode relay|lan --out <dir> [--source m:0] [--no-audio]
//
// Needs `dotnet build src/FocusRemote.sln` first. Uses Node's built-in WebSocket as the CDP client (no npm packages).
// Chrome runs with its own --user-data-dir under --out; only the processes this script started are stopped.
// Input is verified through the harness log ("INPUT ..." lines); the harness never injects into the real desktop.

import { spawn } from 'node:child_process';
import { mkdirSync, writeFileSync, readFileSync, existsSync, createWriteStream } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';
import { setTimeout as delay } from 'node:timers/promises';
import { createServer } from 'node:net';

const here = dirname(fileURLToPath(import.meta.url));
const srcDir = resolve(here, '..', '..');
const { values: opt } = parseArgs({
  options: {
    mode: { type: 'string', default: 'relay' },
    out: { type: 'string' },
    source: { type: 'string', default: 'm:0' },
    'no-audio': { type: 'boolean', default: false },
    chrome: { type: 'string', default: 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe' },
    configuration: { type: 'string', default: 'Debug' },
    headful: { type: 'boolean', default: false },
  },
});
if (!opt.out || !['relay', 'lan'].includes(opt.mode)) {
  console.error('Usage: node run-e2e.mjs --mode relay|lan --out <dir> [--source m:0] [--no-audio] [--headful]');
  process.exit(2);
}
const outDir = resolve(opt.out);
mkdirSync(outDir, { recursive: true });
const wantAudio = !opt['no-audio'];

const ID = 'e2e_' + Math.random().toString(36).slice(2, 10);
const PASSWORD = 'e2e-password-' + Math.random().toString(36).slice(2, 8);
const children = [];
const results = { mode: opt.mode, checks: {} };

function log(...a) { console.log(`[e2e ${new Date().toISOString().slice(11, 23)}]`, ...a); }

function freePort() {
  return new Promise((ok, fail) => {
    const s = createServer();
    s.once('error', fail);
    s.listen(0, '127.0.0.1', () => { const p = s.address().port; s.close(() => ok(p)); });
  });
}

/** Spawns a child, tees its output to <outDir>/<name>.log and keeps the lines for waitForLine. */
function startProcess(name, file, args, cwd) {
  const logFile = createWriteStream(join(outDir, `${name}.log`));
  const proc = spawn(file, args, { cwd, stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
  const entry = { name, proc, lines: [], exited: false };
  let partial = '';
  const onData = (buf) => {
    logFile.write(buf);
    partial += buf.toString('utf8');
    const parts = partial.split(/\r?\n/);
    partial = parts.pop();
    entry.lines.push(...parts);
  };
  proc.stdout.on('data', onData);
  proc.stderr.on('data', onData);
  proc.on('exit', (code) => { entry.exited = true; logFile.end(`\n[exit ${code}]\n`); });
  children.push(entry);
  return entry;
}

async function waitFor(what, fn, timeoutMs = 30000, intervalMs = 200) {
  const end = Date.now() + timeoutMs;
  while (Date.now() < end) {
    const v = await fn();
    if (v) return v;
    await delay(intervalMs);
  }
  throw new Error(`Timeout waiting for ${what}`);
}

const waitForLine = (entry, re, timeoutMs, from = 0) =>
  waitFor(`${entry.name}: ${re}`, () => {
    if (entry.exited) throw new Error(`${entry.name} exited`);
    return entry.lines.slice(from).find((l) => re.test(l));
  }, timeoutMs);

// ---------- minimal CDP client ----------

class Cdp {
  constructor(url) {
    this.ws = new WebSocket(url);
    this.nextId = 1;
    this.pending = new Map();
    this.listeners = [];
    this.ws.onmessage = (e) => {
      const msg = JSON.parse(e.data);
      if (msg.id && this.pending.has(msg.id)) {
        const { ok, fail } = this.pending.get(msg.id);
        this.pending.delete(msg.id);
        if (msg.error) fail(new Error(`${msg.error.message} ${msg.error.data ?? ''}`));
        else ok(msg.result);
      } else if (msg.method) {
        for (const l of this.listeners) l(msg);
      }
    };
  }

  open() { return new Promise((ok, fail) => { this.ws.onopen = ok; this.ws.onerror = fail; }); }

  send(method, params = {}) {
    const id = this.nextId++;
    this.ws.send(JSON.stringify({ id, method, params }));
    return new Promise((ok, fail) => this.pending.set(id, { ok, fail }));
  }

  async eval(expression) {
    const r = await this.send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
    if (r.exceptionDetails) throw new Error('Page exception: ' + JSON.stringify(r.exceptionDetails));
    return r.result.value;
  }

  close() { try { this.ws.close(); } catch { /* ignore */ } }
}

async function click(cdp, x, y) {
  await cdp.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x, y });
  await cdp.send('Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button: 'left', clickCount: 1 });
  await cdp.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x, y, button: 'left', clickCount: 1 });
}

async function clickSelector(cdp, selector) {
  const box = await cdp.eval(`(() => { const r = document.querySelector(${JSON.stringify(selector)})?.getBoundingClientRect();
    return r ? { x: r.left + r.width / 2, y: r.top + r.height / 2 } : null; })()`);
  if (!box) throw new Error('Element not found: ' + selector);
  await click(cdp, box.x, box.y);
}

// ---------- main ----------

async function main() {
  const bin = (project, tfm) => join(srcDir, project, 'bin', opt.configuration, tfm);
  const harnessExe = join(bin('FocusRemote.E2E', 'net8.0-windows10.0.19041'), 'FocusRemote.E2E.exe');
  if (!existsSync(harnessExe)) throw new Error('Build first: ' + harnessExe);

  let pageUrl, relayInput, host;
  if (opt.mode === 'relay') {
    const port = await freePort();
    const relayDll = join(bin('FocusRemote.Relay', 'net8.0'), 'FocusRemote.Relay.dll');
    const relay = startProcess('relay', 'dotnet', [relayDll, '--urls', `http://127.0.0.1:${port}`], join(srcDir, 'FocusRemote.Relay'));
    await waitForLine(relay, /Now listening on/, 30000);
    host = startProcess('host', harnessExe, ['--id', ID, '--password', PASSWORD, '--relay', `http://127.0.0.1:${port}`]);
    await waitForLine(host, /STATUS relay=Registered/, 60000);
    pageUrl = `http://127.0.0.1:${port}/`;
    relayInput = `ws://127.0.0.1:${port}/`;
  } else {
    const port = await freePort();
    host = startProcess('host', harnessExe, ['--id', ID, '--password', PASSWORD, '--lan-port', String(port)]);
    await waitForLine(host, /STATUS .*lan=True/, 60000);
    pageUrl = `https://127.0.0.1:${port}/`;
    relayInput = `wss://127.0.0.1:${port}/`;
  }
  log('host ready', pageUrl);

  const profile = join(outDir, 'chrome-profile');
  const chromeArgs = [
    `--user-data-dir=${profile}`, '--remote-debugging-port=0', '--no-first-run', '--no-default-browser-check',
    '--autoplay-policy=no-user-gesture-required', '--window-size=1382,934', 'about:blank',
  ];
  if (!opt.headful) chromeArgs.unshift('--headless=new');
  if (opt.mode === 'lan') chromeArgs.unshift('--ignore-certificate-errors');
  const chrome = startProcess('chrome', opt.chrome, chromeArgs);
  const portFile = join(profile, 'DevToolsActivePort');
  const [cdpPort] = (await waitFor('DevToolsActivePort', () => existsSync(portFile) && readFileSync(portFile, 'utf8').trim(), 30000)).split(/\r?\n/);
  const targets = await waitFor('page target', async () => {
    const list = await (await fetch(`http://127.0.0.1:${cdpPort}/json/list`)).json();
    return list.find((t) => t.type === 'page');
  });
  const cdp = new Cdp(targets.webSocketDebuggerUrl);
  await cdp.open();
  results.chrome = { pid: chrome.proc.pid, profile };
  const consoleLines = [];
  cdp.listeners.push((m) => {
    if (m.method === 'Runtime.consoleAPICalled') consoleLines.push(`${m.params.type}: ${m.params.args.map((a) => a.value ?? a.description).join(' ')}`);
    if (m.method === 'Runtime.exceptionThrown') consoleLines.push('exception: ' + JSON.stringify(m.params.exceptionDetails.exception?.description ?? m.params.exceptionDetails.text));
  });
  await cdp.send('Runtime.enable');
  await cdp.send('Page.enable');

  try {
    await cdp.send('Page.navigate', { url: pageUrl });
    await waitFor('viewer loaded', () => cdp.eval(`document.readyState === 'complete' && !!globalThis.__frStats`).catch(() => false));
    results.checks.secureContext = await cdp.eval('isSecureContext');

    // Login.
    await cdp.eval(`(() => {
      const set = (id, v) => { const e = document.getElementById(id); e.value = v; e.dispatchEvent(new Event('input')); };
      set('relayInput', ${JSON.stringify(relayInput)}); set('idInput', ${JSON.stringify(ID)}); set('passwordInput', ${JSON.stringify(PASSWORD)});
    })()`);
    await clickSelector(cdp, '#connectButton');
    const stats1 = await waitFor('authenticated + source list', async () => {
      const s = await cdp.eval('__frStats');
      return s.authenticated && s.sources.includes(opt.source) ? s : null;
    }, 60000);
    results.checks.authenticated = true;
    results.sources = stats1.sources;
    log('authenticated, sources:', stats1.sources.length);

    // Unmute with a real (trusted) click so the AudioContext may start.
    if (wantAudio) await clickSelector(cdp, '#muteButton');

    await clickSelector(cdp, `[data-source-id="${opt.source}"]`);
    const stats2 = await waitFor('decoded video frames', async () => {
      const s = await cdp.eval('__frStats');
      return s.videoFramesDrawn >= 10 ? s : null;
    }, 30000);
    results.checks.videoFramesDrawn = stats2.videoFramesDrawn;
    log('video ok', stats2);

    // Let it run a little, then screenshot.
    await delay(1500);
    const shot = await cdp.send('Page.captureScreenshot', { format: 'png' });
    const shotPath = join(outDir, `viewer-${opt.mode}.png`);
    writeFileSync(shotPath, Buffer.from(shot.data, 'base64'));
    results.screenshot = shotPath;

    // Input: mouse move + click + key over the video; verify the harness logged them.
    const lineMark = host.lines.length;
    const rect = await cdp.eval(`(() => { const r = focusRemote.video.getVideoRect(); return r && { x: r.left + r.width / 4, y: r.top + r.height / 4 }; })()`);
    if (!rect) throw new Error('No video rect');
    await cdp.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: rect.x, y: rect.y });
    await click(cdp, rect.x + 10, rect.y + 10);
    await cdp.send('Input.dispatchKeyEvent', { type: 'keyDown', code: 'KeyA', key: 'a', windowsVirtualKeyCode: 65 });
    await cdp.send('Input.dispatchKeyEvent', { type: 'keyUp', code: 'KeyA', key: 'a', windowsVirtualKeyCode: 65 });
    results.checks.inputMouseMove = await waitForLine(host, /INPUT MouseMove/, 10000, lineMark);
    results.checks.inputMouseButton = await waitForLine(host, /INPUT MouseButton Left down=True/, 10000, lineMark);
    results.checks.inputKey = await waitForLine(host, /INPUT Key KeyA down=True/, 10000, lineMark);
    log('input ok');

    if (wantAudio) {
      const stats3 = await waitFor('decoded audio', async () => {
        const s = await cdp.eval('__frStats');
        return s.audioFramesDecoded >= 5 ? s : null;
      }, 15000).catch((e) => { results.audioError = e.message; return null; });
      results.checks.audioFramesDecoded = stats3?.audioFramesDecoded ?? 0;
    }
    results.finalStats = await cdp.eval('__frStats');
    results.ok = true;
  } finally {
    results.console = consoleLines.slice(-50);
    try { await cdp.send('Browser.close'); } catch { /* already gone */ }
    cdp.close();
  }
}

async function cleanup() {
  for (const c of children.reverse()) {
    if (c.exited) continue;
    if (c.name === 'host') c.proc.stdin.end(); // harness stops on stdin EOF
    await waitFor(`${c.name} exit`, () => c.exited, 8000).catch(() => { log('killing', c.name, c.proc.pid); c.proc.kill(); });
  }
}

let exitCode = 0;
try {
  await main();
} catch (e) {
  results.ok = false;
  results.error = String(e?.stack || e);
  exitCode = 1;
} finally {
  await cleanup();
  writeFileSync(join(outDir, `result-${opt.mode}.json`), JSON.stringify(results, null, 2));
  log(results.ok ? 'PASS' : 'FAIL', JSON.stringify({ checks: results.checks, error: results.error, audioError: results.audioError }, null, 2));
  process.exit(exitCode);
}
