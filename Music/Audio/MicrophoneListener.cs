using NAudio.Wave;

namespace Music.Audio;

public readonly record struct PitchReading(double? Frequency, double Level, double Gate);

/// <summary>Captures microphone audio and reports the detected pitch for each buffer.</summary>
public sealed class MicrophoneListener : IDisposable
{
    public const int SampleRate = 44100;
    private const int WindowSize = 4096;
    private const double MinimumGate = 0.003;

    private readonly float[] window = new float[WindowSize];
    private readonly PitchDetector detector = new(SampleRate, WindowSize);
    private WaveInEvent? waveIn;
    private double noiseFloor = 0.002;

    /// <summary>Raised on the audio thread.</summary>
    public event Action<PitchReading>? PitchUpdated;

    /// <summary>How far above the background noise level a sound must be to be analysed.</summary>
    public double GateRatio { get; set; } = 4;

    public static IReadOnlyList<string> GetDevices() =>
        Enumerable.Range(0, WaveInEvent.DeviceCount)
            .Select(i => WaveInEvent.GetCapabilities(i).ProductName)
            .ToList();

    public void Start(int deviceNumber)
    {
        Stop();
        Array.Clear(window);
        noiseFloor = 0.002;

        waveIn = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 50,
        };
        waveIn.DataAvailable += OnDataAvailable;
        waveIn.StartRecording();
    }

    public void Stop()
    {
        if (waveIn is null)
            return;

        waveIn.DataAvailable -= OnDataAvailable;
        waveIn.StopRecording();
        waveIn.Dispose();
        waveIn = null;
    }

    public void Dispose() => Stop();

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        int received = e.BytesRecorded / 2;
        int count = Math.Min(received, WindowSize);
        int skip = received - count;

        // Slide the analysis window and append the newest samples.
        Array.Copy(window, count, window, 0, WindowSize - count);
        for (int i = 0; i < count; i++)
            window[WindowSize - count + i] = BitConverter.ToInt16(e.Buffer, (skip + i) * 2) / 32768f;

        double sumSquares = 0;
        for (int i = WindowSize - count; i < WindowSize; i++)
            sumSquares += window[i] * window[i];
        double level = Math.Sqrt(sumSquares / Math.Max(1, count));

        // Track the room's background level: drop quickly to quiet moments, rise only slowly,
        // so a ringing string doesn't get mistaken for noise.
        double rate = level < noiseFloor ? 0.3 : 0.003;
        noiseFloor = Math.Max(0.0005, noiseFloor + rate * (level - noiseFloor));

        double gate = Math.Max(MinimumGate, noiseFloor * GateRatio);
        double? frequency = level >= gate ? detector.Detect(window) : null;
        PitchUpdated?.Invoke(new PitchReading(frequency, level, gate));
    }
}
