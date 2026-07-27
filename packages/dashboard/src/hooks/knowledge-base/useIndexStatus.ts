/**
 * Index Status Hook
 *
 * React hook for managing index status, triggering, history, and configuration.
 */

import { useState, useEffect, useCallback } from 'react';
import type { IndexStatus, IndexHistoryEntry, IndexConfig } from '@tamma/shared';
import { indexApi } from '../../services/knowledge-base/api-client.js';

export interface UseIndexStatusReturn {
  status: IndexStatus | null;
  history: IndexHistoryEntry[];
  config: IndexConfig | null;
  loading: boolean;
  error: string | null;
  triggerIndex: (fullReindex?: boolean) => Promise<void>;
  cancelIndex: () => Promise<void>;
  refreshStatus: () => Promise<void>;
  loadHistory: (limit?: number) => Promise<void>;
  loadConfig: () => Promise<void>;
  updateConfig: (config: Partial<IndexConfig>) => Promise<void>;
}

export function useIndexStatus(pollIntervalMs = 5000): UseIndexStatusReturn {
  const [status, setStatus] = useState<IndexStatus | null>(null);
  const [history, setHistory] = useState<IndexHistoryEntry[]>([]);
  const [config, setConfig] = useState<IndexConfig | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const refreshStatus = useCallback(async () => {
    try {
      const data = await indexApi.getStatus();
      setStatus(data);
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to fetch status');
    }
  }, []);

  const loadHistory = useCallback(async (limit = 20) => {
    try {
      const data = await indexApi.getHistory(limit);
      setHistory(data);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load history');
    }
  }, []);

  const loadConfig = useCallback(async () => {
    try {
      const data = await indexApi.getConfig();
      setConfig(data);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load config');
    }
  }, []);

  const triggerIndex = useCallback(async (fullReindex?: boolean) => {
    try {
      await indexApi.triggerIndex(fullReindex !== undefined ? { fullReindex } : {});
      await refreshStatus();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to trigger index');
    }
  }, [refreshStatus]);

  const cancelIndex = useCallback(async () => {
    try {
      await indexApi.cancelIndex();
      await refreshStatus();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to cancel index');
    }
  }, [refreshStatus]);

  const updateConfig = useCallback(async (newConfig: Partial<IndexConfig>) => {
    try {
      const updated = await indexApi.updateConfig(newConfig);
      setConfig(updated);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to update config');
    }
  }, []);

  // Initial load
  useEffect(() => {
    const init = async () => {
      setLoading(true);
      await refreshStatus();
      setLoading(false);
    };
    void init();
  }, [refreshStatus]);

  // Poll for status updates
  useEffect(() => {
    const interval = setInterval(() => {
      void refreshStatus();
    }, pollIntervalMs);
    return () => clearInterval(interval);
  }, [pollIntervalMs, refreshStatus]);

  return {
    status,
    history,
    config,
    loading,
    error,
    triggerIndex,
    cancelIndex,
    refreshStatus,
    loadHistory,
    loadConfig,
    updateConfig,
  };
}
