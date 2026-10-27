using Music.Tuning;
using NAudio.Dsp;

namespace Music.Chords;

/// <param name="Chord">The stable chord being heard, or null.</param>
/// <param name="Confidence">How well the notes match the chord (0–1).</param>
/// <param name="Chroma">Energy per pitch class (C..B), normalised so the loudest is 1.</param>
public sealed record ChordAnalysis(Chord? Chord, double Confidence, double[] Chroma);

/// <summary>
/// Recognises chords by building a chromagram (energy per pitch class) from the spectrum's peaks
/// and matching it against chord templates for every root.
/// </summary>
public sealed class ChordDetector
{
    // 16384 samples at 44.1 kHz ≈ 0.37 s, giving ~2.7 Hz resolution: enough to separate low guitar notes.
    private const int FftSize = 16384;
    private const int FftLog2 = 14;
    private const double MinFrequency = 70;
    private const double MaxFrequency = 1400;
    private const double PeakFloor = 0.05;
    private const double MaxCentsOff = 35;
    private const double MinConfidence = 0.8;
    private const double BassBonus = 0.04;
    private const int StableFramesRequired = 3;
    private const double OvertoneWeight = 0.35;
    private const int LowestBassMidi = 38; // D2, for drop-D
    private const int BassCandidateCount = 22; // up to B3
    private const double BassSupportRatio = 0.75;
    private const double StrongFundamental = 0.35;
    private const double BassOutsidePenalty = 0.06;
    private const double ThirdPenalty = 0.25;

    // A strum is a block this much louder than the recent level. The analysis then only looks at audio
    // since the strum (at least MinOnsetSamples), so the previous chord doesn't blend into the new one.
    private const double OnsetRatio = 1.8;
    private const int MinOnsetSamples = 4096;
    private static readonly int[] OddHarmonics = [3, 5, 6, 7];

    private readonly int sampleRate;
    private readonly float[] window = new float[FftSize];
    private readonly float[] hann = new float[FftSize];
    private readonly Complex[] fft = new Complex[FftSize];
    private readonly double[] magnitudes = new double[FftSize / 2];
    private readonly double[] smoothedChroma = new double[12];
    private readonly double[] lowestPeak = new double[12];

    private Chord? candidate;
    private int candidateFrames;
    private Chord? stableChord;
    private double stableConfidence;
    private double recentLevel;
    private int samplesSinceOnset = FftSize;

