/**
 * JWT Authentication Plugin
 *
 * Fastify plugin that provides:
 *   POST /api/auth/login     - username/password login  -> JWT
 *   POST /api/auth/refresh   - refresh token            -> new JWT
 *   POST /api/auth/api-key   - API key auth (CI/scripts)-> JWT
 *
 * When `enableAuth` is false (dev mode), all routes pass through with a stub user.
 */

import fp from 'fastify-plugin';
import rateLimit from '@fastify/rate-limit';
import { timingSafeEqual } from 'node:crypto';
import type { FastifyInstance, FastifyRequest, FastifyReply } from 'fastify';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

export interface RateLimitConfig {
  /** Max requests per window. Default: 10 */
  max?: number;
  /** Window size in milliseconds. Default: 60000 (1 minute) */
  windowMs?: number;
}

export interface AuthConfig {
  jwtSecret: string;
  enableAuth: boolean;
  /** Optional list of valid API keys for machine-to-machine auth. */
  apiKeys?: string[];
  /** Token expiry in seconds. Default: 3600 (1 hour). */
  tokenExpiresIn?: number;
  /** Refresh token expiry in seconds. Default: 604800 (7 days). */
  refreshExpiresIn?: number;
  /** Rate limit configuration for auth routes. */
  rateLimit?: RateLimitConfig;
}

export interface AuthUser {
  id: string;
  username: string;
  role: 'admin' | 'operator' | 'viewer';
}

interface LoginBody {
  username: string;
  password: string;
}

interface RefreshBody {
  refreshToken: string;
}

interface ApiKeyBody {
  apiKey: string;
}

// ---------------------------------------------------------------------------
// Stub user for dev mode
// ---------------------------------------------------------------------------

const STUB_USER: AuthUser = {
  id: 'dev-user',
  username: 'developer',
  role: 'admin',
};

// ---------------------------------------------------------------------------
// Plugin
// ---------------------------------------------------------------------------

