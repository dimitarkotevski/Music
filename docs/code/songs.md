# Songs

[← Code guide](README.md)

The Songs screen turns a complete audio file into tablature. It is deliberately separate from the live **Tabs** screen: Tabs is monophonic and follows the microphone in real time, while Songs can contain chords, sustained notes and a full arrangement.

## Pipeline

```
audio file
  └─ BasicPitchTranscriber (C#)
        └─ tools/basic_pitch_worker.py (Python, Basic Pitch + ONNX)
              └─ JSON note events: start, end, MIDI pitch, amplitude
                    └─ FretboardArranger (C#)
                          ├─ merge Basic Pitch's sustained fragments
                          ├─ group notes that start together (60 ms window)
                          ├─ generate playable string/fret voicings
                          └─ Viterbi/beam search over onset columns
                                └─ SongTabStaff / SongTabText / MidiExporter / MusicXmlExporter
```

Audio → notes and notes → string/fret are separate layers. The Python worker only reports pitches and timing; it never decides which string was used. That decision is made by `FretboardArranger` using the selected guitar tuning.

## Files

| File | Contents |
|---|---|
| [Transcription/BasicPitchTranscriber.cs](../../Music/Transcription/BasicPitchTranscriber.cs) | `TranscriptionResult`, `TranscribedNote`, `BasicPitchTranscriber`, `IFileTranscriber`; starts the Python worker and reads its JSON |
| [Transcription/FretboardArranger.cs](../../Music/Transcription/FretboardArranger.cs) | `FretboardArranger`, `ArrangementResult`, `SongTabColumn`, `ArrangedNote`; polyphonic string/fret assignment |
| [Transcription/SongTabText.cs](../../Music/Transcription/SongTabText.cs) | Plain-text tab export with multiple notes per column |
| [Transcription/MidiExporter.cs](../../Music/Transcription/MidiExporter.cs) | Standard MIDI File export using NAudio.Midi |
| [Transcription/MusicXmlExporter.cs](../../Music/Transcription/MusicXmlExporter.cs) | MusicXML tab score export |
| [Controls/SongTabStaff.cs](../../Music/Controls/SongTabStaff.cs) | Read-only tab control; draws chord columns, timing labels and dotted uncertain notes |
| [MainWindow.Songs.cs](../../Music/MainWindow.Songs.cs) | Songs screen: file picker, transcription, tuning, cancel, copy/export |
| [tools/basic_pitch_worker.py](../../Music/tools/basic_pitch_worker.py) | Python worker: audio → JSON note events |
| [tools/setup-transcription.ps1](../../Music/tools/setup-transcription.ps1) | One-time local Python environment setup |
| [tools/requirements-transcription.txt](../../Music/tools/requirements-transcription.txt) | Python dependencies |

## Python environment

The model is intentionally isolated in a subprocess so a future ONNX or other backend can replace it without touching the tab logic. The app searches for Python in this order:

1. `BasicPitchTranscriber.PythonPath` (used by tests),
2. the `MUSIC_PYTHON` environment variable,
3. `.venv-transcription\Scripts\python.exe` in the app directory or one of its parents,
4. `python` on `PATH`.

Run once from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File Music\tools\setup-transcription.ps1
```

The script creates `Music\Music\.venv-transcription` (the project folder) and installs the ONNX runtime without pulling TensorFlow. Basic Pitch is installed with `--no-deps` on purpose; its other dependencies are pinned in `requirements-transcription.txt`.

The worker is copied to `bin\…\tools\basic_pitch_worker.py` by the project file. The C# side uses `Process` with redirected stdout/stderr and passes arguments through `ArgumentList`, so paths with spaces are safe. Cancellation kills the whole process tree.

## Note post-processing

`FretboardArranger.Arrange`:

1. Keeps notes in the selected tuning's range and longer than 45 ms; notes with amplitude ≤ 0.005 are ignored.
2. Merges same-pitch events whose gap is at most 60 ms. Basic Pitch often splits one ringing string into several adjacent events; without this step the tab repeats the same note many times.
3. Groups remaining notes whose starts are within 60 ms into one tab column.
4. In **Solo** mode, keeps only the strongest note in each column; in **Chords** mode, removes duplicate pitches, caps a column at six notes, and removes weak octave/12th shadows when a column has more than three notes.
5. Generates every combination of one string per pitch with no repeated string, then rejects voicings with a fretted hand span above five frets or too many fretted notes.

## Viterbi/beam search

Each candidate voicing has an intrinsic cost:

- lower frets and smaller hand span,
- fewer fretted notes and a smaller string spread,
- open strings are slightly preferred,
- high positions above fret 12 cost more.

Transitions add:

- hand movement between the previous and current minimum fret,
- average distance from each new note to the nearest previously used string,
- a fast-passage penalty when the gap is below 180 ms,
- an open-string penalty when the hand is already high on the neck,
- reduced cost after a pause longer than one second.

The search keeps the best 24 paths (`BeamWidth`) and at most 240 voicings per column. The final path is the arrangement. Per-column confidence is the margin between the chosen voicing and the best alternative given the previous chosen position: an uncertain column gets a dotted amber outline.

## Screen

- **Browse…** accepts WAV, MP3, FLAC, OGG, AIFF and M4A. Actual codec support comes from the installed Python audio libraries; WAV is the safest input.
- **Tuning** re-arranges already-transcribed notes without re-running the model.
- **Solo** keeps only the strongest note at each onset. Leave it off for chords; turn it on for a single-line guitar part so ringing overtones are less likely to become extra columns.
- **Transcribe** is asynchronous and can be cancelled; the Python process is killed on cancel.
- **Copy tab** writes the text export to the clipboard. **MIDI** writes a type-1 MIDI file at 120 BPM with an acoustic-guitar program change and the original note timing. **MusicXML** writes a tab score (quarter-note columns) for notation software.
- **Click a note** to move it to the next playable string; **right-click** removes a wrong note. A manual edit clears that column's uncertain marking.
- **Clear** removes the current arrangement but keeps the selected file.

## Limits

- Basic Pitch transcribes **notes, not strings**. The arranger is a best guess; different positions for the same pitch are often equally valid.
- Dense mixes, distortion, vocals and drums confuse the note model. A clean solo-guitar recording works much better. Source separation is not part of this version.
- Chords are supported as onset columns, but the model may report ringing overtones as extra voices. The weak-octave filter handles common cases; difficult recordings still need manual editing.
- Timing is preserved for MIDI export. The MusicXML export uses one quarter-note column per onset, so it is for notation/import rather than rhythmic accuracy.
- Basic Pitch runs on CPU; a four-minute song can take minutes. The screen shows an indeterminate progress bar, not a percentage.
