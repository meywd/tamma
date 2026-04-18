# Finding 008: Installation callback does not generate or provision an API key

**Scope**: github
**Severity**: P0 (cutover-blocking)
**Status**: Semantic rewrite (structure changed, not a port)
**Estimated port effort**: 3-4h (plus Finding 013 for the provisioner itself)

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/github/github-callback.ts`.

- File: `packages/api/src/routes/github/github-callback.ts:105-148`
- Contract/behavior: After persisting the installation and its repos, the TS callback generated a fresh API key, hashed it, stored the `(hash, prefix)` on the installation row, and provisioned the plaintext key to every accessible repo as a GitHub Actions secret named `TAMMA_API_KEY`. Partial failures (e.g., one repo archived) were collected and logged but did not fail the callback — the key was server-side stored, and provisioning could be retried out of band.

```typescript
// packages/api/src/routes/github/github-callback.ts:105-148 (9e9a57c~1)
// Generate and provision API key
const apiKey = generateApiKey();
const apiKeyHash = hashApiKey(apiKey);
const apiKeyPrefix = getApiKeyPrefix(apiKey);

await options.installationStore.updateApiKeyHash(installationId, apiKeyHash, apiKeyPrefix);

// Provision API key as GitHub Actions secret to all repos
if (repos.length > 0) {
  try {
    const provisionResults = await provisioner.provisionApiKey(
      installationOctokit,
      repos.map((r) => ({ owner: r.owner, name: r.name })),
      apiKey,
    );

    const successCount = provisionResults.filter((r) => r.success).length;
    const failureCount = provisionResults.filter((r) => !r.success).length;

    app.log.info({
      msg: 'API key provisioned to repos',
      installationId,
      keyPrefix: apiKeyPrefix,
      reposProvisioned: successCount,
      reposFailed: failureCount,
    });

    if (failureCount > 0) {
      const failures = provisionResults.filter((r) => !r.success);
      app.log.warn({
        msg: 'Some repos failed API key provisioning',
        installationId,
        failures,
      });
    }
  } catch (err) {
    app.log.error({
      msg: 'Failed to provision API key to repos',
      error: err,
      installationId,
    });
    // Don't fail the callback — key is stored, provisioning can be retried
  }
}
```

The three functions `generateApiKey`, `hashApiKey`, `getApiKeyPrefix` came from `packages/api/src/auth/api-key.ts` (crypto-random 32-byte key, SHA-256 hash, 12-char visible prefix like `tk_live_abcdef…`). The `GitHubSecretsProvisioner` is the subject of Finding 013.

- Dependencies: `generateApiKey`, `hashApiKey`, `getApiKeyPrefix` (from `auth/api-key.ts`), `IGitHubInstallationStore.updateApiKeyHash`, `GitHubSecretsProvisioner.provisionApiKey`.
- Tests that exercised this: integration tests asserted `updateApiKeyHash` was called, and that the provisioner received the repo list and the plaintext key.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs:41-107`; `apps/tamma-elsa/src/Tamma.Api/Services/SaaS/ApiKeyRotationService.cs:13-16`
- Contract/behavior: The callback does not generate a key. It does not hash or persist a key. It does not provision to any repo. An API key for this installation only comes into existence when someone later calls `POST /api/v1/installations/{id}/rotate-key` (see `Program.cs:474` → `SaaSEndpoints.RotateInstallationKey` → `ApiKeyRotationService.RotateAsync`). And even that rotation explicitly admits it skips the repo provisioning step.

The C# `HandleCallbackAsync` signature ends at:

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs:91-107 (current)
        await EmitEventAsync(
            "INSTALLATION.LINKED.SUCCESS",
            tenant.Id,
            new Dictionary<string, object?>
            {
                ["installationId"] = installationId,
                ["tenantId"] = tenant.Id,
                ["userId"] = callingUserId,
                ["setupAction"] = setupActionId
            });

        _logger.LogInformation(
            "Linked GitHub installation {InstallationId} to tenant {TenantId} (user {UserId})",
            installationId, tenant.Id, callingUserId);

        return new CallbackResult(true, stored.Id, installationId, tenant.Id, null);
    }
```

There's no `apiKey = ApiKeyGenerator.Generate()`, no `apiKeys.CreateAsync`, no provisioner invocation. `ApiKeyRotationService` makes this explicit:

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/SaaS/ApiKeyRotationService.cs:13-16 (current)
// Ported from the deleted TypeScript routes/saas/key-rotation.ts
// (Epic 19 Phase 3). The TS version also re-provisioned the rotated key to
// GitHub-hosted repo secrets via Octokit; that re-provisioning is out of
// scope for this C# port because we do not yet wire a GitHub App client.
```

- Dependencies: none on the key-generation side of the callback. The rotation service exists (`ApiKeyRotationService`) and has an `IApiKeyRepository` to persist new keys, but the callback does not invoke it.
- Tests: `InstallationRouterServiceTests.HandleCallback_*` does not assert key creation (there is none).

## 3. The gap

Concrete behavioral difference — what a caller or user experiences differently.

