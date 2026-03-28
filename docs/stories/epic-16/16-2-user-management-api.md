# Story 16.2: User Management REST API

Status: ready-for-dev

## Story

As an **admin user**,
I want REST API endpoints to list, invite, update, and remove users along with their roles and API keys,
so that I can manage who has access to the Tamma platform without direct database access.

## Acceptance Criteria

1. `GET /api/users` returns a paginated list of all users (admin/owner only) with fields: id, githubLogin, email, role, createdAt, updatedAt, lastActiveAt
2. `GET /api/users/:id` returns a single user's details including their installations and API key prefixes (admin/owner only; members can GET their own)
3. `POST /api/users/invite` creates an invitation record with a specified role and generates a one-time invite link (admin/owner only)
4. `PUT /api/users/:id/role` updates a user's role (owner only for promoting to admin; admin can set member role)
5. `DELETE /api/users/:id` soft-deletes a user — sets a `deleted_at` timestamp, revokes all API keys, removes installation links (owner only)
6. `POST /api/users/:id/api-keys` generates a new API key for the user, returns the full key once, stores only the hash (admin/owner for any user; members for themselves)
7. `GET /api/users/:id/api-keys` lists API keys for a user with prefix, created date, last used date (no full key) (admin/owner for any user; members for themselves)
8. `DELETE /api/users/:id/api-keys/:keyId` revokes an API key (admin/owner for any user; members for their own)
9. All mutations emit audit events to the event store with type pattern `USER.{ACTION}.SUCCESS|FAILED`
10. Rate limiting: 30 requests/minute for user management endpoints
11. Input validation: role must be one of `owner`, `admin`, `member`; email must be valid format if provided
12. Invite flow: admin creates invite -> system stores invite with role + expiry (72h) -> invite link redirects to GitHub OAuth -> on callback, user is created with the invited role instead of default `member`

## Technical Context

### Existing Components

- **User model**: `packages/api/src/persistence/user-store.ts` defines `User`, `UserInstallation`, `IUserStore`
- **PgUserStore**: `packages/api/src/persistence/pg-user-store.ts` implements PostgreSQL persistence
- **Users table**: `database/migrations/002_users.sql` — has `id`, `github_id`, `github_login`, `email`, `role`, `created_at`, `updated_at`
- **API keys**: `database/migrations/003_api_keys.sql` — adds `api_key_hash`, `api_key_prefix`, `api_key_encrypted` to `github_installations`
- **API key utils**: `packages/api/src/auth/api-key.ts` — `generateApiKey()`, `hashApiKey()`, `getApiKeyPrefix()`
- **GitHub OAuth**: `packages/api/src/routes/auth/github-oauth.ts` — current OAuth callback with `upsertUser()`

### Current Limitations

- `IUserStore` only has `upsertUser`, `getUser`, `getUserByGithubId`, `linkUserToInstallation`, `getUserInstallations`, `getUserSettings`, `updateUserSettings`
- No `listUsers`, `deleteUser`, `updateUserRole` methods
- API keys are on the `github_installations` table, not per-user
- No invite mechanism

### Files to Create

| File | Purpose |
|------|---------|
| `database/migrations/005_user_api_keys.sql` | Per-user API keys table |
| `database/migrations/006_user_invites.sql` | User invitations table |
| `database/migrations/007_users_soft_delete.sql` | Add `deleted_at` and `last_active_at` columns to users |
| `packages/api/src/routes/users/index.ts` | User management route registration |
| `packages/api/src/routes/users/user-routes.ts` | User CRUD route handlers |
| `packages/api/src/routes/users/api-key-routes.ts` | Per-user API key route handlers |
| `packages/api/src/routes/users/invite-routes.ts` | Invite flow route handlers |
| `packages/api/src/persistence/user-api-key-store.ts` | IUserApiKeyStore interface + PgUserApiKeyStore |
| `packages/api/src/persistence/invite-store.ts` | IInviteStore interface + PgInviteStore |
| `packages/api/src/middleware/require-role.ts` | Role-checking middleware for route guards |

### Files to Modify

