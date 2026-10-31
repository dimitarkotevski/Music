using AllTunning.Transcription;

namespace AllTunning.Tabs;

/// <summary>
/// Live note detection with the Basic Pitch network. Microphone blocks are downsampled to 22.05 kHz,
/// the model looks at the latest two seconds about ten times a second, and its note/onset activations
/// are stitched into one rolling timeline. Notes are decoded from that timeline the way Basic Pitch does
/// it offline (onset peaks that are followed by a sustained note), but causally, so each note is reported
/// a fraction of a second after it is played. Because the network is polyphonic, a string that is still
/// ringing does not hide the next one, and chords come out as one column.
/// </summary>
public sealed class LiveNoteModel : IDisposable
{
    // Basic Pitch's own decoding defaults.
    private const float OnsetThreshold = 0.5f;
    private const float FrameThreshold = 0.3f;

    // Frames after an onset that must stay on before the note is reported (about 70 ms). Offline Basic
    // Pitch waits for 11; fewer keeps the latency down, and the onset peak already did most of the work.
    private const int ConfirmFrames = 6;

    // Offline Basic Pitch tolerates eleven quiet frames after an onset. A low string heard through a laptop
    // or phone mic often needs that: its onset is clear, but the sustained note only builds up after
    // 80–100 ms. Such onsets wait up to this many frames (about 160 ms) instead of being dropped.
    private const int SlowConfirmFrames = 14;

    // The model sees no future at the right edge of its window, and no past at the left edge, so those
    // frames are not trusted. A frame is final once it is this far from the right edge (about 185 ms).
    private const int RightEdgeFrames = 16;
    private const int LeftEdgeFrames = 20;

    // How often the model runs, in frames (9 frames ≈ 104 ms).
    private const int RunHopFrames = 9;

    // Notes whose onsets are this close together form one chord column (about 60 ms).
    private const int ColumnFrames = 5;

    // A new onset on the same key this soon is the same pluck seen twice (about 140 ms).
    private const int SameKeyRefractoryFrames = 12;

    // A note that was already sounding this many frames before an onset must have risen by at least
    // RepluckRise to count as plucked again. Measured: re-plucks rise 0.17–0.29, ringing notes ≤ 0.10.
    private const int SustainLookbackFrames = 6;
    private const int RiseAfterFrames = 2;
    private const float RepluckRise = 0.15f;

    // The activation of a loud ringing note is already near its ceiling, so a re-strum (a chord's common
    // tones) barely moves it. The audio still shows it: the note's own partials get clearly louder, while
    // a string that only rings on keeps decaying.
    private const double RepluckEnergyRatio = 1.6;
    private const int EnergyWindow = 1024; // about 46 ms

    // Sound fingerprint (SoundProfile): a 93 ms window starting just after the attack, and a second one
    // 100 ms later for the decay. Both end 0.22 s after the onset, before any note can be reported.
    private const int ProfileHarmonics = 10;
    private const int ProfileWindow = 2048;
    private const int ProfileDelay = 512;
    private const int ProfileDecayGap = 2205;

    // A column with at least this many freshly started notes is a strum. Strums re-strike strings that were
    // already ringing and contain real octaves, so they are filtered more gently than single notes.
    private const int StrumNotes = 3;

    // In a strum, a note this much quieter than a lower note it is a harmonic of is that harmonic, not a
    // second note. Outside strums any harmonic of a note played just before is dropped: when one string is
    // plucked, the model often adds its octave, sometimes a few frames later than the note itself.
    private const double StrumShadowRatio = 0.55;
    private const int RecentShadowFrames = 16; // about 190 ms

    // The network normalises its input, so room noise in a silent window can look like notes. An onset
    // only counts if the microphone level around it is above the listener's noise gate.
    private const int GateLookBehindFrames = 2;
    private const int GateLookAheadFrames = 4;

    // Semitones from a note to its harmonics (octave, twelfth, two octaves, …).
    private static readonly int[] OvertoneOffsets = [12, 19, 24, 28, 31, 36];

