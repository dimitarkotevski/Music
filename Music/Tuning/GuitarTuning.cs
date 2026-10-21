namespace Music.Tuning;

/// <summary>A guitar tuning, strings ordered from lowest (6th) to highest (1st).</summary>
public sealed class GuitarTuning(string name, int[] strings)
{
    public string Name { get; } = name;

    public int[] Strings { get; } = strings;

    public string DisplayName => $"{Name}  ({string.Join(" ", Strings.Select(NoteMath.Name))})";

    public static IReadOnlyList<GuitarTuning> All { get; } =
    [
        Create("Standard", "E2 A2 D3 G3 B3 E4"),
        Create("Drop D", "D2 A2 D3 G3 B3 E4"),
        Create("Half Step Down", "Eb2 Ab2 Db3 Gb3 Bb3 Eb4"),
        Create("Full Step Down", "D2 G2 C3 F3 A3 D4"),
        Create("C Standard", "C2 F2 Bb2 Eb3 G3 C4"),
        Create("Drop C", "C2 G2 C3 F3 A3 D4"),
        Create("Drop B", "B1 F#2 B2 E3 G#3 C#4"),
        Create("Drop A", "A1 E2 A2 D3 F#3 B3"),
        Create("Open G", "D2 G2 D3 G3 B3 D4"),
        Create("Open D", "D2 A2 D3 F#3 A3 D4"),
        Create("Open E", "E2 B2 E3 G#3 B3 E4"),
        Create("Open A", "E2 A2 E3 A3 C#4 E4"),
        Create("Open C", "C2 G2 C3 G3 C4 E4"),
        Create("DADGAD", "D2 A2 D3 G3 A3 D4"),
    ];

    private static GuitarTuning Create(string name, string notes) =>
        new(name, notes.Split(' ').Select(NoteMath.Parse).ToArray());
}
