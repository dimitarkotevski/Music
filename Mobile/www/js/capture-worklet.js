// Runs on the audio thread: collects microphone samples into fixed-size blocks for the tuner.
class CaptureProcessor extends AudioWorkletProcessor {
  constructor(options) {
    super();
    this.block = new Float32Array(options.processorOptions.blockSize);
    this.filled = 0;
  }

  process(inputs) {
    const channel = inputs[0]?.[0];
    if (channel) {
      for (let i = 0; i < channel.length; i++) {
        this.block[this.filled++] = channel[i];
        if (this.filled === this.block.length) {
          this.port.postMessage(this.block.slice());
          this.filled = 0;
        }
      }
    }
    return true;
  }
}

registerProcessor('capture', CaptureProcessor);
