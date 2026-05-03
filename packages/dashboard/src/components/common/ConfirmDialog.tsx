import { useEffect, useRef, useCallback, type JSX } from 'react';

interface ConfirmDialogProps {
  open: boolean;
  title: string;
  message: string;
  confirmLabel?: string;
  cancelLabel?: string;
  onConfirm: () => void;
  onCancel: () => void;
  variant?: 'danger' | 'default';
}

export function ConfirmDialog({
  open,
  title,
  message,
  confirmLabel = 'Confirm',
  cancelLabel = 'Cancel',
  onConfirm,
  onCancel,
  variant = 'default',
}: ConfirmDialogProps): JSX.Element | null {
  const dialogRef = useRef<HTMLDivElement>(null);
  const previousFocusRef = useRef<Element | null>(null);

  // Capture previously focused element when dialog opens
  useEffect(() => {
    if (open) {
      previousFocusRef.current = document.activeElement;
      // Focus the dialog on open
      requestAnimationFrame(() => {
        dialogRef.current?.focus();
      });
    } else if (previousFocusRef.current instanceof HTMLElement) {
      // Return focus when closing
      previousFocusRef.current.focus();
      previousFocusRef.current = null;
    }
  }, [open]);

  // Handle Escape key
  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.stopPropagation();
        onCancel();
      }
    },
    [onCancel],
  );

  if (!open) return null;

  const confirmClasses =
    variant === 'danger'
      ? 'bg-red-600 hover:bg-red-700 text-white'
      : 'bg-blue-600 hover:bg-blue-700 text-white';

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center" role="presentation">
      <div className="fixed inset-0 bg-black/50" onClick={onCancel} aria-hidden="true" />
      <div
        ref={dialogRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby="confirm-dialog-title"
        tabIndex={-1}
        onKeyDown={handleKeyDown}
        className="relative bg-white rounded-lg shadow-xl p-6 max-w-md w-full mx-4 outline-none dark:bg-gray-800"
      >
        <h3 id="confirm-dialog-title" className="text-lg font-semibold text-gray-900 mb-2 dark:text-gray-100">
          {title}
        </h3>
        <p className="text-sm text-gray-600 mb-6 dark:text-gray-400">{message}</p>
        <div className="flex justify-end gap-3">
          <button
            type="button"
            onClick={onCancel}
            className="px-4 py-2 text-sm font-medium text-gray-700 bg-gray-100 hover:bg-gray-200 rounded-md dark:bg-gray-800 dark:text-gray-300"
          >
            {cancelLabel}
          </button>
          <button
            type="button"
            onClick={onConfirm}
            className={`px-4 py-2 text-sm font-medium rounded-md ${confirmClasses}`}
          >
            {confirmLabel}
          </button>
        </div>
      </div>
    </div>
  );
}
