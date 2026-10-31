# Audio layer

[← Code guide](README.md)

Shared audio code in [AllTunning/Audio/](../../AllTunning/Audio/). Uses [NAudio](https://github.com/naudio/NAudio) (WinMM `WaveInEvent` / `WaveOutEvent`).

| File | Class | Job |
|---|---|---|
| [MicrophoneListener.cs](../../AllTunning/Audio/MicrophoneListener.cs) | `MicrophoneListener` | Records the mic, applies the noise gate, raises events |
| [PitchDetector.cs](../../AllTunning/Audio/PitchDetector.cs) | `PitchDetector` | Finds the pitch of a single note (YIN) |
| [TonePlayer.cs](../../AllTunning/Audio/TonePlayer.cs) | `TonePlayer` | Plays the tuner's reference tones |
| [Metronome.cs](../../AllTunning/Audio/Metronome.cs) | `Metronome` | Click engine, see [Metronome](metronome.md) |

## MicrophoneListener

Captures **44.1 kHz, 16-bit mono** audio. The constructor `MicrophoneListener(windowSize = 4096, bufferMilliseconds = 50, maxFrequency = 1000)` sets the pitch window, block length and highest detected pitch. The tuner and chords use the defaults; tabs use `(2048, 25, 1350)` to react faster and reach high frets.

| Member | What it does |
|---|---|
| `GetDevices()` | Names of all input devices (for the microphone dropdowns) |
| `Start(deviceNumber)` / `Stop()` | Open/close the mic. `Start` also resets the window and the noise floor |
| `IsRunning` | True after the mic opened; used by the guided test to fail early |
| `StartRecording(path)` / `StopRecording()` | Write the captured blocks to a 44.1 kHz, 16-bit mono WAV (guided test) |
| `PitchUpdated` event | `PitchReading(Frequency?, Level, Gate)` per block. **Pitch detection only runs if something is subscribed** |
| `SamplesCaptured` event | `AudioBlock(Samples, Level, Gate)` per block with the new samples; `HasSignal` = above the gate |
| `GateRatio` | How much louder than the room a sound must be (set by the tuner's Sensitivity slider) |
| `MusicPresent` | Set when the block contains music: automatically when a pitch is found, or by a `SamplesCaptured` handler during the callback (the chord screen sets it when a chord is recognised) |
| `OnDataAvailable` | Per-block work: slide the window, compute RMS level, update the noise floor, raise events |

**Noise gate** (in `OnDataAvailable`):
- `noiseFloor` follows the room level. It drops fast in quiet moments and rises slowly, **and never rises while `MusicPresent` is set**. Otherwise, continuous playing with no quiet moments would push the gate above the notes after a few seconds.
- The rates (`noiseFloorFallRate`, `noiseFloorRiseRate`) are scaled by the block length, so they behave the same in real time for 25 ms and 50 ms blocks.
- `gate = max(MinimumGate, noiseFloor × GateRatio)`. Below the gate, no pitch is detected.

Three instances exist: `listener` (tuner, `PitchUpdated`), `chordListener` (chords, `SamplesCaptured`, so it skips YIN) and `tabListener` (tabs, `PitchUpdated` with the faster settings).

## PitchDetector

The [YIN algorithm](http://audition.ens.fr/adc/pdf/2002_JASA_YIN.pdf) on a 4096-sample window. Range 50–1000 Hz (constructor defaults).

`Detect(buffer)` steps:
1. **Difference function + cumulative mean normalisation** into the `yin` array.
2. **Absolute threshold**: the first dip below `Threshold` (0.1; lower = stricter), walked down to its local minimum.
3. **`CorrectOctaveError`**: if 2× or 3× that period is much more periodic (`OctaveCheckRatio` 0.5) and the chosen dip isn't already clean (`OctaveCheckFloor` 0.02), use the longer period. This fixes strings read an octave too high when the 2nd/3rd harmonic is strong, e.g. the A string showing as G.
4. **Parabolic interpolation** for sub-sample (sub-cent) accuracy.
5. **`CorrectSubharmonic`**: if the detected frequency has almost no energy of its own (`SubharmonicEnergyRatio` 0.15) but 2× or 3× it does, report the stronger partial. Below 80 Hz both 2× and 3× are checked (A1 → A2, E1 → E2); above 80 Hz only 3× is checked, so a quiet low E is not pulled up an octave. This fixes YIN locking onto a multiple of the period (a B string read as E2, or the A string read as A1).

Returns `null` when nothing is periodic enough.

| Tweak | Change |
|---|---|
| Detect quieter or less clean notes | Raise `Threshold` (more noise gets through) |
| Fewer octave-down mistakes | Lower `OctaveCheckRatio` or raise `OctaveCheckFloor` |
| Fewer subharmonic mistakes (A1/B read as low E) | `SubharmonicEnergyRatio`, or the 80 Hz split in `CorrectSubharmonic` |
| Lower/higher notes | `minFrequency` / `maxFrequency` constructor arguments |

## TonePlayer

`Play(frequency)` plays a 2.5 s tone through `WaveOutEvent`; `Stop()` cuts it off. The sound comes from the private `PluckedTone` sample provider: 6 harmonics with amplitude 1/h, each decaying faster the higher it is, plus a 5 ms attack. It's additive synthesis rather than Karplus-Strong, so the pitch is exact.

Used by the tuner's string buttons (`SelectString` in MainWindow.xaml.cs).
