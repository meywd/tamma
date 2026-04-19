# Finding 020: SaaS `POST /installations/:id/rotate-key` — Guid id vs numeric `installation_id` URL mismatch

**Scope**: engine (SaaS)
**Severity**: P1 (feature broken — deployed clients with numeric installation ids hit 400)
**Status**: Data-model regression (URL schema changed)
**Estimated port effort**: 2h

## 1. What's in TS

- File: `packages/api/src/routes/saas/key-rotation.ts:24-37` (9e9a57c~1)

```typescript
// packages/api/src/routes/saas/key-rotation.ts:24-37 (9e9a57c~1)
app.post(
  '/api/v1/installations/:id/rotate-key',
  async (request, reply) => {
    const installationId = parseInt(request.params.id, 10);
    if (Number.isNaN(installationId)) {
      return reply.status(400).send({ error: 'Invalid installation ID' });
    }

    const installation = await options.installationStore.getInstallation(installationId);
```

The URL parameter is the GitHub-assigned numeric `installation_id` (e.g. `12345678`). The TS `GitHubInstallation` table (archived migration 001) has `installation_id BIGINT PRIMARY KEY` — matching the URL parameter directly.

## 2. What's in C#

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/SaaSEndpoints.cs:155-190`

```csharp
// SaaSEndpoints.cs:155-164 (current)
public static async Task<IResult> RotateInstallationKey(
    Guid id,
    [FromServices] IApiKeyRotationService rotation,
    ClaimsPrincipal principal)
{
    // ...
    var result = await rotation.RotateAsync(id, callerUserId.Value);
```

- Service: `apps/tamma-elsa/src/Tamma.Api/Services/SaaS/ApiKeyRotationService.cs:48`

```csharp
public async Task<KeyRotationResult> RotateAsync(Guid installationEntityId, Guid callerUserId)
{
    var installation = await _installations.GetByEntityIdAsync(installationEntityId);
```

The C# route parameter is `Guid id` — the internal surrogate key from `GitHubInstallation.Id` (see entity at `Tamma.Data/Entities/GitHubInstallation.cs:5`). The **numeric** `InstallationId` is a separate field used for GitHub correlation.

### The URL contract change

TS: `POST /api/v1/installations/12345678/rotate-key` (the numeric GitHub installation id).
C#: `POST /api/v1/installations/1a2b3c4d-5e6f-7890-abcd-ef0123456789/rotate-key` (the server-side Guid).

A deployed client (CLI, dashboard widget, partner integration) that was built against TS knows the numeric installation id from GitHub's own webhook payload. It has no idea about the server-side Guid surrogate — that was introduced in the C# port.

## 3. The gap

For a deployed client calling `POST /api/v1/installations/12345678/rotate-key` (numeric, as the TS API was documented to accept):

- TS: 200 with `{ok: true, installationId: 12345678, keyPrefix, provisioning: {...}}`.
- C#: 400 — `"12345678"` cannot be parsed as `Guid`. ASP.NET returns a model-binding error.

For a new client calling with the internal entity Guid (only discoverable from an admin query):

- TS: 400 `Invalid installation ID` (not a number).
- C#: 200.

The two APIs cover disjoint URL spaces. Every pre-existing key-rotation automation breaks.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md` (GitHub App install flow).
- Also `docs/stories/epic-19/19-1-api-consolidation-to-csharp.md` — Phase 3 migrates the key rotation endpoint.
- The C# route signature appears to have been a pragmatic choice: with the new schema keying `GitHubInstallation` by Guid surrogate, using the Guid in the URL avoided a lookup. But it silently rewrote the public contract.
- Story alignment:
  - [x] Matches TS behavior (C# is a contract regression)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Data-model regression → URL contract change.
- **What's needed to finish**:
  1. Change the route parameter to `long installationId` to match the GitHub-assigned id.
  2. Look up the installation via `GetByInstallationIdAsync(long)` (already exists on `IInstallationRepository`).
  3. Use its `Id` (Guid) internally to call the existing rotation service, or refactor `ApiKeyRotationService.RotateAsync` to accept the GitHub installation id directly.
  4. Update OpenAPI spec.
- **Is it "just a stub" or is scope missing?** Implementation drift during the schema refactor. Pure contract fix.
- **Blockers**: none.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/SaaSEndpoints.cs:155-190` — `Guid id` → `long installationId`.
  - `apps/tamma-elsa/src/Tamma.Api/Services/SaaS/ApiKeyRotationService.cs:48` — accept `long installationId`, look up via `GetByInstallationIdAsync`.
  - Program.cs / route registration — update the route template if needed.
- Tests to add:
  - `RotateInstallationKey_AcceptsNumericId`
  - `RotateInstallationKey_RejectsNonNumericId`
  - `RotateInstallationKey_ResolvesGuidViaInstallationId`
  - `RotateInstallationKey_404_WhenInstallationMissing`
- Estimated effort: 2h — route + service change 1h, tests 1h.

## References

- TS source: `packages/api/src/routes/saas/key-rotation.ts:24-37`
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/SaaSEndpoints.cs:155-190`, `Services/SaaS/ApiKeyRotationService.cs:48`
- Story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md`
- Archived SQL: `database/archived-sql-migrations/001_github_installations.sql:5` (`installation_id BIGINT PRIMARY KEY`)
- Related findings: `021-key-rotation-no-reprovision.md` (the more-severe sibling), `030-installation-soft-delete-vs-hard.md`

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: c9dd51e
- **Notes**: Route param flipped from `Guid id` to `long id` to match the
  GitHub-issued numeric installation id (TS contract). Server-side resolves
  to the entity Guid via `IInstallationRepository.GetByInstallationIdAsync`
  using the new `IApiKeyRotationService.RotateByInstallationIdAsync`
  method. Internal `RotateAsync(Guid)` retained for callers that already
  hold the entity Guid. Tests updated to use a numeric id in the URL.
