using Music.Tuning;

namespace Music.Tabs;

public readonly record struct DetectedNote(int Midi, double Frequency);

/// <summary>
/// Turns a stream of pitch readings into separate notes: one per pluck (re-plucking the same note
/// included), plus pitch changes without a pluck such as hammer-ons and slides.
/// </summary>
/// <remarks>
/// Low strings are hard: at the start of a pluck, and again as the note fades (laptop mics barely pick
/// up low fundamentals), the detector often locks onto an overtone such as the octave. So after a pluck
/// a few readings are collected and the lowest note that explains them is chosen, and while a note
/// rings, readings that are its overtones are treated as the same note.
/// </remarks>
public sealed class NoteTracker
{
    // A pluck makes the level jump above the decaying envelope of the previous note...
    private const double AttackRatio = 1.4;
    private const double EnvelopeDecay = 0.85;

    // ...or rise sharply since the previous reading (a quick re-pluck of a ringing string)...
    private const double RiseRatio = 1.25;
    private const double RiseEnvelopeRatio = 1.15;

    // ...and, while a note rings, reach a good part of that note's loudness (ignores bumps as it fades).
    private const double RepluckFraction = 0.35;

    // Readings collected after a pluck before deciding which note it was.
    private const int OnsetReadings = 4;
    private const int MaxOnsetBlocks = 8;

    // Readings of a new, non-overtone pitch, without a pluck, before it counts as a new note (hammer-on, slide).
    private const int LegatoReadings = 3;

    // Readings below the noise gate that end the current note.
    private const int SilentReadingsToEnd = 3;

    // Semitones from a note to its overtones (octave, octave + fifth, two octaves, …), and below it
    // to the octave errors the detector sometimes makes.
    private static readonly int[] OvertoneOffsets = [12, 19, 24, 28, 31, 36];
    private static readonly int[] UndertoneOffsets = [-12, -19, -24];

    private readonly List<(int Midi, double Frequency)> onsetReadings = [];
    private double envelope;
    private double lastLevel;
    private bool collecting;
    private int collectBlocks;
    private int? currentNote;
    private double currentPeak;
    private int? legatoCandidate;
    private int legatoCount;
    private int silentReadings;

    public double A4 { get; set; } = 440;

    /// <summary>Lowest playable note in the current tuning; lower readings are treated as octave errors.</summary>
    public int LowestMidi { get; set; }

    /// <summary>Highest playable note in the current tuning; higher readings are treated as octave errors.</summary>
    public int HighestMidi { get; set; } = 127;

    public void Reset()
    {
        envelope = lastLevel = currentPeak = 0;
        collecting = false;
        collectBlocks = legatoCount = silentReadings = 0;
        currentNote = legatoCandidate = null;
        onsetReadings.Clear();
    }

    /// <summary>Feeds one reading. Returns a note when a new one starts.</summary>
    public DetectedNote? Process(double? frequency, double level, double gate)
    {
        if (level < gate)
        {
            if (++silentReadings >= SilentReadingsToEnd)
            {
                currentNote = legatoCandidate = null;
                collecting = false;
                currentPeak = 0;
            }

            envelope = lastLevel = level;
            return null;
        }

        silentReadings = 0;
        bool rising = level > envelope * AttackRatio || (level > lastLevel * RiseRatio && level > envelope * RiseEnvelopeRatio);
        bool loudEnough = currentNote is null || level >= currentPeak * RepluckFraction;
        if (rising && loudEnough)
        {
            collecting = true;
            collectBlocks = 0;
            onsetReadings.Clear();
            currentPeak = level;
        }

        currentPeak = Math.Max(currentPeak, level);
        envelope = Math.Max(level, envelope * EnvelopeDecay);
        lastLevel = level;

        int? midi = null;
        double? heardFrequency = null;
        if (frequency is double rawFrequency)
        {
            int rawMidi = (int)Math.Round(NoteMath.FrequencyToMidi(rawFrequency, A4));
            int normalizedMidi = NormalizeToRange(rawMidi);
            heardFrequency = normalizedMidi == rawMidi
                ? rawFrequency
                : rawFrequency * Math.Pow(2, (normalizedMidi - rawMidi) / 12.0);
            midi = normalizedMidi;
        }

        // A sound that starts softly, without a clear pluck, is collected the same way.
        if (!collecting && currentNote is null && midi is not null)
        {
            collecting = true;
            collectBlocks = 0;
            onsetReadings.Clear();
        }

        if (collecting)
            return CollectOnset(midi, heardFrequency);

        if (midi is not int heard || currentNote is not int current)
            return null;

        // Overtones (or octave errors) of the ringing note are still that note.
        int offset = heard - current;
        if (offset == 0 || OvertoneOffsets.Contains(offset) || UndertoneOffsets.Contains(offset))
        {
            legatoCandidate = null;
            legatoCount = 0;
            return null;
        }

        legatoCount = heard == legatoCandidate ? legatoCount + 1 : 1;
        legatoCandidate = heard;
        if (legatoCount < LegatoReadings)
            return null;

        currentNote = heard;
        legatoCandidate = null;
        return new DetectedNote(heard, heardFrequency!.Value);
    }

    /// <summary>
    /// Folds a reading into the tuning's range by whole octaves. This fixes the detector's occasional
    /// subharmonic (an A string read as A1) without guessing frets: the tab can't use notes below the
    /// lowest open string anyway.
    /// </summary>
    private int NormalizeToRange(int midi)
    {
        while (midi < LowestMidi && midi + 12 <= 127)
            midi += 12;
        while (midi > HighestMidi && midi - 12 >= 0)
            midi -= 12;
        return midi;
    }

    private DetectedNote? CollectOnset(int? midi, double? frequency)
    {
        collectBlocks++;
        if (midi is int m)
            onsetReadings.Add((m, frequency!.Value));

        bool enough = onsetReadings.Count >= OnsetReadings || (collectBlocks >= MaxOnsetBlocks && onsetReadings.Count >= 2);
        if (!enough)
            return null;

        collecting = false;
        int note = ChooseNote();
        currentNote = note;
        legatoCandidate = null;
        double noteFrequency = onsetReadings.Where(r => r.Midi == note).Select(r => r.Frequency).DefaultIfEmpty(NoteMath.MidiToFrequency(note, A4)).Average();
        return new DetectedNote(note, noteFrequency);
    }

    /// <summary>
    /// The note that best explains the readings: each reading of the note itself counts 2, each reading of
    /// one of its overtones counts 1. Ties go to the lower note.
    /// </summary>
    private int ChooseNote() =>
        onsetReadings.Select(r => r.Midi).Distinct()
            .OrderByDescending(candidate => onsetReadings.Sum(r =>
                r.Midi == candidate ? 2 : OvertoneOffsets.Contains(r.Midi - candidate) ? 1 : 0))
            .ThenBy(candidate => candidate)
            .First();
}
