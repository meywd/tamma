/**
 * Registration + Email Verification Routes (Story 18-1)
 *
 * Endpoints:
 *   POST /api/v1/auth/register        — Create account with email+password
 *   POST /api/v1/auth/verify-email    — Verify email with token
 *   POST /api/v1/auth/resend-verification — Resend verification email
 */

import { createHash, randomBytes } from 'node:crypto';
import type { FastifyInstance, FastifyRequest, FastifyReply } from 'fastify';
import type { IUserStore } from '../../persistence/user-store.js';
import type { IEmailService } from '../../services/email.js';
import { buildVerificationEmail } from '../../services/email.js';
import { hashPassword, validatePasswordStrength } from '../../auth/password.js';

export interface RegisterRoutesOptions {
  userStore: IUserStore;
  emailService: IEmailService;
}

/** Email validation regex (simple but effective). */
const EMAIL_REGEX = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

/** Rate limit tracking for resend verification (email -> timestamps). */
const resendRateLimit = new Map<string, number[]>();
const RESEND_MAX_PER_HOUR = 3;

export async function registerRegistrationRoutes(
  app: FastifyInstance,
  options: RegisterRoutesOptions,
): Promise<void> {
  const { userStore, emailService } = options;

  // -------------------------------------------------------------------
  // POST /api/v1/auth/register
  // -------------------------------------------------------------------
  app.post<{
    Body: { email?: string; password?: string; name?: string };
  }>(
    '/api/v1/auth/register',
    async (request: FastifyRequest<{ Body: { email?: string; password?: string; name?: string } }>, reply: FastifyReply) => {
      const { email, password, name } = request.body ?? {};

      // Input validation
      if (!email || !password || !name) {
        return reply.status(400).send({ error: 'email, password, and name are required' });
      }

      if (typeof name !== 'string' || name.trim().length < 2 || name.trim().length > 100) {
        return reply.status(400).send({ error: 'Name must be between 2 and 100 characters' });
      }

      if (!EMAIL_REGEX.test(email)) {
        return reply.status(400).send({ error: 'Invalid email format' });
      }

      // Password strength validation
      const passwordValidation = validatePasswordStrength(password);
      if (!passwordValidation.valid) {
        return reply.status(400).send({ error: 'Password too weak', details: passwordValidation.errors });
      }

      // Normalize email
      const normalizedEmail = email.toLowerCase().trim();

      // Check email uniqueness
      const existingUser = await userStore.getUserByEmail(normalizedEmail);
      if (existingUser) {
        return reply.status(409).send({ error: 'Email already registered' });
      }

      // Hash password
      const passwordHash = await hashPassword(password);

      // Generate verification token
      const rawToken = randomBytes(32).toString('hex');
      const tokenHash = createHash('sha256').update(rawToken).digest('hex');
      const expiresAt = new Date(Date.now() + 24 * 60 * 60 * 1000).toISOString(); // 24 hours

      // Create user
      const user = await userStore.createEmailUser({
        email: normalizedEmail,
        name: name.trim(),
        passwordHash,
        emailVerificationTokenHash: tokenHash,
        emailVerificationExpiresAt: expiresAt,
      });

      // Send verification email (fire-and-forget with error logging)
      emailService.sendEmail(
        buildVerificationEmail(normalizedEmail, name.trim(), rawToken),
      ).catch((err) => {
        request.log.error({ err, userId: user.id }, 'Failed to send verification email');
      });

      request.log.info({
        event: 'USER.REGISTERED.SUCCESS',
        userId: user.id,
        email: normalizedEmail,
      }, 'User registered');

      return reply.status(201).send({
        id: user.id,
        email: user.email,
        message: 'Verification email sent',
      });
    },
  );

  // -------------------------------------------------------------------
  // POST /api/v1/auth/verify-email
  // -------------------------------------------------------------------
  app.post<{
    Body: { token?: string };
  }>(
    '/api/v1/auth/verify-email',
    async (request: FastifyRequest<{ Body: { token?: string } }>, reply: FastifyReply) => {
      const { token } = request.body ?? {};

      if (!token) {
        return reply.status(400).send({ error: 'token is required' });
      }

      // Hash the incoming token
      const tokenHash = createHash('sha256').update(token).digest('hex');

      // Find user by verification token hash
      // We need to search for the user with this token hash
      // Since IUserStore doesn't have a "getUserByVerificationToken" method,
      // we scan for the user. In practice, PgUserStore would use a SQL query.
      // For now, we use a helper approach:
      const user = await findUserByVerificationTokenHash(userStore, tokenHash);

      if (!user) {
        return reply.status(400).send({ error: 'Invalid or expired verification token' });
      }

      // Check expiry
      if (!user.emailVerificationExpiresAt || new Date(user.emailVerificationExpiresAt) < new Date()) {
        return reply.status(400).send({ error: 'Verification token has expired' });
      }

      // Already verified
      if (user.emailVerified) {
        return reply.status(400).send({ error: 'Email already verified' });
      }

      // Mark as verified
      await userStore.setEmailVerified(user.id);

      request.log.info({
        event: 'USER.EMAIL_VERIFIED.SUCCESS',
        userId: user.id,
      }, 'Email verified');

      return reply.send({ message: 'Email verified successfully' });
    },
  );

  // -------------------------------------------------------------------
  // POST /api/v1/auth/resend-verification
  // -------------------------------------------------------------------
  app.post<{
    Body: { email?: string };
  }>(
    '/api/v1/auth/resend-verification',
    async (request: FastifyRequest<{ Body: { email?: string } }>, reply: FastifyReply) => {
      const { email } = request.body ?? {};

      if (!email) {
        return reply.status(400).send({ error: 'email is required' });
      }

      const normalizedEmail = email.toLowerCase().trim();

      // Rate limiting: 3 per hour per email
      if (isResendRateLimited(normalizedEmail)) {
        return reply.status(429).send({ error: 'Too many requests. Please try again later.' });
      }

      // Always return 200 to prevent enumeration
      const user = await userStore.getUserByEmail(normalizedEmail);
      if (!user || user.emailVerified) {
        // Don't reveal whether the email exists or is already verified
        return reply.send({ message: 'If the email exists and is unverified, a verification email has been sent' });
      }

      // Generate new token
      const rawToken = randomBytes(32).toString('hex');
      const tokenHash = createHash('sha256').update(rawToken).digest('hex');
      const expiresAt = new Date(Date.now() + 24 * 60 * 60 * 1000).toISOString();

      // Update user with new token
      await userStore.updateVerificationToken(user.id, tokenHash, expiresAt);

      // Send email
      const displayName = user.githubLogin || normalizedEmail.split('@')[0] || 'User';
      emailService.sendEmail(
        buildVerificationEmail(normalizedEmail, displayName, rawToken),
      ).catch((err) => {
        request.log.error({ err, userId: user.id }, 'Failed to resend verification email');
      });

      // Track rate limit
      recordResendAttempt(normalizedEmail);

      return reply.send({ message: 'If the email exists and is unverified, a verification email has been sent' });
    },
  );
}

