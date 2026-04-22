# Story 31-8 Implementation Plan — CI Secrets Provisioner Abstraction

**Status**: Planned (2026-04-21)
**Story brief**: [`31-8-ci-secrets-provisioner-abstraction.md`](./31-8-ci-secrets-provisioner-abstraction.md)
**Epic 31 phase**: Layer 4 — serial after 31-3/31-4/31-6.
**Branch**: `feat/story-31-8-ci-secrets-provisioner-abstraction`

---

## 1. Objective

Ship `ICiSecretsProvisioner` as a platform-neutral, plaintext-in
interface that each driver implements in its native wire format:
libsodium sealed-box for GitHub, plaintext POST for Gitea/Forgejo,
CI/CD variable with masked+protected flags for GitLab. Expose
scope + target + metadata as neutral records; drivers that don't
support a capability return per-target `scope_not_supported_on_platform`
without throwing. Redaction helper prevents secret values from
appearing in any log. Existing `IGitHubSecretsProvisioner` is
deprecated but kept; callers migrate in a follow-up.

## 2. Dependencies

Hard blockers:

- **Story 31-1** — abstraction project.
- **Story 31-3** — GitHub driver (wraps existing
  `LibsodiumGitHubSecretsProvisioner`).
- **Story 31-4** — Gitea driver (plaintext endpoint).
- **Story 31-6** — GitLab driver (masked-variable endpoint).

Soft:

- **Epic 1.5-30** — RotationCascadeWorkflow expects a compatible
  consumer shape.

Blocks: **31-9** (onboarding UI reads capability matrix to pre-
disable unsupported scopes).

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.Abstractions/ICiSecretsProvisioner.cs` | Neutral interface. |
| `.../CiSecretScope.cs` | Enum: `Repo`, `Org`, `User`, `Global`, `Environment`. |
| `.../CiSecretTarget.cs` | Discriminated union: `Repo(owner, repo)`, `Org(orgOrGroup)`, `User(userLogin)`, `Environment(repo, environmentName)`. |
| `.../CiSecretMetadata.cs` | `sealed record { bool Protected = false, bool Masked = false, string? EnvironmentScope, string? VariableType = "env_var" }`. |
| `.../CiSecretProvisionResult.cs` | `sealed record (PlatformKind, string TargetDescriptor, bool Success, string? Error)`. |
| `.../RedactedSecret.cs` | `sealed struct` wrapping a string with `ToString() => "[redacted:<N> chars]"`; implicit ctor from string. |
| `.../Logging/SecretLoggingScope.cs` | Helper `Redact(string)` + `RedactForLogging(object)` scopes. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.GitHub/GitHubCiSecretsProvisioner.cs` | Impl: delegates to `LibsodiumGitHubSecretsProvisioner` for libsodium encryption; normalizes result shape. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.Gitea/GiteaCiSecretsProvisioner.cs` | Impl: plaintext POST `PUT /repos/{owner}/{repo}/actions/secrets/{name}` with `{ data: value }`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.Gitea/ForgejoCiSecretsProvisioner.cs` | Thin wrapper over Gitea impl (API-compat). |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.GitLab/GitLabCiSecretsProvisioner.cs` | Impl: `POST /api/v4/projects/{id}/variables` with `{ key, value, protected, masked, environment_scope, variable_type }`; masked-value pre-validation. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.GitLab/MaskedVariableValidator.cs` | Client-side enforcement of GitLab masked-value rules. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Platforms.Abstractions.Tests/CiSecretsProvisionerContractTests.cs` | Abstract contract-test base class. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Platforms.GitHub.Tests/GitHubCiSecretsProvisionerTests.cs` | Libsodium round-trip + log sanitization. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Platforms.Gitea.Tests/GiteaCiSecretsProvisionerTests.cs` | Plaintext POST shape + scope enforcement. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Platforms.GitLab.Tests/GitLabCiSecretsProvisionerTests.cs` | Masked-value validation + protected/env-scope flags. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/GitHub/IGitHubSecretsProvisioner.cs` | Re-annotate `[Obsolete("Use ICiSecretsProvisioner via IGitPlatformDriver", false)]`. Remains callable for transition. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.GitHub/GitHubPlatformDriver.cs` | Expose `CiSecretsProvisioner` via the driver. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.Gitea/GiteaPlatformDriver.cs` | Same. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.GitLab/GitLabPlatformDriver.cs` | Same. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Program.cs` | Register each platform's provisioner as keyed-DI entry. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.Abstractions/IGitPlatformDriver.cs` | Add `ICiSecretsProvisioner? CiSecretsProvisioner { get; }` (nullable if not supported). |