| File | Change |
|------|--------|
| `packages/api/src/persistence/user-store.ts` | Add `listUsers`, `deleteUser`, `updateUserRole`, `updateLastActive` to `IUserStore` |
| `packages/api/src/persistence/pg-user-store.ts` | Implement new methods |
| `packages/api/src/routes/auth/github-oauth.ts` | Check for pending invite on OAuth callback, apply invited role |
| `packages/api/src/serve.ts` (or route registration) | Register new user management routes |

## Implementation Plan

### Step 1: Database Migrations

**005_user_api_keys.sql:**

```sql
CREATE TABLE IF NOT EXISTS user_api_keys (
  id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id       UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  key_hash      TEXT NOT NULL UNIQUE,
  key_prefix    TEXT NOT NULL,
  label         TEXT NOT NULL DEFAULT 'default',
  last_used_at  TIMESTAMPTZ,
  created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  revoked_at    TIMESTAMPTZ
);

CREATE INDEX idx_user_api_keys_user_id ON user_api_keys (user_id);
CREATE INDEX idx_user_api_keys_key_hash ON user_api_keys (key_hash);
```

**006_user_invites.sql:**

```sql
CREATE TABLE IF NOT EXISTS user_invites (
  id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  email         TEXT,
  role          TEXT NOT NULL DEFAULT 'member' CHECK (role IN ('owner', 'admin', 'member')),
  invite_token  TEXT NOT NULL UNIQUE,
  invited_by    UUID NOT NULL REFERENCES users(id),
  expires_at    TIMESTAMPTZ NOT NULL,
  accepted_at   TIMESTAMPTZ,
  created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_user_invites_token ON user_invites (invite_token);
```

**007_users_soft_delete.sql:**

```sql
ALTER TABLE users ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ;
ALTER TABLE users ADD COLUMN IF NOT EXISTS last_active_at TIMESTAMPTZ;

CREATE INDEX idx_users_deleted_at ON users (deleted_at) WHERE deleted_at IS NULL;
```

### Step 2: Extend IUserStore

Add these methods to the interface:

```typescript
interface IUserStore {
  // ... existing methods ...

  /** List users with pagination, excluding soft-deleted. */
  listUsers(options: { limit: number; offset: number }): Promise<{ users: User[]; total: number }>;

  /** Soft-delete a user. */
  deleteUser(id: string): Promise<void>;

  /** Update a user's role. */
  updateUserRole(id: string, role: 'owner' | 'admin' | 'member'): Promise<User>;

  /** Update last_active_at timestamp. */
  updateLastActive(id: string): Promise<void>;
}
```

### Step 3: Role Middleware

```typescript
// packages/api/src/middleware/require-role.ts
import type { FastifyRequest, FastifyReply } from 'fastify';

type Role = 'owner' | 'admin' | 'member';

const ROLE_HIERARCHY: Record<Role, number> = {
  member: 0,
  admin: 1,
  owner: 2,
};

export function requireRole(minimumRole: Role) {
  return async (request: FastifyRequest, reply: FastifyReply): Promise<void> => {
    const user = (request as FastifyRequest & { authUser?: { role: string } }).authUser;

    if (!user) {
      reply.status(401).send({ error: 'Not authenticated' });
      return;
    }

    const userLevel = ROLE_HIERARCHY[user.role as Role] ?? -1;
    const requiredLevel = ROLE_HIERARCHY[minimumRole];

    if (userLevel < requiredLevel) {
      reply.status(403).send({ error: `Requires ${minimumRole} role or higher` });
      return;
    }
  };
}

export function requireSelfOrRole(minimumRole: Role) {
  return async (request: FastifyRequest, reply: FastifyReply): Promise<void> => {
    const user = (request as FastifyRequest & { authUser?: { id: string; role: string } }).authUser;
    const params = request.params as { id?: string };

    if (!user) {
      reply.status(401).send({ error: 'Not authenticated' });
      return;
    }

    // Allow if user is accessing their own resource
    if (params.id === user.id) {
      return;
    }

    // Otherwise require minimum role
    const userLevel = ROLE_HIERARCHY[user.role as Role] ?? -1;
    const requiredLevel = ROLE_HIERARCHY[minimumRole];

    if (userLevel < requiredLevel) {
      reply.status(403).send({ error: `Requires ${minimumRole} role or access to own resource` });
      return;
    }
  };
}
```

