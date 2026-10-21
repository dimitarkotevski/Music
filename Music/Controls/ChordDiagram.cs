using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Music.Chords;
using Music.Tuning;

namespace Music.Controls;

/// <summary>
/// Draws a guitar chord box: strings vertical (low E on the left), frets horizontal, ○ = open, × = muted,
/// numbered dots for fingers, a bar for barre chords, and "Nfr" when the shape starts above the nut.
/// </summary>
public sealed class ChordDiagram : FrameworkElement
{
    private const int FretsShown = 5;
    private const double MarginTop = 34, MarginBottom = 26, MarginLeft = 34, MarginRight = 12;

    public static readonly DependencyProperty ShapeProperty = DependencyProperty.Register(
        nameof(Shape), typeof(ChordShape), typeof(ChordDiagram),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DotBrushProperty = DependencyProperty.Register(
        nameof(DotBrush), typeof(Brush), typeof(ChordDiagram),
        new FrameworkPropertyMetadata(Brushes.Teal, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush LineBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x6C, 0x73, 0x89)));
    private static readonly Brush StringBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xB4)));
    private static readonly Brush NutBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0xE8, 0xEF)));
    private static readonly Brush BoardBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x23, 0x27, 0x33)));
    private static readonly Typeface Font = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public ChordShape? Shape
    {
        get => (ChordShape?)GetValue(ShapeProperty);
        set => SetValue(ShapeProperty, value);
    }

    public Brush DotBrush
    {
        get => (Brush)GetValue(DotBrushProperty);
        set => SetValue(DotBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Shape is not ChordShape shape)
            return;

        double left = MarginLeft, top = MarginTop;
        double width = ActualWidth - MarginLeft - MarginRight;
        double height = ActualHeight - MarginTop - MarginBottom;
        double stringGap = width / 5;
        double fretGap = height / FretsShown;
        double dotRadius = Math.Min(stringGap, fretGap) * 0.36;
        double X(int stringIndex) => left + stringIndex * stringGap;
        double RowCenter(int fret) => top + (fret - shape.StartFret + 0.5) * fretGap;

        dc.DrawRoundedRectangle(BoardBrush, null, new Rect(left - 6, top, width + 12, height), 4, 4);

        // Frets, and the nut (or the starting fret number) at the top.
        var fretPen = new Pen(LineBrush, 1.5);
        for (int i = 0; i <= FretsShown; i++)
            dc.DrawLine(fretPen, new Point(left, top + i * fretGap), new Point(left + width, top + i * fretGap));

        if (shape.StartFret == 1)
            dc.DrawRectangle(NutBrush, null, new Rect(left - 1, top - 5, width + 2, 6));
        else
            DrawText(dc, $"{shape.StartFret}fr", 13, StringBrush, new Point(left - 20, top + fretGap / 2), center: true);

        // Strings get thicker towards the low E.
        for (int s = 0; s < 6; s++)
            dc.DrawLine(new Pen(StringBrush, 2.2 - s * 0.25), new Point(X(s), top), new Point(X(s), top + height));

        // Open / muted markers above the nut, and the note each string plays below the box.
        var markerPen = new Pen(NutBrush, 1.8);
        var notes = shape.Notes.ToArray();
        for (int s = 0; s < 6; s++)
        {
            var markerCenter = new Point(X(s), top - 18);
            if (shape.Frets[s] == ChordShape.Muted)
                DrawText(dc, "×", 18, NutBrush, markerCenter, center: true);
            else if (shape.Frets[s] == 0)
                dc.DrawEllipse(null, markerPen, markerCenter, 6, 6);

            if (notes[s] is int note)
                DrawText(dc, NoteMath.Name(note), 11, StringBrush, new Point(X(s), top + height + 13), center: true);
        }

        // Barre: one bar under the index finger, numbered once in the middle.
        var barre = shape.Barre;
        if (barre is (int barreFret, int first, int last))
        {
            double y = RowCenter(barreFret);
            dc.DrawRoundedRectangle(DotBrush, null,
                new Rect(X(first) - dotRadius, y - dotRadius, X(last) - X(first) + 2 * dotRadius, 2 * dotRadius), dotRadius, dotRadius);
            DrawText(dc, "1", 13, Brushes.White, new Point((X(first) + X(last)) / 2, y), center: true);
        }

        for (int s = 0; s < 6; s++)
        {
            int fret = shape.Frets[s];
            bool onBarre = barre is (int bf, int bFirst, int bLast) && fret == bf && shape.Fingers[s] == 1 && s >= bFirst && s <= bLast;
            if (fret <= 0 || onBarre)
                continue;

            var center = new Point(X(s), RowCenter(fret));
            dc.DrawEllipse(DotBrush, null, center, dotRadius, dotRadius);
            if (shape.Fingers[s] > 0)
                DrawText(dc, shape.Fingers[s].ToString(CultureInfo.InvariantCulture), 13, Brushes.White, center, center: true);
        }
    }

    private void DrawText(DrawingContext dc, string text, double size, Brush brush, Point position, bool center)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Font, size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var origin = center ? new Point(position.X - formatted.Width / 2, position.Y - formatted.Height / 2) : position;
        dc.DrawText(formatted, origin);
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
