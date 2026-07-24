# Story 42-1: Tool Contract & Registry Evolution

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then check the knowledge base: `.dev/spikes/`, `.dev/bugs/`, `.dev/findings/`, `.dev/decisions/`.

## User Story

As the **platform**, I want every tool to **declare its governance** — category, permission class,
autonomy floor, required secret, and whether it suspends — and I want the registry to accept **dynamic
registration**, so that a tool carries the metadata needed to gate, secure, and audit it, and the
catalog can grow per deployment and via MCP without a code change per tool.

## Priority

P0 / Wave 0 — **the contract every other Epic 42 story builds on.** No gating (42-3), secret binding
(42-4), audit (42-5), MCP path (42-6), or tool family (42-7/8/9) can exist until a tool can *declare*
what it is and the registry can hold tools added at runtime.

## The gap (READ FIRST)

`IToolExecutor` (`Tamma.Activities/LlmCall/Tools/IToolExecutor.cs`, namespace
`Tamma.Activities.LlmCall.Tools`) is `{ ToolName, Description, InputSchema, ExecuteAsync }` and **must
never throw** (all failure is `ToolExecutionResult { Success = false }`). It carries **no governance
metadata** — nothing says who may call the tool, at what autonomy, what credential it needs, or how
destructive it is.

`ToolExecutorRegistry` (`ToolExecutorRegistry.cs` L14–39) is populated **once** in its constructor from
`IEnumerable<IToolExecutor>` injected by DI. `IToolExecutorRegistry` (L7–29) exposes only read paths —
`GetExecutor`, `IsAllowed`, `GetAll`, `GetAllowed`. There is **no** `Register`/`Unregister`: the catalog
is frozen at startup, and the backing store is a plain `Dictionary` (L11, L19).

**Corrected — the DI citation.** Earlier drafts cited `Tamma.Api/Program.cs` "~L753–763". The six
`AddSingleton<IToolExecutor, …>` calls are at **L753–764** (FileRead, FileWrite, SearchCode,
ShellExecute, GitOperations, RunTests); the registry `TryAddSingleton<IToolExecutorRegistry,
ToolExecutorRegistry>` is at **L765–766**. Registration lives in `Tamma.Api`, not the engine —
`Tamma.ElsaServer/Program.cs` L286–289 records that the tool catalog was removed from the engine
(Story 32-5 AC9) and "the tool executors are registered there, not here."

**Prior art you must not undo — the principal-bound tool (Story 39-5, D6).** A **seventh**
`IToolExecutor` implementation already exists outside the DI set:
`Tamma.Api/Services/AcceptanceRules/GetAcceptanceRulesTool.cs` L27 (`get_acceptance_rules`, L51). Its
own doc (L12–23) states it is *"PRINCIPAL-BOUND AT CONSTRUCTION and NOT globally DI-registered"* — the
`GetAcceptanceRulesToolFactory` (same file, L125–154; `AddScoped` at `Program.cs` L422) mints one per
principal and enforces `userId` XOR `tenantId` (L142–146). This is the in-repo answer to *"never mutate
the global set for a principal-scoped tool."*

It is also why the dynamic seam in §3 is still needed rather than superseded: the factory has **no
production call site** (only DI of the factory plus tests), and the only live tool loop resolves
executors solely through the registry — `InlineToolLoopRunner.cs` L431 `_toolRegistry.GetExecutor(...)`,
with a ctor (L45–55) that accepts no ad-hoc executor collection. The D6 pattern today has no delivery
path into a run.

**Where the types live (dependency direction).** `Tamma.Core ← Tamma.Activities ← Tamma.Api`, one-way.
`Tamma.Core.csproj` has **zero** `ProjectReference` entries (a leaf); `Tamma.Activities.csproj`
references only `Tamma.Core` + `Tamma.Data`; `Tamma.Api.csproj` references `Tamma.Activities` (L78).
Anything the `IToolExecutor` surface names must therefore live in `Tamma.Core`, `Tamma.Data`, or
`Tamma.Activities` — **never** in `Tamma.Api`. See Scope §0.

## Scope

