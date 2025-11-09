# Authentication via Git Provider OAuth

## Overview

Instead of maintaining a separate authentication system, we use **OAuth from the Git provider** (GitHub, GitLab, etc.). This provides:

- 🔐 **Single Sign-On** - Users log in with their Git account
- 🔑 **Automatic Permissions** - Inherit repo access from Git provider
- 🎫 **Personal Access Tokens** - Use user's own token for Git operations
- 👥 **No User Database** - Git provider is the source of truth
- 🔒 **Secure** - OAuth 2.0 standard flow

## Architecture

```
User clicks "Login with GitHub"
         ↓
Redirect to GitHub OAuth
         ↓
User authorizes app
         ↓
GitHub redirects back with code
         ↓
Exchange code for access token
         ↓
Store token in session (KV)
         ↓
Use token to access Git API
         ↓
User's Git permissions apply automatically!
```

---

## OAuth Flow Implementation

### 1. Provider OAuth Configurations

**File: `app/lib/auth/oauth-configs.ts`**

```typescript
export interface OAuthConfig {
  clientId: string;
  clientSecret: string;
  authorizationUrl: string;
  tokenUrl: string;
  userInfoUrl: string;
  scopes: string[];
}

export const OAUTH_CONFIGS: Record<string, OAuthConfig> = {
  github: {
    clientId: "", // Set via env
    clientSecret: "", // Set via env/secret
    authorizationUrl: "https://github.com/login/oauth/authorize",
    tokenUrl: "https://github.com/login/oauth/access_token",
    userInfoUrl: "https://api.github.com/user",
    scopes: ["repo", "read:user", "user:email"],
  },

  gitlab: {
    clientId: "",
    clientSecret: "",
    authorizationUrl: "https://gitlab.com/oauth/authorize",
    tokenUrl: "https://gitlab.com/oauth/token",
    userInfoUrl: "https://gitlab.com/api/v4/user",
    scopes: ["api", "read_user", "read_repository", "write_repository"],
  },

  gitea: {
    clientId: "",
    clientSecret: "",
    authorizationUrl: "", // Set based on instance URL
    tokenUrl: "", // Set based on instance URL
    userInfoUrl: "", // Set based on instance URL
    scopes: ["repo", "user"],
  },
};
```

### 2. OAuth Service

**File: `app/lib/auth/oauth.server.ts`**