## 5. Sequence of changes

### Step 1 — Interface + records + redaction helper (4h)

- `ICiSecretsProvisioner` with `ProvisionSecretAsync` +
  `DeleteSecretAsync`.
- `CiSecretScope`, `CiSecretTarget` (discriminated union via record
  pattern), `CiSecretMetadata`, `CiSecretProvisionResult`.
- `RedactedSecret` struct + `SecretLoggingScope.Redact(...)`.
- `IGitPlatformDriver` gains nullable `CiSecretsProvisioner`
  property.
- Unit tests: record construction; redaction output format;
  `ToString` on `RedactedSecret` never exposes value.
- **Commit**: `feat(platforms): ICiSecretsProvisioner contract`.

### Step 2 — Contract-test base class (2h)

- `abstract class CiSecretsProvisionerContractTests<TFixture>`:
  - `Test_Provision_HappyPath`
  - `Test_Provision_PerTargetErrorIsolation`
  - `Test_Provision_ScopeNotSupported_ReturnsPerTargetError`
  - `Test_Provision_NoSecretValueInLogs`
  - `Test_Delete_Idempotent`
- Driver test projects subclass with their WireMock fixture.
- **Commit**: `test(platforms): CiSecretsProvisioner contract`.

### Step 3 — GitHub impl (2h)

- `GitHubCiSecretsProvisioner`:
  - Delegates encryption to existing `LibsodiumGitHubSecretsProvisioner`
    (internal helper from 31-3).
  - For each target in the input:
    - Resolves repo public key via the internal client.
    - Encrypts with `crypto_box_seal`.
    - `PUT /repos/{owner}/{repo}/actions/secrets/{name}` with
      `{ encrypted_value, key_id }`.
  - Scope handling:
    - `Repo` → single `PUT` per target.
    - `Org` → `PUT /orgs/{org}/actions/secrets/{name}`.
    - `Environment` → `PUT /repos/{owner}/{repo}/environments/{env}/secrets/{name}`.
    - `User`, `Global` → `CiSecretProvisionResult { Success = false,
      Error = "scope_not_supported_on_platform" }`.
- Tests:
  - Round-trip libsodium encrypt → mocked PUT with correct payload
    shape.
  - No plaintext in logs (grep assertion via `ILogger` mock).
  - Per-target error isolation: 500 on one target does not fail
    others.
- **Commit**: `feat(platforms.github): CiSecretsProvisioner impl`.

### Step 4 — Gitea/Forgejo impl (3h)

- `GiteaCiSecretsProvisioner`:
  - For each target:
    - `PUT /repos/{owner}/{repo}/actions/secrets/{name}` with
      `{ data: plaintext }`.
    - `Org` → `PUT /orgs/{org}/actions/secrets/{name}`.
    - `User` → `PUT /user/actions/secrets/{name}` (Gitea-specific
      scope).
    - `Global` → supported on 1.25+ via admin endpoint; guard on
      probe.
    - `Environment` → unsupported on Gitea 1.25; return
      `scope_not_supported`.
- `ForgejoCiSecretsProvisioner` = thin wrapper over Gitea impl.
- Tests cover each scope.
- **Commit**: `feat(platforms.gitea): CiSecretsProvisioner impl`.

### Step 5 — GitLab impl + masked-value validator (5h)

- `GitLabCiSecretsProvisioner`:
  - For each target:
    - `Repo` → `POST /api/v4/projects/{pid}/variables` with
      `{ key, value, protected, masked, environment_scope,
      variable_type }`.
    - `Org` (mapped to GitLab group) →
      `POST /api/v4/groups/{gid}/variables`.
    - `Environment` → same as `Repo` but with non-null
      `environment_scope`.
    - `User`, `Global` → `scope_not_supported`.
- `MaskedVariableValidator.Validate(value)`:
  - Length >= 8.
  - No newlines.
  - Only `A-Za-z0-9+/=@:.~_-`.
  - Returns `null` if ok else `"masked_value_invalid: <rule>"`.
  - Called pre-POST when `metadata.Masked == true`.
