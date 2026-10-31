using NAudio.Wave;

namespace AllTunning.Audio;

/// <summary>Plays a short plucked-string-like reference tone.</summary>
public sealed class TonePlayer : IDisposable
{
    private WaveOutEvent? output;

    public void Play(double frequency)
    {
        Stop();
        output = new WaveOutEvent();
        output.Init(new PluckedTone(frequency, MicrophoneListener.SampleRate, seconds: 2.5));
        output.Play();
    }

    public void Stop()
    {
        output?.Stop();
        output?.Dispose();
        output = null;
    }

    public void Dispose() => Stop();

    /// <summary>Additive synthesis with decaying harmonics, so the pitch stays exact.</summary>
    private sealed class PluckedTone(double frequency, int sampleRate, double seconds) : ISampleProvider
    {
        private const int Harmonics = 6;
        private readonly int totalSamples = (int)(sampleRate * seconds);
        private int position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            int samples = Math.Min(count, totalSamples - position);
            for (int n = 0; n < samples; n++, position++)
            {
                double t = (double)position / sampleRate;
                double value = 0;
                for (int h = 1; h <= Harmonics; h++)
                    value += Math.Sin(2 * Math.PI * frequency * h * t) / h * Math.Exp(-t * (1.0 + 0.8 * h));

                double attack = Math.Min(1, t / 0.005);
                buffer[offset + n] = (float)(value * 0.3 * attack);
            }

            return samples;
        }
    }
}