```typescript
import type { AppLoadContext } from "react-router";
import { OAUTH_CONFIGS } from "./oauth-configs";

export interface OAuthUser {
  id: string;
  username: string;
  name: string;
  email: string;
  avatarUrl: string;
  provider: string;
  accessToken: string;
}

export class OAuthService {
  constructor(
    private provider: string,
    private config: OAuthConfig,
    private kv: KVNamespace
  ) {}

  /**
   * Generate OAuth authorization URL
   */
  getAuthorizationUrl(state: string, redirectUri: string): string {
    const params = new URLSearchParams({
      client_id: this.config.clientId,
      redirect_uri: redirectUri,
      scope: this.config.scopes.join(" "),
      state,
      response_type: "code",
    });

    return `${this.config.authorizationUrl}?${params}`;
  }

  /**
   * Exchange authorization code for access token
   */
  async exchangeCode(code: string, redirectUri: string): Promise<string> {
    const response = await fetch(this.config.tokenUrl, {
      method: "POST",
      headers: {
        Accept: "application/json",
        "Content-Type": "application/json",
      },
      body: JSON.stringify({
        client_id: this.config.clientId,
        client_secret: this.config.clientSecret,
        code,
        redirect_uri: redirectUri,
        grant_type: "authorization_code",
      }),
    });

    const data = await response.json();

    if (data.error) {
      throw new Error(`OAuth error: ${data.error_description || data.error}`);
    }

    return data.access_token;
  }

  /**
   * Get user info from provider
   */
  async getUserInfo(accessToken: string): Promise<OAuthUser> {
    const response = await fetch(this.config.userInfoUrl, {
      headers: {
        Authorization: `Bearer ${accessToken}`,
        Accept: "application/json",
      },
    });

    const data = await response.json();

    // Parse provider-specific response
    return this.parseUserInfo(data, accessToken);
  }

  /**
   * Create session and store in KV
   */
  async createSession(user: OAuthUser): Promise<string> {
    const sessionId = crypto.randomUUID();
    const sessionData = {
      userId: user.id,
      username: user.username,
      name: user.name,
      email: user.email,
      avatarUrl: user.avatarUrl,
      provider: this.provider,
      accessToken: user.accessToken,
      createdAt: Date.now(),
    };

    // Store in KV with 7 day expiration
    await this.kv.put(
      `session:${sessionId}`,
      JSON.stringify(sessionData),
      { expirationTtl: 60 * 60 * 24 * 7 } // 7 days
    );

    return sessionId;
  }

  /**
   * Get session from KV
   */
  async getSession(sessionId: string): Promise<OAuthUser | null> {
    const data = await this.kv.get(`session:${sessionId}`, "json");

    if (!data) return null;

    return data as OAuthUser;
  }

  /**
   * Delete session
   */
  async deleteSession(sessionId: string): Promise<void> {
    await this.kv.delete(`session:${sessionId}`);
  }

  /**
   * Refresh session (extend expiration)
   */
  async refreshSession(sessionId: string): Promise<void> {
    const session = await this.getSession(sessionId);

    if (session) {
      await this.kv.put(
        `session:${sessionId}`,
        JSON.stringify(session),
        { expirationTtl: 60 * 60 * 24 * 7 }
      );
    }
  }

  /**
   * Parse provider-specific user info
   */
  private parseUserInfo(data: any, accessToken: string): OAuthUser {
    if (this.provider === "github") {
      return {
        id: data.id.toString(),
        username: data.login,
        name: data.name || data.login,
        email: data.email || "",
        avatarUrl: data.avatar_url,
        provider: "github",
        accessToken,
      };
    } else if (this.provider === "gitlab") {
      return {
        id: data.id.toString(),
        username: data.username,
        name: data.name,
        email: data.email,
        avatarUrl: data.avatar_url,
        provider: "gitlab",
        accessToken,
      };
    } else if (this.provider === "gitea") {
      return {
        id: data.id.toString(),
        username: data.login || data.username,
        name: data.full_name || data.login,
        email: data.email,
        avatarUrl: data.avatar_url,
        provider: "gitea",
        accessToken,
      };
    }

    throw new Error(`Unknown provider: ${this.provider}`);
  }
}

/**
 * Create OAuth service from environment
 */
export function createOAuthService(
  provider: string,
  env: { CACHE: KVNamespace; [key: string]: any }
): OAuthService {
  const config = { ...OAUTH_CONFIGS[provider] };

  // Set credentials from environment
  config.clientId = env[`${provider.toUpperCase()}_CLIENT_ID`];
  config.clientSecret = env[`${provider.toUpperCase()}_CLIENT_SECRET`];

  // For self-hosted instances (Gitea/GitLab)
  if (provider === "gitea") {
    const baseUrl = env.GITEA_URL;
    config.authorizationUrl = `${baseUrl}/login/oauth/authorize`;
    config.tokenUrl = `${baseUrl}/login/oauth/access_token`;
    config.userInfoUrl = `${baseUrl}/api/v1/user`;
  }

  return new OAuthService(provider, config, env.CACHE);
}
```

### 3. Authentication Routes

**File: `app/routes/auth.login.tsx`**

```typescript
import type { Route } from "./+types/auth.login";
import { redirect } from "react-router";
import { createOAuthService } from "~/lib/auth/oauth.server";

export async function loader({ request, context }: Route.LoaderArgs) {
  const provider = context.cloudflare.env.GIT_PROVIDER || "github";
  const oauth = createOAuthService(provider, context.cloudflare.env);

  const url = new URL(request.url);
  const redirectUri = `${url.origin}/auth/callback`;
  const state = crypto.randomUUID();

  // Store state in KV for CSRF protection
  await context.cloudflare.env.CACHE.put(
    `oauth_state:${state}`,
    "valid",
    { expirationTtl: 600 } // 10 minutes
  );

  const authUrl = oauth.getAuthorizationUrl(state, redirectUri);

  return redirect(authUrl);
}
```

