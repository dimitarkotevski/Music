using NAudio.Midi;

namespace AllTunning.Transcription;

/// <summary>Writes the arranged song as a Standard MIDI File.</summary>
public static class MidiExporter
{
    public static void Export(
        string path,
        IReadOnlyList<SongTabColumn> columns,
        double bpm = 120,
        int channel = 1)
    {
        const int ticksPerQuarterNote = 480;
        int tempoMicroseconds = (int)Math.Round(60_000_000 / Math.Clamp(bpm, 20, 400));
        double ticksPerSecond = ticksPerQuarterNote * bpm / 60.0;

        var events = new MidiEventCollection(1, ticksPerQuarterNote);
        events.AddTrack();
        var track = new List<MidiEvent>
        {
            new TextEvent("Guitar transcription", MetaEventType.SequenceTrackName, 0),
            new TempoEvent(0, tempoMicroseconds),
            new PatchChangeEvent(0, channel, 25), // acoustic guitar (steel)
        };

        foreach (var column in columns)
        {
            foreach (var note in column.Notes)
            {
                long start = (long)Math.Round(note.Start * ticksPerSecond);
                long end = (long)Math.Round(note.End * ticksPerSecond);
                int length = (int)Math.Max(1, end - start);
                var noteOn = new NoteOnEvent(start, channel, note.Midi, 90, length);
                track.Add(noteOn);
                if (noteOn.OffEvent is not null)
                    track.Add(noteOn.OffEvent);
            }
        }

        // Note-offs at the same tick must precede note-ons so repeated pitches retrigger cleanly.
        track.Sort((a, b) =>
        {
            int byTime = a.AbsoluteTime.CompareTo(b.AbsoluteTime);
            if (byTime != 0)
                return byTime;

            bool aOff = a.CommandCode == MidiCommandCode.NoteOff;
            bool bOff = b.CommandCode == MidiCommandCode.NoteOff;
            return aOff == bOff ? 0 : aOff ? -1 : 1;
        });

        foreach (var midiEvent in track)
            events.AddEvent(midiEvent, 0);

        MidiFile.Export(path, events);
    }
}
