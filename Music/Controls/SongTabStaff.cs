using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Music.Tabs;
using Music.Transcription;

namespace Music.Controls;

/// <summary>
/// Read-only tablature for a whole song. Unlike the live <see cref="TabStaff"/>, several
/// notes can share one vertical column, which is what makes chord transcription possible.
/// </summary>
public sealed class SongTabStaff : FrameworkElement
{
    private const double SlotWidth = 34, LineGap = 18, RowGap = 40, LabelWidth = 34, PaddingTop = 24, PaddingRight = 12;
    private const int MaxColumnsPerRow = 28;
    private static readonly Typeface Font = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface LabelFont = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface TimeFont = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Brush LineBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4A, 0x50, 0x66)));
    private static readonly Brush FaintBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2C, 0x31, 0x40)));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xB4)));
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0xE8, 0xEF)));
    private static readonly Brush BackgroundBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1B, 0x1E, 0x27)));
    private static readonly Brush UncertainBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)));

    private readonly List<(Rect Bounds, int Column, int Note)> noteBounds = [];

    public event Action<int, int>? NoteClicked;
    public event Action<int, int>? NoteRightClicked;

    public IReadOnlyList<SongTabColumn> Columns { get; set; } = [];

    /// <summary>Open-string MIDI notes, lowest string first (for the labels).</summary>
    public int[] Tuning { get; set; } = [40, 45, 50, 55, 59, 64];

    private static double RowHeight => 5 * LineGap + RowGap;

    public void Refresh()
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width;
        int perRow = ColumnsPerRow(width);
        int rows = Math.Max(1, (Columns.Count + perRow - 1) / perRow);
        return new Size(width, PaddingTop + rows * RowHeight);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        noteBounds.Clear();
        double width = Math.Max(0, ActualWidth);
        int perRow = ColumnsPerRow(width);
        int rows = Math.Max(1, (Columns.Count + perRow - 1) / perRow);
        double lineEnd = Math.Max(LabelWidth + SlotWidth, width - PaddingRight);
        var linePen = new Pen(LineBrush, 1);
        var faintPen = new Pen(FaintBrush, 1);

        for (int row = 0; row < rows; row++)
        {
            double top = PaddingTop + row * RowHeight;
            int firstColumn = row * perRow;

            if (firstColumn < Columns.Count)
            {
                var time = MakeText(FormatTime(Columns[firstColumn].Start), TimeFont, 10, LabelBrush);
                dc.DrawText(time, new Point(LabelWidth + 2, top - 16));
            }

            for (int s = 0; s < Tuning.Length; s++)
            {
                double y = StringY(top, s);
                DrawText(dc, TabText.StringLabel(Tuning, s).Trim(), LabelFont, 13, LabelBrush, new Point(LabelWidth / 2 - 2, y));
                dc.DrawLine(linePen, new Point(LabelWidth, y), new Point(lineEnd, y));
            }

            dc.DrawLine(new Pen(LineBrush, 2), new Point(LabelWidth, StringY(top, Tuning.Length - 1)),
                new Point(LabelWidth, StringY(top, 0)));

            // A light grid line every four columns makes it easier to follow the song.
            for (int slot = 4; slot < perRow; slot += 4)
            {
                double x = LabelWidth + slot * SlotWidth;
                if (x < lineEnd)
                    dc.DrawLine(faintPen, new Point(x, StringY(top, 0)), new Point(x, StringY(top, Tuning.Length - 1)));
            }

            for (int slot = 0; slot < perRow; slot++)
            {
                int index = firstColumn + slot;
                if (index >= Columns.Count)
                    break;

                var column = Columns[index];
                double centerX = LabelWidth + (slot + 0.5) * SlotWidth;
                for (int noteIndex = 0; noteIndex < column.Notes.Count; noteIndex++)
                {
                    var note = column.Notes[noteIndex];
                    double centerY = StringY(top, note.Position.String);
                    var text = MakeText(note.Position.Fret.ToString(CultureInfo.InvariantCulture), Font, 14, TextBrush);
                    double boxWidth = Math.Max(18, text.Width + 8);
                    var bounds = new Rect(centerX - boxWidth / 2, centerY - 9, boxWidth, 18);
                    dc.DrawRoundedRectangle(BackgroundBrush, null, bounds, 5, 5);
                    if (column.Confidence < 0.5)
                    {
                        var uncertainPen = new Pen(UncertainBrush, 1.2) { DashStyle = DashStyles.Dot };
                        dc.DrawRoundedRectangle(null, uncertainPen, bounds, 5, 5);
                    }

                    dc.DrawText(text, new Point(centerX - text.Width / 2, centerY - text.Height / 2));
                    noteBounds.Add((bounds, index, noteIndex));
                }
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Cursor = HitNote(e.GetPosition(this)) is null ? Cursors.Arrow : Cursors.Hand;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (HitNote(e.GetPosition(this)) is var hit && hit is not null)
        {
            NoteClicked?.Invoke(hit.Value.Column, hit.Value.Note);
            e.Handled = true;
        }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (HitNote(e.GetPosition(this)) is var hit && hit is not null)
        {
            NoteRightClicked?.Invoke(hit.Value.Column, hit.Value.Note);
            e.Handled = true;
        }
    }

    private (int Column, int Note)? HitNote(Point point)
    {
        foreach (var (bounds, column, note) in noteBounds)
        {
            var target = bounds;
            target.Inflate(4, 4);
            if (target.Contains(point))
                return (column, note);
        }

        return null;
    }

    private int ColumnsPerRow(double width) =>
        Math.Clamp((int)((width - LabelWidth - PaddingRight) / SlotWidth), 4, MaxColumnsPerRow);

    private static string FormatTime(double seconds)
    {
        int total = (int)Math.Max(0, seconds);
        return $"{total / 60}:{total % 60:00}";
    }

    private double StringY(double rowTop, int stringIndex) => rowTop + (Tuning.Length - 1 - stringIndex) * LineGap;

    private FormattedText MakeText(string text, Typeface font, double size, Brush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, font, size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private void DrawText(DrawingContext dc, string text, Typeface font, double size, Brush brush, Point center)
    {
        var formatted = MakeText(text, font, size, brush);
        dc.DrawText(formatted, new Point(center.X - formatted.Width / 2, center.Y - formatted.Height / 2));
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
