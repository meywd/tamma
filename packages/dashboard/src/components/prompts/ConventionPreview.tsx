/**
 * ConventionPreview (Story 27-4 AC 27-28)
 *
 * Browser for the 20 shipped convention starter templates. Hits two
 * endpoints:
 *   - `GET /api/convention-templates`        — list (key, name, description)
 *   - `GET /api/convention-templates/{key}`  — full template with conventions body
 *
 * The full body is fetched lazily on selection so the initial render
 * stays cheap (the list is metadata-only by backend design).
 */

import { useEffect, useState } from 'react';
import {
  conventionTemplatesApi,
  type ConventionTemplate,
  type ConventionTemplateSummary,
} from '../../services/admin/prompts-api-client.js';
import { LoadingSpinner } from '../common/LoadingSpinner.js';

export function ConventionPreview(): JSX.Element {
  const [list, setList] = useState<ConventionTemplateSummary[]>([]);
  const [listLoading, setListLoading] = useState(true);
  const [listError, setListError] = useState<string | null>(null);

  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  const [selected, setSelected] = useState<ConventionTemplate | null>(null);
  const [detailLoading, setDetailLoading] = useState(false);
  const [detailError, setDetailError] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  // Initial list load
  useEffect(() => {
    let cancelled = false;
    setListLoading(true);
    setListError(null);
    conventionTemplatesApi
      .list()
      .then((items) => {
        if (cancelled) return;
        setList(items);
        if (items.length > 0 && !selectedKey) {
          setSelectedKey(items[0]?.key ?? null);
        }
      })
      .catch((err: unknown) => {
        if (cancelled) return;
        setListError(
          err instanceof Error ? err.message : 'Failed to load conventions',
        );
      })
      .finally(() => {
        if (!cancelled) setListLoading(false);
      });
    return () => {
      cancelled = true;
    };
    // selectedKey intentionally excluded — we only want to seed the
    // initial selection once on mount.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Detail load on selection change
  useEffect(() => {
    if (!selectedKey) {
      setSelected(null);
      return;
    }
    let cancelled = false;
    setDetailLoading(true);
    setDetailError(null);
    setCopied(false);
    conventionTemplatesApi
      .get(selectedKey)
      .then((t) => {
        if (!cancelled) setSelected(t);
      })
      .catch((err: unknown) => {
        if (cancelled) return;
        setDetailError(
          err instanceof Error ? err.message : 'Failed to load template',
        );
      })
      .finally(() => {
        if (!cancelled) setDetailLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [selectedKey]);

  const handleCopy = async () => {
    if (!selected) return;
    try {
      await navigator.clipboard.writeText(selected.conventions);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1500);
    } catch {
      // Clipboard can fail (insecure context, permissions). Surface a
      // hint without breaking — copying is a nice-to-have.
      setDetailError('Clipboard write failed — copy manually from the box.');
    }
  };

  if (listLoading) {
    return (
      <div className="flex justify-center py-12">
        <LoadingSpinner size="lg" />
      </div>
    );
  }

  if (listError) {
    return (
      <div className="bg-red-50 border border-red-200 rounded-md p-4 text-sm text-red-700">
        {listError}
      </div>
    );
  }

  return (
    <div>
      <p className="text-sm text-gray-600 mb-4">
        Language and framework starter templates. Repo owners select one as
        the seed for their{' '}
        <code className="font-mono text-purple-700 bg-gray-50 px-1 rounded-sm">
          {'{{conventions}}'}
        </code>{' '}
        variable in <code className="font-mono">.tamma/config.json</code>.
      </p>

      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        {/* Left: cards */}
        <div className="lg:col-span-1 space-y-2 max-h-[60vh] overflow-y-auto pr-1">
          {list.map((c) => {
            const isActive = c.key === selectedKey;
            return (
              <button
                key={c.key}
                type="button"
                onClick={() => setSelectedKey(c.key)}
                className={`w-full text-left p-3 rounded-md border transition-colors ${
                  isActive
                    ? 'border-blue-500 bg-blue-50'
                    : 'border-gray-200 bg-white hover:border-gray-300'
                }`}
              >
                <div className="text-sm font-medium text-gray-900">
                  {c.name}
                </div>
                <div className="text-xs text-gray-500 mt-1 leading-snug">
                  {c.description}
                </div>
              </button>
            );
          })}
          {list.length === 0 && (
            <p className="text-sm text-gray-500 italic">
              No convention templates installed.
            </p>
          )}
        </div>

        {/* Right: detail */}
        <div className="lg:col-span-2">
          <div className="bg-white border border-gray-200 rounded-lg shadow-sm">
            <div className="flex items-center justify-between px-4 py-3 border-b border-gray-200">
              <div>
                <h3 className="text-sm font-semibold text-gray-900">
                  {selected?.name ?? 'Select a template'}
                </h3>
                {selected?.description && (
                  <p className="text-xs text-gray-500 mt-0.5">
                    {selected.description}
                  </p>
                )}
              </div>
              {selected && (
                <button
                  type="button"
                  onClick={() => void handleCopy()}
                  className="px-3 py-1 text-xs font-medium text-gray-700 border border-gray-300 bg-white rounded-md hover:bg-gray-50"
                >
                  {copied ? 'Copied!' : 'Copy'}
                </button>
              )}
            </div>
            <div className="p-4">
              {detailLoading ? (
                <div className="flex justify-center py-8">
                  <LoadingSpinner />
                </div>
              ) : detailError ? (
                <div className="bg-red-50 border border-red-200 rounded-md p-3 text-sm text-red-700">
                  {detailError}
                </div>
              ) : selected ? (
                <pre className="text-xs font-mono leading-relaxed whitespace-pre-wrap break-words text-gray-800 bg-gray-50 border border-gray-100 rounded-md px-3 py-2 max-h-[55vh] overflow-y-auto">
                  {selected.conventions}
                </pre>
              ) : (
                <p className="text-sm text-gray-500 italic">
                  Pick a template on the left to preview its conventions body.
                </p>
              )}
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
