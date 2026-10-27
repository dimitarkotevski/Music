using System.IO;
using System.Text.Json;

namespace Music.Tabs;

/// <summary>One labelled note from a training phrase: what was played, and how it sounded.</summary>
/// <param name="OpenMidi">The open string's pitch when it was recorded, so data from another tuning is not mixed in.</param>
/// <param name="Features">The <see cref="LiveNoteModel.SoundProfile"/> of the note.</param>
public sealed record StringSoundExample(
    int Midi,
    int String,
    int Fret,
    int OpenMidi,
    double[] Features,
    string Session,
    double Time);

/// <summary>
/// Learns how each string of the player's own guitar sounds through their own microphone, from notes
/// recorded in training. A microphone only hears pitch, so open A and low E fret 5 are the same note;
/// what differs is the timbre, and that differs per guitar and mic, which is why it is learned rather
/// than built in. It is a nearest-neighbour model: a new note is compared with the recorded notes of
/// similar pitch on each candidate string.
/// <para>
/// The model also measures itself (leave-one-out on the training data) and only gets a say in the
/// string choice in proportion to how much better than guessing it turned out to be.
/// </para>
/// </summary>
public sealed class StringTimbreModel
{
    // Timbre changes along the neck, so only notes this close in pitch are compared.
    private const int PitchRadius = 3;
    private const int Neighbours = 3;

    // Below this many testable notes, the measured accuracy means little and the model stays silent.
    private const int MinimumEvaluated = 20;

    // The largest cost the model can add for a string it thinks is wrong (an ordinary hand shift costs
    // 0.9 per fret in the arranger), reached only by a model that is always right.
    private const double MaxWeight = 2.0;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly List<StringSoundExample> examples = [];
    private readonly List<double[]> standardizedExamples = [];
    private double[] mean = [];

    // How quickly probability falls off with distance; fitted on the training data in Retrain.
    private double temperature = 0.3;
    private double[] scale = [];

