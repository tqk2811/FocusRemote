// Remote terminal panel: xterm.js tabs over the Terminal channel (docs/Protocol-vi.md §5.5).

import {
  TerminalType, MAX_TERMINALS, encodeJson, utf8,
  buildTerminalData, parseTerminalData, buildTerminalResize, buildTerminalClose,
  parseShellList, parseTerminalEvent,
} from './protocol.js';

const DEFAULT_COLS = 80;
const DEFAULT_ROWS = 24;
const MAX_PENDING_BYTES = 64 * 1024;
const PENDING_LABEL = 'Đang mở…';
const THEME = { background: '#0b0d10', foreground: '#e5e7eb', cursor: '#e5e7eb', selectionBackground: '#3b82f680' };

export class TerminalPanel {
  /**
   * @param {{ view: HTMLElement, tabs: HTMLElement, newButton: HTMLButtonElement, shellMenu: HTMLElement,
   *           message: HTMLElement, host: HTMLElement, toggleButton: HTMLButtonElement,
   *           send: (type: number, payload: Uint8Array) => void, isConnected: () => boolean,
   *           onBeforeShow?: () => void, onVisibilityChange?: (visible: boolean) => void }} options
   */
  constructor(options) {
    this.o = options;
    this.visible = false;
    this.shellInfo = null;       // { enabled, shells } once the host sent ShellList
    this.tabs = new Map();       // terminalId -> tab
    this.activeId = null;
    this.nextId = 1;
    this.resizeTimer = 0;

    this.o.toggleButton.addEventListener('click', () => this.toggle());
    this.o.newButton.addEventListener('click', () => this._onNewClicked());
    if (typeof ResizeObserver !== 'undefined') new ResizeObserver(() => this._scheduleFit()).observe(this.o.host);
    this._renderMessage();
  }

  get count() { return this.tabs.size; }
  get openCount() { let n = 0; for (const t of this.tabs.values()) if (!t.closed) n++; return n; }

  // ---------- visibility ----------

  toggle() { this.setVisible(!this.visible); }

  setVisible(visible) {
    if (this.visible === visible) return;
    if (visible) this.o.onBeforeShow?.();
    this.visible = visible;
    this.o.view.hidden = !visible;
    this.o.toggleButton.setAttribute('aria-pressed', String(visible));
    this.o.toggleButton.classList.toggle('active', visible);
    if (visible) {
      if (!this.shellInfo && this.o.isConnected()) this.o.send(TerminalType.ListShells, new Uint8Array(0));
      this._renderMessage();
      this._fitActive();
      this._focusActive();
    } else {
      this._hideShellMenu();
    }
    this.o.onVisibilityChange?.(visible);
  }

  // ---------- messages from the host ----------

  handle(type, payload) {
    switch (type) {
      case TerminalType.ShellList: {
        const info = parseShellList(payload);
        if (!info) return;
        this.shellInfo = info;
        this._renderMessage();
        this._renderShellMenu();
        return;
      }
      case TerminalType.Opened: {
        const msg = parseTerminalEvent(payload);
        const tab = msg && this.tabs.get(msg.terminalId);
        if (tab && !tab.closed && !tab.opened) this._markOpened(tab);
        return;
      }
      case TerminalType.Closed: {
        const msg = parseTerminalEvent(payload);
        if (!msg) return;
        const tab = this.tabs.get(msg.terminalId);
        if (tab) this._markClosed(tab, closedText(msg));
        return;
      }
      case TerminalType.Output: {
        const data = parseTerminalData(payload);
        const tab = data && this.tabs.get(data.terminalId);
        if (tab) tab.term.write(data.data.slice()); // copy: write() is asynchronous
        return;
      }
      default:
        return; // unknown terminal types are ignored
    }
  }

  /** Connection lost or replaced: every tab becomes read-only, shell list is stale. */
  onDisconnect() {
    this.shellInfo = null;
    for (const tab of this.tabs.values()) {
      if (!tab.closed) { tab.lostConnection = true; this._markClosed(tab, '[Mất kết nối với host]'); }
    }
    this._renderMessage();
    this._hideShellMenu();
  }

  /** A new authenticated session started: drop tabs that died with the previous connection. */
  onConnected() {
    for (const tab of [...this.tabs.values()]) {
      if (tab.lostConnection) this._removeTab(tab);
    }
    this._renderMessage();
  }

  // ---------- tabs ----------

