/**
 * TemplateEditor (Story 27-4 Task 4)
 *
 * Lightweight textarea with an aligned highlight overlay that paints
 * `{{variable}}` tokens in distinct colour as the user types. The
 * 16h plan explicitly defers CodeMirror integration ("monospaced textarea
 * is sufficient for v1" — see risks table); this overlay approach keeps
 * the bundle small while still satisfying the visual-distinctness AC.
 *
 * Implementation note: a real `<textarea>` owns input + selection + caret;
 * the overlay is a `<pre aria-hidden>` painted underneath via absolute
 * positioning. The two share the same font metrics + padding so the
 * highlights line up with the actual characters.
 */

import { forwardRef, useEffect, useRef } from 'react';

interface TemplateEditorProps {
  value: string;
  onChange: (value: string) => void;
  /** `id` is forwarded to the textarea so external <label> can reference it. */
  id?: string;
  /** Total visible rows for the underlying textarea. */
  rows?: number;
  placeholder?: string;
  /** Disable input — used while the dialog is loading the prompt. */
  disabled?: boolean;
}

const VARIABLE_PATTERN = /\{\{([^}]{1,64})\}\}/g;

/**
 * Escape HTML special chars so the overlay renders the user's literal
 * text rather than parsing it. Newlines are preserved by the `<pre>`.
 */
function escapeHtml(s: string): string {
  return s
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');
}

/**
 * Build the highlighted HTML for the overlay. Trailing newline is
 * preserved with a non-breaking space so the textarea's last empty line
 * still has visual height in the overlay.
 */
function renderHighlights(value: string): string {
  const escaped = escapeHtml(value);
  const withVars = escaped.replace(
    VARIABLE_PATTERN,
    (match) =>
      `<span class="bg-purple-100 text-purple-700 rounded-sm px-0.5">${match}</span>`,
  );
  return withVars + '\n';
}

export const TemplateEditor = forwardRef<HTMLTextAreaElement, TemplateEditorProps>(
  function TemplateEditor(
    { value, onChange, id, rows = 18, placeholder, disabled = false },
    ref,
  ) {
    const overlayRef = useRef<HTMLPreElement>(null);
    const localTextareaRef = useRef<HTMLTextAreaElement>(null);

    // Forward the textarea ref outward while still keeping a local handle
    // for the scroll-sync listener.
    useEffect(() => {
      if (typeof ref === 'function') {
        ref(localTextareaRef.current);
      } else if (ref) {
        ref.current = localTextareaRef.current;
      }
    });

    const handleScroll = () => {
      const ta = localTextareaRef.current;
      const pre = overlayRef.current;
      if (!ta || !pre) return;
      pre.scrollTop = ta.scrollTop;
      pre.scrollLeft = ta.scrollLeft;
    };

    return (
      <div className="relative">
        {/*
          The overlay is decorative — `aria-hidden` so screen readers see
          only the textarea below. Its dangerouslySetInnerHTML is safe:
          all user input is HTML-escaped by `renderHighlights`.
        */}
        <pre
          ref={overlayRef}
          aria-hidden="true"
          className="absolute inset-0 m-0 px-3 py-2 text-sm font-mono leading-6 whitespace-pre-wrap break-words pointer-events-none overflow-hidden text-transparent"
          dangerouslySetInnerHTML={{ __html: renderHighlights(value) }}
        />
        <textarea
          id={id}
          ref={localTextareaRef}
          value={value}
          onChange={(e) => onChange(e.target.value)}
          onScroll={handleScroll}
          rows={rows}
          placeholder={placeholder}
          disabled={disabled}
          spellCheck={false}
          className="relative w-full px-3 py-2 text-sm font-mono leading-6 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500 bg-transparent resize-y disabled:opacity-50 dark:border-gray-600"
        />
      </div>
    );
  },
);