**File: `app/routes/auth.callback.tsx`**

```typescript
import type { Route } from "./+types/auth.callback";
import { redirect } from "react-router";
import { createOAuthService } from "~/lib/auth/oauth.server";

export async function loader({ request, context }: Route.LoaderArgs) {
  const provider = context.cloudflare.env.GIT_PROVIDER || "github";
  const oauth = createOAuthService(provider, context.cloudflare.env);

  const url = new URL(request.url);
  const code = url.searchParams.get("code");
  const state = url.searchParams.get("state");

  if (!code || !state) {
    throw new Response("Invalid callback", { status: 400 });
  }

  // Verify state (CSRF protection)
  const validState = await context.cloudflare.env.CACHE.get(`oauth_state:${state}`);
  if (!validState) {
    throw new Response("Invalid state", { status: 400 });
  }

  // Delete used state
  await context.cloudflare.env.CACHE.delete(`oauth_state:${state}`);

  // Exchange code for token
  const redirectUri = `${url.origin}/auth/callback`;
  const accessToken = await oauth.exchangeCode(code, redirectUri);

  // Get user info
  const user = await oauth.getUserInfo(accessToken);

  // Create session
  const sessionId = await oauth.createSession(user);

  // Set session cookie
  const headers = new Headers();
  headers.append(
    "Set-Cookie",
    `session=${sessionId}; Path=/; HttpOnly; Secure; SameSite=Lax; Max-Age=${60 * 60 * 24 * 7}`
  );

  return redirect("/", { headers });
}
```

**File: `app/routes/auth.logout.tsx`**

```typescript
import type { Route } from "./+types/auth.logout";
import { redirect } from "react-router";
import { createOAuthService } from "~/lib/auth/oauth.server";
import { getSessionId } from "~/lib/auth/session.server";

export async function loader({ request, context }: Route.LoaderArgs) {
  const provider = context.cloudflare.env.GIT_PROVIDER || "github";
  const oauth = createOAuthService(provider, context.cloudflare.env);

  const sessionId = getSessionId(request);
  if (sessionId) {
    await oauth.deleteSession(sessionId);
  }

  const headers = new Headers();
  headers.append(
    "Set-Cookie",
    "session=; Path=/; HttpOnly; Secure; SameSite=Lax; Max-Age=0"
  );

  return redirect("/", { headers });
}
```

### 4. Session Helpers

**File: `app/lib/auth/session.server.ts`**

```typescript
import type { AppLoadContext } from "react-router";
import { createOAuthService } from "./oauth.server";
import type { OAuthUser } from "./oauth.server";

export function getSessionId(request: Request): string | null {
  const cookie = request.headers.get("Cookie");
  if (!cookie) return null;

  const match = cookie.match(/session=([^;]+)/);
  return match ? match[1] : null;
}

export async function requireAuth(
  request: Request,
  context: AppLoadContext
): Promise<OAuthUser> {
  const sessionId = getSessionId(request);

  if (!sessionId) {
    throw redirect("/auth/login");
  }

  const provider = context.cloudflare.env.GIT_PROVIDER || "github";
  const oauth = createOAuthService(provider, context.cloudflare.env);

  const user = await oauth.getSession(sessionId);

  if (!user) {
    throw redirect("/auth/login");
  }

  // Refresh session on each request
  await oauth.refreshSession(sessionId);

  return user;
}

export async function getUser(
  request: Request,
  context: AppLoadContext
): Promise<OAuthUser | null> {
  const sessionId = getSessionId(request);

  if (!sessionId) return null;

  const provider = context.cloudflare.env.GIT_PROVIDER || "github";
  const oauth = createOAuthService(provider, context.cloudflare.env);

  return await oauth.getSession(sessionId);
}
```

### 5. Using User's Token for Git Operations

**File: `app/lib/docs/loader.server.ts`**

