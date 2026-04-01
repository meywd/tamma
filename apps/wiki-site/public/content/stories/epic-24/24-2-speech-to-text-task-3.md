---
title: "Task 3: useVoiceSession React Hook (Browser Audio Capture + VAD)"
sidebar:
  order: 240
---

**Story:** 24-2-speech-to-text - Speech-to-Text Integration
**Epic:** 24

## Task Description

Create the `useVoiceSession` React hook that manages browser-side audio capture, Voice Activity Detection (VAD), WebSocket connection to the server, and PCM16 audio streaming. This hook is the browser counterpart to the server-side `VoiceSession`.

## Acceptance Criteria

- `useVoiceSession` hook manages WebSocket lifecycle (connect, reconnect, close)
- AudioWorklet captures mic audio and outputs PCM16 chunks at 16kHz mono
- `@ricky0123/vad-web` (Silero VAD) detects speech start/end events locally
- Audio is only sent to server during active speech (VAD active)
- VAD speech start sends `input.start` JSON message
- VAD speech end sends `input.end` JSON message
- Interim and final transcripts received from server and exposed in hook state
- Hook handles microphone permission request with graceful fallback
- Push-to-talk mode supported (bypass VAD when Space key held)
- Hook exposes: `{ state, transcript, connect, disconnect, sendText, isConnected }`
- `DEEPGRAM_API_KEY` env var added to docker-compose for tamma-api service

## Implementation Details

### Technical Requirements

- [ ] Add dependencies to `packages/dashboard/package.json`:
  ```
  "@ricky0123/vad-web": "^0.0.19"
  ```

- [ ] Create `packages/dashboard/src/hooks/useVoiceSession.ts`:

