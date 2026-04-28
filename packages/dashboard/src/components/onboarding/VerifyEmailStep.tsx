import type { JSX } from "react";
/**
 * Step 1 — Email verification.
 *
 * Re-sending the verification email and the actual verify-token endpoint
 * live in `AuthEndpoints.cs` (Story 18-1) and have their own UX. This
 * step exists for users who landed here via a back-button, refresh, or
 * direct URL — it explains the gate and gives them a "Check now" button.
 */

interface VerifyEmailStepProps {
  /** Re-poll the status endpoint. */
  onRefresh: () => void;
}

export function VerifyEmailStep({ onRefresh }: VerifyEmailStepProps): JSX.Element {
  return (
    <div className="space-y-4">
      <p className="text-sm text-slate-300">
        We sent a verification link to your inbox. Click it, then return
        here. Once your email is verified the next step unlocks
        automatically.
      </p>
      <div className="rounded-md bg-slate-800/50 border border-slate-700 p-3 text-xs text-slate-400">
        Can't find the email? Check your spam folder or use the
        "Resend verification" option on the sign-in page.
      </div>
      <button
        type="button"
        onClick={onRefresh}
        className="px-4 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-500 rounded-md"
      >
        I verified my email
      </button>
    </div>
  );
}
