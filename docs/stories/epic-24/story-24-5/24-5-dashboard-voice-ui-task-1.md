# Task 1: Voice Zustand Store + VoiceModeToggle Component

**Story:** 24-5-dashboard-voice-ui - Dashboard Voice UI
**Epic:** 24

## Task Description

Create the voice Zustand store for centralized voice state management and the `VoiceModeToggle` component: a mic button with status indicator that integrates into the existing chat UI layout.

## Acceptance Criteria

- Zustand store at `packages/dashboard/src/stores/voice/store.ts` manages all voice state
- Store wraps the `useVoiceSession` hook state and exposes actions
- `VoiceModeToggle` component: mic button with status indicator (idle, listening, processing, speaking)
- Button colors: idle (gray), listening (red pulsing), processing (yellow), speaking (green)
- Toggle action: click to connect/disconnect voice mode
- Integrated into existing chat UI layout (not a separate page)
- Accessible: aria-labels for all states, focus indicators
- Mobile responsive: button accessible on small screens

## Implementation Details

### Technical Requirements

- [ ] Create `packages/dashboard/src/stores/voice/store.ts`:

```typescript
import { create } from 'zustand';
import type {
  VoiceConnectionState,
  TranscriptEntry,
  UseVoiceSessionOptions,
} from '../../hooks/useVoiceSession.js';

export interface VoiceState {
  // Connection
  connectionState: VoiceConnectionState;
  sessionId: string | null;
  isConnected: boolean;

  // Audio
  isSpeaking: boolean;       // TTS audio playing
  isListening: boolean;      // User speaking / VAD active
  isMicPermissionGranted: boolean;

  // Transcript
  transcript: TranscriptEntry[];

  // Config
  voiceEnabled: boolean;     // Master toggle
  pushToTalk: boolean;
  notificationsEnabled: boolean;

  // Actions
  setConnectionState: (state: VoiceConnectionState) => void;
  setSessionId: (id: string | null) => void;
  setIsSpeaking: (v: boolean) => void;
  setIsListening: (v: boolean) => void;
  setMicPermission: (granted: boolean) => void;
  addTranscriptEntry: (entry: TranscriptEntry) => void;
  updateInterimTranscript: (id: string, text: string) => void;
  clearTranscript: () => void;
  setVoiceEnabled: (enabled: boolean) => void;
  setPushToTalk: (enabled: boolean) => void;
  setNotificationsEnabled: (enabled: boolean) => void;
}

export const useVoiceStore = create<VoiceState>((set) => ({
  // Connection
  connectionState: 'disconnected',
  sessionId: null,
  isConnected: false,

  // Audio
  isSpeaking: false,
  isListening: false,
  isMicPermissionGranted: false,

  // Transcript
  transcript: [],

  // Config
  voiceEnabled: false,
  pushToTalk: false,
  notificationsEnabled: true,

  // Actions
  setConnectionState: (state) => set({
    connectionState: state,
    isConnected: state !== 'disconnected' && state !== 'error',
  }),
  setSessionId: (id) => set({ sessionId: id }),
  setIsSpeaking: (v) => set({ isSpeaking: v }),
  setIsListening: (v) => set({ isListening: v }),
  setMicPermission: (granted) => set({ isMicPermissionGranted: granted }),
  addTranscriptEntry: (entry) => set((s) => ({
    transcript: [...s.transcript, entry],
  })),
  updateInterimTranscript: (id, text) => set((s) => ({
    transcript: s.transcript.map((e) =>
      e.id === id ? { ...e, text } : e
    ),
  })),
  clearTranscript: () => set({ transcript: [] }),
  setVoiceEnabled: (enabled) => set({ voiceEnabled: enabled }),
  setPushToTalk: (enabled) => set({ pushToTalk: enabled }),
  setNotificationsEnabled: (enabled) => set({ notificationsEnabled: enabled }),
}));
```

- [ ] Create `packages/dashboard/src/components/voice/VoiceModeToggle.tsx`:

