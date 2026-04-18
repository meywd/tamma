# Finding 013: Verify `JsonSerializerOptions.PropertyNamingPolicy` defaults to camelCase (or dashboard breaks)

**Scope**: prompts
**Severity**: P2 (correctness/contract — JSON wire format)
**Status**: Behavioral drift (no explicit configuration — relies on framework default)
**Estimated port effort**: 0.2h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/prompts/prompt-routes.ts`.

- File: `packages/api/src/routes/prompts/prompt-routes.ts` (response shapes).
- Contract/behavior: TypeScript interfaces use camelCase (`renderedTemplate`, `unresolvedVariables`, `enableTools`, `maxTokens`, `systemPrompt`, etc.). Fastify's `reply.send()` serializes the returned object via `JSON.stringify`, preserving the exact property names as-declared in the TS interface. All on-wire responses are **guaranteed camelCase** because the JS source is camelCase and Fastify's default `application/json` serializer does not transform keys.
- Key code (verbatim quote, `prompt-store.ts:55-62`):

```typescript
// packages/api/src/services/prompt-store.ts (9e9a57c~1)
export interface RenderedPrompt {
  role: string;
  action: string;
  version: number;
  renderedTemplate: string;
  renderedSystemPrompt: string;
  enableTools: boolean;
  maxTokens: number;
  unresolvedVariables: string[];
}
```

Wire output (example): `{"role":"developer","action":"plan","version":1,"renderedTemplate":"...","renderedSystemPrompt":"...","enableTools":true,"maxTokens":8192,"unresolvedVariables":[]}`.

- Dependencies: Fastify's default JSON serializer (`fast-json-stringify` or native `JSON.stringify`).
- Tests that exercised this: `prompt-routes.test.ts` — assertions on response body fields by exact camelCase name.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Dtos/Prompts/PromptDtos.cs` (all DTOs use PascalCase property names as is conventional for C# records) and `apps/tamma-elsa/src/Tamma.Api/Program.cs` (no explicit JSON options configured).
- Contract/behavior: C# records declare properties in PascalCase (`SystemPrompt`, `UserPrompt`, `Unresolved`, `RoleActionTemplates`, `SystemPrompts`, `ActionDefaults`, `Role`, `Action`, `Template`, `Variables`, `EnableTools`, `MaxTokens`, `Source`). ASP.NET Core Minimal APIs use `System.Text.Json` with the **framework default** `JsonSerializerOptions.Web`, which sets `PropertyNamingPolicy = JsonNamingPolicy.CamelCase`. This means the wire output *should* be camelCase automatically.
- Key code (verbatim quote, `PromptDtos.cs`):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Dtos/Prompts/PromptDtos.cs (current)
public record UpsertPromptRequest(string Template, string? SystemPrompt, string[]? Variables, bool? EnableTools, int? MaxTokens);
public record RenderPromptRequest(Dictionary<string, string> Variables);
public record PromptResponse(string? Role, string? Action, string Template, string? SystemPrompt, string[]? Variables, bool EnableTools, int MaxTokens, string Source);
public record RenderedPromptResponse(string SystemPrompt, string UserPrompt, string[]? Unresolved = null);

public record SystemDefaultsResponse(
    IReadOnlyList<PromptResponse> RoleActionTemplates,
    IReadOnlyDictionary<string, string> SystemPrompts,
    IReadOnlyDictionary<string, PromptResponse> ActionDefaults);
```

And relevant excerpt from `Program.cs`:

```csharp
// apps/tamma-elsa/src/Tamma.Api/Program.cs (current) — no JsonOptions configuration for the minimal API pipeline
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => { ... });
// No call to builder.Services.ConfigureHttpJsonOptions(...)
// No call to builder.Services.AddControllers().AddJsonOptions(...) for the minimal API surface
```

Grep for `ConfigureHttpJsonOptions|AddJsonOptions` in `Tamma.Api/Program.cs` returns zero hits; the only JSON options in the codebase are scoped to specific `HttpClient` instances (`ElsaWorkflowService`, `IntelligenceHttpClient`, `WorkflowSyncService`) and set `PropertyNamingPolicy = JsonNamingPolicy.CamelCase` on each — confirming the team's intent is camelCase but the minimal-API pipeline relies on the framework default.

- Dependencies: ASP.NET Core 9 `JsonSerializerDefaults.Web` preset — `PropertyNamingPolicy = JsonNamingPolicy.CamelCase`, `PropertyNameCaseInsensitive = true`, `NumberHandling = AllowReadingFromString`.
- Tests: `apps/tamma-elsa/tests/Tamma.Api.Tests/PromptStore/*` — assertions deserialize into the C# DTO types (PascalCase), so they do **not** verify the wire JSON uses camelCase. The wire shape is effectively untested.

## 3. The gap

Concrete behavioral difference:

- TS did: guaranteed camelCase wire output because the source objects were camelCase.
- C# does: emits camelCase **by ASP.NET Core framework default** (`JsonSerializerDefaults.Web`), but there is no explicit configuration and no test asserting it. If a future change adds `AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.PropertyNamingPolicy = null)` or someone sets `ConfigureHttpJsonOptions` to disable the camelCase policy, the wire format will silently flip to PascalCase — breaking every dashboard, CLI, and Elsa consumer.

Two distinct risks:

1. **Contract implicit, not asserted.** The camelCase contract is enforced only by framework default. No test locks it. A one-line change in `Program.cs` (e.g., adding `.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)`) silently breaks all prompt-related (and other) endpoints.

2. **Swagger/OpenAPI metadata mismatch risk.** `AddSwaggerGen` is configured separately and may not honor `JsonSerializerDefaults.Web`. If Swagger emits PascalCase but the wire emits camelCase, client-code-generators (NSwag, openapi-typescript) will produce broken types.

Concrete consequence for prompts scope: the dashboard expects `{ systemPrompt, userPrompt, unresolved }` on the render response. If the framework default flips, the dashboard will receive `{ SystemPrompt, UserPrompt, Unresolved }` and fail to parse.

For a caller sending `GET /api/prompts/system`:
- Current behavior: `{ roleActionTemplates: [...], systemPrompts: {...}, actionDefaults: {...} }`.
- If camelCase policy is lost: `{ RoleActionTemplates: [...], SystemPrompts: {...}, ActionDefaults: {...} }`.

Error paths: N/A — this is a silent contract drift, no error surface.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-27/27-3-prompt-store-api-endpoints.md`.
- Story's acceptance criteria for this behavior: AC #12 — *"Error responses use consistent format: `{ error: string, code?: string }`"* — the story mandates camelCase error shape. AC #8 (render) refers to "existing endpoint" which implies preserving TS camelCase.
- Story alignment:
  - [x] Matches TS behavior (wire format is camelCase both pre and post-port, assuming framework default holds)
  - [ ] Matches C# behavior — "matches" only while the default holds; no test pins it.
  - No CLAUDE.md section governs JSON property casing explicitly.

## 5. Status

- **Classification**: Behavioral drift — implicit contract, not asserted.
- **What's needed to finish**:
  1. Explicitly configure JSON options in `Program.cs`:
     ```csharp
     builder.Services.ConfigureHttpJsonOptions(options =>
     {
         options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
         options.SerializerOptions.PropertyNameCaseInsensitive = true;
     });
     ```
  2. Add a wire-format assertion test: send a real HTTP request via `WebApplicationFactory`, inspect the raw response body (not the deserialized DTO), and assert camelCase keys.
  3. Document the contract in a `docs/audit/json-wire-format.md` or in `CLAUDE.md` under "API Endpoints".
- **Is it "just a stub" or is scope missing?** Scope was understood (camelCase is conventional and was the pre-port behavior), but no explicit lock exists.
- **Blockers**: None.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Program.cs` — add explicit `ConfigureHttpJsonOptions` call before `builder.Build()`.
- Files to create: None (optional: a `WireFormatTests.cs` in `Tamma.Api.Tests`).
- Tests to add:
  - `PromptEndpointsWireFormatTests.cs` — send `GET /api/prompts/system` via test host, parse raw JSON, assert `roleActionTemplates` exists and `RoleActionTemplates` does not.
  - Similar wire-format tests for other endpoint groups (to catch regressions at the infrastructure level).
- Estimated effort: 0.2h broken down as:
  - Explicit JSON options: 0.05h
  - Single wire-format test: 0.15h

## References

- TS source: `packages/api/src/routes/prompts/prompt-routes.ts`, `packages/api/src/services/prompt-store.ts:55-62` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Dtos/Prompts/PromptDtos.cs`, `apps/tamma-elsa/src/Tamma.Api/Program.cs`
- Story: `docs/stories/epic-27/27-3-prompt-store-api-endpoints.md` AC #12
- Related findings: `docs/audit/port-gaps/prompts/003-render-response-field-names.md` (field shape), `docs/audit/port-gaps/prompts/006-missing-defaults-endpoints.md`
- CLAUDE.md section: "API Endpoints" (pattern description, no JSON casing mandate)
- Reference: [ASP.NET Core 9 default `JsonSerializerDefaults.Web`](https://learn.microsoft.com/en-us/dotnet/core/compatibility/aspnet-core/7.0/default-json-serializer-options-web) (`PropertyNamingPolicy = CamelCase`)