- Tests cover each rule; protected + env-scope flags round-trip.
- **Commit**: `feat(platforms.gitlab): CiSecretsProvisioner impl`.

### Step 6 — Driver wire-up + DI (2h)

- Each driver's `CiSecretsProvisioner` property returns the impl.
- `Program.cs` registers each impl keyed by `PlatformKind` so 31-9
  can enumerate.
- **Commit**: `feat(platforms): wire CiSecretsProvisioner per driver`.

### Step 7 — Batching + rate limiting (1h)

- `ProvisionSecretAsync` default `maxParallelism = 5` (matches
  existing `LibsodiumGitHubSecretsProvisioner`).
- `SemaphoreSlim` gates parallel calls.
- Per-driver exponential backoff on 429.
- Configurable via `Platforms:{Kind}:SecretsMaxParallelism`.
- **Commit**: `feat(platforms): CiSecretsProvisioner batching`.

### Step 8 — Documentation (1h)

- Each driver's README gains a "Secrets provisioning" section listing
  supported scopes + scope-not-supported fallback.
- **Commit**: `docs(platforms): CiSecretsProvisioner docs`.

## 6. Test strategy

### Unit

- Interface records: construction + equality.
- `RedactedSecret`: `ToString` never leaks; implicit conversion
  works.
- `MaskedVariableValidator`: every rule individually.
- Per-driver provisioner: happy path, per-target errors, unsupported
  scopes.

### Contract

- `CiSecretsProvisionerContractTests<T>` runs against each driver's
  WireMock fixture.

### Security

- ILogger mock captures logs; assert no plaintext secret substring.
- Per-driver: a known secret value is never present in any log call.

## 7. Rollback plan

- **Revert commits**: removes new interface + per-driver impls +
  contract tests. Existing `IGitHubSecretsProvisioner` remains
  callable. Callers that migrated to the new interface break;
  `[Obsolete]` tag makes callers obvious.
- **Non-reversible**: none.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Interface + records + redaction | 4 |
| 2. Contract-test base | 2 |
| 3. GitHub impl | 2 |
| 4. Gitea/Forgejo impl | 3 |
| 5. GitLab impl + masked validator | 5 |
| 6. Driver wire-up + DI | 2 |
| 7. Batching + rate limits | 1 |
| 8. Docs | 1 |
| **Total** | **20** (matches brief). |

## 9. Open questions

- **`User` scope on GitHub**: GitHub has Codespaces personal
  secrets (`PUT /user/codespaces/secrets/{name}`) but not general
  actions user secrets. Plan: `User` on GitHub driver returns
  `scope_not_supported_on_platform`. Gitea supports user scope;
  Bitbucket + GitLab + Azure do not. Capability matrix updated.
- **libsodium internal vs public**: `LibsodiumGitHubSecretsProvisioner`
  was public pre-31-3; 31-3 marked `[Obsolete]` but still public.
  31-8's GitHub provisioner uses it as internal. Plan: move
  `LibsodiumGitHubSecretsProvisioner` inside the
  `Tamma.Platforms.GitHub` project in 31-3 (already planned).
- **Concurrency cap default**: 5 is the GitHub default; GitLab has
  a stricter 60/min rate limit. Plan: per-platform defaults in
  appsettings rather than code-level constant. Document.
- **`variable_type` GitLab values**: `env_var` (default) or `file`.
  `file` causes GitLab to write the value as a file on the runner.
  Plan: driver allows `metadata.VariableType` to be either;
  default `env_var`. Note this is GitLab-specific; other drivers
  ignore with log-debug.
- **Per-tenant LLM rotation hookup**: Epic 1.5-30's
  `RotationCascadeWorkflow` calls a "push to consumer" handler.
  Plan: add `CiSecretsConsumer` implementing the 1.5-30 contract
  in a follow-up; out of scope here.
- **Delete of a secret that doesn't exist**: 404 path. Plan: treat
  as success (idempotent delete); return `Success = true, Error =
  null`. Document.
- **Interface surface future-additions**: `ListSecretsAsync`?
  (brief doesn't require). Plan: add in follow-up; current
  callers only provision + delete.
