using System.IO;
using System.Text.Json;

namespace Music.Tabs;

public sealed record SavedTabNote(int Midi, int String, int Fret);

/// <summary>A tab the player corrected and saved: every column with the strings and frets they chose.</summary>
public sealed record SavedTab(string Id, DateTime Saved, int[] Tuning, List<List<SavedTabNote>> Columns);

/// <summary>
/// Remembers how the player fingers things, from the tabs they corrected and saved. When the notes just
/// played match a passage from a saved tab (the same pitches, with the same notes before them), the
/// positions the player chose there are preferred. The longer the matching passage, the stronger the
/// preference; a single matching note on its own only nudges.
/// </summary>
public sealed class FingeringMemory
{
    /// <summary>How many earlier columns are compared (the passage is up to this many columns plus the new one).</summary>
    public const int MaxContext = 3;

    // Cost weight by the number of earlier columns that matched. An ordinary hand shift costs 0.9 per fret
    // in the arranger, so three matching notes before it outweigh a few frets of movement.
    private static readonly double[] ContextWeights = [0.4, 1.0, 1.8, 2.5];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly List<SavedTab> tabs = [];

    // Passage key → position chosen for each pitch → how often, plus where the passage occurs.
    private readonly Dictionary<string, Dictionary<(int Midi, FretPosition Position), int>> passages = [];
    private readonly Dictionary<string, List<(SavedTab Tab, int Column)>> occurrences = [];

    /// <summary>Desktop\GuitarTabsTest\training\fingerings.json.</summary>
    public static string DefaultPath => Path.Combine(Path.GetDirectoryName(StringTimbreModel.DefaultPath)!, "fingerings.json");

    public int TabCount => tabs.Count;