  _onNewClicked() {
    const info = this.shellInfo;
    if (!info) {
      if (this.o.isConnected()) this.o.send(TerminalType.ListShells, new Uint8Array(0));
      return;
    }
    if (!info.enabled || !info.shells.length) return;
    if (info.shells.length === 1) { this.openShell(info.shells[0]); return; }
    const menu = this.o.shellMenu;
    if (menu.hidden) { this._renderShellMenu(); menu.hidden = false; } else this._hideShellMenu();
  }

  openShell(shell) {
    this._hideShellMenu();
    if (!this.o.isConnected()) return;
    if (this.openCount >= MAX_TERMINALS) { this._setMessage(`Tối đa ${MAX_TERMINALS} terminal cùng lúc.`); return; }
    if (typeof Terminal === 'undefined' || typeof FitAddon === 'undefined') { this._setMessage('Không nạp được xterm.js.'); return; }

    const id = this.nextId++;
    const container = document.createElement('div');
    container.className = 'terminal-pane';
    this.o.host.append(container);

    const term = new Terminal({
      cursorBlink: true,
      fontSize: matchMedia('(max-width: 720px)').matches ? 13 : 14,
      fontFamily: 'ui-monospace, "Cascadia Mono", Consolas, Menlo, monospace',
      scrollback: 5000,
      theme: THEME,
    });
    const fit = new FitAddon.FitAddon();
    term.loadAddon(fit);

    const tab = {
      id, shell, term, fit, container, closed: false, tabEl: null, labelEl: null,
      opened: false, lostConnection: false, pending: [], pendingBytes: 0, sentCols: 0, sentRows: 0,
    };
    this.tabs.set(id, tab);
    this._buildTabElement(tab);
    this._activate(id);          // makes the pane visible so fit can measure it
    term.open(container);
    const size = this._proposeSize(tab);
    term.resize(size.cols, size.rows); // before onResize is registered: Open already carries this size

    term.attachCustomKeyEventHandler((ev) => !this._tryCopy(term, ev));
    term.onData((text) => this._sendInput(tab, utf8(text)));
    term.onBinary((text) => {
      const bytes = new Uint8Array(text.length);
      for (let i = 0; i < text.length; i++) bytes[i] = text.charCodeAt(i) & 0xff;
      this._sendInput(tab, bytes);
    });
    // Before Opened the host does not know the terminal yet; the size is reconciled in _markOpened.
    term.onResize(({ cols, rows }) => {
      if (tab.closed || !tab.opened) return;
      tab.sentCols = cols; tab.sentRows = rows;
      this.o.send(TerminalType.Resize, buildTerminalResize(id, cols, rows));
    });

    tab.sentCols = size.cols; tab.sentRows = size.rows;
    tab.labelEl.textContent = PENDING_LABEL;
    this.o.send(TerminalType.Open, encodeJson({ terminalId: id, shellId: shell.id, cols: size.cols, rows: size.rows }));
    this._renderMessage();
    term.focus();
  }

  closeTab(id) {
    const tab = this.tabs.get(id);
    if (!tab) return;
    if (!tab.closed && this.o.isConnected()) this.o.send(TerminalType.Close, buildTerminalClose(id));
    this._removeTab(tab);
    this._renderMessage();
  }

  _removeTab(tab) {
    tab.closed = true;
    tab.pending = [];
    tab.term.dispose();
    tab.container.remove();
    tab.tabEl.remove();
    this.tabs.delete(tab.id);
    if (this.activeId === tab.id) {
      this.activeId = null;
      const next = [...this.tabs.keys()].pop();
      if (next !== undefined) this._activate(next, true);
    }
  }

  _markOpened(tab) {
    tab.opened = true;
    tab.labelEl.textContent = tab.shell.name;
    const pending = tab.pending;
    tab.pending = []; tab.pendingBytes = 0;
    if (tab.term.cols !== tab.sentCols || tab.term.rows !== tab.sentRows) {
      tab.sentCols = tab.term.cols; tab.sentRows = tab.term.rows;
      this.o.send(TerminalType.Resize, buildTerminalResize(tab.id, tab.sentCols, tab.sentRows));
    }
    for (const bytes of pending) this.o.send(TerminalType.Input, buildTerminalData(tab.id, bytes));
  }

  _sendInput(tab, bytes) {
    if (tab.closed) return;
    if (tab.opened) { this.o.send(TerminalType.Input, buildTerminalData(tab.id, bytes)); return; }
    if (tab.pendingBytes + bytes.length > MAX_PENDING_BYTES) return; // drop: host never answered
    tab.pending.push(bytes);
    tab.pendingBytes += bytes.length;
  }

