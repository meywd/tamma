# Story 31-8: ICiSecretsProvisioner abstraction across GitHub / Gitea / Forgejo / GitLab

Status: todo (planning brief, 2026-04-21)

## Story

As a **Tamma service pushing a secret into a tenant's CI/CD
variable store** (e.g. an installation-scoped `TAMMA_AGENT_TOKEN`
for the agent runner, or a per-repo `LLM_API_KEY`),
I want to call a single `ICiSecretsProvisioner` and receive back a
per-repo push-result list,
so that the caller doesn't know GitHub needs libsodium sealed-box
encryption while Gitea / Forgejo / GitLab / Bitbucket accept
plaintext over TLS.

## Narrative

Research finding (see
[`research/multi-git-platform-2026.md` §6](../research/multi-git-platform-2026.md)):
libsodium sealed-box encryption is GitHub-specific. Every other
platform in scope accepts plaintext secrets over HTTPS and encrypts
them at rest server-side. The existing `IGitHubSecretsProvisioner` +
`LibsodiumGitHubSecretsProvisioner` encode this assumption at the
interface level. 31-8 restructures so:

- Interface is plaintext-in.
- Each driver handles its own wire format (libsodium for GitHub;
  POST plaintext for Gitea/Forgejo/GitLab/Bitbucket).
- Driver config declares the supported scope levels
  (`Global|User|Org|Repo|Environment`) — the caller picks the
  scope per call, the driver enforces its platform's capability
  matrix.

Epic 1.5-23..1.5-26 owns the LLM-safe secret-mirror track; 31-8's
surface is compatible so the LLM-ops handlers can plug in.

## Acceptance Criteria

1. New interface `ICiSecretsProvisioner` in
   `Tamma.Platforms.Abstractions`:

   ```csharp
   Task<IReadOnlyList<CiSecretProvisionResult>> ProvisionSecretAsync(
       CiSecretScope scope,
       string secretName,
       string secretValue,
       IReadOnlyList<CiSecretTarget> targets,
       CiSecretMetadata? metadata,
       CancellationToken ct);

   Task<IReadOnlyList<CiSecretProvisionResult>> DeleteSecretAsync(
       CiSecretScope scope,
       string secretName,
       IReadOnlyList<CiSecretTarget> targets,
       CancellationToken ct);
   ```
2. `CiSecretScope` enum: `Repo`, `Org`, `User`, `Global`,
   `Environment`.
3. `CiSecretTarget` union: `Repo(owner, repo)`,
   `Org(orgOrGroup)`, `Environment(repo, environmentName)`.
4. `CiSecretMetadata` optional record:
   `{ bool Protected = false, bool Masked = false, string?
   EnvironmentScope = null, string? VariableType = "env_var" }`.
   Drivers apply only the flags their platform supports; ignore
   (log-at-debug) unsupported flags.
5. `CiSecretProvisionResult` record matches the existing
   `SecretProvisionResult` shape: `(PlatformKind, TargetDescriptor,
   bool Success, string? Error)`. `Error` populated when
   per-target push fails; the overall call does not throw.
6. Each driver implements its native wire format:
   - **GitHub** (31-3 driver) — reuses
     `LibsodiumGitHubSecretsProvisioner`. Fetches repo public key,
     encrypts with crypto_box_seal, `PUT /repos/{owner}/{repo}/actions/secrets/{name}`.
   - **Gitea / Forgejo** (31-4 / 31-5) — `PUT /repos/{owner}/{repo}/actions/secrets/{name}`
     with `{ "data": "<plaintext>" }`.
   - **GitLab** (31-6) — `POST /api/v4/projects/:id/variables` with
     `{ key, value, protected, masked, environment_scope, variable_type }`.
     Masked + length constraints enforced; violation returns
     `PlatformError.InvalidRequest`.
