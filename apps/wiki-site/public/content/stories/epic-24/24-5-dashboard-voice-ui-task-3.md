---
title: "Task 3: VoiceSettings Panel + ConnectionStatus Component"
sidebar:
  order: 240
---

**Story:** 24-5-dashboard-voice-ui - Dashboard Voice UI
**Epic:** 24

## Task Description

Create the `VoiceSettings` panel for STT/TTS provider selection, voice selection, and language selection. Create the `ConnectionStatus` component that shows WebSocket connection state with reconnecting indicators.

## Acceptance Criteria

- `VoiceSettings` panel: STT provider (Deepgram/Whisper), TTS provider (ElevenLabs/OpenAI TTS)
- Voice selection dropdown: list available TTS voices
- Language selection dropdown: common languages
- Notification toggle: enable/disable proactive spoken notifications
- Push-to-talk toggle: switch between VAD mode and push-to-talk
- Settings persisted via `PUT /api/v1/voice/config`
- `ConnectionStatus` indicator: connected (green), reconnecting (yellow), disconnected (gray)
- Reconnection attempt count shown during reconnect

## Implementation Details

### Technical Requirements

- [ ] Create `packages/dashboard/src/components/voice/VoiceSettings.tsx`:

```tsx
import { useState, useCallback, useEffect } from 'react';
import { useVoiceStore } from '../../stores/voice/store.js';

interface VoiceSettingsProps {
  isOpen: boolean;
  onClose: () => void;
}

interface VoiceConfigFormState {
  sttProvider: 'deepgram' | 'openai-whisper';
  ttsProvider: 'elevenlabs' | 'openai-tts';
  voice: string;
  language: string;
  pushToTalk: boolean;
  notificationsEnabled: boolean;
}

const LANGUAGES = [
  { code: 'en-US', label: 'English (US)' },
  { code: 'en-GB', label: 'English (UK)' },
  { code: 'es-ES', label: 'Spanish' },
  { code: 'fr-FR', label: 'French' },
  { code: 'de-DE', label: 'German' },
  { code: 'ja-JP', label: 'Japanese' },
  { code: 'zh-CN', label: 'Chinese (Simplified)' },
  { code: 'ar-SA', label: 'Arabic' },
];

const TTS_VOICES = [
  { id: 'alloy', label: 'Alloy', provider: 'openai-tts' },
  { id: 'echo', label: 'Echo', provider: 'openai-tts' },
  { id: 'fable', label: 'Fable', provider: 'openai-tts' },
  { id: 'onyx', label: 'Onyx', provider: 'openai-tts' },
  { id: 'nova', label: 'Nova', provider: 'openai-tts' },
  { id: 'shimmer', label: 'Shimmer', provider: 'openai-tts' },
  // ElevenLabs voices loaded from API at runtime
];

export function VoiceSettings({ isOpen, onClose }: VoiceSettingsProps): JSX.Element | null {
  const { pushToTalk, notificationsEnabled, setPushToTalk, setNotificationsEnabled } = useVoiceStore();
  const [form, setForm] = useState<VoiceConfigFormState>({
    sttProvider: 'deepgram',
    ttsProvider: 'elevenlabs',
    voice: 'alloy',
    language: 'en-US',
    pushToTalk,
    notificationsEnabled,
  });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Load config on open
  useEffect(() => {
    if (isOpen) {
      void loadConfig();
    }
  }, [isOpen]);

  async function loadConfig(): Promise<void> {
    try {
      const response = await fetch('/api/v1/voice/config', { credentials: 'include' });
      if (response.ok) {
        const data = (await response.json()) as { config: VoiceConfigFormState };
        setForm(data.config);
      }
    } catch {
      // Use defaults
    }
  }

  const handleSave = useCallback(async () => {
    setSaving(true);
    setError(null);
    try {
      const response = await fetch('/api/v1/voice/config', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify(form),
      });
      if (!response.ok) {
        throw new Error('Failed to save settings');
      }
      setPushToTalk(form.pushToTalk);
      setNotificationsEnabled(form.notificationsEnabled);
      onClose();
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Unknown error');
    } finally {
      setSaving(false);
    }
  }, [form, onClose, setPushToTalk, setNotificationsEnabled]);

  if (!isOpen) return null;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50">
      <div className="bg-white rounded-xl shadow-xl w-full max-w-md mx-4 p-6">
        <div className="flex items-center justify-between mb-6">
          <h2 className="text-lg font-semibold text-gray-900">Voice Settings</h2>
          <button onClick={onClose} className="text-gray-400 hover:text-gray-600" aria-label="Close settings">
            {/* X icon */}
            <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
        </div>

        <div className="space-y-4">
          {/* STT Provider */}
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">Speech Recognition</label>
            <select
              value={form.sttProvider}
              onChange={(e) => setForm({ ...form, sttProvider: e.target.value as 'deepgram' | 'openai-whisper' })}
              className="w-full border border-gray-300 rounded-lg px-3 py-2 text-sm"
            >
              <option value="deepgram">Deepgram (Nova-3, realtime)</option>
              <option value="openai-whisper">OpenAI Whisper (batch, fallback)</option>
            </select>
          </div>

          {/* TTS Provider */}
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">Voice Output</label>
            <select
              value={form.ttsProvider}
              onChange={(e) => setForm({ ...form, ttsProvider: e.target.value as 'elevenlabs' | 'openai-tts' })}
              className="w-full border border-gray-300 rounded-lg px-3 py-2 text-sm"
            >
              <option value="elevenlabs">ElevenLabs (Flash v2.5, low latency)</option>
              <option value="openai-tts">OpenAI TTS (tts-1, fallback)</option>
            </select>
          </div>

          {/* Voice */}
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">Voice</label>
            <select
              value={form.voice}
              onChange={(e) => setForm({ ...form, voice: e.target.value })}
              className="w-full border border-gray-300 rounded-lg px-3 py-2 text-sm"
            >
              {TTS_VOICES.map((v) => (
                <option key={v.id} value={v.id}>{v.label}</option>
              ))}
            </select>
          </div>

          {/* Language */}
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">Language</label>
            <select
              value={form.language}
              onChange={(e) => setForm({ ...form, language: e.target.value })}
              className="w-full border border-gray-300 rounded-lg px-3 py-2 text-sm"
            >
              {LANGUAGES.map((l) => (
                <option key={l.code} value={l.code}>{l.label}</option>
              ))}
            </select>
          </div>

          {/* Push to Talk */}
          <div className="flex items-center justify-between">
            <div>
              <span className="text-sm font-medium text-gray-700">Push to Talk</span>
              <p className="text-xs text-gray-500">Hold Space to speak instead of auto-detect</p>
            </div>
            <ToggleSwitch
              checked={form.pushToTalk}
              onChange={(v) => setForm({ ...form, pushToTalk: v })}
            />
          </div>

          {/* Notifications */}
          <div className="flex items-center justify-between">
            <div>
              <span className="text-sm font-medium text-gray-700">Spoken Notifications</span>
              <p className="text-xs text-gray-500">Hear engine state updates spoken aloud</p>
            </div>
            <ToggleSwitch
              checked={form.notificationsEnabled}
              onChange={(v) => setForm({ ...form, notificationsEnabled: v })}
            />
          </div>
        </div>

        {error && <p className="text-sm text-red-600 mt-3">{error}</p>}

        <div className="flex justify-end gap-3 mt-6">
          <button onClick={onClose} className="px-4 py-2 text-sm text-gray-700 hover:bg-gray-100 rounded-lg">
            Cancel
          </button>
          <button
            onClick={handleSave}
            disabled={saving}
            className="px-4 py-2 text-sm text-white bg-blue-600 hover:bg-blue-700 rounded-lg disabled:opacity-50"
          >
            {saving ? 'Saving...' : 'Save'}
          </button>
        </div>
      </div>
    </div>
  );
}

function ToggleSwitch({ checked, onChange }: { checked: boolean; onChange: (v: boolean) => void }): JSX.Element {
  return (
    <button
      role="switch"
      aria-checked={checked}
      onClick={() => onChange(!checked)}
      className={`relative inline-flex h-6 w-11 items-center rounded-full transition-colors ${
        checked ? 'bg-blue-600' : 'bg-gray-200'
      }`}
    >
      <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${
        checked ? 'translate-x-6' : 'translate-x-1'
      }`} />
    </button>
  );
}
```

- [ ] Create `packages/dashboard/src/components/voice/ConnectionStatus.tsx`:

```tsx
import { useVoiceStore } from '../../stores/voice/store.js';

