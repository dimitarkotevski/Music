using Music.Tabs;

namespace Music.Transcription;

/// <summary>A note placed on the neck, with its original audio timing.</summary>
public sealed record ArrangedNote(int Midi, FretPosition Position, double Start, double End);

/// <summary>All notes that start together, drawn as one vertical tab column.</summary>
public sealed record SongTabColumn(double Start, IReadOnlyList<ArrangedNote> Notes, double Confidence);

public sealed record ArrangementResult(
    IReadOnlyList<SongTabColumn> Columns,
    int DroppedNotes,
    int UnplayableColumns);

public enum ArrangementMode
{
    Solo,
    Chords,
}

/// <summary>
/// Turns a stream of transcribed notes into a physically plausible sequence of
/// string/fret positions. It uses a bounded Viterbi/beam search over onset
/// columns, so chords are voiced together instead of note by note.
/// </summary>
public sealed class FretboardArranger
{
    public const int DefaultMaxFret = 22;
    private const double OnsetTolerance = 0.06;
    private const double MinimumDuration = 0.045;
    private const int MaxNotesPerColumn = 6;
    private const int BeamWidth = 24;
    private const int MaxVoicingsPerColumn = 240;

    public ArrangementResult Arrange(
        IReadOnlyList<TranscribedNote> notes,
        int[] tuning,
        int maxFret = DefaultMaxFret,
        ArrangementMode mode = ArrangementMode.Chords)
    {
        int lowest = tuning.Min();
        int highest = tuning.Max() + maxFret;
        var playable = notes
            .Where(n => n.Midi >= lowest && n.Midi <= highest)
            .Where(n => n.End - n.Start >= MinimumDuration)
            .Where(n => n.Amplitude > 0.005)
            .ToList();
        var usable = MergeSustainedFragments(playable);

        int dropped = notes.Count - playable.Count;
        var groups = GroupByOnset(usable);
        var layers = new List<List<Voicing>>();
        int unplayable = 0;

        foreach (var rawGroup in groups)
        {
            var group = CleanGroup(rawGroup);
            if (mode == ArrangementMode.Solo && group.Count > 1)
                group = [group.OrderByDescending(n => n.Amplitude).First()];
            if (group.Count == 0)
            {
                unplayable++;
                continue;
            }

            var voicings = BuildVoicings(group, tuning, maxFret);
            if (voicings.Count == 0)
            {
                unplayable += group.Count;
                continue;
            }

            layers.Add(voicings);
        }

        if (layers.Count == 0)
            return new ArrangementResult([], dropped, unplayable);

        var states = layers[0]
            .Select(v => new PathState(v, v.Cost, [v]))
            .OrderBy(s => s.Cost)
            .Take(BeamWidth)
            .ToList();

        for (int layer = 1; layer < layers.Count; layer++)
        {
            var next = new List<PathState>();
            double gap = layers[layer][0].Start - layers[layer - 1][0].Start;

            foreach (var previous in states)
            {
                foreach (var voicing in layers[layer])
                {
                    double cost = previous.Cost + Transition(previous.Voicing, voicing, gap);
                    next.Add(new PathState(voicing, cost, [.. previous.Path, voicing]));
                }
            }

            states = next
                .OrderBy(s => s.Cost)
                .Take(BeamWidth)
                .ToList();

            if (states.Count == 0)
                break;
        }

        var best = states.OrderBy(s => s.Cost).First();
        var columns = new List<SongTabColumn>(best.Path.Count);
        for (int i = 0; i < best.Path.Count; i++)
        {
            var voicing = best.Path[i];
            double confidence = Confidence(i, voicing, best.Path, layers);
            var arranged = new List<ArrangedNote>(voicing.Notes.Count);
            for (int n = 0; n < voicing.Notes.Count; n++)
            {
                var note = voicing.Notes[n];
                arranged.Add(new ArrangedNote(
                    note.Midi,
                    voicing.Positions[n],
                    note.Start,
                    note.End));
            }

            columns.Add(new SongTabColumn(voicing.Start, arranged, confidence));
        }

        return new ArrangementResult(columns, dropped, unplayable);
    }