```typescript
import { useState, useCallback, useRef, useEffect } from 'react';
import type { ClientMessage, ServerMessage, VoiceSessionConfig } from '@tamma/voice';

export type VoiceConnectionState =
  | 'disconnected'
  | 'connecting'
  | 'connected'
  | 'listening'
  | 'processing'
  | 'speaking'
  | 'error';

export interface TranscriptEntry {
  id: string;
  role: 'user' | 'assistant';
  text: string;
  source: 'voice' | 'text';
  timestamp: number;
  interim?: boolean;
}

export interface UseVoiceSessionOptions {
  url?: string;               // WebSocket URL (default: auto-detect from window.location)
  config?: Partial<VoiceSessionConfig>;
  onTranscript?: (entry: TranscriptEntry) => void;
  onEngineState?: (state: ServerMessage & { type: 'engine.state' }) => void;
  onError?: (error: { code: string; message: string }) => void;
  pushToTalk?: boolean;       // If true, VAD is disabled; audio only sent while triggered
}

export interface UseVoiceSessionReturn {
  state: VoiceConnectionState;
  transcript: TranscriptEntry[];
  sessionId: string | null;
  isConnected: boolean;
  isSpeaking: boolean;        // True when TTS audio is playing
  isListening: boolean;       // True when VAD is active / user is speaking

  connect: () => Promise<void>;
  disconnect: () => void;
  sendText: (text: string) => void;
  startListening: () => void;   // For push-to-talk: start capturing audio
  stopListening: () => void;    // For push-to-talk: stop capturing audio
}

export function useVoiceSession(options?: UseVoiceSessionOptions): UseVoiceSessionReturn {
  const [state, setState] = useState<VoiceConnectionState>('disconnected');
  const [transcript, setTranscript] = useState<TranscriptEntry[]>([]);
  const [sessionId, setSessionId] = useState<string | null>(null);
  const [isSpeaking, setIsSpeaking] = useState(false);
  const [isListening, setIsListening] = useState(false);

  const wsRef = useRef<WebSocket | null>(null);
  const audioContextRef = useRef<AudioContext | null>(null);
  const workletNodeRef = useRef<AudioWorkletNode | null>(null);
  const streamRef = useRef<MediaStream | null>(null);
  const vadRef = useRef</* MicVAD instance */ unknown>(null);

  const connect = useCallback(async () => {
    // 1. Request microphone permission
    //    navigator.mediaDevices.getUserMedia({ audio: { sampleRate: 16000, channelCount: 1, echoCancellation: true } })
    //    Handle NotAllowedError -> set state to 'error', call onError
    //
    // 2. Create AudioContext at 16kHz
    //    new AudioContext({ sampleRate: 16000 })
    //
    // 3. Load and connect AudioWorklet for PCM16 capture
    //    audioContext.audioWorklet.addModule('/audio-worklet-processor.js')
    //    Create AudioWorkletNode, connect media stream source to it
    //    workletNode.port.onmessage -> receive Float32Array -> convert to Int16 -> send binary
    //
    // 4. Initialize VAD (if not push-to-talk)
    //    import { MicVAD } from '@ricky0123/vad-web'
    //    MicVAD.new({ onSpeechStart, onSpeechEnd, stream })
    //
    // 5. Open WebSocket connection
    //    new WebSocket(url)
    //    ws.binaryType = 'arraybuffer'
    //    ws.onopen -> send session.start message
    //    ws.onmessage -> handleServerMessage
    //    ws.onclose -> cleanup, attempt reconnect
    //    ws.onerror -> set state to 'error'
  }, [options]);

  const disconnect = useCallback(() => {
    // Send session.end
    // Close WebSocket
    // Stop AudioWorklet
    // Stop MediaStream tracks
    // Destroy VAD
    // Reset state
  }, []);

  const sendText = useCallback((text: string) => {
    // Send { type: 'text.input', text } JSON message
    // Add to transcript as user/text entry
  }, []);

  const startListening = useCallback(() => {
    // For push-to-talk: send input.start, begin forwarding audio
    setIsListening(true);
  }, []);

  const stopListening = useCallback(() => {
    // For push-to-talk: send input.end, stop forwarding audio
    setIsListening(false);
  }, []);

  // --- Server message handler ---
  function handleServerMessage(data: string | ArrayBuffer): void {
    if (data instanceof ArrayBuffer) {
      // Binary frame = TTS audio -> forward to playback (Story 24-3)
      return;
    }
    const msg = JSON.parse(data) as ServerMessage;
    switch (msg.type) {
      case 'session.ready':
        setSessionId(msg.sessionId);
        setState('connected');
        break;
      case 'transcript.interim':
        // Update or add interim transcript entry
        break;
      case 'transcript.final':
        // Commit final transcript entry
        break;
      case 'response.text':
        // Add assistant response to transcript
        break;
      case 'response.start':
        setIsSpeaking(true);
        break;
      case 'response.end':
        setIsSpeaking(false);
        break;
      case 'engine.state':
        options?.onEngineState?.(msg);
        break;
      case 'error':
        options?.onError?.({ code: msg.code, message: msg.message });
        break;
      case 'session.ended':
        setState('disconnected');
        break;
    }
  }

  // --- Float32 to Int16 conversion ---
  function float32ToInt16(float32: Float32Array): Int16Array {
    const int16 = new Int16Array(float32.length);
    for (let i = 0; i < float32.length; i++) {
      const s = Math.max(-1, Math.min(1, float32[i]!));
      int16[i] = s < 0 ? s * 0x8000 : s * 0x7FFF;
    }
    return int16;
  }

  // Cleanup on unmount
  useEffect(() => {
    return () => { disconnect(); };
  }, [disconnect]);

  return {
    state, transcript, sessionId,
    isConnected: state !== 'disconnected' && state !== 'error',
    isSpeaking, isListening,
    connect, disconnect, sendText, startListening, stopListening,
  };
}
```

- [ ] Create `packages/dashboard/public/audio-worklet-processor.js`:

```javascript
class PCM16CaptureProcessor extends AudioWorkletProcessor {
  process(inputs) {
    const input = inputs[0];
    if (input && input[0]) {
      // Post Float32Array to main thread for conversion and sending
      this.port.postMessage(input[0]);
    }
    return true;
  }
}
registerProcessor('pcm16-capture', PCM16CaptureProcessor);
```

- [ ] Add `DEEPGRAM_API_KEY` to `docker/docker-compose.yml` for tamma-api service:
  ```yaml
  environment:
    - DEEPGRAM_API_KEY=${DEEPGRAM_API_KEY}
  ```

### Files to Modify/Create

- CREATE `packages/dashboard/src/hooks/useVoiceSession.ts`
- CREATE `packages/dashboard/public/audio-worklet-processor.js`
- MODIFY `packages/dashboard/package.json` -- add `@ricky0123/vad-web`
- MODIFY `docker/docker-compose.yml` -- add `DEEPGRAM_API_KEY` env var

