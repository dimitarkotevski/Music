"""Transcribe an audio file to note events with Spotify's Basic Pitch.

This is deliberately a small, model-specific worker. The C# app talks to it through
a JSON file, so the transcription model can be replaced later without changing the
fretboard logic or the UI.

Usage:
    python basic_pitch_worker.py --input song.wav --output notes.json
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import warnings


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Basic Pitch audio-to-notes worker")
    parser.add_argument("--input", required=True, help="Audio file (wav/mp3/flac/ogg)")
    parser.add_argument("--output", required=True, help="Destination JSON file")
    parser.add_argument("--onset", type=float, default=0.5, help="Onset threshold 0-1")
    parser.add_argument("--frame", type=float, default=0.3, help="Frame threshold 0-1")
    parser.add_argument("--min-note-ms", type=float, default=60.0)
    parser.add_argument("--min-frequency", type=float, default=70.0)
    parser.add_argument("--max-frequency", type=float, default=1400.0)
    return parser.parse_args()


def main() -> int:
    args = _parse_args()
    warnings.filterwarnings("ignore")
    # Basic Pitch prints informational messages from native libraries; keep stdout
    # clean for the JSON contract.
    os.environ.setdefault("TF_CPP_MIN_LOG_LEVEL", "3")

    try:
        import librosa
        from basic_pitch import ICASSP_2022_MODEL_PATH
        from basic_pitch.inference import Model, predict

        model = Model(ICASSP_2022_MODEL_PATH)
        _model_output, _midi_data, note_events = predict(
            args.input,
            model,
            onset_threshold=args.onset,
            frame_threshold=args.frame,
            minimum_note_length=args.min_note_ms,
            minimum_frequency=args.min_frequency,
            maximum_frequency=args.max_frequency,
            multiple_pitch_bends=False,
            melodia_trick=True,
        )

        notes = []
        for event in note_events:
            start, end, midi, amplitude = event[0], event[1], event[2], event[3]
            notes.append(
                {
                    "start": float(start),
                    "end": float(end),
                    "midi": int(midi),
                    "amplitude": float(amplitude),
                }
            )
        notes.sort(key=lambda n: (n["start"], n["midi"]))

        duration = float(librosa.get_duration(path=args.input))
        payload = {
            "model": "basic_pitch",
            "duration": duration,
            "notes": notes,
        }
        with open(args.output, "w", encoding="utf-8") as handle:
            json.dump(payload, handle, ensure_ascii=False, indent=2)
        print(f"transcribed {len(notes)} notes in {duration:.1f}s", flush=True)
        return 0
    except Exception as exc:  # noqa: BLE001 - reported to the C# caller
        try:
            with open(args.output, "w", encoding="utf-8") as handle:
                json.dump({"error": f"{type(exc).__name__}: {exc}"}, handle, ensure_ascii=False)
        except OSError:
            pass
        print(f"transcription failed: {type(exc).__name__}: {exc}", file=sys.stderr, flush=True)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