### Step 4: User Routes

```typescript
// packages/api/src/routes/users/user-routes.ts
import type { FastifyInstance } from 'fastify';
import type { IUserStore } from '../../persistence/user-store.js';
import { requireRole } from '../../middleware/require-role.js';

export async function registerUserRoutes(
  app: FastifyInstance,
  userStore: IUserStore,
): Promise<void> {
  // GET /api/users — list all users (admin+)
  app.get('/api/users', {
    preHandler: [requireRole('admin')],
  }, async (request, reply) => {
    const query = request.query as { limit?: string; offset?: string };
    const limit = Math.min(parseInt(query.limit ?? '50', 10), 100);
    const offset = parseInt(query.offset ?? '0', 10);

    const result = await userStore.listUsers({ limit, offset });
    return reply.send(result);
  });

  // GET /api/users/:id — get single user (admin+ or self)
  app.get('/api/users/:id', {
    preHandler: [requireSelfOrRole('admin')],
  }, async (request, reply) => {
    const { id } = request.params as { id: string };
    const user = await userStore.getUser(id);

    if (!user) {
      return reply.status(404).send({ error: 'User not found' });
    }

    const installations = await userStore.getUserInstallations(id);
    return reply.send({ user, installations });
  });

  // PUT /api/users/:id/role — update role (owner only for admin promotion)
  app.put('/api/users/:id/role', {
    preHandler: [requireRole('admin')],
  }, async (request, reply) => {
    const { id } = request.params as { id: string };
    const { role } = request.body as { role: string };
    const authUser = (request as any).authUser;

    if (!['owner', 'admin', 'member'].includes(role)) {
      return reply.status(400).send({ error: 'Invalid role' });
    }

    // Only owners can promote to admin or owner
    if ((role === 'admin' || role === 'owner') && authUser.role !== 'owner') {
      return reply.status(403).send({ error: 'Only owners can promote to admin or owner' });
    }

    // Cannot demote yourself
    if (id === authUser.id) {
      return reply.status(400).send({ error: 'Cannot change your own role' });
    }

    const updated = await userStore.updateUserRole(id, role as 'owner' | 'admin' | 'member');

    // Emit audit event
    // await eventStore.append({ type: 'USER.ROLE_CHANGED.SUCCESS', ... });

    return reply.send({ user: updated });
  });

  // DELETE /api/users/:id — soft delete (owner only)
  app.delete('/api/users/:id', {
    preHandler: [requireRole('owner')],
  }, async (request, reply) => {
    const { id } = request.params as { id: string };
    const authUser = (request as any).authUser;

    if (id === authUser.id) {
      return reply.status(400).send({ error: 'Cannot delete yourself' });
    }

    await userStore.deleteUser(id);

    // Emit audit event
    // await eventStore.append({ type: 'USER.DELETED.SUCCESS', ... });

    return reply.send({ ok: true });
  });
}
```

### Step 5: Per-User API Key Routes

