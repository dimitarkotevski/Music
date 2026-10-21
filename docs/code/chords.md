# Chords

[← Code guide](README.md)

Chord recognition. Analysis is in [Music/Chords/](../../Music/Chords/); the screen is in [MainWindow.Chords.cs](../../Music/MainWindow.Chords.cs).

| File | Contents |
|---|---|
| [Chord.cs](../../Music/Chords/Chord.cs) | `ChordQuality` (chord types) and `Chord` (a recognised chord) |
| [ChordDetector.cs](../../Music/Chords/ChordDetector.cs) | `ChordDetector` and its result `ChordAnalysis` |
| [ChordShape.cs](../../Music/Chords/ChordShape.cs) | `ChordShape` (a fingering) and `ChordShapes.Find` (shape library) |
| [Controls/ChordDiagram.cs](../../Music/Controls/ChordDiagram.cs) | `ChordDiagram`, the chord box drawing |
| [MainWindow.Chords.cs](../../Music/MainWindow.Chords.cs) | Chord screen UI |

## Chord types: `ChordQuality.All`

| Suffix | Name | Intervals (semitones) | Prior |
|---|---|---|---|
| *(none)* | major | 0 4 7 | 0 |
| `m` | minor | 0 3 7 | 0 |
| `5` | power chord | 0 7 | −0.02 |
| `7` | dominant seventh | 0 4 7 10 | −0.01 |
| `maj7` | major seventh | 0 4 7 11 | −0.01 |
| `m7` | minor seventh | 0 3 7 10 | −0.01 |
| `sus2` | suspended second | 0 2 7 | −0.02 |
| `sus4` | suspended fourth | 0 5 7 | −0.02 |
| `dim` | diminished | 0 3 6 | −0.03 |
| `aug` | augmented | 0 4 8 | −0.03 |

**Prior** is a small score penalty so simple chords win ties. **To add a chord type**, add a line here, e.g. `new("9", "dominant ninth", [0, 4, 7, 10, 2], -0.02)`. Avoid types with the same notes as an existing one under another root (C6 = Am7), or they'll compete.

`Chord` gives you `Name` ("Am7", "C/E"), `FullName` ("A minor seventh"), `PitchClasses` and `NoteNames`. `Bass` is set only for inversions (slash chords).

## How detection works: `ChordDetector.Process`

Called every 50 ms with the new samples, **on the audio thread**.

1. **`Append`**: slide a 16384-sample window (`FftSize`, about 0.37 s, about 2.7 Hz resolution).
2. If below the noise gate: fade the chroma, clear the chord, return.
3. **`ComputeChroma`**:
   - Hann window + FFT (`NAudio.Dsp.FastFourierTransform`).
   - Find spectral peaks between `MinFrequency`–`MaxFrequency` (70–1400 Hz) above `PeakFloor` (5% of the loudest), with parabolic interpolation.
   - Drop peaks more than `MaxCentsOff` (35 cents) from a semitone.
   - Add √magnitude to that peak's pitch class, ×`OvertoneWeight` (0.35) if **`IsOvertone`**: a 3rd, 5th, 6th or 7th harmonic of a lower peak, unless the same note also appears an octave or two lower.
   - **`FindBass`**: score every note from D2 to B3 (`LowestBassMidi`, `BassCandidateCount`) by its harmonic series. A candidate needs some energy at its own fundamental, which rules out phantom octaves below. Pick the lowest one with ≥ `BassSupportRatio` (0.75) of the best score, or a loud fundamental (`StrongFundamental`).
4. **Smoothing**: average with the previous chroma (50/50).
5. **`Match`**: cosine similarity between the chroma and every chord type × 12 roots, + `Prior`, + `BassBonus` (0.04) if the root is the bass. Rejected below `MinConfidence` (0.8).
6. **`UpdateStability`**: the same chord must win `StableFramesRequired` (3) times in a row before it's reported.

Returns `ChordAnalysis(Chord?, Confidence, Chroma[12])`.

## Tuning the detector

