using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Music.Audio;
using Music.Tuning;

namespace Music;

public partial class MainWindow : Window
{
    private const double InTuneCents = 5;
    private const double GaugeRangeCents = 50;
    private const double GaugeSweepDegrees = 60;
    private const double GaugeCenterX = 160, GaugeCenterY = 170, GaugeRadius = 140;
    private static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan NewNoteGap = TimeSpan.FromSeconds(0.25);

    private static readonly Brush NeutralBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0xE8, 0xEF)));
    private static readonly Brush DimBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xB4)));
    private static readonly Brush InTuneBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x84)));
    private static readonly Brush OffBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x43)));
    private static readonly Brush StringBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2C, 0x31, 0x40)));
    private static readonly Brush ActiveStringBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x5B, 0x8C, 0xFF)));
    private readonly MicrophoneListener listener = new();
    private readonly TonePlayer tonePlayer = new();
    private readonly List<double> recentFrequencies = [];
    private readonly List<Button> stringButtons = [];

    private GuitarTuning tuning = GuitarTuning.All[0];
    private double a4 = 440;
    private int? manualString;
    private double needleCents;
    private DateTime lastPitchTime = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        DrawGauge();

        listener.PitchUpdated += reading => Dispatcher.BeginInvoke(() => OnPitch(reading));

        TuningBox.ItemsSource = GuitarTuning.All;
        TuningBox.SelectedIndex = 0;

        var devices = MicrophoneListener.GetDevices();
        DeviceBox.ItemsSource = devices;
        if (devices.Count > 0)
            DeviceBox.SelectedIndex = 0;
        else
            HintText.Text = "No microphone found";
    }

    private void OnPitch(PitchReading reading)
    {
        LevelBar.Value = Math.Min(1, Math.Sqrt(reading.Level) * 3);

        DateTime now = DateTime.UtcNow;
        if (reading.Frequency is not double frequency)
        {
            if (now - lastPitchTime > NewNoteGap)
                recentFrequencies.Clear();
            if (now - lastPitchTime > HoldTime)
                ShowIdle();
            return;
        }

        lastPitchTime = now;
        recentFrequencies.Add(frequency);
        if (recentFrequencies.Count > 5)
            recentFrequencies.RemoveAt(0);
        double smoothed = Median(recentFrequencies);

        int stringIndex = manualString ?? NearestString(smoothed);
        int targetMidi = tuning.Strings[stringIndex];
        double cents = NoteMath.Cents(smoothed, NoteMath.MidiToFrequency(targetMidi, a4));

        int detectedMidi = (int)Math.Round(NoteMath.FrequencyToMidi(smoothed, a4));
        NoteText.Text = NoteMath.Name(targetMidi);
        CentsText.Text = $"{cents:+0;-0;0} cents";
        FrequencyText.Text = $"{smoothed:F1} Hz  ·  hearing {NoteMath.Name(detectedMidi)}{NoteMath.Octave(detectedMidi)}";

        bool inTune = Math.Abs(cents) <= InTuneCents;
        Brush stateBrush = inTune ? InTuneBrush : OffBrush;
        HintText.Text = inTune ? "In tune ✓" : cents < 0 ? "Tune up ↑" : "Tune down ↓";
        HintText.Foreground = stateBrush;
        NoteText.Foreground = stateBrush;
        Needle.Stroke = stateBrush;

        MoveNeedle(Math.Clamp(cents, -GaugeRangeCents, GaugeRangeCents));
        HighlightString(stringIndex);
    }

    private void ShowIdle()
    {
        NoteText.Text = manualString is int i ? NoteMath.Name(tuning.Strings[i]) : "–";
        NoteText.Foreground = NeutralBrush;
        HintText.Text = "Play a string";
        HintText.Foreground = DimBrush;
        CentsText.Text = " ";
        FrequencyText.Text = " ";
        Needle.Stroke = NeutralBrush;
        MoveNeedle(0);
        HighlightString(manualString ?? -1);
    }

    private void MoveNeedle(double targetCents)
    {
        needleCents += 0.35 * (targetCents - needleCents);
        NeedleRotate.Angle = needleCents / GaugeRangeCents * GaugeSweepDegrees;
    }

    private int NearestString(double frequency)
    {
        int best = 0;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < tuning.Strings.Length; i++)
        {
            double distance = Math.Abs(NoteMath.Cents(frequency, NoteMath.MidiToFrequency(tuning.Strings[i], a4)));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private void BuildStringButtons()
    {
        StringsPanel.Children.Clear();
        stringButtons.Clear();

        for (int i = 0; i < tuning.Strings.Length; i++)
        {
            int index = i;
            var button = new Button
            {
                Content = NoteMath.Name(tuning.Strings[i]),
                Style = (Style)FindResource("StringButton"),
                ToolTip = $"String {tuning.Strings.Length - i}: click to select and hear the reference tone",
            };
            button.Click += (_, _) => SelectString(index);
            stringButtons.Add(button);
            StringsPanel.Children.Add(button);
        }

        HighlightString(manualString ?? -1);
    }

    private void SelectString(int index)
    {
        manualString = index;
        AutoBox.IsChecked = false;
        HighlightString(index);
        tonePlayer.Play(NoteMath.MidiToFrequency(tuning.Strings[index], a4));
    }

    private void HighlightString(int index)
    {
        for (int i = 0; i < stringButtons.Count; i++)
        {
            var brush = i == index ? ActiveStringBrush : StringBrush;
            stringButtons[i].Background = brush;
            stringButtons[i].BorderBrush = brush;
        }
    }

    private void DrawGauge()
    {
        for (int c = -50; c <= 50; c += 5)
        {
            bool major = c % 10 == 0;
            var (x1, y1) = GaugePoint(c, GaugeRadius);
            var (x2, y2) = GaugePoint(c, GaugeRadius - (major ? 16 : 8));
            GaugeCanvas.Children.Insert(0, new Line
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                Stroke = c == 0 ? InTuneBrush : DimBrush,
                StrokeThickness = major ? 2 : 1,
            });
        }

        foreach (int c in new[] { -50, -25, 25, 50 })
        {
            var (x, y) = GaugePoint(c, GaugeRadius - 32);
            var label = new TextBlock { Text = c > 0 ? $"+{c}" : $"{c}", FontSize = 11, Foreground = DimBrush };
            Canvas.SetLeft(label, x - 10);
            Canvas.SetTop(label, y - 8);
            GaugeCanvas.Children.Insert(0, label);
        }

        // Green "in tune" band.
        double bandRadius = GaugeRadius + 6;
        var (sx, sy) = GaugePoint(-InTuneCents, bandRadius);
        var (ex, ey) = GaugePoint(InTuneCents, bandRadius);
        var figure = new PathFigure { StartPoint = new Point(sx, sy) };
        figure.Segments.Add(new ArcSegment(new Point(ex, ey), new Size(bandRadius, bandRadius), 0, false,
            SweepDirection.Clockwise, true));
        GaugeCanvas.Children.Insert(0, new Path
        {
            Data = new PathGeometry([figure]),
            Stroke = InTuneBrush,
            StrokeThickness = 6,
            Opacity = 0.8,
        });
    }

    private static (double X, double Y) GaugePoint(double cents, double radius)
    {
        double angle = cents / GaugeRangeCents * GaugeSweepDegrees * Math.PI / 180;
        return (GaugeCenterX + radius * Math.Sin(angle), GaugeCenterY - radius * Math.Cos(angle));
    }

    private void TuningBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TuningBox.SelectedItem is not GuitarTuning selected)
            return;

        tuning = selected;
        BuildStringButtons();
        ShowIdle();
    }

    private void DeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceBox.SelectedIndex < 0)
            return;

        try
        {
            listener.Start(DeviceBox.SelectedIndex);
            HintText.Text = "Play a string";
        }
        catch (Exception ex)
        {
            HintText.Text = "Could not open microphone";
            FrequencyText.Text = ex.Message;
        }
    }

    private void AutoBox_Changed(object sender, RoutedEventArgs e)
    {
        if (AutoBox.IsChecked == true)
        {
            manualString = null;
            HighlightString(-1);
        }
    }

    private void A4Down_Click(object sender, RoutedEventArgs e) => SetA4(a4 - 1);

    private void A4Up_Click(object sender, RoutedEventArgs e) => SetA4(a4 + 1);

    private void SetA4(double value)
    {
        a4 = Math.Clamp(value, 415, 466);
        A4Text.Text = $"A4 = {a4:0} Hz";
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        listener.Dispose();
        tonePlayer.Dispose();
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
