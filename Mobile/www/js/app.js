import { initMetronome, isMetronomeRunning, stopMetronome } from './metronome-view.js';
import { initTuner, startListening, stopListening } from './tuner.js';
import { initTuningEditor } from './tuning-editor.js';

const $ = id => document.getElementById(id);

// Capacitor injects its bridge (and plugins such as App) only in the native apps.
const capacitor = window.Capacitor;
const platform = capacitor?.getPlatform?.() ?? 'web';
const nativeApp = capacitor?.Plugins?.App;

let screen = 'menu';
let wakeLock = null;

initTuner();
initTuningEditor();
initMetronome();

for (const button of document.querySelectorAll('[data-open]'))
  button.addEventListener('click', () => showScreen(button.dataset.open));
for (const button of document.querySelectorAll('.back'))
  button.addEventListener('click', () => showScreen('menu'));

// Only Android lets an app close itself. iOS and a browser tab can't, so Exit stops everything and says goodbye.
$('exit').addEventListener('click', () => {
  if (platform === 'android' && nativeApp)
    nativeApp.exitApp();
  else
    showScreen('goodbye');
});

// Android's back button returns to the menu, and from the menu leaves the app.
nativeApp?.addListener('backButton', () => {
  if ($('editor').open)
    $('editor').close();
  else if (screen !== 'menu')
    showScreen('menu');
  else
    nativeApp.exitApp();
});

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
  for (const id of ['menu', 'tuner', 'metronome', 'goodbye'])
    $(id).hidden = id !== name;

  // Like the desktop app, going back to the menu stops whatever was running.
  if (name === 'tuner')
    startListening();
  else
    stopListening();
  if (name !== 'metronome')
    stopMetronome();

  $('main').scrollTop = 0;
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