### Dependencies

- [ ] Story 24-1: Voice types (`ClientMessage`, `ServerMessage`, `VoiceSessionConfig`)
- [ ] `@ricky0123/vad-web` -- Silero VAD for browser
- [ ] Browser APIs: `MediaDevices`, `AudioContext`, `AudioWorklet`, `WebSocket`

## Testing Strategy

### Unit Tests -- useVoiceSession.test.ts

- [ ] Test initial state is `disconnected` with empty transcript
- [ ] Test `connect()` transitions to `connecting` then `connected` on session.ready
- [ ] Test `connect()` requests microphone permission
- [ ] Test `connect()` handles permission denied (state -> error, onError called)
- [ ] Test `sendText()` sends JSON message and adds to transcript
- [ ] Test `transcript.interim` message updates current interim entry
- [ ] Test `transcript.final` message commits entry (removes interim)
- [ ] Test `response.text` message adds assistant entry to transcript
- [ ] Test `disconnect()` sends session.end and resets state
- [ ] Test WebSocket close triggers cleanup
- [ ] Test cleanup on component unmount
- [ ] Test binary message (ArrayBuffer) is ignored in current task (playback in 24-3)
- [ ] Test `startListening()`/`stopListening()` for push-to-talk mode
- [ ] Test error message updates state and calls onError

### Mocking Strategy

```typescript
// Mock MediaDevices
const mockGetUserMedia = vi.fn().mockResolvedValue({
  getTracks: () => [{ stop: vi.fn() }],
});
Object.defineProperty(navigator, 'mediaDevices', {
  value: { getUserMedia: mockGetUserMedia },
});

// Mock AudioContext
class MockAudioContext {
  sampleRate = 16000;
  audioWorklet = { addModule: vi.fn().mockResolvedValue(undefined) };
  createMediaStreamSource = vi.fn().mockReturnValue({ connect: vi.fn() });
  close = vi.fn();
}

// Mock WebSocket
class MockWebSocket extends EventTarget {
  static OPEN = 1;
  readyState = MockWebSocket.OPEN;
  send = vi.fn();
  close = vi.fn();
  binaryType = 'arraybuffer';
}
```

### Validation Steps

1. [ ] Create useVoiceSession hook with WebSocket and audio management
2. [ ] Create AudioWorklet processor for PCM16 capture
3. [ ] Test mic permission flow
4. [ ] Test WebSocket message routing
5. [ ] Test transcript state management
6. [ ] Add DEEPGRAM_API_KEY to docker-compose
7. [ ] Run all unit tests
8. [ ] Manual test in browser: verify mic access, VAD detection, audio sending

## Notes & Considerations

- AudioWorklet runs on a separate thread, avoiding main-thread jank during audio processing. The worklet posts Float32 samples to the main thread, which converts to Int16 (PCM16) before sending over WebSocket.
- `@ricky0123/vad-web` uses the Silero VAD ONNX model (~1.5MB) which runs in a Web Worker. It provides `onSpeechStart` and `onSpeechEnd` callbacks with configurable sensitivity.
- The hook must handle the case where `AudioContext` is created in a suspended state (browsers require user gesture). The `connect()` function should be called from a click handler.
- PCM16 at 16kHz: 20ms frames = 640 bytes per frame. At 50 frames/second, this is ~32KB/s of upload bandwidth. WebSocket handles this efficiently.
- Push-to-talk mode bypasses VAD entirely. Audio is captured and sent only while `startListening()` is active (Space key held). This is useful in noisy environments.
- The `binaryType = 'arraybuffer'` setting is required to receive TTS audio as ArrayBuffer instead of Blob.

## Completion Checklist

- [ ] `useVoiceSession` hook created with full lifecycle management
- [ ] AudioWorklet processor created for PCM16 capture
- [ ] VAD integration with @ricky0123/vad-web
- [ ] Float32 to Int16 conversion
- [ ] WebSocket message routing for all ServerMessage types
- [ ] Transcript state management
- [ ] Push-to-talk support
- [ ] DEEPGRAM_API_KEY in docker-compose
- [ ] All unit tests passing
- [ ] TypeScript compiles without errors
