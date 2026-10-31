using AllTunning.Tuning;

namespace AllTunning.Chords;

/// <summary>
/// A guitar fingering in standard tuning. Arrays run from the low E string (index 0) to the high E string (index 5).
/// </summary>
/// <param name="Frets"><see cref="Muted"/>, 0 for an open string, otherwise the fret pressed.</param>
/// <param name="Fingers">0 = no finger, 1 = index, 2 = middle, 3 = ring, 4 = little finger.</param>
public sealed record ChordShape(int[] Frets, int[] Fingers)
{
    public const int Muted = -1;

    /// <summary>MIDI notes of the open strings in standard tuning (E2 A2 D3 G3 B3 E4).</summary>
    public static readonly int[] OpenStrings = [40, 45, 50, 55, 59, 64];

    public int LowestFret => Frets.Where(f => f > 0).DefaultIfEmpty(0).Min();

    public int HighestFret => Frets.Max();

    /// <summary>The first fret drawn in a diagram: 1 (with the nut) when the shape fits near the nut.</summary>
    public int StartFret => HighestFret <= 4 ? 1 : LowestFret;

    /// <summary>The index finger lying across several strings, if any.</summary>
    public (int Fret, int FirstString, int LastString)? Barre
    {
        get
        {
            int fret = LowestFret;
            var strings = Enumerable.Range(0, 6).Where(i => Fingers[i] == 1 && Frets[i] == fret && fret > 0).ToList();
            return strings.Count >= 2 ? (fret, strings.Min(), strings.Max()) : null;
        }
    }

    /// <summary>The MIDI note each string plays, or null when muted.</summary>
    public IEnumerable<int?> Notes => Frets.Select((fret, i) => fret == Muted ? (int?)null : OpenStrings[i] + fret);
}

/// <summary>Finds a playable shape for any chord the detector can report.</summary>
public static class ChordShapes
{
    private const int X = ChordShape.Muted;

    // Common open-position shapes, keyed by chord name. Everything else uses the movable shapes below.
    private static readonly Dictionary<string, ChordShape> OpenShapes = new()
    {
        ["C"] = new([X, 3, 2, 0, 1, 0], [0, 3, 2, 0, 1, 0]),
        ["D"] = new([X, X, 0, 2, 3, 2], [0, 0, 0, 1, 3, 2]),
        ["G"] = new([3, 2, 0, 0, 0, 3], [2, 1, 0, 0, 0, 3]),
        ["Dm"] = new([X, X, 0, 2, 3, 1], [0, 0, 0, 2, 3, 1]),
        ["C7"] = new([X, 3, 2, 3, 1, 0], [0, 3, 2, 4, 1, 0]),
        ["D7"] = new([X, X, 0, 2, 1, 2], [0, 0, 0, 2, 1, 3]),
        ["G7"] = new([3, 2, 0, 0, 0, 1], [3, 2, 0, 0, 0, 1]),
        ["B7"] = new([X, 2, 1, 2, 0, 2], [0, 2, 1, 3, 0, 4]),
        ["Cmaj7"] = new([X, 3, 2, 0, 0, 0], [0, 3, 2, 0, 0, 0]),
        ["Dmaj7"] = new([X, X, 0, 2, 2, 2], [0, 0, 0, 1, 2, 3]),
        ["Fmaj7"] = new([X, X, 3, 2, 1, 0], [0, 0, 3, 2, 1, 0]),
        ["Gmaj7"] = new([3, 2, 0, 0, 0, 2], [3, 2, 0, 0, 0, 1]),
        ["Dm7"] = new([X, X, 0, 2, 1, 1], [0, 0, 0, 2, 1, 1]),
        ["Dsus2"] = new([X, X, 0, 2, 3, 0], [0, 0, 0, 1, 3, 0]),
        ["Dsus4"] = new([X, X, 0, 2, 3, 3], [0, 0, 0, 1, 2, 3]),
        ["D5"] = new([X, X, 0, 2, 3, X], [0, 0, 0, 1, 3, 0]),
    };

