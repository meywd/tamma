# Port-Gap Audit — Prompts / Conventions

Scope: `packages/api/src/routes/prompts/*`, `packages/api/src/services/{default-prompts,prompt-store,pg-prompt-store,in-memory-prompt-store,prompt-store-events,convention-templates}.ts` (TS, deleted in commit `9e9a57c`) → `apps/tamma-elsa/src/Tamma.Api/Endpoints/{PromptEndpoints,ConventionEndpoints}.cs`, `apps/tamma-elsa/src/Tamma.Api/Auth/SystemPrompts.cs`, `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/*.cs`, `apps/tamma-elsa/src/Tamma.Api/Services/Conventions/*.cs`, `apps/tamma-elsa/src/Tamma.Data/Entities/PromptOverride.cs`, `apps/tamma-elsa/src/Tamma.Data/Repositories/PromptRepository.cs` (C#).

Source audit summary: [`/tmp/tamma-audit/32-prompts.md`](../../../../../tmp/tamma-audit/32-prompts.md).

Headline: among all audited scopes, prompts is the **cleanest port** — most surface is byte-equivalent or better. The 13 findings below are concentrated around contract/wire-format drift and data-model regression, not missing functionality.

## Severity distribution

| Severity | Count |
|---|---|
| P0 (cutover-blocking) | 0 |
| P1 (feature broken) | 3 |
| P2 (correctness/observability) | 4 |
| P3 (drift/contract, incl. positive) | 6 |

## Findings

| # | Title | Severity | Status | Effort | Remediation |
|---|---|---|---|---|---|
| [001](./001-role-action-template-whitespace-diff.md) | 16 role+action templates diverge in whitespace (plan-review + code-review × 8 roles) | P3 | Behavioral drift | 1h | Fixed (formalize C# shape) |
| [002](./002-cpp-convention-missing-words.md) | `cpp` convention template drops "for readability" | P3 | Behavioral drift | 0.1h | Fixed |
| [003](./003-render-response-field-names.md) | Render response field names changed (`renderedTemplate` → `userPrompt`, etc.) | **P1** | Behavioral drift | 0.5h | Fixed (extended to TS contract) |
| [004](./004-tenant-scoped-to-user-scoped.md) | Prompt overrides moved from tenant-scoped to user-scoped | **P1** | Semantic rewrite | 4h | Already-fixed (CLAUDE.md spec) |
| [005](./005-put-system-prompt-semantic-drift.md) | `PUT/DELETE /api/prompts/system/:role/:action` semantic drift — writes user override, not system default | **P1** | Semantic rewrite | 2h | Fixed (URL → /system/{role}) |
| [006](./006-missing-defaults-endpoints.md) | Missing `/api/prompts/defaults*` endpoints and `POST /reset` | P2 | Incomplete | 1.5h | Fixed |
| [007](./007-dead-event-emit-methods.md) | `EmitCreatedAsync` and `EmitResetAsync` never called from endpoints | P3 | Incomplete | 0.5h | Fixed |
| [008](./008-action-default-layer-new-in-csharp.md) | Action-default safety-net layer new in C# (positive deviation) | P3 | Behavioral drift (positive) | 0h | Already-fixed (locked by tests) |
| [009](./009-variables-column-type-change.md) | `variables` column type changed JSONB → `text[]` | P3 | Data-model regression | 0.3h | Already-fixed (CLAUDE.md spec) |
| [010](./010-prompt-overrides-missing-audit-columns.md) | `prompt_overrides` missing `version`, `created_by`, `updated_by` | P2 | Data-model regression | 1h | Fixed (schema by admin-db 030; wiring here) |
| [011](./011-missing-unique-constraint.md) | No `UNIQUE(user_id, scope, role, action)` constraint | P2 | Data-model regression | 0.5h | Already-fixed (index in DbContext + migration) |
| [012](./012-resolution-order-four-layer.md) | TS 2-layer vs C# 4-layer resolution (matches CLAUDE.md) | P3 | Behavioral drift (positive) | 0h | Already-fixed (CLAUDE.md spec; locked by tests) |
| [013](./013-json-property-naming-policy.md) | No explicit `JsonSerializerOptions.PropertyNamingPolicy` config | P2 | Behavioral drift | 0.2h | Fixed |

## Cross-cutting themes

1. **Tenant-to-user scope shift** (#004, #005, #011) — biggest single architectural change; cascades into constraint shape and authorization semantics.
2. **Wire-format drift risk** (#003, #013) — render response fields renamed without migration path; JSON casing relies on framework default.
3. **Audit/event gaps** (#007, #010) — dead emit methods, dropped authorship columns; weakens the CLAUDE.md "complete audit trail" promise.
4. **Contract-vs-spec divergence** — TS, the stories, and CLAUDE.md describe three different systems. C# picked CLAUDE.md for resolution order (#012) but kept TS's `/system` URL naming (#006), producing a hybrid that matches neither source of truth end-to-end.
5. **Positive deviations** (#008, #012) — the 4-layer resolution order and action-default safety net expand the system beyond TS, aligning with CLAUDE.md. Document, don't revert.

## Ordered fix plan (suggested)

1. **Resolve scoping axis** (Finding #004) — decide user-scoped vs tenant-scoped; blocks #005 and #011.
2. **Fix render response contract** (Finding #003) — P1 dashboard break.
3. **Fix PUT /system semantics** (Finding #005) — P1 admin endpoint regression.
4. **Lock JSON camelCase** (Finding #013) — 0.2h; prevents silent future regressions.
5. **Add unique constraint + concurrent upsert** (Finding #011).
6. **Add audit columns** (Finding #010).
7. **Wire dead event emissions** (Finding #007).
8. **Add missing `/defaults*` endpoints** (Finding #006).
9. **Fix cpp convention** (Finding #002) — 6-minute fix.
10. **Decide whitespace shape for 16 templates** (Finding #001).
11. **Column-type decision for variables** (Finding #009).
12. **Document positive deviations** (#008, #012) — story + CLAUDE.md alignment.

**Total effort**: ~11.8h (most is scoping decisions; code changes are small).

## Known missing sources

None. All required TS sources (7 files) and C# sources (10 files) were available via `git show 9e9a57c~1:` and the current working tree. Archived SQL migration `012_prompt_store.sql` was available. Epic-27 stories (7 files + README) were fully readable. CLAUDE.md Prompt Store Architecture section (lines ~230-310) was readable.
