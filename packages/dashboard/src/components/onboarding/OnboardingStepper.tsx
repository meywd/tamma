/**
 * OnboardingStepper — compact horizontal progress strip rendered above the
 * onboarding card. Steps are derived from the live `OnboardingStatus` so
 * the user always sees their actual progress, not a remembered counter.
 */

import type { OnboardingStep } from '../../services/onboarding/onboarding-api-client.js';

import type { JSX } from "react";

interface StepDef {
  id: OnboardingStep;
  label: string;
}

const STEPS: StepDef[] = [
  { id: 'verify-email', label: 'Verify email' },
  { id: 'create-org', label: 'Organization' },
  { id: 'connect-github', label: 'Connect GitHub' },
  { id: 'review-repos', label: 'Pick repos' },
];

interface OnboardingStepperProps {
  /** Currently active step. */
  current: OnboardingStep;
}

export function OnboardingStepper({ current }: OnboardingStepperProps): JSX.Element {
  const currentIdx = STEPS.findIndex((s) => s.id === current);
  // 'complete' is never in STEPS — clamp to last known step so the bar
  // visually fills entirely.
  const effectiveIdx = currentIdx === -1 ? STEPS.length - 1 : currentIdx;

  return (
    <ol
      className="flex items-center justify-between gap-2"
      aria-label="Onboarding progress"
    >
      {STEPS.map((step, idx) => {
        const done = idx < effectiveIdx;
        const active = idx === effectiveIdx;
        return (
          <li key={step.id} className="flex-1 flex items-center min-w-0">
            <div className="flex flex-col items-center min-w-0">
              <div
                className={`w-8 h-8 rounded-full flex items-center justify-center text-xs font-semibold transition-colors ${ done ? 'bg-blue-500 text-white' : active ? 'bg-blue-500/20 text-blue-300 ring-2 ring-blue-500' : 'bg-slate-800 text-slate-500 ring-1 ring-slate-700' }`}
                aria-current={active ? 'step' : undefined}
              >
                {done ? (
                  <svg
                    className="w-4 h-4"
                    viewBox="0 0 20 20"
                    fill="currentColor"
                    aria-hidden="true"
                  >
                    <path
                      fillRule="evenodd"
                      d="M16.707 5.293a1 1 0 010 1.414l-8 8a1 1 0 01-1.414 0l-4-4a1 1 0 011.414-1.414L8 12.586l7.293-7.293a1 1 0 011.414 0z"
                      clipRule="evenodd"
                    />
                  </svg>
                ) : (
                  idx + 1
                )}
              </div>
              <span
                className={`mt-2 text-[10px] uppercase tracking-wider truncate max-w-[5.5rem] ${ active ? 'text-blue-300 font-semibold' : done ? 'text-slate-300' : 'text-slate-500' }`}
              >
                {step.label}
              </span>
            </div>
            {idx < STEPS.length - 1 && (
              <div
                className={`flex-1 h-px mx-2 ${ done ? 'bg-blue-500/60' : 'bg-slate-800' }`}
                aria-hidden="true"
              />
            )}
          </li>
        );
      })}
    </ol>
  );
}
