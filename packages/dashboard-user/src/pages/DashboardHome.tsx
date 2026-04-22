/**
 * DashboardHome — landing page after login. Three widgets pull from
 * /api/v1/orgs/{tenantId}/dashboard/{summary,runs,stats}.
 *
 * When the user has no active tenant (fresh sign-up), show an empty
 * state pointing at /onboarding so we never issue calls against a null
 * tenantId.
 */

import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { useAuth } from '../hooks/useAuth';
import {
  fetchDashboardSummary,
  fetchRecentRuns,
  fetchDashboardStats,
  type DashboardSummary,
  type DashboardRuns,
  type DashboardStats,
} from '../api/dashboard';

export function DashboardHome(): JSX.Element {
  const { user } = useAuth();
  const [summary, setSummary] = useState<DashboardSummary | null>(null);
  const [runs, setRuns] = useState<DashboardRuns | null>(null);
  const [stats, setStats] = useState<DashboardStats | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const tenantId = user?.tenantId;
    if (!tenantId) return;

    let cancelled = false;
    async function load(): Promise<void> {
      try {
        const [s, r, st] = await Promise.all([
          fetchDashboardSummary(tenantId!),
          fetchRecentRuns(tenantId!, 10),
          fetchDashboardStats(tenantId!),
        ]);
        if (cancelled) return;
        setSummary(s);
        setRuns(r);
        setStats(st);
      } catch (err) {
        if (cancelled) return;
        setError(err instanceof Error ? err.message : 'Failed to load dashboard');
      }
    }
    void load();
    return () => {
      cancelled = true;
    };
  }, [user?.tenantId]);

  if (!user?.tenantId) {
    return (
      <div className="max-w-md mx-auto mt-16 p-6 bg-white rounded-lg shadow-sm border border-gray-200 text-center">
        <h2 className="text-lg font-medium text-gray-900">No organization</h2>
        <p className="mt-2 text-sm text-gray-500">
          You need to create or join an organization before you can use the dashboard.
        </p>
        <Link
          to="/onboarding"
          className="mt-4 inline-block px-4 py-2 text-sm font-medium text-white bg-gray-900 rounded-md"
        >
          Start onboarding
        </Link>
      </div>
    );
  }

  if (error) {
    return (
      <div role="alert" className="p-4 text-sm text-red-700 bg-red-50 rounded-md">
        {error}
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <h1 className="text-2xl font-bold text-gray-900">Dashboard</h1>

      <section
        aria-label="Quick stats"
        className="grid gap-4 grid-cols-1 sm:grid-cols-2 lg:grid-cols-4"
      >
        <StatCard label="Total events" value={summary?.totalEvents ?? '—'} />
        <StatCard label="Total runs" value={stats?.totalRuns ?? '—'} />
        <StatCard
          label="Success rate"
          value={
            stats ? `${Math.round(stats.successRate * 100)}%` : '—'
          }
        />
        <StatCard
          label="Avg duration"
          value={stats ? `${Math.round(stats.avgDurationSeconds)}s` : '—'}
        />
      </section>

      <section aria-label="Recent runs">
        <h2 className="text-lg font-medium text-gray-900 mb-2">Recent runs</h2>
        {runs === null ? (
          <p className="text-sm text-gray-500">Loading…</p>
        ) : runs.runs.length === 0 ? (
          <p className="text-sm text-gray-500">No runs yet.</p>
        ) : (
          <ul className="divide-y divide-gray-200 bg-white rounded-md border border-gray-200">
            {runs.runs.map((run) => (
              <li key={run.id} className="p-3 flex items-center justify-between">
                <div>
                  <div className="text-sm font-medium text-gray-900">{run.id}</div>
                  <div className="text-xs text-gray-500">{run.status}</div>
                </div>
                <div className="text-xs text-gray-500">
                  {new Date(run.createdAt).toLocaleString()}
                </div>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}

function StatCard({
  label,
  value,
}: {
  label: string;
  value: string | number;
}): JSX.Element {
  return (
    <div className="bg-white rounded-md border border-gray-200 p-4">
      <div className="text-xs text-gray-500">{label}</div>
      <div className="mt-1 text-2xl font-bold text-gray-900">{value}</div>
    </div>
  );
}
