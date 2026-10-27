import { getAudioContext, setAudioSession } from './audio.js';

export const MIN_BPM = 30;
export const MAX_BPM = 250;

export const TIME_SIGNATURES = [
  { name: '2/4', beats: 2 },
  { name: '3/4', beats: 3 },
  { name: '4/4', beats: 4 },
  { name: '5/4', beats: 5 },
  { name: '6/8', beats: 6 },
  { name: '7/8', beats: 7 },
];

// Clicks are scheduled on the audio clock slightly ahead, so timers firing late never shift a beat.
const SCHEDULER_INTERVAL_MS = 25;
const SCHEDULE_AHEAD_SECONDS = 0.12;

/** Sample-accurate metronome clicks with an accented first beat (port of Metronome.cs). */
export class Metronome {
  constructor({ onBeat }) {
    this.onBeat = onBeat;
    this.bpm = 120;
    this.beatsPerBar = 4;
    this.timer = null;
    this.pendingBeats = [];
  }

  get isRunning() { return this.timer !== null; }

  setBpm(bpm) {
    this.bpm = Math.min(MAX_BPM, Math.max(MIN_BPM, Math.round(bpm)));
  }

  async start() {
    setAudioSession('playback');
    const ctx = await getAudioContext();
    this.ctx = ctx;
    this.accentClick ??= createClick(ctx, 1600, 0.7);
    this.normalClick ??= createClick(ctx, 1000, 0.45);

    this.beat = -1;
    this.nextBeatTime = ctx.currentTime + 0.05;
    this.pendingBeats = [];
    this.schedule();
    this.timer = setInterval(() => this.schedule(), SCHEDULER_INTERVAL_MS);
    this.animate();
  }

  stop() {
    clearInterval(this.timer);
    this.timer = null;
    cancelAnimationFrame(this.frame);
    this.pendingBeats = [];
    this.onBeat(-1);
  }

  schedule() {
    while (this.nextBeatTime < this.ctx.currentTime + SCHEDULE_AHEAD_SECONDS) {
      this.beat = (this.beat + 1) % this.beatsPerBar;
      const source = this.ctx.createBufferSource();
      source.buffer = this.beat === 0 ? this.accentClick : this.normalClick;
      source.connect(this.ctx.destination);
      source.start(this.nextBeatTime);
      this.pendingBeats.push({ beat: this.beat, time: this.nextBeatTime });
      this.nextBeatTime += 60 / this.bpm;
    }
  }

  /** Lights each beat when its click is actually heard. */
  animate() {
    const heardTime = this.ctx.currentTime - (this.ctx.outputLatency || this.ctx.baseLatency || 0);
    while (this.pendingBeats.length && this.pendingBeats[0].time <= heardTime)
      this.onBeat(this.pendingBeats.shift().beat);
    this.frame = requestAnimationFrame(() => this.animate());
  }
}

/** A short, sharp woodblock-like tick. */
function createClick(ctx, frequency, amplitude) {
  const buffer = ctx.createBuffer(1, Math.round(ctx.sampleRate * 0.04), ctx.sampleRate);
  const data = buffer.getChannelData(0);
  for (let i = 0; i < data.length; i++) {
    const t = i / ctx.sampleRate;
    data[i] = amplitude * Math.sin(2 * Math.PI * frequency * t) * Math.exp(-t / 0.008);
  }
  return buffer;
}

export function tempoName(bpm) {
  if (bpm < 40) return 'Grave';
  if (bpm < 60) return 'Largo';
  if (bpm < 66) return 'Larghetto';
  if (bpm < 76) return 'Adagio';
  if (bpm < 108) return 'Andante';
  if (bpm < 120) return 'Moderato';
  if (bpm < 168) return 'Allegro';
  if (bpm < 200) return 'Presto';
  return 'Prestissimo';
}
