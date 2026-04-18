# Finding 006: `installation.created` webhook does not provision API key or fetch repositories via API

**Scope**: github
**Severity**: P0 (cutover-blocking)
**Status**: Incomplete (partial port, missing N behaviors)
**Estimated port effort**: 6-8h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/github/github-webhook.ts`.

- File: `packages/api/src/routes/github/github-webhook.ts:141-162`
- Contract/behavior: On `installation.created` the TS handler (a) upserted the installation row with `apiKeyHash: null, apiKeyPrefix: null, apiKeyEncrypted: null` placeholders, and (b) seeded `github_installation_repos` from `payload.repositories` (the list GitHub includes on `installation.created` delivery).

```typescript
// packages/api/src/routes/github/github-webhook.ts:141-162 (9e9a57c~1)
if (action === 'created') {
  await options.installationStore.upsertInstallation({
    installationId: id,
    accountLogin: String(account['login']),
    accountType: String(account['type']) as 'User' | 'Organization',
    appId: options.appId,
    permissions: (installation['permissions'] ?? {}) as Record<string, string>,
    suspendedAt: null,
    apiKeyHash: null,
    apiKeyPrefix: null,
    apiKeyEncrypted: null,
  });

  // Store repos from the installation event
  const repositories = (payload['repositories'] ?? []) as Array<Record<string, unknown>>;
  const repos = repositories.map((repo) => ({
    repoId: Number(repo['id']),
    owner: String((repo['full_name'] as string).split('/')[0]),
    name: String(repo['name']),
    fullName: String(repo['full_name']),
  }));
  await options.installationStore.setRepos(id, repos);
}
```

Note: the webhook itself did NOT generate an API key — key generation was the job of the OAuth-callback path (`github-callback.ts`; see Finding 007). The webhook merely prepared the row with null placeholder columns. The actual key-generation and secret-provisioning happened on the user return leg of the install flow, where `github-callback.ts:105-148` called `generateApiKey()`, `hashApiKey()`, `updateApiKeyHash()`, and `provisioner.provisionApiKey()`.

- Dependencies: `IGitHubInstallationStore.upsertInstallation`, `IGitHubInstallationStore.setRepos`.
- Tests that exercised this: webhook integration tests asserted the upsert fields including the three `apiKey*` nulls and the repo list seeding.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs:207-266`
- Contract/behavior: On `installation.created` the C# handler upserts the installation row (no `apiKey*` columns exist on the entity — see Finding 018 for the schema shift) and seeds repos from `payload.repositories`. This surface matches TS **for the webhook leg**.

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs:230-266 (current)
var stored = await _installations.UpsertAsync(new GitHubInstallation
{
    InstallationId = installationId.Value,
    AccountLogin = accountLogin,
    AccountType = accountType,
    AppId = appId,
    Permissions = permissions
});

// Seed initial repositories (if the payload carries them).
if (payload.TryGetProperty("repositories", out var reposEl) &&
    reposEl.ValueKind == JsonValueKind.Array)
{
    foreach (var repo in reposEl.EnumerateArray())
    {
        var repoId = TryGetLong(repo, "id");
        var fullName = TryGetString(repo, "full_name");
        if (repoId is not null && !string.IsNullOrWhiteSpace(fullName))
        {
            await _installations.AddRepoAsync(
                stored.Id, repoId.Value, fullName);
        }
    }
}

await EmitEventAsync(
    "INSTALLATION.CREATED.SUCCESS",
    stored.TenantId,
    new Dictionary<string, object?>
    {
        ["installationId"] = installationId,
        ["accountLogin"] = accountLogin,
        ["accountType"] = accountType
    });
