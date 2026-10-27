import { MicrophoneListener, playReferenceTone } from './audio.js';
import { SUGGESTED_TUNINGS, cents as centsBetween, frequencyToMidi, midiToFrequency, noteName, octave, tuningLabel } from './notes.js';
import { load, save } from './storage.js';
import { openTuningEditor } from './tuning-editor.js';

const IN_TUNE_CENTS = 5;
// Once green, the reading must drift this much further before it turns orange again, so it doesn't flicker.
const IN_TUNE_HYSTERESIS_CENTS = 2;
const GAUGE_RANGE_CENTS = 50;
const GAUGE_SWEEP_DEGREES = 60;
const GAUGE_CX = 160, GAUGE_CY = 170, GAUGE_RADIUS = 140;

const HOLD_TIME_MS = 1500;
const NEW_NOTE_GAP_MS = 250;
// A pitch must hold steady (within STABLE_CENTS) for this many readings before it is shown.
const STABLE_READINGS_REQUIRED = 3;
const STABLE_CENTS = 40;
// Each reading moves the shown pitch this fraction of the way to the new one; a bigger jump snaps.
const PITCH_SMOOTHING = 0.35;
const NEW_PITCH_CENTS = 50;
const NEEDLE_TIME_CONSTANT_SECONDS = 0.06;
// In auto mode a note keeps its string unless another string is heard for this many readings in a row.
const STRING_SWITCH_READINGS = 10;

const $ = id => document.getElementById(id);

let customTunings = load('customTunings', []);
let tuning = findTuning(load('tuningId', SUGGESTED_TUNINGS[0].id)) ?? SUGGESTED_TUNINGS[0];
let a4 = load('a4', 440);
let manualString = null;
let inTune = false;
let lastPitchTime = 0;
let lastRawFrequency = null;
let stableReadings = 0;
let smoothedFrequency = null;
let recentFrequencies = [];
let autoString = null;
let stringSwitchVotes = 0;
let needleCents = 0;
let needleTarget = 0;
let lastFrame = performance.now();

const listener = new MicrophoneListener({ onReading });

function findTuning(id) {
  return [...customTunings, ...SUGGESTED_TUNINGS].find(t => t.id === id);
}

export function initTuner() {
  drawGauge();
  fillTuningSelect();
  setA4(a4);
  buildStringButtons();
  showIdle();

  $('tuning-select').addEventListener('change', e => selectTuning(findTuning(e.target.value)));
  $('a4-down').addEventListener('click', () => setA4(a4 - 1));
  $('a4-up').addEventListener('click', () => setA4(a4 + 1));
  $('auto-button').addEventListener('click', () => {
    manualString = null;
    showIdle();
  });
  $('new-tuning').addEventListener('click', () =>
    openTuningEditor({ base: tuning, isNew: true, a4, onSave: saveCustomTuning }));
  $('edit-tuning').addEventListener('click', () =>
    openTuningEditor({ base: tuning, isNew: false, a4, onSave: saveCustomTuning, onDelete: deleteCustomTuning }));
  $('mic-start').addEventListener('click', startListening);

  const sensitivity = $('sensitivity');
  sensitivity.value = load('sensitivity', 0.5);
  const applySensitivity = () => {
    // 0 → sound must be ~16x louder than the background, 1 → no extra margin.
    listener.gateRatio = 10 ** ((1 - sensitivity.value) * 1.2);
    save('sensitivity', Number(sensitivity.value));
  };
  sensitivity.addEventListener('input', applySensitivity);
  applySensitivity();

  requestAnimationFrame(animateNeedle);
}

export async function startListening() {
  try {
    await listener.start();
    $('mic-start').hidden = true;
    showIdle();
  } catch (error) {
    $('mic-start').hidden = false;
    $('hint').textContent = error?.name === 'NotAllowedError'
      ? 'Allow microphone access to tune'
      : error?.name === 'NotFoundError' ? 'No microphone found' : 'Tap Start to tune';
  }
}

export function stopListening() {
  listener.stop();
  $('mic-start').hidden = false;
}

function fillTuningSelect() {
  const select = $('tuning-select');
  select.replaceChildren();
  const group = (label, tunings) => {
    if (!tunings.length)
      return;
    const optgroup = document.createElement('optgroup');
    optgroup.label = label;
    for (const t of tunings)
      optgroup.append(new Option(`${t.name}  (${tuningLabel(t)})`, t.id));
    select.append(optgroup);
  };
  group('My tunings', customTunings);
  group('Suggested', SUGGESTED_TUNINGS);
  select.value = tuning.id;
  $('edit-tuning').hidden = !tuning.custom;
}