  /** Ctrl+C / Ctrl+Shift+C copies the selection instead of sending ^C; returns true when handled. */
  _tryCopy(term, ev) {
    if (ev.type !== 'keydown' || !ev.ctrlKey || ev.altKey || ev.metaKey) return false;
    if (ev.key !== 'c' && ev.key !== 'C') return false;
    if (!term.hasSelection()) return false;
    try { navigator.clipboard?.writeText(term.getSelection()).catch(() => {}); } catch { /* clipboard unavailable */ }
    return true;
  }

  _markClosed(tab, text) {
    tab.closed = true;
    tab.pending = [];
    tab.term.write(`\r\n\x1b[33m${text}\x1b[0m\r\n`);
    tab.tabEl.classList.add('closed');
    tab.labelEl.textContent = `${tab.shell.name} (đã đóng)`;
  }

  _buildTabElement(tab) {
    const wrap = document.createElement('div');
    wrap.className = 'terminal-tab';
    wrap.setAttribute('role', 'tab');
    const label = document.createElement('button');
    label.type = 'button';
    label.className = 'terminal-tab-label';
    label.textContent = tab.shell.name;
    label.addEventListener('click', () => this._activate(tab.id, true));
    const close = document.createElement('button');
    close.type = 'button';
    close.className = 'terminal-tab-close';
    close.title = 'Đóng terminal';
    close.textContent = '✕';
    close.addEventListener('click', () => this.closeTab(tab.id));
    wrap.append(label, close);
    this.o.tabs.append(wrap);
    tab.tabEl = wrap;
    tab.labelEl = label;
  }

  _activate(id, focus = false) {
    this.activeId = id;
    for (const t of this.tabs.values()) {
      const on = t.id === id;
      t.container.hidden = !on;
      t.tabEl.classList.toggle('active', on);
      t.tabEl.setAttribute('aria-selected', String(on));
    }
    if (focus) { this._fitActive(); this._focusActive(); }
  }

  // ---------- sizing ----------

  _proposeSize(tab) {
    try {
      const d = tab.fit.proposeDimensions();
      if (d && d.cols > 1 && d.rows > 0) return { cols: d.cols, rows: d.rows };
    } catch { /* not measurable yet */ }
    return { cols: DEFAULT_COLS, rows: DEFAULT_ROWS };
  }

  _scheduleFit() {
    clearTimeout(this.resizeTimer);
    this.resizeTimer = setTimeout(() => this._fitActive(), 60);
  }

  _fitActive() {
    const tab = this.tabs.get(this.activeId);
    if (!tab || !this.visible) return;
    try { tab.fit.fit(); } catch { /* host has no size yet */ }
  }

  _focusActive() { this.tabs.get(this.activeId)?.term.focus(); }

  // ---------- status / shell menu ----------

  _renderMessage() {
    if (!this.shellInfo) {
      this._setMessage(this.tabs.size ? '' : (this.o.isConnected() ? 'Đang lấy danh sách shell…' : ''));
    } else if (!this.shellInfo.enabled) {
      this._setMessage('Host chưa bật terminal');
    } else if (!this.shellInfo.shells.length) {
      this._setMessage('Host không có shell nào khả dụng.');
    } else {
      this._setMessage(this.tabs.size ? '' : 'Bấm ＋ để mở terminal mới.');
    }
    const canOpen = !!this.shellInfo?.enabled && this.shellInfo.shells.length > 0;
    this.o.newButton.disabled = !canOpen;
  }

  _setMessage(text) {
    this.o.message.textContent = text;
    this.o.message.hidden = !text;
  }

  _renderShellMenu() {
    const menu = this.o.shellMenu;
    menu.replaceChildren();
    for (const shell of this.shellInfo?.shells || []) {
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.dataset.shellId = shell.id;
      btn.textContent = shell.name;
      btn.addEventListener('click', () => this.openShell(shell));
      menu.append(btn);
    }
  }

  _hideShellMenu() { this.o.shellMenu.hidden = true; }
}

/** Strips ESC and other C0/C1 control characters (and DEL) from host-provided text before it reaches the terminal. */
export function sanitizeHostText(text) {
  return String(text ?? '').replace(/[\u0000-\u001f\u007f-\u009f]/g, '');
}

export function closedText(msg) {
  if (Number.isInteger(msg.exitCode)) return `[Tiến trình đã kết thúc (mã ${msg.exitCode})]`;
  const why = sanitizeHostText(msg.message || msg.reason);
  return why ? `[Terminal đã đóng: ${why}]` : '[Tiến trình đã kết thúc]';
}
