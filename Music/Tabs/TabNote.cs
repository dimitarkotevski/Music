using System.Text;
using Music.Tuning;

namespace Music.Tabs;

/// <summary>A note written in the tab. <paramref name="GapBefore"/> marks a pause before it.</summary>
public sealed record TabNote(int Midi, FretPosition Position, bool GapBefore)
{
    /// <summary>How clearly the string/fret won over the alternatives (1 = locked or only one position).</summary>
    public double Confidence { get; init; } = 1;
}

public static class TabText
{
    /// <summary>Formats notes as plain-text tablature (highest string on top), wrapped every <paramref name="notesPerRow"/> notes.</summary>
    public static string Format(IReadOnlyList<TabNote> notes, int[] tuning, int notesPerRow = 16)
    {
        var text = new StringBuilder();
        for (int start = 0; start < Math.Max(1, notes.Count); start += notesPerRow)
        {
            var row = notes.Skip(start).Take(notesPerRow).ToList();
            for (int s = tuning.Length - 1; s >= 0; s--)
            {
                text.Append(StringLabel(tuning, s)).Append('|');
                foreach (var note in row)
                {
                    int width = note.Position.Fret >= 10 ? 2 : 1;
                    if (note.GapBefore)
                        text.Append("--");
                    text.Append('-');
                    text.Append(note.Position.String == s ? note.Position.Fret.ToString().PadRight(width, '-') : new string('-', width));
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
