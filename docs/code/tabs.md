# Tabs

[← Code guide](README.md)

Writes every note you play as guitar tablature, including which string it's on. Logic is in [Music/Tabs/](../../Music/Tabs/); the screen is in [MainWindow.Tabs.cs](../../Music/MainWindow.Tabs.cs).

There are two detectors, chosen in the **Detector** box: **AI** (default) runs Spotify's Basic Pitch neural network in-process and hears chords and notes over ringing strings; **Classic** is the original one-note-at-a-time YIN pipeline, kept for comparison and as the fallback when the model file is missing.

| File | Contents |
|---|---|
| [Transcription/BasicPitchModel.cs](../../Music/Transcription/BasicPitchModel.cs) | `BasicPitchModel`: runs `Models/basic-pitch.onnx` through ONNX Runtime (2 s of 22.05 kHz audio → 172 frames × 88 keys of note and onset activations) |
| [Tabs/LiveNoteModel.cs](../../Music/Tabs/LiveNoteModel.cs) | `LiveNoteModel`: streams microphone blocks through the model and decodes notes causally; raises `ColumnDetected` with the notes that started together |
| [Transcription/FretboardArranger.cs](../../Music/Transcription/FretboardArranger.cs) | `ArrangeNext`: places each new AI column on the neck (shared with the Songs screen's `Arrange`) |
| [Tabs/NoteTracker.cs](../../Music/Tabs/NoteTracker.cs) | `NoteTracker`: pitch readings → one `DetectedNote` per played note |
| [Tabs/FretAssigner.cs](../../Music/Tabs/FretAssigner.cs) | `FretAssigner`: picks the string/fret; `FretPosition` |
| [Tabs/TabNote.cs](../../Music/Tabs/TabNote.cs) | `TabNote` (a written note) and `TabText` (plain-text tab, string labels/names) |
| [Controls/TabStaff.cs](../../Music/Controls/TabStaff.cs) | `TabStaff`: draws the tab, raises `NoteClicked` |
| [MainWindow.Tabs.cs](../../Music/MainWindow.Tabs.cs) | Tabs screen UI |

## AI pipeline

```
tabListener.SamplesCaptured (audio thread) ─► LiveNoteModel.AddSamples (44.1 → 22.05 kHz, 2 s ring buffer)
  └─ worker thread, every 9 frames (~104 ms): BasicPitchModel.Predict(latest 2 s) ─► stitched note/onset timeline
        └─ decode ─► ColumnDetected ─► Dispatcher ─► AddTabColumn ─► FretboardArranger.ArrangeNext ─► tabNotes ─► TabStaff
```

`PitchUpdated` still runs in AI mode, but only for the level bar.

**Stitching.** The model sees no future at the right edge of its window and no past at the left edge, so only frames 20…155 of each 172-frame window are kept (`LeftEdgeFrames`, `RightEdgeFrames`); a later window overwrites a frame with more context. Inference takes ~20 ms per window, about 20 % of one core.

**Decoding** follows Basic Pitch's `output_to_notes_polyphonic`, made causal:

| Step | Rule | Constant |
|---|---|---|
| Onset | A local peak ≥ 0.5 of the onset output, or of a sharp rise in the note output (Basic Pitch's inferred onsets) | `OnsetThreshold` |
| Confirm | The note output stays ≥ 0.3 for 6 of the next 7 frames (~70 ms). Low strings on small mics build up slowly, so an onset that fails waits up to 14 frames instead of being dropped | `FrameThreshold`, `ConfirmFrames`, `SlowConfirmFrames` |
| Noise | The model normalises its input, so room noise can look like notes; the mic level near the onset must be above the listener's noise gate | `AboveGate` |
| Ringing string | When another string is plucked the model often re-fires a ringing note. A note already sounding 6 frames earlier only counts if its activation rises by 0.15, or, in a strum, if its own partials get 1.6× louder in the audio (re-strummed common tones) | `RepluckRise`, `RepluckEnergyRatio`, `EnergyRise` |
| Same pluck twice | No second onset on the same key within 12 frames, or on a neighbouring key within 5 | `SameKeyRefractoryFrames`, `ColumnFrames` |
| Column | Onsets within 5 frames (~60 ms) form one column | `ColumnFrames` |
| Harmonics | Outside a strum (fewer than 3 fresh notes), any octave/12th/… of a note in the column or played within ~190 ms is dropped. In a strum only a harmonic quieter than 0.55× its root is dropped, so real octaves in chords survive | `StrumNotes`, `StrumShadowRatio`, `RecentShadowFrames` |
| Sympathetic | Outside a strum, a ringing string re-fired by a new note on one of its harmonics is dropped | `FlushColumn` |

Latency from pluck to tab is about 0.35–0.45 s (right-edge margin + confirmation + run interval).

**String and fret.** `FretboardArranger.ArrangeNext(previous, notes, tuning)` is one step of the Songs arranger's Viterbi search: every playable voicing of the column is scored with the same voicing cost (fret height, span, open strings) plus the transition from the column already on the tab (hand movement, string distance). The previous column is read back from `tabNotes`, so a note the player moved by clicking steers the next one. Confidence is the margin to the second-best voicing. A string lock bypasses this: the loudest note playable on that string goes through `AddTabNote` as before.

## Editing by hand and the fingering memory

**Editing.** Clicking a string at a column (`TabStaff.CellClicked`) selects that cell (`tabCell`, outlined in white). While a cell is selected, `HandleTabCellKey` takes the keys before the string shortcuts:

| Key | Effect |
|---|---|
| `0`–`9` | Sets the fret on that string. Two digits within a second make a two-digit fret (`1` `2` = 12). In a single-note column the note moves there and takes the new pitch, which also fixes a wrong note; in a chord the string is set, or a note is added if it was empty (`SetTabCell`) |
| `Delete` / `Backspace` | Removes the note on that string (`DeleteTabCell`) |
| Arrows | Move the selection (Up = the next higher string) |
| `Esc` | Deselects; the next `Esc` goes back to the menu |

Clicking the selected note again still moves it to the next string with the same pitch. Edited notes get `TabNote.Edited`, so nothing automatic changes them afterwards.

**Save tab** (`SaveTab_Click`) stores the tab as the player's answer, under one id per take (`tabTakeId`, new after **Clear**), so saving again replaces it:

- `Desktop\GuitarTabsTest\training\saved-tabs\<take>.txt`: the text tab.
- `fingerings.json`: every column with its strings and frets, for `FingeringMemory`.
- `string-sounds.json`: single notes whose heard pitch still matches (`TabNote.Heard`) become `StringSoundExample`s, so corrected tabs also train the string sound model.

**FingeringMemory.** Each saved column is indexed by its pitches together with the pitches of up to three columns before it (`MaxContext`). For a new AI column, `Find` looks for the longest matching passage and returns a `MemoryMatch`: for each pitch, how often each position was chosen there. `Cost` adds `weight × −ln share` to `ArrangeNext`, with the weight growing with the match length (0.4 for the pitches alone, up to 2.5 with three earlier columns), so one familiar note only nudges, while a familiar passage overrules hand-position habits. Once at least two earlier columns match, `ApplyToRecent` also moves those just-written columns to the saved fingering; notes the player edited are left alone. Notes placed from memory have `Remembered` set, and the detail line says **From your saved tabs**.

## String training

A microphone hears pitch, not strings: open A and low E fret 5 are the same note. What differs is the sound, and that differs per guitar and mic, so it is learned from the player instead of built in.

| File | Contents |
|---|---|
| [Tabs/TrainingPhrases.cs](../../Music/Tabs/TrainingPhrases.cs) | The phrases, generated from the tuning: open strings, the same notes on neighbouring strings and two strings apart, and each string up the neck |
| [Tabs/StringTimbreModel.cs](../../Music/Tabs/StringTimbreModel.cs) | `StringTimbreModel` (nearest-neighbour model of each string's sound) and `StringSoundExample` |
| [MainWindow.Training.cs](../../Music/MainWindow.Training.cs) | The **Train** overlay |

**Flow.** **Train** shows a phrase on a `TabStaff` with the next note highlighted (`HighlightIndex`). While training runs, `AddTabColumn` hands every AI column to `TrainingColumn` instead of the tab. A note that matches the asked pitch (or the one after it, if the detector missed one) is labelled with the asked string/fret, together with `LiveNoteModel.SoundProfile`: harmonics 2–10 relative to the fundamental (searched slightly sharp for wound-string inharmonicity), the average harmonic number, and the decay over 100 ms. Examples are saved after every phrase to `Desktop\GuitarTabsTest\training\string-sounds.json`, with a WAV of the session next to it.

**Model.** For a new note, each candidate string is scored by the mean distance (standardised features) to its 3 closest training notes within ±3 semitones, recorded in the same open-string pitch. Scores become probabilities with a sharpness fitted on the training data (`temperature`, by maximum leave-one-out likelihood).

**Honest weight.** `Retrain` hides each note in turn and asks the model which string it was on (`Accuracy`), against a blind guess between the same strings (`Chance`). `Weight = 2 × (Accuracy − Chance) / (1 − Chance)`, and 0 below 20 testable notes. `StringSoundCost` adds `Weight × −ln p` per position to `ArrangeNext` for single notes (chords blur each other's sound). A model that cannot beat guessing therefore has no effect. The summary after training says which case applies.

## Classic pipeline

```
tabListener (2048-sample window, 25 ms blocks, up to 1350 Hz)
  └─ PitchUpdated (audio thread) ─► NoteTracker.Process ─► DetectedNote?
        └─ Dispatcher.BeginInvoke ─► ShowTabInput ─► AddTabNote ─► FretAssigner.Estimate ─► tabNotes ─► TabStaff
```

`tabListener` is a separate `MicrophoneListener` with a shorter window and blocks than the tuner, so quick notes are separated. It sets `DetectHarmonics`; every reading also gets a harmonic-summing pitch candidate from `PitchDetector.DetectSpectral`.

## NoteTracker: when is it a new note?

`Process(frequency, level, gate, spectralFrequency)` is called once per 25 ms block:

| Situation | Rule | Constant |
|---|---|---|
| **Pluck** (including re-plucking the same note) | Level jumps above the decaying envelope, **or** rises sharply since the last block; the new note may be softer than the ringing one | `AttackRatio` 1.4, `EnvelopeDecay` 0.85, `RiseRatio` 1.25 + `RiseEnvelopeRatio` 1.15, `RepluckFraction` 0.2 |
| Decide the plucked note | Collect up to 4 readings after the attack (8 blocks max), then choose the lowest note that explains them: a reading of the note itself counts 2, a reading of one of its overtones counts 1 | `OnsetReadings` 4, `MaxOnsetBlocks` 8 |
| **New pluck over a ringing string** | YIN often stays on the old, louder period. A consistent harmonic-summing candidate wins when it is not related to the old note, or when it lands on the old note's 3rd/4th harmonic (open B over low E, high e over low E, …). The same candidate can also start a new note without a level jump, so a soft second string is not lost | `DetectSpectral`, `ChooseNote` |
| **Old string keeps ringing** | Every recently sounding note is kept in a small ringing set for four seconds. Its fading readings and overtones are ignored instead of becoming extra notes, even after several string changes | `RingingBlocks` 160 (~4 s), `IsRinging`, `IsOvertoneOfRinging` |
| Duplicate from the attack tail | A note that is still sounding, or is still in the ringing set, needs a strong new attack (`SameNoteAttackRatio` 1.8) before it is written again. Small level wobbles no longer make the tab jump back to the old string | `NoteCooldownBlocks` 4, `SameNoteAttackRatio`, `attackRise` |
| **Hammer-on / slide** (pitch changes, no pluck) | New semitone held for N readings | `LegatoReadings` 3 |
| Note ends | N readings below the noise gate | `SilentReadingsToEnd` 3 |
| Octave-range guard | Readings below/above the tuning's range are folded by whole octaves (A1 → A2 in standard tuning) | `LowestMidi`, `HighestMidi`, `NormalizeToRange` |

## FretAssigner: which string?

A microphone only hears pitch, and the same note exists on several strings (E4 = open e, B5, G9, D14, A19). `Estimate(midi, tuning, time, stringLock, brightness)` returns both a `FretPosition` and a **confidence** value; `Assign(...)` remains a position-only wrapper. It picks the position with the lowest `Cost`:

- The assigner remembers the **last fretted note** (`lastFret`) and the last string. A candidate within `HandSpan` (3) frets costs `FrettedCost` (0.1); beyond that it pays the distance.
- **Open strings** are free while the hand is near the nut (fret ≤ 2); above that they pay `0.15` for every fret above 2, so a low open string is less likely after a high phrase.
- Above fret 12: +0.5. Changing strings: +0.07 per string. Lower frets: +0.01 per fret.
- A **repeated pitch** within two seconds strongly prefers the same position; a fast passage penalises large fret jumps.
- **Timbre** (`HarmonicBrightness`, the average harmonic number from the audio) is a weak tie-breaker: a thinner string or shorter vibrating length tends to be brighter. It is deliberately weighted low because one microphone cannot prove a string from timbre.
- `MoveHandTo` sets the last fretted note; open strings update the last string but leave the hand position alone.
- After an 8 s pause the hand position is cleared, so a new phrase can start anywhere.

`Positions(midi, tuning)` lists every playable position (frets 0–`MaxFret` 22). It's used for the "Also:" line and for click-to-move.

**Confidence:** the gap between the best and second-best cost becomes a 0–1 confidence (`margin`). If it is below 0.5 the tab note gets a dotted amber outline and the detail line says **Uncertain**, with the alternatives. This is the honest limit of audio-only estimation; a string lock or a click on the note overrides it.

`Estimate(midi, tuning, time, stringLock)` also takes an optional string. When the player picks a string in the **Play on** box, clicks one of the quick **6 E … 1 e** buttons, or presses **6–1** on the keyboard, `stringLock` is set and that string is used whenever the pitch fits between frets 0 and `MaxFret`; notes outside its range are ignored. Press **0** (or click **Auto**) to return to guessing. This is the fix for the microphone's inherent ambiguity: audio only gives the pitch, so A2 could be open A or low E fret 5.

**Click to fix:** clicking a note (`MoveTabNoteToNextString`) moves it to the next position in `Positions`. If it's the latest note, `MoveHandTo` is called, so the following notes are placed around the new hand position.

## Tablature

- `TabNote(Midi, Position, GapBefore)` plus init-only `Confidence` and `ChordWithPrevious`. A chord is several consecutive `TabNote`s, every one after the first with `ChordWithPrevious = true`; `TabText.Columns` groups them, `TabStaff` draws them in one slot, **Undo** removes the whole column, and the string lock leaves chords alone. `GapBefore` is set when more than `TabGap` (1.5 s) passed since the previous note; it's shown as extra space. A confidence below 0.5 is drawn with a dotted amber outline.
- **`TabStaff`** (control): rows of 6 lines (highest string on top), 30 px per note, wraps to new rows and grows taller inside `TabScroll`. Fret numbers sit on their string over a background patch; the latest note uses `HighlightBrush` (amber), uncertain notes get a dotted amber border. Hit-testing per note drives `NoteClicked`. Call `Refresh()` after changing `Notes`/`Tuning`.
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
- **One note at a time.** Chords are not transcribed. A new pluck now wins over a still-ringing previous string, including the common overtone cases (open B or high e over low E), but very loud ringing and overlapping harmonics can still confuse the detector. Mute the previous string for the cleanest result, and lock the string when exact frets matter.
- With another string ringing, the old version could fail completely (open D under a ringing A) or treat the new note as an overtone. The harmonic fallback and two-second `priorNote` suppression address the common cases; the guided test's previously missed high e is now tracked.
- **Laptop microphones** with Windows noise suppression / "audio enhancements" fade sustained notes after about 100 ms, which can drop notes. Turning that off for the mic helps.
- Notes are equally spaced: no rhythm.