    private const int HistoryFrames = 1024; // about 12 seconds
    private const int HistorySamples = BasicPitchModel.WindowSamples + 64 * BasicPitchModel.FrameHop;

    private static readonly float[] HalfBand = CreateHalfBand(31);

    private readonly BasicPitchModel model;
    private readonly object audioLock = new();
    private readonly float[] audio = new float[HistorySamples];
    private readonly float[] filterHistory = new float[HalfBand.Length];
    private int filterPosition;
    private bool keepNextSample = true;
    private long samplesWritten;
    private readonly bool[] frameAboveGate = new bool[HistoryFrames];
    private double frameSumSquares;

    private readonly float[,] notes = new float[HistoryFrames, BasicPitchModel.Keys];
    private readonly float[,] onsets = new float[HistoryFrames, BasicPitchModel.Keys];
    private readonly long[] lastOnsetFrame = new long[BasicPitchModel.Keys];
    private readonly List<(TranscribedNote Note, bool WasRinging, bool OnlyByEnergy)> pendingColumn = [];
    private readonly List<(long Frame, int Key, bool WasRinging, bool OnlyByEnergy)> slowOnsets = [];
    private readonly List<(long Frame, int Midi)> recentNotes = [];
    private long pendingColumnFrame = -1;
    private long nextWindowFrame;
    private long finalFrames;
    private long nextDecodeFrame;

    private readonly AutoResetEvent wake = new(false);
    private Thread? worker;
    private volatile bool running;
    private int generation;

    public LiveNoteModel(BasicPitchModel model)
    {
        this.model = model;
        Reset();
    }

    /// <summary>Raised on a background thread with the notes that started together (one or more).</summary>
    public event Action<IReadOnlyList<TranscribedNote>>? ColumnDetected;

    /// <summary>Lowest and highest MIDI notes to report (the tuning's playable range).</summary>
    public int LowestMidi { get; set; } = 40;
    public int HighestMidi { get; set; } = 86;

    /// <summary>
    /// When true, a quiet octave or twelfth above a louder note in the same column is dropped. The model
    /// sometimes reports a strong harmonic as its own note; a guitarist rarely doubles a note that way.
    /// </summary>
    public bool DropOvertones { get; set; } = true;

    /// <summary>Seconds of audio analysed so far; note times use the same clock.</summary>
    public double StreamTime => Interlocked.Read(ref samplesWritten) / (double)BasicPitchModel.SampleRate;

    public void Start()
    {
        Stop();
        Reset();
        running = true;
        worker = new Thread(Run) { IsBackground = true, Name = "Basic Pitch live", Priority = ThreadPriority.BelowNormal };
        worker.Start();
    }

    public void Stop()
    {
        running = false;
        wake.Set();
        worker?.Join(1000);
        worker = null;
    }

    public void Dispose()
    {
        Stop();
        wake.Dispose();
    }

    /// <summary>Adds 44.1 kHz microphone samples. Cheap: safe to call from the audio thread.</summary>
    /// <param name="gate">The listener's current noise gate (RMS); quieter audio cannot start a note.</param>
    public void AddSamples(ReadOnlySpan<float> samples44k, double gate)
    {
        lock (audioLock)
        {
            foreach (float sample in samples44k)
            {
                filterHistory[filterPosition] = sample;
                filterPosition = (filterPosition + 1) % filterHistory.Length;
                keepNextSample = !keepNextSample;
                if (keepNextSample)
                    continue;

                // Low-pass at the new Nyquist frequency, then keep every second sample.
                double sum = 0;
                for (int t = 0; t < HalfBand.Length; t++)
                    sum += HalfBand[t] * filterHistory[(filterPosition + t) % filterHistory.Length];
                audio[samplesWritten % HistorySamples] = (float)sum;
                samplesWritten++;

                frameSumSquares += sum * sum;
                if (samplesWritten % BasicPitchModel.FrameHop == 0)
                {
                    long frame = samplesWritten / BasicPitchModel.FrameHop - 1;
                    double level = Math.Sqrt(frameSumSquares / BasicPitchModel.FrameHop);
                    frameAboveGate[frame % HistoryFrames] = level >= gate;
                    frameSumSquares = 0;
                }
            }
        }

        if (samplesWritten >= (nextWindowFrame * BasicPitchModel.FrameHop) + BasicPitchModel.WindowSamples)
            wake.Set();
    }

