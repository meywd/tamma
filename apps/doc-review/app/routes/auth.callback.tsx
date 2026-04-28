import { redirect } from 'react-router';
import type { LoaderFunctionArgs } from 'react-router';
import { createOAuthService } from '~/lib/auth/oauth.server';
import { syncUserRecord } from '~/lib/db/users.server';
import { resolveEnv } from '~/lib/auth/session.server';

export async function loader({ request, context }: LoaderFunctionArgs) {
  const env = resolveEnv(context);
  const provider = env.GIT_PROVIDER || 'github';
  const oauth = createOAuthService(provider, env);

  const url = new URL(request.url);
  const code = url.searchParams.get('code');
  const state = url.searchParams.get('state');

  if (!code || !state) {
    throw new Response('Invalid callback', { status: 400 });
  }

  // Verify state (CSRF protection) - skip if KV not available
  if (env.CACHE) {
    const validState = await env.CACHE.get(`oauth_state:${state}`);
    if (!validState) {
      throw new Response('Invalid state', { status: 400 });
    }

    // Delete used state
    await env.CACHE.delete(`oauth_state:${state}`);
  }

  // Exchange code for token
  const redirectUri = `${url.origin}/auth/callback`;
  const accessToken = await oauth.exchangeCode(code, redirectUri);

  // Get user info
  const oauthUser = await oauth.getUserInfo(accessToken);

  // Sync user to database and get role - pass only DB binding
  const dbEnv = context.cloudflare?.env ?? context.env;

  // Convert OAuthUser to UserWithRole for syncUserRecord
  const userWithRole = {
    id: oauthUser.id,
    username: oauthUser.username,
    name: oauthUser.name,
    email: oauthUser.email,
    avatarUrl: oauthUser.avatarUrl,
    role: oauthUser.role || 'reviewer', // Provide default role
  };

  const dbUser = await syncUserRecord({ DB: (dbEnv)?.DB }, userWithRole);
  if (dbUser && 'role' in dbUser) {
    oauthUser.role = dbUser.role;
  }

  // Create session
  const sessionId = await oauth.createSession(oauthUser);

  // Set session cookie
  const headers = new Headers();
  headers.append(
    'Set-Cookie',
    `session=${sessionId}; Path=/; HttpOnly; Secure; SameSite=Lax; Max-Age=${60 * 60 * 24 * 7}`
  );

  return redirect('/', { headers });
}
