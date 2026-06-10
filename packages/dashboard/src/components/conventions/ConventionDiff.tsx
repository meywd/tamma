/**
 * ConventionDiff — inline diff of override body vs system default body.
 *
 * Shows: green lines (added), red lines (removed), grey unchanged lines.
 * No external diff library is added — uses a simple line-by-line LCS diff
 * so the bundle stays small.
 *
 * Story 27-12 AC: "Compare with Default" toggle on tenant overrides.
 */

import { useMemo, type JSX } from 'react';

interface ConventionDiffProps {
  overrideBody: string;
  systemBody: string;
}

type DiffLine =
  | { type: 'unchanged'; text: string }
  | { type: 'added'; text: string }
  | { type: 'removed'; text: string };

/**
 * Compute a simple line diff using the Myers-inspired patience approach.
 * Good enough for convention bodies (prose / short rule lists).
 */
function diffLines(a: string, b: string): DiffLine[] {
  const aLines = a.split('\n');
  const bLines = b.split('\n');
  const result: DiffLine[] = [];

  // Build LCS table
  const m = aLines.length;
  const n = bLines.length;
  const dp: number[][] = Array.from({ length: m + 1 }, () => new Array(n + 1).fill(0));
  for (let i = m - 1; i >= 0; i--) {
    for (let j = n - 1; j >= 0; j--) {
      if (aLines[i] === bLines[j]) {
        dp[i]![j] = dp[i + 1]![j + 1]! + 1;
      } else {
        dp[i]![j] = Math.max(dp[i + 1]![j]!, dp[i]![j + 1]!);
      }
    }
  }

  let i = 0;
  let j = 0;
  while (i < m || j < n) {
    if (i < m && j < n && aLines[i] === bLines[j]) {
      result.push({ type: 'unchanged', text: aLines[i]! });
      i++;
      j++;
    } else if (j < n && (i >= m || dp[i]![j + 1]! >= dp[i + 1]![j]!)) {
      result.push({ type: 'added', text: bLines[j]! });
      j++;
    } else {
      result.push({ type: 'removed', text: aLines[i]! });
      i++;
    }
  }

  return result;
}

export function ConventionDiff({ overrideBody, systemBody }: ConventionDiffProps): JSX.Element {
  const lines = useMemo(() => diffLines(systemBody, overrideBody), [overrideBody, systemBody]);

  const addedCount = lines.filter((l) => l.type === 'added').length;
  const removedCount = lines.filter((l) => l.type === 'removed').length;

  return (
    <div>
      <div className="flex items-center gap-3 mb-2 text-xs">
        <span className="text-green-700 font-medium dark:text-green-400">+{addedCount} added</span>
        <span className="text-red-700 font-medium dark:text-red-400">−{removedCount} removed</span>
      </div>
      <div className="border border-gray-200 rounded-md overflow-auto max-h-80 dark:border-gray-700">
        <table className="w-full text-xs font-mono">
          <tbody>
            {lines.map((line, idx) => {
              const bg =
                line.type === 'added'
                  ? 'bg-green-50 dark:bg-green-950'
                  : line.type === 'removed'
                    ? 'bg-red-50 dark:bg-red-950'
                    : '';
              const text =
                line.type === 'added'
                  ? 'text-green-800 dark:text-green-300'
                  : line.type === 'removed'
                    ? 'text-red-800 dark:text-red-300'
                    : 'text-gray-700 dark:text-gray-400';
              const prefix =
                line.type === 'added' ? '+' : line.type === 'removed' ? '−' : ' ';
              return (
                <tr key={idx} className={bg}>
                  <td className={`px-2 py-0.5 select-none w-4 ${text} opacity-60`}>{prefix}</td>
                  <td className={`px-2 py-0.5 whitespace-pre-wrap break-words ${text}`}>
                    {line.text}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
        {lines.length === 0 && (
          <div className="px-4 py-6 text-center text-sm text-gray-500 dark:text-gray-400">
            No differences.
          </div>
        )}
      </div>
    </div>
  );
}
