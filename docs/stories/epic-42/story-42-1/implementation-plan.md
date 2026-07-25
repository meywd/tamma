# Implementation Plan — Story 42-1: Tool Contract & Registry Evolution

## Reconciled scope — differs from the story file

**Epic 42 was reconciled against Epic 43 on 2026-07-25** (`docs/stories/epic-42/README.md`, the boxed
verdict table at the head of its Stories section; `docs/stories/epic-43/README.md`;
`.dev/decisions/epic-43-action-catalog-design.md`). 42-1's verdict is **"Rewritten"**. The story file below
this plan is pre-reconciliation text. The deltas, each traceable to a verdict line:

| Story file says | Reconciled |
|---|---|
| `ToolDescriptor(Category, PermissionClass, AutonomyFloor, RequiredSecret, Suspends)` | **`ToolDescriptor(RequiredSecret, Suspends)`.** `Category`, `PermissionClass` and `AutonomyFloor` are **dropped** — Epic 43's catalog owns all three (`ActionNamespace.Tool` keyed by `ToolAction` wire; one `MinAutonomy` integer per action per principal). |
| `enum ToolCategory`, `enum ToolPermissionClass` | **Not introduced.** They are Epic 43 vocabulary. |
| §1's **default interface member** returning the fail-safe `(Native, Destructive, 100, null, false)`, plus its three caveats (DIM invisible through a concrete reference; `Mock<IToolExecutor>` returns `null`; `Type.GetInterfaceMap` needed to distinguish declared-from-inherited) | **All dropped.** With `PermissionClass`/`AutonomyFloor` gone there is no governance tier left to fail safe *to* — the remaining fields are a nullable secret requirement and a `false` flag, so "deny-by-default" has no meaning. `Descriptor` becomes a **plain abstract interface member** (D2), which is compiler-enforced and deletes the entire DIM caveat surface along with story AC2, AC4 and most of AC8. |
| §2's annotation table (per-tool `PermissionClass` + `AutonomyFloor`) | Reduced to the two surviving fields. All seven tools declare `RequiredSecret = null`, `Suspends = false` — the six built-ins touch only the local repo/git/shell, and `get_acceptance_rules` reads in-process. The table becomes trivial; **that is the point** — the interesting per-tool governance moved to the catalog. |
| §4 — thread `Descriptor` onto `ResolvedTool` "so 42-3 can read `PermissionClass`/`AutonomyFloor`" | **42-3 is deleted**, so that consumer does not exist. Narrowed to D6: no descriptor is threaded onto `ResolvedTool`. The genuine defect §4 uncovered — `ToResolvedTools` advertises **name-only** tools — is handed to **Epic 43 Story 4** ("Tool-vocabulary reconciliation + startup validator"), which already owns the advertised-tool-set surface and correctly classifies fixing it as *a privilege expansion, not a cleanup*. |
| §5 / AC10 — delete or fix `ResolveToolsActivity` | **Handed to Epic 43 Story 0**, which states it "deletes a dead tool-resolution activity with zero callers (a third dead tool vocabulary)". Kept here only as a precondition to verify, not as work to do. |
| AC5 — `AutonomyFloor` validated to `[70,100]` at registration | **Dropped.** Epic 43 D3 is explicit that no story may hardcode the dial bound a second time, and names Story 42-1's `AutonomyFloor` as one of the two specs that would have. |
| Dependencies "Unblocks: 42-2 … 42-3 …" | 42-2 and 42-3 are **deleted**. Surviving dependents: 42-4 (`RequiredSecret`), 42-5, 42-6 Part B (`Register`/`Unregister`), 42-7/8A/8B/9 (`Suspends`). |

**Unchanged and still in scope:** §0's `SecretPurpose` relocation, `SecretRequirement`, `Suspends` with its
load-bearing "an executor cannot suspend a workflow" wording, and §3's dynamic `Register`/`Unregister` seam
with its platform-only constraint (AC7).

## Scope & Deliverable

