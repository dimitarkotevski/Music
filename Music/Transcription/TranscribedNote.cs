namespace Music.Transcription;

/// <summary>One note produced by an audio transcription model. Times are seconds from the start of the file.</summary>
public sealed record TranscribedNote(double Start, double End, int Midi, double Amplitude = 1);

public sealed record TranscriptionResult(IReadOnlyList<TranscribedNote> Notes, double Duration);
