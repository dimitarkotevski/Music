namespace Music.Audio;

/// <summary>
/// Fundamental frequency estimation using the YIN algorithm
/// (de Cheveigné and Kawahara, 2002).
/// </summary>
public sealed class PitchDetector
{
    // Lower = stricter: only clearly periodic sounds (like a plucked string) pass.
    private const double Threshold = 0.1;

    // Octave correction only kicks in when the chosen dip is not already clean, and a
    // longer period is clearly (OctaveCheckRatio times) more periodic.
    private const double OctaveCheckFloor = 0.02;
    private const double OctaveCheckRatio = 0.5;

    // If the detected frequency has almost no energy of its own but a harmonic does, YIN has
    // locked onto a subharmonic (a B string read as E2). Correct only when the fundamental is
    // this weak, so a genuinely quiet low string is left alone.
    private const double SubharmonicEnergyRatio = 0.15;

    private readonly int sampleRate;
    private readonly int tauMin;
    private readonly int tauMax;
    private readonly float[] yin;

    public PitchDetector(int sampleRate, int bufferSize, double minFrequency = 50, double maxFrequency = 1000)
    {
        this.sampleRate = sampleRate;
        tauMin = Math.Max(2, (int)(sampleRate / maxFrequency));
        tauMax = Math.Min(bufferSize / 2, (int)(sampleRate / minFrequency));
        yin = new float[tauMax + 1];
    }

    /// <summary>Returns the detected frequency in Hz, or null if no clear pitch was found.</summary>
    public double? Detect(float[] buffer)
    {
        int window = buffer.Length - tauMax;

        // Difference function combined with cumulative mean normalisation.
        yin[0] = 1;
        double runningSum = 0;
        for (int tau = 1; tau <= tauMax; tau++)
        {
            double sum = 0;
            for (int i = 0; i < window; i++)
            {
                double delta = buffer[i] - buffer[i + tau];
                sum += delta * delta;
            }

            runningSum += sum;
            yin[tau] = runningSum == 0 ? 1 : (float)(sum * tau / runningSum);
        }

        // First dip below the threshold, then walk down to its local minimum.
        int best = -1;
        for (int tau = tauMin; tau < tauMax; tau++)
        {
            if (yin[tau] < Threshold)
            {
                while (tau + 1 < tauMax && yin[tau + 1] < yin[tau])
                    tau++;
                best = tau;
                break;
            }
        }

        if (best < 0)
            return null;

        best = CorrectOctaveError(best);

        // Parabolic interpolation for sub-sample accuracy.
        double s0 = yin[best - 1], s1 = yin[best], s2 = yin[best + 1];
        double denominator = s0 + s2 - 2 * s1;
        double refinedTau = denominator == 0 ? best : best + (s0 - s2) / (2 * denominator);

        return CorrectSubharmonic(buffer, sampleRate / refinedTau);
    }

    /// <summary>
    /// YIN can lock onto two or three times the real period when a string's fundamental is weak.
    /// If the detected frequency has virtually no spectral energy but 2× or 3× it does, report the
    /// stronger partial instead. Two-times correction is only used below 80 Hz, so a genuinely
    /// quiet low E is not pulled up an octave; three-times catches the B-string-read-as-E2 case.
    /// </summary>
    private double CorrectSubharmonic(float[] buffer, double frequency)
    {
        double magnitude = Magnitude(buffer, frequency);
        double best = frequency;
        double bestMagnitude = magnitude;

        // Above the standard low E, only a 3× error is safe to correct: a low note's 2nd harmonic
        // can be louder than its fundamental without the fundamental being wrong.
        int firstMultiple = frequency < 80 ? 2 : 3;
        for (int multiple = firstMultiple; multiple <= 3; multiple++)
        {
            double candidate = frequency * multiple;
            if (candidate >= sampleRate / 2.0)
                break;

            double candidateMagnitude = Magnitude(buffer, candidate);
            if (candidateMagnitude > bestMagnitude)
            {
                best = candidate;
                bestMagnitude = candidateMagnitude;
            }
        }

        return best != frequency && magnitude < bestMagnitude * SubharmonicEnergyRatio ? best : frequency;
    }

    /// <summary>Magnitude of one frequency in the buffer (Hann-windowed Goertzel).</summary>
    private double Magnitude(float[] buffer, double frequency)
    {
        double coefficient = 2 * Math.Cos(2 * Math.PI * frequency / sampleRate);
        double s1 = 0, s2 = 0, windowSum = 0;
        for (int i = 0; i < buffer.Length; i++)
        {
            double window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (buffer.Length - 1));
            double s0 = buffer[i] * window + coefficient * s1 - s2;
            s2 = s1;
            s1 = s0;
            windowSum += window;
        }

        double power = s1 * s1 + s2 * s2 - coefficient * s1 * s2;
        return Math.Sqrt(Math.Max(0, power)) / Math.Max(1e-9, windowSum);
    }

    /// <summary>
    /// A strong 2nd or 3rd harmonic (common on low strings) can make a fraction of the real
    /// period look periodic enough to pass the threshold, reading the note an octave too high.
    /// If a multiple of the chosen period is much more periodic, the real note is lower.
    /// </summary>
    private int CorrectOctaveError(int tau)
    {
        int result = tau;
        for (int multiple = 2; multiple <= 3; multiple++)
        {
            int center = tau * multiple;
            int spread = Math.Max(2, center / 33);
            if (center - spread < 1 || center + spread >= tauMax)
                break;

            int candidate = center - spread;
            for (int i = candidate + 1; i <= center + spread; i++)
            {
                if (yin[i] < yin[candidate])
                    candidate = i;
            }

            if (yin[result] > OctaveCheckFloor && yin[candidate] < yin[result] * OctaveCheckRatio)
                result = candidate;
        }

        return result;
    }
}