    /// <summary>Feeds audio and runs the model inline, for offline tests. Do not mix with <see cref="Start"/>.</summary>
    public void ProcessNow(ReadOnlySpan<float> samples44k, double gate)
    {
        AddSamples(samples44k, gate);
        RunPendingWindows();
    }

    /// <summary>Reports any notes still waiting for their column to close, for offline tests.</summary>
    public void Flush()
    {
        DecodeUpTo(long.MaxValue / 2, final: true);
        FlushColumn();
    }

    private void Reset()
    {
        Interlocked.Increment(ref generation);
        lock (audioLock)
        {
            Array.Clear(audio);
            Array.Clear(filterHistory);
            filterPosition = 0;
            keepNextSample = true;
            samplesWritten = 0;
            Array.Clear(frameAboveGate);
            frameSumSquares = 0;
        }

        Array.Clear(notes);
        Array.Clear(onsets);
        Array.Fill(lastOnsetFrame, long.MinValue / 2);
        pendingColumn.Clear();
        slowOnsets.Clear();
        recentNotes.Clear();
        pendingColumnFrame = -1;

        // The first window ends once two seconds have been heard; earlier audio is silence.
        nextWindowFrame = -(BasicPitchModel.WindowFrames - RunHopFrames);
        finalFrames = 0;
        nextDecodeFrame = 0;
    }

    private void Run()
    {
        while (running)
        {
            wake.WaitOne(200);
            if (!running)
                break;

            try
            {
                RunPendingWindows();
            }
            catch
            {
                // A failed inference must never take down the microphone; the next window retries.
            }
        }
    }

    private void RunPendingWindows()
    {
        int myGeneration = generation;
        while (true)
        {
            long available = Interlocked.Read(ref samplesWritten);
            long windowStart = nextWindowFrame * BasicPitchModel.FrameHop;
            if (available < windowStart + BasicPitchModel.WindowSamples)
                return;

            // Fell behind (slow PC or a stall): skip ahead rather than queueing old audio.
            long latestStart = (available - BasicPitchModel.WindowSamples) / BasicPitchModel.FrameHop;
            if (latestStart - nextWindowFrame > 3 * RunHopFrames)
            {
                nextWindowFrame = latestStart;
                windowStart = nextWindowFrame * BasicPitchModel.FrameHop;
            }

            var window = new float[BasicPitchModel.WindowSamples];
            lock (audioLock)
            {
                for (int i = 0; i < window.Length; i++)
                {
                    long index = windowStart + i;
                    window[i] = index < 0 || index < samplesWritten - HistorySamples ? 0 : audio[index % HistorySamples];
                }
            }

            var output = model.Predict(window);
            if (myGeneration != generation)
                return;

            StoreWindow(output, nextWindowFrame);
            nextWindowFrame += RunHopFrames;
            DecodeUpTo(finalFrames, final: false);
        }
    }

    private void StoreWindow(BasicPitchFrames output, long firstFrame)
    {
        // The very first window has nothing before it, so its left edge is real silence.
        int from = firstFrame <= 0 ? (int)Math.Max(0, -firstFrame) : LeftEdgeFrames;
        int to = BasicPitchModel.WindowFrames - RightEdgeFrames;
        for (int j = from; j < to; j++)
        {
            long frame = firstFrame + j;
            if (frame < 0)
                continue;

            int row = (int)(frame % HistoryFrames);
            for (int k = 0; k < BasicPitchModel.Keys; k++)
            {
                notes[row, k] = output.Notes[j, k];
                onsets[row, k] = output.Onsets[j, k];
            }
        }

        finalFrames = Math.Max(finalFrames, firstFrame + to);
    }