```typescript
// packages/api/src/routes/users/api-key-routes.ts
import type { FastifyInstance } from 'fastify';
import type { IUserApiKeyStore } from '../../persistence/user-api-key-store.js';
import { generateApiKey, hashApiKey, getApiKeyPrefix } from '../../auth/api-key.js';
import { requireSelfOrRole } from '../../middleware/require-role.js';

export async function registerApiKeyRoutes(
  app: FastifyInstance,
  apiKeyStore: IUserApiKeyStore,
): Promise<void> {
  // POST /api/users/:id/api-keys — generate new key
  app.post('/api/users/:id/api-keys', {
    preHandler: [requireSelfOrRole('admin')],
  }, async (request, reply) => {
    const { id } = request.params as { id: string };
    const { label } = request.body as { label?: string };

    const rawKey = generateApiKey();
    const keyHash = hashApiKey(rawKey);
    const keyPrefix = getApiKeyPrefix(rawKey);

    const record = await apiKeyStore.createApiKey({
      userId: id,
      keyHash,
      keyPrefix,
      label: label ?? 'default',
    });

    // Return the full key ONCE — it cannot be retrieved again
    return reply.status(201).send({
      id: record.id,
      key: rawKey,
      prefix: keyPrefix,
      label: record.label,
      createdAt: record.createdAt,
    });
  });

  // GET /api/users/:id/api-keys — list keys (no full key)
  app.get('/api/users/:id/api-keys', {
    preHandler: [requireSelfOrRole('admin')],
  }, async (request, reply) => {
    const { id } = request.params as { id: string };
    const keys = await apiKeyStore.listApiKeys(id);
    return reply.send({ apiKeys: keys });
  });

  // DELETE /api/users/:id/api-keys/:keyId — revoke key
  app.delete('/api/users/:id/api-keys/:keyId', {
    preHandler: [requireSelfOrRole('admin')],
  }, async (request, reply) => {
    const { id, keyId } = request.params as { id: string; keyId: string };
    await apiKeyStore.revokeApiKey(keyId, id);
    return reply.send({ ok: true });
  });
}
```

### Step 6: Invite Flow

```typescript
// packages/api/src/routes/users/invite-routes.ts
import type { FastifyInstance } from 'fastify';
import type { IInviteStore } from '../../persistence/invite-store.js';
import { requireRole } from '../../middleware/require-role.js';
import { randomBytes } from 'node:crypto';

export async function registerInviteRoutes(
  app: FastifyInstance,
  inviteStore: IInviteStore,
  dashboardUrl: string,
): Promise<void> {
  // POST /api/users/invite — create invitation (admin+)
  app.post('/api/users/invite', {
    preHandler: [requireRole('admin')],
  }, async (request, reply) => {
    const { email, role } = request.body as { email?: string; role?: string };
    const authUser = (request as any).authUser;

    const inviteRole = role ?? 'member';
    if (!['owner', 'admin', 'member'].includes(inviteRole)) {
      return reply.status(400).send({ error: 'Invalid role' });
    }

    // Only owners can invite admins/owners
    if ((inviteRole === 'admin' || inviteRole === 'owner') && authUser.role !== 'owner') {
      return reply.status(403).send({ error: 'Only owners can invite admin/owner roles' });
    }

    const token = randomBytes(32).toString('base64url');
    const expiresAt = new Date(Date.now() + 72 * 60 * 60 * 1000).toISOString(); // 72 hours

    const invite = await inviteStore.createInvite({
      email: email ?? null,
      role: inviteRole as 'owner' | 'admin' | 'member',
      inviteToken: token,
      invitedBy: authUser.id,
      expiresAt,
    });

    const inviteLink = `${dashboardUrl}/invite/${token}`;

    return reply.status(201).send({
      id: invite.id,
      inviteLink,
      role: inviteRole,
      expiresAt,
    });
  });

  // GET /api/users/invites — list pending invitations (admin+)
  app.get('/api/users/invites', {
    preHandler: [requireRole('admin')],
  }, async (_request, reply) => {
    const invites = await inviteStore.listPendingInvites();
    return reply.send({ invites });
  });

  // DELETE /api/users/invites/:id — revoke invitation (admin+)
  app.delete('/api/users/invites/:id', {
    preHandler: [requireRole('admin')],
  }, async (request, reply) => {
    const { id } = request.params as { id: string };
    await inviteStore.revokeInvite(id);
    return reply.send({ ok: true });
  });
}
```

### Step 7: Modify GitHub OAuth Callback for Invites

In `packages/api/src/routes/auth/github-oauth.ts`, add invite token handling:

```typescript
// In the callback handler, before upsertUser:
const inviteToken = request.query.state; // Pass invite token via OAuth state parameter
let invitedRole: 'owner' | 'admin' | 'member' | undefined;

if (inviteToken) {
  const invite = await inviteStore.getInviteByToken(inviteToken);
  if (invite && !invite.acceptedAt && new Date(invite.expiresAt) > new Date()) {
    invitedRole = invite.role;
    await inviteStore.acceptInvite(invite.id);
  }
}

const user = await userStore.upsertUser({
  githubId: githubUser.id,
  githubLogin: githubUser.login,
  email: githubUser.email,
  role: invitedRole ?? 'member',
});
```

