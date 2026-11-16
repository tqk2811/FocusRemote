// Input mode for window sources (Control SetInputMode): 'sendinput' (global SendInput, window brought to front)
// or 'message' (host posts window messages; its real mouse/keyboard stay free). Pure helpers, no DOM.

export const InputMode = Object.freeze({ SendInput: 'sendinput', Message: 'message' });

/** Any unknown/missing value falls back to SendInput (same default as the host). */
export function normalizeInputMode(value) {
  return value === InputMode.Message ? InputMode.Message : InputMode.SendInput;
}

/** The mode selector only makes sense while a window source is active (monitors always use SendInput). */
export function isWindowSource(sources, activeSourceId) {
  if (!activeSourceId || !Array.isArray(sources)) return false;
  const source = sources.find((s) => s && s.id === activeSourceId);
  return source ? source.kind === 'window' : activeSourceId.startsWith('w:');
}
