# Code guide

A map of the code so you can find things quickly. Start with the table below, then open the page for the area you're working on.

| Page | Covers |
|---|---|
| [App & navigation](app-and-navigation.md) | `MainWindow` layout, the start menu, switching screens, styles, colours, shortcuts, startup/shutdown |
| [Audio layer](audio.md) | Microphone capture, noise gate, YIN pitch detection, reference tones |
| [Tuner](tuner.md) | Tuner screen: gauge, string selection, auto-detect, smoothing |
| [Chords](chords.md) | Chord recognition (FFT → chromagram → template matching), chord shapes and diagrams, chord screen |
| [Metronome](metronome.md) | Click engine and metronome screen |
| [Music theory helpers](music-theory.md) | Note/frequency/cents maths, guitar tunings |

> Members are referenced by name instead of line number, so the docs stay correct as the code changes. Search for the name (Ctrl+F / Ctrl+T in Visual Studio) to jump to it.

## Project layout

```
Music/
├── App.xaml(.cs)               App entry point, opens MainWindow
├── MainWindow.xaml             All screens: start menu, tuner, chords, metronome
├── MainWindow.xaml.cs          Shared window logic + tuner screen
├── MainWindow.Chords.cs        Chord screen (partial class)
├── MainWindow.Metronome.cs     Metronome screen (partial class)
├── Audio/
│   ├── MicrophoneListener.cs   Mic capture, noise gate, raises pitch/sample events
│   ├── PitchDetector.cs        YIN pitch detection + octave-error correction
│   ├── TonePlayer.cs           Reference tone for the tuner's string buttons
│   └── Metronome.cs            Sample-accurate click engine + time signatures
├── Chords/
│   ├── Chord.cs                Chord and ChordQuality (chord types)
│   ├── ChordDetector.cs        Chord recognition
│   └── ChordShape.cs           Guitar fingerings + shape library
├── Controls/
│   └── ChordDiagram.cs         Draws a chord box (fretboard diagram)
└── Tuning/
    ├── NoteMath.cs             Frequency ↔ note ↔ cents conversions
    └── GuitarTuning.cs         The list of guitar tunings
```

## Where do I find…?

| I want to… | Go to |
|---|---|
| Add a guitar tuning | `GuitarTuning.All` in [Tuning/GuitarTuning.cs](../../Music/Tuning/GuitarTuning.cs) |
| Add a chord type (e.g. 9th, 6th) | `ChordQuality.All` in [Chords/Chord.cs](../../Music/Chords/Chord.cs) |
| Add a time signature | `TimeSignature.All` in [Audio/Metronome.cs](../../Music/Audio/Metronome.cs) |
| Add a button to the start menu | `StartMenu` in [MainWindow.xaml](../../Music/MainWindow.xaml) + a `Start…_Click` handler, see [App & navigation](app-and-navigation.md#adding-a-new-screen) |
| Change what counts as "in tune" | `InTuneCents` in [MainWindow.xaml.cs](../../Music/MainWindow.xaml.cs) |
| Make the tuner ignore more/less noise | `MicrophoneListener.GateRatio` / `MinimumGate`, the Sensitivity slider handler `SensitivitySlider_ValueChanged` |
| Fix wrong notes / octave jumps | `PitchDetector.Detect` and `CorrectOctaveError` in [Audio/PitchDetector.cs](../../Music/Audio/PitchDetector.cs) |
| Fix the wrong string being picked in auto mode | `AutoDetectString` / `NearestString` in [MainWindow.xaml.cs](../../Music/MainWindow.xaml.cs) |
| Fix wrong chords | `ChordDetector` constants and `Match` in [Chords/ChordDetector.cs](../../Music/Chords/ChordDetector.cs) |
| Fix wrong slash chords / bass note | `ChordDetector.FindBass` |
| Change how a chord is fingered | `ChordShapes` (`OpenShapes`, `EShapes`, `AShapes`) in [Chords/ChordShape.cs](../../Music/Chords/ChordShape.cs) |
| Change how chord diagrams look | `ChordDiagram.OnRender` in [Controls/ChordDiagram.cs](../../Music/Controls/ChordDiagram.cs) |
| Change the metronome click sound | `ClickTrack.CreateClick` in [Audio/Metronome.cs](../../Music/Audio/Metronome.cs) |
| Change the tuner's reference tone | `TonePlayer.PluckedTone` in [Audio/TonePlayer.cs](../../Music/Audio/TonePlayer.cs) |
| Change colours | The `…Brush` fields, see [colour table](app-and-navigation.md#colours) |
| Change button looks | `StringButton`, `SmallButton`, `MenuButton` styles in [MainWindow.xaml](../../Music/MainWindow.xaml) |
| Change keyboard shortcuts | `Window.InputBindings` in [MainWindow.xaml](../../Music/MainWindow.xaml) |

## How audio flows

```
Microphone ──► MicrophoneListener (44.1 kHz mono, 50 ms blocks, noise gate)
                 │
                 ├─ PitchUpdated ──► PitchDetector (YIN) ──► MainWindow.OnPitch ──► tuner gauge
                 │
                 └─ SamplesCaptured ──► ChordDetector (FFT, chroma) ──► ShowChordAnalysis ──► chord screen

Metronome.ClickTrack ──► speakers          (Beat event ──► ShowBeat ──► beat lights)
TonePlayer.PluckedTone ──► speakers        (string button clicks)
```

Audio events fire on **background threads**. The UI code always moves back to the UI thread with `Dispatcher.BeginInvoke` before touching controls.
