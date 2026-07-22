/**
 * RulesTable (Story 39-5)
 *
 * Lists the resolved acceptance rules for every document type with a
 * default-vs-override provenance badge and the effective autonomy level. Row
 * click opens the edit dialog.
 */

import type { JSX } from 'react';
import type {
  AcceptanceRulesSource,
  ResolvedAcceptanceRules,
} from '../../services/admin/acceptance-rules-api-client.js';

const SOURCE_LABEL: Record<AcceptanceRulesSource, string> = {
  'system-default': 'Default',
  'principal-default': 'Base override',
  'type-override': 'Type override',
};

const SOURCE_CLASS: Record<AcceptanceRulesSource, string> = {
  'system-default':
    'bg-gray-100 text-gray-700 dark:bg-gray-800 dark:text-gray-300',
  'principal-default':
    'bg-blue-100 text-blue-800 dark:bg-blue-950 dark:text-blue-300',
  'type-override':
    'bg-green-100 text-green-800 dark:bg-green-950 dark:text-green-300',
};

export interface RulesTableProps {
  rows: ResolvedAcceptanceRules[];
  onRowClick: (documentTypeKey: string) => void;
}

export function RulesTable({ rows, onRowClick }: RulesTableProps): JSX.Element {
  return (
    <div className="overflow-x-auto border border-gray-200 rounded-md dark:border-gray-700">
      <table className="min-w-full text-sm">
        <thead className="bg-gray-50 dark:bg-gray-800">
          <tr>
            <th className="px-4 py-2 text-left font-medium text-gray-600 dark:text-gray-300">Document type</th>
            <th className="px-4 py-2 text-left font-medium text-gray-600 dark:text-gray-300">Autonomy</th>
            <th className="px-4 py-2 text-left font-medium text-gray-600 dark:text-gray-300">Rounds</th>
            <th className="px-4 py-2 text-left font-medium text-gray-600 dark:text-gray-300">Reviewer</th>
            <th className="px-4 py-2 text-left font-medium text-gray-600 dark:text-gray-300">Source</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <tr
              key={row.documentTypeKey}
              data-testid={`rules-row-${row.documentTypeKey}`}
              onClick={() => onRowClick(row.documentTypeKey)}
              className="border-t border-gray-100 cursor-pointer hover:bg-gray-50 dark:border-gray-800 dark:hover:bg-gray-800"
            >
              <td className="px-4 py-2 font-mono text-gray-900 dark:text-gray-100">{row.documentTypeKey}</td>
              <td className="px-4 py-2 text-gray-700 dark:text-gray-300">{row.rules.autonomyLevel}</td>
              <td className="px-4 py-2 text-gray-700 dark:text-gray-300">{row.rules.maxRevisionRounds}</td>
              <td className="px-4 py-2 text-gray-700 dark:text-gray-300">
                {row.rules.reviewerSelection.mode === 'panel'
                  ? `panel (${row.rules.reviewerSelection.panelRoles.length})`
                  : (row.rules.reviewerSelection.reviewerRole ?? '—')}
              </td>
              <td className="px-4 py-2">
                <span
                  data-testid={`rules-source-${row.documentTypeKey}`}
                  className={`inline-block px-2 py-0.5 rounded text-xs font-medium ${SOURCE_CLASS[row.source]}`}
                >
                  {SOURCE_LABEL[row.source]}
                </span>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
