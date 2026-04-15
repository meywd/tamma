/**
 * Login + Refresh + Logout Routes (Story 18-2)
 *
 * Endpoints:
 *   POST /api/v1/auth/login    — Email+password login → JWT + refresh token
 *   POST /api/v1/auth/refresh  — Refresh token → new JWT + refresh token
 *   POST /api/v1/auth/logout   — Invalidate refresh token, clear cookie
 */

import { createHash, randomBytes } from 'node:crypto';
import type { FastifyInstance, FastifyRequest, FastifyReply } from 'fastify';
import type { IUserStore } from '../../persistence/user-store.js';
import type { IRefreshTokenStore } from '../../persistence/refresh-token-store.js';
import type { ITenantMembershipStore } from '../../persistence/tenant-membership-store.js';
import type { ILoginLockoutService } from '../../auth/login-lockout.js';
import { verifyPassword } from '../../auth/password.js';
import { buildJwtClaims } from '../../auth/jwt.js';
import type { TenantRole, PlatformRole } from '../../auth/jwt.js';

export interface LoginRoutesOptions {
  userStore: IUserStore;
  refreshTokenStore: IRefreshTokenStore;
  membershipStore: ITenantMembershipStore;
  lockoutService: ILoginLockoutService;
  jwtSecret: string;
  /** Access token expiry in seconds. Default: 900 (15 min). */
  accessTokenExpiresIn?: number;
  /** Refresh token expiry in seconds. Default: 604800 (7 days). */
  refreshTokenExpiresIn?: number;
}

/** Simple email check — no regex backtracking. */
function isValidEmail(e: string): boolean {
  if (e.length > 254 || e.length < 5) return false;
  const at = e.indexOf('@');
  if (at < 1 || at > 64) return false;
  const domain = e.slice(at + 1);
  return domain.length >= 3 && domain.includes('.') && !e.includes(' ');
}

