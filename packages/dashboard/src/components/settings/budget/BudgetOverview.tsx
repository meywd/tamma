
import type { AgentType } from '@tamma/shared';
import { useAgentsConfig } from '../../../hooks/settings/useAgentsConfig.js';
import { useDiagnostics } from '../../../hooks/settings/useDiagnostics.js';
import { Card } from '../../common/Card.js';
import { Badge } from '../../common/Badge.js';
import { LoadingSpinner } from '../../common/LoadingSpinner.js';
import { CostBreakdownTable } from './CostBreakdownTable.js';

import type { JSX } from "react";

const ALL_ROLES: AgentType[] = [
  'scrum_master', 'architect', 'researcher', 'analyst',
  'planner', 'implementer', 'reviewer', 'tester', 'documenter',
];

const ROLE_LABELS: Record<string, string> = {
  scrum_master: 'Scrum Master',
  architect: 'Architect',
  researcher: 'Researcher',
  analyst: 'Analyst',
  planner: 'Planner',
  implementer: 'Implementer',
  reviewer: 'Reviewer',
  tester: 'Tester',
  documenter: 'Documenter',
};

export function BudgetOverview(): JSX.Element {
  const { config, loading: configLoading } = useAgentsConfig();
  const { events, loading: diagLoading } = useDiagnostics({ limit: 200 });

  if (configLoading && !config) return <LoadingSpinner />;

  // Compute cost per role from diagnostics events
  const costByRole: Record<string, number> = {};
  for (const event of events) {
    if ('providerName' in event && event.costUsd !== undefined && event.agentType) {
      costByRole[event.agentType] = (costByRole[event.agentType] ?? 0) + event.costUsd;
    }
  }

  return (
    <div className="space-y-6">
      <Card title="Budget per Role">
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
          {ALL_ROLES.map((role) => {
            const roleConfig = config?.roles?.[role];
            const budget = roleConfig?.maxBudgetUsd ?? config?.defaults.maxBudgetUsd;
            const spent = costByRole[role] ?? 0;
            const pct = budget ? Math.min((spent / budget) * 100, 100) : 0;
            const variant = pct > 90 ? 'error' : pct > 70 ? 'warning' : 'healthy';

            return (
              <div key={role} className="p-4 bg-gray-50 rounded-lg border border-gray-200 dark:bg-gray-900 dark:border-gray-700">
                <div className="flex items-center justify-between mb-2">
                  <span className="text-sm font-medium text-gray-900 dark:text-gray-100">
                    {ROLE_LABELS[role]}
                  </span>
                  <Badge variant={variant}>
                    {budget ? `$${spent.toFixed(3)} / $${budget}` : 'No limit'}
                  </Badge>
                </div>
                {budget !== undefined && (
                  <div className="w-full bg-gray-200 rounded-full h-2 dark:bg-gray-700">
                    <div
                      className={`h-2 rounded-full transition-all ${ pct > 90 ? 'bg-red-500' : pct > 70 ? 'bg-yellow-500' : 'bg-green-500' }`}
                      style={{ width: `${pct}%` }}
                    />
                  </div>
                )}
              </div>
            );
          })}
        </div>
      </Card>

      <Card title="Cost Breakdown">
        {diagLoading && events.length === 0 ? (
          <LoadingSpinner />
        ) : (
          <CostBreakdownTable events={events} />
        )}
      </Card>
    </div>
  );
}