7. Capability-aware fallback — if caller requests
   `CiSecretScope.Environment` on a driver that doesn't support it
   (e.g. Gitea pre-1.25), the driver returns
   `CiSecretProvisionResult { Success = false, Error =
   "scope_not_supported_on_platform" }` per target instead of
   throwing. Caller's onboarding UI (31-9) reads capability matrix
   and pre-disables unsupported picks.
8. Batching + rate-limit awareness — each driver defaults to
   `max-parallelism = 5` (same as the existing
   `LibsodiumGitHubSecretsProvisioner`), with exponential backoff
   on 429. Overridable per driver via config.
9. Redaction — `secretValue` parameter is `string`, but the
   provisioner logs use a `RedactedSecret` wrapper that prints
   `[redacted:N chars]` in any stringification. Per-target result
   logs include `secretName` + scope + target identifier but **not
   the value**. Shared helper `SecretLoggingScope.Redact(value)`.
10. `IGitHubSecretsProvisioner` is deprecated but left in place;
    callers migrate to `ICiSecretsProvisioner` via the 31-3
    refactor. A later cleanup story removes `IGitHubSecretsProvisioner`.
11. Unit tests per driver:
    - GitHub: round-trip libsodium encrypt → mocked
      `createOrUpdateRepoSecret` → verify payload shape + no
      plaintext in logs.
    - Gitea: plaintext POST shape correct, secret-name
      validation, scope-level enforcement.
    - GitLab: masked-value validation rules applied; protected +
      environment-scope flags wired correctly.
12. Contract test shared: `ICiSecretsProvisionerContractTests<T>`
    asserts the basic happy-path + idempotency + per-target error
    isolation on every driver.

## Technical Context

### Keeping libsodium a GitHub-driver detail

No `LibsodiumSecrets` flag in the interface surface — that's a
`PlatformCapability` checked at driver level. The interface only
cares about plaintext-in. This inverts today's code where the
provisioner interface is libsodium-shaped; every non-GitHub driver
would have had to fake an encryption step.

### Interop with Epic 1.5 rotation cascade

Epic 1.5-30 (`RotationCascadeWorkflow`) expects to call a handler
that "pushes the new value to the consumer". `ICiSecretsProvisioner`
is one of those consumers. The handler contract from 29-6 /
1.5-30 accepts a provisioner + target list + new value. 31-8's
interface is compatible.

### Credential fetch

The provisioner takes an installation-scoped driver via the
`IGitPlatformDriver` it lives on — auth is handled upstream, not
in this interface. 31-8 surfaces only the push operation.

## Dependencies

- **31-1** — abstraction
- **31-3** — GitHub driver (libsodium impl)
- **31-4** — Gitea driver (plaintext impl)
- **31-6** — GitLab driver (masked-variable impl)
- Blocks 31-9 (onboarding UI shows which secret scopes are
  available)

## Estimated hours

**20h**

| Task | Hours |
|---|---|
| Interface + records + capability check | 4 |
| GitHub driver impl (wraps libsodium) | 2 |
| Gitea / Forgejo impl | 3 |
| GitLab impl + masked-value validation | 5 |
| Redaction helper | 1 |
| Unit + contract tests | 5 |

## Files touched

- `apps/tamma-elsa/src/Tamma.Platforms.Abstractions/ICiSecretsProvisioner.cs` (new)
- `apps/tamma-elsa/src/Tamma.Platforms.{GitHub,Gitea,GitLab}/*CiSecretsProvisioner.cs` (new in each driver)
- `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/IGitHubSecretsProvisioner.cs` (mark deprecated)
- `apps/tamma-elsa/tests/Tamma.Platforms.{GitHub,Gitea,GitLab}.Tests/*CiSecretsProvisionerTests.cs` (new)

## References

- Research notes: [`../research/multi-git-platform-2026.md`](../research/multi-git-platform-2026.md) §6
- Existing GitHub impl: `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/LibsodiumGitHubSecretsProvisioner.cs`
- Epic 1.5 track: [`../plans/secret-management-track.md`](../plans/secret-management-track.md)
