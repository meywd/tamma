# Story 42-2: Tool Binding & Config Store (two-scoping)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then check the knowledge base: `.dev/spikes/`, `.dev/bugs/`, `.dev/findings/`, `.dev/decisions/`.

## User Story

As a **user (single-user) or a tenant_admin (SaaS)**, I want to control **which tools are enabled** and
override their **per-role grant and autonomy floor**, stored under the right principal for my operating
mode, so that the tool catalog is governed by the same two-scoping ownership model as prompts — the
sole user owns it in single-user, the tenant_admin owns it in SaaS, and members can't edit it.

## Priority

P0 / Wave 1 — the persistence + resolution order that 42-3's gating reads. Ships right after 42-1.

## The gap (READ FIRST)

42-1 gives each tool a **system-default descriptor** (permission class + autonomy floor). But CLAUDE.md's
universal rule demands per-principal customization with **two** ownership models: in single-user the
sole user owns it; in SaaS the tenant_admin owns it and members don't. There is **no equivalent for
tools**: a tool's floor/enablement is a hardcoded descriptor with no override layer.

Two landed stores already solve exactly this shape and are the template to copy, not to re-derive:
`prompt_overrides` (Story 27-2) and — closer in shape, because it also stores one policy body per
principal — `acceptance_rules_overrides` (Story 39-5). Both are **EF-first**: a POCO in
`Tamma.Data/Entities`, a `DbSet` on `TenantDbContext`, an entity block in `TammaModelConfiguration`,
and an EF-generated tenant migration. Neither ships hand-written DDL.

**One RBAC piece does *not* exist yet.** `Permissions.Matrix`
(`Tamma.Api/Auth/Permissions.cs` L12–81) has no tool permission, and there is no `ToolsManage`
authorization policy. The member-403 this story promises is **new work**, not a reuse — see Scope 3.

## Scope

1. **`tool_bindings` — the EF entity, not hand-rolled DDL.**
   *Corrected: an earlier draft of this story specified a raw `CREATE TABLE`. Tenant tables in this
   repo are declared on the model and the migration is generated from it; a hand-written table drifts
   from `TenantDbContextModelSnapshot` on the next scaffold.* Four artefacts, mirroring
   `AcceptanceRulesOverride` (Story 39-5) member-for-member:

   **(a) POCO** — `Tamma.Data/Entities/ToolBinding.cs`, modelled on
   `Tamma.Data/Entities/AcceptanceRulesOverride.cs`:
   `Guid Id`, `Guid? UserId`, `Guid? TenantId`, `string ToolName` (matches `IToolExecutor.ToolName`),
   `bool Enabled`, `int? AutonomyFloor`, `string[]? AllowedRoles` (`AgentRole` wire names),
   `string? SecretBindingName`, `string? ConfigJson`, `int Version`, `Guid? CreatedBy`,
   `Guid? UpdatedBy`, `DateTime CreatedAt`, `DateTime UpdatedAt`.
   A nullable override field means "not set → fall through to the 42-1 descriptor".

   **(b) DbSet** — `public DbSet<ToolBinding> ToolBindings => Set<ToolBinding>();` on
   `TenantDbContext` (alongside `AcceptanceRulesOverrides`, `TenantDbContext.cs` L56).

   **(c) Model configuration** — an entity block in `TammaModelConfiguration` placed next to the
   `AcceptanceRulesOverride` block (L1621–1654), carrying the same five load-bearing pieces:

   ```csharp
   modelBuilder.Entity<ToolBinding>(entity =>
   {
       entity.ToTable("tool_bindings", t =>
       {
           t.HasCheckConstraint(
               "ck_tool_bindings_principal_xor",
               "(\"UserId\" IS NOT NULL AND \"TenantId\" IS NULL) " +
               "OR (\"UserId\" IS NULL AND \"TenantId\" IS NOT NULL)");
       });
       entity.HasKey(e => e.Id);
       entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
       entity.Property(e => e.ToolName).IsRequired().HasMaxLength(64);   // ToolCallValidator's name budget
       entity.Property(e => e.AllowedRoles).HasColumnType("text[]");      // PromptOverride.Variables precedent
       entity.Property(e => e.ConfigJson).HasColumnType("jsonb");         // AcceptanceRulesOverride.RulesJson precedent
       entity.Property(e => e.Enabled).HasDefaultValue(true);
       entity.Property(e => e.Version).HasDefaultValue(1);
       entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
       entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

       entity.HasIndex(e => new { e.UserId, e.TenantId, e.ToolName })
           .IsUnique()
           .AreNullsDistinct(false)                                       // PG15+; production runs PG17
           .HasDatabaseName("IX_tool_bindings_UserId_TenantId_ToolName");

       if (omitTenantIdColumn) entity.Ignore(e => e.TenantId);
       ApplyTenantFilter(entity, fixedTenantId, e => e.TenantId);
   });
   ```

   **(d) Migration** — generated into `Tamma.Data/Migrations/Tenant/` from the model, never
   hand-authored. `20260722011909_AddAcceptanceRulesOverrides.cs` shows the shape EF emits, including
   `table.CheckConstraint("ck_…_principal_xor", …)` and
   `.Annotation("Npgsql:NullsDistinct", false)` on the unique index.

   **Residency.** `tool_bindings` is **tenant-resident in both modes** — single-user users own a
   personal tenant DB, which is why `AcceptanceRulesRepository.RequireTenantId()` demands an ambient
   tenant id even for `user_id`-keyed rows. Single-user vs SaaS is a *column* distinction inside that
   DB, not a different physical home.

