# App & navigation

[← Code guide](README.md)

The whole app is one window, `MainWindow`, that shows one screen at a time.

## Files

| File | What's in it |
|---|---|
| [App.xaml](../../Music/App.xaml) | Starts the app; `StartupUri="MainWindow.xaml"` |
| [MainWindow.xaml](../../Music/MainWindow.xaml) | Markup for **all** screens, button styles, keyboard shortcuts |
| [MainWindow.xaml.cs](../../Music/MainWindow.xaml.cs) | Constructor, navigation, shared colours, closing, and the **tuner** screen |
| [MainWindow.Chords.cs](../../Music/MainWindow.Chords.cs) | Chord screen (same `MainWindow` class, `partial`) |
| [MainWindow.Metronome.cs](../../Music/MainWindow.Metronome.cs) | Metronome screen (same `MainWindow` class, `partial`) |

## Screens

Each screen is a panel in `MainWindow.xaml`. Exactly one is `Visible`; the others are `Collapsed`.

| Screen | Element name | Opened by | Code |
|---|---|---|---|
| Start menu | `StartMenu` | App start, `BackToMenu_Click` | [MainWindow.xaml.cs](../../Music/MainWindow.xaml.cs) |
| Tuner | `TunerView` | `StartTuning_Click` | [Tuner](tuner.md) |
| Chords | `ChordView` | `StartChords_Click` | [Chords](chords.md) |
| Metronome | `MetronomeView` | `StartMetronome_Click` | [Metronome](metronome.md) |

The start menu buttons in order: **Tuning** (blue), **Chords** (teal), **Metronome** (purple), **Quit**.

### Going back to the menu

`BackToMenu_Click` (the **← Menu** button on every screen, or **Esc** via `BackToMenuCommand`):

1. Stops everything that makes or records sound: `listener.Stop()`, `chordListener.Stop()`, `tonePlayer.Stop()`, `StopMetronome()`.
2. Collapses all screens and shows `StartMenu`.

The microphone only runs while the tuner or chord screen is open.

### Adding a new screen

1. In `MainWindow.xaml`, add a `DockPanel x:Name="MyView" Visibility="Collapsed"` next to the other views, with the same top bar (`← Menu` button using `SmallButton`, `Click="BackToMenu_Click"`).
2. Add a button to `StartMenu` using `Style="{StaticResource MenuButton}"` and a `Click` handler.
3. Create `MainWindow.MyFeature.cs` as `public partial class MainWindow`, with the click handler that collapses `StartMenu` and shows `MyView`.
4. In `BackToMenu_Click`, stop anything the screen runs and collapse `MyView`.
5. If it owns disposable audio objects, dispose them in `Window_Closing`.
6. If it needs setup, add an `InitializeMyFeature()` call at the end of the `MainWindow` constructor.

## Startup and shutdown

**Constructor** (`MainWindow()` in [MainWindow.xaml.cs](../../Music/MainWindow.xaml.cs)):

1. `InitializeComponent()`, then `DrawGauge()` (tuner gauge ticks).
2. Subscribes `listener.PitchUpdated` → `OnPitch`.
3. Fills the tuning dropdown (`TuningBox`) and microphone dropdown (`DeviceBox`).
4. `InitializeMetronome()` and `InitializeChords()`.

**Closing** (`Window_Closing`): disposes `listener`, `chordListener`, `tonePlayer`, `metronome`.

## Keyboard shortcuts

Defined in `Window.InputBindings` / `Window.CommandBindings` in `MainWindow.xaml`.

| Key | Command | Handler |
|---|---|---|
| `Ctrl+Q` | `ApplicationCommands.Close` | `Quit_Executed` |
| `Esc` | `MainWindow.BackToMenuCommand` | `BackToMenu_Click` |

The **Quit** button on the start menu uses the same `ApplicationCommands.Close` command.

## Styles

In `Window.Resources` in `MainWindow.xaml`:

| Style key | Used for |
|---|---|
| `StringButton` | Round 64 px tuner string buttons |
| `SmallButton` | Small rounded buttons: `← Menu`, A4 −/+, BPM −/+, Tap tempo |
| `MenuButton` | Large 260×56 buttons: start menu, metronome Start/Stop |

## Colours

Brushes are `static readonly` fields, frozen with `Freeze()` for performance.

| Brush | Colour | Used for | Defined in |
|---|---|---|---|
| `NeutralBrush` | `#E6E8EF` | Main text, idle needle | MainWindow.xaml.cs |
| `DimBrush` | `#9AA0B4` | Secondary text, gauge ticks | MainWindow.xaml.cs |
| `InTuneBrush` | `#3DDC84` green | In tune, accented metronome beat | MainWindow.xaml.cs |
| `OffBrush` | `#FF9F43` orange | Out of tune | MainWindow.xaml.cs |
| `StringBrush` | `#2C3140` | Inactive buttons/dots/pills | MainWindow.xaml.cs |
| `ActiveStringBrush` | `#5B8CFF` blue | Selected string, tuner level bar | MainWindow.xaml.cs |
| `LevelGatedBrush` | `#4A5066` grey | Level bar when input is treated as noise | MainWindow.xaml.cs |
| `ChordBrush` | `#14B8A6` teal | Chord name, chord-tone bars | MainWindow.Chords.cs |
| `ChromaBrush` | `#3A4155` | Non-chord-tone bars | MainWindow.Chords.cs |
| `MetronomeBrush` | `#8B5CF6` purple | Metronome Start button, beat lights | MainWindow.Metronome.cs |
| `StopBrush` | `#E5484D` red | Metronome Stop button | MainWindow.Metronome.cs |

The window background `#15171E` and the top bar `#232733` are set in the XAML.
