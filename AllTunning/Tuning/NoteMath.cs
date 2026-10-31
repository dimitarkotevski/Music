namespace AllTunning.Tuning;

public static class NoteMath
{
    private static readonly string[] Names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    public static double MidiToFrequency(double midi, double a4) => a4 * Math.Pow(2, (midi - 69) / 12.0);

    public static double FrequencyToMidi(double frequency, double a4) => 69 + 12 * Math.Log2(frequency / a4);

    public static double Cents(double frequency, double target) => 1200 * Math.Log2(frequency / target);

    public static string Name(int midi) => Names[((midi % 12) + 12) % 12];

    public static int Octave(int midi) => midi / 12 - 1;

    /// <summary>Parses notes like "E2", "F#3" or "Eb2" into a MIDI note number.</summary>
    public static int Parse(string note)
    {
        int semitone = char.ToUpperInvariant(note[0]) switch
        {
            'C' => 0, 'D' => 2, 'E' => 4, 'F' => 5, 'G' => 7, 'A' => 9, 'B' => 11,
            _ => throw new FormatException($"Invalid note '{note}'.")
        };

        int i = 1;
        if (note[i] == '#') { semitone++; i++; }
        else if (note[i] == 'b') { semitone--; i++; }

        int octave = int.Parse(note[i..]);
        return (octave + 1) * 12 + semitone;
    }
}