/**
 * Find a user by their email verification token hash.
 * This is a workaround since we search by a field on the user rather than a separate table.
 */
async function findUserByVerificationTokenHash(userStore: IUserStore, tokenHash: string): Promise<import('../../persistence/user-store.js').User | null> {
  // For InMemoryUserStore, we need to check all users.
  // For PgUserStore, this would be a SQL query.
  // We add a method to the store interface in a future iteration.
  // For now, use a cast to access internal state or iterate.
  const store = userStore as unknown as { users?: Map<string, import('../../persistence/user-store.js').User> };
  if (store.users) {
    // InMemoryUserStore
    for (const user of store.users.values()) {
      if (user.emailVerificationTokenHash === tokenHash) return user;
    }
    return null;
  }

  // PgUserStore — direct query
  const pgStore = userStore as unknown as { pool?: { query: (sql: string, params: unknown[]) => Promise<{ rows: Record<string, unknown>[] }> } };
  if (pgStore.pool) {
    const result = await pgStore.pool.query(
      'SELECT * FROM users WHERE email_verification_token_hash = $1 AND deleted_at IS NULL',
      [tokenHash],
    );
    if (result.rows.length === 0) return null;
    const row = result.rows[0]!;
    return {
      id: String(row['id']),
      githubId: row['github_id'] !== null && row['github_id'] !== undefined ? Number(row['github_id']) : null,
      githubLogin: String(row['github_login'] ?? ''),
      email: row['email'] !== null && row['email'] !== undefined ? String(row['email']) : null,
      role: String(row['role']) as 'owner' | 'admin' | 'member',
      tenantId: row['tenant_id'] !== null && row['tenant_id'] !== undefined ? String(row['tenant_id']) : null,
      settings: (row['settings'] ?? { providers: {} }) as import('@tamma/shared').IProvidersConfig,
      lastActiveAt: row['last_active_at'] !== null && row['last_active_at'] !== undefined ? String(row['last_active_at']) : null,
      createdAt: String(row['created_at']),
      updatedAt: String(row['updated_at']),
      passwordHash: row['password_hash'] !== null && row['password_hash'] !== undefined ? String(row['password_hash']) : null,
      emailVerified: Boolean(row['email_verified']),
      authMethod: String(row['auth_method'] ?? 'github') as import('../../persistence/user-store.js').AuthMethod,
      emailVerificationTokenHash: row['email_verification_token_hash'] !== null && row['email_verification_token_hash'] !== undefined ? String(row['email_verification_token_hash']) : null,
      emailVerificationExpiresAt: row['email_verification_expires_at'] !== null && row['email_verification_expires_at'] !== undefined ? String(row['email_verification_expires_at']) : null,
    };
  }

  return null;
}

/** Check if resend is rate limited for an email. */
function isResendRateLimited(email: string): boolean {
  const now = Date.now();
  const oneHourAgo = now - 60 * 60 * 1000;
  const timestamps = resendRateLimit.get(email);
  if (!timestamps) return false;
  const recent = timestamps.filter((t) => t > oneHourAgo);
  resendRateLimit.set(email, recent);
  return recent.length >= RESEND_MAX_PER_HOUR;
}

/** Record a resend attempt for rate limiting. */
function recordResendAttempt(email: string): void {
  const timestamps = resendRateLimit.get(email) ?? [];
  timestamps.push(Date.now());
  resendRateLimit.set(email, timestamps);
}
