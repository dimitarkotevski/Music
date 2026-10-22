# Tabs

[← Code guide](README.md)

Writes every note you play as guitar tablature, including which string it's on. Logic is in [Music/Tabs/](../../Music/Tabs/); the screen is in [MainWindow.Tabs.cs](../../Music/MainWindow.Tabs.cs).

| File | Contents |
|---|---|
| [Tabs/NoteTracker.cs](../../Music/Tabs/NoteTracker.cs) | `NoteTracker`: pitch readings → one `DetectedNote` per played note |
| [Tabs/FretAssigner.cs](../../Music/Tabs/FretAssigner.cs) | `FretAssigner`: picks the string/fret; `FretPosition` |
| [Tabs/TabNote.cs](../../Music/Tabs/TabNote.cs) | `TabNote` (a written note) and `TabText` (plain-text tab, string labels/names) |
| [Controls/TabStaff.cs](../../Music/Controls/TabStaff.cs) | `TabStaff`: draws the tab, raises `NoteClicked` |
| [MainWindow.Tabs.cs](../../Music/MainWindow.Tabs.cs) | Tabs screen UI |

## Pipeline

```
tabListener (2048-sample window, 25 ms blocks, up to 1350 Hz)
  └─ PitchUpdated (audio thread) ─► NoteTracker.Process ─► DetectedNote?
        └─ Dispatcher.BeginInvoke ─► ShowTabInput ─► AddTabNote ─► FretAssigner.Assign ─► tabNotes ─► TabStaff
```

`tabListener` is a separate `MicrophoneListener` with a shorter window and blocks than the tuner, so quick notes are separated.

## NoteTracker: when is it a new note?

`Process(frequency, level, gate)` is called once per 25 ms block:

| Situation | Rule | Constant |
|---|---|---|
| **Pluck** (including re-plucking the same note) | Level jumps above the decaying envelope, **or** rises sharply since the last block | `AttackRatio` 1.4, `EnvelopeDecay` 0.85, `RiseRatio` 1.25 + `RiseEnvelopeRatio` 1.15 |
| Decide the plucked note | Collect up to 4 readings after the attack (8 blocks max), then choose the lowest note that explains them: a reading of the note itself counts 2, a reading of one of its overtones counts 1 | `OnsetReadings` 4, `MaxOnsetBlocks` 8 |
| **Hammer-on / slide** (pitch changes, no pluck) | New semitone held for N readings | `LegatoReadings` 3 |
| Note ends | N readings below the noise gate | `SilentReadingsToEnd` 3 |
| Octave-range guard | Readings below/above the tuning's range are folded by whole octaves (A1 → A2 in standard tuning) | `LowestMidi`, `HighestMidi`, `NormalizeToRange` |

## FretAssigner: which string?

A microphone only hears pitch, and the same note exists on several strings (E4 = open e, B5, G9, D14, A19). `Assign(midi, tuning, time)` picks the position with the lowest `Cost`:

- The hand covers a **4-fret window** (`handFret` … `handFret + HandSpan`). Frets inside it cost `FrettedCost` (0.1); outside, the distance to the window.
- **Open strings** are free while the hand is near the nut (fret ≤ 2), otherwise `0.15 × handFret`. Players avoid open strings high up the neck.
- Above fret 12: +0.5. Tie-breakers: stay near the previous string, then lower frets.
- After choosing, `MoveHandTo` shifts the window just enough to include the new fret.
- After a `ResetAfter` (4 s) pause the hand goes back to open position.

`Assign(midi, tuning, time, stringLock)` also takes an optional string. When the player picks a string in the **Play on** box, clicks one of the quick **6 E … 1 e** buttons, or presses **6–1** on the keyboard, `stringLock` is set and that string is used whenever the pitch fits between frets 0 and `MaxFret`; notes outside its range are ignored. Press **0** (or click **Auto**) to return to guessing. This is the fix for the microphone's inherent ambiguity: audio only gives the pitch, so A2 could be open A or low E fret 5.

`Positions(midi, tuning)` lists every playable position (frets 0–`MaxFret` 22). It's used for the "Also:" line and for click-to-move.

**Click to fix:** clicking a note (`MoveTabNoteToNextString`) moves it to the next position in `Positions`. If it's the latest note, `MoveHandTo` is called, so the following notes are placed around the new hand position.

## Tablature