0. **Relocate `SecretPurpose` to `Tamma.Core` (prerequisite for §1).** The Epic 29 taxonomy currently
   lives at `Tamma.Api/Services/Secrets/SecretPurpose.cs`, namespace `Tamma.Api.Services.Secrets`
   (7 members: `DbCredential`, `ApiKey`, `SigningKey`, `HmacSharedSecret`, `Webhook`, `Connection`,
   `Other`). It is **unreachable** from `Tamma.Activities`, and adding a `Tamma.Activities → Tamma.Api`
   reference is circular (see the dependency-direction note above) — a hard `CS0246`, not a suppressible
   diagnostic. Move the enum verbatim to `Tamma.Core/Enums/SecretPurpose.cs`
   (namespace `Tamma.Core.Enums`) and update the `using` in the ~16 `Tamma.Api` source files + tests
   that name it.

   *Decision — move, not mirror.* The alternative (a Core-owned `ToolSecretPurpose` mapped onto Epic
   29's in the Api layer) was rejected: it creates two taxonomies that must be kept in sync, and the
   mapping table is exactly the kind of drift the descriptor is meant to remove. The move is safe:
   `SecretRow.Purpose` is a `string` column (`Tamma.Data/Entities/SecretRow.cs` L83), so **no schema or
   data change**, and no member is added, removed, or reordered.

1. **`ToolDescriptor` on the executor surface.** Introduce `ToolDescriptor` in
   `namespace Tamma.Activities.LlmCall.Tools` (alongside `IToolExecutor`) and surface it via a new
   `ToolDescriptor Descriptor { get; }` member declared as a C# **default interface member** returning
   the fail-safe default.

   ```csharp
   // namespace Tamma.Activities.LlmCall.Tools
   using Tamma.Core.Enums;            // SecretPurpose, after Scope §0

   public enum ToolCategory { Native, Mcp, ProviderAbstracted }
   public enum ToolPermissionClass { ReadOnly, Mutating, Command, Destructive }

   public sealed record SecretRequirement(
       SecretPurpose Purpose,   // Epic 29 taxonomy, relocated to Tamma.Core by §0
       string Name,             // logical secret name, resolved to a SecretRef per mode by 42-4
       bool Required);          // hard-fail vs. best-effort

   public sealed record ToolDescriptor(
       ToolCategory Category,
       ToolPermissionClass PermissionClass,  // the family MAXIMUM over its operations
       int AutonomyFloor,                    // 70–100
       SecretRequirement? RequiredSecret,
       bool Suspends);                       // completion is owned by an engine-side wait
   ```

   **`Suspends` does not mean "the executor suspends the workflow" — it cannot.** *This wording is
   load-bearing; 42-7 §4 and 42-8B §6 both depend on it.* The tool loop runs server-side inside a
   **blocking** `POST /api/v1/llm/call` in `Tamma.Api` (`CallLlmInlineActivity` is a thin client over
   `TammaApiClient`), where there is no `ActivityExecutionContext` and no bookmark to create. So
   `Suspends = true` is a **declaration that this tool's completion is owned by an engine-side wait**:
   the executor returns promptly with an `operationHandle`, and the engine-side
   `WaitForToolOperationActivity` (credential-free, resumes on a callback or a durable timeout)
   carries the suspension. Put this on the property's XML doc — an implementer who reads `Suspends`
   as an executor capability will write an AC that cannot be satisfied.

   The **fail-safe default** (returned by the default interface member) is
   `new ToolDescriptor(Native, Destructive, 100, null, false)` — **deny-by-default**: an un-annotated
   tool is treated as the most dangerous, never silently granted. `AutonomyFloor` is validated to
   `[70,100]` at registration, matching `AcceptanceRules.AutonomyLevel`'s existing range check
   (`AcceptanceRules.cs` L85–86).

   **`PermissionClass` is the family MAXIMUM over the tool's operations — define it that way here.**
   A tool that exposes several verbs (42-7's `cloud_resource_write`, 42-8A's `feature_flag_write`,
   42-8B's `deploy_control`, 42-9's `http_request`) declares the most dangerous class any one of them
   can reach. The *per-call* class comes from 42-3's `ToolInvocationFacts Describe(argumentsJson)`
   seam. This matters beyond documentation: a consumer that treats the descriptor class as the
   per-call class and excludes `Destructive` tools from the eligible set would make every Wave-3 write
   tool unreachable — 42-3 Scope 1 / AC1b pin the correct reading (stage 1 keys on the
   binding-resolved effective ceiling; `Destructive` is a stage-2 discriminator). Say so on the type's
   XML doc so the semantics travel with the code, not just with this story.

   **Why a default interface member (verified sound, with three caveats).** Every implementer is a
   plain class implementing the interface directly — `FileReadTool` L15, `FileWriteTool` L16,
   `SearchCodeTool` L15, `GitOperationsTool` L13, `ShellExecuteTool` L14, `RunTestsTool` L13,
   `GetAcceptanceRulesTool` L27, plus five test fakes in `ParallelToolExecutorTests.cs`. No
   explicit-interface implementation, no abstract base, no struct/record implementer, and
   `LangVersion=latest` on `net8.0` supports DIMs — so nothing breaks. The caveats the implementation
   must handle: (a) a DIM is **not** invocable through a concrete-typed reference, so every read (and
   every test assertion) must go through an `IToolExecutor`-typed variable; (b) `Mock<IToolExecutor>`
   proxies implement the DIM and return `default` — i.e. a **null** descriptor, not the deny-by-default
   one — so the fail-safe cannot be asserted against a mock (see Risks); (c) distinguishing "declared a
   descriptor" from "inherited the DIM" needs `Type.GetInterfaceMap` reflection, which the drift test
   in §2 must actually do.

2. **Annotate the built-ins.** Each of the six DI-registered tools declares a real descriptor:

   | Tool | PermissionClass | AutonomyFloor | RequiredSecret |
   |---|---|---|---|
   | `SearchCodeTool` (`search_code`) | `ReadOnly` | 70 | none |
   | `FileReadTool` (`file_read`) | `ReadOnly` | 70 | none |
   | `FileWriteTool` (`file_write`) | `Mutating` | 70 | none |
   | `GitOperationsTool` (`git_operations`) | `Mutating` | 75 | none (platform creds resolved elsewhere today) |
   | `RunTestsTool` (`run_tests`) | `Mutating` | 70 | none |
   | `ShellExecuteTool` (`shell_execute`) | `Command` | 85 | none (stays `ActionGate`-gated) |

   **Plus the seventh.** `GetAcceptanceRulesTool` (`get_acceptance_rules`) also declares one —
   `ReadOnly`, floor 70, no secret. It is a read of the effective acceptance rules; leaving it to
   inherit `Destructive`/floor 100 would arm a latent trap for the 39-17 host that eventually mounts it.
   Annotating it is a two-line change and costs nothing. It stays **out of the DI-registered startup
   drift test** (AC4) because by D6 design it is never DI-registered — an implementer must not widen
   that test to "all `IToolExecutor` types."

   These floors are defaults, overridable per principal by the 42-2 binding store.

3. **Dynamic registration seam — platform-scoped in this story.** Extend `IToolExecutorRegistry` with
   `Register(IToolExecutor)` / `Unregister(string toolName)` and make `ToolExecutorRegistry`'s backing
   map thread-safe (`ConcurrentDictionary`, case-insensitive as today). The DI-seeded set remains the
   base layer; dynamic registrations layer on top. Duplicate-name handling keeps today's "keep first,
   warn" for the DI seed, but a dynamic `Register` of an existing name is an explicit
   **replace-or-reject** decision (default reject; MCP refresh uses `Unregister`+`Register`). Registry
   read paths (`GetAll`/`GetAllowed`) see the merged set.

   **The singleton is platform/deployment scope only.** `ToolExecutorRegistry` is a singleton, so a
   principal-scoped (per-user / per-tenant / per-run) tool registered into it leaks to every other
   principal. This story therefore ships `Register`/`Unregister` for **deployment-wide** tools only and
   **rejects a principal-bound registration outright** (see AC7) until 42-6 lands the per-principal
   registry *view*. Until then the D6 factory pattern — construct per principal, never register — is the
   only sanctioned way to mount a principal-bound tool. The 42-6 view is what finally gives that pattern
   a delivery path into `InlineToolLoopRunner`.

4. **Descriptor reaches the tool set the LLM actually sees.**

   **Corrected — `ResolveToolsActivity` is not that place.** Earlier drafts said "`ResolveToolsActivity`
   / `ResolvedTool` already builds the tools array the LLM sees." What that activity actually does:
   `Tamma.Activities/LlmCall/ResolveToolsActivity.cs` is a `CodeActivity<List<ResolvedTool>>` injecting
   only `ILogger` + `IConfiguration` (L26–27, ctor L42–48). Per requested name it reads
   `LlmTools:{provider}:{tool}` (L115) then `LlmTools:{tool}` (L122) from configuration, else falls back
   to a hard-coded three-case switch — `search_code`, `read_file`, `run_tests` (L161–225). It **never
   touches `IToolExecutorRegistry` or `IToolExecutor`** (they are not even in its usings), one of its
   three built-ins (`read_file`) matches no registered tool name (`FileReadTool.ToolName` is
   `file_read`), and the activity is **referenced nowhere** in `src/`, `tests/`, or `workflows/` — the
   only other mention in the tree is a doc comment. It is dead code.

   The live path is: `ManagedAgent.ToResolvedTools` (`ManagedAgent.cs` L923–936) builds
   `new ResolvedTool { Name = n }` from bare names and passes it to `InlineToolLoopRunner.RunAsync`
   (call site L328); the runner derives the validator allowlist from that list (`InlineToolLoopRunner.cs`
   L262) and resolves executors via `_toolRegistry.GetExecutor` (L431, sequential) or via
   `ParallelToolExecutor` (parallel branch, gated at L335 — note `ToolLoopConfig.EnableParallelTools`
   defaults to **false** at `LlmCallModels.cs` L534, so **sequential is the default path**).

   This story therefore threads the descriptor onto `ResolvedTool` where that list is *really* built —
   `ToResolvedTools` populates `Description`/`InputSchema`/`Descriptor` from the registry instead of a
   bare name — so 42-3 can read `PermissionClass`/`AutonomyFloor` at that point. This story only
   *plumbs* it; 42-3 acts on it. No behavior change to the six built-ins' availability.

5. **Dispose of the dead activity (prerequisite, not optional).** Either delete `ResolveToolsActivity`
   or fix its `"read_file"` built-in to `"file_read"`. Leaving an unreferenced resolver whose built-in
   names disagree with the registry guarantees a future implementer wires the wrong surface (42-3's
   earlier draft did exactly that). Deletion is preferred; if it is kept, it must resolve names through
   `IToolExecutorRegistry` rather than its own switch.

## Acceptance Criteria

1. `SecretPurpose` compiles at `Tamma.Core.Enums.SecretPurpose` with the same **7** members in the same
   order (a test pins `Enum.GetValues<SecretPurpose>().Length == 7` and the member names); `Tamma.Api`
   builds with no `Tamma.Api.Services.Secrets.SecretPurpose` left in the tree; `Tamma.Activities.csproj`
   still references **only** `Tamma.Core` + `Tamma.Data` (no `Tamma.Api` reference is added).
2. `IToolExecutor.Descriptor` exists as a default interface member returning
   `ToolDescriptor(Native, Destructive, 100, null, false)`; a regression test declares a bare
   `IToolExecutor` implementing only the four original members, assigns it to an **`IToolExecutor`-typed
   variable**, and asserts the descriptor read through that variable is `Destructive`/floor `100`.
   (The test must use a real class, **not** `Mock<IToolExecutor>` — see AC8.)
3. All six DI-registered built-ins **and** `GetAcceptanceRulesTool` declare explicit descriptors matching
   the §2 table; a test asserts each tool's `PermissionClass`/`AutonomyFloor`.
4. A startup drift test enumerates the DI-registered `IToolExecutor` set and asserts each type
   **overrides** `Descriptor` (via `Type.GetInterfaceMap`, not by comparing values — a tool that
   coincidentally declares `Destructive`/100 must still count as declared), failing the boot loudly,
   matching `PromptFileLoader`'s "refuse to start on a missing cell" posture. The test is scoped to the
   DI-registered set; a test asserts it does **not** trip on `GetAcceptanceRulesTool`.
5. `AutonomyFloor` outside `[70,100]` is rejected at registration with a loud error (not clamped).
6. `IToolExecutorRegistry.Register`/`Unregister` exist; a test registers a tool at runtime, resolves it
   via `GetExecutor`/`GetAll`, unregisters it, and asserts it is gone — with no restart. Concurrent
   `Register`/`GetAll` calls are race-free; a stress test asserts no lost/duplicated entries. A dynamic
   `Register` of a name already present rejects by default (test) and succeeds via
   `Unregister`+`Register` (test).
7. `Register` **rejects a principal-scoped registration** in this story: a test passes a tool carrying a
   principal binding (or calls the principal-scoped overload) and asserts a loud, typed failure naming
   42-6's per-principal view as the sanctioned path. No API exists in this story to register a tool for
   one user/tenant only.
8. **The mock hazard is pinned, not merely noted.** A test asserts a `Mock<IToolExecutor>` with no
   `Descriptor` setup returns **null**
   (Castle proxies implement the DIM and return `default`), so the three existing fixtures
   (`ToolExecutorRegistryTests.cs` L23, `InlineToolLoopRunnerTests.cs` L191,
   `AgenticToolLoopIntegrationTests.cs` L316) must either set the descriptor up or be excluded from
   deny-by-default assertions. Any registry/runner code that reads a descriptor treats `null` as the
   fail-safe default, and a test pins that.
9. `ResolvedTool` carries the descriptor and `ManagedAgent.ToResolvedTools` populates it from the
   registry; a test asserts a resolved built-in exposes its `PermissionClass`. **No change** to which of
   the six tools an unrestricted caller sees (back-compat: an empty allowlist still yields all six, on
   both the sequential and the parallel branch).
10. `ResolveToolsActivity` is deleted, **or** its built-in name `"read_file"` is corrected to
    `"file_read"` and it resolves through `IToolExecutorRegistry`; a test asserts no resolver in the tree
    emits a tool name that `IToolExecutorRegistry.GetExecutor` cannot resolve.

## Events

None new in this story (the `TOOL.*` DCB family is 42-5). Registry `Register`/`Unregister` log at
INFO with the tool name and category (never secrets).

## Single-user vs SaaS

No principal-scoped behavior here — the descriptor is a **static property of the tool**, identical in
both modes, and §3 explicitly forbids principal-scoped registration until 42-6. Per-principal
*overrides* of a tool's floor/enablement are 42-2; per-mode *resolution* is 42-3. This story is
mode-agnostic by construction.

## Dependencies

- **Epic 29 `SecretPurpose`** — exists, but **not where this story needs it**. It is at
  `Tamma.Api/Services/Secrets/SecretPurpose.cs`, namespace `Tamma.Api.Services.Secrets`, and is
  unreachable from `Tamma.Activities` (which references only `Tamma.Core` + `Tamma.Data`); adding a
  `Tamma.Activities → Tamma.Api` reference is circular because `Tamma.Api.csproj` L78 already references
  `Tamma.Activities`. Scope §0 relocates it to `Tamma.Core.Enums`. *(Corrected: earlier drafts said only
  "exists", which read as "usable here". It is not.)* The same reachability note applies to 42-4, which
  names `SecretRequirement`/`SecretRef` throughout.
- **Story 39-5 D6** (`GetAcceptanceRulesTool` + factory) — exists; this story annotates the tool and
  preserves the not-DI-registered invariant.
- **Unblocks:** 42-2 (store keys off descriptor names), 42-3 (reads `PermissionClass`/`AutonomyFloor`),
  42-4 (reads `RequiredSecret`), 42-5 (tags events with `PermissionClass`), 42-6 (registers via the new
  seam + owns the per-principal view), 42-7/8/9 (each declares a descriptor).

## Risks

- **A null descriptor is not the deny-by-default descriptor.** Mocked executors and any future
  proxy-based implementer return `null` from the DIM, silently bypassing the fail-safe. Mitigation:
  every consumer coalesces `null` to the deny-by-default descriptor at the read site, and the
  deny-by-default guarantee is asserted against **real** implementers (AC2/AC8), never mocks.
- **Drift test scope creep.** Widening AC4's drift test from "DI-registered tools" to "all
  `IToolExecutor` implementations" would fail the build on `GetAcceptanceRulesTool` and on the test
  fakes. AC4 pins the narrow scope with a negative test.
- **Registry mutability across principals.** The singleton makes any principal-scoped registration a
  cross-tenant leak. Converted from prose to a testable constraint in AC7: Wave 0 ships
  platform/deployment scope only; principal scope waits for 42-6's per-principal view.
- **Descriptors on the parallel branch.** `EnableParallelTools` defaults to `false`, so a change tested
  only on one branch can pass while the default path is unchanged. AC9 requires both branches.

## Estimated Effort

Medium — contract + registry change on the hottest path, plus the `SecretPurpose` relocation (mechanical
but wide: ~16 `Tamma.Api` files + tests) and the dead-activity cleanup. Heavy on tests, light on new
surface. ~4–5 days.
