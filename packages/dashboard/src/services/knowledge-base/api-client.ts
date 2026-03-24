/**
 * Knowledge Base API Client
 *
 * Typed HTTP client for communicating with the Knowledge Base Management API.
 */

import type {
  IndexStatus,
  IndexHistoryEntry,
  IndexConfig,
  TriggerIndexRequest,
  CollectionInfo,
  CollectionStatsInfo,
  VectorSearchRequest,
  VectorSearchResult,
  StorageUsage,
  CreateCollectionRequest,
  RAGConfigInfo,
  RAGMetricsInfo,
  RAGTestRequest,
  RAGTestResult,
  MCPServerInfo,
  MCPTool,
  MCPToolInvokeResult,
  MCPServerLog,
  ContextTestRequest,
  ContextTestResult,
  ContextFeedbackRequest,
  UsageAnalytics,
  QualityAnalytics,
  CostAnalytics,
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

// === Index Management ===

export const indexApi = {
  getStatus: () => fetchJSON<IndexStatus>('/knowledge-base/index/status'),

  triggerIndex: (request?: TriggerIndexRequest) =>
    fetchJSON<{ message: string }>('/knowledge-base/index/trigger', {
      method: 'POST',
      body: JSON.stringify(request ?? {}),
    }),

  cancelIndex: () =>
    fetchJSON<{ message: string }>('/knowledge-base/index/cancel', {
      method: 'DELETE',
    }),

  getHistory: (limit = 20) =>
    fetchJSON<IndexHistoryEntry[]>(`/knowledge-base/index/history?limit=${limit}`),

  getConfig: () => fetchJSON<IndexConfig>('/knowledge-base/index/config'),

  updateConfig: (config: Partial<IndexConfig>) =>
    fetchJSON<IndexConfig>('/knowledge-base/index/config', {
      method: 'PUT',
      body: JSON.stringify(config),
    }),
};

// === Vector Database ===

export const vectorDBApi = {
  listCollections: () =>
    fetchJSON<CollectionInfo[]>('/knowledge-base/vector-db/collections'),

  createCollection: (request: CreateCollectionRequest) =>
    fetchJSON<{ message: string }>('/knowledge-base/vector-db/collections', {
      method: 'POST',
      body: JSON.stringify(request),
    }),

  getCollectionStats: (name: string) =>
    fetchJSON<CollectionStatsInfo>(`/knowledge-base/vector-db/collections/${name}/stats`),

  deleteCollection: (name: string) =>
    fetchJSON<{ message: string }>(`/knowledge-base/vector-db/collections/${name}`, {
      method: 'DELETE',
    }),

  search: (request: VectorSearchRequest) =>
    fetchJSON<VectorSearchResult[]>('/knowledge-base/vector-db/search', {
      method: 'POST',
      body: JSON.stringify(request),
    }),

  getStorageUsage: () =>
    fetchJSON<StorageUsage>('/knowledge-base/vector-db/storage'),
};

// === RAG Pipeline ===

export const ragApi = {
  getConfig: () => fetchJSON<RAGConfigInfo>('/knowledge-base/rag/config'),

  updateConfig: (config: Partial<RAGConfigInfo>) =>
    fetchJSON<RAGConfigInfo>('/knowledge-base/rag/config', {
      method: 'PUT',
      body: JSON.stringify(config),
    }),

  getMetrics: () => fetchJSON<RAGMetricsInfo>('/knowledge-base/rag/metrics'),

  testQuery: (request: RAGTestRequest) =>
    fetchJSON<RAGTestResult>('/knowledge-base/rag/test', {
      method: 'POST',
      body: JSON.stringify(request),
    }),
};

// === MCP Servers ===

export const mcpApi = {
  listServers: () =>
    fetchJSON<MCPServerInfo[]>('/knowledge-base/mcp/servers'),

  getServerStatus: (name: string) =>
    fetchJSON<MCPServerInfo>(`/knowledge-base/mcp/servers/${name}`),

  startServer: (name: string) =>
    fetchJSON<{ message: string }>(`/knowledge-base/mcp/servers/${name}/start`, {
      method: 'POST',
    }),

  stopServer: (name: string) =>
    fetchJSON<{ message: string }>(`/knowledge-base/mcp/servers/${name}/stop`, {
      method: 'POST',
    }),

  restartServer: (name: string) =>
    fetchJSON<{ message: string }>(`/knowledge-base/mcp/servers/${name}/restart`, {
      method: 'POST',
    }),

  listTools: (serverName: string) =>
    fetchJSON<MCPTool[]>(`/knowledge-base/mcp/servers/${serverName}/tools`),

  invokeTool: (serverName: string, toolName: string, args: Record<string, unknown>) =>
    fetchJSON<MCPToolInvokeResult>(
      `/knowledge-base/mcp/servers/${serverName}/tools/${toolName}/invoke`,
      { method: 'POST', body: JSON.stringify({ arguments: args }) },
    ),

  getServerLogs: (name: string, limit = 100) =>
    fetchJSON<MCPServerLog[]>(`/knowledge-base/mcp/servers/${name}/logs?limit=${limit}`),
};

// === Context Testing ===

export const contextApi = {
  testContext: (request: ContextTestRequest) =>
    fetchJSON<ContextTestResult>('/knowledge-base/context/test', {
      method: 'POST',
      body: JSON.stringify(request),
    }),

  submitFeedback: (feedback: ContextFeedbackRequest) =>
    fetchJSON<{ message: string }>('/knowledge-base/context/feedback', {
      method: 'POST',
      body: JSON.stringify(feedback),
    }),

  getHistory: (limit = 10) =>
    fetchJSON<ContextTestResult[]>(`/knowledge-base/context/history?limit=${limit}`),
};

// === Analytics ===

export const analyticsApi = {
  getUsageAnalytics: (start?: string, end?: string) => {
    const params = new URLSearchParams();
    if (start) params.set('start', start);
    if (end) params.set('end', end);
    return fetchJSON<UsageAnalytics>(`/knowledge-base/analytics/usage?${params}`);
  },

  getQualityAnalytics: (start?: string, end?: string) => {
    const params = new URLSearchParams();
    if (start) params.set('start', start);
    if (end) params.set('end', end);
    return fetchJSON<QualityAnalytics>(`/knowledge-base/analytics/quality?${params}`);
  },

  getCostAnalytics: (start?: string, end?: string) => {
    const params = new URLSearchParams();
    if (start) params.set('start', start);
    if (end) params.set('end', end);
    return fetchJSON<CostAnalytics>(`/knowledge-base/analytics/costs?${params}`);
  },
};
