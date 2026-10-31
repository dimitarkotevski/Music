using AllTunning.Tuning;

namespace AllTunning.Chords;

/// <summary>A chord type, defined by its intervals in semitones above the root.</summary>
public sealed record ChordQuality(string Suffix, string Name, int[] Intervals, double Prior)
{
    public static IReadOnlyList<ChordQuality> All { get; } =
    [
        new("", "major", [0, 4, 7], 0),
        new("m", "minor", [0, 3, 7], 0),
        new("5", "power chord", [0, 7], -0.02),
        new("7", "dominant seventh", [0, 4, 7, 10], -0.01),
        new("maj7", "major seventh", [0, 4, 7, 11], -0.01),
        new("m7", "minor seventh", [0, 3, 7, 10], -0.01),
        new("sus2", "suspended second", [0, 2, 7], -0.02),
        new("sus4", "suspended fourth", [0, 5, 7], -0.02),
        new("dim", "diminished", [0, 3, 6], -0.03),
        new("aug", "augmented", [0, 4, 8], -0.03),
    ];
}

/// <summary>A recognised chord. <see cref="Bass"/> is set when the lowest note is not the root (an inversion).</summary>
public sealed record Chord(int Root, ChordQuality Quality, int? Bass = null)
{
    public string Name => NoteMath.Name(Root) + Quality.Suffix + (Bass is int bass ? "/" + NoteMath.Name(bass) : "");

    public string FullName => $"{NoteMath.Name(Root)} {Quality.Name}" + (Bass is int bass ? $" over {NoteMath.Name(bass)}" : "");

    public IEnumerable<int> PitchClasses => Quality.Intervals.Select(i => (Root + i) % 12);

    public IEnumerable<string> NoteNames => PitchClasses.Select(NoteMath.Name);
}
