# Finding 021: Key rotation does not re-provision to GitHub repo secrets

**Scope**: engine (SaaS)
**Severity**: P0 (cutover-blocking — security incident response broken)
**Status**: Not-yet-implemented (service admits this in comments)
**Estimated port effort**: 8h

## 1. What's in TS

- File: `packages/api/src/routes/saas/key-rotation.ts:50-78` (9e9a57c~1)

```typescript
// packages/api/src/routes/saas/key-rotation.ts:50-78 (9e9a57c~1)
// Generate new key
const newKey = generateApiKey();
const newHash = hashApiKey(newKey);
const newPrefix = getApiKeyPrefix(newKey);

// Update database
await options.installationStore.updateApiKeyHash(installationId, newHash, newPrefix);

// Re-provision to all repos
const repos = await options.installationStore.listRepos(installationId);

let provisionResults: Array<{ owner: string; repo: string; success: boolean; error?: string }> = [];
try {
  const octokit = await options.createOctokit(installationId);
  provisionResults = await provisioner.provisionApiKey(
    octokit,
    repos.map((r) => ({ owner: r.owner, name: r.name })),
    newKey,
  );
} catch (err) {
  const message = err instanceof Error ? err.message : String(err);
  app.log.error({ msg: 'Failed to provision rotated key to repos', error: message, installationId });
  // Key is rotated in DB even if provisioning fails — user can retry
}
```

`GitHubSecretsProvisioner.provisionApiKey(...)` writes the new plaintext to every repo's GitHub Actions secrets. The rotation returns `{ok, installationId, keyPrefix, provisioning: {total, success, failed, results[]}}` so the caller can see which repos succeeded.

Security model: when a key is leaked, rotation **must atomically** replace both (a) the DB hash used to validate inbound API calls and (b) the secret deployed to every repo that uses it. Otherwise rotated workflows can't authenticate.

## 2. What's in C#

- File: `apps/tamma-elsa/src/Tamma.Api/Services/SaaS/ApiKeyRotationService.cs:9-16`

The service header admits the gap in a comment:

```csharp
/// Ported from the deleted TypeScript <c>routes/saas/key-rotation.ts</c>
/// (Epic 19 Phase 3). The TS version also re-provisioned the rotated key to
/// GitHub-hosted repo secrets via Octokit; that re-provisioning is out of
/// scope for this C# port because we do not yet wire a GitHub App client.
```

- File: `ApiKeyRotationService.cs:85-147` — generates a new key, hashes, updates the `api_keys` table, emits `API_KEY.ROTATED` audit event. Then returns the plaintext key to the caller. **Nothing writes to GitHub Actions secrets.**

```csharp
// ApiKeyRotationService.cs:141-147 (current)
return new KeyRotationResult(
    Success: true,
    PlaintextKey: plaintext,
    KeyPrefix: keyPrefix,
    KeyId: stored.Id,
    ErrorReason: null);
```

Response reaches the endpoint at `SaaSEndpoints.cs:180-189`:

```csharp
return Results.Ok(new
{
    ok = true,
    installationId = id,
    keyId = result.KeyId,
    keyPrefix = result.KeyPrefix,
    // One-time plaintext reveal. The caller has exactly one opportunity
    // to capture and surface it to the end-user.
    apiKey = result.PlaintextKey
});
```

