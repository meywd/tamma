# Implementation Plan — Story 39-5: Acceptance Rules — configurable policy, admin UI, orchestrator read path

## Scope & Deliverable

When this story is done, `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/` holds the `AcceptanceRules` model (autonomy level 70–100, bounds, escalation criteria, reviewer selection, guidance text), the acceptor contract (`AcceptanceRequest`, `AcceptanceDecision`, `AcceptanceRouting`), the pure `AcceptanceGuardrails` function, static shipped defaults, and the `IAcceptanceRulesResolver` interface. `Tamma.Api` gains the EF-backed resolver over a new `acceptance_rules_overrides` table (user_id XOR tenant_id, `prompt_overrides` pattern), the `/api/acceptance-rules` admin REST surface with prompt-store RBAC parity, and a principal-bound `get_acceptance_rules` `IToolExecutor` for the 39-17 agent. `packages/dashboard` gains an admin screen showing effective rules per document type with default-vs-override provenance and editing the autonomy dial, bounds, escalation criteria, and guidance. 39-6/39-7/39-8/39-17/39-18 consume these contracts; none of them are implemented here.

## Pre-Reading

- `docs/stories/epic-39/story-39-5/39-5-acceptance-policy-per-mode-accept-escalation-configuration.md` — the story (ACs are the source of truth)
- `docs/stories/epic-39/README.md` — settled principles: "Autonomy is a dial, not a mode"; "The acceptor is an actor, not a branch"; the lifecycle diagram
- `docs/stories/epic-39/story-39-2/implementation-plan.md` — the 39-2 contract this story builds on (`DocumentTypeKey` kebab wire keys, `DocumentTypeRegistry`, `DocumentEnvelope`, `DocumentLifecycleOutcome`, `TammaError` codes, `EnumWire`/`[Wire]` style)
- `docs/guides/BEFORE_YOU_CODE.md` — mandatory process
- `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/TammaMode.cs` — `TammaMode` + `ITammaModeProvider`, the process-stable mode detection
- `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/PromptStoreService.cs` — resolution-facade shape; note the `ForTenant` method-name convention (overload-resolution rationale in its doc comment)
- `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/PromptEventsService.cs` — best-effort DCB event emitter precedent (`PROMPT.CREATED.SUCCESS` etc.)
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/PromptEndpoints.cs` + `apps/tamma-elsa/src/Tamma.Api/Program.cs` (policy block ~L1459–1540, prompt routes ~L2529–2561, conventions routes ~L2590) — endpoint + policy + route-group registration style
- `apps/tamma-elsa/src/Tamma.Api/Auth/Permissions.cs` — the RBAC matrix this story extends (never forks)
- `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` (~L1496–1538) — `prompt_overrides` XOR CHECK + NULLS-NOT-DISTINCT unique-index configuration to copy
- `apps/tamma-elsa/src/Tamma.Data/Entities/PromptOverride.cs`, `apps/tamma-elsa/src/Tamma.Data/Repositories/IPromptRepository.cs`, `PromptRepository.cs` — entity/repository seam to mirror
- `apps/tamma-elsa/src/Tamma.Data/TenantDbContext.cs` — where the new DbSet + `ConfigureTenantEntities` call lives (prompt_overrides is tenant-resident since Story 28-1 PR D; the CP context carries only compile-time shims)
- `apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/` — house migration pattern (`<utc-timestamp>_<Name>.cs` + `.Designer.cs`, e.g. `20260704083332_AddDomainEventsUserIdIndex.cs`)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs` (~L97–270) + `apps/tamma-elsa/src/Tamma.Activities/Context/ResolveConventionsActivity.cs` — server-side config resolution precedent (Story 27-13): config resolved server-side, never trusted from the client
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/IToolExecutor.cs` (+ `GitOperationsTool.cs` as an implementation example) — the tool seam `get_acceptance_rules` implements
- `apps/tamma-elsa/src/Tamma.Core/Agents/AgentAction.cs`, `AgentRole.cs`, `EnumWire.cs` — wire vocabularies the always-escalate classes and reviewer selection validate against
- `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/RolePhaseMapTests.cs`, `AgentRoleTests.cs` — drift-test style (count pins, exact-value pins)
- `apps/tamma-elsa/tests/Tamma.Api.Tests/PromptStore/PromptStoreServiceTests.cs`, `PromptStoreServiceSaaSModeTests.cs`, `PromptEndpointsTenantAdminTests.cs`, `PromptOverridesPrincipalXorMigrationTests.cs` (Testcontainers) — the test suite shapes to mirror
- `packages/dashboard/src/pages/admin/prompts/PromptsAdminPage.tsx`, `packages/dashboard/src/pages/admin/conventions/ConventionsAdminPage.tsx`, `packages/dashboard/src/hooks/admin/useSystemPrompts.ts`, `packages/dashboard/src/router.tsx` (AdminGuard routes ~L185–195) — admin-screen precedent
- **NOT FOUND**: `apps/tamma-elsa/src/Tamma.Core/Documents/` — the 39-2 deliverable (`DocumentTypeKey`, `DocumentTypeRegistry`, `DocumentEnvelope`) is not yet implemented; its plan is authored. Likewise 39-4's `Review` type (`ReviewDecision` enum) does not exist yet. See Design Decisions D8/D9 and Dependencies.

## Design Decisions

- **D1 — Model and resolver interface live in `Tamma.Core`; storage/API in `Tamma.Api`/`Tamma.Data`.** The project graph is `Tamma.ElsaServer → Tamma.Activities → (not Tamma.Api)`, so anything 39-6 (Elsa process) or the 39-17 agent host needs at compile time must sit in `Tamma.Core/Documents/Policy/`: `AcceptanceRules`, the acceptor contract records, `AcceptanceGuardrails`, `AcceptanceDefaults`, `ResolvedAcceptanceRules`, and `IAcceptanceRulesResolver`. The EF-backed implementation (`AcceptanceRulesService`) lives in `Tamma.Api/Services/AcceptanceRules/` beside `PromptStoreService`. This is exactly the story's technical note.
- **D2 — Three resolution tiers, wholesale-row precedence, no field merge.** AC4's "per-type override → default" resolves as: (1) principal's per-type override row → (2) principal's base override row (`DocumentTypeKey` NULL — what the admin dial edits deployment-wide) → (3) `AcceptanceDefaults.For(documentType)` per-type static shipped default (AC5's zero-config floor — panel for `plan`/code/`review`, single-reviewer/architect otherwise). Each row stores a *complete*, validated `AcceptanceRules` record; resolution picks the highest-precedence row wholesale. Field-level deep-merging is rejected: it makes provenance unexplainable in the admin UI and has no precedent (a prompt override replaces the template entirely).
- **D3 — One table, JSONB payload, XOR principal keys.** `acceptance_rules_overrides` mirrors `prompt_overrides`: `UserId`/`TenantId` with `ck_acceptance_rules_overrides_principal_xor`, unique `(UserId, TenantId, DocumentTypeKey)` with `AreNullsDistinct(false)`, tenant-resident only (`TenantDbContext`, per Story 28-1 PR D — single-user users own a personal tenant DB, so both modes land in the same physical home). The rules body is one `jsonb` column (`RulesJson`) deserialized into `AcceptanceRules` — precedent `agent_versions.ConfigJson`. Nested records (escalation criteria, reviewer selection) would be miserable as discrete columns; validation happens fail-loud on write AND defensively on read (a corrupt row throws `TammaError`, never silently degrades). **Per-mode ownership, written down per the CLAUDE.md universal rule:** single-user — the sole user owns all rows (keyed `user_id`); SaaS — the tenant owns them (keyed `tenant_id`), `tenant_owner`/`tenant_admin` write, members read; there is NO per-user layer on top of tenant rules.
- **D4 — Validation rejects, never clamps.** `AutonomyLevel` outside 70–100, `MaxRevisionRounds` outside 1–10, `MaxValidationRepairAttempts` outside 0–10, `AmbiguityEscalationThreshold` outside [0,1] → `TammaError ACCEPTANCE_RULES.INVALID` on PUT and at static-default construction (AC8's "validation rejects absurd bound values"). Always-escalate entries and reviewer roles validate fail-loud against `DocumentTypeKeyExtensions.Parse` / `AgentActionExtensions.Parse` / `AgentRoleExtensions.Parse` (AC4's typo-cannot-create-dead-config; the story's technical note on the breaking-changes class).
- **D5 — RBAC: reads `AuthenticatedAny`, writes a new `acceptance-rules:manage` permission.** AC7 says reads for *any* tenant member; the `/api/prompts` group actually uses `SettingsView` (admin+owner) but the convention store deliberately deviated to `AuthenticatedAny` for member-readable config — follow that documented precedent (story text wins). Writes get `["acceptance-rules:manage"] = ["admin", "owner"]` in `Permissions.Matrix` + an `AcceptanceRulesManage` policy mirroring `PromptManage` verbatim (extends the matrix, never forks it).
- **D6 — The `get_acceptance_rules` tool is principal-bound at construction and NOT globally DI-registered.** It implements the existing `Tamma.Activities.LlmCall.Tools.IToolExecutor` seam, but registering it in the DI set that `ResolveToolsActivity` discovers would inject it into every coding-agent tool loop. Instead ship `GetAcceptanceRulesToolFactory.Create(userId?, tenantId?)`; the 39-17 host constructs one per tenant-agent session. The principal comes from the server, never from the LLM's `argumentsJson` (only `documentTypeKey` is an accepted argument) — the `LlmCallWorkflow` conventions discipline.
- **D7 — AC2's "publishes and suspends regardless of autonomy level" test is split across 39-5/39-6.** The accept *stage* is 39-6's workflow, which does not exist yet. This story makes routing-around-the-orchestrator unrepresentable at the contract level: `AcceptanceRequestFactory.Create(...)` is the only way to build an `AcceptanceRequest`, it has no accept/skip output, and `AcceptanceDecision`/`AcceptanceRouting` are closed hierarchies with no `AutoAccept` member. A test iterates every autonomy level 70–100 and asserts the factory yields an orchestrator-bound request each time. 39-6 re-pins the same invariant at workflow level (publish + bookmark suspend) — recorded there as a lockstep obligation.
- **D8 — Guardrails read a `ReviewFacts` projection, not 39-4's full `Review` payload.** 39-4 is a **hard prerequisite** (not merely lockstep): `AcceptanceGuardrails` takes `ReviewFacts(ReviewDecision Decision, bool HasBlockingIssues)` where `ReviewDecision` is **39-4's enum** (`Tamma.Core/Documents/Types/Review.cs`, `Approve | RequestChanges | NeedsDiscussion`, the exact spellings 39-4 AC2 pins) — **referenced directly, no local shadow copy**. 39-5 does not redeclare the enum; if 39-4 has not landed, 39-5 waits on that one type (both live in `Tamma.Core`, so it is a compile-time reference, not a transport coupling). The forged-approval rule (Accept + blocking issues → Escalate) is pinned here, mirroring 39-4 AC3 from the other side.
- **D9 — 39-2 is a hard prerequisite; do not shim `DocumentTypeKey`.** Every rules knob keys on `DocumentTypeKey`, and AC4's fail-loud override validation runs against the 39-2 registry vocabulary. Duplicating the enum would create the exact drift the epic exists to kill. Sequence after 39-2 (its plan is complete and its surface is small).
- **D10 — Escalation reasons are a new closed enum, not a fork of `DocumentLifecycleOutcome`.** `Escalate` carries `AcceptanceEscalationReason` (`RoundsExhausted | AlwaysEscalateClass | BlockingReviewViolation | AmbiguityAboveThreshold | AcceptorJudgment | RejectRequiresHuman`) plus free-text detail — **6 members** (count pin). Two members map 1:1 onto 39-2's `DocumentLifecycleOutcome` (`RoundsExhausted`, `AmbiguityAboveThreshold`) — a static `ToLifecycleOutcome()` mapping is provided and drift-tested so 39-6 never string-matches. `RejectRequiresHuman` (the orchestrator-tried-to-reject clamp, D3/step-3) maps to **no typed lifecycle outcome — `ToLifecycleOutcome()` returns `null`**: it is escalation-only, never a terminal lifecycle state; `AcceptorJudgment` and `BlockingReviewViolation`/`AlwaysEscalateClass` likewise return `null`.
- **D11 — Property-style tests are hand-rolled with seeded `Random`.** No FsCheck anywhere in the solution; do not add a package for AC8. A seeded generator over arbitrary decision sequences (1000 iterations, seed logged on failure) proves termination within bounds.
- **D12 — Admin mutations emit `ACCEPTANCE_RULES.*` DCB events** from the service layer (IMP-2 pattern in `PromptStoreService`), mirroring `PromptEventsService` best-effort semantics.

## Implementation Steps

1. **CREATE `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceRules.cs`** — the model (AC1). Style: sealed records, explicit `[JsonPropertyName]` (39-2 D8), `[Wire]` enums via `EnumWire<T>`:

   ```csharp
   namespace Tamma.Core.Documents.Policy;
   public sealed record AcceptanceRules
   {
       [JsonPropertyName("autonomyLevel")] public required int AutonomyLevel { get; init; }            // 70–100
       [JsonPropertyName("maxRevisionRounds")] public required int MaxRevisionRounds { get; init; }    // 1–10
       [JsonPropertyName("maxValidationRepairAttempts")] public required int MaxValidationRepairAttempts { get; init; } // 0–10
       [JsonPropertyName("ambiguityEscalationThreshold")] public required double AmbiguityEscalationThreshold { get; init; } // [0,1]
       [JsonPropertyName("alwaysEscalate")] public required IReadOnlyList<EscalationClass> AlwaysEscalate { get; init; }
       [JsonPropertyName("reviewerSelection")] public required ReviewerSelection ReviewerSelection { get; init; }
       [JsonPropertyName("decisionGuidance")] public required string DecisionGuidance { get; init; }   // operator prose
       [JsonPropertyName("routingGuidance")] public required string RoutingGuidance { get; init; }     // operator prose
       public AcceptanceRules Validate();  // throws TammaError ACCEPTANCE_RULES.INVALID (D4); returns this for fluent use
   }
   public sealed record EscalationClass(EscalationClassKind Kind, string Key);   // Kind: [Wire] DocumentType | AgentAction
   public sealed record ReviewerSelection(ReviewerMode Mode, string? ReviewerRole,
       IReadOnlyList<string> PanelRoles, int? Quorum, ReviewDecisionRule DecisionRule); // Mode: [Wire] SingleReviewer | Panel; DecisionRule: [Wire] unanimous | majority (39-7 consumes it to resolve a panel verdict)
   ```

   `Validate()` enforces D4 ranges, parses `EscalationClass.Key` against `DocumentTypeKeyExtensions`/`AgentActionExtensions` per `Kind`, and reviewer roles against `AgentRoleExtensions`. `ReviewerSelection` also carries `DecisionRule` (`[Wire] unanimous | majority`, the `ReviewDecisionRule` enum) alongside `Mode`/`ReviewerRole`/`PanelRoles`/`Quorum` — 39-7 reads it to resolve a panel's verdict; a bare numeric `Quorum` is not enough to express unanimous-vs-majority. No stringly knobs outside the two guidance strings. Precedent for file shape: `Tamma.Core/Agents/AgentRole.cs` (enums) + 39-2's envelope records.

2. **CREATE `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceDefaults.cs`** (AC5) — `public static class AcceptanceDefaults { public static AcceptanceRules For(DocumentTypeKey type); public static AcceptanceRules Rules { get; } }`. Shared knobs across all types: AutonomyLevel 70, MaxRevisionRounds 2, MaxValidationRepairAttempts 2, AmbiguityEscalationThreshold 0.7, empty `AlwaysEscalate`, shipped decision/routing guidance strings. **Reviewer selection is PER-TYPE, not a single global default** (so 39-14's PlanReview migration is behavior-preserving with zero config): the `plan` document type and the code/`review`-type document types default to a **panel** — `ReviewerSelection(Panel, null, [the 7-role roster], Quorum: null, DecisionRule: Majority)` — while all other types default to single-reviewer/architect — `ReviewerSelection(SingleReviewer, "architect", [], null, DecisionRule: Unanimous)`. `Rules` remains as the shared-knobs base row (single-reviewer/architect) for the principal-base tier; `For(type)` layers the per-type reviewer default on top. Static ctor calls `Validate()` on every per-type default — an invalid default refuses to load (fail-loud posture of `PromptFileLoader`). The exact panel roster + which types get the panel are pinned in the defaults drift test (AC5).

3. **CREATE `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceDecision.cs`, `AcceptanceRouting.cs`, and `apps/tamma-elsa/src/Tamma.Core/Documents/ApprovalChannel.cs`** (AC2) — closed hierarchies with `[JsonDerivedType]` `kind` discriminators, plus the `ApprovalChannel` enum. **`ApprovalChannel` (`[Wire] orchestrator | user | api`) is a NEW enum OWNED BY 39-5**, defined in `Tamma.Core/Documents/` because `AcceptanceGuardrails` (Tamma.Core, step 6) types `AcceptanceGateContext.DeciderChannel` on it and cannot reference a type from 39-8's scope (39-5 precedes 39-8). 39-8 CONSUMES it and maps its server-derived resume principal onto it — 39-5 owns the type, not 39-8.

   ```csharp
   [JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
   [JsonDerivedType(typeof(Accept), "accept")]
   [JsonDerivedType(typeof(RequestRevision), "request-revision")]
   [JsonDerivedType(typeof(Reject), "reject")]
   [JsonDerivedType(typeof(Escalate), "escalate")]
   public abstract record AcceptanceDecision
   {
       public sealed record Accept : AcceptanceDecision;
       public sealed record RequestRevision([property: JsonPropertyName("notes")] string Notes) : AcceptanceDecision;
       public sealed record Reject([property: JsonPropertyName("reason")] string Reason) : AcceptanceDecision;  // HUMAN-ONLY (design review 2026-07-21): a final "no" → state Rejected
       public sealed record Escalate(AcceptanceEscalationReason Reason, string Detail) : AcceptanceDecision;
   }
   public abstract record AcceptanceRouting   // DecideSelf | AssignToRole(string RoleWire, AssignmentBasis Basis) — role-addressed, never an exact user (design review 2026-07-21); 39-20 resolves the role's audience
   ```

   `AcceptanceEscalationReason` + `AssignmentBasis` (`[Wire] Initiator | RepoAccess`) are `[Wire]` enums; `AcceptanceEscalationReasonExtensions.ToLifecycleOutcome()` maps per D10 (returns `DocumentLifecycleOutcome?`).

   **`Reject` is human-only (settled design review 2026-07-21): the orchestrator cannot reject without escalating.** Rejection is a decided, final "no" — only a human may take it (via the 39-19 Task View / 39-8 resume, decider channel `user` or `api`). The orchestrator's self-decision vocabulary is effectively `Accept | RequestRevision | Escalate`: if it judges a document should be rejected, it escalates (or assigns) so a human confirms. Enforced in `AcceptanceGuardrails.Clamp`, not by prompt: a `Reject` arriving with channel `orchestrator` clamps to `Escalate(RejectRequiresHuman)`.

4. **CREATE `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceRequest.cs`** (AC2, AC3) — the channel payload + its only factory:

   ```csharp
   public sealed record AcceptanceRequest
   {
       [JsonPropertyName("decisionSessionId")] public required Guid DecisionSessionId { get; init; }   // UuidV7
       [JsonPropertyName("document")] public required DocumentEnvelope Document { get; init; }
       [JsonPropertyName("review")]   public required DocumentEnvelope Review { get; init; }           // type key "review"
       [JsonPropertyName("lineage")]  public required IReadOnlyList<DocumentEnvelope> Lineage { get; init; }
       [JsonPropertyName("roundsUsed")] public required int RoundsUsed { get; init; }
       [JsonPropertyName("rules")]    public required ResolvedAcceptanceRules Rules { get; init; }     // resolved server-side
       [JsonPropertyName("issueId")]  public required string IssueId { get; init; }
   }
   public static class AcceptanceRequestFactory
   {
       public static AcceptanceRequest Create(DocumentEnvelope document, DocumentEnvelope review,
           IReadOnlyList<DocumentEnvelope> lineage, int roundsUsed, ResolvedAcceptanceRules rules);
       // No autonomy-level branch exists; there is no other constructor and no accept/skip output (D7).
   }
   ```

5. **CREATE `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/IAcceptanceRulesResolver.cs` + `ResolvedAcceptanceRules.cs` + `AcceptanceRulesJson.cs`** (AC3, AC6):

   ```csharp
   public enum AcceptanceRulesSource { [Wire("system-default")] SystemDefault,
       [Wire("principal-default")] PrincipalDefault, [Wire("type-override")] TypeOverride }
   public sealed record ResolvedAcceptanceRules(AcceptanceRules Rules, AcceptanceRulesSource Source,
       int Version, string DocumentTypeKey, DateTimeOffset ResolvedAt);
   public interface IAcceptanceRulesResolver
   {   // ForTenant naming per PromptStoreService's overload-resolution rationale
       Task<ResolvedAcceptanceRules> ResolveAsync(Guid? userId, DocumentTypeKey documentType, CancellationToken ct = default);
       Task<ResolvedAcceptanceRules> ResolveForTenantAsync(Guid tenantId, DocumentTypeKey documentType, CancellationToken ct = default);
   }
   public static class AcceptanceRulesJson { public static JsonSerializerOptions Options { get; } } // one canonical serializer (39-2 DocumentJson style)
   ```

6. **CREATE `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceGuardrails.cs`** (AC8) — pure static, no I/O, no Elsa:

   ```csharp
   public sealed record ReviewFacts(ReviewDecision Decision, bool HasBlockingIssues);       // D8 shim; Decision is 39-4's ReviewDecision (Tamma.Core/Documents/Types/Review.cs) — no local copy
   // ReviewDecision is NOT redeclared here: it is REFERENCED from 39-4's Tamma.Core/Documents/Types/Review.cs
   // (Approve | RequestChanges | NeedsDiscussion). 39-4 is a prerequisite (see Dependencies); the shadow enum is dropped.
   public sealed record AcceptanceGateContext(DocumentTypeKey DocumentType, string? AgentActionWire,
       ReviewFacts Review, int RoundsUsed, AcceptanceRules Rules, ApprovalChannel DeciderChannel);  // DeciderChannel typed as ApprovalChannel — a [Wire] enum OWNED BY 39-5 in Tamma.Core/Documents/ (see step 3). AcceptanceGuardrails (Tamma.Core) cannot reference a type from 39-8's scope, so 39-5 defines it; 39-8 maps its server-derived resume transport (orchestrator|user|api) onto this enum.
   public static class AcceptanceGuardrails
   {
       public static bool TryPreGate(AcceptanceGateContext ctx, out AcceptanceDecision.Escalate escalation);
       // AlwaysEscalate class match → Escalate(AlwaysEscalateClass); RoundsUsed >= MaxRevisionRounds → Escalate(RoundsExhausted)
       public static AcceptanceDecision Clamp(AcceptanceDecision proposed, AcceptanceGateContext ctx);
       // Accept + (Approve is false OR HasBlockingIssues) → Escalate(BlockingReviewViolation)  [forged approval]
       // Reject + DeciderChannel == Orchestrator → Escalate(RejectRequiresHuman)  [reject is human-only — the orch cannot reject without escalating]
       // RequestRevision with RoundsUsed+1 > MaxRevisionRounds → Escalate(RoundsExhausted); otherwise pass through
   }
   ```

7. **CREATE the data layer.** `apps/tamma-elsa/src/Tamma.Data/Entities/AcceptanceRulesOverride.cs` (Id, UserId?, TenantId?, `DocumentTypeKey` string? — NULL = base row, `RulesJson` string, Version, CreatedBy/UpdatedBy, CreatedAt/UpdatedAt — copy `PromptOverride.cs` shape). **MODIFY** `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs`: add the entity block inside `ConfigureTenantEntities` directly after `PromptOverride` — XOR CHECK `ck_acceptance_rules_overrides_principal_xor`, `RulesJson` `HasColumnType("jsonb")`, unique index `IX_acceptance_rules_overrides_UserId_TenantId_DocumentTypeKey` with `.AreNullsDistinct(false)`, `ApplyTenantFilter`. **MODIFY** `apps/tamma-elsa/src/Tamma.Data/TenantDbContext.cs`: add `DbSet<AcceptanceRulesOverride>`. **CREATE** `apps/tamma-elsa/src/Tamma.Data/Repositories/IAcceptanceRulesRepository.cs` + `AcceptanceRulesRepository.cs` mirroring `IPromptRepository` (Get/Upsert/Delete/List per mode, `GetByTenantAsync` etc.). Generate migration `AddAcceptanceRulesOverrides` into `Migrations/Tenant/` (house pattern, Data & Migrations below).

8. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/AcceptanceRules/AcceptanceRulesService.cs` + `AcceptanceRulesEventsService.cs`** — `AcceptanceRulesService : IAcceptanceRulesResolver` implementing D2's three-tier resolution per mode (copy `PromptStoreService`'s layer-walk + `ForTenant` split), plus `UpsertAsync`/`UpsertForTenantAsync`/`DeleteAsync`/`DeleteForTenantAsync`/`ListEffectiveAsync` (resolves all 10 `DocumentTypeKey`s with provenance for the list endpoint). Upsert runs `AcceptanceRules.Validate()` and rejects unknown `documentTypeKey` via `DocumentTypeKeyExtensions.Parse` (AC4) BEFORE writing; mutations emit events via `AcceptanceRulesEventsService` (D12, copy `PromptEventsService`).

