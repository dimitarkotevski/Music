using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using AllTunning.Tabs;
using AllTunning.Transcription;
using AllTunning.Tuning;

namespace AllTunning;

/// <summary>Songs screen: audio file → Basic Pitch notes → playable tablature.</summary>
public partial class MainWindow
{
    private readonly FretboardArranger songArranger = new();
    private CancellationTokenSource? songCancellation;
    private TranscriptionResult? songTranscription;
    private IReadOnlyList<SongTabColumn> songColumns = [];
    private GuitarTuning? songTuning;
    private string? songFilePath;
    private bool songBusy;

    private void InitializeSongs()
    {
        SongTuningBox.ItemsSource = GuitarTuning.All;
        SongTuningBox.SelectedItem = tuning;
        SongTuningBox.SelectionChanged += SongTuningBox_SelectionChanged;
        SongSoloBox.IsChecked = false;
        SongStaffView.NoteClicked += MoveSongNoteToNextPosition;
        SongStaffView.NoteRightClicked += RemoveSongNote;
        SongStaffView.Tuning = tuning.Strings;
        SongFileText.Text = "No audio file selected";
        SongStatusText.Text = "Choose an audio file to begin.";
        SongInfoText.Text = "Basic Pitch listens to the file; the arranger chooses strings and frets.";
    }

    private void StartSongs_Click(object sender, RoutedEventArgs e)
    {
        StopTabTest();
        StopMetronome();
        listener.Stop();
        chordListener.Stop();
        tabListener.Stop();
        StopTabDebugLog();
        tonePlayer.Stop();
        StartMenu.Visibility = Visibility.Collapsed;
        SongView.Visibility = Visibility.Visible;
    }

    private void BrowseSong_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a song",
            Filter = "Audio files|*.wav;*.mp3;*.flac;*.ogg;*.aiff;*.aif;*.m4a|All files|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
            return;

