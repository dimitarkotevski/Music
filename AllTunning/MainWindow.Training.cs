using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using AllTunning.Tabs;
using AllTunning.Transcription;
using AllTunning.Tuning;

namespace AllTunning;

/// <summary>
/// String training (Tabs screen): the app shows a short tab phrase, the player plays it, and every note
/// the AI detector hears is labelled with the string and fret the phrase asked for. Those labelled
/// notes teach <see cref="StringTimbreModel"/> how each string of this guitar sounds through this mic.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan TrainPhrasePause = TimeSpan.FromSeconds(1.2);

    private StringTimbreModel stringSounds = StringTimbreModel.Load(StringTimbreModel.DefaultPath);
    private IReadOnlyList<TrainingPhrase> trainPhrases = [];
    private readonly List<StringSoundExample> trainUnsaved = [];
    private DispatcherTimer? trainAdvanceTimer;
    private bool trainRunning;
    private int trainPhraseIndex;
    private int trainNoteIndex;
    private int trainLabelled;
    private string trainSession = "";
    private string? trainWavPath;

    private static string TrainingFolder => Path.GetDirectoryName(StringTimbreModel.DefaultPath)!;

    private void StartTraining_Click(object sender, RoutedEventArgs e)
    {
        if (trainRunning)
            return;

        if (liveNotes is null)
        {
            MessageBox.Show("Training needs the AI detector, and its model file (Models\\basic-pitch.onnx) is missing.",
                "String training", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (TabDeviceBox.SelectedIndex < 0)
        {
            MessageBox.Show("No microphone found. Choose one in the Microphone list first.",
                "String training", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Training labels what the AI detector hears; switching restarts the microphone.
        if (!useAiTabs)
            TabEngineBox.SelectedIndex = 0;
        else
            StartTabListening();

        if (!tabListener.IsRunning)
        {
            MessageBox.Show(TabFretText.Text, "String training", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        trainSession = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        try
        {
            Directory.CreateDirectory(TrainingFolder);
            trainWavPath = Path.Combine(TrainingFolder, $"training-{trainSession}.wav");
            tabListener.StartRecording(trainWavPath);
        }
        catch (Exception ex)
        {
            // The recording is only for checking the session later; training itself still works.
            trainWavPath = null;
            LogTabDebug("training: could not record: " + ex.Message);
        }

        trainPhrases = TrainingPhrases.Create(tuning.Strings);
        trainUnsaved.Clear();
        trainLabelled = 0;
        trainRunning = true;
        TrainPanel.Visibility = Visibility.Visible;
        TrainTitle.Text = "STRING TRAINING";
        TrainSkipButton.Visibility = Visibility.Visible;
        TrainFinishButton.Content = "Finish";
        TrainHintText.Visibility = Visibility.Visible;
        ShowTrainingPhrase(0);
    }

    private void ShowTrainingPhrase(int index)
    {
        trainPhraseIndex = index;
        trainNoteIndex = 0;
        var phrase = trainPhrases[index];
        TrainPhraseText.Text = phrase.Title;
        TrainStaff.Tuning = tuning.Strings;
        TrainStaff.Notes = phrase.Notes.Select(p => new TabNote(tuning.Strings[p.String] + p.Fret, p, GapBefore: false)).ToList();
        ShowTrainingNote();
    }

    private void ShowTrainingNote()
    {
        var phrase = trainPhrases[trainPhraseIndex];
        TrainProgressText.Text = $"Phrase {trainPhraseIndex + 1} / {trainPhrases.Count}";
        TrainStatusText.Text = $"{trainLabelled} notes learned this session · {stringSounds.Count + trainUnsaved.Count} in total";

        if (trainNoteIndex >= phrase.Notes.Count)
        {
            TrainNoteText.Text = "✓";
            TrainInstructionText.Text = "Phrase done";
            TrainStaff.HighlightIndex = -1;
            TrainStaff.Refresh();
            return;
        }

        var target = phrase.Notes[trainNoteIndex];
        int midi = tuning.Strings[target.String] + target.Fret;
        TrainNoteText.Text = NoteMath.Name(midi) + NoteMath.Octave(midi);
        TrainInstructionText.Text = TabText.StringName(tuning.Strings, target.String)
            + (target.Fret == 0 ? " — open" : $" — fret {target.Fret}");
        TrainStaff.HighlightIndex = trainNoteIndex;
        TrainStaff.Refresh();
    }

    /// <summary>Called instead of writing to the tab while training: label the note if it is the one asked for.</summary>
    private void TrainingColumn(IReadOnlyList<TranscribedNote> column)
    {
        if (trainAdvanceTimer is not null)
            return;

        var phrase = trainPhrases[trainPhraseIndex];

        // Accept the asked note, or the one after it if the player played on and the detector missed one.
        for (int look = trainNoteIndex; look < Math.Min(phrase.Notes.Count, trainNoteIndex + 2); look++)
        {
            var target = phrase.Notes[look];
            int midi = tuning.Strings[target.String] + target.Fret;
            if (column.FirstOrDefault(n => n.Midi == midi) is not TranscribedNote heard)
                continue;

            if (liveNotes?.SoundProfile(heard.Start, heard.Midi) is double[] profile)
            {
                trainUnsaved.Add(new StringSoundExample(
                    midi, target.String, target.Fret, tuning.Strings[target.String], profile, trainSession, heard.Start));
                trainLabelled++;
            }

            LogTabDebug($"training: heard {DebugNote(midi)} as asked on string {tuning.Strings.Length - target.String} fret {target.Fret}");
            trainNoteIndex = look + 1;
            ShowTrainingNote();
            if (trainNoteIndex >= phrase.Notes.Count)
                CompleteTrainingPhrase();
            return;
        }

        // Tell the player what was heard, so a wrong note or an octave slip is obvious.
        var loudest = column.MaxBy(n => n.Amplitude)!;
        TrainStatusText.Text = $"Heard {NoteMath.Name(loudest.Midi)}{NoteMath.Octave(loudest.Midi)} — waiting for {TrainNoteText.Text}";
    }

    private void CompleteTrainingPhrase()
    {
        SaveTrainingExamples();
        trainAdvanceTimer = new DispatcherTimer { Interval = TrainPhrasePause };
        trainAdvanceTimer.Tick += (_, _) =>
        {
            trainAdvanceTimer?.Stop();
            trainAdvanceTimer = null;
            if (!trainRunning)
                return;
            if (trainPhraseIndex + 1 < trainPhrases.Count)
                ShowTrainingPhrase(trainPhraseIndex + 1);
            else
                FinishTraining();
        };
        trainAdvanceTimer.Start();
    }

    /// <summary>Learned notes are saved after every phrase, so closing the app never loses a session.</summary>
    private void SaveTrainingExamples()
    {
        if (trainUnsaved.Count == 0)
            return;

        try
        {
            stringSounds.Add(trainUnsaved);
            stringSounds.Save(StringTimbreModel.DefaultPath);
            trainUnsaved.Clear();
        }
        catch (Exception ex)
        {
            TrainStatusText.Text = "Could not save the training data: " + ex.Message;
        }
    }

    private void SkipTrainingPhrase_Click(object sender, RoutedEventArgs e)
    {
        if (!trainRunning || trainAdvanceTimer is not null)
            return;

        SaveTrainingExamples();
        if (trainPhraseIndex + 1 < trainPhrases.Count)
            ShowTrainingPhrase(trainPhraseIndex + 1);
        else
            FinishTraining();
    }

    private void FinishTraining_Click(object sender, RoutedEventArgs e)
    {
        if (trainRunning)
            FinishTraining();
        else
            TrainPanel.Visibility = Visibility.Collapsed;
    }

    private void FinishTraining()
    {
        StopTraining();

        TrainPanel.Visibility = Visibility.Visible;
        TrainTitle.Text = "TRAINING SAVED";
        TrainPhraseText.Text = "";
        TrainNoteText.Text = "✓";
        TrainInstructionText.Text = $"{trainLabelled} notes learned this session";
        TrainStaff.Notes = [];
        TrainStaff.Refresh();
        TrainProgressText.Text = TrainingSummary();
        TrainStatusText.Text = trainWavPath is null ? "" : "Recording:\n" + trainWavPath;
        TrainHintText.Visibility = Visibility.Collapsed;
        TrainSkipButton.Visibility = Visibility.Collapsed;
        TrainFinishButton.Content = "Close";
    }

    /// <summary>The model's own test result, in plain words.</summary>
    private string TrainingSummary()
    {
        if (stringSounds.Evaluated < 20)
            return $"{stringSounds.Count} notes so far. Play a few more phrases before the app can tell your strings apart by sound.";

        string verdict = stringSounds.Weight <= 0
            ? "Your strings sound too similar through this mic to tell apart; string choice stays as before."
            : stringSounds.Weight < 0.7
                ? "It helps a little with choosing the string."
                : "It now helps clearly with choosing the string.";
        return $"Self-test on {stringSounds.Evaluated} notes: the right string {stringSounds.Accuracy:P0} of the time "
            + $"(a guess: {stringSounds.Chance:P0}). {verdict}";
    }

    /// <summary>Stops a training session and keeps what was learned. Also used when leaving the Tabs screen.</summary>
    private void StopTraining()
    {
        if (!trainRunning)
            return;

        trainRunning = false;
        trainAdvanceTimer?.Stop();
        trainAdvanceTimer = null;
        SaveTrainingExamples();
        tabListener.StopRecording();
        TrainPanel.Visibility = Visibility.Collapsed;
    }

    private void OpenTrainingFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(TrainingFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", TrainingFolder) { UseShellExecute = true });
        }
        catch
        {
            // Opening Explorer is only a convenience.
        }
    }
}
