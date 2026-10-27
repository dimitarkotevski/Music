using System.Text;
using Music.Tuning;

namespace Music.Tabs;

/// <summary>A note written in the tab. <paramref name="GapBefore"/> marks a pause before it.</summary>
public sealed record TabNote(int Midi, FretPosition Position, bool GapBefore)
{
    /// <summary>How clearly the string/fret won over the alternatives (1 = locked or only one position).</summary>
    public double Confidence { get; init; } = 1;

    /// <summary>True when this note sounds together with the note before it (a chord), so both share a column.</summary>
    public bool ChordWithPrevious { get; init; }

    /// <summary>How the note sounded when it was heard (for string training), or null.</summary>
    public HeardSound? Heard { get; init; }

    /// <summary>True when the position came from the player's saved tabs (<see cref="FingeringMemory"/>).</summary>
    public bool Remembered { get; init; }

    /// <summary>True when the player set this note by hand; nothing automatic changes it afterwards.</summary>
    public bool Edited { get; init; }
}

/// <summary>The pitch that was heard and its <see cref="LiveNoteModel.SoundProfile"/>.</summary>
public sealed record HeardSound(int Midi, double[] Profile);

public static class TabText
{
    /// <summary>Groups notes into columns: a chord's notes share one column.</summary>
    public static List<List<TabNote>> Columns(IReadOnlyList<TabNote> notes)
    {
        var columns = new List<List<TabNote>>();
        foreach (var note in notes)
        {
            if (note.ChordWithPrevious && columns.Count > 0)
                columns[^1].Add(note);
            else
                columns.Add([note]);
        }

        return columns;
    }

    /// <summary>Formats notes as plain-text tablature (highest string on top), wrapped every <paramref name="notesPerRow"/> columns.</summary>
    public static string Format(IReadOnlyList<TabNote> notes, int[] tuning, int notesPerRow = 16)
    {
        var columns = Columns(notes);
        var text = new StringBuilder();
        for (int start = 0; start < Math.Max(1, columns.Count); start += notesPerRow)
        {
            var row = columns.Skip(start).Take(notesPerRow).ToList();
            for (int s = tuning.Length - 1; s >= 0; s--)
            {
                text.Append(StringLabel(tuning, s)).Append('|');
                foreach (var column in row)
                {
                    int width = column.Any(n => n.Position.Fret >= 10) ? 2 : 1;
                    if (column[0].GapBefore)
                        text.Append("--");
                    text.Append('-');
                    var onString = column.FirstOrDefault(n => n.Position.String == s);
                    text.Append(onString is not null ? onString.Position.Fret.ToString().PadRight(width, '-') : new string('-', width));
                    text.Append('-');
                }

                text.Append("-|").AppendLine();
            }

            text.AppendLine();
        }

        return text.ToString().TrimEnd() + Environment.NewLine;
    }

    /// <summary>The usual tab label: lowercase for the top string when it has the same name as the bottom one (e…E).</summary>
    public static string StringLabel(int[] tuning, int stringIndex)
    {
        string name = NoteMath.Name(tuning[stringIndex]);
        bool topRepeatsBottom = stringIndex == tuning.Length - 1 && name == NoteMath.Name(tuning[0]);
        return (topRepeatsBottom ? name.ToLowerInvariant() : name).PadRight(2);
    }

    /// <summary>"String 1 (e)" … "String 6 (E)": guitarists number strings from the highest.</summary>
    public static string StringName(int[] tuning, int stringIndex) =>
        $"String {tuning.Length - stringIndex} ({StringLabel(tuning, stringIndex).Trim()})";
}
