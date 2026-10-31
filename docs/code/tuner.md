# Tuner

[← Code guide](README.md)

The tuner screen (`TunerView`). Logic is in [MainWindow.xaml.cs](../../AllTunning/MainWindow.xaml.cs); pitch detection is in the [audio layer](audio.md).

## Controls (MainWindow.xaml)

| Element | Purpose | Handler |
|---|---|---|
| `TuningBox` | Tuning dropdown | `TuningBox_SelectionChanged` |
| `DeviceBox` | Microphone (shared with `ChordDeviceBox`) | `DeviceBox_SelectionChanged` |
| `A4Text` + −/+ | Reference pitch 415–466 Hz | `A4Down_Click`, `A4Up_Click`, `SetA4` |
| `AutoBox` | Auto-detect string on/off | `AutoBox_Changed` |
| `SensitivitySlider` | Noise gate strength | `SensitivitySlider_ValueChanged` |
| `GaugeCanvas`, `Needle`, `NeedleRotate` | Cents gauge | `DrawGauge`, `MoveNeedle` |
| `NoteText`, `HintText`, `CentsText`, `FrequencyText` | Readout | `OnPitch`, `ShowIdle` |
| `StringsPanel` | String buttons, created in code | `BuildStringButtons`, `SelectString` |
| `LevelBar` | Input level (blue = analysed, grey = noise) | `OnPitch` |

## From mic to needle: `OnPitch`

Every 25 ms (`TunerBlockMilliseconds`) `MicrophoneListener.PitchUpdated` → `Dispatcher.BeginInvoke` → `OnPitch(reading)`:

1. **Level bar** colour from `reading.Level >= reading.Gate`.
2. **`IsStable`**: a pitch must stay within `StableCents` (40) for `StableReadingsRequired` (3) readings, about 75 ms. This rejects background noise.
3. If there's no stable pitch: after `NewNoteGap` (0.25 s) clear the smoothing history and the auto-string lock; after `HoldTime` (1.5 s) call `ShowIdle()`.
4. **Smoothing**: median of the last 3 frequencies (`recentFrequencies`, `Median`) to drop single bad readings, then each reading moves `smoothedFrequency` `PitchSmoothing` (35%) of the way to the median. A jump larger than `NewPitchCents` (50) snaps straight to the new pitch.
5. **Target string**: `manualString`, or `AutoDetectString(smoothed)` in auto mode.
6. **Cents** = `NoteMath.Cents(smoothed, target frequency)`.
7. Update the texts, colours (`InTuneBrush` within ±`InTuneCents`, and it stays green until ±`InTuneCents` + `InTuneHysteresisCents`; otherwise `OffBrush`), "Tune up ↑ / Tune down ↓ / In tune ✓", `MoveNeedle`, `HighlightString`.

## String selection

| Member | Behaviour |
|---|---|
| `NearestString(f)` | String with the smallest absolute cents distance |
| `AutoDetectString(f)` | Nearest string, but **locked for the whole note** (`autoString`). Switches only after `StringSwitchReadings` (10) readings in a row point to another string |
| `SelectString(i)` | String button click: manual mode (`manualString`), unticks `AutoBox`, plays the reference tone via `TonePlayer` |
| `HighlightString(i)` | Colours the active button (`-1` = none) |

## Gauge

- `DrawGauge()` runs once at startup: ticks every 5 cents from −50 to +50, labels, and the green ±`InTuneCents` arc.
- `GaugePoint(cents, radius)` maps cents to canvas coordinates: ±`GaugeRangeCents` (50) ↔ ±`GaugeSweepDegrees` (60°), centre (`GaugeCenterX`, `GaugeCenterY`).
- `MoveNeedle(cents)` sets the needle's target; `AnimateNeedle` (on `CompositionTarget.Rendering`, every frame) glides it there with a `NeedleTimeConstantSeconds` (60 ms) time constant.

## Microphone lifecycle

- `StartTuning_Click` → `ShowIdle()` + `StartListening()` (opens `DeviceBox.SelectedIndex`).
- `DeviceBox_SelectionChanged` restarts the mic only while the tuner is visible, and copies the choice to `ChordDeviceBox`.
- `BackToMenu_Click` stops it.

## Tunable constants

| Constant | Default | Effect |
|---|---|---|
| `InTuneCents` | 5 | Width of the "in tune" zone |
| `StableReadingsRequired` / `StableCents` | 3 / 40 | How steady a pitch must be before it's shown |
| `PitchSmoothing` / `NewPitchCents` | 0.35 / 50 | How quickly the reading follows the string, and when it snaps |
| `NeedleTimeConstantSeconds` | 0.06 | How quickly the needle glides |
| `InTuneHysteresisCents` | 2 | Stops the in-tune colour flickering at the edge |
| `StringSwitchReadings` | 10 | How long another string must be heard before auto mode switches |
| `HoldTime` | 1.5 s | How long the last reading stays after silence |
| `NewNoteGap` | 0.25 s | Silence that counts as a new note |
