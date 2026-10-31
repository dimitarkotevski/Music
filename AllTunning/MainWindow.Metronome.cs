using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AllTunning.Audio;

namespace AllTunning;

/// <summary>Metronome screen.</summary>
public partial class MainWindow
{
    private static readonly TimeSpan TapResetGap = TimeSpan.FromSeconds(2);
    private static readonly Brush MetronomeBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6)));
    private static readonly Brush StopBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)));

    private readonly Metronome metronome = new();
    private readonly List<DateTime> taps = [];
    private readonly List<Ellipse> beatDots = [];

    private void InitializeMetronome()
    {
        metronome.Beat += beat => Dispatcher.BeginInvoke(() => ShowBeat(beat));
        TimeSignatureBox.ItemsSource = TimeSignature.All;
        TimeSignatureBox.SelectedItem = TimeSignature.All.First(t => t.Beats == 4);
        SetBpm(metronome.Bpm);
    }

    private void StartMetronome_Click(object sender, RoutedEventArgs e)
    {
        StartMenu.Visibility = Visibility.Collapsed;
        MetronomeView.Visibility = Visibility.Visible;
    }

    private void StopMetronome()
    {
        metronome.Stop();
        StartStopButton.Content = "Start";
        StartStopButton.Background = MetronomeBrush;
        ShowBeat(-1);
    }

    private void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (metronome.IsRunning)
        {
            StopMetronome();
            return;
        }

        try
        {
            metronome.Start();
            StartStopButton.Content = "Stop";
            StartStopButton.Background = StopBrush;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not start audio output:\n{ex.Message}", "Metronome",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SetBpm(int bpm)
    {
        metronome.Bpm = bpm;
        BpmText.Text = metronome.Bpm.ToString();
        TempoNameText.Text = TempoName(metronome.Bpm);
        if ((int)BpmSlider.Value != metronome.Bpm)
            BpmSlider.Value = metronome.Bpm;
    }

    private void BpmSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Fires while the XAML is still loading, before the other controls exist.
        if (BpmText is null)
            return;

        SetBpm((int)e.NewValue);
    }

    private void BpmDown_Click(object sender, RoutedEventArgs e) => SetBpm(metronome.Bpm - 1);

    private void BpmUp_Click(object sender, RoutedEventArgs e) => SetBpm(metronome.Bpm + 1);

    private void TapTempo_Click(object sender, RoutedEventArgs e)
    {
        DateTime now = DateTime.UtcNow;
        if (taps.Count > 0 && now - taps[^1] > TapResetGap)
            taps.Clear();

        taps.Add(now);
        if (taps.Count > 5)
            taps.RemoveAt(0);

        if (taps.Count >= 2)
        {
            double averageSeconds = (taps[^1] - taps[0]).TotalSeconds / (taps.Count - 1);
            SetBpm((int)Math.Round(60 / averageSeconds));
        }
    }

    private void TimeSignatureBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TimeSignatureBox.SelectedItem is not TimeSignature signature)
            return;

        metronome.BeatsPerBar = signature.Beats;
        BuildBeatDots(signature.Beats);
    }

    private void BuildBeatDots(int beats)
    {
        BeatsPanel.Children.Clear();
        beatDots.Clear();
        for (int i = 0; i < beats; i++)
        {
            var dot = new Ellipse { Width = i == 0 ? 30 : 24, Height = i == 0 ? 30 : 24, Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center, Fill = StringBrush };
            beatDots.Add(dot);
            BeatsPanel.Children.Add(dot);
        }
    }

    private void ShowBeat(int beat)
    {
        if (beat >= 0 && !metronome.IsRunning)
            return;

        for (int i = 0; i < beatDots.Count; i++)
            beatDots[i].Fill = i != beat ? StringBrush : i == 0 ? InTuneBrush : MetronomeBrush;
    }

    private static string TempoName(int bpm) => bpm switch
    {
        < 40 => "Grave",
        < 60 => "Largo",
        < 66 => "Larghetto",
        < 76 => "Adagio",
        < 108 => "Andante",
        < 120 => "Moderato",
        < 168 => "Allegro",
        < 200 => "Presto",
        _ => "Prestissimo",
    };
}