    private void DecodeUpTo(long availableFrames, bool final)
    {
        long limit = final ? finalFrames : availableFrames - ConfirmFrames - 1;
        int lowKey = Math.Max(0, LowestMidi - BasicPitchModel.LowestMidi);
        int highKey = Math.Min(BasicPitchModel.Keys - 1, HighestMidi - BasicPitchModel.LowestMidi);

        for (long t = nextDecodeFrame; t < limit; t++)
        {
            ResolveSlowOnsets(t + ConfirmFrames);

            // Hold the column while one of its notes is still waiting for a slow confirmation.
            if (pendingColumnFrame >= 0 && t - pendingColumnFrame > ColumnFrames
                && !slowOnsets.Any(o => o.Frame <= pendingColumnFrame + ColumnFrames))
                FlushColumn();

            if (t < 2)
                continue;

            for (int k = lowKey; k <= highKey; k++)
            {
                float here = OnsetStrength(t, k);
                if (here < OnsetThreshold || here <= OnsetStrength(t - 1, k) || here < OnsetStrength(t + 1, k))
                    continue;
                if (t - lastOnsetFrame[k] < SameKeyRefractoryFrames)
                    continue;

                // A neighbouring key that just started is the same note, slightly sharp or flat.
                if (k > 0 && t - lastOnsetFrame[k - 1] < ColumnFrames)
                    continue;
                if (k < BasicPitchModel.Keys - 1 && t - lastOnsetFrame[k + 1] < ColumnFrames)
                    continue;

                // When another string is plucked, the model often fires a weak onset on notes that are
                // simply still ringing. A real re-pluck makes the note itself clearly louder.
                float before = Note(t - SustainLookbackFrames, k);
                bool wasRinging = before >= FrameThreshold;
                bool onlyByEnergy = false;
                if (wasRinging && Note(t + RiseAfterFrames, k) - before < RepluckRise)
                {
                    if (EnergyRise(t, k + BasicPitchModel.LowestMidi) < RepluckEnergyRatio)
                        continue;
                    onlyByEnergy = true; // kept only if the column turns out to be a strum
                }

                if (!AboveGate(t))
                    continue;

                lastOnsetFrame[k] = t;
                var onset = (Frame: t, Key: k, WasRinging: wasRinging, OnlyByEnergy: onlyByEnergy);
                if (CountOn(t, k, ConfirmFrames) >= ConfirmFrames)
                    AcceptOnset(onset, ConfirmFrames);
                else
                    slowOnsets.Add(onset); // a low string on a small mic can take a while to sound
            }
        }

        if (final)
            ResolveSlowOnsets(long.MaxValue / 2);

        nextDecodeFrame = Math.Max(nextDecodeFrame, limit);
    }

    /// <summary>Frames at or above the frame threshold in onset … onset + span.</summary>
    private int CountOn(long frame, int key, int span)
    {
        int on = 0;
        for (int i = 0; i <= span; i++)
        {
            if (Note(frame + i, key) >= FrameThreshold)
                on++;
        }

        return on;
    }

    /// <summary>Decides onsets that were waiting for a slow note once their whole window has been seen.</summary>
    private void ResolveSlowOnsets(long seenFrame)
    {
        for (int i = 0; i < slowOnsets.Count; i++)
        {
            var onset = slowOnsets[i];
            if (onset.Frame + SlowConfirmFrames > seenFrame)
                continue;

            slowOnsets.RemoveAt(i--);
            if (CountOn(onset.Frame, onset.Key, SlowConfirmFrames) >= ConfirmFrames)
                AcceptOnset(onset, SlowConfirmFrames);
        }
    }

    private void AcceptOnset((long Frame, int Key, bool WasRinging, bool OnlyByEnergy) onset, int span)
    {
        double energy = 0;
        for (int i = 0; i <= span; i++)
            energy += Note(onset.Frame + i, onset.Key);

        double start = onset.Frame / BasicPitchModel.FramesPerSecond;
        double end = (onset.Frame + span + 1) / BasicPitchModel.FramesPerSecond;
        if (pendingColumnFrame < 0)
            pendingColumnFrame = onset.Frame;
        var note = new TranscribedNote(start, end, onset.Key + BasicPitchModel.LowestMidi, energy / (span + 1));
        pendingColumn.Add((note, onset.WasRinging, onset.OnlyByEnergy));
    }

