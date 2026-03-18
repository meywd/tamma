/**
 * GitHub OAuth Login Routes
 *
 * Implements the OAuth 2.0 web application flow for GitHub:
 *   GET  /api/auth/github        → redirect to GitHub authorization
 *   GET  /api/auth/github/callback → exchange code for token, create session
 *
 * Users are identified by their GitHub ID and linked to installations.
 * On successful auth, a JWT is issued and set as an HTTP-only cookie.
 */

import type { FastifyInstance, FastifyRequest, FastifyReply } from 'fastify';
import type { IUserStore } from '../../persistence/user-store.js';
import type { IGitHubInstallationStore } from '../../persistence/installation-store.js';

export interface GitHubOAuthOptions {
  /** GitHub OAuth App client ID (NOT the GitHub App ID). */
  clientId: string;
  /** GitHub OAuth App client secret. */
  clientSecret: string;
  /** JWT secret for signing tokens. */
  jwtSecret: string;
  /** User persistence store. */
  userStore: IUserStore;
  /** Installation store — to check user's installation access. */
  installationStore: IGitHubInstallationStore;
  /** Where to redirect after successful login. */
  dashboardUrl: string;
  /** Base URL for the API (used to build callback URL). */
  apiBaseUrl: string;
  /** Token expiry in seconds. Default: 86400 (24 hours). */
  tokenExpiresIn?: number;
}

export async function registerGitHubOAuthRoutes(
  app: FastifyInstance,
  options: GitHubOAuthOptions,
): Promise<void> {
  const {
    clientId,
    clientSecret,
    jwtSecret,
    userStore,
    installationStore,
    dashboardUrl,
    apiBaseUrl,
    tokenExpiresIn = 86400,
  } = options;

  // Rate limiting for auth routes
  await app.register((await import('@fastify/rate-limit')).default, {
    max: 60,
    timeWindow: '1 minute',
  });

  // Register JWT plugin for this scope
  await app.register(await import('@fastify/jwt').then((m) => m.default ?? m), {
    secret: jwtSecret,
    sign: { expiresIn: `${tokenExpiresIn}s` },
    cookie: { cookieName: 'tamma_session', signed: false },
  });

  // Register cookie plugin
  await app.register(await import('@fastify/cookie').then((m) => m.default ?? m));

  // -------------------------------------------------------------------
  // GET /api/auth/github — redirect to GitHub authorization
  // -------------------------------------------------------------------
  app.get('/api/auth/github', async (_request: FastifyRequest, reply: FastifyReply) => {
    const callbackUrl = `${apiBaseUrl}/api/auth/github/callback`;
    const scope = 'read:user user:email';
    const githubUrl = `https://github.com/login/oauth/authorize?client_id=${clientId}&redirect_uri=${encodeURIComponent(callbackUrl)}&scope=${encodeURIComponent(scope)}`;
    return reply.redirect(githubUrl);
  });

  // -------------------------------------------------------------------
  // GET /api/auth/github/callback — exchange code, create/update user, issue JWT
  // -------------------------------------------------------------------
  app.get<{
    Querystring: { code?: string; error?: string };
  }>('/api/auth/github/callback', async (request, reply) => {
    const { code, error } = request.query;

    if (error || !code) {
      return reply.redirect(`${dashboardUrl}/login?error=${encodeURIComponent(error ?? 'missing_code')}`);
    }

    // Exchange code for access token
    let accessToken: string;
    try {
      const tokenResponse = await fetch('https://github.com/login/oauth/access_token', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Accept: 'application/json',
        },
        body: JSON.stringify({
          client_id: clientId,
          client_secret: clientSecret,
          code,
        }),
      });

      const tokenData = (await tokenResponse.json()) as { access_token?: string; error?: string };
      if (!tokenData.access_token) {
        return reply.redirect(`${dashboardUrl}/login?error=token_exchange_failed`);
      }
      accessToken = tokenData.access_token;
    } catch {
      return reply.redirect(`${dashboardUrl}/login?error=github_unavailable`);
    }

    // Fetch GitHub user profile
    let githubUser: { id: number; login: string; email: string | null };
    try {
      const userResponse = await fetch('https://api.github.com/user', {
        headers: { Authorization: `Bearer ${accessToken}`, Accept: 'application/json' },
      });
      githubUser = (await userResponse.json()) as typeof githubUser;
    } catch {
      return reply.redirect(`${dashboardUrl}/login?error=github_user_fetch_failed`);
    }

    // Upsert user in our store
    const user = await userStore.upsertUser({
      githubId: githubUser.id,
      githubLogin: githubUser.login,
      email: githubUser.email,
      role: 'member',
    });

    // Check if user has access to any installation
    const installations = await userStore.getUserInstallations(user.id);
    if (installations.length === 0) {
      // Auto-link: check if any installation matches the user's GitHub orgs
      // For now, link to all active installations (first-user-gets-access bootstrap)
      const allInstallations = await installationStore.listActiveInstallations();
      for (const inst of allInstallations) {
        await userStore.linkUserToInstallation(user.id, inst.installationId, 'member');
      }
    }

    // Issue JWT
    const token = app.jwt.sign({
      id: user.id,
      username: user.githubLogin,
      githubId: user.githubId,
      role: user.role,
    });

    // Set cookie and redirect to dashboard
    return reply
      .setCookie('tamma_session', token, {
        path: '/',
        httpOnly: true,
        secure: true, // Cloudflare handles TLS
        sameSite: 'lax',
        maxAge: tokenExpiresIn,
        domain: '.tamma.dev', // Shared across subdomains
      })
      .redirect(dashboardUrl);
  });

  // -------------------------------------------------------------------
  // GET /api/auth/me — return current user from JWT cookie
  // -------------------------------------------------------------------
  app.get('/api/auth/me', { config: { rateLimit: { max: 60, timeWindow: '1 minute' } } }, async (request: FastifyRequest, reply: FastifyReply) => { // lgtm[js/missing-rate-limiting]
    try {
      const decoded = await request.jwtVerify<{
        id: string;
        username: string;
        githubId: number;
        role: string;
      }>();
      return reply.send({ user: decoded });
    } catch {
      return reply.status(401).send({ error: 'Not authenticated' });
    }
  });

  // -------------------------------------------------------------------
  // POST /api/auth/logout — clear session cookie
  // -------------------------------------------------------------------
  app.post('/api/auth/logout', async (_request: FastifyRequest, reply: FastifyReply) => {
    return reply
      .clearCookie('tamma_session', {
        path: '/',
        domain: '.tamma.dev',
      })
      .send({ ok: true });
  });
}