- `TabNote(Midi, Position, GapBefore)`. `GapBefore` is set when more than `TabGap` (1.5 s) passed since the previous note; it's shown as extra space.
- **`TabStaff`** (control): rows of 6 lines (highest string on top), 30 px per note, wraps to new rows and grows taller inside `TabScroll`. Fret numbers sit on their string over a background patch; the latest note uses `HighlightBrush` (amber). Hit-testing per note drives `NoteClicked`. Call `Refresh()` after changing `Notes`/`Tuning`.
- **`TabText.Format`**: the **Copy tab** text, 16 notes per row, e.g.

  ```
  e |-------------0--2--4--5-|
  B |----0--2--3-------------|
  G |-2----------------------|
  ```
- `TabText.StringLabel` gives `e B G D A E` (lowercase top string when it repeats the bottom one); `StringName` gives "String 1 (e)". Strings are numbered from the highest, like guitarists do.

## Screen (`TabView`)

| Member | Purpose |
|---|---|
| `InitializeTabs` | Subscribes `tabListener.PitchUpdated` and `TabStaffView.NoteClicked`, fills the tuning/mic boxes and builds the quick string buttons |
| `StartTabs_Click` / `StartTabListening` | Shows the screen, copies `a4` and the tuner's `GateRatio`, opens the mic |
| `ShowTabInput` | Level bar; adds a note when the tracker reports one |
| `AddTabNote` | Assigns string/fret in the current `tuning`, sets `GapBefore`, appends, scrolls |
| `RefreshTabStringChoices` / `BuildTabStringButtons` / `TabStringBox_SelectionChanged` | Build the Auto + per-string list and the quick buttons; a chosen string is applied to new notes and to notes already in the tab |
| `TabStringButton_Click` / `Window_PreviewKeyDown` | Quick string buttons and the **6–1** / **0** shortcuts; the chosen button turns amber |
| `RefreshTab` | Redraws the tab and the current-note card (`TabNoteText`, `TabStringText`, `TabFretText`, `TabAlternativesText`) |
| `UndoTab_Click`, `ClearTab_Click`, `CopyTab_Click` | Remove last note / clear (also resets the hand) / copy `TabText.Format` to the clipboard |
| `TabTuningBox_SelectionChanged`, `TabDeviceBox_SelectionChanged` | Kept in sync with the tuner's `TuningBox` / `DeviceBox` (the master copies) |
| `StartTabTest_Click` | Starts the guided test: clears the tab, builds the note sequence, opens a WAV and overlays the prompt |
| `ShowTabTestStep` | Big note name, string/fret instruction, and a one-note `TabTestStaff` diagram for the target |
| `NextTabTest_Click` / `FinishTabTest` | Closes the current timing window, shows the next note, or stops the WAV and writes the log |
| `CancelTabTest_Click` / `StopTabTest` | Discard a canceled take / stop when leaving Tabs or closing the window |

## Guided test

`Guided test` is a diagnostic mode: instead of guessing what was played, the app **tells you what to play** and records it. The sequence is six open strings (low to high), then low E frets 1–5, 7, 9 and 12. Each step shows:

- the note name (e.g. `E2`),
- `String 6 (E) — open string` or `fret N`,
- the same note drawn on a one-note `TabStaff`, with the fret number on the right line.

The note stays until you click **Next**. There is no timer, so you can take your time. `MicrophoneListener.StartRecording(path)` writes 44.1 kHz, 16-bit mono WAV; `StopRecording()` finalises it. `FinishTabTest` writes a tab-separated log with the actual `Start(s)`, `End(s)`, note, string and fret for every step. Both files go to `Desktop\GuitarTabsTest`, named `guided-YYYYMMDD-HHmmss.wav/.txt`, and are offered via **Open folder**.

## Tested behaviour and limits

Simulated plucks through the same pipeline: open strings, repeated notes, hammer-ons, scales at 200 ms and 125 ms per note and low riffs were all transcribed exactly. 64 notes non-stop came through at about 95% (misses were quiet repeated notes).

- **The string is inferred**, not heard. Fix it by clicking the note, or lock it with the **Play on** box, the quick **6 E … 1 e** buttons, or keys **6–1** (**0** = Auto).
- **One note at a time.** Chords, or notes left ringing across strings, confuse single-pitch detection. Mute the previous string (in the test, the let-ring scale failed). With another string ringing, the detector can fail completely (open D under a ringing A), report a difference tone (low E under B + high e), or treat the new note as an overtone (B is the 12th above E and arrives late).
- **Laptop microphones** with Windows noise suppression / "audio enhancements" fade sustained notes after about 100 ms, which can drop notes. Turning that off for the mic helps.
- Notes are equally spaced: no rhythm.
