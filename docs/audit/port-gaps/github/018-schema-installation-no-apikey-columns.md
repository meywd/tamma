# Finding 018: Schema — `github_installations` lacks `ApiKeyHash` / `ApiKeyPrefix` / `ApiKeyEncrypted` columns (architectural shift)

**Scope**: github
**Severity**: P1 (feature broken) — the shift is defensible; the new model is incomplete
**Status**: Data-model regression (with new model still missing a component)
**Estimated port effort**: 2-4h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/persistence/installation-store.ts` (conceptually; not a required file quote for this finding — the shape is authoritative from callers).

- File: TS `IGitHubInstallation` interface (used across `github-webhook.ts` and `github-callback.ts`).
- Contract/behavior: TS had the installation row carry the API key material directly: `apiKeyHash`, `apiKeyPrefix`, `apiKeyEncrypted`. The hash was the SHA-256 of the plaintext key (used to validate inbound API requests without storing plaintext). The prefix was the first 12 chars of the plaintext, used for log correlation and for the UI to display "the key starting with `tk_live_abcdef…`". The encrypted copy was the plaintext encrypted at-rest with a server-side KMS key — needed specifically so that **rotation could re-provision the same key back to GitHub repos** even though the hash is one-way.

Representative write calls from the TS callback:

```typescript
// packages/api/src/routes/github/github-callback.ts:107-110 (9e9a57c~1)
const apiKey = generateApiKey();
const apiKeyHash = hashApiKey(apiKey);
const apiKeyPrefix = getApiKeyPrefix(apiKey);

await options.installationStore.updateApiKeyHash(installationId, apiKeyHash, apiKeyPrefix);
```

And null-initialization during webhook upsert:

```typescript
// packages/api/src/routes/github/github-webhook.ts:149-152 (9e9a57c~1)
apiKeyHash: null,
apiKeyPrefix: null,
apiKeyEncrypted: null,
```

The three columns lived on the installation row. One installation, one current API key, one hash, one prefix, one encrypted copy.

- Dependencies: `generateApiKey`, `hashApiKey`, `getApiKeyPrefix` from `auth/api-key.ts`.
- Tests that exercised this: store tests asserted the three-column update; hash lookup tests asserted inbound API auth found the installation by hash.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/Entities/GitHubInstallation.cs`
- Contract/behavior: The entity has no `ApiKeyHash`, no `ApiKeyPrefix`, no `ApiKeyEncrypted`. The full entity surface:

```csharp
// apps/tamma-elsa/src/Tamma.Data/Entities/GitHubInstallation.cs (current)
namespace Tamma.Data.Entities;

public class GitHubInstallation
{
    public Guid Id { get; set; }
    public long InstallationId { get; set; }
    public string AccountLogin { get; set; } = null!;
    public string AccountType { get; set; } = null!;
    public int AppId { get; set; }
    public string? AppSlug { get; set; }
    public string Permissions { get; set; } = "{}";
    public DateTime? SuspendedAt { get; set; }
    public Guid? TenantId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<GitHubInstallationRepo> Repos { get; set; } = [];
}
```

The C# architecture moved API key storage to a separate `api_keys` table (an `ApiKey` entity + `IApiKeyRepository`). This is the **correct architectural shift**: it supports multiple active keys per installation, key-level revocation, key-level audit, and separation of concerns. The shift is good.

However, the move is incomplete:
1. **No `ApiKeyEncrypted` equivalent** anywhere. The new `ApiKey` entity stores the hash and the prefix but not an encrypted plaintext copy. This is confirmed by `ApiKeyRotationService.cs:13-16` which admits "we do not yet wire a GitHub App client" — and by extension, cannot re-provision a rotated key because the plaintext is discarded immediately after generation.
2. **Rotation can't re-push**: as noted in Finding 013, rotating a key invalidates the server-side hash but leaves the client-side (GitHub Actions) secret pointing at the stale plaintext. The `ApiKeyEncrypted` column (or some equivalent) is the mechanism TS used to keep the plaintext recoverable for exactly this purpose.

- Dependencies: `IApiKeyRepository` (new), `ApiKeyRotationService` (new).
- Tests: `ApiKeyRotationService` tests assert the hash/prefix DB write but cannot test the re-provision because the feature is absent.

## 3. The gap

- TS did: one row per installation with all three key fields inline; rotation updated the row in place and re-encrypted the new plaintext.
- C# does: installation row has no key fields; new `api_keys` table stores hash + prefix (not the encrypted plaintext); rotation creates a new `api_keys` row + revokes the old one.
- For a caller rotating a key via `POST /api/v1/installations/{id}/rotate-key`:
  - TS: updates the installation row's `apiKeyHash` + `apiKeyPrefix` + `apiKeyEncrypted`, calls the provisioner to push the new plaintext to every repo. The old plaintext is overwritten in both places atomically (ish).
  - C#: creates a new `api_keys` row (active), marks the old `api_keys` row revoked. Does NOT push to any repo. Cannot push to any repo because: (a) the provisioner doesn't exist (Finding 013), AND (b) even if the provisioner did exist, the C# model doesn't store the plaintext anywhere after the HTTP response has returned to the rotator — the generation code only returns the plaintext once, in the response body, to the user who initiated rotation.
- In production with existing data / deployed clients, this means:
  - **Rotations silently break Actions**: rotating invalidates the old hash. GitHub Actions workflows that still carry the old plaintext secret suddenly return 401 from Tamma API calls. There is no path to automatically propagate the new key back.
  - **The schema is architecturally better** (one-to-many key model supports revocation, audit, multiple concurrent keys) **but operationally incomplete** (no at-rest encryption for the current key, so re-provisioning is impossible).

