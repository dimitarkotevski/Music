using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Music.Audio;
using Music.Chords;
using Music.Tuning;

namespace Music;

/// <summary>Chord recognition screen.</summary>
public partial class MainWindow
{
    private const int RecentChordCount = 8;
    private const double ChromaBarMaxHeight = 120;
    private static readonly Brush ChordBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x14, 0xB8, 0xA6)));
    private static readonly Brush ChromaBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3A, 0x41, 0x55)));

    private readonly MicrophoneListener chordListener = new();
    private readonly ChordDetector chordDetector = new(MicrophoneListener.SampleRate);
    private readonly List<Border> chromaBars = [];
    private readonly List<string> recentChords = [];
    private Chord? shownChord;
    private DateTime lastChordTime = DateTime.MinValue;

    private void InitializeChords()
    {
        // Analysis runs on the audio thread; only the result is passed to the UI.
        chordListener.SamplesCaptured += block =>
        {
            var analysis = chordDetector.Process(block.Samples, block.HasSignal);
            chordListener.MusicPresent = analysis.Chord is not null;
            Dispatcher.BeginInvoke(() => ShowChordAnalysis(analysis, block));
        };

        ChordDeviceBox.ItemsSource = DeviceBox.ItemsSource;
        ChordDeviceBox.SelectedIndex = DeviceBox.SelectedIndex;
        BuildChromaBars();
    }

    private void StartChords_Click(object sender, RoutedEventArgs e)
    {
        StartMenu.Visibility = Visibility.Collapsed;
        ChordView.Visibility = Visibility.Visible;
        ShowChordIdle();
        StartChordListening();
    }

    private void StartChordListening()
    {
        if (ChordDeviceBox.SelectedIndex < 0)
        {
            ChordFullNameText.Text = "No microphone found";
            return;
        }

        try
        {
            chordDetector.A4 = a4;
            chordDetector.Reset();
            chordListener.Start(ChordDeviceBox.SelectedIndex);
        }
        catch (Exception ex)
        {
            ChordFullNameText.Text = "Could not open microphone";
            ChordNotesText.Text = ex.Message;
        }
    }

    private void ChordDeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Keep the microphone choice shared with the tuner screen.
        if (DeviceBox.SelectedIndex != ChordDeviceBox.SelectedIndex)
            DeviceBox.SelectedIndex = ChordDeviceBox.SelectedIndex;

        if (ChordView.Visibility == Visibility.Visible)
            StartChordListening();
    }

    private void ShowChordAnalysis(ChordAnalysis analysis, AudioBlock block)
    {
        if (ChordView.Visibility != Visibility.Visible)
            return;

        ChordLevelBar.Value = Math.Min(1, Math.Sqrt(block.Level) * 3);
        ChordLevelBar.Foreground = block.HasSignal ? ChordBrush : LevelGatedBrush;

        var chordTones = analysis.Chord?.PitchClasses.ToHashSet() ?? shownChord?.PitchClasses.ToHashSet() ?? [];
        for (int i = 0; i < 12; i++)
        {
            chromaBars[i].Height = 4 + ChromaBarMaxHeight * analysis.Chroma[i];
            chromaBars[i].Background = chordTones.Contains(i) ? ChordBrush : ChromaBrush;
        }

        DateTime now = DateTime.UtcNow;
        if (analysis.Chord is not Chord chord)
        {
            if (now - lastChordTime > HoldTime)
                ShowChordIdle();
            return;
        }

        lastChordTime = now;
        ChordMatchText.Text = $"Match {analysis.Confidence:P0}";
        if (chord == shownChord)
            return;

        shownChord = chord;
        ChordNameText.Text = chord.Name;
        ChordNameText.Foreground = ChordBrush;
        ChordFullNameText.Text = chord.FullName;
        ChordFullNameText.Foreground = NeutralBrush;
        ChordNotesText.Text = string.Join("  ·  ", chord.NoteNames);
        ChordDiagramView.Shape = ChordShapes.Find(chord);
        ChordDiagramView.Visibility = ChordDiagramView.Shape is null ? Visibility.Hidden : Visibility.Visible;
        AddRecentChord(chord.Name);
    }

    private void ShowChordIdle()
    {
        shownChord = null;
        ChordNameText.Text = "–";
        ChordNameText.Foreground = NeutralBrush;
        ChordFullNameText.Text = "Strum a chord";
        ChordFullNameText.Foreground = DimBrush;
        ChordNotesText.Text = " ";
        ChordMatchText.Text = " ";
        ChordDiagramView.Visibility = Visibility.Hidden;
    }

    private void AddRecentChord(string name)
    {
        if (recentChords.Count > 0 && recentChords[^1] == name)
            return;

        recentChords.Add(name);
        if (recentChords.Count > RecentChordCount)
            recentChords.RemoveAt(0);

        RecentChordsPanel.Children.Clear();
        foreach (string recent in recentChords)
        {
            RecentChordsPanel.Children.Add(new Border
            {
                Background = StringBrush,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 0),
                Child = new TextBlock { Text = recent, FontWeight = FontWeights.SemiBold },
            });
        }
    }

    private void BuildChromaBars()
    {
        for (int i = 0; i < 12; i++)
        {
            ChromaGrid.ColumnDefinitions.Add(new ColumnDefinition());

            var cell = new Grid();
            cell.RowDefinitions.Add(new RowDefinition());
            cell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var bar = new Border
            {
                Width = 24,
                Height = 4,
                CornerRadius = new CornerRadius(4),
                Background = ChromaBrush,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            var label = new TextBlock
            {
                Text = NoteMath.Name(i),
                FontSize = 12,
                Foreground = DimBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0),
            };
            Grid.SetRow(label, 1);
            cell.Children.Add(bar);
            cell.Children.Add(label);

            Grid.SetColumn(cell, i);
            ChromaGrid.Children.Add(cell);
            chromaBars.Add(bar);
        }
    }
}
