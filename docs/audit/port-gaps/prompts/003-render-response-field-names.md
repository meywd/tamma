# Finding 003: Render response field names changed (dashboard/CLI consumers break)

**Scope**: prompts
**Severity**: P1 (feature broken)
**Status**: Behavioral drift (ported but contract changed)
**Estimated port effort**: 0.5h

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed (extended to TS contract)
- **Commit**: ea4d5e5
- **Notes**: `RenderedPromptResponse` now exposes the full eight-field TS contract (role, action, version, renderedTemplate, renderedSystemPrompt, enableTools, maxTokens, unresolvedVariables). Threaded `Version` through `ResolvedPrompt` and the upsert pipeline (defaults to 1 for system templates, bumps on every override update — closes the link to finding 010). Integration test `PromptEndpointsIntegrationTests.RenderPrompt_Returns_AllEightFields_MatchingTsContract` asserts the camelCase shape on the wire.

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/services/prompt-store.ts` and `packages/api/src/routes/prompts/prompt-routes.ts`.

- File: `packages/api/src/services/prompt-store.ts:55-62` (`RenderedPrompt` interface) and `packages/api/src/routes/prompts/prompt-routes.ts:369-395` (`POST /:role/:action/render` handler).
- Contract/behavior: `POST /api/prompts/:role/:action/render` returns a JSON object with eight fields describing the interpolated template. Field names are stable and used by the dashboard and CLI.
- Key code (verbatim quote, `prompt-store.ts:55-62`):

```typescript
// packages/api/src/services/prompt-store.ts (9e9a57c~1)
/** Result of rendering a prompt template */
export interface RenderedPrompt {
  role: string;
  action: string;
  version: number;
  renderedTemplate: string;
  renderedSystemPrompt: string;
  enableTools: boolean;
  maxTokens: number;
  /** Variable names that were referenced in the template but not provided */
  unresolvedVariables: string[];
}
```

Render implementation returning this shape (`prompt-store.ts:343-364`):

```typescript
async render(role: string, action: string, input: RenderInput): Promise<RenderedPrompt | undefined> {
  await this.initialize();
  const template = this.templates.get(this._key(role, action));
  if (template === undefined) return undefined;

  const unresolvedVariables: string[] = [];
  const renderedTemplate = this._interpolate(template.template, input.variables, unresolvedVariables);
  const renderedSystemPrompt = this._interpolate(template.systemPrompt, input.variables, unresolvedVariables);

  return {
    role: template.role,
    action: template.action,
    version: template.version,
    renderedTemplate,
    renderedSystemPrompt,
    enableTools: template.enableTools,
    maxTokens: template.maxTokens,
    unresolvedVariables: [...new Set(unresolvedVariables)],
  };
}
```

- Dependencies: `IPromptStore.render()`, `interpolateTemplate()` helper in `prompt-store.ts`.
- Tests that exercised this: `packages/api/src/routes/prompts/prompt-routes.test.ts` and `prompt-store.test.ts` — both asserted the exact property names.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Dtos/Prompts/PromptDtos.cs:6` and `apps/tamma-elsa/src/Tamma.Api/Endpoints/PromptEndpoints.cs:252-290`.
- Contract/behavior: The render endpoint returns a `RenderedPromptResponse` DTO with only three fields; the role, action, version, enableTools, and maxTokens fields have been dropped from the response (the caller is expected to have called `GET /api/prompts/:role/:action` separately to get that metadata).
- Key code (verbatim quote, `PromptDtos.cs:6`):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Dtos/Prompts/PromptDtos.cs (current)
public record RenderedPromptResponse(string SystemPrompt, string UserPrompt, string[]? Unresolved = null);
```

Endpoint code (`PromptEndpoints.cs:286-290`):

```csharp
return Results.Ok(new RenderedPromptResponse(
    SystemPrompt: rendered.SystemPrompt,
    UserPrompt: rendered.UserPrompt,
    Unresolved: rendered.Unresolved.ToArray()));