interface ConnectionStatusProps {
  className?: string;
  reconnectAttempts?: number;
}

export function ConnectionStatus({ className = '', reconnectAttempts = 0 }: ConnectionStatusProps): JSX.Element {
  const { connectionState } = useVoiceStore();

  const config = getStatusConfig(connectionState, reconnectAttempts);

  return (
    <div className={`flex items-center gap-1.5 text-xs ${className}`} aria-live="polite">
      <span className={`w-2 h-2 rounded-full ${config.dotColor} ${config.animate ? 'animate-pulse' : ''}`} />
      <span className={config.textColor}>{config.label}</span>
    </div>
  );
}

function getStatusConfig(state: string, attempts: number): {
  dotColor: string;
  textColor: string;
  label: string;
  animate: boolean;
} {
  switch (state) {
    case 'connected':
    case 'listening':
    case 'processing':
    case 'speaking':
      return { dotColor: 'bg-green-500', textColor: 'text-green-600', label: 'Connected', animate: false };
    case 'connecting':
      return { dotColor: 'bg-yellow-500', textColor: 'text-yellow-600', label: attempts > 0 ? `Reconnecting (${attempts})...` : 'Connecting...', animate: true };
    case 'error':
      return { dotColor: 'bg-red-500', textColor: 'text-red-600', label: 'Disconnected', animate: false };
    default:
      return { dotColor: 'bg-gray-400', textColor: 'text-gray-500', label: 'Not connected', animate: false };
  }
}
```

### Files to Modify/Create

- CREATE `packages/dashboard/src/components/voice/VoiceSettings.tsx`
- CREATE `packages/dashboard/src/components/voice/VoiceSettings.test.tsx`
- CREATE `packages/dashboard/src/components/voice/ConnectionStatus.tsx`
- CREATE `packages/dashboard/src/components/voice/ConnectionStatus.test.tsx`

### Dependencies

- [ ] Task 1: Voice Zustand store
- [ ] Story 24-1 Task 3: Voice config REST endpoints

## Testing Strategy

### Unit Tests -- VoiceSettings.test.tsx

- [ ] Test renders when isOpen is true
- [ ] Test does not render when isOpen is false
- [ ] Test STT provider dropdown shows both options
- [ ] Test TTS provider dropdown shows both options
- [ ] Test voice dropdown shows available voices
- [ ] Test language dropdown shows available languages
- [ ] Test push-to-talk toggle works
- [ ] Test notifications toggle works
- [ ] Test save button calls PUT /api/v1/voice/config
- [ ] Test cancel button calls onClose without saving
- [ ] Test error state shown on save failure
- [ ] Test loading state during save (disabled button)
- [ ] Test config loaded from API on open
- [ ] Test close button calls onClose

### Unit Tests -- ConnectionStatus.test.tsx

- [ ] Test shows "Not connected" when disconnected
- [ ] Test shows green dot when connected
- [ ] Test shows yellow pulsing dot when connecting
- [ ] Test shows reconnect attempt count
- [ ] Test shows red dot on error
- [ ] Test aria-live for screen readers

### Validation Steps

1. [ ] Create VoiceSettings modal with all dropdowns and toggles
2. [ ] Create ConnectionStatus indicator
3. [ ] Wire settings save to REST API
4. [ ] Test all form interactions
5. [ ] Test save/cancel flow
6. [ ] Run all unit tests
7. [ ] Verify TypeScript compiles

## Notes & Considerations

- The VoiceSettings panel is a modal overlay, matching the existing settings panel pattern in the dashboard.
- TTS voices for ElevenLabs should ideally be loaded from the ElevenLabs API at runtime (via a `GET /api/v1/voice/voices` endpoint). For now, the OpenAI voices are hardcoded. ElevenLabs voice listing can be added in a follow-up.
- The ToggleSwitch is an inline component matching iOS-style toggle pattern. It uses `role="switch"` and `aria-checked` for accessibility.
- ConnectionStatus uses `aria-live="polite"` so screen readers announce state changes without interrupting the user.

## Completion Checklist

- [ ] VoiceSettings modal with provider/voice/language selection
- [ ] Push-to-talk and notifications toggles
- [ ] Settings persistence via REST API
- [ ] ConnectionStatus with color-coded indicator
- [ ] Reconnect attempt count display
- [ ] Accessibility (aria attributes, roles)
- [ ] All unit tests passing
- [ ] TypeScript compiles
