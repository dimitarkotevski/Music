using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Music.Audio;
using Music.Tabs;
using Music.Transcription;
using Music.Tuning;

namespace Music;

/// <summary>Tabs screen: writes every played note as tablature.</summary>
public partial class MainWindow
{
    private static readonly TimeSpan TabGap = TimeSpan.FromSeconds(1.5);
    private static readonly Brush TabBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)));

    // A shorter window and blocks than the tuner, so quick notes are separated.
    private readonly MicrophoneListener tabListener = new(windowSize: 2048, bufferMilliseconds: 25, maxFrequency: 1350);
    private readonly NoteTracker noteTracker = new();
    private readonly FretAssigner fretAssigner = new();
    private readonly List<TabNote> tabNotes = [];
    private DateTime lastTabNoteTime = DateTime.MinValue;
    private int? tabStringLock;

    // AI detector: Basic Pitch hears every note, the arranger places each new column on the neck.
    private readonly FretboardArranger liveArranger = new();
    private BasicPitchModel? basicPitch;
    private LiveNoteModel? liveNotes;
    private bool useAiTabs;
    private double lastTabColumnStart;

    // Temporary live-input diagnostic: lets us see whether the microphone, the pitch
    // detector, or the note tracker is where a second string gets lost.
    private readonly object tabDebugLock = new();
    private StreamWriter? tabDebugWriter;

    // Guided test: the app names each note, shows it on the tab and records the take until Next is clicked.
    private readonly List<TabTestStep> tabTestSteps = [];
    private readonly List<(int Index, TabTestStep Step, double Start, double End)> tabTestWindows = [];
    private Stopwatch? tabTestClock;
    private int tabTestStepIndex = -1;
    private double tabTestWindowStart;
    private bool tabTestRunning;
    private string? tabTestWavPath;
    private string? tabTestLogPath;
    private string? tabTestFolder;

    private readonly record struct TabTestStep(int Midi, int String, int Fret);

    private void InitializeTabs()
    {
        InitializeAiTabs();

        // Note tracking runs on the audio thread; only finished notes go to the UI.
        tabListener.PitchUpdated += reading =>
        {
            if (useAiTabs)
            {
                // The AI detector takes the raw samples instead; this reading only drives the level bar.
                Dispatcher.BeginInvoke(() => ShowTabInput(reading, null));
                return;
            }

            var note = noteTracker.Process(
                reading.Frequency,
                reading.Level,
                reading.Gate,
                reading.SpectralFrequency);
            LogTabDebug($"audio yin={DebugNote(reading.Frequency)} spec={DebugNote(reading.SpectralFrequency)} "
                + $"bright={(reading.Brightness is double b ? b.ToString("0.00") : "-")} "
                + $"specbright={(reading.SpectralBrightness is double sb ? sb.ToString("0.00") : "-")} "
                + $"level={reading.Level:0.0000} gate={reading.Gate:0.0000} "
                + $"event={(note is DetectedNote n ? DebugNote(n.Midi) : "-")} {noteTracker.DebugState}");
            Dispatcher.BeginInvoke(() => ShowTabInput(reading, note));
        };

        TabStaffView.NoteClicked += MoveTabNoteToNextString;
        tabListener.DetectHarmonics = true;
        TabTuningBox.ItemsSource = GuitarTuning.All;
        TabTuningBox.SelectedItem = tuning;
        TabDeviceBox.ItemsSource = DeviceBox.ItemsSource;
        TabDeviceBox.SelectedIndex = DeviceBox.SelectedIndex;
        RefreshTabStringChoices();
        RefreshTab();
    }

    private void InitializeAiTabs()
    {
        try
        {
            if (BasicPitchModel.DefaultPath is string path)
            {
                basicPitch = new BasicPitchModel(path);
                liveNotes = new LiveNoteModel(basicPitch);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Basic Pitch could not be loaded: " + ex.Message);
            basicPitch = null;
            liveNotes = null;
        }

        if (liveNotes is not null)
        {
            tabListener.SamplesCaptured += block =>
            {
                if (useAiTabs)
                    liveNotes.AddSamples(block.Samples, block.Gate);
            };
            liveNotes.ColumnDetected += column =>
            {
                LogTabDebug("ai column=" + string.Join("+", column.Select(n => $"{DebugNote(n.Midi)}({n.Amplitude:0.00})")));
                Dispatcher.BeginInvoke(() => AddTabColumn(column));
            };
        }

        // The AI detector runs its own thread; stop it whenever the Tabs screen is left.
        TabView.IsVisibleChanged += (_, _) =>
        {
            if (!TabView.IsVisible)
                liveNotes?.Stop();
        };

        TabEngineBox.ItemsSource = liveNotes is null
            ? new[] { "Classic (AI model missing)" }
            : new[] { "AI — chords and ringing strings", "Classic — one note at a time" };
        TabEngineBox.SelectedIndex = 0;
    }

    private void TabEngineBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool ai = liveNotes is not null && TabEngineBox.SelectedIndex == 0;
        if (ai == useAiTabs)
            return;

        useAiTabs = ai;
        RefreshTab();
        if (TabView.Visibility == Visibility.Visible)
            StartTabListening();
    }

    private void StartTabs_Click(object sender, RoutedEventArgs e)
    {
        StartMenu.Visibility = Visibility.Collapsed;
        TabView.Visibility = Visibility.Visible;
        StartTabListening();
    }

    private void StartTabListening()
    {
        if (TabDeviceBox.SelectedIndex < 0)
        {
            TabStringText.Text = "No microphone found";
            return;
        }

        try
        {
            noteTracker.A4 = a4;
            noteTracker.Reset();
            tabListener.GateRatio = listener.GateRatio;
            StartTabDebugLog();
            LogTabDebug("detector=" + (useAiTabs ? "ai" : "classic"));
            liveNotes?.Stop();
            if (useAiTabs && liveNotes is not null)
            {
                liveNotes.LowestMidi = tuning.Strings.Min();
                liveNotes.HighestMidi = tuning.Strings.Max() + FretAssigner.MaxFret;
                liveNotes.Start();
            }

            tabListener.Start(TabDeviceBox.SelectedIndex);
        }
        catch (Exception ex)
        {
            TabStringText.Text = "Could not open microphone";
            TabFretText.Text = ex.Message;
        }
    }

    private void ShowTabInput(PitchReading reading, DetectedNote? note)
    {
        if (TabView.Visibility != Visibility.Visible)
            return;

        TabLevelBar.Value = Math.Min(1, Math.Sqrt(reading.Level) * 3);
        TabLevelBar.Foreground = reading.Level >= reading.Gate ? TabBrush : LevelGatedBrush;

        if (note is DetectedNote detected)
        {
            int yinMidi = reading.Frequency is double yin
                ? (int)Math.Round(NoteMath.FrequencyToMidi(yin, a4))
                : -1;
            int spectralMidi = reading.SpectralFrequency is double spectral
                ? (int)Math.Round(NoteMath.FrequencyToMidi(spectral, a4))
                : -1;
            double? brightness = detected.Midi == yinMidi
                ? reading.Brightness
                : detected.Midi == spectralMidi
                    ? reading.SpectralBrightness
                    : null;
            AddTabNote(detected.Midi, brightness);
        }
    }

    private string DebugNote(int midi) => NoteMath.Name(midi) + NoteMath.Octave(midi);

    private string DebugNote(double? frequency) => frequency is double value
        ? $"{DebugNote((int)Math.Round(NoteMath.FrequencyToMidi(value, a4)))} {value:0.0}Hz"
        : "-";

    private void StartTabDebugLog()
    {
        try
        {
            StopTabDebugLog();
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (string.IsNullOrEmpty(desktop))
                desktop = AppContext.BaseDirectory;
            string folder = Path.Combine(desktop, "GuitarTabsTest");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "tab-live.txt");

            var writer = new StreamWriter(path, append: false) { AutoFlush = true };
            writer.WriteLine("Tabs live input diagnostic");
            writer.WriteLine("Tuning: " + string.Join(" ", tuning.Strings.Select(m => DebugNote(m))));
            writer.WriteLine("Written: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            writer.WriteLine("time\tyin\tspectral\tlevel\tgate\tevent\tstate");
            lock (tabDebugLock)
                tabDebugWriter = writer;
        }
        catch
        {
            // Diagnostics must never stop the microphone.
        }
    }

    private void StopTabDebugLog()
    {
        lock (tabDebugLock)
        {
            tabDebugWriter?.Dispose();
            tabDebugWriter = null;
        }
    }

    private void LogTabDebug(string line)
    {
        lock (tabDebugLock)
        {
            try
            {
                tabDebugWriter?.WriteLine($"{DateTime.Now:HH:mm:ss.fff}\t{line}");
            }
            catch
            {
                // Ignore a failed diagnostic write.
            }
        }
    }

    private void AddTabNote(int midi, double? brightness = null)
    {
        DateTime now = DateTime.UtcNow;
        if (fretAssigner.Estimate(midi, tuning.Strings, now, tabStringLock, brightness) is not FretEstimate estimate)
        {
            // A locked string is a promise: notes that cannot be played on it are dropped.
            // Say so instead of looking as if the new note was never heard.
            LogTabDebug($"ui drop midi={DebugNote(midi)} lock={tabStringLock?.ToString() ?? "auto"}");
            if (tabStringLock is int locked)
            {
                TabStringText.Text = "Note outside the locked string";
                TabFretText.Text = $"{NoteMath.Name(midi)}{NoteMath.Octave(midi)} is not on "
                    + $"{TabText.StringName(tuning.Strings, locked)}. Press 0 for Auto.";
            }

            return;
        }

        FretPosition position = estimate.Position;
        LogTabDebug($"ui add midi={DebugNote(midi)} string={position.String + 1} fret={position.Fret} "
            + $"confidence={estimate.Confidence:0.00} brightness={(brightness is double b ? b.ToString("0.00") : "-")}");
        bool gap = tabNotes.Count > 0 && now - lastTabNoteTime > TabGap;
        lastTabNoteTime = now;
        tabNotes.Add(new TabNote(midi, position, gap) { Confidence = estimate.Confidence });
        RefreshTab();
        TabScroll.ScrollToEnd();
    }

    /// <summary>Writes the notes the AI detector heard together as one tab column.</summary>
    private void AddTabColumn(IReadOnlyList<TranscribedNote> column)
    {
        if (!useAiTabs || TabView.Visibility != Visibility.Visible || column.Count == 0)
            return;

        // A locked string can only hold one note at a time: keep the loudest one it can play.
        if (tabStringLock is int locked)
        {
            var playable = column
                .Where(n => n.Midi - tuning.Strings[locked] is >= 0 and <= FretAssigner.MaxFret)
                .OrderByDescending(n => n.Amplitude)
                .FirstOrDefault();
            AddTabNote((playable ?? column.OrderByDescending(n => n.Amplitude).First()).Midi);
            return;
        }

        var previous = LastTabColumn();
        if (liveArranger.ArrangeNext(previous, column, tuning.Strings, FretAssigner.MaxFret) is not SongTabColumn placed)
        {
            LogTabDebug("ui drop column (no playable shape)");
            return;
        }

        DateTime now = DateTime.UtcNow;
        bool gap = tabNotes.Count > 0 && now - lastTabNoteTime > TabGap;
        lastTabNoteTime = now;
        lastTabColumnStart = placed.Start;

        for (int i = 0; i < placed.Notes.Count; i++)
        {
            var note = placed.Notes[i];
            tabNotes.Add(new TabNote(note.Midi, note.Position, gap && i == 0)
            {
                Confidence = placed.Confidence,
                ChordWithPrevious = i > 0,
            });
        }

        // Keep the classic assigner in step, so switching detectors carries on from the same hand position.
        var top = placed.Notes[^1];
        fretAssigner.MoveHandTo(top.Position, top.Midi);

        LogTabDebug("ui add column " + string.Join(" ", placed.Notes.Select(n => $"{DebugNote(n.Midi)}=s{n.Position.String + 1}f{n.Position.Fret}"))
            + $" confidence={placed.Confidence:0.00}");
        RefreshTab();
        TabScroll.ScrollToEnd();
    }

    /// <summary>The last written column as the arranger sees it, including any string the player moved by hand.</summary>
    private SongTabColumn? LastTabColumn()
    {
        if (tabNotes.Count == 0)
            return null;

        var last = TabText.Columns(tabNotes)[^1];
        var notes = last.Select(n => new ArrangedNote(n.Midi, n.Position, lastTabColumnStart, lastTabColumnStart)).ToList();
        return new SongTabColumn(lastTabColumnStart, notes, last[0].Confidence);
    }

    /// <summary>Index of the first note of the column that contains <paramref name="index"/>.</summary>
    private int TabColumnStart(int index)
    {
        while (index > 0 && tabNotes[index].ChordWithPrevious)
            index--;
        return index;
    }

    /// <summary>Click on a note: move it to the next string where the same pitch can be played.</summary>
    private void MoveTabNoteToNextString(int index)
    {
        var note = tabNotes[index];

        // In a chord, every note needs its own string.
        int first = TabColumnStart(index);
        var usedByChord = new HashSet<int>();
        for (int i = first; i < tabNotes.Count && (i == first || tabNotes[i].ChordWithPrevious); i++)
        {
            if (i != index)
                usedByChord.Add(tabNotes[i].Position.String);
        }

        var positions = FretAssigner.Positions(note.Midi, tuning.Strings)
            .Where(p => p == note.Position || !usedByChord.Contains(p.String))
            .ToList();
        if (positions.Count < 2)
            return;

        int current = positions.FindIndex(p => p == note.Position);
        var next = positions[(current + 1) % positions.Count];
        tabNotes[index] = note with { Position = next, Confidence = 1 };

        // Moving the latest note also tells the assigner where the hand is for the next notes.
        if (index == tabNotes.Count - 1)
            fretAssigner.MoveHandTo(next, note.Midi);

        RefreshTab();
    }

    private void RefreshTab()
    {
        noteTracker.LowestMidi = tuning.Strings.Min();
        noteTracker.HighestMidi = tuning.Strings.Max() + FretAssigner.MaxFret;

        TabStaffView.Notes = tabNotes;
        TabStaffView.Tuning = tuning.Strings;
        TabStaffView.Refresh();

        if (tabNotes.Count == 0)
        {
            TabNoteText.Text = "–";
            TabNoteText.FontSize = 56;
            TabStringText.Text = useAiTabs ? "Play normally" : "Play one note at a time";
            TabFretText.Text = useAiTabs
                ? "Ringing strings and chords are fine"
                : "Mute the previous string before the next note";
            TabAlternativesText.Text = " ";
            return;
        }

        var lastColumn = TabText.Columns(tabNotes)[^1];
        if (lastColumn.Count > 1)
        {
            ShowTabChord(lastColumn);
            return;
        }

        TabNoteText.FontSize = 56;
        var last = tabNotes[^1];
        TabNoteText.Text = NoteMath.Name(last.Midi) + NoteMath.Octave(last.Midi);
        TabStringText.Text = TabText.StringName(tuning.Strings, last.Position.String);
        TabFretText.Text = last.Position.Fret == 0 ? "Open string" : $"Fret {last.Position.Fret}";

        if (tabStringLock is int locked)
        {
            string lockedName = TabText.StringName(tuning.Strings, locked);
            TabAlternativesText.Text = last.Position.String == locked
                ? $"Locked to {lockedName}"
                : $"Locked to {lockedName}; this note is outside its range";
            return;
        }

        string confidence = last.Confidence >= 0.75
            ? "high confidence"
            : last.Confidence >= 0.5
                ? "medium confidence"
                : "uncertain";
        TabStringText.Text += $" · {confidence}";

        var others = FretAssigner.Positions(last.Midi, tuning.Strings).Where(p => p != last.Position).ToList();
        string also = others.Count == 0
            ? "Only playable here"
            : "Also: " + string.Join("  ·  ", others.Select(p => $"{TabText.StringName(tuning.Strings, p.String)} fret {p.Fret}"));
        TabAlternativesText.Text = last.Confidence < 0.5 ? "Uncertain — " + also : also;
    }

    /// <summary>A chord: its notes, and the shape from the low string up (x = not played), e.g. "x 0 2 2 1 0".</summary>
    private void ShowTabChord(IReadOnlyList<TabNote> chord)
    {
        TabNoteText.FontSize = chord.Count > 3 ? 26 : 34;
        TabNoteText.Text = string.Join(" ", chord.OrderBy(n => n.Midi).Select(n => NoteMath.Name(n.Midi)));
        TabStringText.Text = $"Chord · {chord.Count} notes";
        TabFretText.Text = string.Join(" ", Enumerable.Range(0, tuning.Strings.Length)
            .Select(s => chord.FirstOrDefault(n => n.Position.String == s) is TabNote n ? n.Position.Fret.ToString() : "x"));
        TabAlternativesText.Text = chord[0].Confidence < 0.5
            ? "Uncertain shape — click a note to move it to another string"
            : "Click a note to move it to another string";
    }

    /// <summary>Auto plus one entry per string, lowest string first, e.g. "String 6 (E)".</summary>
    private void RefreshTabStringChoices()
    {
        int selected = tabStringLock is int locked ? locked + 1 : 0;
        var choices = new List<string> { "Auto (guess the string)" };
        for (int s = 0; s < tuning.Strings.Length; s++)
            choices.Add(TabText.StringName(tuning.Strings, s));

        BuildTabStringButtons(choices);
        TabStringBox.ItemsSource = choices;
        TabStringBox.SelectedIndex = Math.Min(selected, choices.Count - 1);
        RefreshTabStringButtons();
    }

    /// <summary>Quick string buttons: Auto plus one per string, keyed 6–1 like the guitar.</summary>
    private void BuildTabStringButtons(IReadOnlyList<string> choices)
    {
        var style = (Style)FindResource("TabStringChoice");
        TabStringButtonsPanel.Children.Clear();

        for (int i = 0; i < choices.Count; i++)
        {
            string label = i == 0
                ? "Auto"
                : $"{tuning.Strings.Length - (i - 1)} {TabText.StringLabel(tuning.Strings, i - 1).Trim()}";
            var button = new Button
            {
                Content = label,
                Tag = i,
                Style = style,
                ToolTip = i == 0
                    ? "Auto: guess the string from the notes you play (key 0)"
                    : $"Play only on {choices[i]}; the fret is calculated (key {tuning.Strings.Length - (i - 1)})",
            };
            button.Click += TabStringButton_Click;
            TabStringButtonsPanel.Children.Add(button);
        }
    }

    private void TabStringButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int index } && index >= 0 && index < TabStringBox.Items.Count)
            TabStringBox.SelectedIndex = index;
    }

    /// <summary>Paint the chosen string button amber so the current lock is obvious.</summary>
    private void RefreshTabStringButtons()
    {
        foreach (Button button in TabStringButtonsPanel.Children.OfType<Button>())
        {
            bool selected = button.Tag is int index && index == TabStringBox.SelectedIndex;
            button.Background = selected ? TabBrush : StringBrush;
            button.BorderBrush = selected ? TabBrush : StringBrush;
            button.Foreground = selected ? Brushes.Black : NeutralBrush;
            button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    /// <summary>Keys 6–1 lock the matching guitar string; 0 returns to Auto.</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (TabView.Visibility != Visibility.Visible || TabTestPanel.Visibility == Visibility.Visible)
            return;
        if (Keyboard.Modifiers != ModifierKeys.None)
            return;
        if (Keyboard.FocusedElement is ComboBox { IsDropDownOpen: true })
            return;

        int number = e.Key switch
        {
            Key.D0 or Key.NumPad0 => 0,
            Key.D1 or Key.NumPad1 => 1,
            Key.D2 or Key.NumPad2 => 2,
            Key.D3 or Key.NumPad3 => 3,
            Key.D4 or Key.NumPad4 => 4,
            Key.D5 or Key.NumPad5 => 5,
            Key.D6 or Key.NumPad6 => 6,
            Key.D7 or Key.NumPad7 => 7,
            Key.D8 or Key.NumPad8 => 8,
            Key.D9 or Key.NumPad9 => 9,
            _ => -1,
        };
        if (number < 0)
            return;
        if (number > TabStringBox.Items.Count - 1)
            return;

        int index = number == 0 ? 0 : TabStringBox.Items.Count - number;
        if (index < 0 || index >= TabStringBox.Items.Count)
            return;

        TabStringBox.SelectedIndex = index;
        e.Handled = true;
    }

    private void TabStringBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        tabStringLock = TabStringBox.SelectedIndex <= 0 ? null : TabStringBox.SelectedIndex - 1;

        // Fix notes already in the tab too, so choosing a string repairs the current take.
        if (tabStringLock is int locked)
        {
            for (int i = 0; i < tabNotes.Count; i++)
            {
                // A chord cannot move onto one string.
                bool inChord = tabNotes[i].ChordWithPrevious || (i + 1 < tabNotes.Count && tabNotes[i + 1].ChordWithPrevious);
                if (inChord)
                    continue;

                int fret = tabNotes[i].Midi - tuning.Strings[locked];
                if (fret is >= 0 and <= FretAssigner.MaxFret)
                    tabNotes[i] = tabNotes[i] with { Position = new FretPosition(locked, fret) };
            }
        }

        RefreshTab();
        RefreshTabStringButtons();
    }

    private void UndoTab_Click(object sender, RoutedEventArgs e)
    {
        // A chord is undone as one column.
        if (tabNotes.Count > 0)
            tabNotes.RemoveRange(TabColumnStart(tabNotes.Count - 1), tabNotes.Count - TabColumnStart(tabNotes.Count - 1));
        RefreshTab();
    }

    private void ClearTab_Click(object sender, RoutedEventArgs e)
    {
        tabNotes.Clear();
        fretAssigner.Reset();
        RefreshTab();
    }

    private void CopyTab_Click(object sender, RoutedEventArgs e)
    {
        if (tabNotes.Count > 0)
            Clipboard.SetText(TabText.Format(tabNotes, tuning.Strings));
    }

    private void StartTabTest_Click(object sender, RoutedEventArgs e)
    {
        if (tabTestRunning)
            return;

        if (TabDeviceBox.SelectedIndex < 0)
        {
            MessageBox.Show("No microphone found. Choose one in the Microphone list first.",
                "Guided test", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        StartTabListening();
        if (!tabListener.IsRunning)
        {
            MessageBox.Show(TabFretText.Text, "Guided test", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        tabNotes.Clear();
        fretAssigner.Reset();
        lastTabNoteTime = DateTime.MinValue;
        RefreshTab();

        tabTestSteps.Clear();
        for (int s = 0; s < tuning.Strings.Length; s++)
            tabTestSteps.Add(new TabTestStep(tuning.Strings[s], s, 0));
        foreach (int fret in new[] { 1, 2, 3, 4, 5, 7, 9, 12 })
            tabTestSteps.Add(new TabTestStep(tuning.Strings[0] + fret, 0, fret));

        tabTestWindows.Clear();
        tabTestWindowStart = 0;

        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (string.IsNullOrEmpty(desktop))
            desktop = AppContext.BaseDirectory;
        tabTestFolder = Path.Combine(desktop, "GuitarTabsTest");
        Directory.CreateDirectory(tabTestFolder);

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        tabTestWavPath = Path.Combine(tabTestFolder, $"guided-{stamp}.wav");
        tabTestLogPath = Path.ChangeExtension(tabTestWavPath, ".txt");

        try
        {
            tabListener.StartRecording(tabTestWavPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not start recording: " + ex.Message,
                "Guided test", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        tabTestRunning = true;
        tabTestClock = Stopwatch.StartNew();
        tabTestStepIndex = -1;

        TabTestPanel.Visibility = Visibility.Visible;
        TabTestTitle.Text = "GUIDED TEST";
        TabTestStatusText.Text = "Recording…";
        TabTestFolderButton.Visibility = Visibility.Collapsed;
        TabTestNextButton.Visibility = Visibility.Visible;
        TabTestCancelButton.Content = "Cancel";
        ShowTabTestStep(0);
    }

    /// <summary>Shows one target note as a big note name and as a one-note tablature staff.</summary>
    private void ShowTabTestStep(int index)
    {
        tabTestStepIndex = index;
        var step = tabTestSteps[index];
        TabTestNoteText.Text = NoteMath.Name(step.Midi) + NoteMath.Octave(step.Midi);
        TabTestInstructionText.Text = TabTestInstruction(step);
        TabTestProgressText.Text = $"{index + 1} / {tabTestSteps.Count}";
        TabTestNextButton.Content = index == tabTestSteps.Count - 1 ? "Finish" : "Next";

        TabTestStaff.Tuning = tuning.Strings;
        TabTestStaff.Notes =
        [
            new TabNote(step.Midi, new FretPosition(step.String, step.Fret), GapBefore: false)
        ];
        TabTestStaff.Refresh();
    }

    private void NextTabTest_Click(object sender, RoutedEventArgs e)
    {
        if (!tabTestRunning || tabTestClock is null || tabTestStepIndex < 0)
            return;

        double end = tabTestClock.Elapsed.TotalSeconds;
        tabTestWindows.Add((tabTestStepIndex, tabTestSteps[tabTestStepIndex], tabTestWindowStart, end));
        if (tabTestStepIndex + 1 >= tabTestSteps.Count)
        {
            FinishTabTest();
            return;
        }

        tabTestWindowStart = end;
        ShowTabTestStep(tabTestStepIndex + 1);
    }

    private string TabTestInstruction(TabTestStep step)
    {
        string name = TabText.StringName(tuning.Strings, step.String);
        return step.Fret == 0 ? $"{name} — open string" : $"{name} — fret {step.Fret}";
    }

    private void FinishTabTest()
    {
        if (!tabTestRunning)
            return;

        tabTestRunning = false;
        tabTestClock?.Stop();
        tabTestClock = null;
        tabListener.StopRecording();
        WriteTabTestLog();

        TabTestTitle.Text = "TEST COMPLETE";
        TabTestNoteText.Text = "✓";
        TabTestInstructionText.Text = "Recording saved";
        TabTestProgressText.Text = $"{tabTestWindows.Count} notes";
        TabTestStatusText.Text = $"WAV:\n{tabTestWavPath}\n\nLog:\n{tabTestLogPath}";
        TabTestFolderButton.Visibility = Visibility.Visible;
        TabTestNextButton.Visibility = Visibility.Collapsed;
        TabTestCancelButton.Content = "Close";
    }

    private void WriteTabTestLog()
    {
        if (tabTestLogPath is null)
            return;

        try
        {
            var log = new StringBuilder();
            log.AppendLine("Guided tabs test");
            log.AppendLine("Tuning: " + string.Join(" ", tuning.Strings.Select(m => NoteMath.Name(m) + NoteMath.Octave(m))));
            log.AppendLine("Recording: " + tabTestWavPath);
            log.AppendLine("Written: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            log.AppendLine();
            log.AppendLine("Step\tStart(s)\tEnd(s)\tNote\tString\tFret");
            foreach (var window in tabTestWindows)
            {
                string note = NoteMath.Name(window.Step.Midi) + NoteMath.Octave(window.Step.Midi);
                string name = TabText.StringName(tuning.Strings, window.Step.String);
                log.AppendLine($"{window.Index + 1}\t{window.Start:F3}\t{window.End:F3}\t{note}\t{name}\t{window.Step.Fret}");
            }

            File.WriteAllText(tabTestLogPath, log.ToString());
        }
        catch (Exception ex)
        {
            TabTestStatusText.Text = "Could not save the log: " + ex.Message;
        }
    }

    private void CancelTabTest_Click(object sender, RoutedEventArgs e)
    {
        if (tabTestRunning)
        {
            tabTestRunning = false;
            tabTestClock?.Stop();
            tabTestClock = null;
            tabListener.StopRecording();

            try
            {
                if (tabTestWavPath is string path && File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // A partial recording is harmless if it cannot be removed.
            }
        }

        TabTestPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>Stops a guided test when leaving Tabs or closing the window; the partial take is kept.</summary>
    private void StopTabTest()
    {
        if (tabTestRunning)
        {
            tabTestRunning = false;
            tabTestClock?.Stop();
            tabTestClock = null;
            tabListener.StopRecording();
            WriteTabTestLog();
        }

        TabTestPanel.Visibility = Visibility.Collapsed;
    }

    private void OpenTabTestFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (tabTestWavPath is string file && File.Exists(file))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
            else if (tabTestFolder is string folder)
                Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
        }
        catch
        {
            // Opening Explorer is only a convenience.
        }
    }

    private void TabTuningBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The tuner's dropdown is the master copy of the tuning choice.
        if (TabTuningBox.SelectedItem is GuitarTuning selected && TuningBox.SelectedItem != selected)
            TuningBox.SelectedItem = selected;
    }

    private void TabDeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceBox.SelectedIndex != TabDeviceBox.SelectedIndex)
            DeviceBox.SelectedIndex = TabDeviceBox.SelectedIndex;

        if (TabView.Visibility == Visibility.Visible)
            StartTabListening();
    }
}