```tsx
import { useCallback } from 'react';
import { useVoiceStore } from '../../stores/voice/store.js';

interface VoiceModeToggleProps {
  onConnect: () => Promise<void>;
  onDisconnect: () => void;
  className?: string;
  size?: 'sm' | 'md' | 'lg';
}

export function VoiceModeToggle({
  onConnect,
  onDisconnect,
  className = '',
  size = 'md',
}: VoiceModeToggleProps): JSX.Element {
  const { connectionState, isConnected, isSpeaking, isListening } = useVoiceStore();

  const handleToggle = useCallback(async () => {
    if (isConnected) {
      onDisconnect();
    } else {
      await onConnect();
    }
  }, [isConnected, onConnect, onDisconnect]);

  // Determine visual state
  const visualState = getVisualState(connectionState, isListening, isSpeaking);

  const sizeClasses = {
    sm: 'w-8 h-8',
    md: 'w-10 h-10',
    lg: 'w-12 h-12',
  };

  const iconSizeClasses = {
    sm: 'w-4 h-4',
    md: 'w-5 h-5',
    lg: 'w-6 h-6',
  };

  return (
    <button
      onClick={handleToggle}
      className={`
        relative flex items-center justify-center rounded-full
        transition-all duration-200 focus:outline-none focus:ring-2 focus:ring-offset-2
        ${sizeClasses[size]}
        ${visualState.buttonClasses}
        ${className}
      `}
      aria-label={visualState.ariaLabel}
      title={visualState.tooltip}
    >
      {/* Mic icon */}
      <MicIcon className={iconSizeClasses[size]} />

      {/* Pulsing ring for active states */}
      {visualState.pulsing && (
        <span className="absolute inset-0 rounded-full animate-ping opacity-30 bg-current" />
      )}

      {/* Status dot */}
      <span
        className={`absolute -top-0.5 -right-0.5 w-2.5 h-2.5 rounded-full border-2 border-white ${visualState.dotColor}`}
      />
    </button>
  );
}

interface VisualState {
  buttonClasses: string;
  dotColor: string;
  pulsing: boolean;
  ariaLabel: string;
  tooltip: string;
}

function getVisualState(
  connectionState: string,
  isListening: boolean,
  isSpeaking: boolean,
): VisualState {
  if (connectionState === 'disconnected' || connectionState === 'error') {
    return {
      buttonClasses: 'bg-gray-100 hover:bg-gray-200 text-gray-500 focus:ring-gray-400',
      dotColor: connectionState === 'error' ? 'bg-red-500' : 'bg-gray-400',
      pulsing: false,
      ariaLabel: connectionState === 'error' ? 'Voice mode error. Click to retry.' : 'Enable voice mode',
      tooltip: connectionState === 'error' ? 'Connection error' : 'Click to start voice mode',
    };
  }

  if (connectionState === 'connecting') {
    return {
      buttonClasses: 'bg-yellow-100 text-yellow-600 cursor-wait',
      dotColor: 'bg-yellow-400',
      pulsing: true,
      ariaLabel: 'Connecting voice mode...',
      tooltip: 'Connecting...',
    };
  }

  if (isListening) {
    return {
      buttonClasses: 'bg-red-100 hover:bg-red-200 text-red-600 focus:ring-red-400',
      dotColor: 'bg-red-500',
      pulsing: true,
      ariaLabel: 'Listening. Click to disable voice mode.',
      tooltip: 'Listening...',
    };
  }

  if (isSpeaking) {
    return {
      buttonClasses: 'bg-green-100 text-green-600 focus:ring-green-400',
      dotColor: 'bg-green-500',
      pulsing: false,
      ariaLabel: 'Tamma is speaking. Click to disable voice mode.',
      tooltip: 'Speaking...',
    };
  }

  if (connectionState === 'processing') {
    return {
      buttonClasses: 'bg-yellow-100 text-yellow-600 focus:ring-yellow-400',
      dotColor: 'bg-yellow-500',
      pulsing: true,
      ariaLabel: 'Processing your request. Click to disable voice mode.',
      tooltip: 'Processing...',
    };
  }

  // Connected idle
  return {
    buttonClasses: 'bg-blue-100 hover:bg-blue-200 text-blue-600 focus:ring-blue-400',
    dotColor: 'bg-blue-500',
    pulsing: false,
    ariaLabel: 'Voice mode active. Click to disable.',
    tooltip: 'Voice mode active',
  };
}

function MicIcon({ className }: { className: string }): JSX.Element {
  return (
    <svg className={className} fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
      <path strokeLinecap="round" strokeLinejoin="round"
        d="M19 10v2a7 7 0 01-14 0v-2M12 1a3 3 0 00-3 3v6a3 3 0 006 0V4a3 3 0 00-3-3z" />
      <line x1="12" y1="19" x2="12" y2="23" />
      <line x1="8" y1="23" x2="16" y2="23" />
    </svg>
  );
}
```