function selectTuning(selected) {
  if (!selected)
    return;
  tuning = selected;
  manualString = null;
  autoString = null;
  save('tuningId', tuning.id);
  fillTuningSelect();
  buildStringButtons();
  showIdle();
}

function saveCustomTuning(saved) {
  const index = customTunings.findIndex(t => t.id === saved.id);
  if (index >= 0)
    customTunings[index] = saved;
  else
    customTunings.push(saved);
  save('customTunings', customTunings);
  selectTuning(saved);
}

function deleteCustomTuning(id) {
  customTunings = customTunings.filter(t => t.id !== id);
  save('customTunings', customTunings);
  selectTuning(SUGGESTED_TUNINGS[0]);
}

function setA4(value) {
  a4 = Math.min(466, Math.max(415, value));
  $('a4-value').textContent = `A4 = ${a4} Hz`;
  save('a4', a4);
}

function onReading(reading) {
  const bar = $('level-fill');
  bar.style.width = `${Math.min(1, Math.sqrt(reading.level) * 3) * 100}%`;
  bar.classList.toggle('gated', reading.level < reading.gate);

  const now = performance.now();
  if (!isStable(reading.frequency) || reading.frequency === null) {
    if (now - lastPitchTime > NEW_NOTE_GAP_MS) {
      recentFrequencies = [];
      smoothedFrequency = null;
      autoString = null;
    }
    if (now - lastPitchTime > HOLD_TIME_MS)
      showIdle();
    return;
  }

  // A median of three drops single bad readings; the easing then removes the jitter.
  lastPitchTime = now;
  recentFrequencies.push(reading.frequency);
  if (recentFrequencies.length > 3)
    recentFrequencies.shift();
  const median = medianOf(recentFrequencies);
  const smoothed = smoothedFrequency !== null && Math.abs(centsBetween(median, smoothedFrequency)) < NEW_PITCH_CENTS
    ? smoothedFrequency * (median / smoothedFrequency) ** PITCH_SMOOTHING
    : median;
  smoothedFrequency = smoothed;

  const stringIndex = manualString ?? autoDetectString(smoothed);
  const targetMidi = tuning.strings[stringIndex];
  const cents = centsBetween(smoothed, midiToFrequency(targetMidi, a4));
  const detectedMidi = Math.round(frequencyToMidi(smoothed, a4));

  inTune = Math.abs(cents) <= IN_TUNE_CENTS + (inTune ? IN_TUNE_HYSTERESIS_CENTS : 0);
  const state = inTune ? 'in-tune' : 'off';
  $('note').textContent = noteName(targetMidi);
  $('cents').textContent = `${cents > 0 ? '+' : ''}${Math.round(cents)} cents`;
  $('frequency').textContent = `${smoothed.toFixed(1)} Hz · hearing ${noteName(detectedMidi)}${octave(detectedMidi)}`;
  $('hint').textContent = inTune ? 'In tune ✓' : cents < 0 ? 'Tune up ↑' : 'Tune down ↓';
  $('tuner').dataset.state = state;

  needleTarget = Math.max(-GAUGE_RANGE_CENTS, Math.min(GAUGE_RANGE_CENTS, cents));
  highlightString(stringIndex);
}

/** Background noise jumps around; a ringing string holds its pitch. */
function isStable(frequency) {
  if (frequency !== null && lastRawFrequency !== null && Math.abs(centsBetween(frequency, lastRawFrequency)) <= STABLE_CENTS)
    stableReadings++;
  else
    stableReadings = frequency === null ? 0 : 1;
  lastRawFrequency = frequency;
  return stableReadings >= STABLE_READINGS_REQUIRED;
}

function showIdle() {
  $('note').textContent = manualString !== null ? noteName(tuning.strings[manualString]) : '–';
  $('hint').textContent = listener.isRunning ? 'Play a string' : 'Tap Start to tune';
  $('cents').textContent = ' ';
  $('frequency').textContent = ' ';
  $('tuner').dataset.state = 'idle';
  $('auto-button').classList.toggle('active', manualString === null);
  inTune = false;
  needleTarget = 0;
  highlightString(manualString ?? -1);
}

