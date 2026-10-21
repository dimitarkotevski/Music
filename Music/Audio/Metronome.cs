using NAudio.Wave;

namespace Music.Audio;

public sealed record TimeSignature(string Name, int Beats)
{
    public static IReadOnlyList<TimeSignature> All { get; } =
    [
        new("2/4", 2),
        new("3/4", 3),
        new("4/4", 4),
        new("5/4", 5),
        new("6/8", 6),
        new("7/8", 7),
    ];
}

/// <summary>Plays sample-accurate metronome clicks with an accented first beat.</summary>
public sealed class Metronome : IDisposable
{
    public const int MinBpm = 30;
    public const int MaxBpm = 250;
    private const int SampleRate = 44100;
    private const int OutputLatencyMs = 80;

    private WaveOutEvent? output;
    private ClickTrack? track;
    private int bpm = 120;
    private int beatsPerBar = 4;

    /// <summary>Raised on a background thread, roughly when each click is heard. The argument is the beat index in the bar.</summary>
    public event Action<int>? Beat;

    public bool IsRunning => output is not null;

    public int Bpm
    {
        get => bpm;
        set
        {
            bpm = Math.Clamp(value, MinBpm, MaxBpm);
            if (track is not null)
                track.Bpm = bpm;
        }
    }

    public int BeatsPerBar
    {
        get => beatsPerBar;
        set
        {
            beatsPerBar = Math.Max(1, value);
            if (track is not null)
                track.BeatsPerBar = beatsPerBar;
        }
    }

    public void Start()
    {
        Stop();
        track = new ClickTrack(SampleRate) { Bpm = bpm, BeatsPerBar = beatsPerBar };
        track.BeatStarted += OnBeatStarted;
        output = new WaveOutEvent { DesiredLatency = OutputLatencyMs, NumberOfBuffers = 2 };
        output.Init(track);
        output.Play();
    }

    public void Stop()
    {
        if (track is not null)
            track.BeatStarted -= OnBeatStarted;
        output?.Stop();
        output?.Dispose();
        output = null;
        track = null;
    }

    public void Dispose() => Stop();

    private void OnBeatStarted(int beat, int sampleOffset)
    {
        // Clicks are rendered ahead of playback; delay the event so visuals line up with the sound.
        int delayMs = OutputLatencyMs + sampleOffset * 1000 / SampleRate;
        _ = Task.Delay(delayMs).ContinueWith(_ => Beat?.Invoke(beat), TaskScheduler.Default);
    }

    private sealed class ClickTrack : ISampleProvider
    {
        private readonly int sampleRate;
        private readonly float[] accentClick;
        private readonly float[] normalClick;
        private double samplesToNextBeat;
        private int beat = -1;
        private float[]? currentClick;
        private int clickPosition;
        private volatile int bpm = 120;
        private volatile int beatsPerBar = 4;

        public ClickTrack(int sampleRate)
        {
            this.sampleRate = sampleRate;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
            accentClick = CreateClick(frequency: 1600, amplitude: 0.7);
            normalClick = CreateClick(frequency: 1000, amplitude: 0.45);
        }

        /// <summary>Raised on the audio thread with the beat index and its offset within the current buffer.</summary>
        public event Action<int, int>? BeatStarted;

        public WaveFormat WaveFormat { get; }

        public int Bpm { get => bpm; set => bpm = value; }

        public int BeatsPerBar { get => beatsPerBar; set => beatsPerBar = value; }

        public int Read(float[] buffer, int offset, int count)
        {
            for (int n = 0; n < count; n++)
            {
                if (samplesToNextBeat <= 0)
                {
                    beat = (beat + 1) % beatsPerBar;
                    currentClick = beat == 0 ? accentClick : normalClick;
                    clickPosition = 0;
                    samplesToNextBeat += sampleRate * 60.0 / bpm;
                    BeatStarted?.Invoke(beat, n);
                }

                samplesToNextBeat--;
                buffer[offset + n] = currentClick is not null && clickPosition < currentClick.Length
                    ? currentClick[clickPosition++]
                    : 0f;
            }

            return count;
        }

        /// <summary>A short, sharp woodblock-like tick.</summary>
        private float[] CreateClick(double frequency, double amplitude)
        {
            var click = new float[(int)(sampleRate * 0.04)];
            for (int i = 0; i < click.Length; i++)
            {
                double t = (double)i / sampleRate;
                click[i] = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * t) * Math.Exp(-t / 0.008));
            }

            return click;
        }
    }
}
