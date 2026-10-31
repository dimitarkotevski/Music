using AllTunning.Tuning;

namespace AllTunning.Tabs;

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
    private const double RepluckFraction = 0.2;

    // Readings collected after a pluck before deciding which note it was.
    private const int OnsetReadings = 4;
    private const int MaxOnsetBlocks = 8;

    // Just after a note, ignore the tail of its own attack and any level wobble.
    private const int NoteCooldownBlocks = 4; // about 100 ms

    // After a new note wins, ignore the old string's fading readings for a moment.
    private const int PriorNoteBlocks = 80; // about two seconds

    // A strummed low string can ring through several string changes. Keep the recent notes
    // in a small set so the old drone cannot become a new note again and again.
    private const int RingingBlocks = 160; // about four seconds

    // Re-plucking the same note must be a real attack, not a level wobble on the ringing string.
    private const double SameNoteAttackRatio = 1.8;

    // Readings of a new, non-overtone pitch, without a pluck, before it counts as a new note (hammer-on, slide).
    private const int LegatoReadings = 3;


    // Readings below the noise gate that end the current note.
    private const int SilentReadingsToEnd = 3;

    // Semitones from a note to its overtones (octave, octave + fifth, two octaves, …), and below it
    // to the octave errors the detector sometimes makes.
    private static readonly int[] OvertoneOffsets = [12, 19, 24, 28, 31, 36];
    private static readonly int[] UndertoneOffsets = [-12, -19, -24];

    private readonly List<(int Midi, double Frequency, int? SpectralMidi)> onsetReadings = [];
    private double envelope;
    private double lastLevel;
    private bool collecting;
    private int collectBlocks;
    private int? currentNote;
    private double currentPeak;
    private bool attackWeak;
    private int noteCooldown;
    private int? legatoCandidate;
    private int legatoCount;
    private int silentReadings;
    private bool attackOnset;
    private int? attackOldNote;
    private int? priorNote;
    private int priorCountdown;
    private double attackRise;
    private readonly List<(int Midi, int Countdown)> ringingNotes = [];

    public double A4 { get; set; } = 440;

    /// <summary>Lowest playable note in the current tuning; lower readings are treated as octave errors.</summary>
    public int LowestMidi { get; set; }

    /// <summary>Highest playable note in the current tuning; higher readings are treated as octave errors.</summary>
    public int HighestMidi { get; set; } = 127;

    /// <summary>Compact state for the temporary live-input diagnostic log.</summary>
    public string DebugState =>
        $"cur={currentNote?.ToString() ?? "-"} coll={(collecting ? 1 : 0)} prior={priorNote?.ToString() ?? "-"} "
        + $"priorN={priorCountdown} cool={noteCooldown} env={envelope:0.0000} peak={currentPeak:0.0000} weak={(attackWeak ? 1 : 0)} "
        + $"rise={attackRise:0.00} ring={string.Join(",", ringingNotes.Select(r => r.Midi))}";

    public void Reset()
    {
        envelope = lastLevel = currentPeak = 0;
        collecting = false;
        collectBlocks = legatoCount = silentReadings = 0;
        currentNote = legatoCandidate = null;
        onsetReadings.Clear();
        attackOnset = false;
        attackOldNote = null;
        attackWeak = false;
        priorNote = null;
        priorCountdown = 0;
        noteCooldown = 0;
        attackRise = 0;
        ringingNotes.Clear();
    }

    /// <summary>Feeds one reading. Returns a note when a new one starts.</summary>
    public DetectedNote? Process(
        double? frequency,
        double level,
        double gate,
        double? spectralFrequency = null)
    {
        if (level < gate)
        {
            if (++silentReadings >= SilentReadingsToEnd)
            {
                currentNote = legatoCandidate = null;
                collecting = false;
                currentPeak = 0;
                attackOnset = false;
                attackOldNote = null;
                attackWeak = false;
                priorNote = null;
                priorCountdown = 0;
                noteCooldown = 0;
                attackRise = 0;
                ringingNotes.Clear();
            }

            envelope = lastLevel = level;
            return null;
        }

        silentReadings = 0;
        DecayRinging();
        if (noteCooldown > 0)
        {
            noteCooldown--;
            envelope = Math.Max(level, envelope * EnvelopeDecay);
            lastLevel = level;
            return null;
        }

        bool rising = level > envelope * AttackRatio || (level > lastLevel * RiseRatio && level > envelope * RiseEnvelopeRatio);
        bool weakAttack = currentNote is not null && level < currentPeak * RepluckFraction;
        if (rising)
        {
            collecting = true;
            collectBlocks = 0;
            onsetReadings.Clear();
            currentPeak = level;
            attackOnset = true;
            attackOldNote = currentNote;
            attackWeak = weakAttack;
            attackRise = level / Math.Max(envelope, 1e-9);
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

        int? spectralMidi = spectralFrequency is double sf
            ? NormalizeToRange((int)Math.Round(NoteMath.FrequencyToMidi(sf, A4)))
            : null;

        // A sound that starts softly, without a clear pluck, is collected the same way.
        if (!collecting && currentNote is null && midi is not null)
        {
            collecting = true;
            collectBlocks = 0;
            onsetReadings.Clear();
            attackOnset = false;
            attackOldNote = null;
            attackWeak = false;
            attackRise = 0;
        }

        if (collecting)
            return CollectOnset(midi, heardFrequency, spectralMidi);

        if (midi is not int heard || currentNote is not int current)
            return null;

        // YIN can stay on the old string even when a new one has been plucked. If the
        // harmonic scan sees a different, plausible note, follow that instead.
        int candidate = heard;
        double candidateFrequency = heardFrequency!.Value;
        bool fromSpectral = false;
        bool yinStuck = heard == current
            || OvertoneOffsets.Contains(heard - current)
            || UndertoneOffsets.Contains(heard - current);

        int? spectralChoice = null;
        if (spectralMidi is int spectral && spectral != current && yinStuck)
        {
            int interval = spectral - current;
            bool plausible = interval is 19 or 24
                || (!OvertoneOffsets.Contains(interval) && !UndertoneOffsets.Contains(interval));
            if (plausible)
                spectralChoice = spectral;
        }

        if (spectralChoice is int chosen)
        {
            candidate = chosen;
            candidateFrequency = spectralMidi == chosen
                ? spectralFrequency!.Value
                : NoteMath.MidiToFrequency(chosen, A4);
            fromSpectral = true;
        }

        // A new note has just won; the old string can still ring for a while. Don't turn its
        // fading readings into new notes. A fresh pluck bypasses this via the onset path.
        if (priorNote is int prior && priorCountdown > 0)
        {
            priorCountdown--;
            if (candidate == prior || OvertoneOffsets.Contains(candidate - prior) || UndertoneOffsets.Contains(candidate - prior))
                return null;
        }

        // The old drone can survive several string changes. Exact readings of any recently
        // played note are still that note; its overtones are too, unless the harmonic scan
        // has found a real new note there (for example B over low E).
        if (IsRinging(candidate) || (!fromSpectral && IsOvertoneOfRinging(candidate)))
        {
            legatoCandidate = null;
            legatoCount = 0;
            return null;
        }

        // Overtones (or octave errors) of the ringing note are still that note, unless the
        // spectral scan has identified a real new note on one of those harmonics.
        int offset = candidate - current;
        bool related = offset == 0 || OvertoneOffsets.Contains(offset) || UndertoneOffsets.Contains(offset);
        if (offset == 0 || (related && !fromSpectral))
        {
            legatoCandidate = null;
            legatoCount = 0;
            return null;
        }

        legatoCount = candidate == legatoCandidate ? legatoCount + 1 : 1;
        legatoCandidate = candidate;
        if (legatoCount < LegatoReadings)
            return null;

        if (currentNote is int previous && previous != candidate)
        {
            priorNote = previous;
            priorCountdown = PriorNoteBlocks;
            RememberRinging(previous);
        }

        RemoveRinging(candidate);
        currentNote = candidate;
        legatoCandidate = null;
        noteCooldown = NoteCooldownBlocks;
        return new DetectedNote(candidate, candidateFrequency);
    }

    private void DecayRinging()
    {
        for (int i = ringingNotes.Count - 1; i >= 0; i--)
        {
            var note = ringingNotes[i];
            if (note.Countdown <= 1)
                ringingNotes.RemoveAt(i);
            else
                ringingNotes[i] = (note.Midi, note.Countdown - 1);
        }
    }

    private void RememberRinging(int midi)
    {
        for (int i = 0; i < ringingNotes.Count; i++)
        {
            if (ringingNotes[i].Midi == midi)
            {
                ringingNotes[i] = (midi, RingingBlocks);
                return;
            }
        }

        ringingNotes.Add((midi, RingingBlocks));
        if (ringingNotes.Count > 6)
            ringingNotes.RemoveAt(0);
    }

    private void RemoveRinging(int midi) => ringingNotes.RemoveAll(r => r.Midi == midi);

    private bool IsRinging(int midi) => ringingNotes.Any(r => r.Midi == midi);

    private bool IsOvertoneOfRinging(int midi) => ringingNotes.Any(r =>
        OvertoneOffsets.Contains(midi - r.Midi) || UndertoneOffsets.Contains(midi - r.Midi));

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

    private DetectedNote? CollectOnset(int? midi, double? frequency, int? spectralMidi)
    {
        collectBlocks++;
        if (midi is int m)
            onsetReadings.Add((m, frequency!.Value, spectralMidi));

        bool enough = onsetReadings.Count >= OnsetReadings || (collectBlocks >= MaxOnsetBlocks && onsetReadings.Count >= 2);
        if (!enough)
            return null;

        collecting = false;
        int? oldNote = attackOnset ? attackOldNote : null;
        bool weak = attackWeak;
        attackOnset = false;
        attackOldNote = null;
        attackWeak = false;
        int note = ChooseNote(oldNote);

        // A note that is already sounding, or one that is still ringing from an earlier
        // pluck, must not return on a level wobble. It needs a genuinely strong attack;
        // otherwise the old drone would make the tab jump back to it after every change.
        bool returning = IsRinging(note) || currentNote == note;
        if (returning && attackRise < SameNoteAttackRatio)
        {
            legatoCandidate = null;
            legatoCount = 0;
            return null;
        }

        // The previous string's own tail came back as an onset; keep the note we just switched to.
        if (priorNote is int prior && priorCountdown > 0 && note == prior)
        {
            legatoCandidate = null;
            legatoCount = 0;
            return null;
        }

        // A small bump on the ringing string isn't a new pluck. A genuinely new, different
        // note is still accepted even when it is quieter than the old peak.
        if (weak && oldNote is int old && note == old)
        {
            currentNote = old;
            legatoCandidate = null;
            legatoCount = 0;
            return null;
        }

        if (currentNote is int previous && previous != note)
        {
            priorNote = previous;
            priorCountdown = PriorNoteBlocks;
            RememberRinging(previous);
        }
        else
        {
            priorNote = null;
            priorCountdown = 0;
        }

        RemoveRinging(note);
        currentNote = note;
        legatoCandidate = null;
        noteCooldown = NoteCooldownBlocks;
        double noteFrequency = onsetReadings.Where(r => r.Midi == note).Select(r => r.Frequency).DefaultIfEmpty(NoteMath.MidiToFrequency(note, A4)).Average();
        return new DetectedNote(note, noteFrequency);
    }

    /// <summary>
    /// The note that best explains the readings: each reading of the note itself counts 2, each reading of
    /// one of its overtones counts 1. Ties go to the lower note. When a pluck overlaps a ringing note and
    /// YIN is still stuck on the old pitch, a consistent spectral candidate that is not an overtone of the
    /// old note wins instead.
    /// </summary>
    private int ChooseNote(int? oldNote)
    {
        int yinChoice = onsetReadings.Select(r => r.Midi).Distinct()
            .OrderByDescending(candidate => onsetReadings.Sum(r =>
                r.Midi == candidate ? 2 : OvertoneOffsets.Contains(r.Midi - candidate) ? 1 : 0))
            .ThenBy(candidate => candidate)
            .First();

        if (oldNote is not int old)
            return yinChoice;

        var spectral = onsetReadings
            .Select((reading, index) => (reading, index))
            .Where(x => x.reading.SpectralMidi is not null)
            .GroupBy(x => x.reading.SpectralMidi!.Value)
            .Select(g => (Midi: g.Key, Count: g.Count(), Last: g.Max(x => x.index)))
            .OrderByDescending(x => x.Count)
            .ThenByDescending(x => x.Last)
            .FirstOrDefault();

        if (spectral.Count < 2)
            return yinChoice;

        int spectralChoice = spectral.Midi;
        if (spectralChoice == old)
            return yinChoice;

        static bool Related(int a, int b) =>
            a == b || OvertoneOffsets.Contains(a - b) || UndertoneOffsets.Contains(a - b);

        bool yinIsOld = Related(yinChoice, old);
        bool spectralIsOld = Related(spectralChoice, old);
        if (yinIsOld)
        {
            if (!spectralIsOld)
                return spectralChoice;

            // A new pluck can land on one of the old string's strong harmonics (open B over
            // low E, high e over low E). The old string's own octave is not enough to tell
            // them apart, but the third and fourth harmonics are excellent evidence.
            int interval = spectralChoice - old;
            if (interval is 19 or 24)
                return spectralChoice;
        }

        return yinChoice;
    }
}
