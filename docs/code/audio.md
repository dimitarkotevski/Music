# Audio layer

[← Code guide](README.md)

Shared audio code in [Music/Audio/](../../Music/Audio/). Uses [NAudio](https://github.com/naudio/NAudio) (WinMM `WaveInEvent` / `WaveOutEvent`).

| File | Class | Job |
|---|---|---|
| [MicrophoneListener.cs](../../Music/Audio/MicrophoneListener.cs) | `MicrophoneListener` | Records the mic, applies the noise gate, raises events |
| [PitchDetector.cs](../../Music/Audio/PitchDetector.cs) | `PitchDetector` | Finds the pitch of a single note (YIN) |
| [TonePlayer.cs](../../Music/Audio/TonePlayer.cs) | `TonePlayer` | Plays the tuner's reference tones |
| [Metronome.cs](../../Music/Audio/Metronome.cs) | `Metronome` | Click engine, see [Metronome](metronome.md) |

## MicrophoneListener

Captures **44.1 kHz, 16-bit mono** audio in **50 ms** blocks (`SampleRate`, `BufferMilliseconds`).

| Member | What it does |
|---|---|
| `GetDevices()` | Names of all input devices (for the microphone dropdowns) |
| `Start(deviceNumber)` / `Stop()` | Open/close the mic. `Start` also resets the window and the noise floor |
| `PitchUpdated` event | `PitchReading(Frequency?, Level, Gate)` per block. **Pitch detection only runs if something is subscribed** |
| `SamplesCaptured` event | `AudioBlock(Samples, Level, Gate)` per block with the new samples; `HasSignal` = above the gate |
| `GateRatio` | How much louder than the room a sound must be (set by the tuner's Sensitivity slider) |
| `OnDataAvailable` | Per-block work: slide the 4096-sample window, compute RMS level, update the noise floor, raise events |

**Noise gate** (in `OnDataAvailable`):
- `noiseFloor` follows the room level: it drops fast in quiet moments (rate 0.3) and rises slowly (0.003), so a ringing string isn't mistaken for noise.
- `gate = max(MinimumGate, noiseFloor × GateRatio)`. Below the gate, no pitch is detected.

Two instances exist: `listener` for the tuner (subscribes `PitchUpdated`) and `chordListener` for chords (subscribes `SamplesCaptured`, so it skips YIN).

## PitchDetector

The [YIN algorithm](http://audition.ens.fr/adc/pdf/2002_JASA_YIN.pdf) on a 4096-sample window. Range 50–1000 Hz (constructor defaults).

`Detect(buffer)` steps:
1. **Difference function + cumulative mean normalisation** into the `yin` array.
2. **Absolute threshold**: the first dip below `Threshold` (0.1; lower = stricter), walked down to its local minimum.
3. **`CorrectOctaveError`**: if 2× or 3× that period is much more periodic (`OctaveCheckRatio` 0.5) and the chosen dip isn't already clean (`OctaveCheckFloor` 0.02), use the longer period. This fixes strings read an octave too high when the 2nd/3rd harmonic is strong, e.g. the A string showing as G.
4. **Parabolic interpolation** for sub-sample (sub-cent) accuracy.

Returns `null` when nothing is periodic enough.

| Tweak | Change |
|---|---|
| Detect quieter or less clean notes | Raise `Threshold` (more noise gets through) |
| Fewer octave-down mistakes | Lower `OctaveCheckRatio` or raise `OctaveCheckFloor` |
| Lower/higher notes | `minFrequency` / `maxFrequency` constructor arguments |

## TonePlayer

`Play(frequency)` plays a 2.5 s tone through `WaveOutEvent`; `Stop()` cuts it off. The sound comes from the private `PluckedTone` sample provider: 6 harmonics with amplitude 1/h, each decaying faster the higher it is, plus a 5 ms attack. It's additive synthesis rather than Karplus-Strong, so the pitch is exact.

Used by the tuner's string buttons (`SelectString` in MainWindow.xaml.cs).
