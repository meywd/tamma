# Task 2: VoiceTranscript + AudioVisualizer Components

**Story:** 24-5-dashboard-voice-ui - Dashboard Voice UI
**Epic:** 24

## Task Description

Create the `VoiceTranscript` component for real-time transcript display in the chat panel, and the `AudioVisualizer` component for waveform/level meter showing mic activity. Both integrate into the existing chat UI.

## Acceptance Criteria

- `VoiceTranscript` displays real-time transcript of both user speech and AI responses
- Interim transcripts shown with visual distinction (lighter text, italic)
- Final transcripts appear as committed chat messages
- Voice and text messages interleave in same conversation view
- Source indicator: mic icon for voice messages, keyboard icon for text
- Auto-scroll to bottom on new messages
- `AudioVisualizer` shows real-time mic audio level as a waveform/bar meter
- Visualizer only active when voice session is connected
- Smooth animation using `requestAnimationFrame`
- Both components read from voice Zustand store

## Implementation Details

### Technical Requirements

- [ ] Create `packages/dashboard/src/components/voice/VoiceTranscript.tsx`:

```tsx
import { useEffect, useRef } from 'react';
import { useVoiceStore } from '../../stores/voice/store.js';
import type { TranscriptEntry } from '../../hooks/useVoiceSession.js';

interface VoiceTranscriptProps {
  className?: string;
  maxHeight?: string;  // default: '400px'
}

export function VoiceTranscript({ className = '', maxHeight = '400px' }: VoiceTranscriptProps): JSX.Element {
  const { transcript } = useVoiceStore();
  const bottomRef = useRef<HTMLDivElement>(null);

  // Auto-scroll on new messages
  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [transcript.length]);

  return (
    <div
      className={`overflow-y-auto ${className}`}
      style={{ maxHeight }}
      role="log"
      aria-live="polite"
      aria-label="Voice conversation transcript"
    >
      {transcript.length === 0 ? (
        <div className="flex items-center justify-center h-32 text-gray-400 text-sm">
          Voice conversation will appear here
        </div>
      ) : (
        <div className="space-y-3 p-4">
          {transcript.map((entry) => (
            <TranscriptMessage key={entry.id} entry={entry} />
          ))}
        </div>
      )}
      <div ref={bottomRef} />
    </div>
  );
}

function TranscriptMessage({ entry }: { entry: TranscriptEntry }): JSX.Element {
  const isUser = entry.role === 'user';
  const isInterim = entry.interim === true;

  return (
    <div className={`flex ${isUser ? 'justify-end' : 'justify-start'}`}>
      <div
        className={`
          max-w-[80%] rounded-lg px-4 py-2
          ${isUser
            ? 'bg-blue-100 text-blue-900'
            : 'bg-gray-100 text-gray-900'
          }
          ${isInterim ? 'opacity-60 italic' : ''}
        `}
      >
        {/* Source indicator */}
        <div className="flex items-center gap-1 mb-1">
          {entry.source === 'voice' ? (
            <MicSmallIcon className="w-3 h-3 text-gray-400" />
          ) : (
            <KeyboardSmallIcon className="w-3 h-3 text-gray-400" />
          )}
          <span className="text-xs text-gray-400">
            {isUser ? 'You' : 'Tamma'}
            {isInterim ? ' (listening...)' : ''}
          </span>
        </div>

        {/* Message text */}
        <p className="text-sm whitespace-pre-wrap">{entry.text}</p>

        {/* Timestamp */}
        <div className="text-xs text-gray-400 mt-1">
          {new Date(entry.timestamp).toLocaleTimeString()}
        </div>
      </div>
    </div>
  );
}

function MicSmallIcon({ className }: { className: string }): JSX.Element {
  return (
    <svg className={className} fill="currentColor" viewBox="0 0 20 20">
      <path d="M7 4a3 3 0 016 0v4a3 3 0 01-6 0V4zm4 10.93A7.001 7.001 0 0017 8h-2a5 5 0 01-10 0H3a7.001 7.001 0 006 6.93V18H6v2h8v-2h-3v-3.07z" />
    </svg>
  );
}

function KeyboardSmallIcon({ className }: { className: string }): JSX.Element {
  return (
    <svg className={className} fill="none" viewBox="0 0 20 20" stroke="currentColor" strokeWidth={1.5}>
      <rect x="2" y="4" width="16" height="12" rx="2" />
      <line x1="5" y1="8" x2="7" y2="8" />
      <line x1="9" y1="8" x2="11" y2="8" />
      <line x1="13" y1="8" x2="15" y2="8" />
      <line x1="6" y1="12" x2="14" y2="12" />
    </svg>
  );
}
```

- [ ] Create `packages/dashboard/src/components/voice/AudioVisualizer.tsx`:

```tsx
import { useEffect, useRef, useCallback } from 'react';
import { useVoiceStore } from '../../stores/voice/store.js';

interface AudioVisualizerProps {
  analyserNode: AnalyserNode | null;
  className?: string;
  barCount?: number;    // default: 20
  height?: number;      // default: 40
}

export function AudioVisualizer({
  analyserNode,
  className = '',
  barCount = 20,
  height = 40,
}: AudioVisualizerProps): JSX.Element {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const animationRef = useRef<number>(0);
  const { isConnected, isListening } = useVoiceStore();

  const draw = useCallback(() => {
    const canvas = canvasRef.current;
    if (!canvas || !analyserNode) return;

    const ctx = canvas.getContext('2d');
    if (!ctx) return;

    const dataArray = new Uint8Array(analyserNode.frequencyBinCount);
    analyserNode.getByteFrequencyData(dataArray);

    const width = canvas.width;
    const barWidth = width / barCount;
    const step = Math.floor(dataArray.length / barCount);

    ctx.clearRect(0, 0, width, height);

    for (let i = 0; i < barCount; i++) {
      const value = dataArray[i * step] ?? 0;
      const barHeight = (value / 255) * height;

      // Gradient from blue (quiet) to red (loud)
      const hue = 210 - (value / 255) * 150;  // 210 (blue) -> 60 (yellow/red)
      ctx.fillStyle = isListening
        ? `hsl(${hue}, 80%, 55%)`
        : 'hsl(210, 10%, 75%)';

      ctx.fillRect(
        i * barWidth + 1,
        height - barHeight,
        barWidth - 2,
        barHeight,
      );
    }

    animationRef.current = requestAnimationFrame(draw);
  }, [analyserNode, barCount, height, isListening]);

  useEffect(() => {
    if (isConnected && analyserNode) {
      animationRef.current = requestAnimationFrame(draw);
    }

    return () => {
      if (animationRef.current) {
        cancelAnimationFrame(animationRef.current);
      }
    };
  }, [isConnected, analyserNode, draw]);

  if (!isConnected) return <></>;

  return (
    <canvas
      ref={canvasRef}
      width={barCount * 8}
      height={height}
      className={`rounded ${className}`}
      aria-label="Audio level visualizer"
      role="img"
    />
  );
}
```

### Files to Modify/Create

- CREATE `packages/dashboard/src/components/voice/VoiceTranscript.tsx`
- CREATE `packages/dashboard/src/components/voice/VoiceTranscript.test.tsx`
- CREATE `packages/dashboard/src/components/voice/AudioVisualizer.tsx`
- CREATE `packages/dashboard/src/components/voice/AudioVisualizer.test.tsx`

### Dependencies

- [ ] Task 1: Voice Zustand store
- [ ] Story 24-2 Task 3: `TranscriptEntry` type from `useVoiceSession`
- [ ] Browser APIs: `AnalyserNode`, `requestAnimationFrame`, `Canvas2D`

## Testing Strategy

### Unit Tests -- VoiceTranscript.test.tsx

- [ ] Test renders empty state placeholder when no transcript
- [ ] Test renders user message aligned right with blue background
- [ ] Test renders assistant message aligned left with gray background
- [ ] Test interim message shown with reduced opacity and italic
- [ ] Test voice source shows mic icon
- [ ] Test text source shows keyboard icon
- [ ] Test auto-scroll triggered on new message
- [ ] Test timestamp displayed for each message
- [ ] Test multiple messages rendered in order
- [ ] Test aria-live="polite" for screen readers
- [ ] Test role="log" for semantic meaning

### Unit Tests -- AudioVisualizer.test.tsx

- [ ] Test renders nothing when not connected
- [ ] Test renders canvas when connected
- [ ] Test requestAnimationFrame called when connected
- [ ] Test cancelAnimationFrame called on unmount
- [ ] Test aria-label present for accessibility
- [ ] Test bar colors change based on isListening state

### Validation Steps

1. [ ] Create VoiceTranscript with message rendering
2. [ ] Create AudioVisualizer with canvas-based bars
3. [ ] Test transcript display with mock store data
4. [ ] Test auto-scroll behavior
5. [ ] Test visualizer animation
6. [ ] Run all unit tests
7. [ ] Verify TypeScript compiles

## Notes & Considerations

- The VoiceTranscript component should interleave seamlessly with any existing chat messages. If there is an existing chat component, this should either extend it or render alongside it.
- Interim transcripts use the same entry ID. When a final transcript arrives, it replaces the interim entry. This is handled by `updateInterimTranscript` in the store.
- The AudioVisualizer requires an `AnalyserNode` from the AudioContext. The `useVoiceSession` hook should create this node and pass it down as a prop. The analyser does not modify audio -- it only reads frequency data.
- `requestAnimationFrame` is used for smooth 60fps rendering. The animation loop is cancelled when the component unmounts or voice disconnects.
- Canvas-based rendering is used over DOM-based bars for performance. At 60fps, DOM manipulation would cause layout thrashing.

## Completion Checklist

- [ ] VoiceTranscript component with message rendering
- [ ] Interim vs final transcript visual distinction
- [ ] Source indicators (mic vs keyboard icons)
- [ ] Auto-scroll on new messages
- [ ] AudioVisualizer with frequency bar rendering
- [ ] Color gradient based on audio level
- [ ] requestAnimationFrame animation loop
- [ ] Accessibility: aria-labels, roles, live regions
- [ ] All unit tests passing
- [ ] TypeScript compiles
