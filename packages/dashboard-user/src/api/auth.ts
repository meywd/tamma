/**
 * Password-reset API (Story 45-3). Built on the shared ApiClient — never a
 * bare `fetch` (see 45-1) — though both endpoints are anonymous.
 *
 * Server contracts (read, not guessed — Endpoints/AuthEndpoints.cs):
 *
 *   POST /api/v1/auth/password-reset/request   { email }
 *     - 400 { error: "Email is required" }        when email is blank
 *     - 429 { error: "Too many reset requests. Please try again later." }
 *     - 200 { message: "If the email exists, a reset link has been sent" }
 *       for BOTH known and unknown addresses (and for GitHub-only accounts):
 *       the response is deliberately indistinguishable so the form cannot be
 *       used as an account-enumeration oracle. The client renders ONE success
 *       state and never branches on existence (45-3 D4).
 *
 *   POST /api/v1/auth/password-reset/confirm   { token, newPassword }
 *     - 400 { error: "Password too weak", details: string[] }
 *       (PasswordStrengthValidator — min 8 / max 128 chars, at least one
 *        uppercase, one lowercase, one digit, not in the common-password list)
 *     - 400 { error: "Invalid or expired reset token" }
 *     - 400 { error: "User not found" }
 *     - 200 { message: "Password reset successfully" }
 */

import { apiClient } from './client';

export interface PasswordResetMessage {
  message: string;
}

export async function requestPasswordReset(email: string): Promise<PasswordResetMessage> {
  return apiClient.post<PasswordResetMessage>('/api/v1/auth/password-reset/request', { email });
}

export async function confirmPasswordReset(
  token: string,
  newPassword: string,
): Promise<PasswordResetMessage> {
  return apiClient.post<PasswordResetMessage>('/api/v1/auth/password-reset/confirm', {
    token,
    newPassword,
  });
}

/**
 * Client-side pre-flight mirror of the server's PasswordStrengthValidator
 * (apps/tamma-elsa/src/Tamma.Api/Auth/PasswordStrengthValidator.cs). The
 * server remains authoritative — this is a UX speedup, not a security
 * boundary (the `hasPlaintextCredential` posture, api/alerts.ts). The
 * common-password list is NOT mirrored; the server rejects those with the
 * same "Password too weak" shape and the form surfaces its `details`.
 */
export function passwordPreflightErrors(password: string): string[] {
  const errors: string[] = [];
  if (password.length === 0) {
    errors.push('Password is required');
    return errors;
  }
  if (password.length < 8) errors.push('Password must be at least 8 characters');
  if (password.length > 128) errors.push('Password must be at most 128 characters');
  if (!/[A-Z]/.test(password)) errors.push('Password must contain at least one uppercase letter');
  if (!/[a-z]/.test(password)) errors.push('Password must contain at least one lowercase letter');
  if (!/\d/.test(password)) errors.push('Password must contain at least one digit');
  return errors;
}
