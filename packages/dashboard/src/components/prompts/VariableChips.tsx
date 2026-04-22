/**
 * VariableChips
 *
 * Renders the auto-extracted `{{variable}}` list as clickable chips.
 * Clicking a chip inserts `{{name}}` at the textarea's current caret —
 * the parent passes a ref so we can find the active selection range.
 */

import type { RefObject } from 'react';

interface VariableChipsProps {
  variables: string[];
  /** Ref to the underlying textarea — used for caret-aware insertion. */
  editorRef: RefObject<HTMLTextAreaElement | null>;
  /** Called with the new template text after insertion. */
  onInsert: (newValue: string) => void;
  emptyHint?: string;
}

export function VariableChips({
  variables,
  editorRef,
  onInsert,
  emptyHint = 'No {{variables}} detected yet.',
}: VariableChipsProps): JSX.Element {
  if (variables.length === 0) {
    return <p className="text-xs text-gray-500 italic">{emptyHint}</p>;
  }

  const handleInsert = (name: string) => {
    const ta = editorRef.current;
    if (!ta) return;
    const token = `{{${name}}}`;
    const start = ta.selectionStart ?? ta.value.length;
    const end = ta.selectionEnd ?? ta.value.length;
    const next = ta.value.slice(0, start) + token + ta.value.slice(end);
    onInsert(next);
    // Restore caret to just after the inserted token in the next tick
    // (after React re-renders with the new value).
    requestAnimationFrame(() => {
      ta.focus();
      const caret = start + token.length;
      ta.setSelectionRange(caret, caret);
    });
  };

  return (
    <div className="flex flex-wrap gap-1.5">
      {variables.map((name) => (
        <button
          key={name}
          type="button"
          onClick={() => handleInsert(name)}
          className="text-xs font-mono px-2 py-0.5 rounded-md bg-purple-50 text-purple-700 border border-purple-200 hover:bg-purple-100 transition-colors"
          title={`Insert {{${name}}} at cursor`}
        >
          {`{{${name}}}`}
        </button>
      ))}
    </div>
  );
}