When this story is done: (1) Epic 29's `SecretPurpose` is reachable from `Tamma.Activities` without a
circular project reference; (2) `IToolExecutor` carries a `ToolDescriptor Descriptor { get; }` abstract
member declaring `SecretRequirement?` and `Suspends`, and all seven implementations declare one; (3)
`IToolExecutorRegistry` gains a thread-safe `Register`/`Unregister` seam, platform/deployment-scoped only,
rejecting principal-scoped registration outright. Nothing in this story governs, gates or filters anything —
governance is Epic 43's. This is the contract 42-4 reads a secret requirement from, 42-6 Part B registers
through, and 42-7/8B declare a suspend on.

## Pre-Reading

- `docs/stories/epic-42/story-42-1/42-1-tool-contract-registry-evolution.md` — the story (**read the Reconciled scope table above first**; §0, §3 and the `Suspends` wording survive verbatim)
- `docs/stories/epic-42/README.md` — the reconciliation verdicts; "Where the code lives" (the assembly-siting rule); "The tool contract"
- `docs/stories/epic-43/README.md` — §1 the composite `ActionKey`, §3 "one integer per action", Enforcement Seam B, "The dial becomes one constant" (which names this story's `AutonomyFloor` as a would-be second hardcoding); `.dev/decisions/epic-43-action-catalog-design.md` D2/D3/S2
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/IToolExecutor.cs` — 39 lines total; interface at `:10`; four abstract members `:15`, `:20`, `:25`, `:34-37`; **no default interface members today**; the never-throw contract at `:8` and `:33`
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/IToolExecutorRegistry.cs:7-29` — `GetExecutor` `:12`, `IsAllowed` `:18`, `GetAll` `:23`, `GetAllowed` `:28`
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ToolExecutorRegistry.cs` — `public class` `:9`; `private readonly Dictionary<string, IToolExecutor> _executors` `:11`; ctor `:14-39` builds it once with `StringComparer.OrdinalIgnoreCase` `:19`, keep-first-and-warn on duplicates `:21-33`; `GetExecutor` `:42-53`, `IsAllowed` `:56-62`, `GetAll` `:65-66`, `GetAllowed` `:69-79`
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:753-764` (the six `AddSingleton<IToolExecutor,…>`), `:765-766` (`TryAddSingleton<IToolExecutorRegistry, ToolExecutorRegistry>`), `:422` (`AddScoped<GetAcceptanceRulesToolFactory>`), `:415-418` (the D6 rationale comment), `:808-809` (`IInlineToolLoopRunner` **Scoped**)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs:286-292` — the catalog was *removed* from the engine; "the tool executors are registered there, not here"
- The seven implementations: `FileReadTool.cs:15` (`file_read` `:20`), `FileWriteTool.cs:16` (`file_write` `:21`), `SearchCodeTool.cs:15` (`search_code` `:26`), `ShellExecuteTool.cs:14` (`shell_execute` `:20`), `GitOperationsTool.cs:13` (`git_operations` `:27`), `RunTestsTool.cs:13` (`run_tests` `:20`), `Tamma.Api/Services/AcceptanceRules/GetAcceptanceRulesTool.cs:27` (`get_acceptance_rules` `:51`, principal-bound ctor `:39-49`, doc `:15-18`, factory `:125-154`)
- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/SecretPurpose.cs` — namespace `Tamma.Api.Services.Secrets` `:1`, enum `:16`, seven members `:22`/`:29`/`:36`/`:43`/`:50`/`:57`/`:64`
- **`apps/tamma-elsa/src/Tamma.Core/Agents/AgentRole.cs:1-7` and `:7` `namespace Tamma.Api.Services.Agents;`** — together with `AgentAction.cs:7`, `RolePhaseMap.cs:9`, `EnumWire.cs:10`: **four files that physically live in `Tamma.Core` while declaring a `Tamma.Api.*` namespace.** This is the shipped precedent that makes D1's zero-churn option real
- `apps/tamma-elsa/src/Tamma.Data/Entities/SecretRow.cs:81-83` — `[Required][MaxLength(40)] public string Purpose { get; set; } = "generic";`
- `apps/tamma-elsa/src/Tamma.Api/Services/Agents/InlineToolLoopRunner.cs` — ctor `:45-67` (all ten params nullable, six defaulted); `_toolRegistry.GetExecutor` `:431`; the parallel/sequential fork `:335`; the two independently-sourced allowlists (`tools`-derived `:262-263`, `loopConfig.AllowedTools` `:342`/`:419`)
- `apps/tamma-elsa/src/Tamma.Api/Services/Agents/ManagedAgent.cs:923-937` — `ToResolvedTools`; `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs:287-297` (`ResolvedTool`, a mutable class), `:479-501` (`ToolLoopConfig`, **`EnableParallelTools` default `false` at `:500`**)
- `apps/tamma-elsa/src/Tamma.Activities.Guardrails/Allowlist.cs:16-17` (`IsEngineSurface`), `:45-64` (the 13-entry `InjectionDenylist`, `IProviderCredentialResolver` at `:59`), `:57-58` (the `InlineToolLoopRunner`-is-Api-side note)
- Project references — `Tamma.Core.csproj`: **zero** `ProjectReference`; `Tamma.Activities.csproj:36-37` (Core + Data) `:44-46` (the analyzer); `Tamma.Api.csproj:78` (→ Activities)

## Corrections to the story

- **X1 — `LlmCallModels.cs` L534 is wrong; `EnableParallelTools` defaults `false` at `:500`.** §4 cites
  "`LlmCallModels.cs` L534". The record is `:479-501` and the member is `:500`. The *claim* (sequential is
  the default path) is correct and load-bearing for 42-5/42-7/42-8A/42-8B/42-9.
- **X2 — `ToResolvedTools` is `:923-937`, not `:923-936`.** Trivial, but the method's real content matters
  more (D6): it returns `names.Select(n => new ResolvedTool { Name = n })`, leaving `Description` at `""` and
  `InputSchema` at `null`, so **the six built-in tools are advertised to the model by name alone**. The story
  treats this as a place to add a descriptor; it is first of all a latent defect, and it belongs to Epic 43
  Story 4.
- **X3 — `SecretRow.Purpose` defaults to `"generic"`, which is not a `SecretPurpose` member.** §0 says the
  move is data-safe because `Purpose` is a `string` column — verified (`SecretRow.cs:81-83`, `MaxLength(40)`,
  no fluent config, no CHECK in `SecretsDbContext.cs:62-90`). But the column default is `"generic"` while the
  enum's catch-all is `Other`. Any code that parses the column must tolerate `"generic"`. This does not block
  the move; it means "no schema or data change" is true and "every stored value round-trips through the enum"
  is **not**.
- **X4 — `ToolExecutorRegistry` is thread-safe today only by immutability.** The dictionary is built once in
  the ctor and never mutated, so concurrent reads are safe with no locks and no `ConcurrentDictionary`. §3's
  `Register`/`Unregister` **introduces** the concurrency problem; it does not merely need a faster map. D4
  owns that story.
- **X5 — `ResolveToolsActivity` is dead, but it *is* type-registered with Elsa.**
  `Tamma.ElsaServer/Program.cs:115` `AddActivitiesFrom<ClaudeAnalysisActivity>()` scans the whole
  `Tamma.Activities` assembly, so the activity appears in the Studio designer catalog even with zero
  instantiations. Deletion is still safe — the only workflow JSON in the tree
  (`apps/tamma-elsa/workflows/autonomous-mentorship.json`) does not reference it — but the deleter must also
  fix `GetAcceptanceRulesTool.cs:16`, whose doc comment names it. Recorded for **Epic 43 Story 0**, which now
  owns the deletion.
- **X6 — AC8's three mock fixtures are cited by line and unverified.** `ToolExecutorRegistryTests.cs:23`,
  `InlineToolLoopRunnerTests.cs:191`, `AgenticToolLoopIntegrationTests.cs:316`. Re-derive at implementation;
  do not trust the numbers. The *hazard* survives D2 in reduced form (a `Mock<IToolExecutor>` with no
  `Descriptor` setup returns `null` for an abstract property just as it did for a DIM), but it is no longer
  dangerous — a null descriptor now means "no secret, does not suspend", which is exactly right.

## Design Decisions

- **D1 — Relocate `SecretPurpose` by MOVING THE FILE and KEEPING THE NAMESPACE, following the four-file
  in-repo precedent.** The story's §0 proposes `Tamma.Core/Enums/SecretPurpose.cs` with
  `namespace Tamma.Core.Enums` plus a `using` update in ~16 `Tamma.Api` files and their tests. There is a
  cheaper option the story did not consider because it is unusual: **`Tamma.Core/Agents/AgentRole.cs`,
  `AgentAction.cs`, `RolePhaseMap.cs` and `EnumWire.cs` all live in `Tamma.Core` and declare
  `namespace Tamma.Api.Services.Agents`** — with a NOTE at `AgentRole.cs:1-7` recording that the namespace is
  kept deliberately so consumers do not churn. Applying it here: move the file to
  `Tamma.Core/Enums/SecretPurpose.cs`, keep `namespace Tamma.Api.Services.Secrets;`. Result: `Tamma.Activities`
  can `using Tamma.Api.Services.Secrets;` (now satisfied by the Core assembly), **zero** call-site edits,
  **zero** test edits, and a diff of one moved file. The cost is a namespace that no longer matches its
  assembly — a cost this repo has already accepted four times and documented. **Recommendation: take the
  precedent.** If the reviewer prefers correctness over churn, the story's `Tamma.Core.Enums` variant is the
  fallback and the only difference is ~16 mechanical `using` lines; either way the enum's seven members and
  their order are untouched and pinned.
- **D2 — `Descriptor` is a plain abstract interface member, not a DIM.** Per the Reconciled scope. With only
  `RequiredSecret` and `Suspends` left there is no fail-safe value worth defaulting to, and an abstract member
  makes "every tool declares" a **compile error** rather than a reflection test. This deletes: the DIM, its
  three caveats, story AC2 (the bare-implementer fallback test), story AC4 (the `Type.GetInterfaceMap` drift
  test — the compiler is the drift test), and AC8's danger (a null descriptor from a mock now means "no
  secret, no suspend", which is the correct reading, not a bypass). Consumers still coalesce `null` to
  `new ToolDescriptor(null, false)` at the read site, and one test pins that.

  ```csharp
  // namespace Tamma.Activities.LlmCall.Tools
  using Tamma.Api.Services.Secrets;   // SecretPurpose, after D1

  public sealed record SecretRequirement(SecretPurpose Purpose, string Name, bool Required);

  public sealed record ToolDescriptor(SecretRequirement? RequiredSecret, bool Suspends);

  public interface IToolExecutor
  {
      // ... the four existing members, unchanged ...
      ToolDescriptor Descriptor { get; }
  }
  ```

- **D3 — `Suspends`'s XML doc is the story's wording verbatim, because two Wave-3 stories depend on the exact
  reading.** *"`Suspends = true` declares that this tool's completion is owned by an engine-side wait — it is
  not a capability the executor exercises."* Verified rationale: the tool loop runs inside a blocking
  `POST /api/v1/llm/call` in `Tamma.Api`; there is no `ActivityExecutionContext` there, and `TammaEventEmitter`
  — the only in-engine emit path — structurally requires both an `ActivityExecutionContext` and an `IActivity`
  (`Tamma.Activities/Core/TammaActivity.cs:82-147`) and writes only to
  `TransientProperties["tamma:events"]`, never to the store. An implementer who reads `Suspends` as an
  executor capability writes an AC that cannot be satisfied (42-7 §4 and 42-8B §6 both record this).
- **D4 — the dynamic seam is a `ConcurrentDictionary` plus an explicit two-layer model, and the concurrency
  story is written down.** Today's registry is safe **only because it never mutates** (X4). `Register`/
  `Unregister` breaks that, so: swap `Dictionary` → `ConcurrentDictionary<string, IToolExecutor>` (same
  `OrdinalIgnoreCase` comparer, `:19`); keep the DI-seeded set as an immutable base captured in the ctor and
  layer dynamic entries over it, so `Unregister` can never remove a built-in (an explicit, tested refusal —
  otherwise an MCP refresh bug could delete `file_read` platform-wide). `GetAll`/`GetAllowed` already snapshot
  via `.ToList()` (`:65-66`), so readers see a consistent view without locking. Duplicate handling: the DI
  seed keeps today's keep-first-and-warn (`:21-33`); a dynamic `Register` of an existing name **rejects by
  default** and the sanctioned replace is `Unregister` + `Register` (42-6 §5's refresh path).
- **D5 — principal-scoped registration is rejected, loudly, with the reason in the error.** The registry is a
  singleton (`Program.cs:765-766`), so a per-user/per-tenant/per-run tool registered into it leaks to every
  principal. `Register` therefore takes no principal argument and a typed failure names 42-6 Part B's
  per-principal registry *view* as the sanctioned path. Until that lands, `GetAcceptanceRulesToolFactory`'s
  construct-per-principal-never-register pattern (`GetAcceptanceRulesTool.cs:125-154`) remains the only way
  to mount a principal-bound tool — and it still has **no production call site**, because
  `InlineToolLoopRunner`'s ctor (`:45-67`) accepts no ad-hoc executor collection and resolves solely through
  the registry (`:431`). This story does not fix that; it records it as 42-6 Part B's.
- **D6 — nothing is threaded onto `ResolvedTool`, and the advertised-tool-set defect is handed off.** With
  42-3 deleted there is no consumer for a descriptor on `ResolvedTool`, and Epic 43's Seam B gates on an
  `ActionKey` derived from the tool **name**, not from a descriptor. Meanwhile `ToResolvedTools` genuinely
  advertises name-only tools (X2) — a real defect, but one that sits on the same surface Epic 43 Story 4 owns
  ("three tool vocabularies disagree… reconciling them is a privilege expansion, not a cleanup"). Fixing it
  here in isolation would change what the model sees without the catalog that decides what it may call.
  **Filed to Epic 43 Story 4; explicitly out of scope here.** Story AC9's back-compat half survives as D7.
- **D7 — the visible tool set does not change, on either branch.** An unrestricted caller still sees all six
  built-ins; `IsAllowed`'s null/empty-allowlist behaviour (`:56-62`) is untouched; both the sequential
  (default) and parallel branches are asserted, because `EnableParallelTools` defaults `false` (`:500`) and a
  change tested only on the opt-in branch proves nothing about the path every run takes.
- **D8 — assembly siting is unchanged and non-negotiable.** `ToolDescriptor`, `SecretRequirement`,
  `IToolExecutor`, `IToolExecutorRegistry`, `ToolExecutorRegistry` stay in
  `Tamma.Activities.LlmCall.Tools`. A `ToolDescriptor` **never** carries a `SecretRef` — `SecretRef` /
  `SecretScope` / `ISecretStore` stay Api-side (42-4). `Tamma.Activities → Tamma.Api` is circular
  (`Tamma.Api.csproj:78`) and would be a hard `CS0246`, not a suppressible diagnostic.

## Implementation Steps

1. **MOVE `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/SecretPurpose.cs` →
   `apps/tamma-elsa/src/Tamma.Core/Enums/SecretPurpose.cs`**, namespace unchanged (D1). Add the same
   NOTE header `AgentRole.cs:1-7` carries, pointing at the precedent so the next reader is not surprised.
   Confirm `Tamma.Api` still builds with no `using` edits and `Tamma.Activities.csproj` still references only
   `Tamma.Core` + `Tamma.Data`.
2. **CREATE `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ToolDescriptor.cs`** — `SecretRequirement`
   and `ToolDescriptor` per D2, with D3's `Suspends` doc comment.
3. **MODIFY `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/IToolExecutor.cs`** — add the abstract
   `ToolDescriptor Descriptor { get; }` beside the four existing members (D2).
4. **MODIFY the seven implementations** — `FileReadTool`, `FileWriteTool`, `SearchCodeTool`,
   `ShellExecuteTool`, `GitOperationsTool`, `RunTestsTool`, `GetAcceptanceRulesTool` — each declaring
   `public ToolDescriptor Descriptor => new(null, false);`. **MODIFY the test fakes** the compiler names
   (X6: re-derive the list from the build, do not trust the story's three line cites).
5. **MODIFY `IToolExecutorRegistry.cs` + `ToolExecutorRegistry.cs`** — D4/D5: `Register(IToolExecutor)` /
   `Unregister(string toolName)`, `ConcurrentDictionary`, the immutable DI-seed base layer, built-in
   protection, reject-on-duplicate, and the typed principal-scoped refusal.
6. **CREATE the test suites** (Test Plan).
7. **Finish:** full `dotnet test`; `dotnet ef migrations has-pending-model-changes` clean (D1 touches no
   schema — `SecretRow.Purpose` is a `string` column, X3); confirm `Tamma.Core.csproj` still has zero
   `ProjectReference` entries.

## Data & Migrations

None. `SecretRow.Purpose` is `[Required][MaxLength(40)] string` (`SecretRow.cs:81-83`) with no fluent
config, no CHECK and no enum conversion (`SecretsDbContext.cs:62-90`), so moving the enum's file changes
nothing at rest. Note X3: the column default `"generic"` has no enum member.

## Events

None new. `Register`/`Unregister` log at INFO with the tool name (never a secret name's value; the logical
`SecretRequirement.Name` is a slug, not a credential, and is safe to log). The `TOOL.*` DCB family is 42-5's.

## Test Plan

- **`SecretPurposeRelocationTests`** — `Enum.GetValues<SecretPurpose>().Length == 7` and the member names in
  order; `typeof(SecretPurpose).Assembly.GetName().Name == "Tamma.Core"` (the move actually happened);
  a `Tamma.Activities`-resident test type names `SecretPurpose` and compiles (the reachability proof — this
  test *is* the acceptance criterion). **Covers AC1.**
- **`ToolDescriptorContractTests`** — all seven implementations, read through an `IToolExecutor`-typed
  reference, return `RequiredSecret == null` and `Suspends == false`; a consumer helper coalescing a `null`
  descriptor yields `(null, false)`. Compilation itself proves "every implementer declares" (D2), so no
  reflection drift test is written — and a comment says why, so nobody re-adds one.
- **`ToolExecutorRegistryDynamicTests`** (D4/D5) — register at runtime → resolvable via `GetExecutor`/`GetAll`
  → `Unregister` → gone, no restart; duplicate `Register` rejects; `Unregister`+`Register` succeeds;
  **`Unregister` of a DI-seeded built-in is refused** with a typed error; a principal-scoped registration
  attempt fails loudly naming 42-6 Part B; a stress test hammers `Register`/`Unregister`/`GetAll`
  concurrently and asserts no lost, duplicated or torn entries and no exception from a reader. **Covers AC6,
  AC7.**
- **`ToolCatalogBackCompatTests`** (D7) — with an empty allowlist an unrestricted caller still resolves all
  six built-ins through `InlineToolLoopRunner`, asserted once with `EnableParallelTools = false` (the
  default) and once `true`. **Covers the surviving half of AC9.**

## Definition of Done

| AC (reconciled) | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — `SecretPurpose` reachable from `Tamma.Activities`; 7 members, same order; no new project reference | 1 (D1) | `SecretPurposeRelocationTests`; `Tamma.Activities.csproj` diff shows no added reference |
| 2 — `Descriptor` on the contract; all seven declare | 2, 3, 4 (D2) | Compilation + `ToolDescriptorContractTests` |
| 3 — `Suspends` documented as an engine-side-wait declaration | 2 (D3) | Reviewer check against 42-7 §4 / 42-8B §6 |
| 4 — `Register`/`Unregister` exist, race-free, built-ins protected | 5 (D4) | `ToolExecutorRegistryDynamicTests` incl. the stress case |
| 5 — principal-scoped registration rejected | 5 (D5) | `ToolExecutorRegistryDynamicTests` |
| 6 — no change to the visible tool set, both branches | 5 (D7) | `ToolCatalogBackCompatTests` |
| ~~autonomy floor / permission class / category / DIM default / drift test / `ResolveToolsActivity`~~ | — | **Out of scope — see Reconciled scope** |

## Blocks / Blocked by

- **Blocked by — nothing in Epic 42.** This is Wave 0.
- **Coordinate with — Epic 43 Story 0** (owns deleting `ResolveToolsActivity`, X5) and **Epic 43 Story 4**
  (owns the advertised-tool-set reconciliation, D6). Neither blocks this story; both would conflict if 42-1
  did their work. **Epic 43 Story 1** owns `AutonomyDial`; this story must not reintroduce a `[70,100]`
  literal anywhere (Epic 43 D3 names 42-1's `AutonomyFloor` as one of the two specs that would have).
- **Blocks — 42-4** (`SecretRequirement` + the relocated `SecretPurpose`; without them the descriptor cannot
  name a purpose at all), **42-6 Part B** (`Register`/`Unregister`; also inherits D5's platform-only
  constraint and owns the per-principal view that lifts it), **42-7 / 42-8A / 42-8B / 42-9** (each declares a
  `ToolDescriptor`; 42-7 and 42-8B declare `Suspends = true`).
- **Does not block — 42-6 Part A**, which touches only route mapping, `KbEndpoints`,
  `IIntelligenceHttpClient` and the dashboard, and can land before this story.
- **Does not block — 42-5**, which needs neither `PermissionClass` (dropped) nor `RequiredSecret` for its
  invocation trio; its former `42-1` dependency was for the permission-class tag, which no longer exists.

## Risks & Mitigations

- **The namespace-preserving move (D1) reads as a mistake to a future reader.** Mitigation: the NOTE header
  copied from `AgentRole.cs:1-7`, plus the assembly assertion in `SecretPurposeRelocationTests` so the
  physical location is pinned even though the namespace does not reveal it. The fallback (`Tamma.Core.Enums`
  + ~16 `using` edits) is a one-commit change if the reviewer prefers it.
- **Making `Descriptor` abstract is a breaking interface change.** Every implementer and test fake must be
  edited in the same commit. Mitigation: all of them are in-repo (seven production classes plus the fakes the
  compiler names), each edit is one line, and the breakage is a **compile error** — the safest possible
  failure mode, and strictly better than the DIM's silent-null hazard the story spent AC2/AC4/AC8 defending
  against.
- **The dynamic seam is the first mutation of a hot singleton (X4/D4).** A torn read here degrades every
  agent run. Mitigation: `ConcurrentDictionary` + snapshot reads + the concurrency stress test + built-in
  protection. Note the seam has **no consumer until 42-6 Part B**, so it ships dark and is exercised only by
  tests — deliberate, and the reason its risk is acceptable in Wave 0.
- **A future contributor re-adds `AutonomyFloor`/`PermissionClass` to `ToolDescriptor`** because the story
  file still describes them. Mitigation: this plan's Reconciled scope table is the first thing in the file;
  Epic 43 Story 10 applies the reconciliation to the story text itself; and the `ToolDescriptor` XML doc names
  Epic 43 as the owner of governance so the record travels with the code.
- **Story-vs-canon tensions:** none remaining — the reconciliation resolved them by deletion.

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1 | `SecretPurpose` move (D1 precedent path) + relocation tests | 0.25 |
| 2–3 | `ToolDescriptor` / `SecretRequirement` / interface member | 0.25 |
| 4 | Seven implementations + test-fake fallout | 0.5 |
| 5 | Registry dynamic seam (concurrent map, layering, built-in protection, refusals) | 0.75 |
| 6 | Test suites incl. the concurrency stress and both-branch back-compat | 0.75 |
| 7 | Full green, Epic 43 Story 0/4 hand-off notes | 0.25 |
| **Total** | | **2.75** |

Story estimate: ~4–5 days. **Reconciliation removed roughly two days** — the descriptor lost three of five
fields, the DIM and its `Type.GetInterfaceMap` drift test are gone (the compiler does it), the `[70,100]`
validation is gone, and both the `ResolveToolsActivity` cleanup and the `ResolvedTool` threading moved to
Epic 43. If the reviewer rejects D1's precedent path, add ~0.25 d for the ~16 `using` edits.
