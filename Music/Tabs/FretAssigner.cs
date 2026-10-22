namespace Music.Tabs;

/// <summary>A place on the neck. String 0 is the lowest string, matching <see cref="Music.Tuning.GuitarTuning.Strings"/>.</summary>
public readonly record struct FretPosition(int String, int Fret);

/// <summary>
/// Chooses which string a note was played on. Audio alone only gives the pitch, so this picks the
/// position a guitarist would most likely use: the one needing the least hand movement from the
/// current hand position (a four-fret window), preferring open strings near the nut.
/// </summary>
public sealed class FretAssigner
{
    public const int MaxFret = 22;
    private const int HandSpan = 3; // the hand covers its fret and the next three
    private const int OpenPosition = 1;
    private const double FrettedCost = 0.1; // pressing a fret is a little more effort than an open string
    private static readonly TimeSpan ResetAfter = TimeSpan.FromSeconds(4);

    private int handFret = OpenPosition;
    private int? lastString;
    private DateTime lastTime = DateTime.MinValue;

    /// <summary>Every position where the note can be played, lowest fret first.</summary>
    public static IReadOnlyList<FretPosition> Positions(int midi, int[] tuning) =>
        Enumerable.Range(0, tuning.Length)
            .Select(s => new FretPosition(s, midi - tuning[s]))
            .Where(p => p.Fret is >= 0 and <= MaxFret)
            .OrderBy(p => p.Fret)
            .ToList();

    public void Reset()
    {
        handFret = OpenPosition;
        lastString = null;
        lastTime = DateTime.MinValue;
    }

    public FretPosition? Assign(int midi, int[] tuning, DateTime time, int? stringLock = null)
    {
        // After a long pause, assume the hand went back to open position.
        if (time - lastTime > ResetAfter)
            Reset();
        lastTime = time;

        // A locked string is what the player told us; never second-guess it.
        if (stringLock is int lockedString)
        {
            if (lockedString < 0 || lockedString >= tuning.Length)
                return null;

            int fret = midi - tuning[lockedString];
            if (fret is < 0 or > MaxFret)
                return null;

            var locked = new FretPosition(lockedString, fret);
            MoveHandTo(locked);
            return locked;
        }

        var options = Positions(midi, tuning);
        if (options.Count == 0)
            return null;

        var best = options.MinBy(Cost);
        MoveHandTo(best);
        return best;
    }

    /// <summary>Tells the assigner where the hand is, e.g. after the user moves a note to another string.</summary>
    public void MoveHandTo(FretPosition position)
    {
        lastString = position.String;
        if (position.Fret == 0)
            return;

        if (position.Fret < handFret)
            handFret = position.Fret;
        else if (position.Fret > handFret + HandSpan)
            handFret = position.Fret - HandSpan;
    }

    private double Cost(FretPosition p)
    {
        // Open strings are free near the nut and get less likely the higher the hand is.
        double cost = p.Fret == 0
            ? (handFret <= OpenPosition + 1 ? 0 : 0.15 * handFret)
            : p.Fret < handFret ? handFret - p.Fret
            : p.Fret > handFret + HandSpan ? p.Fret - handFret - HandSpan
            : FrettedCost;

        if (p.Fret > 12)
            cost += 0.5;

        // Tie-breakers: stay near the previous string, then prefer lower frets.
        if (lastString is int previous)
            cost += 0.05 * Math.Abs(p.String - previous);
        return cost + 0.01 * p.Fret;
    }
}