- TS did: generate a 32-byte random key, hash it, store `(hash, prefix)` on the installation row, and push the plaintext key as `TAMMA_API_KEY` to every accessible repo as a GitHub Actions secret. All within the install callback, atomic from the user's perspective.
- C# does: nothing on the key axis. An API key for this installation does not exist until an admin explicitly rotates via `POST /api/v1/installations/{id}/rotate-key`, and even then the key is never pushed back to GitHub (the rotation service explicitly skips that step).
- For a caller completing install, TS returns with the repo's GitHub Actions secret `TAMMA_API_KEY` already set, so the customer's next Actions run can authenticate. C# returns with no key anywhere — the Actions run cannot call `api.tamma.dev` because `${{ secrets.TAMMA_API_KEY }}` is empty.
- In production with existing data / deployed clients, this means: **the SaaS Actions-worker onboarding flow has no automatic credential issuance**. A customer must manually request a key from the (future) dashboard, manually copy it, and manually paste it into the repo's Settings → Secrets → Actions for every repo the app is installed on. On an install with 50 repos this is unacceptable UX and also fragile (secret drift when repos are added/removed later).

Error paths:
- TS error path: generation failure → 500 (wrapped in the callback try/catch); partial provisioning failure → logged warn, callback succeeds.
- C# error path: no operation, no error path.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md`
- Story's acceptance criteria for this behavior: As noted in Finding 006, none of the ACs mention API key generation. Task 3 ("webhook handler") and Task 4 ("repo selection") don't cover this either. The closest story is the (not-present) "SaaS install flow" story, which would belong to a cross-cutting concern.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [x] No story — spec gap; must be backfilled before remediation

Story 18-4 needs a new AC (e.g. AC #11): "On successful GitHub App installation, an API key is generated server-side, its hash/prefix persisted against the installation, and the plaintext provisioned as `TAMMA_API_KEY` GitHub Actions secret to every accessible repo. Partial per-repo provisioning failures are logged but do not fail the install flow."

## 5. Status

- **Classification**: Semantic rewrite. The TS concept was "install = issue credential + push it to the customer's repos". The C# concept is "install = link a row to a tenant". The former is a complete SaaS onboarding primitive; the latter is its database-joining residue.
- **What's needed to finish**:
  1. In C#, the key generation primitives exist inside `Tamma.Api.Services.SaaS` (`ApiKeyRotationService` uses `Base62` + SHA-256 + 16-char prefix). Extract those into a reusable helper or call the rotation service from the callback path.
  2. After the callback has successfully fetched repos (Finding 007 must land first), generate a key, persist it via `IApiKeyRepository.CreateAsync`, and invoke the provisioner (Finding 013) to push to each repo.
  3. Decide failure policy: TS's "don't fail the callback on per-repo provision failure" is the right default.
  4. Emit a companion domain event: `INSTALLATION.API_KEY.ISSUED.SUCCESS` with `{installationId, tenantId, keyPrefix, reposProvisioned, reposFailed}`. Do NOT emit the plaintext key or hash in the event.
- **Is it "just a stub" or is scope missing?** Scope missing. The C# port intentionally excluded this ("out of scope for this C# port" per `ApiKeyRotationService.cs:13-16`).
- **Blockers**:
  - Finding 007 (callback must fetch repos first).
  - Finding 013 (secrets provisioner must exist).
  - Finding 018 (schema shift — the installation entity doesn't have `ApiKeyHash`/`ApiKeyPrefix`/`ApiKeyEncrypted` columns anymore; the `api_keys` table replaces them. The callback should write to `api_keys`, not the installation row.)

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs:91-107` — insert the key-generation + provisioning block after the repo-list persist.
  - `apps/tamma-elsa/src/Tamma.Api/Services/SaaS/ApiKeyRotationService.cs:13-16` — remove or update the apology comment when the gap is closed.
- Files to create:
  - Optionally `apps/tamma-elsa/src/Tamma.Api/Services/SaaS/ApiKeyGenerator.cs` (extract from rotation service for reuse).
- Tests to add:
  - `InstallationRouterServiceTests.HandleCallback_AfterRepoFetch_PersistsApiKey` — assert `IApiKeyRepository.CreateAsync` is called with correct `TenantId` and `InstallationId` scope.
  - `InstallationRouterServiceTests.HandleCallback_ProvisionsApiKeyToAllRepos` — assert provisioner invoked for each fetched repo.
  - `InstallationRouterServiceTests.HandleCallback_ProvisioningPartialFailure_StillReturnsSuccess` — simulate one repo throwing, assert callback result is `Success=true`.
  - `InstallationRouterServiceTests.HandleCallback_KeyPlaintextNotLogged` — assert the plaintext key does not appear in log output.
- Estimated effort: 3-4h broken down as:
  - Wire generator + repo write: 1h
  - Wire provisioner invocation (depends on Finding 013): 1-2h
  - Tests: 1h

## References

- TS source: `packages/api/src/routes/github/github-callback.ts:105-148` (commit `9e9a57c~1`)
- C# source:
  - `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs:41-107`
  - `apps/tamma-elsa/src/Tamma.Api/Services/SaaS/ApiKeyRotationService.cs:13-16` (explicit admission)
- Story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md` (missing AC)
- Related findings: `007-installation-callback-no-github-api-fetch.md`, `013-secrets-provisioner-libsodium-missing.md`, `018-schema-installation-no-apikey-columns.md`
