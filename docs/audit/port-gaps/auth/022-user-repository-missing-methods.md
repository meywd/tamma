# Finding 022: `IUserRepository` missing nine methods from TS `IUserStore`

**Scope**: auth
**Severity**: P1 (multiple auth flows blocked)
**Status**: Incomplete
**Estimated port effort**: 4h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/persistence/user-store.ts`.

- File: `packages/api/src/persistence/user-store.ts:68-118` (the `IUserStore` interface).
- Contract: 19 methods total. Some are unique to the store pattern (`upsertUser`, `getUserByGithubId`) and some are essential plumbing for various auth flows.
- Full interface:

```typescript
// packages/api/src/persistence/user-store.ts:68-118 (9e9a57c~1)
export interface IUserStore {
  upsertUser(user: UpsertUserInput): Promise<User>;
  getUser(id: string): Promise<User | null>;
  getUserByGithubId(githubId: number): Promise<User | null>;
  linkUserToInstallation(userId: string, installationId: number, role: Role): Promise<void>;
  getUserInstallations(userId: string): Promise<UserInstallation[]>;
  getUserSettings(userId: string): Promise<IProvidersConfig>;
  updateUserSettings(userId: string, settings: IProvidersConfig): Promise<IProvidersConfig>;
  listUsers(options: ListUsersOptions): Promise<ListUsersResult>;
  updateUserRole(id: string, role: Role): Promise<User>;
  deleteUser(id: string): Promise<void>;
  updateLastActive(id: string): Promise<void>;
  unlinkAllInstallations(userId: string): Promise<void>;
  // --- Story 18-1: Email auth methods ---
  createEmailUser(input: CreateEmailUserInput): Promise<User>;
  getUserByEmail(email: string): Promise<User | null>;
  setEmailVerified(userId: string): Promise<void>;
  updateVerificationToken(userId: string, tokenHash: string, expiresAt: string): Promise<void>;
  updatePasswordHash(userId: string, passwordHash: string): Promise<void>;
  updateActiveTenant(userId: string, tenantId: string | null): Promise<void>;
  updateAuthMethod(userId: string, authMethod: AuthMethod): Promise<void>;
  setGithubId(userId: string, githubId: number, githubLogin: string): Promise<void>;
}
```

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/Repositories/IUserRepository.cs:5-15`.
- Full interface:

```csharp
// apps/tamma-elsa/src/Tamma.Data/Repositories/IUserRepository.cs
public interface IUserRepository
{
    Task<User> CreateAsync(User user);
    Task<User?> GetByIdAsync(Guid id);
    Task<User?> GetByEmailAsync(string email);
    Task<User?> GetByGitHubIdAsync(int githubId);
    Task<(List<User> Users, int Total)> ListAsync(int limit, int offset, string? role);
    Task<User> UpdateAsync(User user);
    Task SoftDeleteAsync(Guid id);
    Task UpdateActiveTenantAsync(Guid userId, Guid tenantId);
}
```

Eight methods total.

## 3. The gap

Side-by-side mapping:

| TS method | C# method | Status |
|---|---|---|
| `upsertUser` | `CreateAsync` + `UpdateAsync` | Semi — no upsert-by-githubId logic; callers must branch manually |
| `getUser(id)` | `GetByIdAsync` | Match |
| `getUserByGithubId` | `GetByGitHubIdAsync` | Match |
| `linkUserToInstallation` | — | **Missing** (blocks OAuth callback, Finding 008) |
| `getUserInstallations` | — | **Missing** (blocks OAuth callback bootstrap; `/api/auth/me` memberships) |
| `getUserSettings` | — | **Missing** (blocks SaaS-mode per-user provider config) |
| `updateUserSettings` | — | **Missing** (same) |
| `listUsers` | `ListAsync` | Match (shape different but equivalent) |
| `updateUserRole` | `UpdateAsync` (caller mutates field) | Weaker — no dedicated role-update semantic |
| `deleteUser` | `SoftDeleteAsync` | Match |
| `updateLastActive` | — | **Missing** (manually set via `UpdateAsync` in Login endpoint line 223-224) |
| `unlinkAllInstallations` | — | **Missing** (blocks `DeleteUser` cascade, Finding 019) |
| `createEmailUser` | `CreateAsync` | Semi — no signature specialization |
| `getUserByEmail` | `GetByEmailAsync` | Match |
| `setEmailVerified` | — | **Missing** (blocks `VerifyEmail`, Finding 005) |
| `updateVerificationToken` | — | **Missing** (ResendVerification manually mutates via `UpdateAsync` line 137-139) |
| `updatePasswordHash` | — | **Missing** (PasswordResetConfirm manually mutates via `UpdateAsync` line 342-343) |
| `updateActiveTenant` | `UpdateActiveTenantAsync` | Match |
| `updateAuthMethod` | — | **Missing** (blocks account linking in OAuth) |
| `setGithubId` | — | **Missing** (blocks account linking in OAuth) |