    /// <summary>Desktop\GuitarTabsTest\training\string-sounds.json, next to the other test recordings.</summary>
    public static string DefaultPath
    {
        get
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (string.IsNullOrEmpty(desktop))
                desktop = AppContext.BaseDirectory;
            return Path.Combine(desktop, "GuitarTabsTest", "training", "string-sounds.json");
        }
    }

    public int Count => examples.Count;

    /// <summary>Notes where at least two strings had data, i.e. where the model could be tested.</summary>
    public int Evaluated { get; private set; }

    /// <summary>How often it picked the right string on those notes (leave-one-out).</summary>
    public double Accuracy { get; private set; }

    /// <summary>How often a blind guess between the same candidate strings would be right.</summary>
    public double Chance { get; private set; }

    /// <summary>How much the model may influence the string choice; 0 until it has proven itself.</summary>
    public double Weight { get; private set; }

    public static StringTimbreModel Load(string path)
    {
        var model = new StringTimbreModel();
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<List<StringSoundExample>>(File.ReadAllText(path)) is { } saved)
                model.examples.AddRange(saved.Where(e => e.Features is { Length: > 0 }));
        }
        catch
        {
            // A damaged file only means starting again; training writes a fresh one.
        }

        model.Retrain();
        return model;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(examples, JsonOptions));
    }

    public void Add(IEnumerable<StringSoundExample> newExamples)
    {
        examples.AddRange(newExamples);
        Retrain();
    }

    /// <summary>Replaces all examples of one session, so saving the same tab twice does not count it twice.</summary>
    public void ReplaceSession(string session, IEnumerable<StringSoundExample> newExamples)
    {
        examples.RemoveAll(e => e.Session == session);
        Add(newExamples);
    }

    /// <summary>
    /// Probability that the note was played on each string where it fits, or null when fewer than two of
    /// those strings have training data near this pitch (then there is nothing to compare).
    /// </summary>
    public IReadOnlyDictionary<int, double>? Probabilities(int midi, double[] features, int[] tuning, int maxFret = FretAssigner.MaxFret)
    {
        if (examples.Count == 0 || features.Length != mean.Length)
            return null;

        var standardized = Standardize(features);
        var candidates = Enumerable.Range(0, tuning.Length)
            .Where(s => midi - tuning[s] is >= 0 && midi - tuning[s] <= maxFret)
            .ToList();

        var scores = new Dictionary<int, double>();
        foreach (int s in candidates)
        {
            if (Score(s, tuning[s], midi, standardized, exclude: null) is double score)
                scores[s] = score;
        }

        if (scores.Count < 2)
            return null;

        // Closer to a string's recorded notes = more likely; strings without data stay neutral.
        double best = scores.Values.Min();
        var known = scores.ToDictionary(p => p.Key, p => Math.Exp(-(p.Value - best) / temperature));
        double neutral = known.Values.Average();
        var all = candidates.ToDictionary(s => s, s => known.TryGetValue(s, out double p) ? p : neutral);
        double sum = all.Values.Sum();
        return all.ToDictionary(p => p.Key, p => p.Value / sum);
    }

    private void Retrain()
    {
        int dimensions = examples.Count == 0 ? 0 : examples[0].Features.Length;
        var usable = examples.Where(e => e.Features.Length == dimensions).ToList();
        examples.Clear();
        examples.AddRange(usable);

        mean = new double[dimensions];
        scale = new double[dimensions];
        for (int d = 0; d < dimensions; d++)
        {
            mean[d] = examples.Average(e => e.Features[d]);
            double sd = Math.Sqrt(examples.Average(e => (e.Features[d] - mean[d]) * (e.Features[d] - mean[d])));
            scale[d] = sd > 1e-9 ? sd : 1;
        }

        standardizedExamples.Clear();
        standardizedExamples.AddRange(examples.Select(e => Standardize(e.Features)));

        // Leave-one-out: hide each note, ask the model which string it was on.
        int evaluated = 0, correct = 0;
        double chance = 0;
        var tests = new List<(double Truth, double[] Scores)>();
        for (int i = 0; i < examples.Count; i++)
        {
            var example = examples[i];
            var standardized = standardizedExamples[i];
            var scores = examples
                .Where(e => example.Midi - e.OpenMidi is >= 0 and <= FretAssigner.MaxFret)
                .Select(e => (e.String, e.OpenMidi))
                .Distinct()
                .Select(s => (s.String, Score: Score(s.String, s.OpenMidi, example.Midi, standardized, example)))
                .Where(s => s.Score is not null)
                .ToList();

            if (scores.Count < 2 || scores.All(s => s.String != example.String))
                continue;

            evaluated++;
            chance += 1.0 / scores.Count;
            tests.Add((scores.First(s => s.String == example.String).Score!.Value, scores.Select(s => s.Score!.Value).ToArray()));
            if (scores.MinBy(s => s.Score!.Value).String == example.String)
                correct++;
        }

        // Pick the sharpness that gives the true strings the highest probability (calibration), so a model
        // that is usually right speaks up clearly and an unsure one stays mild.
        if (tests.Count > 0)
        {
            temperature = ((double[])[0.03, 0.05, 0.08, 0.12, 0.2, 0.3, 0.5, 0.8, 1.2]).MaxBy(tau => tests.Sum(test =>
            {
                double best = test.Scores.Min();
                double total = test.Scores.Sum(score => Math.Exp(-(score - best) / tau));
                return Math.Log(Math.Exp(-(test.Truth - best) / tau) / total);
            }));
        }

        Evaluated = evaluated;
        Accuracy = evaluated == 0 ? 0 : (double)correct / evaluated;
        Chance = evaluated == 0 ? 0 : chance / evaluated;
        Weight = evaluated >= MinimumEvaluated && Chance < 1
            ? MaxWeight * Math.Max(0, (Accuracy - Chance) / (1 - Chance))
            : 0;
    }

    /// <summary>Mean distance to the closest recorded notes of similar pitch on one string; null without data.</summary>
    private double? Score(int stringIndex, int openMidi, int midi, double[] standardized, StringSoundExample? exclude)
    {
        var distances = Enumerable.Range(0, examples.Count)
            .Where(i => !ReferenceEquals(examples[i], exclude)
                && examples[i].String == stringIndex
                && examples[i].OpenMidi == openMidi
                && Math.Abs(examples[i].Midi - midi) <= PitchRadius)
            .Select(i => Distance(standardized, standardizedExamples[i]))
            .OrderBy(d => d)
            .Take(Neighbours)
            .ToList();

        return distances.Count == 0 ? null : distances.Average();
    }

    private double[] Standardize(double[] features)
    {
        var result = new double[features.Length];
        for (int d = 0; d < features.Length; d++)
            result[d] = (features[d] - mean[d]) / scale[d];
        return result;
    }

    private static double Distance(double[] a, double[] b)
    {
        double sum = 0;
        for (int d = 0; d < a.Length; d++)
            sum += (a[d] - b[d]) * (a[d] - b[d]);
        return Math.Sqrt(sum / a.Length);
    }
}
