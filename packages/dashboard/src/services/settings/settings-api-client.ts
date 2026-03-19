/**
 * Settings API Client
 *
 * Typed HTTP client for communicating with the Settings Management API.
 */

import type {
  IAgentsConfig,
  SecurityConfig,
  DiagnosticsEvent,
  DiagnosticsEventType,
} from '@tamma/shared';

const API_BASE = import.meta.env.VITE_API_BASE_URL ?? '/api';

async function fetchJSON<T>(url: string, options?: RequestInit): Promise<T> {
  const response = await fetch(`${API_BASE}${url}`, {
    headers: { 'Content-Type': 'application/json', ...options?.headers },
    ...options,
  });

  if (!response.ok) {
    const error = await response.json().catch(() => ({ error: response.statusText }));
    throw new Error((error as Record<string, string>).error ?? `HTTP ${response.status}`);
  }

  return response.json() as Promise<T>;
}

// === Agents Config ===

export const agentsApi = {
  getConfig: () => fetchJSON<IAgentsConfig>('/config/agents'),

  updateConfig: (config: IAgentsConfig) =>
    fetchJSON<IAgentsConfig>('/config/agents', {
      method: 'PUT',
      body: JSON.stringify(config),
    }),
};

// === Security Config ===

export const securityApi = {
  getConfig: () => fetchJSON<SecurityConfig>('/config/security'),

  updateConfig: (config: SecurityConfig) =>
    fetchJSON<SecurityConfig>('/config/security', {
      method: 'PUT',
      body: JSON.stringify(config),
    }),
};

// === Provider Health ===

export interface HealthStatusEntry {
  healthy: boolean;
  failures: number;
  circuitOpen: boolean;
}

export const healthApi = {
  getStatus: () => fetchJSON<Record<string, HealthStatusEntry>>('/providers/health'),
};

// === Diagnostics ===

export const diagnosticsApi = {
  getEvents: (options?: { limit?: number; type?: DiagnosticsEventType; since?: number }) => {
    const params = new URLSearchParams();
    if (options?.limit !== undefined) params.set('limit', String(options.limit));
    if (options?.type !== undefined) params.set('type', options.type);
    if (options?.since !== undefined) params.set('since', String(options.since));
    return fetchJSON<DiagnosticsEvent[]>(`/providers/diagnostics?${params}`);
  },
};

// === Prompt Templates ===

export interface PromptTemplateEntry {
  systemPrompt?: string;
  providerPrompts?: Record<string, string>;
}

export const promptsApi = {
  getTemplates: () => fetchJSON<Record<string, PromptTemplateEntry>>('/config/prompts'),

  updateTemplate: (role: string, template: PromptTemplateEntry) =>
    fetchJSON<{ message: string }>(`/config/prompts/${role}`, {
      method: 'PUT',
      body: JSON.stringify(template),
    }),
};
