namespace AllTunning.Tabs;

/// <summary>A short tab line the player is asked to play in training.</summary>
public sealed record TrainingPhrase(string Title, IReadOnlyList<FretPosition> Notes);

/// <summary>
/// The phrases for string training. They are built for one job: giving the sound model the same
/// pitches on different strings (open A and low E fret 5, and so on), because those are exactly
/// the notes a microphone cannot place by pitch alone. Generated from the tuning, so they work in
/// any tuning.
/// </summary>
public static class TrainingPhrases
{
    private const int HighestFret = 12;

    public static IReadOnlyList<TrainingPhrase> Create(int[] tuning)
    {
        var phrases = new List<TrainingPhrase>
        {
            new("Open strings", Enumerable.Range(0, tuning.Length).Select(s => new FretPosition(s, 0)).ToList()),
        };

        // The same note on neighbouring strings, back to back: lower string fretted, upper string lower down.
        for (int s = 0; s + 1 < tuning.Length; s++)
        {
            int offset = tuning[s + 1] - tuning[s];
            var notes = new List<FretPosition>();
            foreach (int upperFret in (int[])[0, 2, 4, 7])
            {
                if (offset + upperFret > HighestFret)
                    break;
                notes.Add(new FretPosition(s, offset + upperFret));
                notes.Add(new FretPosition(s + 1, upperFret));
            }

            if (notes.Count >= 4)
                phrases.Add(new($"Same notes on {TabText.StringName(tuning, s)} and {TabText.StringName(tuning, s + 1)}", notes));
        }

        // The same note two strings apart (for example E4 as open e and as B string fret 5).
        for (int s = 0; s + 2 < tuning.Length; s++)
        {
            int offset = tuning[s + 2] - tuning[s];
            var notes = new List<FretPosition>();
            foreach (int upperFret in (int[])[0, 2, 3])
            {
                if (offset + upperFret > HighestFret)
                    break;
                notes.Add(new FretPosition(s, offset + upperFret));
                notes.Add(new FretPosition(s + 2, upperFret));
            }

            if (notes.Count >= 4)
                phrases.Add(new($"Same notes on {TabText.StringName(tuning, s)} and {TabText.StringName(tuning, s + 2)}", notes));
        }

        // Up each string, so every string has notes along the neck.
        for (int s = 0; s < tuning.Length; s++)
        {
            var notes = ((int[])[0, 3, 5, 7, 9, 12]).Select(f => new FretPosition(s, f)).ToList();
            phrases.Add(new($"Up {TabText.StringName(tuning, s)}", notes));
        }

        return phrases;
    }
}
