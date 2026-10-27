const NAMES = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B'];

/** Lowest and highest notes a custom tuning string can be set to (A0 to C6). */
export const MIN_STRING_MIDI = 21;
export const MAX_STRING_MIDI = 84;

export const midiToFrequency = (midi, a4) => a4 * 2 ** ((midi - 69) / 12);

export const frequencyToMidi = (frequency, a4) => 69 + 12 * Math.log2(frequency / a4);

export const cents = (frequency, target) => 1200 * Math.log2(frequency / target);

export const noteName = midi => NAMES[((midi % 12) + 12) % 12];

export const octave = midi => Math.floor(midi / 12) - 1;

export const fullName = midi => `${noteName(midi)}${octave(midi)}`;

/** Parses notes like "E2", "F#3" or "Eb2" into a MIDI note number. */
export function parseNote(note) {
  const semitones = { C: 0, D: 2, E: 4, F: 5, G: 7, A: 9, B: 11 };
  let semitone = semitones[note[0].toUpperCase()];
  if (semitone === undefined)
    throw new Error(`Invalid note '${note}'.`);

  let i = 1;
  if (note[i] === '#') { semitone++; i++; }
  else if (note[i] === 'b') { semitone--; i++; }

  return (parseInt(note.slice(i), 10) + 1) * 12 + semitone;
}

const preset = (name, notes) => ({ id: `preset:${name}`, name, strings: notes.split(' ').map(parseNote), custom: false });

/** Suggested tunings, strings ordered from lowest (6th) to highest (1st). Same list as the desktop app. */
export const SUGGESTED_TUNINGS = [
  preset('Standard', 'E2 A2 D3 G3 B3 E4'),
  preset('Drop D', 'D2 A2 D3 G3 B3 E4'),
  preset('Half Step Down', 'Eb2 Ab2 Db3 Gb3 Bb3 Eb4'),
  preset('Full Step Down', 'D2 G2 C3 F3 A3 D4'),
  preset('C Standard', 'C2 F2 Bb2 Eb3 G3 C4'),
  preset('Drop C', 'C2 G2 C3 F3 A3 D4'),
  preset('Drop B', 'B1 F#2 B2 E3 G#3 C#4'),
  preset('Drop A', 'A1 E2 A2 D3 F#3 B3'),
  preset('Open G', 'D2 G2 D3 G3 B3 D4'),
  preset('Open D', 'D2 A2 D3 F#3 A3 D4'),
  preset('Open E', 'E2 B2 E3 G#3 B3 E4'),
  preset('Open A', 'E2 A2 E3 A3 C#4 E4'),
  preset('Open C', 'C2 G2 C3 G3 C4 E4'),
  preset('DADGAD', 'D2 A2 D3 G3 A3 D4'),
  preset('7-String Standard', 'B1 E2 A2 D3 G3 B3 E4'),
  preset('Bass Standard', 'E1 A1 D2 G2'),
  preset('Ukulele', 'G4 C4 E4 A4'),
];

export const tuningLabel = tuning => tuning.strings.map(noteName).join(' ');