No `provisioning` field. The caller has no idea what happened (or didn't happen) on GitHub.

## 3. The gap

- TS did: atomic rotation — DB hash updated AND GitHub repo secrets re-provisioned with the new plaintext. Caller gets a report of which repos succeeded.
- C# does: DB hash updated. GitHub repo secrets still hold the old plaintext. Caller thinks rotation succeeded.

For a compromised-key incident:

- TS: user rotates → old key invalidated in DB → repo secrets updated → running workflows continue with the new key → 30 seconds of cleanup.
- C#: user rotates → old key invalidated in DB → repo secrets still contain the old plaintext → every deployed engine starts returning 401 Unauthorized → autonomous workflows halt → user must manually update every repo's `TAMMA_API_KEY` Actions secret.

The security story here is worse than "feature missing" — rotating a compromised key **immediately breaks every deployed engine** until a human updates secrets by hand. Some users will delay rotation to avoid the outage, which extends the compromise window.

Also, finding #30 in the audit summary notes the `api_keys` table has no `ApiKeyEncrypted` column — so even if provisioning were wired, we'd have no way to re-push the plaintext because we don't retain it encrypted-at-rest. The TS schema had `api_key_encrypted` on `github_installations` explicitly for this purpose (see archived pg-installation-store.ts upsert columns list).

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md` (GitHub App flow).
- Also cross-ref `docs/stories/epic-16/16-5-role-based-access-control.md` (key rotation as a privileged op).
- Story alignment:
  - [x] Matches TS behavior (C# is a regression, explicitly admitted in code comments)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Not-yet-implemented — author knew the gap existed and shipped without it.
- **What's needed to finish**:
  1. Wire an Octokit.NET-equivalent GitHub App client (Octokit.GraphQL, Octokit.net, or a direct HttpClient wrapper) — shared blocker with findings 005-011.
  2. Port `GitHubSecretsProvisioner` to C# — computes LibSodium sealed box, calls `PUT /repos/{owner}/{repo}/actions/secrets/{secret_name}`.
  3. **Schema change**: add `ApiKeyEncrypted` column somewhere (either back on `github_installations` or on the `api_keys` table) — encrypted using a KMS-managed key — so the plaintext can be retrieved at rotation time without the user re-supplying it.
     - Alternative: require the plaintext to be kept in memory just for the rotation request, and fail if secrets provisioning fails (not atomic but avoids schema churn).
  4. Wrap rotation + provisioning in a transaction-like flow with rollback if secrets fail.
  5. Return `{provisioning: {total, success, failed, results[]}}` in the response.
- **Is it "just a stub" or is scope missing?** Scope missing — GitHub client was never wired; schema does not support re-provisioning.
- **Blockers**: GitHub App client (shared with 005-011). Schema change for `ApiKeyEncrypted` (cross-ref admin-db audit).

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Services/SaaS/ApiKeyRotationService.cs:85-147` — add provisioning step after DB rotation.
  - `apps/tamma-elsa/src/Tamma.Api/Services/SaaS/IApiKeyRotationService.cs` — extend `KeyRotationResult` with `ProvisioningSummary`.
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/SaaSEndpoints.cs:180-189` — include provisioning summary in response.
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/IGitHubSecretsProvisioner.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/GitHubSecretsProvisioner.cs` (LibSodium sealed box + PUT actions/secrets).
  - EF migration adding `ApiKeyEncrypted` column.
- Tests to add:
  - `RotateKey_ProvisionsToAllRepos_Success`
  - `RotateKey_PartialFailure_ReportsPerRepoStatus`
  - `RotateKey_AllFail_StillRotatesDb_ReturnsFailureList`
  - Integration test with a fake GitHub API (WireMock) simulating sealed-box provisioning.
- Estimated effort: 8h
  - GitHub App client wiring: 2h (cross-ref 005-011 — only done once)
  - `GitHubSecretsProvisioner` port: 3h (LibSodium sealed box is non-trivial)
  - `ApiKeyEncrypted` column + retrieval: 1h
  - Response plumbing + tests: 2h

## References

- TS source: `packages/api/src/routes/saas/key-rotation.ts`, `packages/api/src/services/github-secrets-provisioner.ts`
- C# source: `apps/tamma-elsa/src/Tamma.Api/Services/SaaS/ApiKeyRotationService.cs` (explicit TODO in header)
- Story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md`
- Related findings: `020-saas-key-rotation-id-type.md`, 005-011 (shared GitHub client blocker), cross-ref `docs/audit/port-gaps/admin-db/` on `api_keys` schema
- CLAUDE.md section: "Security Requirements — Credential Management"

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: `4e1e0e4` (Libsodium provisioner); prior `a3d2e7e` wired the result shape
- **Notes**: `LibsodiumGitHubSecretsProvisioner` now lands when the GitHub
  App is configured, so `ApiKeyRotationService.RotateInternalAsync`'s
  `_provisioner.ProvisionSecretAsync(...)` call re-provisions the new
  plaintext to every active repo via libsodium sealed-box + Octokit's
  `Repository.Actions.Secrets.CreateOrUpdate`. Per-repo outcomes surface
  in the documented `{total, success, failed, results[]}` summary; the
  rotation still commits the new DB hash even when some repos fail (same
  posture as TS), and per-repo error strings include archived-repo /
  forbidden / rate-limit detail so operators know which repos need
  manual attention. The `ApiKeyEncrypted`-column follow-up (to permit
  re-provisioning without the user re-supplying the plaintext) is
  still deferred — rotation re-provisions the fresh plaintext from the
  rotation request itself, which matches TS and covers the
  compromised-key-response use case.
