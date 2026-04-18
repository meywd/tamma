# Finding 006: Missing `/api/prompts/defaults*` endpoints and POST reset

**Scope**: prompts
**Severity**: P2 (contract gap — documented endpoints absent)
**Status**: Incomplete (partial port, missing 4 endpoints)
**Estimated port effort**: 1.5h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/prompts/prompt-routes.ts`.

- File: `packages/api/src/routes/prompts/prompt-routes.ts` — the TS route registration.
- Contract/behavior: The TS route set + CLAUDE.md "Prompt Store Architecture > API" section documents endpoints under `/defaults`. The TS code itself exposed them under `/system` (a naming inconsistency), but CLAUDE.md is the governing spec. The following endpoints are spec'd:

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/prompts/defaults` | List all shipped system defaults (read-only) |
| GET | `/api/prompts/defaults/:action` | Get the action-default template |
| GET | `/api/prompts/defaults/:role/:action` | Get the system role+action template |
| POST | `/api/prompts/:role/:action/reset` | Alias for DELETE (restore default) |

The TS implementation variant used `/system` instead of `/defaults`, so callers relying on CLAUDE.md's naming would already have been hitting 404. But the TS API did provide equivalent functionality for the first three via `/system`, `/system/:role/:action` (note no `/system/:action`-only endpoint existed in TS either), and the "POST reset" variant was not implemented in TS — only `DELETE` on `/api/prompts/:role/:action`.

- Key code (verbatim quote, `prompt-routes.ts:172-199`):

```typescript
// packages/api/src/routes/prompts/prompt-routes.ts (9e9a57c~1)
// ---------- GET /api/prompts/system ----------
// List all system default prompts (read-only for any authenticated user).
app.get(
  '/api/prompts/system',
  async (_request, reply) => {
    const summaries = await store.listSystemDefaults();
    return reply.send({ templates: summaries, total: summaries.length });
  },
);

// ---------- GET /api/prompts/system/:role/:action ----------
// Get a specific system default prompt.
app.get(
  '/api/prompts/system/:role/:action',
  async (request, reply) => {
    const { role, action } = request.params;
    const template = await store.getSystemDefault(role, action);
    ...
  },
);
```

- Dependencies: `store.listSystemDefaults()`, `store.getSystemDefault()`.
- Tests that exercised this: `prompt-routes.test.ts` — list and get by role/action; no test for `/defaults/:action`-only or `POST /reset`.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Program.cs:381-390` (route registration), `apps/tamma-elsa/src/Tamma.Api/Endpoints/PromptEndpoints.cs:43-94`.
- Contract/behavior: C# exposes `/api/prompts/system` and `/api/prompts/system/:role/:action` but does **not** expose:
  1. `GET /api/prompts/defaults` (the CLAUDE.md-spec URL)
  2. `GET /api/prompts/defaults/:action` (get a single action-default template — no equivalent at either `/defaults` or `/system`)
  3. `GET /api/prompts/defaults/:role/:action` (the CLAUDE.md-spec URL)
  4. `POST /api/prompts/:role/:action/reset` (documented as an alias for DELETE in CLAUDE.md)

- Key code (verbatim quote, `Program.cs:381-390`):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Program.cs (current)
var prompts = app.MapGroup("/api/prompts").RequireAuthorization("SettingsView");
prompts.MapGet("/", PromptEndpoints.ListAll);
prompts.MapGet("/system", PromptEndpoints.ListSystemDefaults);
prompts.MapGet("/system/{role}/{action}", PromptEndpoints.GetSystemDefault);
prompts.MapGet("/{role}/{action}", PromptEndpoints.GetPrompt);
prompts.MapPut("/{role}/{action}", PromptEndpoints.UpsertPrompt).RequireAuthorization("SettingsManage");
prompts.MapDelete("/{role}/{action}", PromptEndpoints.DeletePrompt).RequireAuthorization("SettingsManage");
prompts.MapPut("/system/{role}/{action}", PromptEndpoints.UpsertSystemPrompt).RequireAuthorization("SettingsManage");
prompts.MapDelete("/system/{role}/{action}", PromptEndpoints.DeleteSystemPrompt).RequireAuthorization("SettingsManage");
prompts.MapPost("/{role}/{action}/render", PromptEndpoints.RenderPrompt);
```

C# does internally support action-defaults via `SystemPrompts.GetActionDefault(action)` (`SystemPrompts.cs:192-193`), but this is not exposed through HTTP. `PromptStoreService` uses it as a fallback layer (Finding #012), but no endpoint returns it directly.

- Dependencies: `PromptEndpoints.ListSystemDefaults` / `GetSystemDefault` / `DeletePrompt`, `SystemPrompts.GetActionDefault`, `SystemPrompts.ActionDefaults`.
- Tests: `apps/tamma-elsa/tests/Tamma.Api.Tests/PromptStore/SystemPromptsTests.cs` tests the static layer; no endpoint test for the missing URLs.

## 3. The gap

Concrete behavioral difference:

Four missing endpoints:

1. **`GET /api/prompts/defaults`** — callers following CLAUDE.md receive 404. C# offers `/api/prompts/system` instead (returns a richer `SystemDefaultsResponse` with all three layers). Workaround exists but contract diverges.

2. **`GET /api/prompts/defaults/:action`** — no workaround. A caller who needs the action-default template (e.g., to preview the safety-net fallback for an action across all roles) cannot reach it via HTTP; they must call `GET /api/prompts/system` and pluck from the `ActionDefaults` dictionary in the response body. This adds ~7 KB to the response per call.

3. **`GET /api/prompts/defaults/:role/:action`** — callers following CLAUDE.md receive 404. C# offers `/api/prompts/system/:role/:action` as an alternative (same payload).

4. **`POST /api/prompts/:role/:action/reset`** — callers using the documented "reset" alias receive 404. The only reset path is `DELETE /api/prompts/:role/:action`, which semantically matches but is not RESTfully idempotent for all clients that tolerate POST-only in form contexts.

For a caller sending `GET /api/prompts/defaults/code-review`, TS and C# both return 404. For `GET /api/prompts/defaults/developer/plan`, both return 404. For `POST /api/prompts/developer/plan/reset`, both return 404.

In production with existing data / deployed clients, this means: the dashboard UI or any integrator that read CLAUDE.md and built against `/defaults` will fail. The intersection of "CLAUDE.md-documented" and "TS-implemented" and "C#-implemented" is empty for these four URLs — the TS code shipped a different naming (`/system`), and C# inherited the TS naming without harmonizing with CLAUDE.md.

Error paths:
- TS error path: 404 via Fastify's default "Route GET:/api/prompts/defaults not found".
- C# error path: 404 via ASP.NET Core's default.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-27/27-3-prompt-store-api-endpoints.md`.
- Story's acceptance criteria: Epic 27-3 AC #5-7 mention `/system`, not `/defaults`. AC list does not require `/defaults/:action` or `POST /reset`.
- Story alignment:
  - [ ] Matches TS behavior — TS only had `/system`, which C# also has.
  - [x] Matches C# behavior — both follow the story.
  - [ ] Describes a third behavior — **CLAUDE.md describes a third behavior** (the `/defaults/*` naming).

Source of truth divergence: epic-27-3 and the TS code agreed on `/system`; CLAUDE.md (authored later, treated as aspirational target spec) uses `/defaults`. The port preserved the story, not CLAUDE.md.

## 5. Status

- **Classification**: Incomplete — four endpoints missing vs CLAUDE.md spec.
- **What's needed to finish**:
  1. Decide: canonical URL is `/system` (match TS + story) or `/defaults` (match CLAUDE.md). If `/defaults`, add aliases for the three existing endpoints and a new endpoint for `/defaults/:action`.
  2. Add `POST /api/prompts/:role/:action/reset` as an alias for `DELETE`. Simplest fix is a single `MapPost(...).WithName("...")` handler that calls the same `DeletePrompt` method.
  3. Expose `GET /api/prompts/defaults/:action` (or `/api/prompts/system/actions/:action`) that returns a `PromptResponse` for the `ActionDefaults[action]` entry.
  4. Harmonize CLAUDE.md if `/system` wins, or add aliases if `/defaults` wins.
- **Is it "just a stub" or is scope missing?** Scope was understood for three of the four (they exist under a different URL); the fourth (`GET /:action`-only) was never implemented at the HTTP layer because TS lacked it too.
- **Blockers**: None — these are additive.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Program.cs:381-390` — add route aliases.
- Files to create: None.
- New endpoints needed:
  - `prompts.MapGet("/defaults", PromptEndpoints.ListSystemDefaults)` (alias)
  - `prompts.MapGet("/defaults/{action}", PromptEndpoints.GetActionDefault)` (new handler)
  - `prompts.MapGet("/defaults/{role}/{action}", PromptEndpoints.GetSystemDefault)` (alias)
  - `prompts.MapPost("/{role}/{action}/reset", PromptEndpoints.DeletePrompt)` (alias for DELETE)
- Tests to add:
  - `PromptEndpointsTests.cs` — `GetDefaults_MatchesGetSystem` (route alias).
  - `PromptEndpointsTests.cs` — `GetActionDefault_ReturnsActionTemplate` (new).
  - `PromptEndpointsTests.cs` — `PostReset_BehavesLikeDelete` (alias).
- Estimated effort: 1.5h broken down as:
  - Route aliases: 0.3h
  - New `GetActionDefault` handler: 0.5h
  - Tests: 0.7h

## References

- TS source: `packages/api/src/routes/prompts/prompt-routes.ts:172-265` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Program.cs:381-390`, `apps/tamma-elsa/src/Tamma.Api/Endpoints/PromptEndpoints.cs:43-94`
- Story: `docs/stories/epic-27/27-3-prompt-store-api-endpoints.md`
- Related findings: `docs/audit/port-gaps/prompts/005-put-system-prompt-semantic-drift.md`, `docs/audit/port-gaps/prompts/008-action-default-layer-new-in-csharp.md`
- CLAUDE.md section: "Prompt Store Architecture > API" (lists `/defaults` endpoints and `POST /reset`)
