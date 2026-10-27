using System.Globalization;
using System.Text;
using System.Xml;

namespace Music.Transcription;

/// <summary>
/// Writes a compact MusicXML tab score. It intentionally uses one quarter-note
/// column per onset; MIDI export preserves the original timing more exactly.
/// </summary>
public static class MusicXmlExporter
{
    private const int Divisions = 480;
    private const int ColumnDuration = 480;
    private const int ColumnsPerMeasure = 4;

    public static void Export(
        string path,
        IReadOnlyList<SongTabColumn> columns,
        int[] tuning,
        string? title = null)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = new UTF8Encoding(false),
        };

        using var writer = XmlWriter.Create(path, settings);
        writer.WriteStartDocument();
        writer.WriteStartElement("score-partwise");
        writer.WriteAttributeString("version", "4.0");

        writer.WriteStartElement("work");
        writer.WriteElementString("work-title", string.IsNullOrWhiteSpace(title) ? "Guitar tab" : title.Trim());
        writer.WriteEndElement();

        writer.WriteStartElement("part-list");
        writer.WriteStartElement("score-part");
        writer.WriteAttributeString("id", "P1");
        writer.WriteElementString("part-name", "Guitar");
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("part");
        writer.WriteAttributeString("id", "P1");

        int measureNumber = 1;
        OpenMeasure(writer, measureNumber, tuning);
        int inMeasure = 0;

        if (columns.Count == 0)
        {
            WriteRest(writer);
        }
        else
        {
            foreach (var column in columns)
            {
                if (inMeasure == ColumnsPerMeasure)
                {
                    writer.WriteEndElement(); // measure
                    measureNumber++;
                    OpenMeasure(writer, measureNumber, tuning);
                    inMeasure = 0;
                }

                if (column.Notes.Count == 0)
                {
                    WriteRest(writer);
                }
                else
                {
                    bool first = true;
                    foreach (var note in column.Notes)
                    {
                        WriteNote(writer, note, tuning, first);
                        first = false;
                    }
                }

                inMeasure++;
            }
        }

        writer.WriteEndElement(); // measure
        writer.WriteEndElement(); // part
        writer.WriteEndElement(); // score-partwise
        writer.WriteEndDocument();
    }

    private static void OpenMeasure(XmlWriter writer, int number, int[] tuning)
    {
        writer.WriteStartElement("measure");
        writer.WriteAttributeString("number", number.ToString(CultureInfo.InvariantCulture));

        if (number == 1)
        {
            writer.WriteStartElement("attributes");
            writer.WriteElementString("divisions", Divisions.ToString(CultureInfo.InvariantCulture));
            writer.WriteStartElement("key");
            writer.WriteElementString("fifths", "0");
            writer.WriteEndElement();
            writer.WriteStartElement("time");
            writer.WriteElementString("beats", "4");
            writer.WriteElementString("beat-type", "4");
            writer.WriteEndElement();
            writer.WriteStartElement("clef");
            writer.WriteElementString("sign", "TAB");
            writer.WriteElementString("line", "5");
            writer.WriteEndElement();
            writer.WriteStartElement("staff-details");
            writer.WriteElementString("staff-lines", tuning.Length.ToString(CultureInfo.InvariantCulture));
            for (int i = tuning.Length - 1; i >= 0; i--)
            {
                var (step, alter, octave) = Pitch(tuning[i]);
                writer.WriteStartElement("staff-tuning");
                writer.WriteAttributeString("line", (tuning.Length - i).ToString(CultureInfo.InvariantCulture));
                writer.WriteElementString("tuning-step", step);
                if (alter != 0)
                    writer.WriteElementString("tuning-alter", alter.ToString(CultureInfo.InvariantCulture));
                writer.WriteElementString("tuning-octave", octave.ToString(CultureInfo.InvariantCulture));
                writer.WriteEndElement();
            }

            writer.WriteEndElement(); // staff-details
            writer.WriteEndElement(); // attributes

            writer.WriteStartElement("direction");
            writer.WriteAttributeString("placement", "above");
            writer.WriteStartElement("direction-type");
            writer.WriteStartElement("metronome");
            writer.WriteAttributeString("parentheses", "no");
            writer.WriteElementString("beat-unit", "quarter");
            writer.WriteElementString("per-minute", "120");
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
        }
    }

    private static void WriteRest(XmlWriter writer)
    {
        writer.WriteStartElement("note");
        writer.WriteElementString("rest", "");
        writer.WriteElementString("duration", Divisions.ToString(CultureInfo.InvariantCulture));
        writer.WriteElementString("voice", "1");
        writer.WriteElementString("type", "quarter");
        writer.WriteEndElement();
    }

    private static void WriteNote(XmlWriter writer, ArrangedNote note, int[] tuning, bool first)
    {
        writer.WriteStartElement("note");
        if (!first)
            writer.WriteElementString("chord", "");

        var (step, alter, octave) = Pitch(note.Midi);
        writer.WriteStartElement("pitch");
        writer.WriteElementString("step", step);
        if (alter != 0)
            writer.WriteElementString("alter", alter.ToString(CultureInfo.InvariantCulture));
        writer.WriteElementString("octave", octave.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement();

        writer.WriteElementString("duration", ColumnDuration.ToString(CultureInfo.InvariantCulture));
        writer.WriteElementString("voice", "1");
        writer.WriteElementString("type", "quarter");

        writer.WriteStartElement("notations");
        writer.WriteStartElement("technical");
        writer.WriteElementString("string", (tuning.Length - note.Position.String).ToString(CultureInfo.InvariantCulture));
        writer.WriteElementString("fret", note.Position.Fret.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteEndElement();
    }

    private static (string Step, int Alter, int Octave) Pitch(int midi)
    {
        string[] names = ["C", "C", "D", "D", "E", "F", "F", "G", "G", "A", "A", "B"];
        int[] alters = [0, 1, 0, 1, 0, 0, 1, 0, 1, 0, 1, 0];
        int pitchClass = ((midi % 12) + 12) % 12;
        int octave = midi / 12 - 1;
        return (names[pitchClass], alters[pitchClass], octave);
    }
}
