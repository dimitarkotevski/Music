import { MAX_BPM, MIN_BPM, Metronome, TIME_SIGNATURES, tempoName } from './metronome.js';
import { load, save } from './storage.js';

const TAP_RESET_GAP_MS = 2000;
// Holding − or + repeats after this delay, then every REPEAT_INTERVAL_MS.
const REPEAT_DELAY_MS = 400;
const REPEAT_INTERVAL_MS = 60;

const $ = id => document.getElementById(id);

const metronome = new Metronome({ onBeat: showBeat });
let taps = [];

export const isMetronomeRunning = () => metronome.isRunning;

export function initMetronome() {
  const select = $('signature');
  for (const signature of TIME_SIGNATURES)
    select.append(new Option(signature.name, signature.beats));
  select.value = load('beatsPerBar', 4);
  select.addEventListener('change', () => setBeatsPerBar(Number(select.value)));
  setBeatsPerBar(Number(select.value));

  const slider = $('bpm-slider');
  slider.min = MIN_BPM;
  slider.max = MAX_BPM;
  slider.addEventListener('input', () => setBpm(Number(slider.value)));
  setBpm(load('bpm', 120));

  holdToRepeat($('bpm-down'), () => setBpm(metronome.bpm - 1));
  holdToRepeat($('bpm-up'), () => setBpm(metronome.bpm + 1));
  $('tap').addEventListener('pointerdown', tapTempo);
  $('start-stop').addEventListener('click', toggle);
}

async function toggle() {
  const button = $('start-stop');
  if (metronome.isRunning) {
    metronome.stop();
  } else {
    try {
      await metronome.start();
    } catch {
      $('tempo-name').textContent = 'Could not start audio';
      return;
    }
  }
  button.textContent = metronome.isRunning ? 'Stop' : 'Start';
  button.classList.toggle('stop', metronome.isRunning);
  document.dispatchEvent(new Event('metronomechange'));
}

function setBpm(bpm) {
  metronome.setBpm(bpm);
  $('bpm').textContent = metronome.bpm;
  $('tempo-name').textContent = tempoName(metronome.bpm);
  $('bpm-slider').value = metronome.bpm;
  save('bpm', metronome.bpm);
}

function setBeatsPerBar(beats) {
  metronome.beatsPerBar = beats;
  save('beatsPerBar', beats);
  const panel = $('beats');
  panel.replaceChildren();
  for (let i = 0; i < beats; i++) {
    const dot = document.createElement('span');
    dot.className = i === 0 ? 'beat accent' : 'beat';
    panel.append(dot);
  }
}

function showBeat(beat) {
  [...$('beats').children].forEach((dot, i) => dot.classList.toggle('on', i === beat));
}

function tapTempo() {
  const now = performance.now();
  if (taps.length && now - taps.at(-1) > TAP_RESET_GAP_MS)
    taps = [];
  taps.push(now);
  if (taps.length > 5)
    taps.shift();
  if (taps.length >= 2)
    setBpm(60000 / ((taps.at(-1) - taps[0]) / (taps.length - 1)));
}

function holdToRepeat(button, action) {
  let timer = null;
  const stop = () => {
    clearTimeout(timer);
    clearInterval(timer);
    timer = null;
  };
  button.addEventListener('pointerdown', e => {
    e.preventDefault();
    action();
    timer = setTimeout(() => { timer = setInterval(action, REPEAT_INTERVAL_MS); }, REPEAT_DELAY_MS);
  });
  for (const type of ['pointerup', 'pointerleave', 'pointercancel'])
    button.addEventListener(type, stop);
}