```typescript
import { createGitProvider } from "../git/providers/factory";
import type { OAuthUser } from "../auth/oauth.server";

export class DocumentLoader {
  /**
   * Create Git provider using user's access token
   * This means all operations use user's permissions!
   */
  static forUser(user: OAuthUser, env: any) {
    const git = createGitProvider({
      type: user.provider,
      owner: env.GIT_OWNER,
      repo: env.GIT_REPO,
      projectId: env.GIT_PROJECT_ID,
      token: user.accessToken, // USER'S TOKEN!
      apiUrl: env.GIT_API_URL,
    });

    return new DocumentLoader(git);
  }

  constructor(private git: IGitProvider) {}

  async loadDocument(path: string): Promise<Document> {
    // Uses user's permissions automatically!
    const file = await this.git.getFile(path);
    // ...
  }

  async createSuggestionPR(suggestion: SuggestionData): Promise<PR> {
    // PR created as the logged-in user!
    return await this.git.createPullRequest({
      title: suggestion.title,
      description: suggestion.description,
      sourceBranch: suggestion.branch,
      targetBranch: "main",
    });
  }
}
```

### 6. Protected Route Example

**File: `app/routes/_authenticated.docs.prd.tsx`**

```typescript
import { useLoaderData } from "react-router";
import type { Route } from "./+types/_authenticated.docs.prd";
import { requireAuth } from "~/lib/auth/session.server";
import { DocumentLoader } from "~/lib/docs/loader.server";

export async function loader({ request, context }: Route.LoaderArgs) {
  // Require authentication
  const user = await requireAuth(request, context);

  // Create loader with user's token
  const loader = DocumentLoader.forUser(user, context.cloudflare.env);

  // Load document using user's permissions
  const document = await loader.loadDocument("PRD.md");

  return { user, document };
}

export default function PRDPage() {
  const { user, document } = useLoaderData<typeof loader>();

  return (
    <div>
      <header>
        <p>Logged in as: {user.name}</p>
        <img src={user.avatarUrl} alt={user.name} />
      </header>

      <main>
        {/* Document content */}
      </main>
    </div>
  );
}
```

---

## Permissions & Security

### Automatic Permission Inheritance

```typescript
// If user has read access to repo → Can view docs
// If user has write access to repo → Can create PRs (suggestions)
// If user has admin access to repo → Can manage everything

// No need to manage permissions separately!
// Git provider handles all access control
```

### Role Mapping

```typescript
export function getUserRole(user: OAuthUser, repoPermissions: any): Role {
  // Map Git permissions to app roles
  if (repoPermissions.admin) return "admin";
  if (repoPermissions.push) return "reviewer"; // Can approve suggestions
  if (repoPermissions.pull) return "viewer";   // Read-only
  return "viewer";
}
```

### Security Benefits

✅ **No Password Storage** - OAuth only
✅ **Token Rotation** - Refresh tokens when needed
✅ **Scope Limiting** - Request minimum permissions
✅ **Revocable** - Users can revoke access anytime
✅ **Audit Trail** - Git provider logs all actions

---

## Configuration

**Environment Variables:**

```jsonc
// wrangler.jsonc
{
  "vars": {
    "GIT_PROVIDER": "github",
    "GIT_OWNER": "meywd",
    "GIT_REPO": "tamma"
  }
}
```

**Secrets:**

```bash
# GitHub OAuth App
pnpm wrangler secret put GITHUB_CLIENT_ID
pnpm wrangler secret put GITHUB_CLIENT_SECRET

# GitLab OAuth App
pnpm wrangler secret put GITLAB_CLIENT_ID
pnpm wrangler secret put GITLAB_CLIENT_SECRET

# Gitea OAuth App
pnpm wrangler secret put GITEA_CLIENT_ID
pnpm wrangler secret put GITEA_CLIENT_SECRET
pnpm wrangler secret put GITEA_URL
```

---

## Setting Up OAuth Apps

### GitHub OAuth App

1. Go to **Settings** → **Developer settings** → **OAuth Apps**
2. Click **New OAuth App**
3. Set:
   - **Application name**: Tamma Doc Review
   - **Homepage URL**: `https://doc-review.yourcompany.com`
   - **Authorization callback URL**: `https://doc-review.yourcompany.com/auth/callback`
