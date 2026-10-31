using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AllTunning.Transcription;

/// <summary>Note and onset activations for one model window: [frame, key], key 0 = MIDI 21 (A0).</summary>
public sealed record BasicPitchFrames(float[,] Notes, float[,] Onsets);

/// <summary>
/// Spotify's Basic Pitch network (ICASSP 2022, Apache 2.0) run in-process through ONNX Runtime.
/// One call analyses two seconds of 22.05 kHz mono audio and returns 172 frames of 88 piano keys.
/// The live Tabs screen uses it directly; the Songs screen still runs the Python package.
/// </summary>
public sealed class BasicPitchModel : IDisposable
{
    public const int SampleRate = 22050;
    public const int FrameHop = 256;
    public const int WindowSamples = SampleRate * 2 - FrameHop; // 43844
    public const int WindowFrames = 172;
    public const int Keys = 88;
    public const int LowestMidi = 21;
    public const double FramesPerSecond = (double)SampleRate / FrameHop;

    private const string InputName = "serving_default_input_2:0";
    private const string NoteOutput = "StatefulPartitionedCall:1";
    private const string OnsetOutput = "StatefulPartitionedCall:2";

    private readonly InferenceSession session;
    private readonly object runLock = new();

    public BasicPitchModel(string modelPath)
    {
        var options = new SessionOptions
        {
            // Leave cores for audio capture and the UI.
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        session = new InferenceSession(modelPath, options);
    }

    /// <summary>The model shipped next to the app, or null when it is missing.</summary>
    public static string? DefaultPath
    {
        get
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Models", "basic-pitch.onnx");
            return File.Exists(path) ? path : null;
        }
    }

    /// <param name="audio">Exactly <see cref="WindowSamples"/> samples at <see cref="SampleRate"/>.</param>
    public BasicPitchFrames Predict(float[] audio)
    {
        if (audio.Length != WindowSamples)
            throw new ArgumentException($"Expected {WindowSamples} samples.", nameof(audio));

        var input = new DenseTensor<float>(audio, [1, WindowSamples, 1]);
        lock (runLock)
        {
            using var results = session.Run(
                [NamedOnnxValue.CreateFromTensor(InputName, input)],
                [NoteOutput, OnsetOutput]);
            return new BasicPitchFrames(
                ToMatrix(results.First(r => r.Name == NoteOutput).AsTensor<float>()),
                ToMatrix(results.First(r => r.Name == OnsetOutput).AsTensor<float>()));
        }
    }

    public void Dispose() => session.Dispose();

    private static float[,] ToMatrix(Tensor<float> tensor)
    {
        int frames = tensor.Dimensions[1];
        int keys = tensor.Dimensions[2];
        var matrix = new float[frames, keys];
        for (int f = 0; f < frames; f++)
            for (int k = 0; k < keys; k++)
                matrix[f, k] = tensor[0, f, k];
        return matrix;
    }
}
