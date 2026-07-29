/**
 * RulesEditDialog (Story 39-5)
 *
 * Edits one document type's acceptance rules (or the `base` dial row): the
 * autonomy 70–100 slider, the numeric bounds, the ambiguity threshold, the
 * always-escalate class list, the reviewer selection, the acceptor requirement,
 * and the decision/routing guidance text. Save → PUT; Reset → DELETE (falls back
 * to the next tier).
 *
 * Story 43-0: the `body` memo must carry EVERY field of `AcceptanceRules`. It
 * previously built eight of the nine, and because the interface was missing the
 * ninth (`acceptorRequirement`) the literal still type-checked — so every save
 * shipped a body the API defaulted, silently resetting `design`/`sprint-plan`/
 * `threat-model` from a human acceptor back to `any`. Do not reintroduce a
 * partial literal here: spread-or-list every field.
 */

import { useMemo, useState, type JSX } from 'react';
import type {
  AcceptanceRules,
  AcceptorRequirement,
  EscalationClass,
  EscalationClassKind,
  ResolvedAcceptanceRules,
  ReviewDecisionRule,
  ReviewerMode,
} from '../../services/admin/acceptance-rules-api-client.js';

const MIN_AUTONOMY = 70;
const MAX_AUTONOMY = 100;

export interface RulesEditDialogProps {
  resolved: ResolvedAcceptanceRules;
  onSave: (documentTypeKey: string, body: AcceptanceRules) => Promise<unknown>;
  onReset: (documentTypeKey: string) => Promise<void>;
  onClose: () => void;
}