9. **CREATE `apps/tamma-elsa/src/Tamma.Api/Endpoints/AcceptanceRulesEndpoints.cs` + `apps/tamma-elsa/src/Tamma.Api/Dtos/AcceptanceRules/AcceptanceRulesDtos.cs`; MODIFY `apps/tamma-elsa/src/Tamma.Api/Program.cs` and `apps/tamma-elsa/src/Tamma.Api/Auth/Permissions.cs`** (AC7). Routes (register specific before parameterized, per the conventions-store ordering comment):

   ```
   var rules = app.MapGroup("/api/acceptance-rules").RequireAuthorization("AuthenticatedAny");
   rules.MapGet("/", ...ListEffective);            // resolved per type + provenance
   rules.MapGet("/defaults", ...GetDefaults);      // AcceptanceDefaults.Rules
   rules.MapGet("/{documentTypeKey}", ...GetResolved);
   rules.MapPut("/{documentTypeKey}", ...Upsert).RequireAuthorization("AcceptanceRulesManage");
   rules.MapDelete("/{documentTypeKey}", ...Delete).RequireAuthorization("AcceptanceRulesManage");
   ```

   `{documentTypeKey}` additionally accepts the literal `base` for the principal-default row (the dial). Handlers branch on `ITammaModeProvider.Mode` + `ITenantContext` exactly like `PromptEndpoints.ListAll`. `Permissions.Matrix` gains `["acceptance-rules:manage"] = ["admin", "owner"]` with the standard story comment; Program.cs gains the `AcceptanceRulesManage` policy (copy `PromptManage` block) and DI registrations for repository/service/events beside the prompt-store registrations.

10. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/AcceptanceRules/GetAcceptanceRulesTool.cs`** (AC3a, D6) — `public sealed class GetAcceptanceRulesTool : IToolExecutor` with `ToolName = "get_acceptance_rules"`, `InputSchema = { documentTypeKey: string (optional) }`; executes via the injected `IAcceptanceRulesResolver` + the construction-bound principal, serializes `ResolvedAcceptanceRules` with `AcceptanceRulesJson.Options`, never throws (returns `ToolExecutionResult` failure). Plus `GetAcceptanceRulesToolFactory` for the 39-17 host. NOT added to the global `IToolExecutor` DI set.

11. **CREATE the admin dashboard screen** (AC7): `packages/dashboard/src/pages/admin/acceptance-rules/AcceptanceRulesAdminPage.tsx` (copy `ConventionsAdminPage.tsx` scaffolding), `packages/dashboard/src/hooks/admin/useAcceptanceRules.ts` (copy `useSystemPrompts.ts` fetch/upsert/reset shape against the Step 9 routes), components `packages/dashboard/src/components/acceptance-rules/RulesTable.tsx` (10 type rows, provenance badge default/base/override) and `RulesEditDialog.tsx` (autonomy slider 70–100, numeric bounds, threshold, always-escalate class picker fed from the registry keys, reviewer selection, two guidance textareas). **MODIFY** `packages/dashboard/src/router.tsx`: add `/admin/acceptance-rules` inside the AdminGuard block beside `/admin/prompts`.

12. **CREATE tests** (Test Plan below) and verify: `dotnet ef migrations has-pending-model-changes` reports none for `TenantDbContext`; `dotnet test`; `pnpm test --filter @tamma/dashboard`.

## Data & Migrations

- New table `acceptance_rules_overrides` (tenant-resident, `TenantDbContext` only — no ControlPlane migration): `Id uuid PK gen_random_uuid()`, `UserId uuid NULL`, `TenantId uuid NULL`, `DocumentTypeKey text NULL` (max 64; NULL = principal base row), `RulesJson jsonb NOT NULL`, `Version int NOT NULL DEFAULT 1`, `CreatedBy uuid NULL`, `UpdatedBy uuid NULL`, `CreatedAt/UpdatedAt timestamptz DEFAULT now()`.
- Constraints: `ck_acceptance_rules_overrides_principal_xor` (exactly one of UserId/TenantId non-null); unique `IX_acceptance_rules_overrides_UserId_TenantId_DocumentTypeKey` with NULLS NOT DISTINCT (PG17).
- Migration: `dotnet ef migrations add AddAcceptanceRulesOverrides --context TenantDbContext --output-dir Migrations/Tenant` → `apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/<timestamp>_AddAcceptanceRulesOverrides.cs` (+ Designer + snapshot update). Additive only; `has-pending-model-changes` clean afterwards.

## Events

- **Emits** (admin mutations, `AcceptanceRulesEventsService`, best-effort): `ACCEPTANCE_RULES.CREATED.SUCCESS`, `ACCEPTANCE_RULES.UPDATED.SUCCESS`, `ACCEPTANCE_RULES.RESET.SUCCESS` — tagged `tenantId`/`userId`, data carries `documentTypeKey`, `autonomyLevel`, `version` (never the guidance prose verbatim beyond length).
- **Consumes**: none. `DOCUMENT.*` (39-6), `APPROVAL.*`/`ESCALATION.*` (39-8), `ORCHESTRATOR.TOOL_INVOKED` (39-17) are other stories' constants; this story only guarantees `ResolvedAcceptanceRules.Version` is present so 39-6's decision event can record the rules version decided under (AC3).

## Test Plan

C# tests in `apps/tamma-elsa/tests/Tamma.Api.Tests/AcceptanceRules/` (service/endpoint/tool — project already hosts PromptStore + Agents suites) and `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/Policy/` (pure model/guardrails). NUnit + FluentAssertions + Moq; Testcontainers only where marked.

- **`AcceptanceRulesModelTests`** (Core.Tests) — `Validate()` rejects autonomy 69/101, accepts 70/100; rejects absurd bounds (0 rounds, 11 rounds, negative repair, threshold 1.1); rejects unknown always-escalate keys per kind and unknown reviewer roles; accepts a fully-populated valid record; closed-enum wire round-trips (`EscalationClassKind`, `ReviewerMode`, `AcceptanceRulesSource`, `AcceptanceEscalationReason` — 6-member count pin, incl. `ToLifecycleOutcome` mapping pins where `RejectRequiresHuman`/`AcceptorJudgment`/`BlockingReviewViolation`/`AlwaysEscalateClass` map to `null`). **AC1, AC4 (validation half), AC8 (bounds-rejection clause).**
- **`AcceptanceDefaultsDriftTests`** (Core.Tests) — pins every default value exactly (`AutonomyLevel.Should().Be(70)`, rounds 2, repair 2, threshold 0.7, `AlwaysEscalate.Should().BeEmpty()`, guidance non-empty) AND pins the **per-type reviewer defaults**: `AcceptanceDefaults.For(plan)` and the code/`review`-type defaults resolve to `Panel` with the exact 7-role roster and `DecisionRule.Majority`; every other type resolves to `SingleReviewer`/`architect`/`DecisionRule.Unanimous` — so the panel-for-plan default (which makes 39-14's PlanReview migration behavior-preserving) cannot silently regress. `RolePhaseMapTests` narrative-comment style so changing a default is a conscious reviewed edit. **AC5.**
- **`AcceptanceContractTests`** (Core.Tests) — `AcceptanceDecision`/`AcceptanceRouting` polymorphic JSON round-trips with pinned `kind` discriminators; the derived-type sets are pinned (exactly 4 and 2 — `accept | request-revision | reject | escalate`; no `AutoAccept` can appear unnoticed); `Reject` with channel `orchestrator` clamps to `Escalate(RejectRequiresHuman)` while `user`/`api` pass through (the human-only pin); for every autonomy level 70..100, `AcceptanceRequestFactory.Create` returns a request whose shape is identical modulo rules payload (the D7 no-branch pin); factory rejects a review envelope whose type is not `review`. **AC2 (contract half).**
- **`AcceptanceGuardrailsTests`** (Core.Tests) — pre-gate: matching always-escalate class (by document type and by agent action) short-circuits to `Escalate(AlwaysEscalateClass)`; rounds exhausted → `Escalate(RoundsExhausted)`; **forged-approval test**: `Clamp(Accept)` with `ReviewFacts(Approve, HasBlockingIssues: true)` and with `(RequestChanges, false)` both yield `Escalate(BlockingReviewViolation)`; `RequestRevision` past budget → `Escalate(RoundsExhausted)`; legitimate `Accept`/`RequestRevision` pass through untouched. **Property-style** (D11): 1000 seeded-random decision sequences against random valid rules always terminate in `Accept`/`Escalate` within `MaxRevisionRounds + 1` gate passes. **AC8.**
- **`AcceptanceRulesServiceTests`** (Api.Tests, Moq'd repository) — resolution ordering per mode: type override → base override → static default, source + version reported correctly (`PromptStoreServiceTests` style); `ResolveForTenantAsync` never consults user rows and vice versa; upsert of unknown `documentTypeKey` throws before repository touch; corrupt `RulesJson` on read throws `TammaError`; mutation events emitted (`PromptEventsServiceTests` style). **AC4, AC6.**
- **`AcceptanceRulesToolParityTests`** (Api.Tests) — the AC3 pin: for the same principal + document type, `GetAcceptanceRulesTool.ExecuteAsync` output JSON equals `AcceptanceRulesJson`-serialized `ResolvedAcceptanceRules` embedded via `AcceptanceRequestFactory` — byte-identical; tool ignores/errors on a principal smuggled into `argumentsJson`; tool never throws on bad input. **AC3.**
- **`AcceptanceRulesEndpointsTests`** (Api.Tests, `PromptEndpointsTenantAdminTests` style) — SaaS: member GET 200, member PUT/DELETE 403, admin/owner PUT 200 writing a tenant-keyed row; single-user: sole user full access writing a user-keyed row; PUT invalid autonomy → 400 with `ACCEPTANCE_RULES.INVALID`; unknown type key → 400; `defaults` read-only. **AC7 (API half), AC6.**
- **`AcceptanceRulesOverridesMigrationTests`** (Api.Tests, **Testcontainers**, `PromptOverridesPrincipalXorMigrationTests` style) — XOR CHECK rejects both-set/both-null; NULLS NOT DISTINCT dedupe on `(principal, NULL)` base rows; jsonb round-trip through the real column. **AC6 (schema).**
- **`AcceptanceRulesAdminPage` Vitest suite** (`packages/dashboard/src/pages/admin/acceptance-rules/__tests__/`) — renders 10 type rows with provenance badges; dial constrained to 70–100; save calls PUT with the edited payload; reset calls DELETE. **AC7 (UI half).**

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — `AcceptanceRules` model, closed enums, validated 70–100 | 1, 2 | `AcceptanceRulesModelTests` |
| 2 — acceptor contract, one routing path, no auto-accept branch | 3, 4 | `AcceptanceContractTests` (contract-level pin; workflow-level publish+suspend re-pinned in 39-6 — see D7) |
| 3 — tool + embedded payload, both from the resolver, identical | 5, 8, 10 | `AcceptanceRulesToolParityTests` |
| 4 — per-type override → default; unknown keys fail-loud vs registry | 1 (`Validate`), 8 | `AcceptanceRulesServiceTests`, `AcceptanceRulesModelTests` |
| 5 — static defaults shipped, drift-pinned | 2 | `AcceptanceDefaultsDriftTests` |
| 6 — two scoping models, XOR schema, resolver per principal | 5, 7, 8 | `AcceptanceRulesServiceTests`, `AcceptanceRulesOverridesMigrationTests`, `AcceptanceRulesEndpointsTests`; per-mode ownership written down in D3 before code |
| 7 — admin API + RBAC parity + admin dashboard screen | 9, 11 | `AcceptanceRulesEndpointsTests`, `AcceptanceRulesAdminPage` Vitest suite |
| 8 — deterministic guardrails; forged-approval; bounded termination | 6, 1 (bound validation) | `AcceptanceGuardrailsTests` (incl. property-style), `AcceptanceRulesModelTests` |

## Dependencies & Sequencing

- **Must land first: 39-2** (`DocumentTypeKey`, `DocumentTypeRegistry`, `DocumentEnvelope`, `DocumentLifecycleOutcome`, `EnumWire` reuse) — D9; its implementation plan is complete. Steps 1–6 compile against it.
- **Must land first: 39-4** — guardrails REFERENCE its `ReviewDecision` enum (`Tamma.Core/Documents/Types/Review.cs`) directly via the `ReviewFacts` projection (D8); there is no local shadow copy to reconcile, so 39-4 is a hard prerequisite for step 6 to compile.
- **Lockstep obligation on 39-6**: the workflow-level "publishes and suspends regardless of autonomy level" test (D7) — record it in 39-6's plan; 39-6 also consumes `AcceptanceRequestFactory`, `AcceptanceGuardrails`, and the resolver interface only (never `Tamma.Api` types).
- **Stubbed, not pulled in**: 39-17 agent host (tool is constructed by `GetAcceptanceRulesToolFactory`; tests exercise the `IToolExecutor` directly with a Moq'd `IAcceptanceRulesResolver`); 39-18 channel (the `AcceptanceRequest` record is transport-agnostic JSON; no SignalR here); 39-8 gate (decision-session id is minted here, resume surface is theirs); 39-20 eligibility (`AssignToRole.Basis` enum defined here; the resolver that computes the role's audience is 39-20's).
- **In place, verified**: `ITammaModeProvider`/`TammaMode`, `prompt_overrides` XOR pattern + repositories, `Permissions.Matrix` + policy plumbing, `IToolExecutor` seam, admin dashboard scaffolding, Testcontainers test precedent.

## Risks & Mitigations

- **39-2 slips and this P0 blocks 39-6.** Mitigation: 39-2's surface used here is small (one enum + parse + registry validation); coordinate so 39-2 Steps 1–2 (the vocabulary) merge early; everything else in this story is independent of 39-2's envelope internals.
- **Rules JSONB becomes a silent schema landfill.** Mitigation: single canonical `AcceptanceRulesJson.Options`, `Validate()` on every write and read, `Version` bump per upsert, drift tests pin the default payload; unknown JSON fields tolerated on read (STJ default) so additive evolution is safe.
- **RBAC drift between prompt store and rules (reads).** D5 deliberately deviates from the `/api/prompts` `SettingsView` read gate to satisfy AC7; the deviation is documented in the route-group comment exactly like the convention store's — reviewers should confirm this is intended, not accidental.
- **Guardrail/acceptor boundary creep** (guardrails start "deciding"). Mitigation: `Clamp` can only pass through or convert to `Escalate` — it structurally cannot produce `Accept` from a non-Accept input; a test asserts `Clamp` never returns `Accept` unless `proposed` was `Accept`.
- **Admin UI scope blowout** (a full policy IDE). Mitigation: v1 is one table + one dialog against the existing endpoints; panel-composition editing can be a JSON-backed subsection if the picker runs long — the API contract, not the UI polish, is the AC.

## Effort Breakdown

| Step(s) | Work | Days |
|---|---|---|
| 1–3 | Rules model + defaults + decision/routing contract | 1.0 |
| 4–5 | Request factory, resolver interface, canonical JSON | 0.5 |
| 6 | Guardrails + property-style termination tests | 1.0 |
| 7 | Entity, EF config, repository, migration | 0.75 |
| 8 | Resolver service + events service | 0.75 |
| 9 | Endpoints, DTOs, RBAC, Program.cs wiring | 0.75 |
| 10 | `get_acceptance_rules` tool + factory + parity test | 0.5 |
| 11 | Admin dashboard page, hook, components, route | 1.0 |
| 12 | Remaining test suites, migration checks, polish | 0.75 |
| **Total** | | **7.0** (story estimate: 5–7 days) |