4. Copy **Client ID** and **Client Secret**

### GitLab OAuth App

1. Go to **User Settings** → **Applications**
2. Create new application:
   - **Name**: Tamma Doc Review
   - **Redirect URI**: `https://doc-review.yourcompany.com/auth/callback`
   - **Scopes**: `api`, `read_user`, `read_repository`, `write_repository`
3. Copy **Application ID** and **Secret**

### Gitea OAuth App

1. Go to **Settings** → **Applications** → **Manage OAuth2 Applications**
2. Create new application:
   - **Application Name**: Tamma Doc Review
   - **Redirect URI**: `https://doc-review.yourcompany.com/auth/callback`
3. Copy **Client ID** and **Client Secret**

---

## User Experience

### Login Flow

```
User visits app
     ↓
Not authenticated → Redirect to /auth/login
     ↓
Click "Login with GitHub"
     ↓
Redirected to github.com
     ↓
User authorizes app
     ↓
Redirected back to app
     ↓
Session created
     ↓
User sees documentation!
```

### UI Components

**File: `app/components/auth/LoginButton.tsx`**

```typescript
export function LoginButton({ provider }: { provider: string }) {
  const providerNames = {
    github: "GitHub",
    gitlab: "GitLab",
    gitea: "Gitea",
  };

  const providerIcons = {
    github: "🐙",
    gitlab: "🦊",
    gitea: "🍵",
  };

  return (
    <a
      href="/auth/login"
      className="btn btn-primary"
    >
      {providerIcons[provider]} Login with {providerNames[provider]}
    </a>
  );
}
```

**File: `app/components/auth/UserMenu.tsx`**

```typescript
import { useLoaderData } from "react-router";

export function UserMenu() {
  const { user } = useLoaderData<{ user: OAuthUser }>();

  return (
    <div className="user-menu">
      <img src={user.avatarUrl} alt={user.name} className="avatar" />
      <span>{user.name}</span>
      <a href="/auth/logout">Logout</a>
    </div>
  );
}
```

---

## Benefits of Git Provider OAuth

### 1. **Single Sign-On**
- Users already have Git accounts
- No separate registration needed
- Familiar login flow

### 2. **Automatic Permissions**
- Repo access = App access
- No manual permission management
- Updates automatically when Git permissions change

### 3. **User's Identity**
- PRs created as actual user
- Comments attributed correctly
- Git history shows real authors

### 4. **Security**
- OAuth 2.0 standard
- No password handling
- Tokens can be revoked
- Scope-limited access

### 5. **No User Database**
- Git provider is source of truth
- No user sync needed
- Always up-to-date

---

## Advanced Features

### Token Refresh

```typescript
async function refreshTokenIfNeeded(user: OAuthUser): Promise<string> {
  // Check if token is still valid
  const valid = await validateToken(user.accessToken);

  if (!valid) {
    // Refresh token (if provider supports it)
    const newToken = await refreshOAuthToken(user.provider, user.refreshToken);
    return newToken;
  }

  return user.accessToken;
}
```

### Multi-Provider Support

```typescript
// Support multiple Git providers in same app
const user = await requireAuth(request, context);

if (user.provider === "github") {
  // GitHub-specific logic
} else if (user.provider === "gitlab") {
  // GitLab-specific logic
}
```

### SSO for Organizations

```typescript
// Restrict to specific organization
export async function requireOrgMember(user: OAuthUser, org: string) {
  const git = createGitProvider({
    type: user.provider,
    token: user.accessToken,
  });

  const orgs = await git.getUserOrganizations();

  if (!orgs.some(o => o.login === org)) {
    throw new Response("Not authorized", { status: 403 });
  }
}
```

---

## Conclusion

Using **Git provider OAuth** gives you:

✅ **Zero user management** - Git provider handles it
✅ **Automatic permissions** - Inherit from repo access
✅ **User's identity** - PRs/comments as actual user
✅ **Single sign-on** - Familiar OAuth flow
✅ **Secure** - OAuth 2.0 standard
✅ **Revocable** - Users control access

**This is the cleanest, most secure approach for a Git-based documentation system!** 🔐