    public ChordDetector(int sampleRate)
    {
        this.sampleRate = sampleRate;
        for (int i = 0; i < FftSize; i++)
            hann[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1)));
    }

    public double A4 { get; set; } = 440;

    public void Reset()
    {
        Array.Clear(window);
        Array.Clear(smoothedChroma);
        candidate = stableChord = null;
        candidateFrames = 0;
        recentLevel = 0;
        samplesSinceOnset = FftSize;
    }

    /// <summary>Feeds newly captured samples. <paramref name="hasSignal"/> is false when the input is below the noise gate.</summary>
    public ChordAnalysis Process(float[] samples, bool hasSignal)
    {
        Append(samples);
        bool onset = DetectOnset(samples);

        if (!hasSignal)
        {
            for (int i = 0; i < 12; i++)
                smoothedChroma[i] *= 0.6;
            candidate = stableChord = null;
            candidateFrames = 0;
            return new ChordAnalysis(null, 0, (double[])smoothedChroma.Clone());
        }

        // Right after a strum there is too little new audio to analyse; keep showing the previous result.
        if (samplesSinceOnset < MinOnsetSamples)
            return new ChordAnalysis(stableChord, stableChord is null ? 0 : stableConfidence, (double[])smoothedChroma.Clone());

        // The first analysis after a strum starts the smoothing afresh.
        var (chroma, bass) = ComputeChroma();
        double keep = samplesSinceOnset - samples.Length < MinOnsetSamples ? 0 : 0.5;
        for (int i = 0; i < 12; i++)
            smoothedChroma[i] = keep * smoothedChroma[i] + (1 - keep) * chroma[i];
        Normalize(smoothedChroma);

        var (best, confidence) = Match(smoothedChroma, bass, lowestPeak);
        UpdateStability(best, confidence);
        return new ChordAnalysis(stableChord, stableChord is null ? 0 : stableConfidence, (double[])smoothedChroma.Clone());
    }

    private void Append(float[] samples)
    {
        int count = Math.Min(samples.Length, FftSize);
        Array.Copy(window, count, window, 0, FftSize - count);
        Array.Copy(samples, samples.Length - count, window, FftSize - count, count);
    }

    /// <summary>Tracks the input level and restarts the analysis window when a new strum starts.</summary>
    private bool DetectOnset(float[] samples)
    {
        double sumSquares = 0;
        foreach (float sample in samples)
            sumSquares += sample * sample;
        double level = Math.Sqrt(sumSquares / Math.Max(1, samples.Length));

        bool onset = level > recentLevel * OnsetRatio && level > 1e-3;
        recentLevel = onset ? level : Math.Max(level, 0.7 * recentLevel + 0.3 * level);
        samplesSinceOnset = onset ? samples.Length : Math.Min(FftSize, samplesSinceOnset + samples.Length);
        return onset;
    }

    private (double[] Chroma, Bass? Bass) ComputeChroma()
    {
        // Only the audio since the last strum is analysed, with a Hann window fitted to that length.
        int length = samplesSinceOnset;
        int start = FftSize - length;
        for (int i = 0; i < FftSize; i++)
        {
            double weight = i < start ? 0
                : length == FftSize ? hann[i]
                : 0.5 - 0.5 * Math.Cos(2 * Math.PI * (i - start) / (length - 1));
            fft[i].X = (float)(window[i] * weight);
            fft[i].Y = 0;
        }

        FastFourierTransform.FFT(true, FftLog2, fft);

        int minBin = (int)(MinFrequency * FftSize / sampleRate);
        int maxBin = Math.Min(magnitudes.Length - 2, (int)(MaxFrequency * FftSize / sampleRate));
        double loudest = 1e-12;
        for (int k = minBin - 1; k <= maxBin + 1; k++)
        {
            magnitudes[k] = Math.Sqrt(fft[k].X * fft[k].X + fft[k].Y * fft[k].Y);
            if (k >= minBin && k <= maxBin)
                loudest = Math.Max(loudest, magnitudes[k]);
        }

        var peaks = new List<(double Frequency, double Magnitude, int PitchClass)>();
        for (int k = minBin; k <= maxBin; k++)
        {
            double m = magnitudes[k];
            if (m < loudest * PeakFloor || m <= magnitudes[k - 1] || m < magnitudes[k + 1])
                continue;

            // Parabolic interpolation on log magnitudes for the true peak frequency.
            double a = Math.Log(magnitudes[k - 1] + 1e-12), b = Math.Log(m), c = Math.Log(magnitudes[k + 1] + 1e-12);
            double denominator = a - 2 * b + c;
            double offset = denominator == 0 ? 0 : 0.5 * (a - c) / denominator;
            double frequency = (k + offset) * sampleRate / FftSize;

            double midi = NoteMath.FrequencyToMidi(frequency, A4);
            double nearest = Math.Round(midi);
            if (Math.Abs(midi - nearest) * 100 > MaxCentsOff)
                continue;

            peaks.Add((frequency, m / loudest, (((int)nearest % 12) + 12) % 12));
        }

        var chroma = new double[12];
        Array.Fill(lowestPeak, double.MaxValue);
        foreach (var peak in peaks)
        {
            chroma[peak.PitchClass] += Math.Sqrt(peak.Magnitude) * (IsOvertone(peak.Frequency, peak.Magnitude, peaks) ? OvertoneWeight : 1);
            lowestPeak[peak.PitchClass] = Math.Min(lowestPeak[peak.PitchClass], peak.Frequency);
        }

        Normalize(chroma);
        return (chroma, FindBass(peaks));
    }

    /// <summary>
    /// True if the peak sits on a 3rd, 5th, 6th or 7th harmonic of a lower peak. Those overtones land on
    /// other pitch classes (e.g. the 3rd harmonic of E is B) and would otherwise add notes that aren't played.
    /// </summary>
    private static bool IsOvertone(double frequency, double magnitude, List<(double Frequency, double Magnitude, int PitchClass)> peaks)
    {
        // A note an octave or two below means this pitch class is really being played.
        if (StrongestPeakNear(peaks, frequency / 2) > 0 || StrongestPeakNear(peaks, frequency / 4) > 0)
            return false;

        foreach (var lower in peaks)
        {
            if (lower.Frequency >= frequency / 2.5)
                break;
            if (lower.Magnitude < magnitude * 0.25)
                continue;

            double ratio = frequency / lower.Frequency;
            foreach (int harmonic in OddHarmonics)
            {
                if (Math.Abs(ratio / harmonic - 1) < 0.012)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Finds the bass pitch class from whole harmonic series rather than the lowest peak, because cheap
    /// microphones barely pick up the fundamentals of the low strings.
    /// </summary>
    private Bass? FindBass(List<(double Frequency, double Magnitude, int PitchClass)> peaks)
    {
        if (peaks.Count == 0)
            return null;

        var scores = new double[BassCandidateCount];
        var fundamentals = new double[BassCandidateCount];
        for (int i = 0; i < BassCandidateCount; i++)
        {
            double f0 = NoteMath.MidiToFrequency(LowestBassMidi + i, A4);
            fundamentals[i] = StrongestPeakNear(peaks, f0);

            // Without at least a faint peak at the fundamental, the "note" is just the
            // octave below a higher string, whose harmonics also line up.
            if (fundamentals[i] == 0)
                continue;

            for (int harmonic = 1; harmonic <= 6; harmonic++)
                scores[i] += StrongestPeakNear(peaks, f0 * harmonic) / Math.Sqrt(harmonic);
        }

        double best = scores.Max();
        if (best <= 0)
            return null;

        // The lowest well-supported (or clearly loud) note is the bass; higher strings have full harmonic series too.
        for (int i = 0; i < BassCandidateCount; i++)
        {
            if (scores[i] >= best * BassSupportRatio || fundamentals[i] >= StrongFundamental)
                return new Bass((LowestBassMidi + i) % 12, NoteMath.MidiToFrequency(LowestBassMidi + i, A4));
        }

        return null;
    }

    private static double StrongestPeakNear(List<(double Frequency, double Magnitude, int PitchClass)> peaks, double frequency)
    {
        double strongest = 0;
        foreach (var peak in peaks)
        {
            if (Math.Abs(peak.Frequency / frequency - 1) < 0.015)
                strongest = Math.Max(strongest, peak.Magnitude);
        }

        return strongest;
    }

    /// <summary>
    /// Picks the best chord template. The bass note favours chords that contain it, and a power or sus chord
    /// loses out when a third is clearly heard.
    /// </summary>
    /// <param name="lowestPeak">Lowest frequency heard for each pitch class.</param>
    private static (Chord? Chord, double Confidence) Match(double[] chroma, Bass? bass, double[] lowestPeak)
    {
        double chromaNorm = Math.Sqrt(chroma.Sum(v => v * v));
        if (chromaNorm == 0)
            return (null, 0);

        Chord? best = null;
        double bestScore = double.MinValue, bestSimilarity = 0;
        foreach (var quality in ChordQuality.All)
        {
            double templateNorm = Math.Sqrt(quality.Intervals.Length);
            for (int root = 0; root < 12; root++)
            {
                double dot = 0;
                foreach (int interval in quality.Intervals)
                    dot += chroma[(root + interval) % 12];

                double similarity = dot / (chromaNorm * templateNorm);
                double score = similarity + quality.Prior;
                bool bassInChord = bass is Bass b && quality.Intervals.Any(i => (root + i) % 12 == b.PitchClass);
                if (bass?.PitchClass == root)
                    score += BassBonus;
                else if (bass is not null && !bassInChord)
                    score -= BassOutsidePenalty;
                if (!quality.Intervals.Contains(3) && !quality.Intervals.Contains(4))
                    score -= ThirdPenalty * Math.Max(chroma[(root + 3) % 12], chroma[(root + 4) % 12]);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestSimilarity = similarity;

                    // An inversion only when the root really isn't sounding below the bass note.
                    bool inversion = bass is Bass heard && bassInChord && heard.PitchClass != root
                        && lowestPeak[root] > heard.Frequency * 0.97;
                    best = new Chord(root, quality, inversion ? bass!.Value.PitchClass : null);
                }
            }
        }

        return bestSimilarity >= MinConfidence ? (best, bestSimilarity) : (null, bestSimilarity);
    }

    private void UpdateStability(Chord? chord, double confidence)
    {
        if (chord is not null && chord == candidate)
        {
            candidateFrames++;
        }
        else
        {
            candidate = chord;
            candidateFrames = 1;
        }

        if (candidate is not null && candidateFrames >= StableFramesRequired)
        {
            stableChord = candidate;
            stableConfidence = confidence;
        }
        else if (candidate is null && candidateFrames >= StableFramesRequired)
        {
            stableChord = null;
        }
    }

    private static void Normalize(double[] values)
    {
        double max = values.Max();
        if (max <= 0)
            return;
        for (int i = 0; i < values.Length; i++)
            values[i] /= max;
    }
}

/// <summary>The detected bass note: its pitch class and the frequency it was heard at.</summary>
internal readonly record struct Bass(int PitchClass, double Frequency);
