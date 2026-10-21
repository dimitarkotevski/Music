# Music theory helpers

[← Code guide](README.md)

Small, UI-free helpers in [Music/Tuning/](../../Music/Tuning/), used by the tuner and the chord detector.

## NoteMath

[NoteMath.cs](../../Music/Tuning/NoteMath.cs). Notes are **MIDI numbers** (A4 = 69, middle C = 60, low E string = 40). The A4 reference (usually 440 Hz) is passed in so the tuner's A4 setting applies everywhere.

| Method | Formula / behaviour |
|---|---|
| `MidiToFrequency(midi, a4)` | `a4 × 2^((midi − 69) / 12)` |
| `FrequencyToMidi(f, a4)` | `69 + 12 × log2(f / a4)` (fractional) |
| `Cents(f, target)` | `1200 × log2(f / target)`; negative = flat |
| `Name(midi)` | Note name with sharps (`C`, `C#` … `B`); also works for pitch classes 0–11 |
| `Octave(midi)` | Scientific octave number (`midi / 12 − 1`) |
| `Parse("E2")` | Note text → MIDI. Accepts `#` and `b`: `"F#3"`, `"Eb2"` |

## GuitarTuning

[GuitarTuning.cs](../../Music/Tuning/GuitarTuning.cs). `Name`, `Strings` (6 MIDI notes, **lowest string first**), and `DisplayName` for the dropdown, e.g. "Standard  (E A D G B E)".

`GuitarTuning.All`, in dropdown order:

| Tuning | Notes |
|---|---|
| Standard | E2 A2 D3 G3 B3 E4 |
| Drop D | D2 A2 D3 G3 B3 E4 |
| Half Step Down | Eb2 Ab2 Db3 Gb3 Bb3 Eb4 |
| Full Step Down | D2 G2 C3 F3 A3 D4 |
| C Standard | C2 F2 Bb2 Eb3 G3 C4 |
| Drop C | C2 G2 C3 F3 A3 D4 |
| Drop B | B1 F#2 B2 E3 G#3 C#4 |
| Drop A | A1 E2 A2 D3 F#3 B3 |
| Open G | D2 G2 D3 G3 B3 D4 |
| Open D | D2 A2 D3 F#3 A3 D4 |
| Open E | E2 B2 E3 G#3 B3 E4 |
| Open A | E2 A2 E3 A3 C#4 E4 |
| Open C | C2 G2 C3 G3 C4 E4 |
| DADGAD | D2 A2 D3 G3 A3 D4 |

**To add a tuning**, add one line to the list:

```csharp
Create("Open D Minor", "D2 A2 D3 F3 A3 D4"),
```

The pitch detector goes down to 50 Hz, so tunings as low as about G1 (49 Hz) won't be detected.
