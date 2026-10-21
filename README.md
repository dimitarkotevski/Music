# Guitar Tuner

A GuitarTuna-style guitar tuner for Windows PC. It listens to your microphone, detects the pitch of the string you play, and shows whether to tune up or down.

<p>
  <img src="docs/start-menu.png" alt="Start menu" width="300" />
  <img src="docs/tuner.png" alt="Tuner screen" width="300" />
</p>

## Features

- **Start menu:** choose **Tuning** or **Quit**.
- **Live tuning gauge:** a needle shows how many cents you are off, with a green ±5 cent "in tune" zone.
- **Clear hints:** "Tune up ↑", "Tune down ↓" or "In tune ✓", colour-coded orange/green.
- **Auto-detect string:** play any string and the tuner picks the nearest one. You can also click a string to lock onto it.
- **Reference tones:** clicking a string plays its target note.
- **14 tunings:** Standard, Drop D, Half Step Down, Full Step Down, C Standard, Drop C, Drop B, Drop A, Open G, Open D, Open E, Open A, Open C, DADGAD.
- **Background noise rejection:** an adaptive noise gate plus a stability check, with a **Sensitivity** slider for noisy rooms.
- **Adjustable reference pitch:** A4 from 415 to 466 Hz (default 440 Hz).
- **Microphone selection** and an input level meter (grey = ignored as noise, blue = being analysed).

## Requirements

- Windows 10 or 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A microphone

## Getting started

```sh
git clone https://github.com/dimitarkotevski/Music.git
cd Music
dotnet run --project Music
```

You can also open `Music.slnx` in Visual Studio and press **F5**.

## How to use

1. Click **Tuning** on the start menu.
2. Pick your tuning and microphone at the top.
3. Pluck a string and let it ring. Tune until the needle sits in the green zone and the hint says **In tune ✓**.
4. If it reacts to background noise, move the **Sensitivity** slider towards *Ignore noise*. If it misses quiet notes, move it towards *Pick up quiet*.

| Shortcut | Action |
|---|---|
| `Esc` | Back to the start menu |
| `Ctrl+Q` | Quit |

## How it works

- **Audio capture:** [NAudio](https://github.com/naudio/NAudio) records 44.1 kHz mono audio in 50 ms buffers into a sliding 4096-sample window.
- **Pitch detection:** the [YIN algorithm](http://audition.ens.fr/adc/pdf/2002_JASA_YIN.pdf) estimates the fundamental frequency (50–1000 Hz), with parabolic interpolation for sub-cent accuracy.
- **Noise handling:** the app tracks the room's noise floor and only analyses sounds clearly above it. A pitch must hold steady for about 150 ms before it is shown, and readings are smoothed with a median filter.
- **Tuning maths:** the cents offset is `1200 × log2(detected / target)`.

## Project structure

```
Music/
├── App.xaml                  # Application entry point
├── MainWindow.xaml(.cs)      # Start menu and tuner UI
├── Audio/
│   ├── MicrophoneListener.cs # Mic capture, noise gate, pitch events
│   ├── PitchDetector.cs      # YIN pitch detection
│   └── TonePlayer.cs         # Reference tone synthesis
└── Tuning/
    ├── GuitarTuning.cs       # Tuning definitions
    └── NoteMath.cs           # Frequency / note / cents conversions
```

To add a tuning, add one line to the list in `Music/Tuning/GuitarTuning.cs`:

```csharp
Create("Open D Minor", "D2 A2 D3 F3 A3 D4"),
```
