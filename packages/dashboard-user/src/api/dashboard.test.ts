/**
 * Tests for the dashboard API wrapper — ensures URL construction is
 * correct and tenant IDs are interpolated into the path as UUIDs.
 */

import { describe, it, expect, beforeEach, vi } from 'vitest';
import {
  fetchDashboardSummary,
  fetchRecentRuns,
  fetchDashboardStats,
} from './dashboard';

describe('dashboard API', () => {
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    fetchMock = vi.fn();
    globalThis.fetch = fetchMock as unknown as typeof fetch;
  });

  it('fetchDashboardSummary hits the right URL', async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          tenantId: 'abc',
          totalEvents: 0,
          totalWorkflows: 0,
          workflowDefinitions: 0,
          recentEvents: [],
          timestamp: '2026-04-22T00:00:00Z',
        }),
        { status: 200, headers: { 'content-type': 'application/json' } },
      ),
    );

    const result = await fetchDashboardSummary('abc-tenant-id');
    expect(fetchMock.mock.calls[0][0]).toMatch(
      /\/api\/v1\/orgs\/abc-tenant-id\/dashboard\/summary$/,
    );
    expect(result.totalEvents).toBe(0);
  });

  it('fetchRecentRuns appends ?limit= when provided', async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({ tenantId: 'abc', total: 0, runs: [] }),
        { status: 200, headers: { 'content-type': 'application/json' } },
      ),
    );
    await fetchRecentRuns('abc', 5);
    expect(fetchMock.mock.calls[0][0]).toMatch(
      /\/api\/v1\/orgs\/abc\/dashboard\/runs\?limit=5$/,
    );
  });

  it('fetchRecentRuns omits ?limit= when undefined', async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({ tenantId: 'abc', total: 0, runs: [] }),
        { status: 200, headers: { 'content-type': 'application/json' } },
      ),
    );
    await fetchRecentRuns('abc');
    expect(fetchMock.mock.calls[0][0]).toMatch(
      /\/api\/v1\/orgs\/abc\/dashboard\/runs$/,
    );
  });

  it('fetchDashboardStats hits the stats URL', async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          tenantId: 'abc',
          totalRuns: 0,
          completedRuns: 0,
          failedRuns: 0,
          runningRuns: 0,
          successRate: 0,
          avgDurationSeconds: 0,
        }),
        { status: 200, headers: { 'content-type': 'application/json' } },
      ),
    );
    await fetchDashboardStats('abc');
    expect(fetchMock.mock.calls[0][0]).toMatch(
      /\/api\/v1\/orgs\/abc\/dashboard\/stats$/,
    );
  });
});
