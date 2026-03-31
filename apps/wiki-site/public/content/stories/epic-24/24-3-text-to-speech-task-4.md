---
title: "Task 4: Browser Audio Playback (AudioWorklet Ring Buffer)"
sidebar:
  order: 240
---

**Story:** 24-3-text-to-speech - Text-to-Speech Integration
**Epic:** 24

## Task Description

Implement browser-side audio playback for TTS output received as binary WebSocket frames. Uses an AudioWorklet with a ring buffer for smooth, low-latency playback. Integrate into the `useVoiceSession` hook to handle binary frames and playback lifecycle.

## Acceptance Criteria

- AudioWorklet-based playback with ring buffer for smooth audio output
- Binary WebSocket frames (PCM16) written to ring buffer
- Playback starts when buffer has enough data (configurable threshold)
- Playback stops and notifies when buffer is empty (end of response)
- `response.cancel` message stops playback immediately and clears buffer
- No audible clicks/pops between audio chunks
- AudioWorklet processor runs off main thread for smooth playback
- Integration into `useVoiceSession` hook for TTS binary frame handling

## Implementation Details

### Technical Requirements

- [ ] Create `packages/dashboard/public/audio-playback-processor.js`:

```javascript
/**
 * AudioWorklet processor for TTS playback.
 * Receives PCM16 Int16Array chunks via port messages and plays them
 * through the audio output using a ring buffer.
 */
class AudioPlaybackProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this.buffer = new Float32Array(0);
    this.writeIndex = 0;
    this.readIndex = 0;
    this.playing = false;
    this.ringBuffer = new Float32Array(48000); // 3 seconds at 16kHz (rendered at sampleRate)
    this.ringWritePos = 0;
    this.ringReadPos = 0;
    this.ringLength = 0;

    this.port.onmessage = (event) => {
      if (event.data.type === 'audio') {
        // event.data.pcm16 is an Int16Array
        this.enqueueAudio(event.data.pcm16);
      } else if (event.data.type === 'clear') {
        this.clearBuffer();
      } else if (event.data.type === 'stop') {
        this.playing = false;
        this.clearBuffer();
      }
    };
  }

  enqueueAudio(int16Array) {
    // Convert Int16 to Float32
    for (let i = 0; i < int16Array.length; i++) {
      const sample = int16Array[i] / 32768;
      this.ringBuffer[this.ringWritePos] = sample;
      this.ringWritePos = (this.ringWritePos + 1) % this.ringBuffer.length;
      this.ringLength = Math.min(this.ringLength + 1, this.ringBuffer.length);
    }
    if (!this.playing && this.ringLength > 800) {
      // Start playing once we have 50ms of audio buffered (at 16kHz)
      this.playing = true;
    }
  }

  clearBuffer() {
    this.ringWritePos = 0;
    this.ringReadPos = 0;
    this.ringLength = 0;
  }

  process(inputs, outputs) {
    const output = outputs[0];
    if (!output || !output[0]) return true;

    const channel = output[0];

    if (!this.playing || this.ringLength === 0) {
      // Output silence
      channel.fill(0);
      if (this.playing && this.ringLength === 0) {
        this.playing = false;
        this.port.postMessage({ type: 'ended' });
      }
      return true;
    }

    for (let i = 0; i < channel.length; i++) {
      if (this.ringLength > 0) {
        channel[i] = this.ringBuffer[this.ringReadPos];
        this.ringReadPos = (this.ringReadPos + 1) % this.ringBuffer.length;
        this.ringLength--;
      } else {
        channel[i] = 0;
      }
    }

    return true;
  }
}

registerProcessor('audio-playback', AudioPlaybackProcessor);
```

- [ ] Update `useVoiceSession` hook to handle binary frames and playback:

