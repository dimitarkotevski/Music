// Port of AllTunning/Audio/PitchDetector.cs: YIN (de Cheveigné and Kawahara, 2002) with the
// same octave and subharmonic corrections as the desktop tuner.

// Lower = stricter: only clearly periodic sounds (like a plucked string) pass.
const THRESHOLD = 0.1;

// Octave correction only kicks in when the chosen dip is not already clean, and a
// longer period is clearly (OCTAVE_CHECK_RATIO times) more periodic.
const OCTAVE_CHECK_FLOOR = 0.02;
const OCTAVE_CHECK_RATIO = 0.5;

// If the detected frequency has almost no energy of its own but a harmonic does, YIN has
// locked onto a subharmonic (a B string read as E2).
const SUBHARMONIC_ENERGY_RATIO = 0.15;

export class PitchDetector {
  constructor(sampleRate, bufferSize, minFrequency = 30, maxFrequency = 1400) {
    this.sampleRate = sampleRate;
    this.tauMin = Math.max(2, Math.floor(sampleRate / maxFrequency));
    this.tauMax = Math.min(Math.floor(bufferSize / 2), Math.floor(sampleRate / minFrequency));
    this.yin = new Float32Array(this.tauMax + 1);

    this.hann = new Float32Array(bufferSize);
    let sum = 0;
    for (let i = 0; i < bufferSize; i++) {
      this.hann[i] = 0.5 - 0.5 * Math.cos(2 * Math.PI * i / (bufferSize - 1));
      sum += this.hann[i];
    }
    this.hannSum = sum;
  }

  /** Returns the detected frequency in Hz, or null if no clear pitch was found. */
  detect(buffer) {
    const { yin, tauMin, tauMax } = this;
    const window = buffer.length - tauMax;

    // Difference function combined with cumulative mean normalisation.
    yin[0] = 1;
    let runningSum = 0;
    for (let tau = 1; tau <= tauMax; tau++) {
      let sum = 0;
      for (let i = 0; i < window; i++) {
        const delta = buffer[i] - buffer[i + tau];
        sum += delta * delta;
      }
      runningSum += sum;
      yin[tau] = runningSum === 0 ? 1 : sum * tau / runningSum;
    }

    // First dip below the threshold, then walk down to its local minimum.
    let best = -1;
    for (let tau = tauMin; tau < tauMax; tau++) {
      if (yin[tau] < THRESHOLD) {
        while (tau + 1 < tauMax && yin[tau + 1] < yin[tau])
          tau++;
        best = tau;
        break;
      }
    }
    if (best < 0)
      return null;

    best = this.correctOctaveError(best);

    // Parabolic interpolation for sub-sample accuracy.
    const s0 = yin[best - 1], s1 = yin[best], s2 = yin[best + 1];
    const denominator = s0 + s2 - 2 * s1;
    const refinedTau = denominator === 0 ? best : best + (s0 - s2) / (2 * denominator);

    return this.correctSubharmonic(buffer, this.sampleRate / refinedTau);
  }

  /** A strong 2nd or 3rd harmonic can make a fraction of the real period look periodic enough to pass. */
  correctOctaveError(tau) {
    const { yin, tauMax } = this;
    let result = tau;
    for (let multiple = 2; multiple <= 3; multiple++) {
      const center = tau * multiple;
      const spread = Math.max(2, Math.floor(center / 33));
      if (center - spread < 1 || center + spread >= tauMax)
        break;

      let candidate = center - spread;
      for (let i = candidate + 1; i <= center + spread; i++) {
        if (yin[i] < yin[candidate])
          candidate = i;
      }

      if (yin[result] > OCTAVE_CHECK_FLOOR && yin[candidate] < yin[result] * OCTAVE_CHECK_RATIO)
        result = candidate;
    }
    return result;
  }

  /** YIN can lock onto two or three times the real period when a string's fundamental is weak. */
  correctSubharmonic(buffer, frequency) {
    const magnitude = this.magnitude(buffer, frequency);
    let best = frequency;
    let bestMagnitude = magnitude;

    const firstMultiple = frequency < 80 ? 2 : 3;
    for (let multiple = firstMultiple; multiple <= 3; multiple++) {
      const candidate = frequency * multiple;
      if (candidate >= this.sampleRate / 2)
        break;

      const candidateMagnitude = this.magnitude(buffer, candidate);
      if (candidateMagnitude > bestMagnitude) {
        best = candidate;
        bestMagnitude = candidateMagnitude;
      }
    }

    return best !== frequency && magnitude < bestMagnitude * SUBHARMONIC_ENERGY_RATIO ? best : frequency;
  }

  /** Magnitude of one frequency in the buffer (Hann-windowed Goertzel). */
  magnitude(buffer, frequency) {
    if (frequency >= this.sampleRate / 2)
      return 0;

    const coefficient = 2 * Math.cos(2 * Math.PI * frequency / this.sampleRate);
    let s1 = 0, s2 = 0;
    for (let i = 0; i < buffer.length; i++) {
      const s0 = buffer[i] * this.hann[i] + coefficient * s1 - s2;
      s2 = s1;
      s1 = s0;
    }
    const power = s1 * s1 + s2 * s2 - coefficient * s1 * s2;
    return Math.sqrt(Math.max(0, power)) / Math.max(1e-9, this.hannSum);
  }
}