    /// <summary>The predicted onset, or a sharp rise of the note activation (Basic Pitch's inferred onsets).</summary>
    private float OnsetStrength(long frame, int key)
    {
        if (frame < 2 || frame >= finalFrames)
            return 0;

        float rise = Math.Min(Note(frame, key) - Note(frame - 1, key), Note(frame, key) - Note(frame - 2, key));
        return Math.Max(onsets[frame % HistoryFrames, key], Math.Max(0, rise));
    }

    // Rows past the last stored frame still hold audio from twelve seconds ago.
    private float Note(long frame, int key) =>
        frame < 0 || frame >= finalFrames ? 0 : notes[frame % HistoryFrames, key];

    /// <summary>Energy at a note's fundamental and octave just after an onset, relative to just before it.</summary>
    private double EnergyRise(long frame, int midi)
    {
        double frequency = 440 * Math.Pow(2, (midi - 69) / 12.0);
        long onsetSample = frame * BasicPitchModel.FrameHop;
        double before = PartialEnergy(onsetSample - EnergyWindow - 2 * BasicPitchModel.FrameHop, frequency);
        double after = PartialEnergy(onsetSample, frequency);
        return after / Math.Max(before, 1e-9);
    }

    private double PartialEnergy(long start, double frequency) =>
        ReadAudio(start, EnergyWindow) is float[] window
            ? Goertzel(window, frequency) + 0.5 * Goertzel(window, 2 * frequency)
            : 0;

    /// <summary>
    /// A fingerprint of how a single note sounds, for telling strings apart: the strength of harmonics
    /// 2–10 relative to the fundamental, the average harmonic number, and how fast the note fades.
    /// For the same pitch, a thicker string and a longer vibrating length change all three a little.
    /// Returns null when the audio around the note is no longer (or not yet) in the buffer.
    /// </summary>
    /// <param name="startSeconds">The note's <see cref="TranscribedNote.Start"/>.</param>
    public double[]? SoundProfile(double startSeconds, int midi)
    {
        double fundamental = 440 * Math.Pow(2, (midi - 69) / 12.0);
        long onset = (long)Math.Round(startSeconds * BasicPitchModel.SampleRate);
        if (ReadAudio(onset + ProfileDelay, ProfileWindow) is not float[] early
            || ReadAudio(onset + ProfileDelay + ProfileDecayGap, ProfileWindow) is not float[] late)
            return null;

        var harmonics = new double[ProfileHarmonics];
        for (int h = 1; h <= ProfileHarmonics; h++)
        {
            // Wound strings are slightly inharmonic: their upper partials sit a little sharp.
            foreach (double stretch in (double[])[0.99, 1.0, 1.01, 1.02])
                harmonics[h - 1] = Math.Max(harmonics[h - 1], Goertzel(early, fundamental * h * stretch));
        }

        if (harmonics[0] < 1e-6)
            return null;

        var profile = new double[ProfileHarmonics + 1];
        double weighted = 0, total = 0;
        for (int h = 1; h <= ProfileHarmonics; h++)
        {
            if (h > 1)
                profile[h - 2] = Math.Log10(harmonics[h - 1] / harmonics[0] + 1e-3);
            weighted += h * harmonics[h - 1];
            total += harmonics[h - 1];
        }

        profile[ProfileHarmonics - 1] = weighted / total;
        double earlyLevel = Goertzel(early, fundamental) + Goertzel(early, 2 * fundamental);
        double lateLevel = Goertzel(late, fundamental) + Goertzel(late, 2 * fundamental);
        profile[ProfileHarmonics] = Math.Log10((lateLevel + 1e-9) / (earlyLevel + 1e-9));
        return profile;
    }

    /// <summary>A copy of the downsampled audio, or null if it is not (or no longer) in the buffer.</summary>
    private float[]? ReadAudio(long start, int length)
    {
        var window = new float[length];
        lock (audioLock)
        {
            if (start < 0 || start < samplesWritten - HistorySamples || start + length > samplesWritten)
                return null;
            for (int i = 0; i < length; i++)
                window[i] = audio[(start + i) % HistorySamples];
        }

        return window;
    }

