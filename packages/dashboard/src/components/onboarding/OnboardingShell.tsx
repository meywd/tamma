/**
 * OnboardingShell — Midnight-Ocean themed wrapper for every onboarding page.
 *
 * The onboarding flow runs *outside* the standard `AppLayout` (no
 * sidebar, no NavHeader) because:
 *  1. New users haven't picked a tenant yet, so most sidebar items are
 *     unreachable.
 *  2. A focused single-card layout drives completion better than the
 *     full chrome.
 *
 * Visual language: deep blue/slate gradient ("midnight ocean"), centred
 * card, generous whitespace. Matches the LoginPage's centred-card shape
 * but on a dark background to signal "you are in a guided flow".
 */

import type { ReactNode } from 'react';

interface OnboardingShellProps {
  /** Tagline shown above the card title. */
  eyebrow?: string;
  /** Card-level heading. */
  title: string;
  /** Optional sub-heading shown under the title. */
  subtitle?: ReactNode;
  /** Card body. */
  children: ReactNode;
  /** Optional footer (action buttons, "skip", etc.). Rendered inside the card, separated by a divider. */
  footer?: ReactNode;
  /** Optional compact stepper rendered above the card. */
  stepper?: ReactNode;
}

export function OnboardingShell({
  eyebrow,
  title,
  subtitle,
  children,
  footer,
  stepper,
}: OnboardingShellProps): JSX.Element {
  return (
    <div
      className="min-h-screen flex flex-col items-center justify-center px-4 py-12 font-sans"
      style={{
        background:
          'radial-gradient(ellipse at top, rgb(15 23 42) 0%, rgb(2 6 23) 70%)',
      }}
    >
      <div className="w-full max-w-xl">
        <div className="flex items-center justify-center gap-2 mb-6">
          <img src="/logo.png" alt="Tamma" className="w-9 h-9 rounded-md" />
          <span className="text-xl font-semibold tracking-tight text-slate-100">
            Tamma
          </span>
        </div>
        {stepper && <div className="mb-6">{stepper}</div>}
        <div className="bg-slate-900/80 backdrop-blur-sm border border-slate-800 rounded-2xl shadow-2xl p-8 text-slate-100">
          {eyebrow && (
            <div className="text-xs uppercase tracking-widest text-blue-400 font-semibold mb-2">
              {eyebrow}
            </div>
          )}
          <h1 className="text-2xl font-bold text-white mb-2">{title}</h1>
          {subtitle && (
            <div className="text-sm text-slate-400 mb-6">{subtitle}</div>
          )}
          <div>{children}</div>
          {footer && (
            <div className="mt-8 pt-6 border-t border-slate-800 flex flex-wrap items-center justify-end gap-3">
              {footer}
            </div>
          )}
        </div>
        <div className="text-center text-xs text-slate-500 mt-6">
          Need help? See{' '}
          <a
            href="https://github.com/meywd/tamma"
            className="text-blue-400 hover:text-blue-300 underline"
            target="_blank"
            rel="noopener noreferrer"
          >
            Tamma docs
          </a>
          .
        </div>
      </div>
    </div>
  );
}
