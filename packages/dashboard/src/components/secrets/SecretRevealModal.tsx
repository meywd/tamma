import { useCallback, useEffect, useRef, useState } from 'react';

/**
 * Story 29-3 reveal-once copy-to-clipboard modal. Renders the
 * plaintext value for a freshly-minted secret exactly once — matches
 * the GitHub "you'll only see this value once" pattern.
 *
 * Expected caller flow:
 *   1. User clicks "Create" / "Rotate" in the 29-4 / 29-5 admin UI.
 *   2. Parent posts to the create / rotate endpoint; server returns
 *      `{ revealToken, revealUrl }` (no plaintext in the create
 *      response).
 *   3. Parent GETs `revealUrl` once, extracts the `plaintext`
 *      field, and mounts this component with it.
 *   4. The modal reveals the plaintext, shows a "Copy to clipboard"
 *      button, and requires the user to tick the "I have saved this
 *      value" checkbox before the "Close" button enables.
 *   5. On close / Escape / overlay click, the component clears the
 *      plaintext from its local state so a re-render does not leak
 *      it, and invokes `onClose()`.
 *
 * This component deliberately accepts the plaintext string directly
 * rather than fetching it itself — the parent owns the one-shot HTTP
 * call (so the reveal endpoint is burned exactly once, even if this
 * component re-mounts). The parent must not re-invoke the GET.
 */
export interface SecretRevealModalProps {
  /** Whether the modal is open. */
  readonly open: boolean;
  /** Human-readable name of the secret (e.g. "db/app-role"). */
  readonly name: string;
  /** Version number this reveal applies to. */
  readonly version: number;
  /**
   * The raw plaintext to display. Exactly one instance of this value
   * should ever enter the component — after the user confirms, the
   * component zeroes its local state.
   */
  readonly plaintext: string;
  /** ISO timestamp when the reveal token itself expires. */
  readonly expiresAt?: string;
  /**
   * Called after the user confirms "I have saved this value" and
   * dismisses. Parent should drop the plaintext from its own state at
   * this point too.
   */
  readonly onClose: () => void;
}

export function SecretRevealModal({
  open,
  name,
  version,
  plaintext,
  expiresAt,
  onClose,
}: SecretRevealModalProps): JSX.Element | null {
  const dialogRef = useRef<HTMLDivElement>(null);
  const previousFocusRef = useRef<Element | null>(null);
  const [acknowledged, setAcknowledged] = useState(false);
  const [copiedAt, setCopiedAt] = useState<number | null>(null);
  const [copyFailed, setCopyFailed] = useState(false);

  useEffect(() => {
    if (open) {
      previousFocusRef.current = document.activeElement;
      requestAnimationFrame(() => {
        dialogRef.current?.focus();
      });
    } else if (previousFocusRef.current instanceof HTMLElement) {
      previousFocusRef.current.focus();
      previousFocusRef.current = null;
    }
  }, [open]);

  // Reset the acknowledgement + copy state whenever a fresh plaintext
  // lands. This prevents a stale "saved" flag carrying over if the
  // parent rotates the secret and re-opens the modal with a new value.
  useEffect(() => {
    setAcknowledged(false);
    setCopiedAt(null);
    setCopyFailed(false);
  }, [plaintext]);

  const handleCopy = useCallback(async () => {
    try {
      if (navigator.clipboard && typeof navigator.clipboard.writeText === 'function') {
        await navigator.clipboard.writeText(plaintext);
      } else {
        throw new Error('Clipboard API not available');
      }
      setCopiedAt(Date.now());
      setCopyFailed(false);
    } catch {
      setCopyFailed(true);
    }
  }, [plaintext]);

  const handleClose = useCallback(() => {
    if (!acknowledged) return;
    // Local state clear — the parent is responsible for dropping its
    // own copy of the plaintext; we just make sure nothing inside this
    // component survives the dismiss.
    setCopiedAt(null);
    setCopyFailed(false);
    setAcknowledged(false);
    onClose();
  }, [acknowledged, onClose]);

  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent) => {
      if (e.key === 'Escape' && acknowledged) {
        e.stopPropagation();
        handleClose();
      }
    },
    [acknowledged, handleClose],
  );

  if (!open) return null;

  const expiryLine = expiresAt
    ? `Reveal token expires at ${new Date(expiresAt).toLocaleTimeString()}.`
    : null;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center" role="presentation">
      <div
        className="fixed inset-0 bg-black/50"
        // Overlay click only dismisses AFTER acknowledgement — a
        // stray click shouldn't lose the plaintext before the user
        // confirms they saved it.
        onClick={acknowledged ? handleClose : undefined}
        aria-hidden="true"
      />
      <div
        ref={dialogRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby="secret-reveal-title"
        aria-describedby="secret-reveal-warning"
        tabIndex={-1}
        onKeyDown={handleKeyDown}
        className="relative bg-white rounded-lg shadow-xl p-6 max-w-lg w-full mx-4 outline-none"
      >
        <h3
          id="secret-reveal-title"
          className="text-lg font-semibold text-gray-900 mb-1"
        >
          Secret created: {name}
        </h3>
        <p
          id="secret-reveal-warning"
          className="text-sm text-red-700 font-medium mb-3"
        >
          This value will not be shown again. Copy it now.
        </p>
        {expiryLine ? (
          <p className="text-xs text-gray-500 mb-3">{expiryLine}</p>
        ) : null}

        <div className="mb-4">
          <label className="block text-xs font-medium text-gray-500 uppercase tracking-wide mb-1">
            Version {version} plaintext
          </label>
          <div className="flex gap-2">
            <input
              type="text"
              readOnly
              value={plaintext}
              className="flex-1 font-mono text-sm px-3 py-2 bg-gray-50 border border-gray-300 rounded-md"
              aria-label="Secret plaintext"
              onFocus={(e) => e.currentTarget.select()}
            />
            <button
              type="button"
              onClick={handleCopy}
              className="px-3 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md"
            >
              {copiedAt ? 'Copied!' : 'Copy'}
            </button>
          </div>
          {copyFailed ? (
            <p className="text-xs text-red-600 mt-1">
              Copy failed — select the value manually and copy it.
            </p>
          ) : null}
        </div>

        <label className="flex items-start gap-2 text-sm text-gray-700 mb-5">
          <input
            type="checkbox"
            checked={acknowledged}
            onChange={(e) => setAcknowledged(e.target.checked)}
            className="mt-0.5"
            aria-describedby="secret-reveal-ack-help"
          />
          <span id="secret-reveal-ack-help">
            I have saved this value. I understand it cannot be retrieved again
            — only rotated.
          </span>
        </label>

        <div className="flex justify-end">
          <button
            type="button"
            onClick={handleClose}
            disabled={!acknowledged}
            className={
              acknowledged
                ? 'px-4 py-2 text-sm font-medium text-white bg-green-600 hover:bg-green-700 rounded-md'
                : 'px-4 py-2 text-sm font-medium text-gray-400 bg-gray-100 rounded-md cursor-not-allowed'
            }
          >
            Close
          </button>
        </div>
      </div>
    </div>
  );
}
