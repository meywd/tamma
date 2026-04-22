/**
 * ActionDefaultsList (Story 27-4 AC 9, scoped to v1)
 *
 * Read-only listing of the 10 action default templates — the layer-4
 * safety nets used when no role+action and no role+action override
 * resolves. The backend exposes them via `GET /api/prompts/defaults/{action}`
 * (and includes them in the bulk `GET /api/prompts/system` payload), but
 * editing them is intentionally NOT exposed: action defaults are role-
 * agnostic safety nets shipped in code (`SystemPrompts.ActionDefaults`),
 * and customisation belongs at the role+action override layer where it
 * scopes to the user's role context.
 *
 * The card view here surfaces them so admins know what's running, with
 * a "Customise per role" CTA pointing back to the templates tab.
 */

import type { PromptResponse } from '../../services/admin/prompts-api-client.js';
import { ACTIONS, actionLabel } from './prompt-constants.js';

interface ActionDefaultsListProps {
  /** `{ [action]: ActionDefault }` — supplied by the parent page. */
  actionDefaults: Record<string, PromptResponse>;
  /** Switch to the Templates tab so the admin can customise per role. */
  onCustomise: () => void;
}

export function ActionDefaultsList({
  actionDefaults,
  onCustomise,
}: ActionDefaultsListProps): JSX.Element {
  return (
    <div>
      <p className="text-sm text-gray-600 mb-4">
        Layer-4 safety-net templates used when no role+action template (or
        override) is found for the calling user. These are role-agnostic and
        shipped in code; customise per-role via the Templates tab.
      </p>

      <div className="space-y-3">
        {ACTIONS.map(({ id }) => {
          const tmpl = actionDefaults[id];
          return (
            <div
              key={id}
              className="bg-white border border-gray-200 rounded-lg shadow-sm p-4"
            >
              <div className="flex items-center justify-between mb-3">
                <span className="inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium bg-cyan-100 text-cyan-800">
                  {actionLabel(id)}
                </span>
                {tmpl && (
                  <div className="flex items-center gap-2 text-xs text-gray-500 font-mono">
                    <span>{tmpl.maxTokens.toLocaleString()} tokens</span>
                    <span>·</span>
                    <span>{tmpl.variables?.length ?? 0} vars</span>
                    <span>·</span>
                    <span>tools {tmpl.enableTools ? 'on' : 'off'}</span>
                  </div>
                )}
              </div>

              {tmpl ? (
                <pre className="text-xs font-mono leading-relaxed whitespace-pre-wrap break-words text-gray-700 bg-gray-50 border border-gray-100 rounded-md px-3 py-2 max-h-48 overflow-y-auto">
                  {tmpl.template}
                </pre>
              ) : (
                <p className="text-xs italic text-gray-500">
                  No action default shipped for {actionLabel(id)}.
                </p>
              )}

              <div className="mt-3 text-right">
                <button
                  type="button"
                  onClick={onCustomise}
                  className="text-xs font-medium text-blue-600 hover:text-blue-800"
                >
                  Customise per role →
                </button>
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}
