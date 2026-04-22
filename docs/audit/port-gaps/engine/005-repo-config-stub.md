# Finding 005: `GET /api/engine/repo-config` returns `{configured: false}`

**Scope**: engine
**Severity**: P0 (cutover-blocking — convention templates / prompt injection relies on this)
**Status**: Not-yet-implemented (stub)
**Estimated port effort**: 3h

## 1. What's in TS

- File: `packages/api/src/routes/engine/engine-context-routes.ts:258-307` (9e9a57c~1)

```typescript
// packages/api/src/routes/engine/engine-context-routes.ts:258-307 (9e9a57c~1)
fastify.get(
  '/api/engine/repo-config',
  async (
    request: FastifyRequest<{ Querystring: { repo?: string; branch?: string } }>,
    reply: FastifyReply,
  ) => {
    const repoParam = request.query.repo ?? '';
    if (!repoParam) {
      return reply.status(400).send({ error: 'Missing required query parameter: repo' });
    }
    const parts = repoParam.split('/');
    if (parts.length !== 2 || parts[0] === '' || parts[1] === '') {
      return reply.status(400).send({
        error: `Invalid repo format: "${repoParam}". Expected "owner/repo".`,
      });
    }
    const owner = parts[0]!;
    const repo = parts[1]!;
    const branch = request.query.branch ?? 'main';
    const reader = options?.repoConfigReader;
    if (!reader) {
      return reply.send({});
    }
    try {
      const config = await reader.readRepoConfig(owner, repo, branch);
      return reply.send(config);
    } catch (error) {
      fastify.log.error({ repo: repoParam, error }, 'Failed to read repo config');
      return reply.send({});
    }
  },
);
```

Contract: given `?repo=owner/repo&branch=main`, fetch `.tamma/config.json` from the repo's default branch and return the parsed JSON. Graceful degradation: when the reader is not wired (self-hosted dev mode) or fetch fails, return `{}` not 500 — the Elsa activity expects empty-object fallthrough.

The `conventions` field returned here is the primary way users customize per-repo LLM coding conventions (per CLAUDE.md "Convention Templates" section).

## 2. What's in C#

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:69-70`

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:69-70
public static Task<IResult> GetRepoConfig() =>
    Task.FromResult(Results.Ok(new { configured = false }));
```

No parameters bound (no `repo`, no `branch`). Returns a fixed object that looks like a feature flag rather than a config document.

### Deployed Elsa caller

```csharp
// apps/tamma-elsa/src/Tamma.Activities/Context/ReadRepoConventionsActivity.cs:70-79
var httpClient = _httpClientFactory.CreateClient();
var url = $"{callbackUrl.TrimEnd('/')}/api/engine/repo-config?repo={Uri.EscapeDataString(repo)}";
Logger?.LogInformation("Fetching repo config from {Url}", url);
var response = await httpClient.GetAsync(url);
response.EnsureSuccessStatusCode();
var json = await response.Content.ReadFromJsonAsync<JsonElement>();
if (json.TryGetProperty("conventions", out var conv))
{
    conventions = conv.GetString() ?? "";
}
```

The activity requests `?repo=owner/repo`, expects JSON, and reads the `conventions` field. With the C# stub, `TryGetProperty("conventions", ...)` returns false and `conventions` stays empty — every LLM call downstream is missing repo-specific coding conventions.

- Tests: none cover this endpoint.

## 3. The gap

- TS did: fetched `.tamma/config.json` from GitHub via an injected `RepoConfigReader`, returned the parsed JSON (or `{}` on failure).
- C# does: ignores query parameters and returns a literal `{configured: false}` object.

For `ReadRepoConventionsActivity` fetching `GET /api/engine/repo-config?repo=acme/webapp`:

- TS: `{conventions: "Use PascalCase for C#...", linter: "eslint", ...}` sourced from the repo's `.tamma/config.json`.
- C#: `{configured: false}` — no `conventions` field. The activity's `conventions` value stays empty.

Downstream: every LLM prompt that interpolates `{{conventions}}` gets an empty string. Per CLAUDE.md ("Convention Templates" section): "LlmCallWorkflow injects it into every prompt via `{{conventions}}`." That injection is now a no-op on every repo — code generation proceeds without any language/framework-specific guidance.