        songFilePath = dialog.FileName;
        songTranscription = null;
        SetSongColumns([]);
        SongFileText.Text = songFilePath;
        SongStatusText.Text = "Ready to transcribe.";
        SongInfoText.Text = Path.GetFileName(songFilePath);
        SongHintText.Text = "Transcription is local. Nothing is uploaded.";
    }

    private async void TranscribeSong_Click(object sender, RoutedEventArgs e)
    {
        if (songBusy)
            return;

        if (string.IsNullOrWhiteSpace(songFilePath) || !File.Exists(songFilePath))
        {
            SongStatusText.Text = "Choose an audio file first.";
            return;
        }

        if (SongTuningBox.SelectedItem is not GuitarTuning selectedTuning)
        {
            SongStatusText.Text = "Choose a tuning first.";
            return;
        }

        songCancellation?.Cancel();
        songCancellation?.Dispose();
        songCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = songCancellation.Token;
        SetSongBusy(true);
        SetSongColumns([]);
        songTranscription = null;
        SongInfoText.Text = Path.GetFileName(songFilePath);
        SongStatusText.Text = "Listening to the file with Basic Pitch…";

        try
        {
            var transcriber = new BasicPitchTranscriber();
            var transcription = await transcriber.TranscribeAsync(songFilePath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            songTranscription = transcription;

            SongStatusText.Text = $"Found {transcription.Notes.Count} notes. Choosing strings and frets…";
            var arrangement = songArranger.Arrange(
                transcription.Notes,
                selectedTuning.Strings,
                mode: SongSoloBox.IsChecked == true ? ArrangementMode.Solo : ArrangementMode.Chords);
            ApplySongArrangement(arrangement, selectedTuning, transcription.Duration);
        }
        catch (OperationCanceledException)
        {
            SongStatusText.Text = "Transcription cancelled.";
            SongInfoText.Text = " ";
        }
        catch (Exception ex)
        {
            SongStatusText.Text = "Could not transcribe this file.";
            SongInfoText.Text = ex.Message;
            SongHintText.Text = "Install the Python dependencies once with tools\\setup-transcription.ps1, then try again.";
        }
        finally
        {
            SetSongBusy(false);
        }
    }

    private void CancelSong_Click(object sender, RoutedEventArgs e)
    {
        songCancellation?.Cancel();
        SongStatusText.Text = "Cancelling…";
    }

    private void SongTuningBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SongTuningBox.SelectedItem is not GuitarTuning selectedTuning)
            return;

        SongStaffView.Tuning = selectedTuning.Strings;
        SongStaffView.Refresh();

        if (songTranscription is { Notes.Count: > 0 } transcription && !songBusy)
        {
            SongStatusText.Text = "Re-arranging for the new tuning…";
            var arrangement = songArranger.Arrange(
                transcription.Notes,
                selectedTuning.Strings,
                mode: SongSoloBox.IsChecked == true ? ArrangementMode.Solo : ArrangementMode.Chords);
            ApplySongArrangement(arrangement, selectedTuning, transcription.Duration);
        }
    }

    private void SongSoloBox_Changed(object sender, RoutedEventArgs e)
    {
        if (songBusy || songTranscription is not { Notes.Count: > 0 } transcription)
            return;

        if (SongTuningBox.SelectedItem is not GuitarTuning selectedTuning)
            return;

        SongStatusText.Text = "Re-arranging…";
        var arrangement = songArranger.Arrange(
            transcription.Notes,
            selectedTuning.Strings,
            mode: SongSoloBox.IsChecked == true ? ArrangementMode.Solo : ArrangementMode.Chords);
        ApplySongArrangement(arrangement, selectedTuning, transcription.Duration);
    }

    private void ApplySongArrangement(ArrangementResult arrangement, GuitarTuning selectedTuning, double duration)
    {
        songTuning = selectedTuning;
        SetSongColumns(arrangement.Columns);

        int totalNotes = arrangement.Columns.Sum(column => column.Notes.Count);
        if (arrangement.Columns.Count == 0)
        {
            SongStatusText.Text = "No playable notes were found.";
            SongInfoText.Text = "Try a cleaner recording with a single guitar, or choose another tuning.";
            return;
        }

        double averageConfidence = arrangement.Columns.Average(column => column.Confidence);
        SongStatusText.Text = $"Transcribed {totalNotes} notes into {arrangement.Columns.Count} tab columns.";
        var details = $"{FormatTime(duration)} · {selectedTuning.Name} · average confidence {averageConfidence:0%}";
        details += SongSoloBox.IsChecked == true ? " · solo" : " · chords";
        if (arrangement.DroppedNotes > 0)
            details += $" · {arrangement.DroppedNotes} out-of-range notes ignored";
        if (arrangement.UnplayableColumns > 0)
            details += $" · {arrangement.UnplayableColumns} unplayable notes skipped";
        SongInfoText.Text = details;
        SongHintText.Text = "Click a note to move it to the next string; right-click to remove it.";
    }

    private void SetSongColumns(IReadOnlyList<SongTabColumn> columns)
    {
        songColumns = columns;
        SongStaffView.Columns = columns;
        SongStaffView.Refresh();
    }

    private void RemoveSongNote(int columnIndex, int noteIndex)
    {
        if (columnIndex < 0 || columnIndex >= songColumns.Count)
            return;

        var column = songColumns[columnIndex];
        if (noteIndex < 0 || noteIndex >= column.Notes.Count)
            return;

        var notes = column.Notes.ToList();
        notes.RemoveAt(noteIndex);
        var updated = songColumns.ToList();
        if (notes.Count == 0)
            updated.RemoveAt(columnIndex);
        else
            updated[columnIndex] = column with { Notes = notes, Confidence = 1 };

        SetSongColumns(updated);
        SongStatusText.Text = notes.Count == 0 ? "Removed the column." : "Removed note.";
        UpdateSongEditInfo();
    }

    private void MoveSongNoteToNextPosition(int columnIndex, int noteIndex)
    {
        if (columnIndex < 0 || columnIndex >= songColumns.Count)
            return;

        var column = songColumns[columnIndex];
        if (noteIndex < 0 || noteIndex >= column.Notes.Count)
            return;

        var note = column.Notes[noteIndex];
        var usedStrings = column.Notes
            .Where((_, index) => index != noteIndex)
            .Select(n => n.Position.String)
            .ToHashSet();
        var candidates = FretAssigner.Positions(note.Midi, (songTuning ?? tuning).Strings)
            .Where(position => !usedStrings.Contains(position.String))
            .ToList();

        if (candidates.Count <= 1)
        {
            SongStatusText.Text = candidates.Count == 0
                ? "That note has no free string in this column."
                : "That note has only one playable position.";
            return;
        }

        int current = candidates.FindIndex(position => position == note.Position);
        var next = candidates[(current + 1) % candidates.Count];
        var notes = column.Notes.ToList();
        notes[noteIndex] = note with { Position = next };
        var updated = songColumns.ToList();
        updated[columnIndex] = column with { Notes = notes, Confidence = 1 };

        SetSongColumns(updated);
        SongStatusText.Text = "Moved " + NoteMath.Name(note.Midi) + " to " +
            TabText.StringName((songTuning ?? tuning).Strings, next.String) + ", fret " + next.Fret + ".";
        UpdateSongEditInfo();
    }

    private void UpdateSongEditInfo()
    {
        int notes = songColumns.Sum(column => column.Notes.Count);
        SongInfoText.Text = notes == 0
            ? "No notes left in the tab."
            : $"{notes} notes · {songColumns.Count} tab columns · manual edits";
    }

    private void CopySongTab_Click(object sender, RoutedEventArgs e)
    {
        if (songColumns.Count == 0)
        {
            SongStatusText.Text = "There is no tab to copy yet.";
            return;
        }

        try
        {
            string title = songFilePath is null ? "Guitar tab" : Path.GetFileNameWithoutExtension(songFilePath);
            string tab = SongTabText.Format(songColumns, (songTuning ?? tuning).Strings, 24, title);
            Clipboard.SetText(tab);
            SongStatusText.Text = "Tab copied to the clipboard.";
        }
        catch (Exception ex)
        {
            SongStatusText.Text = "Could not copy the tab: " + ex.Message;
        }
    }

    private void ExportSongMidi_Click(object sender, RoutedEventArgs e)
    {
        if (songColumns.Count == 0)
        {
            SongStatusText.Text = "There is no tab to export yet.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export MIDI",
            Filter = "MIDI file|*.mid",
            FileName = songFilePath is null ? "guitar-tab.mid" : Path.GetFileNameWithoutExtension(songFilePath) + ".mid",
            AddExtension = true,
            DefaultExt = ".mid",
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            MidiExporter.Export(dialog.FileName, songColumns);
            SongStatusText.Text = "MIDI exported: " + Path.GetFileName(dialog.FileName);
        }
        catch (Exception ex)
        {
            SongStatusText.Text = "Could not export MIDI: " + ex.Message;
        }
    }

    private void ExportSongMusicXml_Click(object sender, RoutedEventArgs e)
    {
        if (songColumns.Count == 0)
        {
            SongStatusText.Text = "There is no tab to export yet.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export MusicXML",
            Filter = "MusicXML file|*.musicxml;*.xml",
            FileName = songFilePath is null ? "guitar-tab.musicxml" : Path.GetFileNameWithoutExtension(songFilePath) + ".musicxml",
            AddExtension = true,
            DefaultExt = ".musicxml",
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            string title = songFilePath is null ? "Guitar tab" : Path.GetFileNameWithoutExtension(songFilePath);
            MusicXmlExporter.Export(dialog.FileName, songColumns, (songTuning ?? tuning).Strings, title);
            SongStatusText.Text = "MusicXML exported: " + Path.GetFileName(dialog.FileName);
        }
        catch (Exception ex)
        {
            SongStatusText.Text = "Could not export MusicXML: " + ex.Message;
        }
    }

    private void ClearSong_Click(object sender, RoutedEventArgs e)
    {
        SetSongColumns([]);
        songTranscription = null;
        SongStatusText.Text = songFilePath is null ? "Choose an audio file to begin." : "Ready to transcribe.";
        SongInfoText.Text = songFilePath is null ? " " : Path.GetFileName(songFilePath);
    }

    private void SetSongBusy(bool busy)
    {
        songBusy = busy;
        SongTranscribeButton.IsEnabled = !busy;
        SongBrowseButton.IsEnabled = !busy;
        SongCancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SongProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SongTuningBox.IsEnabled = !busy;
        SongSoloBox.IsEnabled = !busy;
    }

    private void StopSongWorker()
    {
        songCancellation?.Cancel();
        songCancellation?.Dispose();
        songCancellation = null;
    }

    private static string FormatTime(double seconds)
    {
        int total = (int)Math.Max(0, seconds);
        return total >= 3600
            ? $"{total / 3600}:{total / 60 % 60:00}:{total % 60:00}"
            : $"{total / 60}:{total % 60:00}";
    }
}
