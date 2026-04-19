# Finding 013: Secrets provisioner (libsodium sealed-box + GitHub Actions secrets) entirely missing

**Scope**: github
**Severity**: P0 (cutover-blocking)
**Status**: Not-yet-implemented (stub) — the port explicitly admits dropping this
**Estimated port effort**: 6-8h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/services/github-secrets-provisioner.ts`.

The full file, quoted:

```typescript
/**
 * GitHub Secrets Provisioner
 *
 * Provisions TAMMA_API_KEY as a GitHub Actions secret to repos
 * associated with an installation, using libsodium sealed-box encryption.
 */

import type { Octokit } from '@octokit/rest';

/** Result of provisioning a secret to a single repo. */
export interface ProvisionResult {
  owner: string;
  repo: string;
  success: boolean;
  error?: string;
}

/** Public key response from GitHub Actions secrets API. */
interface RepoPublicKey {
  key_id: string;
  key: string;
}

/** Maximum number of concurrent secret writes. */
const MAX_CONCURRENCY = 5;

/**
 * Provisions GitHub Actions secrets to repositories using
 * libsodium sealed-box encryption.
 */
export class GitHubSecretsProvisioner {
  /**
   * Get a repo's public key for encrypting secrets.
   */
  async getRepoPublicKey(
    octokit: Octokit,
    owner: string,
    repo: string,
  ): Promise<RepoPublicKey> {
    const { data } = await octokit.rest.actions.getRepoPublicKey({
      owner,
      repo,
    });
    return { key_id: data.key_id, key: data.key };
  }

  /**
   * Encrypt a secret value using libsodium crypto_box_seal.
   *
   * @param publicKeyBase64 - The repo's public key (base64-encoded).
   * @param secretValue - The plaintext secret to encrypt.
   * @returns base64-encoded encrypted value.
   */
  async encryptSecret(publicKeyBase64: string, secretValue: string): Promise<string> {
    // Dynamic import to avoid bundling issues and allow libsodium to initialize
    const sodium = await import('libsodium-wrappers').then((m) => m.default ?? m);
    await sodium.ready;

    const publicKeyBytes = sodium.from_base64(publicKeyBase64, sodium.base64_variants.ORIGINAL);
    const messageBytes = sodium.from_string(secretValue);
    const encryptedBytes = sodium.crypto_box_seal(messageBytes, publicKeyBytes);
    return sodium.to_base64(encryptedBytes, sodium.base64_variants.ORIGINAL);
  }

  /**
   * Write a single secret to a repository.
   * Full flow: get public key, encrypt, PUT.
   */
  async writeSecret(
    octokit: Octokit,
    owner: string,
    repo: string,
    secretName: string,
    secretValue: string,
  ): Promise<void> {
    const publicKey = await this.getRepoPublicKey(octokit, owner, repo);
    const encryptedValue = await this.encryptSecret(publicKey.key, secretValue);

    await octokit.rest.actions.createOrUpdateRepoSecret({
      owner,
      repo,
      secret_name: secretName,
      encrypted_value: encryptedValue,
      key_id: publicKey.key_id,
    });
  }

  /**
   * Provision TAMMA_API_KEY to multiple repos (parallel, max 5 concurrent).
   *
   * Handles errors per-repo: skips archived repos, logs warnings, does not
   * fail the entire batch on individual failures.
   */
  async provisionApiKey(
    octokit: Octokit,
    repos: Array<{ owner: string; name: string }>,
    apiKey: string,
  ): Promise<ProvisionResult[]> {
    const results: ProvisionResult[] = [];

    // Process in batches of MAX_CONCURRENCY
    for (let i = 0; i < repos.length; i += MAX_CONCURRENCY) {
      const batch = repos.slice(i, i + MAX_CONCURRENCY);
      const batchResults = await Promise.allSettled(
        batch.map(async (repo) => {
          try {
            await this.writeSecret(octokit, repo.owner, repo.name, 'TAMMA_API_KEY', apiKey);
            return { owner: repo.owner, repo: repo.name, success: true } satisfies ProvisionResult;
          } catch (err: unknown) {
            const message = err instanceof Error ? err.message : String(err);
            // Check for archived repo or permission errors
            const isArchived = message.includes('archived');
            const errorMessage = isArchived
              ? `Skipped archived repo ${repo.owner}/${repo.name}`
              : `Failed to provision secret for ${repo.owner}/${repo.name}: ${message}`;
            return { owner: repo.owner, repo: repo.name, success: false, error: errorMessage } satisfies ProvisionResult;
          }
        }),
      );

      for (const result of batchResults) {
        if (result.status === 'fulfilled') {
          results.push(result.value);
        } else {
          // Should not happen since we catch errors above, but handle defensively
          results.push({
            owner: 'unknown',
            repo: 'unknown',
            success: false,
            error: String(result.reason),
          });
        }
      }
    }

    return results;
  }
}
```

Key technical points:
- Uses `libsodium-wrappers` (JS binding to libsodium) for `crypto_box_seal`, a sealed-box primitive that takes a recipient public key and produces an anonymous ciphertext. This is what GitHub mandates for Actions secrets — GitHub provides a per-repo X25519 public key, callers encrypt via sealed-box, GitHub decrypts server-side.
- Base64 encoding uses `sodium.base64_variants.ORIGINAL` (standard base64, not URL-safe). GitHub expects standard base64 for the `encrypted_value` field.
- Concurrency cap: 5 parallel writes per batch. For an installation with 100 repos, this means 20 batches.
- Per-repo failures don't abort the batch — the caller gets a `ProvisionResult[]` and decides policy.

- Dependencies: `@octokit/rest` (for `actions.getRepoPublicKey`, `actions.createOrUpdateRepoSecret`), `libsodium-wrappers`.
- Tests that exercised this: unit tests mocked Octokit, asserted the sealed-box output shape, asserted the 5-concurrency batching, asserted archived-repo handling.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: none exists.
- Contract/behavior: zero equivalent. No provisioner class, no sealed-box call, no GitHub Actions secrets API integration. Grep across the entire C# solution for `sealed-box`, `crypto_box_seal`, `NSec.Cryptography`, `Sodium`, `libsodium`, `createOrUpdateRepoSecret`, `actions/secrets`, etc. yields nothing.

The closest reference is the apology comment in `ApiKeyRotationService.cs`:

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/SaaS/ApiKeyRotationService.cs:13-16 (current)
/// Ported from the deleted TypeScript <c>routes/saas/key-rotation.ts</c>
/// (Epic 19 Phase 3). The TS version also re-provisioned the rotated key to
/// GitHub-hosted repo secrets via Octokit; that re-provisioning is out of
/// scope for this C# port because we do not yet wire a GitHub App client.
```