    /// <summary>Hann-windowed magnitude of one frequency.</summary>
    private static double Goertzel(float[] samples, double frequency)
    {
        if (frequency >= BasicPitchModel.SampleRate / 2.0)
            return 0;

        double coefficient = 2 * Math.Cos(2 * Math.PI * frequency / BasicPitchModel.SampleRate);
        double s1 = 0, s2 = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            double hann = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (samples.Length - 1));
            double s0 = samples[i] * hann + coefficient * s1 - s2;
            s2 = s1;
            s1 = s0;
        }

        return Math.Sqrt(Math.Max(0, s1 * s1 + s2 * s2 - coefficient * s1 * s2));
    }

    private bool AboveGate(long frame)
    {
        lock (audioLock)
        {
            long newest = samplesWritten / BasicPitchModel.FrameHop - 1;
            for (long f = Math.Max(0, frame - GateLookBehindFrames); f <= Math.Min(newest, frame + GateLookAheadFrames); f++)
            {
                if (newest - f < HistoryFrames && frameAboveGate[f % HistoryFrames])
                    return true;
            }
        }

        return false;
    }

    private void FlushColumn()
    {
        if (pendingColumn.Count == 0)
        {
            pendingColumnFrame = -1;
            return;
        }

        var candidates = pendingColumn.OrderBy(n => n.Note.Midi).ToList();
        bool strum = candidates.Count(c => !c.WasRinging) >= StrumNotes;
        if (!strum)
            candidates.RemoveAll(c => c.OnlyByEnergy);

        var column = candidates.Select(c => c.Note).ToList();
        long columnFrame = pendingColumnFrame;
        recentNotes.RemoveAll(r => columnFrame - r.Frame > RecentShadowFrames);
        if (DropOvertones)
        {
            column = candidates
                .Where(c =>
                {
                    var note = c.Note;

                    // A harmonic of a lower note in the column (or one played just before) is that note.
                    bool shadow = strum
                        ? candidates.Any(lower =>
                            lower.Note.Midi < note.Midi
                            && OvertoneOffsets.Contains(note.Midi - lower.Note.Midi)
                            && note.Amplitude < lower.Note.Amplitude * StrumShadowRatio)
                        : candidates.Any(lower => OvertoneOffsets.Contains(note.Midi - lower.Note.Midi))
                            || recentNotes.Any(r => OvertoneOffsets.Contains(note.Midi - r.Midi));

                    // A string that was still ringing gets louder when a new note lands on one of its
                    // harmonics. That is sympathetic resonance, not a re-pluck. A strum re-strikes it for real.
                    bool sympathetic = !strum && c.WasRinging && candidates.Any(higher =>
                        !higher.WasRinging
                        && OvertoneOffsets.Contains(higher.Note.Midi - note.Midi));

                    return !shadow && !sympathetic;
                })
                .Select(c => c.Note)
                .ToList();
        }

        recentNotes.AddRange(column.Select(n => (columnFrame, n.Midi)));
        pendingColumn.Clear();
        pendingColumnFrame = -1;
        if (column.Count > 0)
            ColumnDetected?.Invoke(column);
    }

    /// <summary>Windowed-sinc low-pass for 2:1 decimation (cut-off just below 11 kHz).</summary>
    private static float[] CreateHalfBand(int taps)
    {
        var h = new float[taps];
        int middle = taps / 2;
        double sum = 0;
        for (int i = 0; i < taps; i++)
        {
            int n = i - middle;
            double sinc = n == 0 ? 0.45 * 2 : Math.Sin(2 * Math.PI * 0.225 * n) / (Math.PI * n) * 2;
            double blackman = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (taps - 1)) + 0.08 * Math.Cos(4 * Math.PI * i / (taps - 1));
            h[i] = (float)(sinc * blackman);
            sum += h[i];
        }

        for (int i = 0; i < taps; i++)
            h[i] = (float)(h[i] / sum);
        return h;
    }
}
