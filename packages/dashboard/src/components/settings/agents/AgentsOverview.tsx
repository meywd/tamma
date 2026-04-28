import { useState, useEffect, useCallback, type JSX } from 'react';
import type { IAgentsConfig, IAgentRoleConfig, AgentType } from '@tamma/shared';
import { useAgentsConfig } from '../../../hooks/settings/useAgentsConfig.js';
import { LoadingSpinner } from '../../common/LoadingSpinner.js';
import { AgentRoleCard } from './AgentRoleCard.js';

const ALL_ROLES: AgentType[] = [
  'scrum_master',
  'architect',
  'researcher',
  'analyst',
  'planner',
  'implementer',
  'reviewer',
  'tester',
  'documenter',
];

export function AgentsOverview(): JSX.Element | null {
  const { config, loading, error, save } = useAgentsConfig();
  const [draft, setDraft] = useState<IAgentsConfig | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);

  useEffect(() => {
    if (config && !draft) {
      setDraft(structuredClone(config));
    }
  }, [config, draft]);

  const handleRoleChange = useCallback(
    (role: AgentType, roleConfig: Partial<IAgentRoleConfig>) => {
      if (!draft) return;
      const newDraft = structuredClone(draft);
      if (!newDraft.roles) {
        newDraft.roles = {};
      }
      newDraft.roles[role] = roleConfig;
      setDraft(newDraft);
    },
    [draft],
  );

  const handleDefaultsChange = useCallback(
    (defaultsConfig: IAgentRoleConfig) => {
      if (!draft) return;
      setDraft({ ...draft, defaults: defaultsConfig });
    },
    [draft],
  );

  const handleSave = useCallback(async () => {
    if (!draft) return;
    setSaving(true);
    setSaveError(null);
    try {
      await save(draft);
    } catch (err) {
      setSaveError(err instanceof Error ? err.message : 'Failed to save');
    } finally {
      setSaving(false);
    }
  }, [draft, save]);

  if (loading && !draft) {
    return <LoadingSpinner />;
  }

  if (error && !draft) {
    return <div className="text-red-600 text-sm">{error}</div>;
  }

  if (!draft) return null;

  const hasChanges = JSON.stringify(draft) !== JSON.stringify(config);

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <p className="text-sm text-gray-500">
          Configure provider chains and settings per agent role.
        </p>
        <button
          onClick={handleSave}
          disabled={!hasChanges || saving}
          className="px-4 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md disabled:opacity-50 disabled:cursor-not-allowed"
        >
          {saving ? 'Saving...' : 'Save Changes'}
        </button>
      </div>

      {saveError && <div className="mb-4 text-sm text-red-600">{saveError}</div>}

      {/* Defaults Card */}
      <div className="mb-6">
        <AgentRoleCard
          role="defaults"
          roleConfig={draft.defaults}
          isDefaults
          onChange={(rc) => handleDefaultsChange(rc as IAgentRoleConfig)}
        />
      </div>

      {/* Role Cards */}
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
        {ALL_ROLES.map((role) => (
          <AgentRoleCard
            key={role}
            role={role}
            roleConfig={draft.roles?.[role] ?? {}}
            onChange={(rc) => handleRoleChange(role, rc)}
          />
        ))}
      </div>
    </div>
  );
}