### Step 8: Audit Events

All user management mutations should emit events:

```typescript
// Event type patterns:
// USER.CREATED.SUCCESS       — new user via OAuth
// USER.INVITED.SUCCESS       — invite created
// USER.INVITE_ACCEPTED.SUCCESS — invite accepted via OAuth callback
// USER.ROLE_CHANGED.SUCCESS  — role updated
// USER.DELETED.SUCCESS       — soft delete
// USER.API_KEY_CREATED.SUCCESS — new API key generated
// USER.API_KEY_REVOKED.SUCCESS — API key revoked
```

## Logging Requirements

| Event | Level | Output | Notes |
|-------|-------|--------|-------|
| User listed | DEBUG | Pino structured log | Include requestor ID, pagination params |
| User role changed | INFO | Pino structured log + audit event | Include target user ID, old role, new role, changed by |
| User soft-deleted | INFO | Pino structured log + audit event | Include target user ID, deleted by |
| API key created | INFO | Pino structured log + audit event | Include user ID, key prefix (NOT full key) |
| API key revoked | INFO | Pino structured log + audit event | Include user ID, key ID |
| Invite created | INFO | Pino structured log + audit event | Include invited email, role, invited by |
| Invite accepted | INFO | Pino structured log + audit event | Include invite ID, accepting user ID |
| Authorization denied | WARN | Pino structured log | Include requestor ID, required role, endpoint |

### Sensitive Data Redaction

- NEVER log the full API key. Only the prefix (`tamma_sk_a1b2`).
- NEVER log the invite token in full. Log the invite ID instead.
- User email addresses may be logged (internal platform).

## Testing Strategy

### Unit Tests

Create `packages/api/src/routes/users/user-routes.test.ts`:

1. GET /api/users returns paginated list for admin, 403 for member
2. GET /api/users/:id returns user for admin, returns own user for member, 403 for other member
3. PUT /api/users/:id/role changes role for owner, 403 for admin trying to promote to owner
4. PUT /api/users/:id/role prevents self-role-change
5. DELETE /api/users/:id soft-deletes for owner, 403 for admin
6. DELETE /api/users/:id prevents self-deletion

Create `packages/api/src/routes/users/api-key-routes.test.ts`:

1. POST creates key, returns full key once
2. GET lists keys without full key
3. DELETE revokes key
4. Member can manage own keys, not others'

Create `packages/api/src/routes/users/invite-routes.test.ts`:

1. POST creates invite with link
2. Only owners can invite admin/owner roles
3. Invite link works via OAuth callback
4. Expired invite is rejected
5. Already-accepted invite is rejected

Create `packages/api/src/middleware/require-role.test.ts`:

1. requireRole('admin') allows admin and owner, blocks member
2. requireSelfOrRole('admin') allows self-access for member
3. Missing auth returns 401

### Integration Tests

1. Full invite flow: create invite -> follow link -> OAuth callback -> user created with invited role
2. API key lifecycle: create -> use for auth -> revoke -> auth fails
3. Soft delete: delete user -> user cannot log in -> API keys revoked

## Dependencies

- **Story 16.1** (oauth2-proxy) — unified auth must be in place before user management UI is useful
- Internal: `packages/api/src/auth/api-key.ts` (key generation utilities)
- Internal: Event store (for audit events)

## Estimated Effort

| Task | Hours |
|------|-------|
| Database migrations (3 files) | 2 |
| Extend IUserStore + PgUserStore | 3 |
| IUserApiKeyStore + PgUserApiKeyStore | 3 |
| IInviteStore + PgInviteStore | 2 |
| require-role middleware | 1 |
| User CRUD routes | 3 |
| API key routes | 2 |
| Invite routes + OAuth callback modification | 3 |
| Audit event emission | 1 |
| **Total** | **20 hours** |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-28 | 1.0 | Initial story creation | Architecture Team |