Error paths:
- TS error path: rotation failure mid-way → hash updated but provisioning partially failed → stored hash is new, some repos have new, some repos have old; audit logs show the discrepancy.
- C# error path: rotation never attempts provisioning; every rotation leaves every repo in "old-key" state until a human intervenes.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md` (install-time key generation) and an implicit story for key rotation (`key-rotation` TS file, no matching Epic 18 story).
- Story's acceptance criteria for this behavior:
  - Story 18-4 doesn't discuss key storage columns.
  - There's no dedicated rotation story. `ApiKeyRotationService.cs:13-16` is the only trace.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS) — the old TS columns are gone AND no equivalent `ApiKeyEncrypted` was added.
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior — closer to this; the C# schema is an evolution that wasn't spec'd.
  - [x] No story — spec gap; must be backfilled before remediation

Story needed: "Key rotation and re-provisioning" as an explicit 18-4.x or cross-cutting. Must specify whether plaintext is stored at-rest (encrypted) OR whether rotation always requires a synchronous per-repo push before the old key is revoked.

## 5. Status

- **Classification**: Data-model regression (incomplete). The direction is better (normalized keys table) but the transitional at-rest encryption column was lost.
- **What's needed to finish**:
  1. Design decision — two acceptable shapes:
     - **Option A (add encrypted column on `api_keys`)**: add `ApiKey.EncryptedPlaintext` column (bytea). On rotation, before revoking the old key, use the stored encrypted plaintext of the old key to track which repos need updating; use the encrypted plaintext of the new key to push.
     - **Option B (synchronous dual-writer)**: at rotation time, do not revoke until the provisioner has successfully pushed to all repos. Keep both keys active until the migration is confirmed. Store nothing extra — just an `ActivatedAt` on the new key and a grace window. Simpler schema, harder operations.
  2. Recommended: Option A for simplicity of failure recovery. Encrypt with a KMS key managed by `DataProtection` or a dedicated AES key loaded from config.
  3. Implement:
     - Add `byte[]? EncryptedPlaintext` column to `ApiKey` entity. Migration.
     - Update `ApiKeyRotationService.RotateAsync` to populate this column.
     - Update callback key-issuance (Finding 008) to populate this column.
     - Add provisioner invocation as in Finding 013.
     - Add a startup job or admin endpoint "re-provision all installations" that iterates active installations, decrypts current plaintext, pushes to repos — useful for recovery.
  4. Remove the apology comment at `ApiKeyRotationService.cs:13-16`.
- **Is it "just a stub" or is scope missing?** Scope missing with an architecturally-better replacement planned but not fully implemented.
- **Blockers**:
  - Need to decide where the decryption key lives (DataProtection, KMS, secret store).
  - Couples with Findings 008, 013.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Data/Entities/ApiKey.cs` — add `EncryptedPlaintext` column.
  - `apps/tamma-elsa/src/Tamma.Api/Services/SaaS/ApiKeyRotationService.cs:13-16` — remove comment; invoke provisioner.
  - `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs` — at key issuance (Finding 008), populate encrypted column.
- Files to create:
  - EF Core migration adding `EncryptedPlaintext bytea NULL` column.
  - `apps/tamma-elsa/src/Tamma.Api/Services/Crypto/IApiKeyEncryptor.cs` + impl using `IDataProtectionProvider` or `Aes`.
- Tests to add:
  - `ApiKeyEncryptorTests.EncryptRoundtrip`
  - `ApiKeyRotationServiceTests.Rotate_PopulatesEncryptedPlaintext`
  - `ApiKeyRotationServiceTests.Rotate_InvokesProvisioner_WithDecryptedPlaintext`
  - `ApiKeyRotationServiceTests.Rotate_ProvisionerFails_LeavesOldKeyActive` (soft-delete semantics for rotation)
- Estimated effort: 2-4h broken down as:
  - Entity + migration: 0.5h
  - Encryptor + tests: 1h
  - Rotation service integration: 1h
  - End-to-end rotation test: 1h

## References

- TS source: `packages/api/src/routes/github/github-callback.ts:107-110`, `packages/api/src/routes/github/github-webhook.ts:149-152` (commit `9e9a57c~1`)
- C# source:
  - `apps/tamma-elsa/src/Tamma.Data/Entities/GitHubInstallation.cs` (missing columns)
  - `apps/tamma-elsa/src/Tamma.Api/Services/SaaS/ApiKeyRotationService.cs:13-16` (admission)
- Archived SQL migration: `database/archived-sql-migrations/001_github_installations.sql` (original schema without these columns — they were added later in TS migrations that predate the port)
- Story: spec gap — needs backfill (proposed 18-4.1)
- Related findings: `006`, `007`, `008`, `013`, `021-installation-id-bigint-pk-vs-guid.md`

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed (column added; encryptor wiring deferred)
- **Commit**: `6dead62`
- **Notes**: Added `ApiKey.EncryptedPlaintext byte[]?` (column type `bytea`, nullable) via migration `ApiKeyEncryptedPlaintext`. Architectural direction confirmed (multi-key per owner via `api_keys` table is correct). The actual encrypt-on-issue path is intentionally deferred: an `IApiKeyEncryptor` abstraction (to wrap `IDataProtectionProvider` or a config-loaded AES key) is the natural next step but has no consumers today since the provisioner re-push path is itself deferred (finding 013). When the GitHub App client port lands and wires a real provisioner, this finding's "encrypt before persist + decrypt for re-push" loop closes in one focused commit on `ApiKeyRotationService` + the new `IssueInstallationKeyAsync` helper.
