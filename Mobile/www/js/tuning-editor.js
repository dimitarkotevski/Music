import { playReferenceTone } from './audio.js';
import { MAX_STRING_MIDI, MIN_STRING_MIDI, fullName, midiToFrequency } from './notes.js';

const MIN_STRINGS = 1;
const MAX_STRINGS = 12;

const $ = id => document.getElementById(id);

let strings = [];
let options = null;

/**
 * Opens the custom tuning sheet. A new tuning starts as a copy of `base`; editing changes `base` itself.
 * Strings are kept lowest (thickest) first, the same order as the suggested tunings.
 */
export function openTuningEditor(opts) {
  options = opts;
  strings = [...opts.base.strings];
  $('editor-title').textContent = opts.isNew ? 'New tuning' : 'Edit tuning';
  $('editor-name').value = opts.isNew ? '' : opts.base.name;
  $('editor-name').placeholder = opts.isNew ? `My ${opts.base.name}` : 'Tuning name';
  $('editor-delete').hidden = opts.isNew;
  render();
  $('editor').showModal();
}

export function initTuningEditor() {
  $('editor-add').addEventListener('click', () => {
    if (strings.length >= MAX_STRINGS)
      return;
    // A new highest string, a fourth above the current top string.
    const top = strings.at(-1) ?? 52;
    strings.push(Math.min(MAX_STRING_MIDI, top + 5));
    render();
  });
  $('editor-remove').addEventListener('click', () => {
    if (strings.length > MIN_STRINGS) {
      strings.pop();
      render();
    }
  });
  $('editor-whole').addEventListener('click', () => shiftAll(-2));
  $('editor-half').addEventListener('click', () => shiftAll(-1));
  $('editor-half-up').addEventListener('click', () => shiftAll(1));
  $('editor-cancel').addEventListener('click', () => $('editor').close());
  $('editor-delete').addEventListener('click', () => {
    if (confirm(`Delete "${options.base.name}"?`)) {
      $('editor').close();
      options.onDelete(options.base.id);
    }
  });
  $('editor-form').addEventListener('submit', e => {
    e.preventDefault();
    const name = $('editor-name').value.trim() || $('editor-name').placeholder;
    const id = options.isNew ? `custom:${Date.now()}` : options.base.id;
    $('editor').close();
    options.onSave({ id, name, strings: [...strings], custom: true });
  });
}

function shiftAll(semitones) {
  if (strings.every(midi => inRange(midi + semitones))) {
    strings = strings.map(midi => midi + semitones);
    render();
  }
}

const inRange = midi => midi >= MIN_STRING_MIDI && midi <= MAX_STRING_MIDI;

function render() {
  const list = $('editor-strings');
  list.replaceChildren();

  // Shown highest string (1) first, like looking down at the guitar.
  for (let i = strings.length - 1; i >= 0; i--) {
    const row = document.createElement('li');
    row.className = 'editor-row';
    row.innerHTML = `
      <span class="editor-number">${strings.length - i}</span>
      <button type="button" class="round" aria-label="Lower string ${strings.length - i}">−</button>
      <button type="button" class="editor-note" aria-label="Play string ${strings.length - i}">${fullName(strings[i])}</button>
      <button type="button" class="round" aria-label="Raise string ${strings.length - i}">+</button>`;
    const [down, note, up] = row.querySelectorAll('button');
    down.disabled = !inRange(strings[i] - 1);
    up.disabled = !inRange(strings[i] + 1);
    down.addEventListener('click', () => changeString(i, -1));
    up.addEventListener('click', () => changeString(i, 1));
    note.addEventListener('click', () => playReferenceTone(midiToFrequency(strings[i], options.a4)));
    list.append(row);
  }

  $('editor-count').textContent = `${strings.length} string${strings.length === 1 ? '' : 's'}`;
  $('editor-remove').disabled = strings.length <= MIN_STRINGS;
  $('editor-add').disabled = strings.length >= MAX_STRINGS;
}

function changeString(index, semitones) {
  strings[index] += semitones;
  render();
  playReferenceTone(midiToFrequency(strings[index], options.a4));
}