    /// <summary>
    /// Live version of <see cref="Arrange"/>: places one new column right after the column already written.
    /// Earlier columns are never revised, so this is the single next step of the same search, with the
    /// same voicing and hand-movement costs.
    /// </summary>
    /// <param name="previous">The last column on the tab (as the player may have corrected it), or null.</param>
    /// <param name="positionCost">Optional extra cost for playing a pitch at a position, e.g. from the learned string sound.</param>
    public SongTabColumn? ArrangeNext(
        SongTabColumn? previous,
        IReadOnlyList<TranscribedNote> notes,
        int[] tuning,
        int maxFret = DefaultMaxFret,
        ArrangementMode mode = ArrangementMode.Chords,
        Func<int, FretPosition, double>? positionCost = null)
    {
        int lowest = tuning.Min();
        int highest = tuning.Max() + maxFret;
        var group = CleanGroup(notes.Where(n => n.Midi >= lowest && n.Midi <= highest).ToList());
        if (mode == ArrangementMode.Solo && group.Count > 1)
            group = [group.OrderByDescending(n => n.Amplitude).First()];
        if (group.Count == 0)
            return null;

        var voicings = BuildVoicings(group, tuning, maxFret);

        // More notes than one hand can hold (an overtone the model kept, or a ringing string): keep the
        // loudest notes that still make a playable shape.
        while (voicings.Count == 0 && group.Count > 1)
        {
            group = group.OrderByDescending(n => n.Amplitude).Take(group.Count - 1).OrderBy(n => n.Midi).ToList();
            voicings = BuildVoicings(group, tuning, maxFret);
        }

        if (voicings.Count == 0)
            return null;

        Voicing? before = null;
        if (previous is { Notes.Count: > 0 })
        {
            var previousNotes = previous.Notes
                .Select(n => new TranscribedNote(n.Start, n.End, n.Midi))
                .ToList();
            before = MakeVoicing(previousNotes, previous.Notes.Select(n => n.Position).ToArray())
                ?? new Voicing(previousNotes, previous.Notes.Select(n => n.Position).ToArray(), 0,
                    previous.Notes.Where(n => n.Position.Fret > 0).Select(n => n.Position.Fret).DefaultIfEmpty(0).Min(),
                    previous.Notes.Max(n => n.Position.Fret), previous.Notes.Count(n => n.Position.Fret == 0), previous.Start);
        }

        var scored = voicings
            .Select(v => (Voicing: v, Cost: v.Cost
                + (before is null ? 0 : Transition(before, v, v.Start - before.Start))
                + (positionCost is null ? 0 : v.Notes.Select((n, i) => positionCost(n.Midi, v.Positions[i])).Sum())))
            .OrderBy(x => x.Cost)
            .ToList();

        var best = scored[0];
        double confidence = scored.Count == 1
            ? 1
            : Math.Clamp(1 - Math.Exp(-Math.Max(0, scored[1].Cost - best.Cost) / 0.7), 0, 1);

        var arranged = best.Voicing.Notes
            .Select((note, i) => new ArrangedNote(note.Midi, best.Voicing.Positions[i], note.Start, note.End))
            .ToList();
        return new SongTabColumn(best.Voicing.Start, arranged, confidence);
    }

    private static List<TranscribedNote> MergeSustainedFragments(List<TranscribedNote> notes)
    {
        var merged = new List<TranscribedNote>();
        foreach (var pitchGroup in notes.GroupBy(n => n.Midi))
        {
            var ordered = pitchGroup.OrderBy(n => n.Start).ToList();
            var current = ordered[0];
            for (int i = 1; i < ordered.Count; i++)
            {
                var next = ordered[i];
                // Basic Pitch sometimes splits one ringing note into several
                // back-to-back events. Only bridge a very small gap.
                if (next.Start <= current.End + 0.06)
                {
                    current = current with
                    {
                        End = Math.Max(current.End, next.End),
                        Amplitude = Math.Max(current.Amplitude, next.Amplitude),
                    };
                }
                else
                {
                    merged.Add(current);
                    current = next;
                }
            }

            merged.Add(current);
        }

        return merged
            .OrderBy(n => n.Start)
            .ThenBy(n => n.Midi)
            .ToList();
    }

    private static List<List<TranscribedNote>> GroupByOnset(List<TranscribedNote> notes)
    {
        var groups = new List<List<TranscribedNote>>();
        List<TranscribedNote>? current = null;
        double currentStart = 0;

        foreach (var note in notes)
        {
            if (current is null || note.Start - currentStart > OnsetTolerance)
            {
                current = [];
                groups.Add(current);
                currentStart = note.Start;
            }

            current.Add(note);
        }

        return groups;
    }

    private static List<TranscribedNote> CleanGroup(List<TranscribedNote> group)
    {
        var byPitch = group
            .GroupBy(n => n.Midi)
            .Select(g => g.OrderByDescending(n => n.Amplitude).First())
            .OrderBy(n => n.Midi)
            .ToList();

        // Basic Pitch often reports a quiet octave/12th as a second voice. When a
        // column has many notes, remove only weak exact-octave shadows.
        if (byPitch.Count > 3)
        {
            var kept = new List<TranscribedNote>();
            foreach (var note in byPitch)
            {
                bool weakOvertone = byPitch.Any(lower =>
                    lower.Midi < note.Midi
                    && note.Midi - lower.Midi is 12 or 19 or 24 or 28 or 31
                    && note.Amplitude < lower.Amplitude * 0.55);
                if (!weakOvertone)
                    kept.Add(note);
            }

            byPitch = kept;
        }

        if (byPitch.Count > MaxNotesPerColumn)
            byPitch = byPitch.OrderByDescending(n => n.Amplitude).Take(MaxNotesPerColumn).OrderBy(n => n.Midi).ToList();

        return byPitch;
    }