2. **Resolution order (mirrors the two template stores exactly).**
   - **single-user** `(userId, toolName)`: user binding → system-default descriptor (42-1).
   - **SaaS** `(tenantId, toolName)`: tenant binding → system-default descriptor. **No per-user layer.**
   A binding overrides only the fields it sets (`Enabled`, `AutonomyFloor`, `AllowedRoles`,
   `SecretBindingName`, `ConfigJson`); unset fields fall through to the descriptor — never a full-record
   replace.

   **Where the seam lives.** `IToolBindingResolver` goes in **`Tamma.Core`** and the EF-backed
   implementation in **`Tamma.Api`**, exactly like `IAcceptanceRulesResolver`
   (`Tamma.Core/Documents/Policy/IAcceptanceRulesResolver.cs`) ↔ `AcceptanceRulesService`
   (`Tamma.Api/Services/AcceptanceRules/`). 42-3's stage-1 gate runs in `Tamma.Api`
   (`ManagedAgent`), so `Tamma.Core` siting is not strictly forced — but it keeps the contract
   reachable from `Tamma.Activities` if a later story needs it, and it costs nothing.
   Use **two distinctly-named methods**, never a `Guid?`/`Guid` overload pair:
   `ResolveAsync(Guid? userId, string toolName, …)` and
   `ResolveForTenantAsync(Guid tenantId, string toolName, …)` — a non-null `Guid` binds to both
   overloads and the non-nullable always wins, silently routing single-user callers onto the SaaS path
   (the rationale `IAcceptanceRulesResolver.cs` L10–15 records).

3. **RBAC — a NEW permission and a NEW policy (this does not exist).**
   *Corrected: an earlier draft said RBAC "mirrors the Prompt Store", implying it was already
   available. `Permissions.Matrix` has no tool entry and no `ToolsManage` policy is registered, so the
   member-403 has to be built.* Three edits, each with a landed precedent (Story 39-5's
   `acceptance-rules:manage`, added in the same three places):
   - `Tamma.Api/Auth/Permissions.cs` — add `["tools:manage"] = ["admin", "owner"]` to `Matrix`.
     (`settings:manage` is **owner-only** and would 403 every `tenant_admin`, which is why each of
     these stores has its own admin+owner permission rather than reusing it.)
   - `Tamma.Api/Program.cs` — register the policy next to `AcceptanceRulesManage` (L1618–1622):
     `options.AddPolicy("ToolsManage", p => { p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, "ApiKey"); p.AddRequirements(new PermissionRequirement("tools:manage")); });`
   - `Tamma.Api/Program.cs` L1724–1726 — add `"ToolsManage"` to the dev-without-JWT permissive
     policy list. **Easy to miss and it breaks local dev:** that branch replaces every *named* policy
     with `AllowAnonymousRequirement`, and a route referencing a policy absent from that array fails
     authorization in Development.

   | Action | single-user | SaaS |
   |---|---|---|
   | GET resolved binding | any authenticated user | any tenant member |
   | PUT/DELETE binding | any authenticated user | `tenant_owner`/`tenant_admin` only (member → 403) |
   | GET system defaults (descriptors) | any authenticated user | any member |

   Single-user needs no code-path split: every signed-up user is auto-`owner` of their personal
   tenant, so `tools:manage` grants them write access through the same policy.