### Files to Modify/Create

- CREATE `packages/dashboard/src/stores/voice/store.ts`
- CREATE `packages/dashboard/src/components/voice/VoiceModeToggle.tsx`
- CREATE `packages/dashboard/src/components/voice/VoiceModeToggle.test.tsx`
- CREATE `packages/dashboard/src/stores/voice/store.test.ts`

### Dependencies

- [ ] Story 24-2 Task 3: `useVoiceSession` hook types
- [ ] `zustand` (already a dependency)
- [ ] React, Tailwind CSS (already in dashboard)

## Testing Strategy

### Unit Tests -- store.test.ts

- [ ] Test initial state values
- [ ] Test `setConnectionState` updates `connectionState` and `isConnected`
- [ ] Test `addTranscriptEntry` appends to transcript array
- [ ] Test `updateInterimTranscript` updates matching entry by ID
- [ ] Test `clearTranscript` empties the array
- [ ] Test `setVoiceEnabled` toggles the flag
- [ ] Test `setPushToTalk` toggles the flag

### Unit Tests -- VoiceModeToggle.test.tsx

- [ ] Test renders mic button in disconnected state (gray)
- [ ] Test click calls onConnect when disconnected
- [ ] Test click calls onDisconnect when connected
- [ ] Test listening state shows red pulsing ring
- [ ] Test speaking state shows green indicator
- [ ] Test processing state shows yellow indicator
- [ ] Test error state shows red dot
- [ ] Test connecting state shows cursor-wait
- [ ] Test aria-label changes with state
- [ ] Test size prop affects button dimensions
- [ ] Test focus ring visible on keyboard navigation

### Validation Steps

1. [ ] Create Zustand store with all voice state
2. [ ] Create VoiceModeToggle with visual states
3. [ ] Test all visual state transitions
4. [ ] Test accessibility (aria-labels, focus)
5. [ ] Test responsiveness at different sizes
6. [ ] Run all unit tests
7. [ ] Verify TypeScript compiles

## Notes & Considerations

- The Zustand store is the single source of truth for voice state in the dashboard. The `useVoiceSession` hook writes to this store, and all voice components read from it.
- The VoiceModeToggle is designed to be placed alongside the existing text input in the chat UI, not as a separate page. It should take minimal space (40x40px default).
- The pulsing animation (Tailwind `animate-ping`) provides clear visual feedback that the mic is active, which is important for user confidence that their voice is being captured.
- The status dot in the corner provides at-a-glance state information even when the button is small.
- All visual states have distinct colors to be accessible to users with color vision deficiency. The combination of color + animation + position differentiates each state.

## Completion Checklist

- [ ] Zustand store with all voice state and actions
- [ ] VoiceModeToggle component with all visual states
- [ ] Color coding: gray/red/yellow/green for states
- [ ] Pulsing animation for active states
- [ ] Accessibility: aria-labels, focus rings
- [ ] Size variants (sm/md/lg)
- [ ] All unit tests passing
- [ ] TypeScript compiles