    public static FingeringMemory Load(string path)
    {
        var memory = new FingeringMemory();
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<List<SavedTab>>(File.ReadAllText(path)) is { } saved)
                memory.tabs.AddRange(saved);
        }
        catch
        {
            // A damaged file only means starting again; the next save writes a fresh one.
        }

        memory.Rebuild();
        return memory;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(tabs, JsonOptions));
    }

    /// <summary>Adds a tab, or replaces the one with the same id (saving the same take again).</summary>
    public void Add(SavedTab tab)
    {
        tabs.RemoveAll(t => t.Id == tab.Id);
        tabs.Add(tab);
        Rebuild();
    }

    /// <summary>
    /// What the saved tabs say about the new column: for each pitch, how often each position was chosen,
    /// using the longest matching passage. Null when no saved tab has these pitches.
    /// </summary>
    /// <param name="previous">The pitches of the columns already on the tab, oldest first.</param>
    public MemoryMatch? Find(IReadOnlyList<IReadOnlyCollection<int>> previous, IReadOnlyCollection<int> current, int[] tuning)
    {
        for (int length = Math.Min(MaxContext, previous.Count); length >= 0; length--)
        {
            var context = previous.Skip(previous.Count - length).ToList();
            if (!passages.TryGetValue(Key(tuning, context, current), out var counts))
                continue;

            var shares = new Dictionary<(int Midi, FretPosition Position), double>();
            foreach (var pitch in counts.Keys.Select(k => k.Midi).Distinct())
            {
                int total = counts.Where(c => c.Key.Midi == pitch).Sum(c => c.Value);
                foreach (var (key, count) in counts.Where(c => c.Key.Midi == pitch))
                    shares[key] = (double)count / total;
            }

            // How the player fingered the matched earlier columns, so they can be fixed too.
            var (tab, column) = occurrences[Key(tuning, context, current)][^1];
            var contextColumns = tab.Columns.Skip(column - length).Take(length).ToList();
            return new MemoryMatch(length, ContextWeights[length], shares, contextColumns);
        }

        return null;
    }

    private void Rebuild()
    {
        passages.Clear();
        occurrences.Clear();
        foreach (var tab in tabs)
        {
            var pitches = tab.Columns.Select(c => (IReadOnlyCollection<int>)c.Select(n => n.Midi).ToList()).ToList();
            for (int i = 0; i < tab.Columns.Count; i++)
            {
                for (int length = 0; length <= Math.Min(MaxContext, i); length++)
                {
                    string key = Key(tab.Tuning, pitches.Skip(i - length).Take(length).ToList(), pitches[i]);
                    if (!passages.TryGetValue(key, out var counts))
                        passages[key] = counts = [];
                    if (!occurrences.TryGetValue(key, out var places))
                        occurrences[key] = places = [];
                    places.Add((tab, i));
                    foreach (var note in tab.Columns[i])
                    {
                        var entry = (note.Midi, new FretPosition(note.String, note.Fret));
                        counts[entry] = counts.GetValueOrDefault(entry) + 1;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Once a saved passage is recognised (the new column and at least <paramref name="minimumContext"/>
    /// before it match), gives the columns just before the newest one the player's fingering from that
    /// passage. Notes the player edited by hand are never changed. Returns how many notes moved.
    /// </summary>
    public static int ApplyToRecent(List<TabNote> notes, MemoryMatch match, int minimumContext = 2)
    {
        if (match.ContextLength < minimumContext)
            return 0;

        var columns = TabText.Columns(notes);
        int firstColumn = columns.Count - 1 - match.ContextLength;
        if (firstColumn < 0)
            return 0;

        // Note index where each column starts.
        var starts = new List<int>();
        for (int i = 0; i < notes.Count; i++)
        {
            if (i == 0 || !notes[i].ChordWithPrevious)
                starts.Add(i);
        }

        int moved = 0;
        for (int k = 0; k < match.ContextLength; k++)
        {
            int column = firstColumn + k;
            var saved = match.ContextColumns[k];
            for (int i = starts[column]; i < starts[column] + columns[column].Count; i++)
            {
                var note = notes[i];
                if (note.Edited || saved.FirstOrDefault(n => n.Midi == note.Midi) is not SavedTabNote wanted)
                    continue;

                var position = new FretPosition(wanted.String, wanted.Fret);
                bool stringTaken = columns[column].Any(other => !ReferenceEquals(other, note) && other.Position.String == position.String);
                if (position == note.Position || stringTaken)
                    continue;

                notes[i] = note with { Position = position, Remembered = true, Confidence = Math.Max(note.Confidence, 0.9) };
                moved++;
            }
        }

        return moved;
    }

    private static string Key(int[] tuning, IReadOnlyList<IReadOnlyCollection<int>> context, IReadOnlyCollection<int> current) =>
        string.Join(",", tuning) + "|"
        + string.Join(";", context.Select(c => string.Join(",", c.Order()))) + "|"
        + string.Join(",", current.Order());
}

/// <summary>A passage found in the saved tabs.</summary>
/// <param name="ContextLength">Earlier columns that matched as well (0 = only the new pitches).</param>
/// <param name="Shares">For each pitch and position, the share of saved occurrences that used that position.</param>
/// <param name="ContextColumns">The matched earlier columns as the player fingered them (latest saved tab), oldest first.</param>
public sealed record MemoryMatch(
    int ContextLength,
    double Weight,
    IReadOnlyDictionary<(int Midi, FretPosition Position), double> Shares,
    IReadOnlyList<IReadOnlyList<SavedTabNote>> ContextColumns)
{
    /// <summary>The arranger cost of a position: low where the player usually puts this pitch here, high elsewhere.</summary>
    public double Cost(int midi, FretPosition position)
    {
        if (!Shares.Keys.Any(k => k.Midi == midi))
            return 0;
        return Weight * -Math.Log(Math.Max(0.05, Shares.GetValueOrDefault((midi, position))));
    }

    /// <summary>True when the position is the one the player chose most often here.</summary>
    public bool IsUsual(int midi, FretPosition position)
    {
        var forPitch = Shares.Where(s => s.Key.Midi == midi).ToList();
        return forPitch.Count > 0 && forPitch.MaxBy(s => s.Value).Key.Position == position;
    }
}