4. **API** (endpoint shape identical across modes; the handler picks the key from
   `ITammaModeProvider.Mode` + `ITenantContext`, exactly like `AcceptanceRulesEndpoints`):
   ```
   GET    /api/tools                     — list catalog, resolved for the current principal
   GET    /api/tools/:toolName           — resolved binding + descriptor
   PUT    /api/tools/:toolName           — upsert binding (ToolsManage)
   DELETE /api/tools/:toolName           — delete binding → fall back to descriptor (ToolsManage)
   GET    /api/tools/defaults            — system-default descriptors
   ```
   Group carries `RequireAuthorization("AuthenticatedAny")`; the two mutations carry
   `RequireAuthorization("ToolsManage")` — the shape at `Program.cs` L2729–2734.

5. **Concurrency: version counter + 409 on the upsert race.**
   *(New — the earlier draft had neither.)* `Version` is an **application-layer counter**, bumped by
   the repository on every update; it is deliberately **not** `.IsConcurrencyToken()` (the distinction
   `Convention.cs` L81–85 spells out). Its job is audit + conflict *detection*, not EF-enforced
   optimistic locking.
   The real 409 comes from the repository's check-then-insert window (`UpsertInternal`:
   `FirstOrDefaultAsync` → `Add` → `SaveChangesAsync`): two concurrent same-key upserts both miss the
   read and the second hits the unique index, raising Postgres `23505`. Map it the way the prompt and
   convention stores already do — `PromptEndpoints.IsUniqueViolation(DbUpdateException)` →
   `Results.Conflict(new { error = "Conflict — concurrent same-key upsert race; retry.", code = "CONCURRENT_UPSERT_CONFLICT" })`.
   Reuse that helper rather than minting a third payload shape.
   **Stated limit, not a defect to design around here:** two concurrent PUTs against an *existing* row
   are last-write-wins (no token, no `If-Match`). If a later story needs true lost-update protection
   it must add an `expectedVersion` precondition; this story does not, and no AC asserts it.

6. **`SecretBindingName` is a logical name, resolved by 42-4 — and single-user has no user-scoped
   secret.** *Corrected: nothing here implies a per-user secret scope.* `SecretScope` has exactly
   `Platform` and `Tenant`, and `SecretRef`'s constructor throws on either mismatch. A binding row
   may legitimately be keyed on `user_id`, but the secret it names still resolves to
   `SecretRef.ForPlatform(name)` in single-user and `SecretRef.ForTenant(tenantId, name)` in SaaS.
   User ownership of a secret is metadata (`SecretMetadata.OwnerUserId`), never a scope.

## Acceptance Criteria

1. A Postgres-backed migration test (Testcontainers PG17, the
   `AcceptanceRulesOverridesMigrationTests` fixture shape) asserts: a row with **both** `UserId` and
   `TenantId` set is rejected by `ck_tool_bindings_principal_xor`; a row with **neither** set is
   rejected; and a second `(NULL, tenantId, toolName)` row is rejected by the unique index — proving
   `NULLS NOT DISTINCT` is in effect (EF-InMemory enforces neither, so this test cannot be written
   against it).
2. `IToolBindingResolver` returns the 42-1 descriptor values verbatim when no binding row exists; with
   a row that sets **only** `AutonomyFloor`, the resolved result carries the row's floor and the
   descriptor's `AllowedRoles`, `Enabled`, and `SecretBindingName` (field-level merge, not replace).
3. `ResolveAsync(userId, …)` returns null-binding fall-through when only a `tenant_id`-keyed row
   exists for the same `toolName`, and `ResolveForTenantAsync(tenantId, …)` likewise ignores a
   `user_id`-keyed row — one test per direction, asserting the other column is never consulted.
4. A SaaS caller with role `member` receives **403** from `PUT /api/tools/{toolName}` and
   `DELETE /api/tools/{toolName}`; `admin` and `owner` receive 200. A test asserts
   `Permissions.HasPermission("member", "tools:manage")` is `false` and
   `HasPermission("admin", "tools:manage")` is `true`.
5. `DELETE` removes the row and a subsequent `GET /api/tools/{toolName}` reports the 42-1 descriptor
   floor — asserted by value, not by "falls back".