/** Picks the nearest string, but sticks with it for the rest of the note to avoid flickering. */
function autoDetectString(frequency) {
  const nearest = nearestString(frequency);
  if (autoString === null || nearest === autoString || ++stringSwitchVotes >= STRING_SWITCH_READINGS) {
    autoString = nearest;
    stringSwitchVotes = 0;
  }
  return autoString;
}

function nearestString(frequency) {
  let best = 0;
  let bestDistance = Infinity;
  tuning.strings.forEach((midi, i) => {
    const distance = Math.abs(centsBetween(frequency, midiToFrequency(midi, a4)));
    if (distance < bestDistance) {
      bestDistance = distance;
      best = i;
    }
  });
  return best;
}

function medianOf(values) {
  const sorted = [...values].sort((a, b) => a - b);
  const mid = sorted.length >> 1;
  return sorted.length % 2 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
}

function buildStringButtons() {
  const panel = $('strings');
  panel.replaceChildren();
  tuning.strings.forEach((midi, i) => {
    const button = document.createElement('button');
    button.className = 'string-button';
    button.innerHTML = `<span>${noteName(midi)}</span><small>${tuning.strings.length - i}</small>`;
    button.setAttribute('aria-label', `String ${tuning.strings.length - i}, ${noteName(midi)}${octave(midi)}: select and hear the reference tone`);
    button.addEventListener('click', () => {
      manualString = i;
      showIdle();
      playReferenceTone(midiToFrequency(midi, a4));
    });
    panel.append(button);
  });

  // Lowest string decides how low the detector has to listen.
  const lowest = Math.min(...tuning.strings);
  listener.setMinFrequency(Math.min(60, Math.max(25, midiToFrequency(lowest, a4) * 0.7)));
}

function highlightString(index) {
  [...$('strings').children].forEach((button, i) => button.classList.toggle('active', i === index));
}

/** Runs every frame so the needle glides between readings instead of jumping. */
function animateNeedle(now) {
  const elapsed = Math.min(0.1, Math.max(0, (now - lastFrame) / 1000));
  lastFrame = now;
  needleCents += (needleTarget - needleCents) * (1 - Math.exp(-elapsed / NEEDLE_TIME_CONSTANT_SECONDS));
  $('needle').setAttribute('transform', `rotate(${needleCents / GAUGE_RANGE_CENTS * GAUGE_SWEEP_DEGREES} ${GAUGE_CX} ${GAUGE_CY})`);
  requestAnimationFrame(animateNeedle);
}

function gaugePoint(cents, radius) {
  const angle = cents / GAUGE_RANGE_CENTS * GAUGE_SWEEP_DEGREES * Math.PI / 180;
  return [GAUGE_CX + radius * Math.sin(angle), GAUGE_CY - radius * Math.cos(angle)];
}

function drawGauge() {
  const svg = $('gauge');
  const ns = 'http://www.w3.org/2000/svg';
  const add = (tag, attributes, text) => {
    const element = document.createElementNS(ns, tag);
    for (const [key, value] of Object.entries(attributes))
      element.setAttribute(key, value);
    if (text)
      element.textContent = text;
    svg.insertBefore(element, $('needle'));
  };

  // Green "in tune" band.
  const bandRadius = GAUGE_RADIUS + 6;
  const [sx, sy] = gaugePoint(-IN_TUNE_CENTS, bandRadius);
  const [ex, ey] = gaugePoint(IN_TUNE_CENTS, bandRadius);
  add('path', { d: `M${sx} ${sy} A${bandRadius} ${bandRadius} 0 0 1 ${ex} ${ey}`, class: 'gauge-band' });

  for (let c = -50; c <= 50; c += 5) {
    const major = c % 10 === 0;
    const [x1, y1] = gaugePoint(c, GAUGE_RADIUS);
    const [x2, y2] = gaugePoint(c, GAUGE_RADIUS - (major ? 16 : 8));
    add('line', { x1, y1, x2, y2, class: c === 0 ? 'tick zero' : major ? 'tick major' : 'tick' });
  }

  for (const c of [-50, -25, 25, 50]) {
    const [x, y] = gaugePoint(c, GAUGE_RADIUS - 32);
    add('text', { x, y: y + 4, class: 'gauge-label' }, c > 0 ? `+${c}` : `${c}`);
  }
}
