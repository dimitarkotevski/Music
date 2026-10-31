# Tuner & Metronome (mobile)

The tuner and metronome from the desktop app, for Android and iOS. The app is plain HTML/JS in [`www/`](www/),
wrapped as native apps with [Capacitor](https://capacitorjs.com/).

## Features

- **Start menu**: **Tuner**, **Metronome** and **Exit**, like the desktop app. **← Menu** (or Android's back button)
  returns to it and stops the tuner or metronome. Exit closes the app on Android; iOS and browsers don't let an app
  close itself, so there it stops everything and shows a goodbye screen.
- **Tuner**: the same YIN pitch detection, smoothing and auto string detection as the desktop tuner
  ([`pitch-detector.js`](www/js/pitch-detector.js) is a port of `AllTunning/Audio/PitchDetector.cs`).
  Tap a string to lock onto it and hear its reference tone; A4 reference 415–466 Hz; sensitivity slider.
- **Suggested tunings**: Standard, Drop D/C/B/A, step-down, open tunings, DADGAD, 7-string, bass and ukulele.
- **Custom tunings**: tap **+ Custom** to start from the current tuning, then set every string (1–12 strings,
  A0 to C6) with − / +, or shift all strings at once. Saved tunings appear under *My tunings*; **Edit** changes
  or deletes them. They are stored on the phone.
- **Metronome**: 30–250 BPM, hold − / + to repeat, tap tempo, 2/4 to 7/8 with an accented first beat. Clicks are
  scheduled on the audio clock, so the timing stays exact.
- Settings are remembered, and the screen stays on while tuning or while the metronome runs.

## Code

| File | Purpose |
|---|---|
| `www/js/notes.js` | Note maths and the suggested tunings |
| `www/js/pitch-detector.js` | YIN with octave and subharmonic correction |
| `www/js/audio.js` | Shared `AudioContext`, microphone listener with noise gate, reference tone |
| `www/js/capture-worklet.js` | Audio-thread worklet that delivers 25 ms microphone blocks |
| `www/js/tuner.js` | Tuner screen: gauge, readout, strings, tuning list |
| `www/js/tuning-editor.js` | Custom tuning sheet |
| `www/js/metronome.js`, `metronome-view.js` | Click scheduler and metronome screen |
| `www/js/app.js` | Start menu, Exit and Android back button, microphone lifecycle, screen wake lock |

## Try it in a browser

```
npm run serve
```

Open <http://localhost:8080>. A phone on the same Wi-Fi needs HTTPS for the microphone, so test on the phone
with the native app instead.

## Build

```
npm install
npx cap sync
```

- **Android**: install [Android Studio](https://developer.android.com/studio), then `npm run android` opens the
  project; press Run with a phone connected (USB debugging on). Or download the APK built by the
  **Mobile app** GitHub workflow (`tuner-android-debug` artifact).
- **iOS**: needs a Mac with Xcode. `npm run ios` opens the project; pick your Apple ID team under
  *Signing & Capabilities* and run it on your iPhone. The GitHub workflow checks that the iOS project compiles,
  but installing on an iPhone or publishing to the App Store needs signing with an Apple developer account.

Run `npx cap sync` after changing anything in `www/`.

Native changes: microphone permission (`RECORD_AUDIO` in `AndroidManifest.xml`, `NSMicrophoneUsageDescription`
in `Info.plist`), portrait only, and on iOS an audio session in `AppDelegate.swift` so the metronome plays
through the speaker even with the silent switch on.
