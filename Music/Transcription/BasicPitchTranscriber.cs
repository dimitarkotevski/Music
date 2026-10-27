using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Music.Transcription;

public interface IFileTranscriber
{
    Task<TranscriptionResult> TranscribeAsync(string audioPath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs the Python Basic Pitch worker as an isolated process. Keeping the model
/// behind this class means a future ONNX or YourMT3+ backend can replace it
/// without touching the fretboard arranger or the Songs screen.
/// </summary>
public sealed class BasicPitchTranscriber : IFileTranscriber
{
    public string? PythonPath { get; init; }
    public string? WorkerPath { get; init; }
    public double OnsetThreshold { get; init; } = 0.5;
    public double FrameThreshold { get; init; } = 0.3;
    public double MinimumNoteMilliseconds { get; init; } = 60;

    public async Task<TranscriptionResult> TranscribeAsync(
        string audioPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(audioPath))
            throw new FileNotFoundException("The audio file was not found.", audioPath);

        string python = FindPython(PythonPath);
        string worker = FindWorker(WorkerPath);
        string outputDirectory = Path.Combine(Path.GetTempPath(), "MusicTranscription");
        Directory.CreateDirectory(outputDirectory);
        string outputPath = Path.Combine(outputDirectory, $"notes-{Guid.NewGuid():N}.json");

        try
        {
            var startInfo = new ProcessStartInfo(python)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(worker) ?? AppContext.BaseDirectory,
            };
            startInfo.ArgumentList.Add(worker);
            startInfo.ArgumentList.Add("--input");
            startInfo.ArgumentList.Add(audioPath);
            startInfo.ArgumentList.Add("--output");
            startInfo.ArgumentList.Add(outputPath);
            startInfo.ArgumentList.Add("--onset");
            startInfo.ArgumentList.Add(OnsetThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--frame");
            startInfo.ArgumentList.Add(FrameThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--min-note-ms");
            startInfo.ArgumentList.Add(MinimumNoteMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                throw new InvalidOperationException("Could not start the transcription worker.");

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();

            await using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // The process may have exited between the check and Kill.
                }
            });

            await process.WaitForExitAsync(cancellationToken);
            string stdout = await stdoutTask;
            string stderr = await stderrTask;

            if (!File.Exists(outputPath))
            {
                string detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                throw new InvalidOperationException(
                    "The transcription worker did not produce a result. " + Shorten(detail));
            }

            string json = await File.ReadAllTextAsync(outputPath, cancellationToken);
            var payload = JsonSerializer.Deserialize<WorkerPayload>(json, JsonOptions)
                ?? throw new InvalidOperationException("The transcription worker returned invalid JSON.");

            if (!string.IsNullOrWhiteSpace(payload.Error))
                throw new InvalidOperationException(payload.Error);

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"The transcription worker exited with code {process.ExitCode}. {Shorten(stderr)}");

            var notes = (payload.Notes ?? [])
                .Where(n => n.End > n.Start && n.Midi is >= 0 and <= 127)
                .Select(n => new TranscribedNote(n.Start, n.End, n.Midi, n.Amplitude))
                .ToList();

            return new TranscriptionResult(notes, payload.Duration);
        }
        finally
        {
            try
            {
                if (File.Exists(outputPath))
                    File.Delete(outputPath);
            }
            catch
            {
                // A leftover temp file is harmless.
            }
        }
    }

    private static string FindPython(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string value = Environment.ExpandEnvironmentVariables(configured.Trim());
            if (Directory.Exists(value))
                value = Path.Combine(value, "Scripts", "python.exe");
            if (File.Exists(value) || value == "python")
                return value;
            throw new FileNotFoundException("The configured Python interpreter was not found.", value);
        }

        string? environment = Environment.GetEnvironmentVariable("MUSIC_PYTHON");
        if (!string.IsNullOrWhiteSpace(environment))
        {
            string value = Environment.ExpandEnvironmentVariables(environment.Trim());
            if (Directory.Exists(value))
                value = Path.Combine(value, "Scripts", "python.exe");
            if (File.Exists(value))
                return value;
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, ".venv-transcription", "Scripts", "python.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        return "python";
    }

    private static string FindWorker(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string value = Environment.ExpandEnvironmentVariables(configured.Trim());
            if (File.Exists(value))
                return value;
            throw new FileNotFoundException("The configured transcription worker was not found.", value);
        }

        var roots = new List<string> { AppContext.BaseDirectory, Environment.CurrentDirectory };
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            roots.Add(directory.FullName);

        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string candidate = Path.Combine(root, "tools", "basic_pitch_worker.py");
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            "The Basic Pitch worker was not found. Expected tools\\basic_pitch_worker.py next to the app.");
    }

    private static string Shorten(string text)
    {
        text = text.Trim();
        return text.Length <= 700 ? text : text[^700..];
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class WorkerPayload
    {
        public string? Model { get; set; }
        public double Duration { get; set; }
        public List<WorkerNote>? Notes { get; set; }
        public string? Error { get; set; }
    }

    private sealed class WorkerNote
    {
        public double Start { get; set; }
        public double End { get; set; }
        public int Midi { get; set; }
        public double Amplitude { get; set; } = 1;
    }
}
