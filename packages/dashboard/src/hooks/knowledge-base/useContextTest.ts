/**
 * Context Test Hook
 *
 * React hook for interactive context retrieval testing.
 */

import { useState, useCallback } from 'react';
import type {
  ContextTestResult,
  UIContextSource,
  UITaskType,
  RelevanceFeedbackInput,
} from '@tamma/shared';
import { contextApi } from '../../services/knowledge-base/api-client.js';

export interface UseContextTestReturn {
  result: ContextTestResult | null;
  history: ContextTestResult[];
  loading: boolean;
  error: string | null;
  testContext: (
    query: string,
    taskType: UITaskType,
    maxTokens: number,
    sources?: UIContextSource[],
    options?: { deduplicate?: boolean; includeMetadata?: boolean },
  ) => Promise<void>;
  submitFeedback: (requestId: string, feedback: RelevanceFeedbackInput[]) => Promise<void>;
  loadHistory: (limit?: number) => Promise<void>;
}

export function useContextTest(): UseContextTestReturn {
  const [result, setResult] = useState<ContextTestResult | null>(null);
  const [history, setHistory] = useState<ContextTestResult[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const testContext = useCallback(async (
    query: string,
    taskType: UITaskType,
    maxTokens: number,
    sources?: UIContextSource[],
    options?: { deduplicate?: boolean; includeMetadata?: boolean },
  ) => {
    setLoading(true);
    setError(null);
    try {
      const data = await contextApi.testContext({
        query,
        taskType,
        maxTokens,
        ...(sources !== undefined ? { sources } : {}),
        ...(options !== undefined ? { options } : {}),
      });
      setResult(data);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Context test failed');
    } finally {
      setLoading(false);
    }
  }, []);

  const submitFeedback = useCallback(async (
    requestId: string,
    feedback: RelevanceFeedbackInput[],
  ) => {
    try {
      await contextApi.submitFeedback({ requestId, feedback });
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to submit feedback');
    }
  }, []);

  const loadHistory = useCallback(async (limit = 10) => {
    try {
      const data = await contextApi.getHistory(limit);
      setHistory(data);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load history');
    }
  }, []);

  return {
    result,
    history,
    loading,
    error,
    testContext,
    submitFeedback,
    loadHistory,
  };
}
