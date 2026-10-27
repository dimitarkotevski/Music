namespace Music.Tabs;

/// <summary>A place on the neck. String 0 is the lowest string, matching <see cref="Music.Tuning.GuitarTuning.Strings"/>.</summary>
public readonly record struct FretPosition(int String, int Fret);

/// <summary>A position guess together with how clearly it won over the alternatives (0–1).</summary>
public readonly record struct FretEstimate(FretPosition Position, double Confidence);

/// <summary>
/// Chooses which string a note was played on. Audio alone only gives the pitch, so this picks the
/// position a guitarist would most likely use: near the last fretted note, on the same or a nearby
/// string, with a preference for open strings while the hand is near the nut. A timbre clue and the
/// distance between candidates decide how confident the result is.
/// </summary>
public sealed class FretAssigner
{
    public const int MaxFret = 22;
    private const int HandSpan = 3; // frets within this distance of the last note feel like the same position
    private const int OpenPosition = 1;
    private const double FrettedCost = 0.1; // pressing a fret is a little more effort than an open string
    private const double OpenPenaltyPerFret = 0.15; // an open string is unlikely once the hand is above the nut
    private const double StringChangeCost = 0.07; // crossing strings is normal, but staying is a little easier
    private const double SameNoteStayReward = 0.2;
    private const double SameNoteMoveCost = 0.25;
    private const double FastJumpCost = 0.1;
    private const double TimbreWeight = 0.06; // only a tie-breaker; a microphone cannot prove a string from timbre alone
    private static readonly TimeSpan ResetAfter = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RepeatedNoteWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FastPassage = TimeSpan.FromMilliseconds(180);

    private int? lastFret;
    private int? lastString;
    private int? lastMidi;
    private FretPosition? lastPosition;
    private DateTime lastTime = DateTime.MinValue;
    private TimeSpan lastGap = TimeSpan.MaxValue;

    /// <summary>Every position where the note can be played, lowest fret first.</summary>
    public static IReadOnlyList<FretPosition> Positions(int midi, int[] tuning) =>
        Enumerable.Range(0, tuning.Length)
            .Select(s => new FretPosition(s, midi - tuning[s]))
            .Where(p => p.Fret is >= 0 and <= MaxFret)
            .OrderBy(p => p.Fret)
            .ToList();

    public void Reset()
    {
        lastFret = null;
        lastString = null;
        lastMidi = null;
        lastPosition = null;
        lastTime = DateTime.MinValue;
        lastGap = TimeSpan.MaxValue;
    }

    /// <summary>Returns the most likely position and a confidence value.</summary>
    public FretEstimate? Estimate(
        int midi,
        int[] tuning,
        DateTime time,
        int? stringLock = null,
        double? brightness = null)
    {
        TimeSpan gap = lastTime == DateTime.MinValue ? TimeSpan.MaxValue : time - lastTime;
        if (gap > ResetAfter)
        {
            Reset();
            gap = TimeSpan.MaxValue;
        }

        lastGap = gap;
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
            Remember(locked, midi);
            return new FretEstimate(locked, 1);
        }

        var options = Positions(midi, tuning);
        if (options.Count == 0)
            return null;

        var scored = options
            .Select(position => (Position: position, Cost: Cost(position, midi, brightness)))
            .OrderBy(x => x.Cost)
            .ThenBy(x => x.Position.Fret)
            .ThenBy(x => x.Position.String)
            .ToList();

        var best = scored[0];
        bool repeatedSamePosition = lastMidi == midi && lastPosition == best.Position;
        Remember(best.Position, midi);

        // The margin is the honest part of the answer. Two positions with almost the same cost
        // are not a confident tab; the UI marks that note as uncertain.
        double confidence = 1;
        if (scored.Count > 1)
        {
            double margin = Math.Max(0, scored[1].Cost - best.Cost);
            confidence = 1 - Math.Exp(-margin / 0.35);
            if (brightness is null)
                confidence *= 0.85;
            if (repeatedSamePosition)
                confidence = Math.Min(1, confidence + 0.1);
            confidence = Math.Clamp(confidence, 0, 0.99);
        }

        return new FretEstimate(best.Position, confidence);
    }

    /// <summary>Compatibility helper for callers that only need the position.</summary>
    public FretPosition? Assign(
        int midi,
        int[] tuning,
        DateTime time,
        int? stringLock = null,
        double? brightness = null) =>
        Estimate(midi, tuning, time, stringLock, brightness)?.Position;

    /// <summary>Tells the assigner where the hand is, e.g. after the user moves a note to another string.</summary>
    public void MoveHandTo(FretPosition position, int? midi = null)
    {
        lastString = position.String;
        lastPosition = position;
        lastMidi = midi;
        if (position.Fret > 0)
            lastFret = position.Fret;
    }

    private void Remember(FretPosition position, int midi) => MoveHandTo(position, midi);

    private double Cost(FretPosition p, int midi, double? brightness)
    {
        double cost;
        if (p.Fret == 0)
        {
            // Open strings are free near the nut. Once the hand moves up, reaching back to an
            // open string becomes less likely, so prefer a fretted note near the current position.
            cost = lastFret is int hand && hand > OpenPosition + 1
                ? OpenPenaltyPerFret * (hand - OpenPosition - 1)
                : 0;
        }
        else
        {
            int reference = lastFret ?? OpenPosition;
            int distance = Math.Abs(p.Fret - reference);
            cost = FrettedCost + Math.Max(0, distance - HandSpan);
            cost += 0.01 * p.Fret;
        }

        if (p.Fret > 12)
            cost += 0.5;

        // A repeated pitch is usually the same string and fret, not a jump across the neck.
        if (lastPosition is FretPosition previous && midi == lastMidi && lastGap <= RepeatedNoteWindow)
            cost += p == previous ? -SameNoteStayReward : SameNoteMoveCost;

        // In a fast line, a large fret jump is less likely than a nearby alternative.
        if (lastPosition is FretPosition handPosition && lastGap < FastPassage)
            cost += FastJumpCost * Math.Max(0, Math.Abs(p.Fret - handPosition.Fret) - 2);

        // Timbre is deliberately weak: it can separate two otherwise equal candidates, but it
        // must not overrule continuity. The average harmonic number tends to rise with a thinner
        // string and a shorter vibrating length.
        if (brightness is double observed && observed > 0)
        {
            double expected = 1.8 + 0.08 * p.String + 0.01 * p.Fret;
            cost += TimbreWeight * Math.Min(2.5, Math.Abs(observed - expected));
        }

        // Tie-breakers: stay near the previous string, then prefer lower frets.
        if (lastString is int previousString)
            cost += StringChangeCost * Math.Abs(p.String - previousString);
        return cost;
    }
}