```typescript
// In useVoiceSession:

const playbackNodeRef = useRef<AudioWorkletNode | null>(null);

// Initialize playback worklet (in connect())
async function initPlayback(audioContext: AudioContext): Promise<AudioWorkletNode> {
  await audioContext.audioWorklet.addModule('/audio-playback-processor.js');
  const node = new AudioWorkletNode(audioContext, 'audio-playback');
  node.connect(audioContext.destination);

  node.port.onmessage = (event) => {
    if (event.data.type === 'ended') {
      setIsSpeaking(false);
    }
  };

  return node;
}

// Handle binary WebSocket message (TTS audio):
function handleBinaryMessage(data: ArrayBuffer): void {
  if (!playbackNodeRef.current) return;

  const int16 = new Int16Array(data);
  playbackNodeRef.current.port.postMessage(
    { type: 'audio', pcm16: int16 },
    [int16.buffer] // Transfer ownership for zero-copy
  );
  setIsSpeaking(true);
}

// Handle response.cancel:
function handleResponseCancel(): void {
  if (playbackNodeRef.current) {
    playbackNodeRef.current.port.postMessage({ type: 'clear' });
  }
  setIsSpeaking(false);
}
```

### Files to Modify/Create

- CREATE `packages/dashboard/public/audio-playback-processor.js`
- MODIFY `packages/dashboard/src/hooks/useVoiceSession.ts` -- add binary frame handling and playback

### Dependencies

- [ ] Story 24-2 Task 3: `useVoiceSession` hook
- [ ] Browser APIs: `AudioContext`, `AudioWorklet`, `AudioWorkletNode`

## Testing Strategy

### Unit Tests

- [ ] Test AudioPlaybackProcessor `enqueueAudio()` converts Int16 to Float32 and writes to ring buffer
- [ ] Test `process()` reads from ring buffer and outputs to channel
- [ ] Test `process()` outputs silence when buffer is empty
- [ ] Test `process()` sends 'ended' message when playing and buffer empties
- [ ] Test `clearBuffer()` resets all pointers
- [ ] Test playback starts after threshold (800 samples / 50ms)
- [ ] Test 'stop' message clears buffer and stops playback
- [ ] Test ring buffer wraps correctly at boundary
- [ ] Test no overflow when enqueuing more data than ring buffer size

### Integration with useVoiceSession

- [ ] Test binary WebSocket frame routed to playback worklet
- [ ] Test `isSpeaking` state set to true when audio enqueued
- [ ] Test `isSpeaking` state set to false when playback ends
- [ ] Test `response.cancel` clears playback buffer
- [ ] Test Transferable used for zero-copy audio posting

### Validation Steps

1. [ ] Create AudioPlaybackProcessor worklet with ring buffer
2. [ ] Integrate playback into useVoiceSession hook
3. [ ] Test smooth playback without clicks between chunks
4. [ ] Test cancellation clears buffer immediately
5. [ ] Test buffer underrun (empty buffer) outputs silence
6. [ ] Verify TypeScript compiles

## Notes & Considerations

- The ring buffer size of 48000 samples provides ~3 seconds of buffer at 16kHz. This is enough to smooth out network jitter without adding noticeable latency.
- The playback threshold of 800 samples (50ms) ensures there is enough buffered audio before starting playback, preventing stuttering.
- `Transferable` objects (`[int16.buffer]`) are used when posting audio data to the worklet. This avoids copying the buffer, reducing GC pressure and latency.
- The `process()` callback runs at the audio rendering rate (typically 128 samples per call at 16kHz). It must be lock-free and never block.
- When the ring buffer empties during playback, the worklet outputs silence and sends an 'ended' message. This notifies the main thread that TTS playback is complete.
- If the user starts speaking during playback (interruption), the server sends `response.cancel`, which triggers `clearBuffer()` in the worklet, immediately stopping audio output.

## Completion Checklist

- [ ] AudioPlaybackProcessor worklet with ring buffer
- [ ] Int16 to Float32 conversion in worklet
- [ ] Ring buffer write/read with wrap-around
- [ ] Playback threshold before starting
- [ ] 'ended' message when buffer empties
- [ ] 'clear' message for interruption
- [ ] Integrated into useVoiceSession hook
- [ ] Binary frame handling
- [ ] Zero-copy Transferable posting
- [ ] All unit tests passing