    private static List<Voicing> BuildVoicings(List<TranscribedNote> notes, int[] tuning, int maxFret)
    {
        var candidates = new List<FretPosition>[notes.Count];
        for (int i = 0; i < notes.Count; i++)
        {
            candidates[i] = Positions(notes[i].Midi, tuning, maxFret);
            if (candidates[i].Count == 0)
                return [];
        }

        var positions = new FretPosition[notes.Count];
        var results = new List<Voicing>();
        Build(0, 0, positions);

        return results
            .OrderBy(v => v.Cost)
            .Take(MaxVoicingsPerColumn)
            .ToList();

        void Build(int index, int usedStrings, FretPosition[] chosen)
        {
            if (index == notes.Count)
            {
                var voicing = MakeVoicing(notes, chosen);
                if (voicing is not null)
                    results.Add(voicing);
                return;
            }

            foreach (var candidate in candidates[index])
            {
                int bit = 1 << candidate.String;
                if ((usedStrings & bit) != 0)
                    continue;

                chosen[index] = candidate;
                Build(index + 1, usedStrings | bit, chosen);
            }
        }
    }

    private static Voicing? MakeVoicing(List<TranscribedNote> notes, FretPosition[] positions)
    {
        var fretted = positions.Where(p => p.Fret > 0).ToList();
        int minFret = fretted.Count == 0 ? 0 : fretted.Min(p => p.Fret);
        int maxFret = fretted.Count == 0 ? 0 : fretted.Max(p => p.Fret);
        int span = maxFret - minFret;

        // A hand can cover roughly four frets; open strings are free, so only the
        // fretted span is constrained.
        if (notes.Count > 1 && span > 5)
            return null;
        if (fretted.Count > 4 && span > 4)
            return null;

        int stringSpread = positions.Max(p => p.String) - positions.Min(p => p.String);
        double cost = 0.10 * positions.Sum(p => p.Fret);
        cost += 0.08 * span;
        cost += 0.03 * stringSpread;
        cost -= 0.025 * positions.Count(p => p.Fret == 0);
        if (fretted.Count > 4)
            cost += 0.35;
        if (maxFret > 12)
            cost += 0.25 * (maxFret - 12);

        return new Voicing(
            notes,
            positions.ToArray(),
            cost,
            minFret,
            maxFret,
            positions.Count(p => p.Fret == 0),
            notes[0].Start);
    }

    private static double Transition(Voicing previous, Voicing current, double gap)
    {
        int handMove = Math.Abs(current.HandFret - previous.HandFret);
        double cost = 0.9 * handMove;

        // Average distance from every current note to the nearest string used before.
        double stringDistance = 0;
        foreach (var position in current.Positions)
        {
            int nearest = previous.Positions.Min(p => Math.Abs(p.String - position.String));
            stringDistance += nearest;
        }

        cost += 0.12 * (stringDistance / Math.Max(1, current.Positions.Count));

        if (gap < 0.18)
            cost += 1.2 * Math.Max(0, handMove - 2);
        else if (gap > 1.0)
            cost *= 0.55;

        if (current.OpenCount > 0 && current.HandFret > 2)
            cost += 0.15 * (current.HandFret - 2) * current.OpenCount;

        return cost;
    }

    private static double Confidence(
        int index,
        Voicing chosen,
        IReadOnlyList<Voicing> path,
        IReadOnlyList<List<Voicing>> layers)
    {
        if (layers[index].Count <= 1)
            return 1;

        double chosenCost = index == 0
            ? chosen.Cost
            : path[index - 1].Cost + Transition(path[index - 1], chosen, chosen.Start - path[index - 1].Start);

        double bestOther = double.MaxValue;
        foreach (var option in layers[index])
        {
            if (ReferenceEquals(option, chosen))
                continue;

            double cost = index == 0
                ? option.Cost
                : path[index - 1].Cost + Transition(path[index - 1], option, option.Start - path[index - 1].Start);
            if (cost < bestOther)
                bestOther = cost;
        }

        if (bestOther == double.MaxValue)
            return 1;

        double margin = Math.Max(0, bestOther - chosenCost);
        return Math.Clamp(1 - Math.Exp(-margin / 0.7), 0, 1);
    }

    private static List<FretPosition> Positions(int midi, int[] tuning, int maxFret)
    {
        var result = new List<FretPosition>();
        for (int stringIndex = 0; stringIndex < tuning.Length; stringIndex++)
        {
            int fret = midi - tuning[stringIndex];
            if (fret is >= 0 and <= DefaultMaxFret && fret <= maxFret)
                result.Add(new FretPosition(stringIndex, fret));
        }

        return result
            .OrderBy(p => p.Fret)
            .ThenBy(p => p.String)
            .ToList();
    }

    private sealed record PathState(Voicing Voicing, double Cost, IReadOnlyList<Voicing> Path);

    private sealed record Voicing(
        IReadOnlyList<TranscribedNote> Notes,
        IReadOnlyList<FretPosition> Positions,
        double Cost,
        int HandFret,
        int MaxFret,
        int OpenCount,
        double Start);
}
