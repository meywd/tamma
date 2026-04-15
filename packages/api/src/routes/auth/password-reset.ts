/**
 * Password Reset Routes (Story 18-6)
 *
 * Endpoints:
 *   POST /api/v1/auth/password-reset/request — Request a password reset email
 *   POST /api/v1/auth/password-reset/confirm — Reset password with token
 */

import { createHash, randomBytes } from 'node:crypto';
import type { FastifyInstance, FastifyRequest, FastifyReply } from 'fastify';
import type { IUserStore } from '../../persistence/user-store.js';
import type { IPasswordResetStore } from '../../persistence/password-reset-store.js';
import type { IRefreshTokenStore } from '../../persistence/refresh-token-store.js';
import type { IEmailService } from '../../services/email.js';
import { buildPasswordResetEmail } from '../../services/email.js';
import { hashPassword, validatePasswordStrength } from '../../auth/password.js';

export interface PasswordResetRoutesOptions {
  userStore: IUserStore;
  passwordResetStore: IPasswordResetStore;
  refreshTokenStore: IRefreshTokenStore;
  emailService: IEmailService;
}

/** Email validation regex. */
const EMAIL_REGEX = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

/** Rate limit tracking (email -> timestamps). */
const resetRateLimit = new Map<string, number[]>();
const RESET_MAX_PER_EMAIL_PER_HOUR = 3;
const RESET_TOKEN_EXPIRY_MS = 60 * 60 * 1000; // 1 hour

export async function registerPasswordResetRoutes(
  app: FastifyInstance,
  options: PasswordResetRoutesOptions,
): Promise<void> {
  const { userStore, passwordResetStore, refreshTokenStore, emailService } = options;

  // -------------------------------------------------------------------
  // POST /api/v1/auth/password-reset/request
  // -------------------------------------------------------------------
  app.post<{
    Body: { email?: string };
  }>(
    '/api/v1/auth/password-reset/request',
    async (request: FastifyRequest<{ Body: { email?: string } }>, reply: FastifyReply) => {
      const { email } = request.body ?? {};

      if (!email) {
        return reply.status(400).send({ error: 'email is required' });
      }

      if (!EMAIL_REGEX.test(email)) {
        return reply.status(400).send({ error: 'Invalid email format' });
      }

      const normalizedEmail = email.toLowerCase().trim();

      // Rate limiting per email
      if (isResetRateLimited(normalizedEmail)) {
        return reply.status(429).send({ error: 'Too many reset requests. Please try again later.' });
      }

      // Always return 200 to prevent enumeration
      const successMessage = 'If an account with that email exists, a reset link has been sent';

      const user = await userStore.getUserByEmail(normalizedEmail);

      // Don't send email if:
      // - User doesn't exist
      // - User is GitHub-only (no password to reset)
      if (!user || user.authMethod === 'github') {
        return reply.send({ message: successMessage });
      }

      // Generate reset token
      const rawToken = randomBytes(32).toString('hex');
      const tokenHash = createHash('sha256').update(rawToken).digest('hex');
      const expiresAt = new Date(Date.now() + RESET_TOKEN_EXPIRY_MS).toISOString();

      // Store token
      await passwordResetStore.createResetToken(user.id, tokenHash, expiresAt);

      // Send reset email
      const displayName = user.githubLogin || (user.email?.split('@')[0]) || 'User';
      emailService.sendEmail(
        buildPasswordResetEmail(normalizedEmail, displayName, rawToken),
      ).catch((err) => {
        request.log.error({ err, userId: user.id }, 'Failed to send password reset email');
      });

      // Track rate limit
      recordResetAttempt(normalizedEmail);

      request.log.info({
        event: 'USER.PASSWORD_RESET_REQUESTED.SUCCESS',
        userId: user.id,
        email: normalizedEmail,
      }, 'Password reset requested');

      return reply.send({ message: successMessage });
    },
  );

  // -------------------------------------------------------------------
  // POST /api/v1/auth/password-reset/confirm
  // -------------------------------------------------------------------
  app.post<{
    Body: { token?: string; newPassword?: string };
  }>(
    '/api/v1/auth/password-reset/confirm',
    async (request: FastifyRequest<{ Body: { token?: string; newPassword?: string } }>, reply: FastifyReply) => {
      const { token, newPassword } = request.body ?? {};

      if (!token || !newPassword) {
        return reply.status(400).send({ error: 'token and newPassword are required' });
      }

      // Validate new password strength
      const passwordValidation = validatePasswordStrength(newPassword);
      if (!passwordValidation.valid) {
        return reply.status(400).send({ error: 'Password too weak', details: passwordValidation.errors });
      }

      // Hash the incoming token
      const tokenHash = createHash('sha256').update(token).digest('hex');
      const resetToken = await passwordResetStore.getResetTokenByHash(tokenHash);

      if (!resetToken) {
        return reply.status(400).send({ error: 'Invalid or expired reset token' });
      }

      // Check if already consumed
      if (resetToken.consumedAt !== null) {
        return reply.status(400).send({ error: 'Reset token has already been used' });
      }

      // Check expiry
      if (new Date(resetToken.expiresAt) < new Date()) {
        return reply.status(400).send({ error: 'Reset token has expired' });
      }

      // Hash new password
      const newPasswordHash = await hashPassword(newPassword);

      // Update password
      await userStore.updatePasswordHash(resetToken.userId, newPasswordHash);

      // Consume the token
      await passwordResetStore.consumeResetToken(resetToken.id);

      // Revoke all refresh tokens (force re-login on all devices)
      await refreshTokenStore.revokeAllForUser(resetToken.userId);

      request.log.info({
        event: 'USER.PASSWORD_RESET.SUCCESS',
        userId: resetToken.userId,
      }, 'Password reset completed');

      return reply.send({ message: 'Password has been reset. Please log in with your new password.' });
    },
  );
}

/** Check if reset request is rate limited for an email. */
function isResetRateLimited(email: string): boolean {
  const now = Date.now();
  const oneHourAgo = now - 60 * 60 * 1000;
  const timestamps = resetRateLimit.get(email);
  if (!timestamps) return false;
  const recent = timestamps.filter((t) => t > oneHourAgo);
  resetRateLimit.set(email, recent);
  return recent.length >= RESET_MAX_PER_EMAIL_PER_HOUR;
}

/** Record a reset attempt for rate limiting. */
function recordResetAttempt(email: string): void {
  const timestamps = resetRateLimit.get(email) ?? [];
  timestamps.push(Date.now());
  resetRateLimit.set(email, timestamps);
}
