import { useEffect, useState } from 'react';

export interface LinkDetailPopupProps {
  /** Source step of the transition. */
  source: { id: string; name: string };
  /** Target step of the transition. */
  target: { id: string; name: string };
  /** The trigger / branch condition, when the edge is conditional. */
  trigger?: string | undefined;
  /** Rail colour, for the header dot (matches the line on the map). */
  color: string;
  /** Dismiss the panel (X, backdrop, or Esc). */
  onClose: () => void;
  /** Open a step's own detail panel (replaces this link panel). */
  onGoToStep: (stepId: string) => void;
}

/**
 * Floating detail panel for a TRANSITION (a rail/link between two steps) — the
 * edge counterpart to {@link StationDetailPopup}. It names the source step and
 * the target step (each tappable to open that step's own details) and the
 * trigger condition. Clicking a rail opens THIS panel; it does not jump
 * straight to the target step.
 *
 * Mirrors the station popup chrome (responsive full-screen / modal, Esc +
 * backdrop dismiss, body-scroll lock) so the two feel like one system.
 */
export function LinkDetailPopup({
  source,
  target,
  trigger,
  color,
  onClose,
  onGoToStep,
}: LinkDetailPopupProps) {
  const [variant, setVariant] = useState<'fullscreen' | 'modal'>('modal');

  useEffect(() => {
    if (typeof window === 'undefined' || !window.matchMedia) return;
    const mq = window.matchMedia('(max-width: 640px)');
    const apply = () => setVariant(mq.matches ? 'fullscreen' : 'modal');
    apply();
    mq.addEventListener('change', apply);
    return () => mq.removeEventListener('change', apply);
  }, []);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    document.addEventListener('keydown', onKey);
    const prev = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    return () => {
      document.removeEventListener('keydown', onKey);
      document.body.style.overflow = prev;
    };
  }, [onClose]);

  return (
    <div
      className="twv-popup-backdrop"
      data-variant={variant}
      onClick={(e) => {
        if (e.target === e.currentTarget) onClose();
      }}
    >
      <div
        className="twv-popup"
        data-variant={variant}
        role="dialog"
        aria-modal="true"
        aria-label={`Transition from ${source.name} to ${target.name}`}
      >
        <div className="twv-popup-header">
          <div className="twv-popup-title">
            <span
              className="twv-panel-kind-dot"
              style={{ background: color }}
              aria-hidden="true"
            />
            <div className="twv-popup-title-text">
              <div className="twv-panel-name">Transition</div>
              <code className="twv-panel-class">
                {trigger ? `when: ${trigger}` : 'unconditional'}
              </code>
            </div>
          </div>
          <button
            type="button"
            className="twv-popup-close"
            onClick={onClose}
            aria-label="Close transition details"
          >
            <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" strokeWidth={2} aria-hidden="true">
              <path strokeLinecap="round" strokeLinejoin="round" d="M6 6l12 12M18 6L6 18" />
            </svg>
          </button>
        </div>

        <div className="twv-popup-body">
          {/* source → target, each step tappable to open its own panel */}
          <div className="twv-link-flow">
            <button
              type="button"
              className="twv-link-step"
              onClick={() => onGoToStep(source.id)}
              title={`Open ${source.name}`}
            >
              <span className="twv-link-step-role">From</span>
              <span className="twv-link-step-name">{source.name}</span>
            </button>
            <span className="twv-link-flow-arrow" aria-hidden="true">→</span>
            <button
              type="button"
              className="twv-link-step"
              onClick={() => onGoToStep(target.id)}
              title={`Open ${target.name}`}
            >
              <span className="twv-link-step-role">To</span>
              <span className="twv-link-step-name">{target.name}</span>
            </button>
          </div>

          <section className="twv-section">
            <h4 className="twv-section-title">Trigger</h4>
            <p className="twv-panel-desc">
              {trigger
                ? trigger
                : 'Unconditional — this transition is always taken once the source step completes.'}
            </p>
          </section>

          <p className="twv-panel-hint">Tap a step above to open its details.</p>
        </div>
      </div>
    </div>
  );
}

export default LinkDetailPopup;
