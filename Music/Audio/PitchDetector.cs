namespace Music.Audio;

/// <summary>
/// Fundamental frequency estimation using the YIN algorithm
/// (de Cheveigné and Kawahara, 2002).
/// </summary>
public sealed class PitchDetector
{
    private const double Threshold = 0.15;

    private readonly int sampleRate;
    private readonly int tauMin;
    private readonly int tauMax;
    private readonly float[] yin;

    public PitchDetector(int sampleRate, int bufferSize, double minFrequency = 50, double maxFrequency = 1400)
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

        // Parabolic interpolation for sub-sample accuracy.
        double s0 = yin[best - 1], s1 = yin[best], s2 = yin[best + 1];
        double denominator = s0 + s2 - 2 * s1;
        double refinedTau = denominator == 0 ? best : best + (s0 - s2) / (2 * denominator);

        return sampleRate / refinedTau;
    }
}
