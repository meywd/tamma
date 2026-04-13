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
import type { IInviteStore } from '../../persistence/invite-store.js';

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
  /** Invite store — to process invite tokens during OAuth callback. */
  inviteStore?: IInviteStore;
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
    inviteStore,
    dashboardUrl,
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
  // Accepts optional ?rd= param for post-login redirect (e.g. elsa.tamma.dev)
  // -------------------------------------------------------------------
  app.get<{
    Querystring: { rd?: string; invite?: string };
  }>('/api/auth/github', async (request: FastifyRequest<{ Querystring: { rd?: string; invite?: string } }>, reply: FastifyReply) => {
    const callbackUrl = `${dashboardUrl}/oauth2/callback`;
    const scope = 'read:user user:email';

    // Encode redirect destination and optional invite token in OAuth state param.
    // Sanitize the URL upfront so only reconstructed (non-tainted) values are stored.
    const rd = request.query.rd;
    const invite = request.query.invite;
    const sanitizedRd = rd ? sanitizeRedirectUrl(rd) : null;
    const statePayload: Record<string, string> = {};
    if (sanitizedRd) {
      statePayload['rd'] = sanitizedRd;
    }
    if (invite) {
      statePayload['invite'] = invite;
    }
    const state = Buffer.from(JSON.stringify(statePayload)).toString('base64url');

    const githubUrl = `https://github.com/login/oauth/authorize?client_id=${clientId}&redirect_uri=${encodeURIComponent(callbackUrl)}&scope=${encodeURIComponent(scope)}&state=${encodeURIComponent(state)}`;
    return reply.redirect(githubUrl);
  });

  // -------------------------------------------------------------------
  // GET /api/auth/github/callback — exchange code, create/update user, issue JWT
  // -------------------------------------------------------------------
  app.get<{
    Querystring: { code?: string; error?: string; state?: string };
  }>('/api/auth/github/callback', async (request, reply) => {
    const { code, error, state } = request.query;

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

    // Parse OAuth state to extract redirect and invite token
    let redirectTo: string | null = null;
    let inviteToken: string | null = null;
    if (state) {
      try {
        const parsed = JSON.parse(Buffer.from(state, 'base64url').toString()) as { rd?: string; invite?: string };
        if (parsed.rd) {
          redirectTo = sanitizeRedirectUrl(parsed.rd);
        }
        if (parsed.invite) {
          inviteToken = parsed.invite;
        }
      } catch {
        // Invalid state — fall back to defaults
      }
    }

    // Determine role from invite token if present
    let assignedRole: 'owner' | 'admin' | 'member' = 'member';
    if (inviteToken && inviteStore) {
      const invite = await inviteStore.getInviteByToken(inviteToken);
      if (invite && invite.acceptedAt === null && invite.expiresAt > new Date().toISOString()) {
        assignedRole = invite.role;
        await inviteStore.acceptInvite(invite.id);
        request.log.info({
          event: 'USER.INVITE_ACCEPTED.SUCCESS',
          inviteId: invite.id,
          role: invite.role,
          githubLogin: githubUser.login,
        }, 'Invite accepted during OAuth callback');
      }
    }

    // Upsert user in our store
    const user = await userStore.upsertUser({
      githubId: githubUser.id,
      githubLogin: githubUser.login,
      email: githubUser.email,
      role: assignedRole,
    });

    // If invite assigned a non-default role and user already existed with 'member',
    // explicitly promote them (upsert may not change role on conflict)
    if (assignedRole !== 'member' && user.role !== assignedRole) {
      await userStore.updateUserRole(user.id, assignedRole);
      user.role = assignedRole;
    }

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

    // Single cookie on parent domain — covers all *.tamma.dev subdomains.
    // Browsers reject Set-Cookie for domains that don't match the current origin,
    // so per-subdomain cookies from api.tamma.dev would be silently dropped.
    reply.setCookie('tamma_session', token, {
      path: '/',
      httpOnly: true,
      secure: true,
      sameSite: 'lax' as const,
      maxAge: tokenExpiresIn,
      domain: '.tamma.dev',
    });

    // Use the sanitized URL if valid, otherwise fall back to the server-controlled dashboardUrl
    return reply.redirect(redirectTo ?? dashboardUrl);
  });

  // -------------------------------------------------------------------
  // GET /api/auth/me — return current user from JWT cookie
  // -------------------------------------------------------------------
  app.get('/api/auth/me', { config: { rateLimit: { max: 60, timeWindow: '1 minute' } } }, async (request: FastifyRequest, reply: FastifyReply) => {
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
    reply.clearCookie('tamma_session', { path: '/', domain: '.tamma.dev' });
    return reply.send({ ok: true });
  });
}

/**
 * Sanitize a redirect URL by reconstructing it from parsed components.
 * This breaks the taint chain for static analysis tools (e.g. CodeQL) by
 * ensuring the returned string is constructed from validated parts rather
 * than being the original user-provided value passed through.
 *
 * Returns `null` if the URL is not a valid tamma.dev redirect target.
 */
function sanitizeRedirectUrl(url: string): string | null {
  // Allow relative paths — reconstruct to ensure no protocol-relative tricks
  if (url.startsWith('/')) {
    // Reconstruct: only keep pathname + search + hash, strip any authority
    try {
      // Use a dummy base to parse the relative URL safely
      const parsed = new URL(url, 'https://placeholder.invalid');
      return parsed.pathname + parsed.search + parsed.hash;
    } catch {
      return null;
    }
  }

  try {
    const parsed = new URL(url);
    // Must be HTTPS and on *.tamma.dev
    if (parsed.protocol !== 'https:') return null;
    if (parsed.hostname !== 'tamma.dev' && !parsed.hostname.endsWith('.tamma.dev')) return null;

    // Reconstruct from validated components — this is a new string, not the
    // user-provided value, so CodeQL will not flag it as tainted.
    return `https://${parsed.hostname}${parsed.pathname}${parsed.search}${parsed.hash}`;
  } catch {
    return null;
  }
}
