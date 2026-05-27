/**
 * MarkdownEditor — edit/preview/split-pane toggle for convention body text.
 *
 * v1 uses a plain textarea for the edit surface (consistent with TemplateEditor)
 * and a minimal `<pre>` preview. No external markdown library is added to keep
 * the bundle small — the body is plain prose/markdown, not rendered for end-users.
 *
 * Story 27-11 AC: body editor for convention text.
 */

import { useState, type JSX } from 'react';

type EditorMode = 'edit' | 'preview' | 'split';

interface MarkdownEditorProps {
  value: string;
  onChange: (value: string) => void;
  rows?: number;
  disabled?: boolean;
  placeholder?: string;
}

export function MarkdownEditor({
  value,
  onChange,
  rows = 14,
  disabled = false,
  placeholder = 'Enter convention body…',
}: MarkdownEditorProps): JSX.Element {
  const [mode, setMode] = useState<EditorMode>('edit');

  const modeBtn = (m: EditorMode, label: string) => (
    <button
      type="button"
      key={m}
      onClick={() => setMode(m)}
      className={`px-3 py-1 text-xs font-medium rounded-md transition-colors ${
        mode === m
          ? 'bg-blue-100 text-blue-700 dark:bg-blue-900 dark:text-blue-200'
          : 'text-gray-500 hover:text-gray-700 dark:text-gray-400 dark:hover:text-gray-200'
      }`}
    >
      {label}
    </button>
  );

  return (
    <div>
      <div className="flex items-center gap-1 mb-2">
        {modeBtn('edit', 'Edit')}
        {modeBtn('preview', 'Preview')}
        {modeBtn('split', 'Split')}
      </div>

      <div className={`flex gap-3 ${mode === 'split' ? 'flex-row' : 'flex-col'}`}>
        {(mode === 'edit' || mode === 'split') && (
          <textarea
            value={value}
            onChange={(e) => onChange(e.target.value)}
            rows={rows}
            disabled={disabled}
            placeholder={placeholder}
            spellCheck={false}
            className={`w-full px-3 py-2 text-sm font-mono leading-6 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:opacity-50 resize-y dark:border-gray-600 dark:bg-gray-800 ${mode === 'split' ? 'flex-1' : ''}`}
          />
        )}

        {(mode === 'preview' || mode === 'split') && (
          <div
            className={`${mode === 'split' ? 'flex-1' : 'w-full'} px-3 py-2 text-sm leading-6 border border-gray-200 rounded-md bg-gray-50 overflow-auto dark:border-gray-700 dark:bg-gray-900`}
            style={{ minHeight: `${rows * 1.5}rem` }}
          >
            {value.trim() ? (
              <pre className="whitespace-pre-wrap break-words font-mono text-sm text-gray-800 dark:text-gray-200">
                {value}
              </pre>
            ) : (
              <span className="text-xs italic text-gray-400 dark:text-gray-600">
                Nothing to preview yet.
              </span>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
