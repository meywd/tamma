/**
 * GET /api/auth/me — Current User Identity Endpoint (Story 16.4)
 *
 * Returns the authenticated user's identity from the JWT session cookie.
 * Used by the unified navigation bar to display user info and role-based
 * links (admin visibility) across all Tamma subdomains.
 *
 * Response: { user: { id, username, githubId, role } }
 * Error:    { error: "Not authenticated" } (401)
 *
 * The tamma_session cookie has domain=.tamma.dev, so it is sent automatically
 * on cross-subdomain requests from elsa.tamma.dev, logs.tamma.dev, etc.
 * CORS is configured at the app level to allow these origins with credentials.
 *
 * Note: This endpoint is also registered as part of registerGitHubOAuthRoutes().
 * This standalone version can be registered independently when GitHub OAuth
 * is not configured (e.g., dev mode with manual JWT injection).
 */

import type { FastifyInstance, FastifyRequest, FastifyReply } from 'fastify';

export interface AuthMeUser {
  id: string;
  username: string;
  githubId: number;
  role: string;
}

export interface AuthMeRouteOptions {
  /** JWT secret for verifying tokens. */
  jwtSecret: string;
}

/**
 * Register a standalone GET /api/auth/me route.
 *
 * Requires @fastify/jwt and @fastify/cookie to be registered on the instance
 * (or a parent instance). If registerGitHubOAuthRoutes() is already used,
 * this registration is not needed — the endpoint is included there.
 */
export async function registerAuthMeRoute(
  app: FastifyInstance,
  options: AuthMeRouteOptions,
): Promise<void> {
  // Register JWT plugin if not already registered
  if (!app.hasDecorator('jwt')) {
    await app.register(await import('@fastify/jwt').then((m) => m.default ?? m), {
      secret: options.jwtSecret,
      cookie: { cookieName: 'tamma_session', signed: false },
    });
  }

  // Register cookie plugin if not already registered
  if (!app.hasDecorator('parseCookie')) {
    await app.register(await import('@fastify/cookie').then((m) => m.default ?? m));
  }

  app.get(
    '/api/auth/me',
    async (request: FastifyRequest, reply: FastifyReply) => {
      try {
        const decoded = await request.jwtVerify<AuthMeUser>();
        return reply.send({ user: decoded });
      } catch {
        return reply.status(401).send({ error: 'Not authenticated' });
      }
    },
  );
}