```

So the webhook upsert + repo seeding is ported correctly (and actually improved by emitting a domain event, which TS did not). The gap named by the audit summary — "Gap 3: `installation.created` does not provision API key or fetch repos" — is more precisely: **the full install flow (webhook + callback) no longer results in an API key being generated and secret-provisioned anywhere**, because the callback half was not ported (Finding 007). This webhook finding is thus a companion to Finding 007 and Finding 008 — the three together describe what install was supposed to do end-to-end and what C# now does.

On the webhook path specifically, the only true regression vs TS is that `TenantId` is not set during `UpsertAsync` here (line 230-237 doesn't set it) — the current webhook handler assumes the tenant link happens separately via `HandleCallbackAsync`. But since that callback tenant-link path relies on an already-authenticated user and does not itself fetch repos or provision keys (Findings 007, 008), the overall story is: whichever leg arrives first creates the row, the second leg hopes to fill in the missing pieces, and at no point is an API key issued.

- Dependencies: `IInstallationRepository.UpsertAsync` and `.AddRepoAsync`; `IEventRepository.AppendAsync`.
- Tests: `InstallationRouterServiceTests` covers the created branch; validates the upsert and event emission but does not assert any key-generation side effect (there isn't one to assert).

## 3. The gap

Concrete behavioral difference — what a caller or user experiences differently.

- TS end-to-end install flow: webhook upserts placeholder row → callback fetches installation + repos via GitHub API → callback generates `TAMMA_API_KEY` → callback hashes the key, writes the hash/prefix/encrypted-copy to the row → callback provisions the plaintext key to every repo as a GitHub Actions secret via libsodium sealed-box.
- C# end-to-end install flow: webhook upserts row → callback links row to tenant → **no key generation anywhere, no repo fetching via GitHub API, no secret provisioning**.
- For a caller who installs the Tamma GitHub App on a fresh org, TS returned the user to a state where every repo had a `TAMMA_API_KEY` secret and the backend had the hash/prefix stored. C# returns the user to a state where neither the backend nor the repos have a key. A repo-side Actions worker attempting to authenticate to the Tamma API sends no `Authorization` header and is rejected at the edge.
- In production with existing data / deployed clients, this means: **customer onboarding is broken at the very last step**. A customer completes the GitHub App install UI, sees the success page, and then discovers that their GitHub Actions workflows cannot call the Tamma API because there is no secret. They must manually request an API key from the dashboard (once that flow exists) and manually paste it as `TAMMA_API_KEY` into each repo's Settings → Secrets. This is a non-trivial self-service regression and makes the SaaS Actions-worker flow unusable without operator intervention.

Error paths:
- TS error path: repo provisioning partial failure was non-fatal — key was stored server-side and the user saw the success page; a warning log captured per-repo failures (`github-callback.ts:132-139`).
- C# error path: no error path because no operation is attempted.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md`
- Story's acceptance criteria for this behavior: AC #3 ("Installation callback handles the GitHub App `installation.created` webhook and links the new installation to the user's org based on the `state` parameter") focuses on the linking concern. API key provisioning is not in the AC at all. However, the existence of `TAMMA_API_KEY` as a customer-repo secret is implicit in the SaaS architecture documented in `ApiKeyRotationService.cs:13-16` ("The TS version also re-provisioned the rotated key to GitHub-hosted repo secrets via Octokit"). So key provisioning is a cross-cutting requirement that no story owns.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS) — TS covered this; C# does not
  - [ ] Matches C# behavior (story was updated during port; TS was ahead of spec)
  - [ ] Describes a third behavior
  - [x] No story — spec gap; must be backfilled before remediation

There is an implicit gap: the story 18-4 AC list never mentions libsodium, sealed-box, `TAMMA_API_KEY`, or provisioning. Story 18-4 needs a new AC (say, AC #11: "On successful installation, an API key is generated, stored as `(hash, prefix)` on the installation, and provisioned to every accessible repo as a GitHub Actions secret named `TAMMA_API_KEY`"). Until that AC is added the scope of "install done" is under-spec'd.

## 5. Status

- **Classification**: Incomplete (partial port). The webhook leg was ported; the callback leg was gutted (Finding 007); the secrets provisioner was not ported (Finding 013).
- **What's needed to finish** (on the webhook side — see Findings 007, 008, 013 for the rest):
  1. Decide the split: does the webhook or the callback own key provisioning? TS put it on the callback (user-driven). That's the right call because a webhook can race and fire before the user finishes authenticating.
  2. Keep the webhook as a placeholder-upsert + repo-seed. This finding's webhook logic is already correct.
  3. The "missing provisioning" behavior is implemented via Findings 007 (callback fetches + generates key) and 013 (secrets provisioner ported).
  4. Ensure the webhook-created row does NOT clobber a previously callback-created row that already has `TenantId` set. Today `UpsertAsync` at `InstallationRepository.cs:8-30` preserves `TenantId` only because the webhook path doesn't set it — correct by accident. Add a test.
- **Is it "just a stub" or is scope missing?** Scope missing. The install flow end-to-end was a multi-leg dance; half the legs were not ported.
- **Blockers**:
  - Finding 007 (callback implementation).
  - Finding 013 (secrets provisioner).
  - Finding 018 (schema shift — the C# model deliberately doesn't have `ApiKeyHash/Prefix/Encrypted` on the installation entity; those moved to the `api_keys` table, which changes where the "bind key to installation" write lands).

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs:207-266` — no behavioral change required, but add an assertion test that `TenantId` is preserved across webhook re-delivery.
  - (Remediation of the overall install flow lives in Findings 007 / 013 / 018.)
- Files to create: see Findings 007, 013.
- Tests to add:
  - `InstallationRouterServiceTests.HandleWebhook_InstallationCreated_DoesNotClobberTenantLink` — seed a row via `CreateAsync` with `TenantId = someGuid`, fire an `installation.created` webhook for the same `installationId`, assert `TenantId` is still `someGuid`.
  - `InstallationRouterServiceTests.HandleWebhook_InstallationCreated_SeedsReposFromPayload`
  - End-to-end test pending Findings 007/013 that asserts the combined webhook+callback flow results in a key row in `api_keys` and a successful (or attempted) secret provision.
- Estimated effort: 6-8h combined, broken down as:
  - Webhook race-safety test: 0.5h
  - Callback key-generation (Finding 007): 3-4h
  - Secrets provisioner (Finding 013): see that finding
  - Integration test covering the combined flow: 2-3h

## References

- TS source: `packages/api/src/routes/github/github-webhook.ts:141-162`, `packages/api/src/routes/github/github-callback.ts:105-148` (commit `9e9a57c~1`)
- C# source:
  - `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs:207-266`
  - `apps/tamma-elsa/src/Tamma.Api/Services/SaaS/ApiKeyRotationService.cs:13-16` (comment admitting the drop)
- Story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md` (missing AC for key provisioning)
- Related findings: `007-installation-callback-no-github-api-fetch.md`, `008-installation-callback-no-api-key-generation.md`, `013-secrets-provisioner-libsodium-missing.md`, `018-schema-installation-no-apikey-columns.md`