export async function registerLoginRoutes(
  app: FastifyInstance,
  options: LoginRoutesOptions,
): Promise<void> {
  const {
    userStore,
    refreshTokenStore,
    membershipStore,
    lockoutService,
    accessTokenExpiresIn = 900,
    refreshTokenExpiresIn = 604800,
  } = options;

  // Ensure JWT plugin is registered
  if (!app.hasDecorator('jwt')) {
    await app.register(await import('@fastify/jwt').then((m) => m.default ?? m), {
      secret: options.jwtSecret,
      sign: { expiresIn: `${accessTokenExpiresIn}s` },
      cookie: { cookieName: 'tamma_session', signed: false },
    });
  }

  // Ensure cookie plugin is registered
  if (!app.hasDecorator('parseCookie')) {
    await app.register(await import('@fastify/cookie').then((m) => m.default ?? m));
  }

  // -------------------------------------------------------------------
  // POST /api/v1/auth/login
  // -------------------------------------------------------------------
  app.post<{
    Body: { email?: string; password?: string };
  }>(
    '/api/v1/auth/login',
    async (request: FastifyRequest<{ Body: { email?: string; password?: string } }>, reply: FastifyReply) => {
      const { email, password } = request.body ?? {};

      if (!email || !password) {
        return reply.status(400).send({ error: 'email and password are required' });
      }

      if (!isValidEmail(email)) {
        return reply.status(400).send({ error: 'Invalid email format' });
      }

      const normalizedEmail = email.toLowerCase().trim();

      // Check lockout
      if (lockoutService.isLocked(normalizedEmail)) {
        const remaining = lockoutService.getRemainingLockoutSeconds(normalizedEmail);
        return reply.status(429).send({
          error: 'Account temporarily locked due to too many failed login attempts',
          retryAfterSeconds: remaining,
        });
      }

      // Look up user
      const user = await userStore.getUserByEmail(normalizedEmail);

      if (!user || !user.passwordHash) {
        // Constant-time path: always hash something to prevent timing attacks
        await verifyPassword(password, 'scrypt:32768:8:1:64:deadbeef:deadbeef');
        lockoutService.recordFailedAttempt(normalizedEmail);

        request.log.info({
          event: 'USER.LOGIN.FAILED',
          email: normalizedEmail,
          reason: 'user_not_found',
        }, 'Login failed');

        return reply.status(401).send({ error: 'Invalid email or password' });
      }

      // Verify password
      const isValid = await verifyPassword(password, user.passwordHash);
      if (!isValid) {
        const locked = lockoutService.recordFailedAttempt(normalizedEmail);

        request.log.info({
          event: 'USER.LOGIN.FAILED',
          userId: user.id,
          email: normalizedEmail,
          reason: 'invalid_password',
          locked,
        }, 'Login failed');

        if (locked) {
          const remaining = lockoutService.getRemainingLockoutSeconds(normalizedEmail);
          return reply.status(429).send({
            error: 'Account temporarily locked due to too many failed login attempts',
            retryAfterSeconds: remaining,
          });
        }

        return reply.status(401).send({ error: 'Invalid email or password' });
      }

      // Check email verification
      if (!user.emailVerified) {
        return reply.status(403).send({ error: 'Please verify your email' });
      }

      // Reset lockout on success
      lockoutService.resetAttempts(normalizedEmail);

      // Resolve tenant role
      let tenantRole: TenantRole = 'member';
      if (user.tenantId) {
        const membership = await membershipStore.getMembership(user.tenantId, user.id);
        if (membership) {
          tenantRole = membership.role;
        }
      }

      // Determine platform role
      const platformRole: PlatformRole = user.role === 'owner' ? 'platform_admin' : 'user';

      // Build JWT claims
      const displayName = user.githubLogin || (user.email?.split('@')[0]) || 'User';
      const claims = buildJwtClaims(
        user.id,
        user.email ?? '',
        displayName,
        user.tenantId,
        tenantRole,
        platformRole,
        user.authMethod,
      );

      // Sign access token
      const accessToken = app.jwt.sign(claims as Record<string, unknown>);

      // Generate refresh token
      const rawRefreshToken = randomBytes(32).toString('hex');
      const refreshTokenHash = createHash('sha256').update(rawRefreshToken).digest('hex');
      const refreshExpiresAt = new Date(Date.now() + refreshTokenExpiresIn * 1000).toISOString();

      await refreshTokenStore.createToken(user.id, refreshTokenHash, refreshExpiresAt);

      // Update last active
      await userStore.updateLastActive(user.id);

      // Set session cookie
      reply.setCookie('tamma_session', accessToken, {
        path: '/',
        httpOnly: true,
        secure: true,
        sameSite: 'lax' as const,
        maxAge: accessTokenExpiresIn,
        domain: '.tamma.dev',
      });

      request.log.info({
        event: 'USER.LOGIN.SUCCESS',
        userId: user.id,
        email: normalizedEmail,
      }, 'Login successful');

      return reply.send({
        accessToken,
        refreshToken: rawRefreshToken,
        user: {
          id: user.id,
          email: user.email,
          name: displayName,
          role: tenantRole,
          tenantId: user.tenantId,
        },
      });
    },
  );

  // -------------------------------------------------------------------
  // POST /api/v1/auth/refresh
  // -------------------------------------------------------------------
  app.post<{
    Body: { refreshToken?: string };
  }>(
    '/api/v1/auth/refresh',
    async (request: FastifyRequest<{ Body: { refreshToken?: string } }>, reply: FastifyReply) => {
      const { refreshToken } = request.body ?? {};

      if (!refreshToken) {
        return reply.status(400).send({ error: 'refreshToken is required' });
      }

      // Hash the incoming token
      const tokenHash = createHash('sha256').update(refreshToken).digest('hex');
      const storedToken = await refreshTokenStore.getTokenByHash(tokenHash);

      if (!storedToken) {
        return reply.status(401).send({ error: 'Invalid refresh token' });
      }

      // Check if revoked
      if (storedToken.revokedAt !== null) {
        // Token reuse detected — potential compromise. Revoke all tokens for user.
        await refreshTokenStore.revokeAllForUser(storedToken.userId);

        request.log.warn({
          event: 'USER.REFRESH_TOKEN_REUSE',
          userId: storedToken.userId,
        }, 'Refresh token reuse detected — all sessions revoked');

        return reply.status(401).send({ error: 'Refresh token has been revoked' });
      }

      // Check expiry
      if (new Date(storedToken.expiresAt) < new Date()) {
        return reply.status(401).send({ error: 'Refresh token has expired' });
      }

      // Revoke the old token (rotation)
      await refreshTokenStore.revokeToken(storedToken.id);

      // Get user
      const user = await userStore.getUser(storedToken.userId);
      if (!user) {
        return reply.status(401).send({ error: 'User not found' });
      }

      // Resolve tenant role
      let tenantRole: TenantRole = 'member';
      if (user.tenantId) {
        const membership = await membershipStore.getMembership(user.tenantId, user.id);
        if (membership) {
          tenantRole = membership.role;
        }
      }

      const platformRole: PlatformRole = user.role === 'owner' ? 'platform_admin' : 'user';
      const displayName = user.githubLogin || (user.email?.split('@')[0]) || 'User';

      const claims = buildJwtClaims(
        user.id,
        user.email ?? '',
        displayName,
        user.tenantId,
        tenantRole,
        platformRole,
        user.authMethod,
      );

      // Sign new access token
      const accessToken = app.jwt.sign(claims as Record<string, unknown>);

      // Generate new refresh token
      const newRawRefreshToken = randomBytes(32).toString('hex');
      const newRefreshTokenHash = createHash('sha256').update(newRawRefreshToken).digest('hex');
      const newRefreshExpiresAt = new Date(Date.now() + refreshTokenExpiresIn * 1000).toISOString();

      await refreshTokenStore.createToken(user.id, newRefreshTokenHash, newRefreshExpiresAt);

      // Set session cookie
      reply.setCookie('tamma_session', accessToken, {
        path: '/',
        httpOnly: true,
        secure: true,
        sameSite: 'lax' as const,
        maxAge: accessTokenExpiresIn,
        domain: '.tamma.dev',
      });

      return reply.send({
        accessToken,
        refreshToken: newRawRefreshToken,
      });
    },
  );

  // -------------------------------------------------------------------
  // POST /api/v1/auth/logout
  // -------------------------------------------------------------------
  app.post<{
    Body: { refreshToken?: string };
  }>(
    '/api/v1/auth/logout',
    async (request: FastifyRequest<{ Body: { refreshToken?: string } }>, reply: FastifyReply) => {
      const { refreshToken } = request.body ?? {};

      if (refreshToken) {
        const tokenHash = createHash('sha256').update(refreshToken).digest('hex');
        const storedToken = await refreshTokenStore.getTokenByHash(tokenHash);
        if (storedToken) {
          await refreshTokenStore.revokeToken(storedToken.id);
        }
      }

      // Clear session cookie
      reply.clearCookie('tamma_session', { path: '/', domain: '.tamma.dev' });

      request.log.info({
        event: 'USER.LOGOUT.SUCCESS',
      }, 'User logged out');

      return reply.send({ ok: true });
    },
  );
}
