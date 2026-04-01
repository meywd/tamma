---
title: "Task 4: Keyboard Shortcuts + Chat Integration + Mobile Responsiveness"
sidebar:
  order: 240
---

**Story:** 24-5-dashboard-voice-ui - Dashboard Voice UI
**Epic:** 24

## Task Description

Wire all voice components into the existing chat UI layout, add keyboard shortcuts (hold Space for push-to-talk, Cmd+Shift+V to toggle), handle microphone permission prompts, and ensure mobile responsiveness.

## Acceptance Criteria

- All voice components integrated into existing chat UI (not a separate page)
- Keyboard shortcut: hold Space for push-to-talk when push-to-talk mode is enabled
- Keyboard shortcut: Cmd+Shift+V (or Ctrl+Shift+V) to toggle voice mode
- Space key only captures when chat input is not focused (prevent conflict)
- Microphone permission prompt: clear UI explanation before requesting access
- Permission denied: graceful fallback to text-only with explanation
- Mobile responsive: mic button accessible on touch screens
- Voice and text messages interleave in same conversation view
- Voice components mount/unmount cleanly without resource leaks

## Implementation Details

### Technical Requirements

- [ ] Create keyboard shortcut hook `packages/dashboard/src/hooks/useVoiceKeyboard.ts`:

```typescript
import { useEffect, useCallback, useRef } from 'react';
import { useVoiceStore } from '../stores/voice/store.js';

interface UseVoiceKeyboardOptions {
  onToggleVoice: () => void;
  onStartListening: () => void;
  onStopListening: () => void;
  enabled?: boolean;
}

export function useVoiceKeyboard({
  onToggleVoice,
  onStartListening,
  onStopListening,
  enabled = true,
}: UseVoiceKeyboardOptions): void {
  const { pushToTalk, isConnected } = useVoiceStore();
  const spaceHeldRef = useRef(false);

  const handleKeyDown = useCallback((e: KeyboardEvent) => {
    if (!enabled) return;

    // Cmd/Ctrl + Shift + V -> toggle voice mode
    if (e.key === 'V' && e.shiftKey && (e.metaKey || e.ctrlKey)) {
      e.preventDefault();
      onToggleVoice();
      return;
    }

    // Space for push-to-talk (only when not in a text input)
    if (
      e.code === 'Space' &&
      pushToTalk &&
      isConnected &&
      !spaceHeldRef.current &&
      !isTextInputFocused()
    ) {
      e.preventDefault();
      spaceHeldRef.current = true;
      onStartListening();
    }
  }, [enabled, pushToTalk, isConnected, onToggleVoice, onStartListening]);

  const handleKeyUp = useCallback((e: KeyboardEvent) => {
    if (e.code === 'Space' && spaceHeldRef.current) {
      e.preventDefault();
      spaceHeldRef.current = false;
      onStopListening();
    }
  }, [onStopListening]);

  useEffect(() => {
    if (!enabled) return;
    document.addEventListener('keydown', handleKeyDown);
    document.addEventListener('keyup', handleKeyUp);
    return () => {
      document.removeEventListener('keydown', handleKeyDown);
      document.removeEventListener('keyup', handleKeyUp);
    };
  }, [enabled, handleKeyDown, handleKeyUp]);
}

function isTextInputFocused(): boolean {
  const el = document.activeElement;
  if (!el) return false;
  const tag = el.tagName.toLowerCase();
  return tag === 'input' || tag === 'textarea' || (el as HTMLElement).isContentEditable;
}
```

- [ ] Create mic permission component `packages/dashboard/src/components/voice/MicPermissionPrompt.tsx`:

```tsx
interface MicPermissionPromptProps {
  onAllow: () => void;
  onDeny: () => void;
}

export function MicPermissionPrompt({ onAllow, onDeny }: MicPermissionPromptProps): JSX.Element {
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50">
      <div className="bg-white rounded-xl shadow-xl w-full max-w-sm mx-4 p-6 text-center">
        {/* Mic icon */}
        <div className="mx-auto w-16 h-16 rounded-full bg-blue-100 flex items-center justify-center mb-4">
          <svg className="w-8 h-8 text-blue-600" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round"
              d="M19 10v2a7 7 0 01-14 0v-2M12 1a3 3 0 00-3 3v6a3 3 0 006 0V4a3 3 0 00-3-3z" />
          </svg>
        </div>

        <h3 className="text-lg font-semibold text-gray-900 mb-2">Microphone Access</h3>
        <p className="text-sm text-gray-600 mb-6">
          Tamma needs access to your microphone to enable voice conversation.
          Your audio is processed in real-time and not stored.
        </p>

        <div className="flex gap-3 justify-center">
          <button
            onClick={onDeny}
            className="px-4 py-2 text-sm text-gray-700 bg-gray-100 hover:bg-gray-200 rounded-lg"
          >
            Stay in text mode
          </button>
          <button
            onClick={onAllow}
            className="px-4 py-2 text-sm text-white bg-blue-600 hover:bg-blue-700 rounded-lg"
          >
            Allow microphone
          </button>
        </div>
      </div>
    </div>
  );
}
```

- [ ] Create the composed voice panel `packages/dashboard/src/components/voice/VoicePanel.tsx`:

```tsx
import { useState, useCallback } from 'react';
import { useVoiceSession } from '../../hooks/useVoiceSession.js';
import { useVoiceKeyboard } from '../../hooks/useVoiceKeyboard.js';
import { useVoiceStore } from '../../stores/voice/store.js';
import { VoiceModeToggle } from './VoiceModeToggle.js';
import { VoiceTranscript } from './VoiceTranscript.js';
import { AudioVisualizer } from './AudioVisualizer.js';
import { VoiceSettings } from './VoiceSettings.js';
import { ConnectionStatus } from './ConnectionStatus.js';
import { MicPermissionPrompt } from './MicPermissionPrompt.js';

interface VoicePanelProps {
  className?: string;
}

export function VoicePanel({ className = '' }: VoicePanelProps): JSX.Element {
  const { isConnected, voiceEnabled } = useVoiceStore();
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [permissionPrompt, setPermissionPrompt] = useState(false);

  const voice = useVoiceSession({
    onTranscript: (entry) => {
      useVoiceStore.getState().addTranscriptEntry(entry);
    },
    onError: (error) => {
      console.error('Voice error:', error);
    },
  });

  const handleConnect = useCallback(async () => {
    setPermissionPrompt(true);
  }, []);

  const handlePermissionAllow = useCallback(async () => {
    setPermissionPrompt(false);
    await voice.connect();
  }, [voice]);

  const handlePermissionDeny = useCallback(() => {
    setPermissionPrompt(false);
  }, []);

  // Keyboard shortcuts
  useVoiceKeyboard({
    onToggleVoice: () => {
      if (isConnected) voice.disconnect();
      else void handleConnect();
    },
    onStartListening: voice.startListening,
    onStopListening: voice.stopListening,
  });

  return (
    <div className={`flex flex-col ${className}`}>
      {/* Top bar: toggle + status + settings */}
      <div className="flex items-center gap-2 p-2">
        <VoiceModeToggle
          onConnect={handleConnect}
          onDisconnect={voice.disconnect}
          size="md"
        />
        {isConnected && <ConnectionStatus />}
        {isConnected && <AudioVisualizer analyserNode={null} height={24} barCount={12} />}
        {isConnected && (
          <button
            onClick={() => setSettingsOpen(true)}
            className="text-gray-400 hover:text-gray-600 ml-auto"
            aria-label="Voice settings"
          >
            {/* Gear icon */}
            <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round"
                d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.066 2.573c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.573 1.066c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.066-2.573c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z" />
              <path strokeLinecap="round" strokeLinejoin="round" d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
            </svg>
          </button>
        )}
      </div>

      {/* Transcript area (visible when voice is active) */}
      {isConnected && <VoiceTranscript className="flex-1 border-t border-gray-100" />}

      {/* Modals */}
      {permissionPrompt && (
        <MicPermissionPrompt onAllow={handlePermissionAllow} onDeny={handlePermissionDeny} />
      )}
      <VoiceSettings isOpen={settingsOpen} onClose={() => setSettingsOpen(false)} />
    </div>
  );
}
```