Error paths:

- TS: 400 when `repo` missing or invalid format.
- C#: 200 with `{configured: false}` always.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-6/story-6-11/6-11-context-api-wiring.md` and (for the `conventions` field specifically) the CLAUDE.md "Convention Templates" section which says "User selects a starter, customizes it, saves to `.tamma/config.json` in their repo as `conventions` field. LlmCallWorkflow injects it into every prompt via `{{conventions}}`."
- Story alignment:
  - [x] Matches TS behavior (C# is a regression)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Not-yet-implemented (stub).
- **What's needed to finish**:
  1. Add `string? Repo` and `string? Branch` query-string parameters to the handler signature.
  2. Port `RepoConfigReader` (or a minimal equivalent) that uses the installation-scoped Octokit/GitHub App client to fetch `.tamma/config.json` from the target repo + branch.
  3. On success, parse JSON and return as body. On missing file / network failure, return `{}` (not 500) per TS contract.
  4. 400 when `repo` query is missing or not `owner/repo` format.
- **Is it "just a stub" or is scope missing?** Both. The stub is trivial, but the underlying scope — a `RepoConfigReader` that fetches from GitHub using an installation-scoped client — must also be implemented. In the absence of a wired GitHub App client (see finding 021), the short-term fix is to return `{}` on any call, which at least matches the TS graceful-degradation path.
- **Blockers**: depends on an Octokit.NET-equivalent GitHub client being wired (cross-ref github finding). Short-term graceful-degradation unblocks the Elsa activity.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:69-70`
  - Endpoint registration in `Program.cs` — wire the `[FromQuery]` binding.
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Services/Engine/IRepoConfigReader.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/Engine/OctokitRepoConfigReader.cs` (depends on GitHub App client).
  - `apps/tamma-elsa/src/Tamma.Api/Services/Engine/NullRepoConfigReader.cs` — always returns `{}`.
- Tests to add:
  - `GetRepoConfig_RejectsMissingRepoQuery` — 400.
  - `GetRepoConfig_RejectsBadFormat` — `?repo=foo` (no slash) → 400.
  - `GetRepoConfig_ReturnsEmpty_WhenReaderNull` — matches graceful-degradation.
  - `GetRepoConfig_ReturnsConventions_WhenReaderSucceeds` — `conventions` field round-trips.
- Estimated effort: 3h
  - Endpoint + DTO: 30m
  - `IRepoConfigReader` + Null impl: 30m
  - Octokit-backed impl (stub until GitHub App wired): 1h
  - Tests: 1h

## References

- TS source: `packages/api/src/routes/engine/engine-context-routes.ts:258-307`
- Deployed caller: `apps/tamma-elsa/src/Tamma.Activities/Context/ReadRepoConventionsActivity.cs:70-79`
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:69-70`
- Story: `docs/stories/epic-6/story-6-11/6-11-context-api-wiring.md`
- CLAUDE.md section: "Convention Templates"
- Related findings: `021-key-rotation-no-reprovision.md` (shared blocker: no GitHub App client), `008-issue-comment-stub.md` (same blocker pattern)

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: `2c2cdfa` (engine wiring); depends on `4e1e0e4` (Octokit client)
- **Notes**: Real `OctokitGitHubEngineCallbackService.ReadRepoConfigAsync`
  now runs when the GitHub App is configured. Reads `.tamma/config.yaml`,
  `.tamma/config.yml`, `.tamma/config.json` in order via
  `Repository.Content.GetAllContentsByRef(owner, repo, path, branch)` using
  an installation-authenticated Octokit client. Returns parsed JSON for the
  `.json` variant, a `{rawYaml}` envelope for YAML, and the TS-parity
  graceful-degradation `{}` when no file exists or read fails — so the
  conventions injection path keeps working on unconfigured repos. Repo →
  installation id resolution goes through `InstallationRepoResolver` which
  queries `github_installation_repos` by `RepoFullName`; unknown repos fall
  through to the 503 `github_client_not_configured` path (Null impl
  parity). The endpoint still preserves the soft-fail 200 `{}` contract on
  503 so Elsa activities never break the workflow.
