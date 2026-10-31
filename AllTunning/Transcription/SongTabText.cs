using System.Text;
using AllTunning.Tabs;

namespace AllTunning.Transcription;

/// <summary>Plain-text export for the Songs screen (highest string on top).</summary>
public static class SongTabText
{
    public static string Format(
        IReadOnlyList<SongTabColumn> columns,
        int[] tuning,
        int columnsPerRow = 24,
        string? title = null)
    {
        columnsPerRow = Math.Max(1, columnsPerRow);
        var text = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(title))
            text.AppendLine(title.Trim());

        for (int start = 0; start < Math.Max(1, columns.Count); start += columnsPerRow)
        {
            var row = columns.Skip(start).Take(columnsPerRow).ToList();
            for (int s = tuning.Length - 1; s >= 0; s--)
            {
                text.Append(TabText.StringLabel(tuning, s)).Append('|');
                foreach (var column in row)
                {
                    var note = column.Notes.FirstOrDefault(n => n.Position.String == s);
                    text.Append('-');
                    text.Append(note is null
                        ? "--"
                        : note.Position.Fret.ToString().PadRight(2, '-'));
                    text.Append('-');
                }

                text.Append("-|").AppendLine();
            }

            text.AppendLine();
        }

        return text.ToString().TrimEnd() + Environment.NewLine;
    }
}
