# Story 24-5: Dashboard Voice UI

Status: planned

## Story

As a user, I want a voice mode toggle in the dashboard with visual feedback so I can seamlessly switch between text and voice interaction.

## Acceptance Criteria

1. `VoiceModeToggle` component: mic button with status indicator (idle, listening, processing, speaking)
2. Integrated into existing chat UI layout (not a separate page)
3. `VoiceTranscript` component: realtime transcript of both user speech and AI responses in the chat panel
4. `AudioVisualizer` component: waveform/level meter showing mic activity
5. `VoiceSettings` panel: STT/TTS provider selection, voice selection, language selection
6. Permission prompt: clear UI for microphone access, graceful fallback to text if denied
7. Keyboard shortcut: hold Space to talk (push-to-talk), or toggle mode with Cmd+Shift+V
8. Mobile responsive: mic button accessible on mobile, voice controls adapt to small screens
9. Connection status indicator: shows WebSocket connected/reconnecting/disconnected
10. Voice and text messages interleave in same conversation — switching modes doesn't break context

## Files

| File | Action |
|------|--------|
| `packages/dashboard/src/components/voice/VoiceModeToggle.tsx` | CREATE |
| `packages/dashboard/src/components/voice/VoiceTranscript.tsx` | CREATE |
| `packages/dashboard/src/components/voice/VoiceSettings.tsx` | CREATE |
| `packages/dashboard/src/components/voice/AudioVisualizer.tsx` | CREATE |
| `packages/dashboard/src/components/voice/ConnectionStatus.tsx` | CREATE |
| `packages/dashboard/src/stores/voice/store.ts` | CREATE |

## Estimated Effort

1 week
