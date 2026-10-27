using NAudio.Wave;

namespace Music.Audio;

public readonly record struct PitchReading(
    double? Frequency,
    double Level,
    double Gate,
    double? SpectralFrequency = null,
    double? Brightness = null,
    double? SpectralBrightness = null);

/// <summary>Newly captured samples plus the input level and current noise gate.</summary>
public readonly record struct AudioBlock(float[] Samples, double Level, double Gate)
{
    public bool HasSignal => Level >= Gate;
}

/// <summary>Captures microphone audio and reports the detected pitch for each buffer.</summary>
public sealed class MicrophoneListener : IDisposable
{
    public const int SampleRate = 44100;
    private const double MinimumGate = 0.003;

    private readonly int windowSize;
    private readonly int bufferMilliseconds;
    private readonly float[] window;
    private readonly PitchDetector detector;
    private readonly double noiseFloorFallRate;
    private readonly double noiseFloorRiseRate;
    private readonly object recorderLock = new();
    private WaveInEvent? waveIn;
    private WaveFileWriter? recorder;
    private double noiseFloor = 0.002;

    /// <param name="windowSize">Samples analysed for pitch. Shorter reacts faster to new notes; 2048 still reaches 50 Hz.</param>
    /// <param name="bufferMilliseconds">How often a block is delivered.</param>
    /// <param name="maxFrequency">Highest pitch detected.</param>
    public MicrophoneListener(int windowSize = 4096, int bufferMilliseconds = 50, double maxFrequency = 1000)
    {
        this.windowSize = windowSize;
        this.bufferMilliseconds = bufferMilliseconds;
        window = new float[windowSize];
        detector = new PitchDetector(SampleRate, windowSize, maxFrequency: maxFrequency);

        // The noise floor adapts at the same speed in real time whatever the block length.
        double blocksPer50Ms = bufferMilliseconds / 50.0;
        noiseFloorFallRate = 1 - Math.Pow(1 - 0.3, blocksPer50Ms);
        noiseFloorRiseRate = 1 - Math.Pow(1 - 0.003, blocksPer50Ms);
    }

    /// <summary>Raised on the audio thread. Pitch detection only runs while this has subscribers.</summary>
    public event Action<PitchReading>? PitchUpdated;

    /// <summary>Raised on the audio thread with each new block of samples.</summary>
    public event Action<AudioBlock>? SamplesCaptured;

    /// <summary>How far above the background noise level a sound must be to be analysed.</summary>
    public double GateRatio { get; set; } = 4;

    /// <summary>
    /// When true, also computes a harmonic-summing pitch candidate. Tabs uses it to hear a new string
    /// that is masked by a still-ringing old one; the tuner doesn't need the extra work.
    /// </summary>
    public bool DetectHarmonics { get; set; }

    /// <summary>
    /// Set by a <see cref="SamplesCaptured"/> handler (during the callback) when it recognises music in the
    /// block, so the noise floor doesn't creep up. Set automatically when a pitch is detected.
    /// </summary>
    public bool MusicPresent { get; set; }

    public bool IsRunning => waveIn is not null;

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
            BufferMilliseconds = bufferMilliseconds,
        };
        waveIn.DataAvailable += OnDataAvailable;
        waveIn.StartRecording();
    }

    public void Stop()
    {
        if (waveIn is not null)
        {
            waveIn.DataAvailable -= OnDataAvailable;
            waveIn.StopRecording();
            waveIn.Dispose();
            waveIn = null;
        }

        StopRecording();
    }

    /// <summary>Starts writing the captured microphone audio to a 44.1 kHz, 16-bit mono WAV.</summary>
    public void StartRecording(string path)
    {
        lock (recorderLock)
        {
            recorder?.Dispose();
            recorder = new WaveFileWriter(path, new WaveFormat(SampleRate, 16, 1));
        }
    }

    /// <summary>Finishes and closes the WAV started by <see cref="StartRecording"/>.</summary>
    public void StopRecording()
    {
        lock (recorderLock)
        {
            recorder?.Dispose();
            recorder = null;
        }
    }

    public void Dispose() => Stop();

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        int received = e.BytesRecorded / 2;
        int count = Math.Min(received, windowSize);
        int skip = received - count;

        // Slide the analysis window and append the newest samples.
        Array.Copy(window, count, window, 0, windowSize - count);
        for (int i = 0; i < count; i++)
            window[windowSize - count + i] = BitConverter.ToInt16(e.Buffer, (skip + i) * 2) / 32768f;

        lock (recorderLock)
            recorder?.WriteSamples(window, windowSize - count, count);

        double sumSquares = 0;
        for (int i = windowSize - count; i < windowSize; i++)
            sumSquares += window[i] * window[i];
        double level = Math.Sqrt(sumSquares / Math.Max(1, count));

        // Track the room's background level: drop quickly to quiet moments...
        if (level < noiseFloor)
            noiseFloor = Math.Max(0.0005, noiseFloor + noiseFloorFallRate * (level - noiseFloor));

        double gate = Math.Max(MinimumGate, noiseFloor * GateRatio);
        MusicPresent = false;
        SamplesCaptured?.Invoke(new AudioBlock(window[^count..], level, gate));

        if (PitchUpdated is { } pitchUpdated)
        {
            double? frequency = level >= gate ? detector.Detect(window) : null;
            double? spectral = DetectHarmonics && level >= gate ? detector.DetectSpectral(window) : null;
            double? brightness = DetectHarmonics && frequency is double f
                ? detector.HarmonicBrightness(window, f)
                : null;
            double? spectralBrightness = DetectHarmonics && spectral is double sf
                ? detector.HarmonicBrightness(window, sf)
                : null;
            MusicPresent |= frequency is not null;
            pitchUpdated(new PitchReading(frequency, level, gate, spectral, brightness, spectralBrightness));
        }

        // ...but rise only slowly, and never while music is being heard. Otherwise continuous
        // playing (no quiet moments) would slowly push the gate above the notes themselves.
        if (level > noiseFloor && !MusicPresent)
            noiseFloor += noiseFloorRiseRate * (level - noiseFloor);
    }
}
