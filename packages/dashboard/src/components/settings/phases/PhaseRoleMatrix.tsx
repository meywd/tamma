import { useState, useEffect, useCallback, type JSX } from 'react';
import type { WorkflowPhase, AgentType, IAgentsConfig } from '@tamma/shared';
import { DEFAULT_PHASE_ROLE_MAP } from '@tamma/shared';
import { useAgentsConfig } from '../../../hooks/settings/useAgentsConfig.js';
import { Card } from '../../common/Card.js';
import { LoadingSpinner } from '../../common/LoadingSpinner.js';

const PHASES: WorkflowPhase[] = [
  'ISSUE_SELECTION',
  'CONTEXT_ANALYSIS',
  'PLAN_GENERATION',
  'CODE_GENERATION',
  'PR_CREATION',
  'CODE_REVIEW',
  'TEST_EXECUTION',
  'STATUS_MONITORING',
];

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

const PHASE_LABELS: Record<WorkflowPhase, string> = {
  ISSUE_SELECTION: 'Issue Selection',
  CONTEXT_ANALYSIS: 'Context Analysis',
  PLAN_GENERATION: 'Plan Generation',
  CODE_GENERATION: 'Code Generation',
  PR_CREATION: 'PR Creation',
  CODE_REVIEW: 'Code Review',
  TEST_EXECUTION: 'Test Execution',
  STATUS_MONITORING: 'Status Monitoring',
};

const ROLE_LABELS: Record<AgentType, string> = {
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

export function PhaseRoleMatrix(): JSX.Element | null {
  const { config, loading, error, save } = useAgentsConfig();
  const [draft, setDraft] = useState<IAgentsConfig | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);

  useEffect(() => {
    if (config && !draft) {
      setDraft(structuredClone(config));
    }
  }, [config, draft]);

  const getRole = (phase: WorkflowPhase): AgentType => {
    return draft?.phaseRoleMap?.[phase] ?? DEFAULT_PHASE_ROLE_MAP[phase];
  };

  const handleChange = (phase: WorkflowPhase, role: AgentType) => {
    if (!draft) return;
    const newDraft = structuredClone(draft);
    if (!newDraft.phaseRoleMap) {
      newDraft.phaseRoleMap = {};
    }
    newDraft.phaseRoleMap[phase] = role;
    setDraft(newDraft);
  };

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

  if (loading && !draft) return <LoadingSpinner />;
  if (error && !draft) return <div className="text-red-600 text-sm">{error}</div>;
  if (!draft) return null;

  const hasChanges = JSON.stringify(draft) !== JSON.stringify(config);

  return (
    <Card
      title="Workflow Phase to Agent Role Mapping"
      actions={
        <button
          onClick={handleSave}
          disabled={!hasChanges || saving}
          className="px-4 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md disabled:opacity-50 disabled:cursor-not-allowed"
        >
          {saving ? 'Saving...' : 'Save Changes'}
        </button>
      }
    >
      {saveError && <div className="mb-4 text-sm text-red-600">{saveError}</div>}
      <div className="overflow-x-auto">
        <table className="w-full text-sm">
          <thead>
            <tr className="border-b border-gray-200">
              <th className="text-left py-3 pr-4 font-semibold text-gray-900">Phase</th>
              <th className="text-left py-3 pr-4 font-semibold text-gray-900">Assigned Role</th>
              <th className="text-left py-3 font-semibold text-gray-500">Default</th>
            </tr>
          </thead>
          <tbody>
            {PHASES.map((phase) => {
              const currentRole = getRole(phase);
              const defaultRole = DEFAULT_PHASE_ROLE_MAP[phase];
              const isOverridden = draft.phaseRoleMap?.[phase] !== undefined && draft.phaseRoleMap[phase] !== defaultRole;

              return (
                <tr key={phase} className="border-b border-gray-100">
                  <td className="py-3 pr-4">
                    <span className="font-medium text-gray-900">{PHASE_LABELS[phase]}</span>
                  </td>
                  <td className="py-3 pr-4">
                    <select
                      value={currentRole}
                      onChange={(e) => handleChange(phase, e.target.value as AgentType)}
                      className={`px-2 py-1 text-sm border rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500 ${
                        isOverridden ? 'border-blue-300 bg-blue-50' : 'border-gray-300'
                      }`}
                    >
                      {ALL_ROLES.map((role) => (
                        <option key={role} value={role}>
                          {ROLE_LABELS[role]}
                        </option>
                      ))}
                    </select>
                  </td>
                  <td className="py-3 text-gray-500">{ROLE_LABELS[defaultRole]}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </Card>
  );
}
