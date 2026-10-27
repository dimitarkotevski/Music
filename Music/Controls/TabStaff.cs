using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Music.Tabs;

namespace Music.Controls;

/// <summary>
/// Draws notes as guitar tablature: six lines per row (highest string on top), fret numbers on the
/// string they were played on, wrapping into new rows. Clicking a note raises <see cref="NoteClicked"/>.
/// </summary>
public sealed class TabStaff : FrameworkElement
{
    private const double SlotWidth = 30, LineGap = 18, RowGap = 30, LabelWidth = 30, PaddingTop = 12, PaddingRight = 12;
    private static readonly Typeface Font = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface LabelFont = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Brush LineBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4A, 0x50, 0x66)));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xB4)));
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0xE8, 0xEF)));
    private static readonly Brush BackgroundBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x15, 0x17, 0x1E)));
    private static readonly Brush UncertainBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)));

    private readonly List<(Rect Bounds, int Index)> noteBounds = [];

    public event Action<int>? NoteClicked;

    public IReadOnlyList<TabNote> Notes { get; set; } = [];

    /// <summary>Open-string MIDI notes, lowest string first (for the labels).</summary>
    public int[] Tuning { get; set; } = [40, 45, 50, 55, 59, 64];

    public Brush HighlightBrush { get; set; } = Brushes.Teal;

    private static double RowHeight => 5 * LineGap + RowGap;

    /// <summary>Call after changing <see cref="Notes"/> or <see cref="Tuning"/>.</summary>
    public void Refresh()
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width;
        int rows = Layout(width).Select(slot => slot.Row).DefaultIfEmpty(0).Max() + 1;
        return new Size(width, PaddingTop + rows * RowHeight);
    }

    protected override void OnRender(DrawingContext dc)
    {
        // A transparent background makes the whole area receive mouse events.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        noteBounds.Clear();

        var slots = Layout(ActualWidth);
        int rows = slots.Select(slot => slot.Row).DefaultIfEmpty(0).Max() + 1;
        double lineEnd = ActualWidth - PaddingRight;
        var linePen = new Pen(LineBrush, 1);

        for (int row = 0; row < rows; row++)
        {
            double top = PaddingTop + row * RowHeight;
            for (int s = 0; s < Tuning.Length; s++)
            {
                double y = StringY(top, s);
                DrawText(dc, TabText.StringLabel(Tuning, s).Trim(), LabelFont, 13, LabelBrush, new Point(LabelWidth / 2 - 2, y));
                dc.DrawLine(linePen, new Point(LabelWidth, y), new Point(lineEnd, y));
            }

            dc.DrawLine(new Pen(LineBrush, 2), new Point(LabelWidth, StringY(top, Tuning.Length - 1)), new Point(LabelWidth, StringY(top, 0)));
        }

        foreach (var (row, slot, index) in slots)
        {
            var note = Notes[index];
            double top = PaddingTop + row * RowHeight;
            var center = new Point(LabelWidth + (slot + 0.5) * SlotWidth, StringY(top, note.Position.String));
            bool highlighted = index == Notes.Count - 1;

            var text = MakeText(note.Position.Fret.ToString(CultureInfo.InvariantCulture), Font, 14, highlighted ? Brushes.White : TextBrush);
            var bounds = new Rect(center.X - Math.Max(9, text.Width / 2 + 4), center.Y - 9, Math.Max(18, text.Width + 8), 18);
            dc.DrawRoundedRectangle(highlighted ? HighlightBrush : BackgroundBrush, null, bounds, 5, 5);
            if (note.Confidence < 0.5)
            {
                var uncertainPen = new Pen(UncertainBrush, 1.2) { DashStyle = DashStyles.Dot };
                dc.DrawRoundedRectangle(null, uncertainPen, bounds, 5, 5);
            }
            dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
            noteBounds.Add((bounds, index));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Cursor = HitNote(e.GetPosition(this)) is null ? Cursors.Arrow : Cursors.Hand;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (HitNote(e.GetPosition(this)) is int index)
        {
            NoteClicked?.Invoke(index);
            e.Handled = true;
        }
    }

    private int? HitNote(Point point)
    {
        foreach (var (bounds, index) in noteBounds)
        {
            var target = bounds;
            target.Inflate(4, 4);
            if (target.Contains(point))
                return index;
        }

        return null;
    }

    /// <summary>Row and slot for every note. A note after a pause gets an empty slot before it.</summary>
    private List<(int Row, int Slot, int Index)> Layout(double width)
    {
        int slotsPerRow = Math.Max(4, (int)((width - LabelWidth - PaddingRight) / SlotWidth));
        var result = new List<(int, int, int)>();
        int position = 0;
        for (int i = 0; i < Notes.Count; i++)
        {
            if (Notes[i].GapBefore && position % slotsPerRow != 0)
                position++;
            result.Add((position / slotsPerRow, position % slotsPerRow, i));
            position++;
        }

        return result;
    }

    /// <summary>Highest string on the top line.</summary>
    private double StringY(double rowTop, int stringIndex) => rowTop + (Tuning.Length - 1 - stringIndex) * LineGap;

    private FormattedText MakeText(string text, Typeface font, double size, Brush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, font, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

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