| Problem | Try |
|---|---|
| Shows chords during noise/speech | Raise `MinConfidence` |
| Misses chords / shows "–" too often | Lower `MinConfidence` or `PeakFloor` |
| Adds 7ths that aren't played | Lower `OvertoneWeight`, raise the 7th chords' `Prior` penalty |
| Wrong slash chords | `FindBass`: `BassSupportRatio`, `StrongFundamental` |
| Too slow to react | `StableFramesRequired`, the smoothing in `Process` |

Accuracy was measured with simulated chords (22 voicings × 10 tries): about 98% on a normal mic or in heavy noise, about 93% on a weak-bass laptop mic. That test harness isn't in the repo.

## Chord shapes (how to play it)

`ChordShape(Frets, Fingers)`: 6 values each, **low E string first**. Frets: `-1` muted (`ChordShape.Muted`), `0` open, `n` = fret. Fingers: `0` none, `1` index … `4` little finger. Standard tuning only (`ChordShape.OpenStrings`).

| Member | Meaning |
|---|---|
| `StartFret` | First fret drawn: 1 (with the nut) if the shape fits in frets 1–4, otherwise its lowest fret ("4fr") |
| `Barre` | `(Fret, FirstString, LastString)` when finger 1 lies across 2+ strings at the lowest fret |
| `Notes` | MIDI note each string plays (null = muted); shown under the diagram |

`ChordShapes.Find(chord)` picks a shape for any detected chord:
1. **`OpenShapes`**: hand-picked open chords by name (C, D, G, Dm, C7, D7, G7, B7, Cmaj7, Dmaj7, Fmaj7, Gmaj7, Dm7, Dsus2, Dsus4, D5).
2. Otherwise a **movable shape** from `EShapes` (root on the low E string) or `AShapes` (root on the A string), moved to the root's fret by `Transpose`. Whichever sits lower on the neck wins. At fret 0 these become the open E/A-family chords (E, Em, E7, A, Am, A7, Asus4 …) with the barre finger removed.
3. Slash chords show the plain chord's shape.

**To add or change a shape**, edit `OpenShapes` (exact chord name, e.g. `["Am"]`) or the templates. Every root × type was checked to play exactly the chord's notes (dropping the 5th is allowed in 4-note chords, e.g. open C7), with a stretch of at most 4 frets and one fret per finger.

### Diagram: `ChordDiagram`

A `FrameworkElement` that draws everything in `OnRender`. Set `Shape` to redraw; `DotBrush` sets the dot colour (teal on the chord screen). It draws strings (low E on the left), `FretsShown` (5) frets, the nut or "Nfr" label, ○ open / × muted markers, one bar + "1" for a barre, numbered finger dots, and note names under the strings.

## Chord screen (`ChordView`)

| Member | Purpose |
|---|---|
| `InitializeChords` | Subscribes `chordListener.SamplesCaptured`, runs `chordDetector.Process` on the audio thread, then `ShowChordAnalysis` on the UI thread; builds the chroma bars |
| `StartChords_Click` / `StartChordListening` | Shows the screen, resets the detector, copies `a4`, opens the mic |
| `ChordDeviceBox_SelectionChanged` | Mic choice, kept in sync with the tuner's `DeviceBox` |
| `ShowChordAnalysis` | Level bar, chroma bar heights/colours, chord texts, `ChordDiagramView.Shape`; holds the last chord for `HoldTime` |
| `ShowChordIdle` | "–" / "Strum a chord", hides the diagram |
| `AddRecentChord` | Last `RecentChordCount` (8) distinct chords in `RecentChordsPanel` |
| `BuildChromaBars` | Creates the 12 bars and labels in `ChromaGrid` |

XAML elements: `ChordNameText` (inside a `Viewbox` that shrinks long names), `ChordFullNameText`, `ChordNotesText`, `ChordMatchText`, `ChordDiagramView`, `ChromaGrid`, `RecentChordsPanel`, `ChordLevelBar`, `ChordDeviceBox`.