    // Movable shapes as fret offsets from the root's fret. Finger 1 is the barre.
    // E-shape: root on the low E string. A-shape: root on the A string.
    private static readonly Dictionary<string, (int[] Offsets, int[] Fingers)> EShapes = new()
    {
        [""] = ([0, 2, 2, 1, 0, 0], [1, 3, 4, 2, 1, 1]),
        ["m"] = ([0, 2, 2, 0, 0, 0], [1, 3, 4, 1, 1, 1]),
        ["5"] = ([0, 2, 2, X, X, X], [1, 3, 4, 0, 0, 0]),
        ["7"] = ([0, 2, 0, 1, 0, 0], [1, 3, 1, 2, 1, 1]),
        ["maj7"] = ([0, 2, 1, 1, 0, 0], [1, 4, 2, 3, 1, 1]),
        ["m7"] = ([0, 2, 0, 0, 0, 0], [1, 3, 1, 1, 1, 1]),
        ["sus4"] = ([0, 2, 2, 2, 0, 0], [1, 2, 3, 4, 1, 1]),
        ["dim"] = ([0, 1, 2, 0, X, X], [1, 2, 3, 1, 0, 0]),
        ["aug"] = ([0, 3, 2, 1, 1, 0], [1, 4, 3, 2, 2, 1]),
    };

    private static readonly Dictionary<string, (int[] Offsets, int[] Fingers)> AShapes = new()
    {
        [""] = ([X, 0, 2, 2, 2, 0], [0, 1, 2, 3, 4, 1]),
        ["m"] = ([X, 0, 2, 2, 1, 0], [0, 1, 3, 4, 2, 1]),
        ["5"] = ([X, 0, 2, 2, X, X], [0, 1, 3, 4, 0, 0]),
        ["7"] = ([X, 0, 2, 0, 2, 0], [0, 1, 3, 1, 4, 1]),
        ["maj7"] = ([X, 0, 2, 1, 2, 0], [0, 1, 3, 2, 4, 1]),
        ["m7"] = ([X, 0, 2, 0, 1, 0], [0, 1, 3, 1, 2, 1]),
        ["sus2"] = ([X, 0, 2, 2, 0, 0], [0, 1, 3, 4, 1, 1]),
        ["sus4"] = ([X, 0, 2, 2, 3, 0], [0, 1, 2, 3, 4, 1]),
        ["dim"] = ([X, 0, 1, 2, 1, X], [0, 1, 2, 4, 3, 0]),
        ["aug"] = ([X, 0, 3, 2, 2, 1], [0, 1, 4, 3, 3, 2]),
    };

    /// <summary>
    /// A shape for the chord: a hand-picked open shape if there is one, otherwise whichever movable
    /// shape sits lowest on the neck. Slash chords use the shape of the plain chord.
    /// </summary>
    public static ChordShape? Find(Chord chord)
    {
        string suffix = chord.Quality.Suffix;
        if (OpenShapes.TryGetValue(NoteMath.Name(chord.Root) + suffix, out var open))
            return open;

        ChordShape? best = null;
        foreach (var (shapes, rootString) in new[] { (EShapes, 0), (AShapes, 1) })
        {
            if (!shapes.TryGetValue(suffix, out var template))
                continue;

            int rootFret = (((chord.Root - ChordShape.OpenStrings[rootString]) % 12) + 12) % 12;
            var shape = Transpose(template.Offsets, template.Fingers, rootFret);
            if (best is null || shape.HighestFret < best.HighestFret)
                best = shape;
        }

        return best;
    }

    private static ChordShape Transpose(int[] offsets, int[] fingers, int rootFret)
    {
        var frets = offsets.Select(o => o == X ? X : o + rootFret).ToArray();

        // At the nut there's no barre: strings on the barre ring open and the other fingers move down one.
        var fingering = rootFret == 0
            ? fingers.Select((f, i) => offsets[i] <= 0 ? 0 : f - 1).ToArray()
            : fingers.ToArray();

        return new ChordShape(frets, fingering);
    }
}
