import { initMetronome, isMetronomeRunning } from './metronome-view.js';
import { initTuner, startListening, stopListening } from './tuner.js';
import { initTuningEditor } from './tuning-editor.js';
import { load, save } from './storage.js';

const $ = id => document.getElementById(id);

let screen = 'tuner';
let wakeLock = null;

initTuner();
initTuningEditor();
initMetronome();

for (const tab of document.querySelectorAll('.tab'))
  tab.addEventListener('click', () => showScreen(tab.dataset.screen));
showScreen(load('screen', 'tuner'));

// The microphone only runs while the tuner is on screen and the app is in front.
document.addEventListener('visibilitychange', () => {
  if (document.hidden)
    stopListening();
  else if (screen === 'tuner')
    startListening();
  updateWakeLock();
});
document.addEventListener('metronomechange', updateWakeLock);

function showScreen(name) {
  screen = name;
  save('screen', name);
  for (const tab of document.querySelectorAll('.tab'))
    tab.setAttribute('aria-selected', tab.dataset.screen === name);
  $('tuner').hidden = name !== 'tuner';
  $('metronome').hidden = name !== 'metronome';

  if (name === 'tuner')
    startListening();
  else
    stopListening();
  updateWakeLock();
}

/** Keeps the screen on while tuning or while the metronome is counting. */
async function updateWakeLock() {
  const wanted = !document.hidden && (screen === 'tuner' || isMetronomeRunning());
  try {
    if (wanted && !wakeLock && navigator.wakeLock) {
      wakeLock = await navigator.wakeLock.request('screen');
      wakeLock.addEventListener('release', () => { wakeLock = null; });
    } else if (!wanted && wakeLock) {
      await wakeLock.release();
      wakeLock = null;
    }
  } catch { /* not supported or refused */ }
}