async function authPlugin(
  fastify: FastifyInstance,
  opts: AuthConfig,
): Promise<void> {
  const {
    jwtSecret,
    enableAuth,
    apiKeys = [],
    tokenExpiresIn = 3600,
    refreshExpiresIn = 604800,
    rateLimit: rateLimitConfig,
  } = opts;

  // Require a real secret when auth is enabled
  if (enableAuth && !jwtSecret) {
    throw new Error('TAMMA_JWT_SECRET must be set when authentication is enabled');
  }
  const secret = jwtSecret || 'dev-secret-do-not-use-in-production';

  // Register rate limiter with global protection and stricter per-route auth limits
  const rlMax = rateLimitConfig?.max ?? 10;
  const rlWindow = rateLimitConfig?.windowMs ?? 60_000;

  // Global rate limit applies to all routes (including JWT-verified requests)
  // to prevent brute-force attacks. Auth routes override with stricter limits below.
  await fastify.register(rateLimit, {
    global: true,
    max: rlMax * 10,
    timeWindow: rlWindow,
    keyGenerator: (request: FastifyRequest) => request.ip,
  });

  // Register @fastify/jwt
  await fastify.register(await import('@fastify/jwt').then((m) => m.default ?? m), {
    secret,
    sign: { expiresIn: `${tokenExpiresIn}s` },
  });

  // ------------------------------------------------------------------
  // Decorate request with user
  // ------------------------------------------------------------------
  fastify.decorateRequest('authUser', null);

  // ------------------------------------------------------------------
  // Global onRequest hook (rate-limited by the global @fastify/rate-limit
  // plugin registered above — max: rlMax * 10 per rlWindow per IP)
  // ------------------------------------------------------------------
  fastify.addHook(
    'onRequest',
    async (request: FastifyRequest, reply: FastifyReply) => {
      // The global rate limiter (registered above via @fastify/rate-limit)
      // already enforces a per-IP request ceiling on every request,
      // including this hook. CodeQL may not recognise plugin-based rate
      // limiting, but the protection is in place.

      // Dev mode — skip auth entirely
      if (!enableAuth) {
        (request as FastifyRequest & { authUser: AuthUser }).authUser = STUB_USER;
        return;
      }

      // Public routes that don't need auth
      const publicPaths = ['/api/auth/login', '/api/auth/api-key', '/api/health'];
      if (publicPaths.some((p) => request.url === p || request.url.startsWith(p + '/'))) {
        return;
      }

      // Verify JWT
      try {
        const decoded = await request.jwtVerify<{ id: string; username: string; role: string }>();

        const allowedRoles: ReadonlySet<string> = new Set(['admin', 'operator', 'viewer']);
        if (!allowedRoles.has(decoded.role)) {
          return reply.status(403).send({ error: 'Invalid role in token' });
        }

        (request as FastifyRequest & { authUser: AuthUser }).authUser = {
          id: decoded.id,
          username: decoded.username,
          role: decoded.role as AuthUser['role'],
        };
      } catch (err) {
        // If we already sent a 403 for invalid role, don't override it
        if (reply.sent) return;
        return reply.status(401).send({ error: 'Unauthorized' });
      }
    },
  );

  // ------------------------------------------------------------------
  // POST /api/auth/login
  // ------------------------------------------------------------------
  fastify.post<{ Body: LoginBody }>(
    '/api/auth/login',
    { config: { rateLimit: { max: rlMax, timeWindow: rlWindow } } },
    async (request, reply) => {
      const { username, password } = request.body ?? {};

      if (!username || !password) {
        return reply.status(400).send({ error: 'username and password are required' });
      }

      // In a real implementation this would check a user store.
      // For MVP we accept any non-empty credentials when auth is disabled,
      // or require a valid user store lookup when auth is enabled.
      if (enableAuth) {
        // Placeholder: reject all logins until a real user store is wired up
        return reply.status(401).send({ error: 'Invalid credentials' });
      }

      const user: AuthUser = { id: 'user-1', username, role: 'admin' };
      const token = fastify.jwt.sign({ id: user.id, username: user.username, role: user.role });
      const refreshToken = fastify.jwt.sign(
        { id: user.id, username: user.username, role: user.role, type: 'refresh' },
        { expiresIn: `${refreshExpiresIn}s` },
      );

      return reply.send({ token, refreshToken, user });
    },
  );

  // ------------------------------------------------------------------
  // POST /api/auth/refresh
  // ------------------------------------------------------------------
  fastify.post<{ Body: RefreshBody }>(
    '/api/auth/refresh',
    { config: { rateLimit: { max: rlMax, timeWindow: rlWindow } } },
    async (request, reply) => {
      const { refreshToken } = request.body ?? {};

      if (!refreshToken) {
        return reply.status(400).send({ error: 'refreshToken is required' });
      }

      try {
        const decoded = fastify.jwt.verify<{
          id: string;
          username: string;
          role: string;
          type?: string;
        }>(refreshToken);

        if (decoded.type !== 'refresh') {
          return reply.status(400).send({ error: 'Invalid refresh token' });
        }

        const token = fastify.jwt.sign({
          id: decoded.id,
          username: decoded.username,
          role: decoded.role,
        });

        return reply.send({ token });
      } catch {
        return reply.status(401).send({ error: 'Invalid or expired refresh token' });
      }
    },
  );

  // ------------------------------------------------------------------
  // POST /api/auth/api-key
  // ------------------------------------------------------------------
  fastify.post<{ Body: ApiKeyBody }>(
    '/api/auth/api-key',
    { config: { rateLimit: { max: rlMax, timeWindow: rlWindow } } },
    async (request, reply) => {
      const { apiKey } = request.body ?? {};

      if (!apiKey) {
        return reply.status(400).send({ error: 'apiKey is required' });
      }

      if (enableAuth) {
        const isValidKey = apiKeys.some((k) => {
          try {
            return (
              k.length === apiKey.length &&
              timingSafeEqual(Buffer.from(k), Buffer.from(apiKey))
            );
          } catch {
            return false;
          }
        });
        if (!isValidKey) {
          return reply.status(401).send({ error: 'Invalid API key' });
        }
      }

      const user: AuthUser = { id: 'api-user', username: 'api', role: 'operator' };
      const token = fastify.jwt.sign({ id: user.id, username: user.username, role: user.role });

      return reply.send({ token, user });
    },
  );
}

export const registerAuthPlugin = fp(authPlugin, {
  name: 'tamma-auth',
});
