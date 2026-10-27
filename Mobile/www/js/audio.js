import { PitchDetector } from './pitch-detector.js';

let context = null;

const isNative = () => window.Capacitor?.isNativePlatform?.() === true;

/** One shared AudioContext; it must be created or resumed from a tap on iOS. */
export async function getAudioContext() {
  context ??= new (window.AudioContext || window.webkitAudioContext)({ latencyHint: 'interactive' });
  if (context.state !== 'running') {
    // Without a tap, iOS leaves resume() pending forever; give up so the caller can ask for one.
    await Promise.race([context.resume(), new Promise(resolve => setTimeout(resolve, 800))]);
    if (context.state !== 'running')
      throw new Error('Audio needs a tap to start.');
  }
  return context;
}

/**
 * In a mobile browser, Safari mutes Web Audio with the silent switch unless the session is "playback".
 * The native iOS app sets its audio session in AppDelegate instead.
 */
export function setAudioSession(type) {
  if (isNative() || !navigator.audioSession)
    return;
  try {
    navigator.audioSession.type = type;
  } catch { /* not supported */ }
}

const MINIMUM_GATE = 0.003;

/** Captures microphone audio and reports the detected pitch for each block (port of MicrophoneListener.cs). */
export class MicrophoneListener {
  constructor({ blockMilliseconds = 25, onReading }) {
    this.blockMilliseconds = blockMilliseconds;
    this.onReading = onReading;
    this.gateRatio = 4;
    this.stream = null;
    this.nodes = [];
  }

  get isRunning() { return this.stream !== null; }

  async start() {
    if (this.stream)
      return;

    setAudioSession('play-and-record');
    const ctx = await getAudioContext();
    // Processing meant for voice calls distorts the pitch and fades out a ringing string.
    this.stream = await navigator.mediaDevices.getUserMedia({
      audio: { echoCancellation: false, noiseSuppression: false, autoGainControl: false },
    });

    try {
      await this.connect(ctx);
    } catch (error) {
      this.stop();
      throw error;
    }
  }

  async connect(ctx) {
    const sampleRate = ctx.sampleRate;
    this.sampleRate = sampleRate;
    this.configureDetector();
    const blockSize = Math.round(sampleRate * this.blockMilliseconds / 1000);
    this.noiseFloor = 0.002;

    // The noise floor adapts at the same speed in real time whatever the block length.
    const blocksPer50Ms = this.blockMilliseconds / 50;
    this.noiseFloorFallRate = 1 - (1 - 0.3) ** blocksPer50Ms;
    this.noiseFloorRiseRate = 1 - (1 - 0.003) ** blocksPer50Ms;

    const source = ctx.createMediaStreamSource(this.stream);
    const mute = ctx.createGain();
    mute.gain.value = 0;

    let capture;
    if (ctx.audioWorklet) {
      await ctx.audioWorklet.addModule('js/capture-worklet.js');
      capture = new AudioWorkletNode(ctx, 'capture', { processorOptions: { blockSize } });
      capture.port.onmessage = e => this.onBlock(e.data);
    } else {
      capture = ctx.createScriptProcessor(2048, 1, 1);
      capture.onaudioprocess = e => this.onBlock(new Float32Array(e.inputBuffer.getChannelData(0)));
    }

    // The capture node only runs while it is connected to the output, so route it through a muted gain.
    source.connect(capture);
    capture.connect(mute);
    mute.connect(ctx.destination);
    this.nodes = [source, capture, mute];
  }

  /** Lowest pitch to detect. Lower costs more work, so it follows the tuning's lowest string. */
  setMinFrequency(frequency) {
    this.minFrequency = frequency;
    if (this.stream)
      this.configureDetector();
  }

  configureDetector() {
    const minFrequency = this.minFrequency ?? 50;
    // Three periods of the lowest note: YIN compares the window with itself shifted by up to one period.
    const windowSize = Math.ceil(3 * this.sampleRate / minFrequency);
    this.window = new Float32Array(windowSize);
    this.detector = new PitchDetector(this.sampleRate, windowSize, minFrequency);
  }

  stop() {
    for (const node of this.nodes)
      node.disconnect();
    this.nodes = [];
    this.stream?.getTracks().forEach(track => track.stop());
    this.stream = null;
    setAudioSession('playback');
  }

  onBlock(samples) {
    const { window } = this;
    const count = Math.min(samples.length, window.length);

    // Slide the analysis window and append the newest samples.
    window.copyWithin(0, count);
    window.set(samples.subarray(samples.length - count), window.length - count);

    let sumSquares = 0;
    for (let i = window.length - count; i < window.length; i++)
      sumSquares += window[i] * window[i];
    const level = Math.sqrt(sumSquares / Math.max(1, count));

    // Track the room's background level: drop quickly to quiet moments...
    if (level < this.noiseFloor)
      this.noiseFloor = Math.max(0.0005, this.noiseFloor + this.noiseFloorFallRate * (level - this.noiseFloor));

    const gate = Math.max(MINIMUM_GATE, this.noiseFloor * this.gateRatio);
    const frequency = level >= gate ? this.detector.detect(window) : null;
    this.onReading({ frequency, level, gate });

    // ...but rise only slowly, and never while music is being heard.
    if (level > this.noiseFloor && frequency === null)
      this.noiseFloor += this.noiseFloorRiseRate * (level - this.noiseFloor);
  }
}

/** Plays a short plucked-string-like reference tone: decaying harmonics, so the pitch stays exact. */
export async function playReferenceTone(frequency) {
  const ctx = await getAudioContext();
  const seconds = 2.5;
  const buffer = ctx.createBuffer(1, Math.round(ctx.sampleRate * seconds), ctx.sampleRate);
  const data = buffer.getChannelData(0);
  for (let n = 0; n < data.length; n++) {
    const t = n / ctx.sampleRate;
    let value = 0;
    for (let h = 1; h <= 6; h++) {
      if (frequency * h < ctx.sampleRate / 2)
        value += Math.sin(2 * Math.PI * frequency * h * t) / h * Math.exp(-t * (1 + 0.8 * h));
    }
    data[n] = value * 0.3 * Math.min(1, t / 0.005);
  }

  playReferenceTone.current?.stop();
  const source = ctx.createBufferSource();
  source.buffer = buffer;
  source.connect(ctx.destination);
  source.start();
  playReferenceTone.current = source;
}