### Files to Modify/Create

- CREATE `packages/dashboard/src/hooks/useVoiceKeyboard.ts`
- CREATE `packages/dashboard/src/hooks/useVoiceKeyboard.test.ts`
- CREATE `packages/dashboard/src/components/voice/MicPermissionPrompt.tsx`
- CREATE `packages/dashboard/src/components/voice/VoicePanel.tsx`
- CREATE `packages/dashboard/src/components/voice/VoicePanel.test.tsx`
- MODIFY existing chat page/component to include `<VoicePanel />`

### Dependencies

- [ ] Tasks 1-3: All voice components and store
- [ ] Story 24-2 Task 3: `useVoiceSession` hook
- [ ] Existing chat UI layout component

## Testing Strategy

### Unit Tests -- useVoiceKeyboard.test.ts

- [ ] Test Cmd+Shift+V calls onToggleVoice
- [ ] Test Ctrl+Shift+V calls onToggleVoice (Windows/Linux)
- [ ] Test Space keydown calls onStartListening when pushToTalk=true and connected
- [ ] Test Space keyup calls onStopListening
- [ ] Test Space does NOT fire when text input is focused
- [ ] Test Space does NOT fire when pushToTalk=false
- [ ] Test Space does NOT fire when not connected
- [ ] Test cleanup removes event listeners on unmount
- [ ] Test disabled=false prevents all shortcuts

### Unit Tests -- VoicePanel.test.tsx

- [ ] Test renders VoiceModeToggle
- [ ] Test settings button visible when connected
- [ ] Test transcript visible when connected
- [ ] Test permission prompt shown on connect click
- [ ] Test permission allow triggers voice.connect()
- [ ] Test permission deny hides prompt
- [ ] Test cleanup on unmount

### Validation Steps

1. [ ] Create keyboard shortcut hook with Space and Cmd+Shift+V
2. [ ] Create mic permission prompt
3. [ ] Create composed VoicePanel
4. [ ] Integrate into existing chat layout
5. [ ] Test keyboard shortcuts
6. [ ] Test permission flow
7. [ ] Test mobile layout
8. [ ] Run all unit tests
9. [ ] Verify TypeScript compiles

## Notes & Considerations

- Space key for push-to-talk requires checking `isTextInputFocused()` to prevent conflict with typing in the chat input. The check looks at `document.activeElement` for input/textarea/contentEditable elements.
- The mic permission prompt is shown BEFORE calling `getUserMedia()`. This gives the user context about why the permission is needed, improving the grant rate. The browser's native permission dialog appears after the user clicks "Allow microphone".
- The VoicePanel is designed to integrate alongside the existing chat input, not replace it. Both text and voice input should be visible simultaneously.
- Mobile responsiveness: the mic button should be easily tappable (minimum 44x44px touch target). The settings gear icon should also be accessible on touch.
- The VoicePanel handles the coordination between `useVoiceSession` hook and the Zustand store. The hook produces events, and the panel syncs them to the store for consumption by child components.

## Completion Checklist

- [ ] Keyboard shortcuts: Cmd+Shift+V toggle, Space push-to-talk
- [ ] Space key prevented when text input focused
- [ ] Mic permission prompt with clear explanation
- [ ] Permission denied fallback to text mode
- [ ] VoicePanel composed from all components
- [ ] Integrated into existing chat UI layout
- [ ] Mobile responsive (44px touch targets)
- [ ] Clean mount/unmount without leaks
- [ ] All unit tests passing
- [ ] TypeScript compiles