```

System.Text.Json in minimal APIs serializes with the default `JsonNamingPolicy.CamelCase`, so the wire field names are `systemPrompt`, `userPrompt`, `unresolved`.

- Dependencies: `PromptStoreService.RenderFull()` returning `RenderedPromptPair`, which itself has the three-field shape.
- Tests: `apps/tamma-elsa/tests/Tamma.Api.Tests/PromptStore/PromptRenderTests.cs` asserts the three-field shape; it does **not** check that the dashboard-consumer contract (TS field names) is honoured.

## 3. The gap

Concrete behavioral difference:

- TS did: return `{ role, action, version, renderedTemplate, renderedSystemPrompt, enableTools, maxTokens, unresolvedVariables }`.
- C# does: return `{ systemPrompt, userPrompt, unresolved }`.
- For a caller sending `POST /api/prompts/developer/plan/render` with variables, TS returns (example):

  ```json
  {
    "role": "developer",
    "action": "plan",
    "version": 1,
    "renderedTemplate": "You are a developer creating ...",
    "renderedSystemPrompt": "You are an expert software developer ...",
    "enableTools": true,
    "maxTokens": 8192,
    "unresolvedVariables": ["conventions"]
  }
  ```

  C# returns:

  ```json
  {
    "systemPrompt": "You are an expert software developer ...",
    "userPrompt": "You are a developer creating ...",
    "unresolved": ["conventions"]
  }
  ```

- In production with existing data / deployed clients, this means: **any dashboard, CLI, or Elsa workflow reading `result.renderedTemplate`, `result.renderedSystemPrompt`, or `result.unresolvedVariables` will silently receive `undefined`**. Callers relying on `role`, `action`, `version`, `enableTools`, or `maxTokens` in the render response to route tool invocations or enforce budgets must now make a second request to `GET /api/prompts/:role/:action`.

Three renaming problems:
1. `renderedTemplate` → `userPrompt` (semantic rename, same content)
2. `renderedSystemPrompt` → `systemPrompt` (semantic rename, same content)
3. `unresolvedVariables` → `unresolved` (shortened name, same content)

Three dropped fields:
4. `role` — no longer echoed back.
5. `action` — no longer echoed back.
6. `version` — no longer echoed back (also see finding #010: `version` column no longer exists).
7. `enableTools` — no longer echoed back.
8. `maxTokens` — no longer echoed back.

Error paths:
- TS error path: 404 `{ error: "Prompt template not found for role=..., action=..." }` when neither tenant override nor system default matches.
- C# error path: 404 `{ error: "No prompt available for this role/action" }` (different wording, same status).

## 4. Gap from stories

- Referenced story: `docs/stories/epic-27/27-3-prompt-store-api-endpoints.md` (acceptance criterion #8 — "renders a prompt with variables") and `docs/stories/epic-12/12-5-prompt-engineering-framework.md`.
- Story's acceptance criteria for this behavior: AC #8 says "POST /api/prompts/:role/:action/render renders a prompt with variables for the current tenant (existing endpoint, now tenant-aware)". The "existing endpoint" wording implies preserving the TS contract; the story does not mandate a new shape.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs the story's "existing endpoint" promise)
  - The C# shape is simpler and arguably cleaner — but it breaks contract expectations without a migration note.

## 5. Status

- **Classification**: Behavioral drift
- **What's needed to finish**:
  1. Decide: extend the C# DTO to match TS (recommended — preserves contract), or update dashboard/CLI/workflow consumers to the new shape.
  2. If extending: add `Role`, `Action`, `Version` (= 1 from system defaults, or override version once Finding #010 is fixed), `EnableTools`, `MaxTokens`, and rename fields to `RenderedTemplate` / `RenderedSystemPrompt` / `UnresolvedVariables`.
  3. If keeping current shape: document the break in release notes and grep the dashboard repo for `renderedTemplate` usage.
- **Is it "just a stub" or is scope missing?** Neither — it's a deliberate rename during port, with no documented rationale.
- **Blockers**: Finding #010 (no `version` column in `prompt_overrides`) — if echoing `version`, either hard-code to 1 or add the column.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Dtos/Prompts/PromptDtos.cs` — extend `RenderedPromptResponse` to 8 fields.
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/PromptEndpoints.cs` — populate additional fields from `resolved` in `RenderPrompt`.
  - `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/PromptStoreService.cs` — consider renaming `RenderedPromptPair` fields back to TS names (`RenderedTemplate`, `RenderedSystemPrompt`).
- Files to create: None.
- Tests to add:
  - `PromptRenderTests.cs` — `Render_Returns_All_Eight_Fields_Matching_Ts_Contract` asserting exact JSON shape.
  - Contract test against a fixture captured from the pre-cutover TS API.
- Estimated effort: 0.5h broken down as:
  - DTO + endpoint change: 0.2h
  - Tests: 0.3h

## References

- TS source: `packages/api/src/services/prompt-store.ts:55-62` and `:343-364` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Dtos/Prompts/PromptDtos.cs:6`, `apps/tamma-elsa/src/Tamma.Api/Endpoints/PromptEndpoints.cs:252-290`
- Story: `docs/stories/epic-27/27-3-prompt-store-api-endpoints.md` AC #8
- Related findings: `docs/audit/port-gaps/prompts/010-prompt-overrides-missing-audit-columns.md`, `docs/audit/port-gaps/prompts/013-json-property-naming-policy.md`
- CLAUDE.md section: "Prompt Store Architecture > API"