This is the explicit admission. The rotation service (which would be a natural consumer of the provisioner) does its DB-side work (generate key, update hash, emit event) and stops.

- Dependencies: nothing wired.
- Tests: no tests cover provisioning because no code exists.

## 3. The gap

- TS did: encrypt API keys via libsodium sealed-box with the per-repo public key, PUT to `/repos/{owner}/{repo}/actions/secrets/TAMMA_API_KEY` via authenticated Octokit, handle archived-repo failures gracefully, batch 5 at a time.
- C# does: nothing. There is no secret-provisioning surface.
- For a caller completing install (Finding 007, 008), TS pushed `TAMMA_API_KEY` to every accessible repo. C# does not. For a caller rotating an API key via `POST /api/v1/installations/{id}/rotate-key`, TS pushed the new key to every repo. C# updates the DB and stops — the old key stays in every repo's secret store even though it no longer validates server-side, which means Actions workflows immediately start failing auth with 401.
- In production with existing data / deployed clients, this means:
  - **No onboarding credential issuance**: customer repos never receive `TAMMA_API_KEY` automatically (Finding 008).
  - **Key rotation breaks Actions**: rotating a key invalidates the old server-side hash; the repo-side secret still has the old plaintext; Actions workflows immediately start returning 401 from `api.tamma.dev`. This is silently destructive — the customer's GitHub Actions runs break and they don't know why.
  - **Multi-repo synchronization impossible**: adding a new repo to an existing installation (via `installation_repositories.added` webhook, Finding 006 / `InstallationRouterService.cs:304-366`) does not push the existing API key to the new repo. The new repo has no secret. Actions fail there too.

Error paths:
- TS error path: archived repo → success=false with "Skipped archived" message, batch continues. 403 (insufficient permissions) → success=false, message includes the permission error. Network error → success=false, retry logic is on the caller.
- C# error path: nothing to fail, nothing to retry — feature absent.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md`
- Story's acceptance criteria for this behavior: **None explicit**. This is the single biggest spec gap in the GitHub-integration audit. Story 18-4 describes the install flow at a business level (AC #3: "link the new installation to the user's org") but does not say "and push a credential to every repo so Actions can authenticate". The provisioning need is implicit in the SaaS Actions-worker architecture (which `ApiKeyRotationService.cs:13-16` confirms exists) but not written down in any story.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS) — TS had this; C# does not.
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [x] No story — spec gap; must be backfilled before remediation

This needs a dedicated follow-up story. Proposed: "Story 18-4.1: API key auto-provisioning to GitHub Actions secrets on install and rotation". Pre-requisite for closing this finding.

## 5. Status

