/**
 * Dashboard API contract — mirrors Story 18-5's
 * /api/v1/orgs/{tenantId}/dashboard/* endpoints.
 */

import { apiClient } from './client';

export interface DashboardSummary {
  tenantId: string;
  totalEvents: number;
  totalWorkflows: number;
  workflowDefinitions: number;
  recentEvents: Array<{
    id: string;
    type: string;
    createdAt: string;
    issueNumber: number | null;
  }>;
  timestamp: string;
}

export interface DashboardRun {
  id: string;
  definitionId: string;
  status: string;
  currentActivity: string | null;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  durationMs: number | null;
}

export interface DashboardRuns {
  tenantId: string;
  total: number;
  runs: DashboardRun[];
}

export interface DashboardStats {
  tenantId: string;
  totalRuns: number;
  completedRuns: number;
  failedRuns: number;
  runningRuns: number;
  successRate: number;
  avgDurationSeconds: number;
}

export async function fetchDashboardSummary(tenantId: string): Promise<DashboardSummary> {
  return apiClient.get<DashboardSummary>(`/api/v1/orgs/${tenantId}/dashboard/summary`);
}

export async function fetchRecentRuns(
  tenantId: string,
  limit?: number,
): Promise<DashboardRuns> {
  const path =
    limit !== undefined
      ? `/api/v1/orgs/${tenantId}/dashboard/runs?limit=${limit}`
      : `/api/v1/orgs/${tenantId}/dashboard/runs`;
  return apiClient.get<DashboardRuns>(path);
}

export async function fetchDashboardStats(tenantId: string): Promise<DashboardStats> {
  return apiClient.get<DashboardStats>(`/api/v1/orgs/${tenantId}/dashboard/stats`);
}
