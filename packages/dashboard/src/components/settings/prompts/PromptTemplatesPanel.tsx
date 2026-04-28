import { useState, type JSX } from 'react';
import { usePromptTemplates } from '../../../hooks/settings/usePromptTemplates.js';
import { Card } from '../../common/Card.js';
import { LoadingSpinner } from '../../common/LoadingSpinner.js';
import { PromptEditor } from './PromptEditor.js';

const ROLE_LABELS: Record<string, string> = {
  defaults: 'Defaults',
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

export function PromptTemplatesPanel(): JSX.Element {
  const { templates, loading, error, save } = usePromptTemplates();
  const [selectedRole, setSelectedRole] = useState<string | null>(null);

  if (loading && Object.keys(templates).length === 0) return <LoadingSpinner />;
  if (error) return <div className="text-red-600 text-sm">{error}</div>;

  const roles = Object.keys(templates);
  // Sort: defaults first, then alphabetical
  roles.sort((a, b) => {
    if (a === 'defaults') return -1;
    if (b === 'defaults') return 1;
    return a.localeCompare(b);
  });

  return (
    <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
      <div className="lg:col-span-1">
        <Card title="Roles">
          <ul className="space-y-1">
            {roles.map((role) => (
              <li key={role}>
                <button
                  onClick={() => setSelectedRole(role)}
                  className={`w-full text-left px-3 py-2 text-sm rounded-md transition-colors ${
                    selectedRole === role
                      ? 'bg-blue-100 text-blue-800 font-medium'
                      : 'text-gray-700 hover:bg-gray-100'
                  }`}
                >
                  {ROLE_LABELS[role] ?? role}
                  {templates[role]?.systemPrompt && (
                    <span className="ml-2 text-xs text-gray-400">custom</span>
                  )}
                </button>
              </li>
            ))}
          </ul>
        </Card>
      </div>

      <div className="lg:col-span-2">
        {selectedRole ? (
          <PromptEditor
            role={selectedRole}
            template={templates[selectedRole] ?? {}}
            onSave={save}
          />
        ) : (
          <Card>
            <div className="text-center py-12 text-gray-500">
              <p>Select a role to edit its prompt templates.</p>
            </div>
          </Card>
        )}
      </div>
    </div>
  );
}
