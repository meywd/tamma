
import type { IAgentRoleConfig } from '@tamma/shared';
import { Card } from '../../common/Card.js';
import { Badge } from '../../common/Badge.js';
import { ProviderChainEditor } from './ProviderChainEditor.js';

import type { JSX } from "react";

interface AgentRoleCardProps {
  role: string;
  roleConfig: Partial<IAgentRoleConfig>;
  isDefaults?: boolean;
  onChange: (config: Partial<IAgentRoleConfig>) => void;
}

const ROLE_LABELS: Record<string, string> = {
  defaults: 'Default Configuration',
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

export function AgentRoleCard({ role, roleConfig, isDefaults = false, onChange }: AgentRoleCardProps): JSX.Element {
  const label = ROLE_LABELS[role] ?? role;
  const chainLength = roleConfig.providerChain?.length ?? 0;

  return (
    <Card
      title={label}
      actions={
        <div className="flex items-center gap-2">
          {isDefaults && <Badge variant="info">Default</Badge>}
          {chainLength > 0 && (
            <Badge variant="neutral">{chainLength} provider{chainLength !== 1 ? 's' : ''}</Badge>
          )}
          {roleConfig.maxBudgetUsd !== undefined && (
            <Badge variant="warning">${roleConfig.maxBudgetUsd}</Badge>
          )}
        </div>
      }
    >
      <ProviderChainEditor
        chain={roleConfig.providerChain ?? []}
        onChange={(chain) => onChange({ ...roleConfig, providerChain: chain })}
      />

      <div className="mt-4 space-y-3">
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Max Budget (USD)</label>
          <input
            type="number"
            min={0}
            max={100}
            step={0.5}
            value={roleConfig.maxBudgetUsd ?? ''}
            onChange={(e) => {
              const val = e.target.value;
              const updated = { ...roleConfig };
              if (val === '') {
                delete updated.maxBudgetUsd;
              } else {
                updated.maxBudgetUsd = Number(val);
              }
              onChange(updated);
            }}
            placeholder="No limit"
            className="w-32 px-3 py-1.5 text-sm border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
          />
        </div>

        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Permission Mode</label>
          <select
            value={roleConfig.permissionMode ?? 'default'}
            onChange={(e) =>
              onChange({
                ...roleConfig,
                permissionMode: e.target.value as 'default' | 'bypassPermissions',
              })
            }
            className="w-48 px-3 py-1.5 text-sm border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
          >
            <option value="default">Default</option>
            <option value="bypassPermissions">Bypass Permissions</option>
          </select>
        </div>
      </div>
    </Card>
  );
}
