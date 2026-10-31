# Metronome

[← Code guide](README.md)

| File | Contents |
|---|---|
| [Audio/Metronome.cs](../../AllTunning/Audio/Metronome.cs) | `TimeSignature`, `Metronome` (engine), private `ClickTrack` (sample generator) |
| [MainWindow.Metronome.cs](../../AllTunning/MainWindow.Metronome.cs) | Metronome screen UI |

## Engine: `Metronome`

| Member | What it does |
|---|---|
| `Bpm` | Tempo, clamped to `MinBpm`–`MaxBpm` (30–250). Can change while running |
| `BeatsPerBar` | Beats per bar; beat 0 is accented. Can change while running |
| `Start()` / `Stop()` / `IsRunning` | Creates/destroys the `WaveOutEvent` + `ClickTrack` |
| `Beat` event | Beat index, raised on a background thread roughly when the click is **heard** |
| `OnBeatStarted` | Delays `Beat` by `OutputLatencyMs` (80) + the beat's offset in the buffer, so the lights match the sound |

### Why it doesn't drift: `ClickTrack.Read`

Clicks are written straight into the audio buffer, accurate to one sample, instead of being triggered by a UI timer. `samplesToNextBeat` is a `double` that counts down per sample; at zero it starts a click and adds `sampleRate × 60 / bpm`. Tempo changes apply from the next beat.

`CreateClick(frequency, amplitude)`: a 40 ms sine with fast exponential decay. Accent: 1600 Hz at 0.7; other beats: 1000 Hz at 0.45.

### Time signatures: `TimeSignature.All`

2/4, 3/4, 4/4, 5/4, 6/8, 7/8, as `TimeSignature(Name, Beats)`. Add a line there to add one. Only the first beat is accented; 6/8 doesn't accent beat 4.

## Screen (`MetronomeView`)

| Member | Purpose |
|---|---|
| `InitializeMetronome` | Subscribes `metronome.Beat` → `ShowBeat`, fills `TimeSignatureBox`, sets 4/4 and 120 BPM |
| `StartMetronome_Click` | Shows the screen |
| `StartStop_Click` / `StopMetronome` | Toggles playback; button turns `StopBrush` red while running |
| `SetBpm` | Single place that updates the engine, `BpmText`, `TempoNameText` and `BpmSlider` |
| `BpmSlider_ValueChanged`, `BpmDown_Click`, `BpmUp_Click` | Tempo controls |
| `TapTempo_Click` | Averages the last 5 taps; resets after a `TapResetGap` (2 s) pause |
| `TimeSignatureBox_SelectionChanged` / `BuildBeatDots` | Beats per bar and the dots in `BeatsPanel` (first dot bigger) |
| `ShowBeat(i)` | Lights dot `i` (green accent, purple others); `-1` clears them |
| `TempoName(bpm)` | Grave, Largo, Larghetto, Adagio, Andante, Moderato, Allegro, Presto, Prestissimo |

`BackToMenu_Click` calls `StopMetronome()`.
