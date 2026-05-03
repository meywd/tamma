/**
 * ConventionSelector — dropdown over the 20 shipped convention templates
 * (Story 27-5 AC #8). Selecting a template loads its body, and clicking
 * "Insert into Template" hands the text back to the caller so the editor
 * can splice it in at the `{{conventions}}` placeholder.
 */

import { useCallback, useEffect, useState, type JSX } from 'react';
import {
  conventionTemplatesApi,
  type ConventionTemplate,
  type ConventionTemplateSummary,
} from '../../services/admin/prompts-api-client.js';

interface ConventionSelectorProps {
  onInsert: (text: string) => void;
}

export function ConventionSelector({ onInsert }: ConventionSelectorProps): JSX.Element {
  const [conventions, setConventions] = useState<ConventionTemplateSummary[]>([]);
  const [selected, setSelected] = useState<string>('');
  const [detail, setDetail] = useState<ConventionTemplate | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    let cancelled = false;
    conventionTemplatesApi.list()
      .then((list) => {
        if (!cancelled) setConventions(list);
      })
      .catch((err: unknown) => {
        if (!cancelled) {
          setError(err instanceof Error ? err.message : 'Failed to load conventions');
        }
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const handleSelect = useCallback(async (key: string) => {
    setSelected(key);
    setDetail(null);
    if (!key) return;
    setLoading(true);
    setError(null);
    try {
      setDetail(await conventionTemplatesApi.get(key));
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load convention');
    } finally {
      setLoading(false);
    }
  }, []);

  const handleInsert = useCallback(() => {
    if (detail) onInsert(detail.conventions);
  }, [detail, onInsert]);

  return (
    <div className="border border-gray-200 rounded-lg p-3 bg-gray-50">
      <label
        htmlFor="convention-template-select"
        className="block text-xs font-medium text-gray-600 mb-1"
      >
        Convention Template
      </label>
      <select
        id="convention-template-select"
        aria-label="Convention Template"
        value={selected}
        onChange={(e) => void handleSelect(e.target.value)}
        className="w-full px-2 py-1 text-sm border border-gray-300 rounded-md bg-white"
      >
        <option value="">Choose a convention template…</option>
        {conventions.map((c) => (
          <option key={c.key} value={c.key}>
            {c.name} — {c.description}
          </option>
        ))}
      </select>

      {error && <div className="mt-2 text-xs text-red-600">{error}</div>}

      {loading && <div className="mt-2 text-xs text-gray-500">Loading…</div>}

      {detail && (
        <div className="mt-3 space-y-2">
          <details className="text-xs">
            <summary className="cursor-pointer text-gray-600">Preview</summary>
            <pre className="mt-1 bg-white p-2 border border-gray-200 rounded max-h-40 overflow-auto whitespace-pre-wrap">
              {detail.conventions}
            </pre>
          </details>
          <button
            type="button"
            onClick={handleInsert}
            className="px-3 py-1 text-xs font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md"
          >
            Insert into Template
          </button>
        </div>
      )}
    </div>
  );
}