Nine methods missing outright. Two more (updateUserRole, createEmailUser) exist in degenerate form (via generic `UpdateAsync` / `CreateAsync`).

Caller-side consequences:
- `VerifyEmail` endpoint cannot look up a user by verification token hash → Finding 005 (stub).
- `GitHubCallback` cannot link a user to installations → Finding 008 blocked by missing `LinkUserToInstallationAsync`.
- `GitHubCallback` cannot update `authMethod` to `'both'` for an account-linking user → Finding 008 impossible without `UpdateAuthMethodAsync`.
- `DeleteUser` cannot unlink installations → Finding 019 blocked.
- User-level provider config (SaaS mode analogue of `~/.tamma/providers.json`) has nowhere to live → Finding 025.
- `/api/auth/me` cannot return `installations` field → unified dashboard feature partially lost.
- `Login` manually does `user.LastActiveAt = DateTime.UtcNow; await userRepo.UpdateAsync(user);` at line 223-224 — forces a full-row update (EF Core change-tracking behavior) when a targeted UPDATE would suffice.
- `ResendVerification` (line 137-139) manually mutates two columns + `UpdateAsync`.
- `PasswordResetConfirm` (line 342-343) manually mutates `PasswordHash` + `UpdateAsync`.

So the repository works for the simple path (update one column via change-tracked full save) but doesn't cleanly express the operations, and some don't work at all.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-1-user-registration-email-verification.md`, `18-2`, `18-3`, and `docs/stories/epic-17/17-1-tenant-model-database-schema.md`.
- Story 18-1 subtask 1.4 (line 32): *"Update `IUserStore` interface with new methods: `createUserWithPassword()`, `getUserByEmail()`, `setEmailVerified()`, `updateVerificationToken()`"*.
- Story 18-2 subtask 4.6-4.7 (line 65-66): *"Check if user exists by email; if yes, link GitHub account (`authMethod: 'both'`, set `githubId`)"* — requires `updateAuthMethod` + `setGithubId`.
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story (story is explicit about required methods)

C# regresses from the story's explicit list.

## 5. Status

- **Classification**: Incomplete (half the interface ported).
- **What's needed to finish**:
  1. Add the nine missing methods to `IUserRepository` and `UserRepository`:
     - `SetEmailVerifiedAsync(Guid id)` — `user.EmailVerified = true; user.EmailVerificationTokenHash = null; user.EmailVerificationExpiresAt = null;`
     - `UpdateVerificationTokenAsync(Guid id, string tokenHash, DateTime expiresAt)`
     - `UpdatePasswordHashAsync(Guid id, string passwordHash)`
     - `UpdateAuthMethodAsync(Guid id, string authMethod)`
     - `SetGitHubIdAsync(Guid id, int githubId, string githubLogin)`
     - `UnlinkAllInstallationsAsync(Guid id)` (blocked by Finding 023)
     - `GetUserInstallationsAsync(Guid id)` (blocked by Finding 023)
     - `LinkUserToInstallationAsync(Guid id, long installationId, string role)` (blocked by Finding 023)
     - `UpdateLastActiveAsync(Guid id)` (targeted UPDATE, avoid full row save)
  2. Add a `GetByEmailVerificationTokenHashAsync(string tokenHash)` (needed by Finding 005).
  3. Consider splitting settings into a `IUserSettingsRepository` pointing at Finding 025's `settings` column (when added).
- **Is it "just a stub" or is scope missing?** Scope missing — nine methods from the TS interface were never ported.
- **Blockers**: Finding 023 (three methods depend on `user_installations` table existing), Finding 025 (two methods depend on `users.settings` column).

## Remediation

- Files to modify: `IUserRepository.cs`, `UserRepository.cs`, callers that currently mutate-and-`UpdateAsync` (AuthEndpoints.cs — login updateLastActive, ResendVerification, PasswordResetConfirm).
- Files to create: None (interface expansion + impl additions).
- Tests to add: One unit test per new method (9 tests minimum). Plus caller-side integration tests aligned with Findings 005, 008, 019.
- Estimated effort: 4h
  - Nine method impls: 1.5h
  - Unit tests: 1.5h
  - Caller refactors (3-4 endpoints): 1h

## References

- TS source: `packages/api/src/persistence/user-store.ts:68-118` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Data/Repositories/IUserRepository.cs`, `UserRepository.cs`
- Story: `docs/stories/epic-18/18-1-user-registration-email-verification.md` (subtask 1.4); `18-2` (subtask 4.6-4.7)
- Related findings: `005-email-verification-stub.md`, `008-oauth-callback-stub.md`, `019-admin-delete-user-no-cascade.md`, `023-user-installations-table-absent.md`, `025-user-settings-jsonb-column-missing.md`