export function RulesEditDialog({
  resolved,
  onSave,
  onReset,
  onClose,
}: RulesEditDialogProps): JSX.Element {
  const initial = resolved.rules;
  const [autonomyLevel, setAutonomyLevel] = useState(initial.autonomyLevel);
  const [maxRevisionRounds, setMaxRevisionRounds] = useState(initial.maxRevisionRounds);
  const [maxValidationRepairAttempts, setMaxValidationRepairAttempts] = useState(
    initial.maxValidationRepairAttempts,
  );
  const [ambiguityEscalationThreshold, setAmbiguityEscalationThreshold] = useState(
    initial.ambiguityEscalationThreshold,
  );
  const [alwaysEscalate, setAlwaysEscalate] = useState<EscalationClass[]>(
    initial.alwaysEscalate,
  );
  const [reviewerMode, setReviewerMode] = useState<ReviewerMode>(
    initial.reviewerSelection.mode,
  );
  const [reviewerRole, setReviewerRole] = useState<string>(
    initial.reviewerSelection.reviewerRole ?? '',
  );
  const [panelRolesText, setPanelRolesText] = useState<string>(
    initial.reviewerSelection.panelRoles.join(', '),
  );
  const [quorumText, setQuorumText] = useState<string>(
    initial.reviewerSelection.quorum != null ? String(initial.reviewerSelection.quorum) : '',
  );
  const [decisionRule, setDecisionRule] = useState<ReviewDecisionRule>(
    initial.reviewerSelection.decisionRule,
  );
  const [acceptorRequirement, setAcceptorRequirement] = useState<AcceptorRequirement>(
    initial.acceptorRequirement,
  );
  const [decisionGuidance, setDecisionGuidance] = useState(initial.decisionGuidance);
  const [routingGuidance, setRoutingGuidance] = useState(initial.routingGuidance);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const isBase = resolved.documentTypeKey === 'base';

  const body = useMemo<AcceptanceRules>(() => {
    const panelRoles = panelRolesText
      .split(',')
      .map((s) => s.trim())
      .filter((s) => s.length > 0);
    const quorum = quorumText.trim() === '' ? null : Number(quorumText.trim());
    return {
      autonomyLevel,
      maxRevisionRounds,
      maxValidationRepairAttempts,
      ambiguityEscalationThreshold,
      alwaysEscalate,
      reviewerSelection: {
        mode: reviewerMode,
        reviewerRole: reviewerMode === 'single-reviewer' ? reviewerRole || null : null,
        panelRoles: reviewerMode === 'panel' ? panelRoles : [],
        quorum,
        decisionRule,
      },
      acceptorRequirement,
      decisionGuidance,
      routingGuidance,
    };
  }, [
    autonomyLevel,
    maxRevisionRounds,
    maxValidationRepairAttempts,
    ambiguityEscalationThreshold,
    alwaysEscalate,
    reviewerMode,
    reviewerRole,
    panelRolesText,
    quorumText,
    decisionRule,
    acceptorRequirement,
    decisionGuidance,
    routingGuidance,
  ]);

  const handleSave = async () => {
    setSaving(true);
    setError(null);
    try {
      await onSave(resolved.documentTypeKey, body);
      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Save failed');
    } finally {
      setSaving(false);
    }
  };

  const handleReset = async () => {
    setSaving(true);
    setError(null);
    try {
      await onReset(resolved.documentTypeKey);
      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Reset failed');
    } finally {
      setSaving(false);
    }
  };

  const addEscalation = () =>
    setAlwaysEscalate((prev) => [...prev, { kind: 'document-type', key: '' }]);
  const removeEscalation = (idx: number) =>
    setAlwaysEscalate((prev) => prev.filter((_, i) => i !== idx));
  const updateEscalation = (idx: number, patch: Partial<EscalationClass>) =>
    setAlwaysEscalate((prev) => prev.map((c, i) => (i === idx ? { ...c, ...patch } : c)));

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4"
      role="dialog"
      aria-modal="true"
    >
      <div className="w-full max-w-2xl max-h-[90vh] overflow-y-auto bg-white rounded-lg shadow-xl p-6 dark:bg-gray-900">
        <div className="flex items-center justify-between mb-4">
          <h2 className="text-lg font-semibold text-gray-900 dark:text-gray-100">
            Acceptance rules — <span className="font-mono">{resolved.documentTypeKey}</span>
          </h2>
          <span className="text-xs text-gray-500 dark:text-gray-400">source: {resolved.source}</span>
        </div>

        {error && (
          <div className="mb-4 rounded-md bg-red-50 border border-red-200 p-3 text-sm text-red-700 dark:bg-red-950 dark:text-red-300 dark:border-red-800">
            {error}
          </div>
        )}

        {/* Autonomy dial */}
        <label className="block mb-4">
          <span className="text-sm font-medium text-gray-700 dark:text-gray-300">
            Autonomy level: {autonomyLevel}
          </span>
          <input
            type="range"
            aria-label="Autonomy level"
            min={MIN_AUTONOMY}
            max={MAX_AUTONOMY}
            value={autonomyLevel}
            onChange={(e) => setAutonomyLevel(Number(e.target.value))}
            className="w-full mt-1"
          />
          <span className="text-xs text-gray-500 dark:text-gray-400">
            70 = supervised baseline · 100 = full auto
          </span>
        </label>

        {/* Bounds + threshold */}
        <div className="grid grid-cols-3 gap-4 mb-4">
          <label className="block">
            <span className="text-sm font-medium text-gray-700 dark:text-gray-300">Max revision rounds</span>
            <input
              type="number"
              aria-label="Max revision rounds"
              min={1}
              max={10}
              value={maxRevisionRounds}
              onChange={(e) => setMaxRevisionRounds(Number(e.target.value))}
              className="w-full mt-1 rounded border border-gray-300 px-2 py-1 dark:bg-gray-800 dark:border-gray-600 dark:text-gray-100"
            />
          </label>
          <label className="block">
            <span className="text-sm font-medium text-gray-700 dark:text-gray-300">Max repair attempts</span>
            <input
              type="number"
              aria-label="Max repair attempts"
              min={0}
              max={10}
              value={maxValidationRepairAttempts}
              onChange={(e) => setMaxValidationRepairAttempts(Number(e.target.value))}
              className="w-full mt-1 rounded border border-gray-300 px-2 py-1 dark:bg-gray-800 dark:border-gray-600 dark:text-gray-100"
            />
          </label>
          <label className="block">
            <span className="text-sm font-medium text-gray-700 dark:text-gray-300">Ambiguity threshold</span>
            <input
              type="number"
              aria-label="Ambiguity threshold"
              min={0}
              max={1}
              step={0.05}
              value={ambiguityEscalationThreshold}
              onChange={(e) => setAmbiguityEscalationThreshold(Number(e.target.value))}
              className="w-full mt-1 rounded border border-gray-300 px-2 py-1 dark:bg-gray-800 dark:border-gray-600 dark:text-gray-100"
            />
          </label>
        </div>

        {/* Always-escalate classes */}
        <div className="mb-4">
          <div className="flex items-center justify-between mb-1">
            <span className="text-sm font-medium text-gray-700 dark:text-gray-300">Always-escalate classes</span>
            <button
              type="button"
              onClick={addEscalation}
              className="text-xs px-2 py-1 rounded border border-gray-300 hover:bg-gray-50 dark:border-gray-600 dark:hover:bg-gray-800 dark:text-gray-200"
            >
              + Add
            </button>
          </div>
          {alwaysEscalate.length === 0 && (
            <p className="text-xs text-gray-500 dark:text-gray-400">None — nothing always escalates.</p>
          )}
          {alwaysEscalate.map((cls, idx) => (
            <div key={idx} className="flex items-center gap-2 mb-1">
              <select
                aria-label={`Escalation kind ${idx}`}
                value={cls.kind}
                onChange={(e) => updateEscalation(idx, { kind: e.target.value as EscalationClassKind })}
                className="rounded border border-gray-300 px-2 py-1 text-sm dark:bg-gray-800 dark:border-gray-600 dark:text-gray-100"
              >
                <option value="document-type">document-type</option>
                <option value="agent-action">agent-action</option>
              </select>
              <input
                type="text"
                aria-label={`Escalation key ${idx}`}
                value={cls.key}
                placeholder="wire key (e.g. design)"
                onChange={(e) => updateEscalation(idx, { key: e.target.value })}
                className="flex-1 rounded border border-gray-300 px-2 py-1 text-sm font-mono dark:bg-gray-800 dark:border-gray-600 dark:text-gray-100"
              />
              <button
                type="button"
                onClick={() => removeEscalation(idx)}
                className="text-xs text-red-600 hover:underline dark:text-red-400"
              >
                Remove
              </button>
            </div>
          ))}
        </div>

        {/* Reviewer selection */}
        <div className="grid grid-cols-2 gap-4 mb-4">
          <label className="block">
            <span className="text-sm font-medium text-gray-700 dark:text-gray-300">Reviewer mode</span>
            <select
              aria-label="Reviewer mode"
              value={reviewerMode}
              onChange={(e) => setReviewerMode(e.target.value as ReviewerMode)}
              className="w-full mt-1 rounded border border-gray-300 px-2 py-1 dark:bg-gray-800 dark:border-gray-600 dark:text-gray-100"
            >
              <option value="single-reviewer">single-reviewer</option>
              <option value="panel">panel</option>
            </select>
          </label>
          <label className="block">
            <span className="text-sm font-medium text-gray-700 dark:text-gray-300">Decision rule</span>
            <select
              aria-label="Decision rule"
              value={decisionRule}
              onChange={(e) => setDecisionRule(e.target.value as ReviewDecisionRule)}
              className="w-full mt-1 rounded border border-gray-300 px-2 py-1 dark:bg-gray-800 dark:border-gray-600 dark:text-gray-100"
            >
              <option value="unanimous">unanimous</option>
              <option value="majority">majority</option>
            </select>
          </label>
          {reviewerMode === 'single-reviewer' ? (
            <label className="block col-span-2">
              <span className="text-sm font-medium text-gray-700 dark:text-gray-300">Reviewer role</span>
              <input
                type="text"
                aria-label="Reviewer role"
                value={reviewerRole}
                placeholder="architect"
                onChange={(e) => setReviewerRole(e.target.value)}
                className="w-full mt-1 rounded border border-gray-300 px-2 py-1 font-mono dark:bg-gray-800 dark:border-gray-600 dark:text-gray-100"
              />
            </label>
          ) : (
            <>
              <label className="block col-span-2">
                <span className="text-sm font-medium text-gray-700 dark:text-gray-300">Panel roles (comma-separated)</span>
                <input
                  type="text"
                  aria-label="Panel roles"
                  value={panelRolesText}
                  placeholder="architect, developer, tester"
                  onChange={(e) => setPanelRolesText(e.target.value)}
                  className="w-full mt-1 rounded border border-gray-300 px-2 py-1 font-mono dark:bg-gray-800 dark:border-gray-600 dark:text-gray-100"
                />
              </label>
              <label className="block">
                <span className="text-sm font-medium text-gray-700 dark:text-gray-300">Quorum (optional)</span>
                <input
                  type="number"
                  aria-label="Quorum"
                  min={1}
                  value={quorumText}
                  onChange={(e) => setQuorumText(e.target.value)}
                  className="w-full mt-1 rounded border border-gray-300 px-2 py-1 dark:bg-gray-800 dark:border-gray-600 dark:text-gray-100"
                />
              </label>
            </>
          )}
        </div>

        {/* Acceptor requirement — WHO may answer the acceptance decision.
            An acceptance-identity knob, not a dial knob, so it sits with the
            reviewer controls rather than beside the autonomy slider. */}
        <label className="block mb-4">
          <span className="text-sm font-medium text-gray-700 dark:text-gray-300">
            Acceptor requirement
          </span>
          <select
            aria-label="Acceptor requirement"
            value={acceptorRequirement}
            onChange={(e) => setAcceptorRequirement(e.target.value as AcceptorRequirement)}
            className="w-full mt-1 rounded border border-gray-300 px-2 py-1 dark:bg-gray-800 dark:border-gray-600 dark:text-gray-100"
          >
            <option value="any">any</option>
            <option value="human">human</option>
          </select>
          <span className="text-xs text-gray-500 dark:text-gray-400">
            human — a person must accept this document type regardless of the autonomy
            level · any — the autonomy dial decides
          </span>
        </label>

        {/* Guidance */}
        <label className="block mb-4">
          <span className="text-sm font-medium text-gray-700 dark:text-gray-300">Decision guidance</span>
          <textarea
            aria-label="Decision guidance"
            rows={3}
            value={decisionGuidance}
            onChange={(e) => setDecisionGuidance(e.target.value)}
            className="w-full mt-1 rounded border border-gray-300 px-2 py-1 text-sm dark:bg-gray-800 dark:border-gray-600 dark:text-gray-100"
          />
        </label>
        <label className="block mb-6">
          <span className="text-sm font-medium text-gray-700 dark:text-gray-300">Routing guidance</span>
          <textarea
            aria-label="Routing guidance"
            rows={3}
            value={routingGuidance}
            onChange={(e) => setRoutingGuidance(e.target.value)}
            className="w-full mt-1 rounded border border-gray-300 px-2 py-1 text-sm dark:bg-gray-800 dark:border-gray-600 dark:text-gray-100"
          />
        </label>

        <div className="flex items-center justify-between">
          <button
            type="button"
            onClick={handleReset}
            disabled={saving || resolved.source === 'system-default'}
            className="px-3 py-1.5 text-sm font-medium text-red-700 border border-red-300 rounded-md hover:bg-red-50 disabled:opacity-40 dark:text-red-300 dark:border-red-700 dark:hover:bg-red-950"
          >
            Reset to default
          </button>
          <div className="flex gap-2">
            <button
              type="button"
              onClick={onClose}
              disabled={saving}
              className="px-3 py-1.5 text-sm font-medium text-gray-700 border border-gray-300 rounded-md hover:bg-gray-50 dark:text-gray-200 dark:border-gray-600 dark:hover:bg-gray-800"
            >
              Cancel
            </button>
            <button
              type="button"
              onClick={handleSave}
              disabled={saving}
              className="px-3 py-1.5 text-sm font-medium text-white bg-blue-600 rounded-md hover:bg-blue-700 disabled:opacity-40"
            >
              {saving ? 'Saving…' : isBase ? 'Save base override' : 'Save override'}
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