- **Classification**: Not-yet-implemented (stub) — no C# artifact exists. The comment in `ApiKeyRotationService.cs` is the only trace.
- **What's needed to finish**:
  1. Choose libsodium binding for .NET. Options:
     - `NSec.Cryptography` (Microsoft-maintained wrapper around libsodium; has `SealedPublicKeyBox`). **Recommended.**
     - `Sodium.Core` (community wrapper; older).
     - `libsodium-core` (actively maintained fork).
     - Writing raw `crypto_box_seal` over X25519 via `System.Security.Cryptography` primitives is non-trivial — sealed-box involves a specific construction (ephemeral keypair + box + HMAC-Blake2b) that's easy to get subtly wrong. Use an existing binding.
  2. Implement `IGitHubSecretsProvisioner` + `GitHubSecretsProvisioner` class with methods:
     - `GetRepoPublicKeyAsync(client, owner, repo) -> RepoPublicKey`
     - `EncryptSecretAsync(publicKeyBase64, secretValue) -> string` (base64 ciphertext)
     - `WriteSecretAsync(client, owner, repo, secretName, secretValue)`
     - `ProvisionApiKeyAsync(client, repos, apiKey) -> IReadOnlyList<ProvisionResult>`
  3. Wire concurrency cap: `SemaphoreSlim(5)` or `Parallel.ForEachAsync` with `MaxDegreeOfParallelism = 5`.
  4. Wire the client: depends on Finding 007's `IGitHubAppClient` — the provisioner needs an installation-auth'd HTTP client.
  5. Register in DI, invoke from `InstallationRouterService.HandleCallbackAsync` (Finding 008) and `ApiKeyRotationService.RotateAsync`.
  6. Remove the apology comment at `ApiKeyRotationService.cs:13-16`.
- **Is it "just a stub" or is scope missing?** Scope missing. The port explicitly cut this; it must be built.
- **Blockers**:
  - Finding 007 (`IGitHubAppClient` must exist to pass into the provisioner).
  - Story spec gap (see section 4).
  - Requires a new NuGet dependency (`NSec.Cryptography` — add to `Tamma.Api.csproj`).

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Services/SaaS/ApiKeyRotationService.cs` — invoke provisioner after DB write; remove apology comment.
  - `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs:41-107` — invoke provisioner at end of `HandleCallbackAsync` (paired with Finding 008).
  - `apps/tamma-elsa/src/Tamma.Api/Tamma.Api.csproj` — add NSec.Cryptography package reference.
  - `apps/tamma-elsa/src/Tamma.Api/Program.cs` — DI registration.
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/IGitHubSecretsProvisioner.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/GitHubSecretsProvisioner.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/ProvisionResult.cs`
- Tests to add:
  - `GitHubSecretsProvisionerTests.EncryptSecret_ProducesBase64SealedBox` — deterministic key-pair, asserts decryption via the matching private key recovers plaintext.
  - `GitHubSecretsProvisionerTests.EncryptSecret_MatchesTSTestVector` — port one test vector from TS unit tests to ensure cross-implementation parity.
  - `GitHubSecretsProvisionerTests.WriteSecret_UploadsEncryptedBody` — WireMock GitHub, assert PUT body shape includes `encrypted_value` and `key_id`.
  - `GitHubSecretsProvisionerTests.ProvisionApiKey_Batches5Concurrent` — assert HTTP client saw at most 5 concurrent requests.
  - `GitHubSecretsProvisionerTests.ProvisionApiKey_ArchivedRepo_ReturnsFailureContinuesBatch`
  - `GitHubSecretsProvisionerTests.ProvisionApiKey_403_ReturnsFailureDoesNotThrow`
- Estimated effort: 6-8h broken down as:
  - NuGet add + sealed-box helper + unit test: 2h
  - Octokit-style HTTP methods for pubkey + PUT secret: 2h
  - Batching + per-repo error handling: 1h
  - Integration tests (6 cases): 1-3h

## References

- TS source: `packages/api/src/services/github-secrets-provisioner.ts` (full file, commit `9e9a57c~1`)
- C# source:
  - `apps/tamma-elsa/src/Tamma.Api/Services/SaaS/ApiKeyRotationService.cs:13-16` (explicit admission)
  - (no other C# artifact)
- Story: **missing** — needs backfill (proposed 18-4.1)
- Related findings: `006-installation-created-no-provisioning.md`, `007-installation-callback-no-github-api-fetch.md`, `008-installation-callback-no-api-key-generation.md`, `018-schema-installation-no-apikey-columns.md`
- GitHub docs: [Encrypting secrets for the REST API](https://docs.github.com/en/rest/actions/secrets?apiVersion=2022-11-28#get-a-repository-public-key)

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Deferred (requires libsodium binding + GitHub App client) — seam wired
- **Commit**: `6dead62`
- **Notes**: Introduced `IGitHubSecretsProvisioner` with `ProvisionSecretAsync(installationId, repos, secretName, secretValue)` returning `IReadOnlyList<SecretProvisionResult>`. The default `NullGitHubSecretsProvisioner` returns one `Success=false, Error="github_client_not_configured"` per repo so callers — `InstallationRouterService.IssueInstallationKeyAsync` and `ApiKeyRotationService.RotateInternalAsync` — emit accurate per-repo summaries. Once a real implementation is registered ahead of the Null fallback (libsodium via `NSec.Cryptography` + Octokit `actions.getRepoPublicKey` + `actions.createOrUpdateRepoSecret`), both call sites push automatically without code changes. Real implementation is the responsibility of the GitHub App client port story (which must also add the NuGet `NSec.Cryptography` reference).
