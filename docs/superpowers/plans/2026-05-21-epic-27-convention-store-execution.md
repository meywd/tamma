# Epic 27 Convention Store + Taxonomy — Execution Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the DB-backed, multi-tenant Convention Store (stories 27-8…27-14) and the shared `(role, action)` taxonomy it rides on (27-15…27-19), so the `{{conventions}}` workflow variable resolves per-tenant by exact `(role, action)` lookup instead of `.tamma/config.json`.

**Architecture:** Mirror the existing Prompt Store exactly — EF Core entity + per-tenant `ITenantDbContextFactory` repository, a scoped resolver service with user-scoped + `…ForTenant` variants, Minimal-API endpoints behind named auth policies, and an Elsa activity that calls the API over **HTTP** (not DI). A single code-defined taxonomy in `RolePhaseMap` (rebuilt on `AgentRole`/`AgentAction` enums) is the one vocabulary both prompts and conventions key off; both seeds are codegen'd from it and a build test prevents drift.

**Tech Stack:** .NET 8, EF Core (Npgsql), Minimal APIs, Elsa workflows, NUnit 3.14 + FluentAssertions + Moq, `InMemoryDbFixture` for unit tests / Testcontainers.PostgreSql + Respawn for integration. Target branch: **`feat/wave-b`** (user's choice).

---

## ⚠️ Story-file corrections (read before dispatching ANY wave)

The research pass found three places where the story files describe a codebase that doesn't exist. **These corrections override the story text.** An agent that follows the story verbatim will build the wrong thing.

| # | Story says | Reality | Correct approach |
|---|---|---|---|
| C1 | 27-8: "create `database/migrations/018_convention_store.sql`" | Schema is **EF Core migrations** (`src/Tamma.Data/Migrations/*.cs`), applied at startup via `dbContext.Database.Migrate()` (`Tamma.Api/Program.cs:1856`). `database/archived-sql-migrations/` is dead. | Generate a migration with `dotnet ef migrations add ConventionStore --project src/Tamma.Data` (timestamped `MigrationBuilder` class + Designer + snapshot). Map the entity in `src/Tamma.Data/TammaModelConfiguration.cs`, add a `DbSet` to `TenantDbContext`. **No numbered `.sql` file, no "018".** |
| C2 | 27-13: activity injects the convention service via DI | Elsa activities call the API over **HTTP** (see `ResolvePromptFromRegistryActivity` → `{Engine:CallbackUrl}/api/prompts/{role}/{action}/render` with `X-Tenant-Id`). | `ResolveConventionsActivity` calls a **new authenticated convention HTTP endpoint**, mirroring the prompt activity. This makes the convention *resolve* endpoint (27-10) a hard prerequisite of 27-13. |
| C3 | 27-15: introduce `AgentRole`/`AgentAction` enums | Today taxonomy is **strings** (`FrozenSet<string>` in `RolePhaseMap.cs`); no enums exist. 27-15 AC#2 expands `AgentAction` from the current **10** actions to **~70** SPEC §4 tokens. | This is the intended (large) change — proceed with enums, but treat AC#8 ("4 consumers compile unchanged, identical behaviour") as the regression gate. The action expansion ripples into seeds (27-16) and the drift test (27-17). |

Two more standing constraints from `CLAUDE.md` + the prompt-store precedent:

- **C4 — Dual scoping is mandatory.** Every store method needs parallel single-user (`userId`) and SaaS (`…ForTenant(tenantId, …)`) variants with **distinct names** (the prompt store documents an overload-resolution hazard — do not overload, name them differently).
- **C5 — Per-tenant DB routing.** Convention overrides live in the per-tenant DB via `ITenantDbContextFactory` / `RequireTenantId()`, exactly like `prompt_overrides` — **not** the control-plane DB.

---

## Dependency DAG & wave structure

Source: `docs/stories/epic-27/README.md` dependency graph, reconciled with the corrections above (C2 adds 27-10 → 27-13).

```
Wave 0:  27-15  taxonomy (AgentRole/AgentAction + RolePhaseMap rebuild)   [solo]
            │
Wave 1:  27-16 codegen ─────────────┐        27-19 dispatch-site migration  [2-way]
            │                        │
Wave 2:  27-17 drift test           27-8 convention EF migration + entity  [2-way]
            │ (needs 16)            │ (needs 15,16; C1)
            │                        │
Wave 3:                            27-9 ConventionStore service (C#)        [solo]
                                     │ (needs 8; C4,C5)
            ┌────────────────────────┼───────────────────────────┐
Wave 4:  27-10 API endpoints     (27-10 unblocks 27-13)        27-14 events  [up to 3-way*]
            │                        │
            │                     27-13 Elsa activity (HTTP; needs 10)
            │
Wave 5:  27-11 admin UI ─────────── 27-12 tenant UI (needs 10[,11])         [2-way]

Separate track (anytime after Wave 1): 27-18 prompt-store taxonomy reshape (needs 15,16,27-1)
```

\* Wave 4 parallelism is constrained by shared files — see the conflict map. 27-13 depends on 27-10's resolve endpoint (C2), so within Wave 4 run **27-10 first**, then 27-13 and 27-14 in parallel.

### File-conflict map (why parallelism is limited)

| File | Touched by | Serialization rule |
|---|---|---|
| `src/Tamma.Api/Services/Agents/RolePhaseMap.cs` | 27-15 | Wave 0 only; nothing else edits it concurrently |
| `src/Tamma.Data/TammaModelConfiguration.cs` | 27-8 | Wave 2; single editor |
| `src/Tamma.Data/TenantDbContext.cs` + migration snapshot | 27-8 | Wave 2; single editor (EF snapshot conflicts are painful) |
| `Tamma.Api/Program.cs` (endpoint + policy registration) | 27-10, 27-11/12 (none), 27-18 | 27-10 and 27-18 both edit Program.cs → **do not run in the same wave**; 27-18 is sequenced to a wave where 27-10 is idle |
| `ConventionTemplates.cs` (reset-default source) | 27-9 (reads), 27-16 (reads) | read-only by both; safe |
| Seed codegen output | 27-16 (writes), 27-8 (consumes) | 27-16 before 27-8 |

**Worktree policy:** within a wave, genuinely file-disjoint stories (e.g. 27-11 admin UI vs 27-12 tenant UI; 27-13 activity vs 27-14 events) run as parallel agents in **isolated worktrees** (`Agent` tool `isolation: "worktree"`), then I integrate onto `feat/wave-b` and resolve any incidental conflicts. Stories that touch `Program.cs` or the EF snapshot run **sequentially on the branch**, never parallel-in-worktrees (snapshot/registration merge conflicts cost more than the parallelism saves).

---

## Verification gates (every wave)

A wave is not "done" until, from `apps/tamma-elsa`:

```bash
dotnet build Tamma.sln --no-restore -c Release          # must succeed, 0 warnings-as-errors
dotnet test  Tamma.sln --no-build  -c Release           # all green
```

…plus, after waves that change migrations (27-8) or the model snapshot:

```bash
dotnet ef migrations list --project src/Tamma.Data      # new migration present, snapshot consistent
```

…plus the dashboard typecheck after UI waves (27-11/12):

```bash
pnpm --filter @tamma/dashboard-user run typecheck        # or the relevant dashboard package
```

Between waves: I review the agent's diff against the story ACs + the corrections table, confirm the gate output, then dispatch the next wave. **No wave starts until the prior wave's gate is green on `feat/wave-b`.**

---

## Just-in-time detailed plans

This document specifies **Wave 0 in full bite-sized detail** (below). Waves 1-5 are specified as **agent dispatch briefs** (self-contained instructions + the story file + corrections), because their non-placeholder code depends on signatures that don't exist yet — e.g. Wave 4's endpoint handlers can't be written without Wave 3's real `IConventionStore` method names. Each later wave's detailed bite-sized plan is written **just before that wave executes**, grounded in the by-then-real code, and saved as `docs/superpowers/plans/2026-05-21-epic-27-waveN-<story>.md`.

This is a deliberate anti-placeholder measure, not deferral of thinking: the DAG, conflict map, gates, and per-story briefs below are complete now.

---

## WAVE 0 — Story 27-15: AgentRole/AgentAction Taxonomy + RolePhaseMap rebuild

**Story file:** `docs/stories/epic-27/27-15-agent-role-action-taxonomy.md`
**SPEC authority for the action token list:** `docs/superpowers/specs/2026-05-18-role-action-taxonomy-and-resolution-design.md` §4 (per-role action sets) — this is the literal source the engineer transcribes; it is not a placeholder.

**Files:**
- Create: `apps/tamma-elsa/src/Tamma.Api/Services/Agents/EnumWire.cs` (shared `[Wire]` attribute + map helper)
- Create: `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentRole.cs`
- Create: `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentAction.cs`
- Modify: `apps/tamma-elsa/src/Tamma.Api/Services/Agents/RolePhaseMap.cs`
- Test (create): `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentRoleTests.cs`
- Test (create): `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentActionTests.cs`
- Test (create): `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/RolePhaseMapTaxonomyTests.cs`

**Current state (verified):** `RolePhaseMap` is string-based — `ValidRoles` (8), `ValidActions` (10), `s_primaryPhase`, `s_eligibleRoles`, `LegacyRoleAliases`, `LegacyPhaseAliases`, `NormalizeRole/Phase`, `Assert*`, `GetPrimaryPhaseForRole`, `GetEligibleRolesForPhase`, `IsRoleEligibleForPhase`. The 4 consumers (`AgentResolverService`, `ProviderChainResolver`, `AgentEndpoints`, `DefaultAgentConfig`) call these by string. AC#8 requires they keep compiling and behaving identically.

### Taxonomy representation (DECISION — strongly typed + easy to evolve)

Roles and actions are real C# `enum`s, but the persisted/transmitted token is carried in a `[Wire("…")]` attribute on each member, **decoupling the C# identifier from the wire string**. Maps are built once by a shared generic helper that fails fast if any member lacks `[Wire]`. This is the single mechanism for both `AgentRole` and `AgentAction`.

Why this satisfies "strongly typed but easy to modify in later versions":
- **Strongly typed:** real enums → exhaustive `switch`, `Enum.GetValues`, value-type, and the `RolePhaseMap` eligibility matrix is keyed by typed enum values (a wrong pair is a *compile* error).
- **Single edit point:** adding an action = one attributed line; its wire string is colocated, so you cannot add a member and forget its mapping (static ctor throws; drift test 27-17 also catches it).
- **Refactor-safe:** rename the C# member freely (e.g. `ContextScan → ScanContext`) without touching any DB row or Elsa payload — only `[Wire]` defines the contract.
- **Versionable:** change a wire token in v2 = edit `[Wire]` + add a `RolePhaseMap.LegacyPhaseAlias` (old→new) so existing data still parses. Deprecate = remove member + alias old→replacement + seed migration.

Shared helper (create `apps/tamma-elsa/src/Tamma.Api/Services/Agents/EnumWire.cs`):

```csharp
using System.Reflection;
namespace Tamma.Api.Services.Agents;

[AttributeUsage(AttributeTargets.Field)]
public sealed class WireAttribute(string wire) : Attribute { public string Wire => wire; }

public static class EnumWire<TEnum> where TEnum : struct, Enum
{
    private static readonly IReadOnlyDictionary<TEnum, string> s_toWire;
    private static readonly IReadOnlyDictionary<string, TEnum> s_fromWire;

    static EnumWire()
    {
        var map = Enum.GetValues<TEnum>().ToDictionary(v => v, v =>
            typeof(TEnum).GetField(v.ToString())!.GetCustomAttribute<WireAttribute>()?.Wire
            ?? throw new InvalidOperationException($"{typeof(TEnum).Name}.{v} is missing [Wire]"));
        s_toWire = map;
        s_fromWire = map.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);
    }

    public static string ToWire(TEnum v) => s_toWire[v];
    public static bool TryParse(string wire, out TEnum v) => s_fromWire.TryGetValue(wire, out v);
    public static IReadOnlyCollection<string> AllWires => (IReadOnlyCollection<string>)s_fromWire.Keys;
}
```

### Task 0.1: AgentRole enum + wire mapping (TDD)

- [ ] **Step 1: Write the failing test** — `tests/Tamma.Api.Tests/Agents/AgentRoleTests.cs`

```csharp
using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;

namespace Tamma.Api.Tests.Agents;

[TestFixture]
public class AgentRoleTests
{
    [Test]
    public void Has_exactly_eight_roles()
    {
        Enum.GetValues<AgentRole>().Length.Should().Be(8);
    }

    [TestCase(AgentRole.Developer, "developer")]
    [TestCase(AgentRole.ProductOwner, "product_owner")]
    [TestCase(AgentRole.SeniorDeveloper, "senior_developer")]
    [TestCase(AgentRole.TechWriter, "tech_writer")]
    public void ToWire_returns_canonical_snake_string(AgentRole role, string wire)
    {
        role.ToWire().Should().Be(wire);
    }

    [TestCase("implementer", AgentRole.Developer)]   // legacy alias
    [TestCase("analyst", AgentRole.ProductOwner)]    // legacy alias
    [TestCase("developer", AgentRole.Developer)]     // exact
    public void Parse_applies_legacy_aliases_then_exact(string input, AgentRole expected)
    {
        AgentRoleExtensions.Parse(input).Should().Be(expected);
    }

    [Test]
    public void Parse_throws_TammaError_with_INVALID_ROLE_on_unknown()
    {
        var act = () => AgentRoleExtensions.Parse("wizard");
        act.Should().Throw<TammaError>().Where(e => e.Code == "INVALID_ROLE");
    }

    [Test]
    public void Roundtrip_holds_for_every_role()
    {
        foreach (var r in Enum.GetValues<AgentRole>())
            AgentRoleExtensions.Parse(r.ToWire()).Should().Be(r);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**
  Run: `cd apps/tamma-elsa && dotnet test Tamma.sln --filter FullyQualifiedName~AgentRoleTests`
  Expected: FAIL — `AgentRole` / `AgentRoleExtensions` / `TammaError` not found (confirm `TammaError` namespace; if the C# port names it differently, align the test).

- [ ] **Step 3: Write minimal implementation** — `src/Tamma.Api/Services/Agents/AgentRole.cs` (uses the `[Wire]` + `EnumWire<>` mechanism from the decision section above)

```csharp
namespace Tamma.Api.Services.Agents;

public enum AgentRole
{
    [Wire("developer")]        Developer,
    [Wire("tester")]           Tester,
    [Wire("security")]         Security,
    [Wire("devops")]           Devops,
    [Wire("architect")]        Architect,
    [Wire("product_owner")]    ProductOwner,
    [Wire("senior_developer")] SeniorDeveloper,
    [Wire("tech_writer")]      TechWriter,
}

public static class AgentRoleExtensions
{
    public static string ToWire(this AgentRole role) => EnumWire<AgentRole>.ToWire(role);

    public static AgentRole Parse(string input)
    {
        var normalized = RolePhaseMap.NormalizeRole(input);
        if (EnumWire<AgentRole>.TryParse(normalized, out var role)) return role;
        throw new TammaError("INVALID_ROLE", $"Unknown role: '{input}'.");
    }
}
```

  (If the C# `TammaError` ctor signature differs from `(code, message)`, match the existing one — grep `class TammaError`.)

- [ ] **Step 4: Run test to verify it passes**
  Run: `cd apps/tamma-elsa && dotnet test Tamma.sln --filter FullyQualifiedName~AgentRoleTests`
  Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add apps/tamma-elsa/src/Tamma.Api/Services/Agents/EnumWire.cs \
        apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentRole.cs \
        apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentRoleTests.cs
git commit -m "feat(epic-27): AgentRole enum + [Wire]/EnumWire helper (Story 27-15)"
```

### Task 0.2: AgentAction enum from SPEC §4 (TDD)

- [ ] **Step 1: Transcribe the canonical action token list.** Open SPEC §4 and copy the **union of all distinct action tokens** (AC#2; ~70 values). Each distinct kebab token becomes one `AgentAction` member in PascalCase; shared tokens (`context-scan`, `code-review`, `plan-review`, `write-tests`) appear once. Build the PascalCase↔kebab table as the single source of truth. *(This is real source content from the SPEC, not a placeholder — the enum literally is SPEC §4.)*

- [ ] **Step 2: Write the failing test** — `tests/Tamma.Api.Tests/Agents/AgentActionTests.cs`

```csharp
using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;

namespace Tamma.Api.Tests.Agents;

[TestFixture]
public class AgentActionTests
{
    [Test]
    public void Roundtrip_holds_for_every_action()
    {
        foreach (var a in Enum.GetValues<AgentAction>())
            AgentActionExtensions.Parse(a.ToWire()).Should().Be(a);
    }

    [TestCase("CONTEXT_ANALYSIS", "context-scan")]  // legacy phase alias → wire
    [TestCase("CODE_GENERATION", "implement")]
    public void Parse_applies_legacy_phase_aliases(string legacy, string canonicalWire)
    {
        AgentActionExtensions.Parse(legacy).ToWire().Should().Be(canonicalWire);
    }

    [Test]
    public void Parse_throws_TammaError_with_INVALID_ACTION_on_unknown()
    {
        var act = () => AgentActionExtensions.Parse("teleport");
        act.Should().Throw<TammaError>().Where(e => e.Code == "INVALID_ACTION");
    }

    [Test]
    public void Shared_tokens_are_single_values()
    {
        // context-scan/code-review/plan-review/write-tests exist exactly once
        var wires = Enum.GetValues<AgentAction>().Select(a => a.ToWire()).ToList();
        wires.Should().OnlyHaveUniqueItems();
    }

    [Test]
    public void Every_member_has_a_Wire_attribute()
    {
        // Touching ToWire() forces the EnumWire static ctor; a missing [Wire]
        // throws InvalidOperationException — guards the single-edit-point invariant.
        var act = () => Enum.GetValues<AgentAction>().Select(a => a.ToWire()).ToList();
        act.Should().NotThrow();
    }
}
```

- [ ] **Step 3: Run test to verify it fails**
  Run: `cd apps/tamma-elsa && dotnet test Tamma.sln --filter FullyQualifiedName~AgentActionTests`
  Expected: FAIL — `AgentAction` not found.

- [ ] **Step 4: Write the implementation** — `src/Tamma.Api/Services/Agents/AgentAction.cs`, mirroring `AgentRole`: a real `enum` with one `[Wire("…")]`-attributed member per SPEC §4 token (from Step 1), plus an `AgentActionExtensions` with `ToWire` (delegates to `EnumWire<AgentAction>.ToWire`) and `Parse` (applies `RolePhaseMap.NormalizePhase` first, then `EnumWire<AgentAction>.TryParse`, else `throw new TammaError("INVALID_ACTION", …)`). No per-action dictionary — the `[Wire]` attributes are the single source.

```csharp
namespace Tamma.Api.Services.Agents;

public enum AgentAction
{
    [Wire("context-scan")] ContextScan,
    [Wire("plan")]         Plan,
    [Wire("plan-review")]  PlanReview,
    [Wire("implement")]    Implement,
    [Wire("write-tests")]  WriteTests,
    [Wire("code-review")]  CodeReview,
    // … remaining SPEC §4 tokens, one attributed line each (~70 total)
}

public static class AgentActionExtensions
{
    public static string ToWire(this AgentAction action) => EnumWire<AgentAction>.ToWire(action);

    public static AgentAction Parse(string input)
    {
        var normalized = RolePhaseMap.NormalizePhase(input);
        if (EnumWire<AgentAction>.TryParse(normalized, out var action)) return action;
        throw new TammaError("INVALID_ACTION", $"Unknown action: '{input}'.");
    }
}
```

- [ ] **Step 5: Run test to verify it passes**
  Run: `cd apps/tamma-elsa && dotnet test Tamma.sln --filter FullyQualifiedName~AgentActionTests`
  Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentAction.cs \
        apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentActionTests.cs
git commit -m "feat(epic-27): AgentAction enum from SPEC §4 (Story 27-15)"
```

### Task 0.3: Rebuild RolePhaseMap on the enums, preserving behaviour (TDD)

- [ ] **Step 1: Write the regression test** — `tests/Tamma.Api.Tests/Agents/RolePhaseMapTaxonomyTests.cs` — pin the **current observable behaviour** so the rebuild can't change it:

```csharp
using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;

namespace Tamma.Api.Tests.Agents;

[TestFixture]
public class RolePhaseMapTaxonomyTests
{
    [Test]
    public void ValidRoles_derive_from_enum()
    {
        RolePhaseMap.ValidRoles.Should().BeEquivalentTo(
            Enum.GetValues<AgentRole>().Select(r => r.ToWire()));
    }

    [TestCase("developer", "implement")]
    [TestCase("architect", "plan")]
    [TestCase("product_owner", "triage")]
    [TestCase("tech_writer", "summarize")]
    public void GetPrimaryPhaseForRole_unchanged(string role, string action)
    {
        RolePhaseMap.GetPrimaryPhaseForRole(role).Should().Be(action);
    }

    [TestCase("implement", "developer", true)]
    [TestCase("implement", "tester", false)]
    [TestCase("code-review", "security", true)]
    public void IsRoleEligibleForPhase_unchanged(string action, string role, bool eligible)
    {
        RolePhaseMap.IsRoleEligibleForPhase(action, role).Should().Be(eligible);
    }
}
```

  ⚠️ **Note the AC#2 vs AC#8 tension:** expanding `AgentAction` to ~70 tokens must NOT change `ValidActions`-driven eligibility for the original 10 phases the consumers use. If SPEC §4's per-role sets would change an existing eligibility answer, that is a SPEC reconciliation question — surface it, don't silently change behaviour.

- [ ] **Step 2: Run test to verify current behaviour passes (baseline)**
  Run: `cd apps/tamma-elsa && dotnet test Tamma.sln --filter FullyQualifiedName~RolePhaseMapTaxonomyTests`
  Expected: PASS against the *current* string implementation (it's a characterization test).

- [ ] **Step 3: Rebuild `RolePhaseMap.cs`** so `ValidRoles`/`ValidActions` derive from `Enum.GetValues<AgentRole>().Select(r => r.ToWire())` / `…<AgentAction>()`, and `s_eligibleRoles` is replaced by the SPEC §4 per-role action sets. Express that matrix **with typed `AgentRole`/`AgentAction` values** (e.g. `Dictionary<AgentRole, FrozenSet<AgentAction>>`) so an invalid pair is a compile error; project to wire strings only where the existing string-keyed public methods need them. Keep `GetPrimaryPhaseForRole`, `Normalize*`, `Assert*`, legacy aliases observably identical, and **keep the public string-keyed method signatures** (so the 4 consumers compile unchanged — AC#8). Internally those methods convert string→enum at entry via `AgentRole.Parse`/`AgentAction.Parse`.

- [ ] **Step 4: Run the full taxonomy + consumer tests**
  Run: `cd apps/tamma-elsa && dotnet test Tamma.sln --filter "FullyQualifiedName~Agents|FullyQualifiedName~AgentResolver|FullyQualifiedName~ProviderChain"`
  Expected: PASS (taxonomy + the 4-consumer regression).

- [ ] **Step 5: Full wave gate**
  Run: `cd apps/tamma-elsa && dotnet build Tamma.sln --no-restore -c Release && dotnet test Tamma.sln --no-build -c Release`
  Expected: build success, all tests green.

- [ ] **Step 6: Commit**

```bash
git add apps/tamma-elsa/src/Tamma.Api/Services/Agents/RolePhaseMap.cs \
        apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/RolePhaseMapTaxonomyTests.cs
git commit -m "feat(epic-27): rebuild RolePhaseMap on AgentRole/AgentAction enums (Story 27-15)"
```

**Wave 0 exit criteria:** all of 27-15 AC#1-8 satisfied; `dotnet build` + `dotnet test` green on `feat/wave-b`; the 4 consumers unchanged. → I review, then write the Wave 1 detailed plan.

---

## WAVES 1-5 — Agent dispatch briefs

Each brief is the self-contained prompt for the agent(s) executing that wave. The detailed bite-sized plan for a wave is written just before dispatch (see "Just-in-time detailed plans"). All agents get: the story file path, the corrections table (C1-C5), "follow the Prompt Store as the blueprint," and the wave gate.

### Wave 1 (parallel, 2 worktrees) — after Wave 0 green

- **Agent 1 → Story 27-16 (Taxonomy Codegen).** Story: `docs/stories/epic-27/27-16-taxonomy-codegen.md`. Generate the prompt + convention seed data from the `RolePhaseMap` per-role action sets (now enum-derived). Output is the seed source consumed by 27-8's migration. Must be deterministic + idempotent. Gate: build+test green.
- **Agent 2 → Story 27-19 (Workflow Dispatch-Site Migration).** Story: `docs/stories/epic-27/27-19-dispatch-site-migration.md`. Migrate workflow dispatch sites to the canonical `(role, action)` enums. **Does not touch Program.cs endpoints or the EF snapshot** — confirm before parallelizing; if it does, sequence it solo. Gate: build+test green; existing workflow tests pass.

Integration note: 27-16 writes seed source; 27-19 edits workflow dispatch code — disjoint, safe to parallelize.

### Wave 2 (parallel, 2 worktrees) — after Wave 1 green

- **Agent 1 → Story 27-17 (Taxonomy Drift Build Test).** Story: `27-17-taxonomy-drift-build-test.md`. A build/test that fails if seeds, enums, and SPEC §4 diverge. Test-project only. Gate: the drift test passes against current seeds; build green.
- **Agent 2 → Story 27-8 (Convention EF Migration + entity).** Story: `27-8-convention-store-database-schema.md` **+ correction C1**. Create a `Convention` entity, map it in `TammaModelConfiguration.cs` (`ToTable("conventions")`, `UNIQUE (tenant_id, role, action)` via partial index w/ NULLS-distinct semantics like `prompt_overrides`, B-tree index on `(tenant_id, role, action)`, no RLS), add `DbSet` to `TenantDbContext`, then `dotnet ef migrations add ConventionStore --project src/Tamma.Data`. Seed system-default rows from 27-16 codegen via idempotent insert. Gate: `dotnet ef migrations list` shows it; `Database.Migrate()` applies cleanly on a Testcontainers Postgres; build+test green. **Solo on the snapshot** — no other agent edits EF model this wave.

### Wave 3 (solo) — after Wave 2 green

- **Agent → Story 27-9 (Convention Store Service, C#).** Story: `27-9-convention-store-service.md` **+ corrections C4, C5**. Create `IConventionRepository` (per-tenant via `ITenantDbContextFactory`, `RequireTenantId()`) + `ConventionStoreService` mirroring `PromptStoreService`: exact `(role, action)` lookup with tenant-override → system-default fallback. **Two distinctly-named resolve methods** (single-user `ResolveAsync(Guid? userId, role, action)` and SaaS `ResolveForTenantAsync(Guid tenantId, role, action)`) — no overloads. DI in a `ConventionStoreServiceCollectionExtensions.AddConventionStoreServices()`. Unit tests via `InMemoryDbFixture`; add a Testcontainers test for the CHECK/unique-index that InMemory can't exercise. Gate: build+test green. **This locks the method signatures Wave 4 depends on — I write Wave 4's detailed plan from this real code.**

### Wave 4 (27-10 first, then 27-13 ∥ 27-14) — after Wave 3 green

- **Agent → Story 27-10 (API Endpoints) FIRST.** Story: `27-10-convention-store-api-endpoints.md`. Create `ConventionStoreEndpoints.cs`; register in `Program.cs` under a `MapGroup("/api/conventions")` with a new `ConventionManage` named policy mirroring `PromptManage` (member→403 enforced by policy). Implement tenant CRUD + `/resolve` + `/defaults` + `/admin/...` + `/registry/...` per the story's 18 ACs, with rate limiting. Keep legacy `/api/convention-templates` untouched. Register the route-prefix order (`/defaults`,`/resolve`,`/registry` before `/:role/:action`). Gate: build+test green; endpoint tests cover the ACs. **Edits Program.cs → solo; 27-18 must not run this wave.**
- **Then parallel (2 worktrees):**
  - **Agent → Story 27-13 (Elsa Integration) + correction C2.** `ResolveConventionsActivity` calls the new `/api/conventions/resolve` endpoint over HTTP with `X-Tenant-Id`, mirroring `ResolvePromptFromRegistryActivity`. Demote `ReadRepoConventionsActivity` to fallback. Populates `{{conventions}}`. Gate: build+test green; activity tests.
  - **Agent → Story 27-14 (Event Sourcing).** `27-14-convention-store-event-sourcing.md`. Emit DCB events on convention create/update/delete. Disjoint from 27-13 (events vs activity). Gate: build+test green.

### Wave 5 (parallel, 2 worktrees) — after Wave 4 green

- **Agent → Story 27-11 (Admin UI).** `27-11-convention-store-admin-ui.md`. System-default management UI. Dashboard package.
- **Agent → Story 27-12 (Tenant UI).** `27-12-convention-store-tenant-ui.md`. Tenant override self-service UI. Note 27-12 lists 27-11 as a dep — if they share components, sequence 27-12 after 27-11; if not, parallelize. Gate: dashboard `typecheck` + tests; the new wiki-site-style typecheck gate already exists for dashboards via CI.

### Separate track — Story 27-18 (Prompt Store Taxonomy Reshape)

Story: `27-18-prompt-store-taxonomy-reshape.md`. Depends on 27-15, 27-16, 27-1. Reshapes prompt seeds/resolution onto the new enums. **Edits Program.cs / prompt code** → schedule in a wave where 27-10 is NOT running (e.g. between Wave 2 and Wave 3, or after Wave 5). Gate: build+test green; prompt regression tests pass.

---

## Risks & open decisions (resolve with user during review)

1. **AC#2 ↔ AC#8 tension (27-15):** expanding actions 10→70 while keeping the 4 consumers' behaviour identical. If SPEC §4 changes an existing eligibility answer, that's a design reconciliation, not a silent code change. **Highest-risk item; it's why Wave 0 is solo and gated hard.**
2. **`feat/wave-b` landing — confirmed appropriate.** `feat/wave-b` is the "Wave B" multi-epic integration branch (PR #343: *"SaaS-mode prompts + Epic 30 pluggable provisioning + Epic 31 git platforms"*, 112 commits, ~52k additions). It already contains Epic 27 work (stories 27-2, 27-3) plus Epics 30/31. Adding the convention store epic (27-8…27-19) is consistent with the branch's purpose — no separate-branch concern. (An earlier draft of this plan wrongly called this a "small security PR"; that was incorrect and is retracted.)
3. **Migration ordering on a shared DB:** 27-8's EF migration must slot after the latest existing migration; if other branches add migrations, snapshot rebases are needed. Keep 27-8 as the only migration in flight.
4. **`TammaError` shape:** Wave 0 tests assume `TammaError(code, message)` with a `.Code`. Verify the C# port's actual ctor/property names before Task 0.1 Step 3.
5. **SPEC §4 completeness:** the whole taxonomy hinges on SPEC §4 being the authoritative ~70-token list. If it's incomplete, 27-15/16/17 all stall. Validate SPEC §4 first thing in Wave 0.

---

## Self-review

- **Spec coverage:** Stories 27-8…27-19 each mapped to a wave + brief; 27-15 fully detailed. ✅
- **Corrections:** the three story-file factual errors (C1-C3) + two standing constraints (C4-C5) captured and bound into the relevant briefs. ✅
- **Placeholders:** Wave 0 steps contain real code; later waves are briefs (justified — their non-placeholder code needs not-yet-existing signatures), with detailed plans written just-in-time. SPEC §4 transcription is a real-source pointer, not a TODO. ✅
- **Type consistency:** `ToWire`/`Parse`/`AgentRole`/`AgentAction`/`AgentRoleExtensions` used consistently across Wave 0 tasks; `ResolveAsync`/`ResolveForTenantAsync` naming fixed for Wave 3→4 handoff. ✅
- **Conflict map:** Program.cs (27-10 vs 27-18) and EF snapshot (27-8 solo) serialization rules stated. ✅
```