6. Two concurrent `PUT`s for the same `(principal, toolName)` where the row does not yet exist produce
   exactly one created row; the loser receives HTTP **409** with body code
   `CONCURRENT_UPSERT_CONFLICT` (test forces the race by seeding the row between the repository's read
   and its save, or by driving the two upserts in parallel against a real Postgres).
7. An update to an existing binding increments `Version` by exactly 1 and stamps `UpdatedBy` with the
   acting user id; a create sets `Version = 1` and stamps `CreatedBy`.
8. `GET /api/tools` returns one entry per tool in the merged catalog (DI-seeded **plus** anything
   registered through 42-1's dynamic seam), each carrying its resolved binding; a test registers a
   tool dynamically and asserts it appears.

## Events

`TOOL.BINDING_UPDATED` / `TOOL.BINDING_DELETED` DCB events (config-change audit) tagged with the
principal key and `toolName` — never the secret (`SecretBindingName` is a logical name, so it is safe;
the resolved value never reaches here), and never the `ConfigJson` payload verbatim. Pass it through
the shipped `ToolOutputHelper.RedactSecrets` before it enters an event, and treat that as a backstop
rather than a guarantee: it is a regex pass over known key/token/PEM/JWT shapes, not schema-aware.

*Corrected: these are **not** emitted via `TammaEventEmitter` → `tamma:events`.* That emitter
structurally requires an `ActivityExecutionContext` **and** an `IActivity`
(`TammaActivity.Emit(context, source, logger, evt)`), and this store is an API-side service with
neither. Append directly through `IEventRepository`, the way
`AcceptanceRulesEventsService` and `PromptEventsService` already do for the two template stores —
best-effort, log-and-swallow, never faulting the mutation.

## Single-user vs SaaS

This story **is** the two-scoping model for tools — it is the whole point. It reuses the landed
`acceptance_rules_overrides` shape (XOR principal, per-mode resolution, member-write 403, no per-user
SaaS layer) rather than inventing a new one. The one thing it does **not** inherit is RBAC: the
`tools:manage` permission and `ToolsManage` policy are new (Scope 3).

## Dependencies

- **42-1** (descriptors are the fall-through target; `ToolName`/floor semantics come from there).
- **`acceptance_rules_overrides` (39-5) + `prompt_overrides` (27-2)** as the reference
  implementations: entity, model block, generated migration, repository upsert, endpoint mode branch,
  `IsUniqueViolation` → 409 helper, and the three-place permission registration.
- **Unblocks:** 42-3 (consumes `IToolBindingResolver`), 42-4 (`SecretBindingName`).
- **Consumer-side note for 42-3:** the stage-1 gate runs in `ManagedAgent`, and
  `ManagedAgentRequest` carries `TenantId` + `Role` but **no `UserId`**. Single-user resolution keys
  on `user_id`, so 42-3 must thread an auth-derived user id into that request. This story only needs
  to expose the two-method resolver; wiring the principal is 42-3's.

## Risks

- **Divergence from the landed pattern.** If this reimplements resolution/RBAC subtly differently, the
  platform grows a third inconsistent override model. Mitigation: copy the `AcceptanceRulesOverride`
  entity + model block + repository structurally, and reuse `PromptEndpoints.IsUniqueViolation` /
  `Conflict()` rather than re-deriving the 409 payload.
- **`omitTenantIdColumn` under a principal-XOR CHECK.** The tail
  `if (omitTenantIdColumn) entity.Ignore(e => e.TenantId)` is carried verbatim from both templates,
  but on a XOR table dropping `TenantId` would leave the CHECK referencing a missing column. The flag
  defaults to `false` and no deployment sets it today, so this is inherited latent behaviour, not a
  new defect — do **not** "fix" it here in isolation; if it is ever enabled, all three tables need one
  decision together.
- **Silent catalog drift.** A binding row can name a `tool_name` no executor provides (typo, or a tool
  removed from DI). Resolution must ignore orphan rows rather than synthesizing a phantom tool; the
  `GET /api/tools` list is driven by the registry, with bindings joined onto it.

## Estimated Effort

Medium. ~3 days (entity + model block + generated migration + resolver + CRUD endpoints + the new
permission/policy + Docker-gated migration test, largely paralleling the acceptance-rules store).
