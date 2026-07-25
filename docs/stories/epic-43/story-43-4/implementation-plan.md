# Implementation Plan — Story 43-4: Tool-Vocabulary Reconciliation + Fail-Loud Startup Validator

## Scope & Deliverable

When this story is done, Tamma.Api refuses to boot if the tool vocabularies disagree with the action
catalog. `ToolNameAliases` (resolution-only) maps the Claude-Code names the model is advertised
(`Read`/`Write`/`Edit`/`Bash`/`Grep`/`Glob`) onto `tool:*` `ActionKey`s so Story 43-5's resolver and Story
43-9's Seam B can evaluate policy under either vocabulary — **without changing a single advertised name**.
`ActionCatalogStartupValidator : IHostedService` runs four bidirectional checks (registry→catalog,
catalog→registry modulo a one-entry shrink-only allowlist, advertised/defensive names→catalog via aliases,
and reflection over every `IToolExecutor` implementation→catalog) and throws at boot naming the offender.
`GitOperationsTool` stops carrying a private copy of the git subcommand list and projects `GitSubcommand`
instead. The engine host gets the catalog touch only, because it registers no tools at all.

What this story deliberately does NOT deliver: `Bash` still does not execute. Making it execute is a
privilege expansion and is filed separately.

## Pre-Reading

- `docs/stories/epic-43/README.md` — the epic; "Fail-loud tool-vocabulary validator at startup — the check
  that has never existed. … Reconciling them is a **privilege expansion, not a cleanup**"
- `docs/stories/epic-43/story-43-2/` — `ActionKey`, `ActionNamespace`, `ToolAction`, `GitSubcommand`,
  `ActionCatalog.BuildIndex` and its seven error codes (this story's checks are codes 8–11 on the same type)
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/IToolExecutorRegistry.cs` +
  `ToolExecutorRegistry.cs:9-70` — the registry: `OrdinalIgnoreCase` dictionary keyed on
  `IToolExecutor.ToolName` (`:19`), duplicate registration logs a warning and keeps the first (`:23-30`),
  `GetExecutor` returns `null` + a warning on a miss (`:42-54`), `IsAllowed` **returns true on a null/empty
  allowlist** (`:56-62` — a fail-open the gate must not depend on)
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:745-766` — the six `AddSingleton<IToolExecutor, …>` lines
  (`:753-764`: FileRead, FileWrite, SearchCode, ShellExecute, GitOperations, RunTests), the registry
  `TryAddSingleton` at `:765`, and `ActionGate`/`ToolCallValidator` at `:750-752`
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:411-417` — the comment explaining why `GetAcceptanceRulesTool`
  is NOT an `IToolExecutor` registration (Story 39-5 D6, factory-minted principal-bound instances);
  `GetAcceptanceRulesToolFactory` registered at `:422`
- `apps/tamma-elsa/src/Tamma.Api/Services/AcceptanceRules/GetAcceptanceRulesTool.cs:27` — the 7th
  implementation, invisible to `GetAll()`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs:286-289` — Story 32-5 AC9: the tool catalog was REMOVED
  from the engine. **The reason the validator is host-asymmetric.**
- `apps/tamma-elsa/src/Tamma.Api/Services/Agents/DefaultAgentConfig.cs:53,70,85,102,118,149,165` — the seven
  `Tools = new[] { … }` sites (see Corrections: `:134` is `Array.Empty<string>()`, not a name site)
- `apps/tamma-elsa/src/Tamma.Api/Services/Agents/ManagedAgent.cs:923-937` (`ToResolvedTools`) and `:328`
  (its single call site) — `names.Select(n => new ResolvedTool { Name = n })`, no schema, no registry lookup
- `apps/tamma-elsa/src/Tamma.Activities/Security/ToolCallValidator.cs:35-45` (`ShellToolNames` 13 members,
  `CommandFields` 6) and `:240` (`IsShellTool`) — checked from outside, never derived
- `apps/tamma-elsa/src/Tamma.Activities/Security/ActionGate.cs:17-49` — the 20-regex denylist
  `ShellToolNames` gates entry to; **the name collision** that makes every new type in this epic
  `ActionCatalog*`/`Autonomy*`
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/GitOperationsTool.cs:20-25` (the private 14-member
  HashSet) and `:30-32` (the same list restated in prose in `Description`)
- `apps/tamma-elsa/src/Tamma.Api/Services/Prompts/PromptFileLoader.cs` — the fail-loud-at-startup posture
  being copied (proven over 101 files)
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs:255-271,388-403` — the
  shrink-only ratchet + staleness idiom; note its shrink-only property is a **comment, not an assertion**,
  which is why AC5 adds a count pin
- **NOT FOUND (this story's prerequisites, no code yet):** `apps/tamma-elsa/src/Tamma.Core/Actions/*`
  (43-2), `apps/tamma-elsa/src/Tamma.Api/Services/Actions/*` (this story creates the folder).
  `Tamma.Activities/LlmCall/ResolveToolsActivity.cs` exists today and is deleted by 43-0.

## Design Decisions

- **D1 — The alias map lives in `Tamma.Api`, not `Tamma.Core`.** `ToolAction` and `ActionKey` are Core
  vocabulary; the *alias* is a fact about what one particular agent-configuration surface advertises
  (`DefaultAgentConfig`, a Tamma.Api type). Putting the map in Core would pull an Api concern into the
  zero-reference assembly and imply the engine needs it — it does not (D2). Home:
  `Tamma.Api/Services/Actions/ToolNameAliases.cs`, beside where 43-5's `AutonomyGateService` and 43-6's
  endpoints land.

- **D2 — Host asymmetry is a design decision, not an oversight, and is tested.** `Tamma.ElsaServer`
  registers no `IToolExecutor` and no `IToolExecutorRegistry` (`ElsaServer/Program.cs:286-289`). Running the
  registry↔catalog checks there would throw on every engine boot with an empty registry. So: **Tamma.Api**
  gets `ActionCatalogStartupValidator` (catalog touch + all four tool checks); **Tamma.ElsaServer** gets a
  bare `ActionCatalogIndexTouch` hosted service (the eager `ActionCatalog.ByKey.Count` read from 43-2 only).
  Pinned by `EngineHost_DoesNotAssertToolParity` so a later "why is this only in one host?" refactor
  re-reads the reason instead of unifying them.

- **D3 — Resolution-only is enforced structurally, not by convention.** `ToolNameAliases` exposes exactly
  `TryResolve(string, out ActionKey)` and `IReadOnlyDictionary<string, ActionKey> All` (for the validator and
  its tests). It is `internal` to Tamma.Api plus `[assembly: InternalsVisibleTo]` for the test project, so
  `ManagedAgent` *could* still call it — therefore a source-scanning test
  (`ToolNameAliases_IsNotReferencedFromAdvertisementPath`) greps `ManagedAgent.cs`,
  `ToolExecutorRegistry.cs`, `DefaultAgentConfig.cs` and `ResolvedTool`'s file for the identifier and fails
  on a hit. Cheap, and it is the only mechanism that can express "this exists but must not be wired here".

- **D4 — The fourth check reflects over implementations, not over `GetAll()`.** `GetAll()` returns the
  container's six; the 7th (`GetAcceptanceRulesTool`) is structurally invisible to it. The check therefore
  scans loaded assemblies for concrete non-abstract `IToolExecutor` implementers. To read `ToolName` it must
  instantiate or read a constant — both fragile (constructors take `ILogger`, `workspaceRoot`, etc.). So the
  binding is **declared, not read**: a `[CataloguedAs("tool:file_read")]` attribute on each implementation
  class, and the check asserts (a) every implementer carries the attribute, (b) the key parses, (c) keys are
  unique, and (d) for the six DI-registered ones the attribute's key agrees with the runtime `ToolName`
  resolved through the aliases. This closes the "7th tool is invisible" gap without constructing tools at
  boot.

- **D5 — `ShellToolNames` is checked, never derived (see the story's Architectural Context).** The check is
  "resolves through aliases to a catalog member **or** is on `KnownDefensiveAliases`". Seeded with the 12
  members that name no executor (`execute_shell_command`, `run_command`, `shell`, `exec`, `bash`, `terminal`,
  `run_shell`, `execute_command`, `system_command`, `run_code`, `execute`, `cmd`) — note `bash` is on this
  list even though `Bash` is an alias, because `ShellToolNames` membership is about triggering `ActionGate`,
  not about resolving policy, and conflating the two is exactly the trap. `shell_execute` resolves. The
  defensive list is count-pinned; if a future story adds a real executor named `exec`, the entry goes stale
  and fails.

- **D6 — `GitSubcommand` replaces the HashSet by projection, and the `Description` prose is generated.**
  `GitOperationsTool.AllowedSubcommands` becomes
  `GitSubcommand.All.Select(s => s.ToWire()).ToHashSet(StringComparer.OrdinalIgnoreCase)` and `Description`
  interpolates `string.Join(", ", …)` over the same source. Two copies collapse to zero. Behaviour is
  identical by construction and asserted by a symmetric-diff test against the literal 14 names, so a wrong
  `GitSubcommand` in 43-2 fails here rather than silently widening or narrowing what git can do. **This is
  the one place in the story where a catalog error could change runtime behaviour**, which is why the test
  pins the literal list rather than comparing the enum to itself.

- **D7 — Failures are `TammaError` with structured context, thrown from `StartAsync`, and aggregated.** All
  four checks run, collect every violation, and throw once with the full list — not first-failure-wins. A
  developer who has added three tools should see three names, not one boot per name. The message shape
  copies `PromptFileLoader`'s: error code, the offending symbol, and the exact file to edit.

- **D8 — The validator does not resolve scoped services.** `IHostedService.StartAsync` runs against the root
  provider; `IToolExecutorRegistry` is a singleton (`Program.cs:765`) so it is directly injectable, but
  `DefaultAgentConfig`'s tool arrays are static data and `ShellToolNames` is `private static`. The latter is
  read via a small `internal static IReadOnlyCollection<string> KnownShellToolNames => ShellToolNames;`
  accessor added to `ToolCallValidator` (the only edit to that file) rather than by reflection over a private
  field — reflection over privates is the kind of test-only coupling that breaks on a rename with no
  compiler help.

## Implementation Steps

1. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/Actions/ToolNameAliases.cs`** (AC1, D1/D3).

   ```csharp
   internal static class ToolNameAliases
   {
       // Resolution-only. MUST NOT be applied to advertised tool names.
       // See docs/stories/epic-43/story-43-4/ — this is a policy map, not a rename.
       private static readonly Dictionary<string, ActionKey> Map = new(StringComparer.OrdinalIgnoreCase)
       {
           ["Read"]  = ToolAction.FileRead.Key(),     ["file_read"]  = ToolAction.FileRead.Key(),
           ["Write"] = ToolAction.FileWrite.Key(),    ["Edit"]       = ToolAction.FileWrite.Key(),
           ["file_write"] = ToolAction.FileWrite.Key(),
           ["Bash"]  = ToolAction.ShellExecute.Key(), ["shell_execute"] = ToolAction.ShellExecute.Key(),
           ["Grep"]  = ToolAction.SearchCode.Key(),   ["Glob"]       = ToolAction.SearchCode.Key(),
           ["search_code"] = ToolAction.SearchCode.Key(),
           ["run_tests"] = ToolAction.RunTests.Key(),
           ["get_acceptance_rules"] = ToolAction.GetAcceptanceRules.Key(),
           // git_operations resolves per-subcommand — see ResolveGit below.
       };
       public static bool TryResolve(string emittedName, out ActionKey key);
       public static bool TryResolveGit(string subcommand, out ActionKey key); // → .read / .write split
       public static IReadOnlyDictionary<string, ActionKey> All => Map;
   }
   ```

   `TryResolveGit` maps the 14 `GitSubcommand` members onto `tool:git_operations.read`
   (`status, diff, log, show, rev-parse, ls-files, fetch, branch`) vs `.write`
   (`add, commit, push, checkout, stash, pull`) per 43-2's descriptors. Bare `git_operations` with no parsed
   subcommand resolves to `.write` (the stricter member) — fail-safe, and stated in the doc comment.

2. **MODIFY `apps/tamma-elsa/src/Tamma.Activities/Security/ToolCallValidator.cs`** (D8) — add exactly one
   member, `internal static IReadOnlyCollection<string> KnownShellToolNames => ShellToolNames;`. No other
   change: the 13-member set, `CommandFields`, and `IsShellTool` (`:240`) are untouched, and the file gains
   no reference to `ActionCatalog` or `ToolNameAliases` (AC8).

3. **CREATE `apps/tamma-elsa/src/Tamma.Core/Actions/CataloguedAsAttribute.cs`** (D4) — a one-line
   `[AttributeUsage(AttributeTargets.Class)] sealed class CataloguedAsAttribute(string wireKey)`. Core,
   because `Tamma.Activities`' tool classes must carry it and Activities cannot see Tamma.Api.
   **MODIFY the six executor classes** (`FileReadTool.cs:15`, `FileWriteTool.cs:16`, `SearchCodeTool.cs:15`,
   `ShellExecuteTool.cs:14`, `GitOperationsTool.cs:13`, `RunTestsTool.cs:13`) **and
   `Tamma.Api/Services/AcceptanceRules/GetAcceptanceRulesTool.cs:27`** to carry it.

4. **MODIFY `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/GitOperationsTool.cs`** (AC6, D6) — replace
   the private `AllowedSubcommands` HashSet (`:20-25`) with a projection over `GitSubcommand`, and generate
   the subcommand list inside `Description` (`:30-32`) from the same source. No behavioural change intended;
   step 8's symmetric-diff test is what proves it.

5. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/Actions/ActionCatalogStartupValidator.cs`**
   (AC3/AC4/AC7, D2/D7):

   ```csharp
   internal sealed class ActionCatalogStartupValidator(
       IToolExecutorRegistry registry, ILogger<ActionCatalogStartupValidator> logger) : IHostedService
   {
       public Task StartAsync(CancellationToken ct)
       {
           _ = ActionCatalog.ByKey.Count;              // force the 43-2 static ctor at boot
           var violations = new List<(string Code, string Detail)>();
           CheckRegistryToCatalog(violations);         // ACTION.CATALOG.TOOL_NOT_IN_CATALOG
           CheckCatalogToRegistry(violations);         // ACTION.CATALOG.CATALOG_TOOL_HAS_NO_EXECUTOR
           CheckAdvertisedAndDefensiveNames(violations); // ACTION.CATALOG.UNRESOLVABLE_TOOL_ALIAS
           CheckImplementationTypes(violations);       // ACTION.CATALOG.EXECUTOR_TYPE_NOT_IN_CATALOG
           if (violations.Count > 0) throw new TammaError(...); // ALL of them, one throw
           logger.LogInformation("Action catalog tool vocabulary validated: {N} members", ...);
           return Task.CompletedTask;
       }
   }
   ```

   Sources read: `registry.GetAll()`; `ActionCatalog.ByNamespace[ActionNamespace.Tool]`;
   `DefaultAgentConfig`'s seeded configs (its public accessor, iterating every `Tools` array — including the
   empty `product_owner` one, which trivially passes); `ToolCallValidator.KnownShellToolNames`; reflection
   over `typeof(IToolExecutor).Assembly` **plus** `typeof(GetAcceptanceRulesTool).Assembly` for concrete
   implementers.

6. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/Actions/ToolCatalogAllowlists.cs`** (AC5, D5) — two
   shrink-only ratchets, each a `record Entry(string Key, string Justification)` array plus a count pin
   consumed by the tests:
   - `NotDiRegisteredTools` — **exactly one** entry, `tool:get_acceptance_rules`, justification
     `"39-5 D6: GetAcceptanceRulesToolFactory mints principal-bound instances per tenant-agent session; a singleton registration would carry no principal (Program.cs:411-417)."`
   - `KnownDefensiveAliases` — the 12 `ShellToolNames` members naming no executor, justification
     `"defensive alias: triggers ActionGate's denylist for a shell-shaped tool call; names no executor by design (ToolCallValidator.cs:35-40)."`

7. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Program.cs`** — register the validator as a hosted service
   immediately after the tool registrations at `:765`, with a comment pointing at this story and at
   `ElsaServer/Program.cs:286-289` for why the engine differs.
   **MODIFY `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs`** — register `ActionCatalogIndexTouch` (43-2's
   bare touch) only, with the same cross-reference comment (D2).

8. **CREATE the test suites** — `apps/tamma-elsa/tests/Tamma.Api.Tests/Actions/ActionCatalogStartupValidatorTests.cs`,
   `ToolNameAliasesTests.cs`, `ToolCatalogAllowlistTests.cs`, and
   `apps/tamma-elsa/tests/Tamma.Activities.Tests/LlmCall/Tools/GitOperationsSubcommandTests.cs`. See Test Plan.

9. **MODIFY `docs/stories/epic-43/README.md`** (AC9) — one paragraph under "Drift prevention" recording the
   divergence this story makes visible but does not close, and naming the follow-on story that would make
   the advertised names executable.

## Test Plan

NUnit + FluentAssertions. No Testcontainers — every check is in-memory or reflection.

- **`ToolNameAliasesTests`** (unit) — every registry name resolves to itself; the six Claude-Code names
  resolve to the four expected members; casing is ignored (`bash`, `BASH`, `Bash` all resolve);
  an unknown name returns false and does not throw; `TryResolveGit` covers all 14 subcommands with the
  read/write split pinned per member (`[TestCase]` each); bare `git_operations` resolves to `.write`.
  **Covers AC1.**
- **`AdvertisedToolNamesAreUnchangedTests`** — the seven `DefaultAgentConfig.Tools` arrays compared to
  hardcoded expected literals (byte-identical, order-sensitive); `ManagedAgent.ToResolvedTools` (exercised
  through its public call path with a Claude-Code-named `ResolvedAgentConfig`) yields `ResolvedTool.Name`
  values equal to the input names; `ToolNameAliases_IsNotReferencedFromAdvertisementPath` source-scans
  `ManagedAgent.cs`, `ToolExecutorRegistry.cs`, `DefaultAgentConfig.cs`. **Covers AC2, D3.**
- **`ActionCatalogStartupValidatorTests`** — one test per throw code, each built by feeding a doctored input
  (a fake registry returning an uncatalogued name; a catalog member with no executor and no allowlist entry;
  an injected advertised name `Frobnicate`; a test-assembly `IToolExecutor` with no `[CataloguedAs]`) and
  asserting the thrown `TammaError` carries the right code **and names the offending symbol in the message**.
  Plus: `Validator_ReportsEveryViolationInOneThrow` (three simultaneous faults → three names, one exception,
  D7); `Validator_Passes_OnTheRealContainer` (the load-bearing green case — `WebApplicationFactory` boots and
  the app starts); `AllIToolExecutorImplementations_HaveACatalogMember` (reflection, explicitly asserting the
  7th, `GetAcceptanceRulesTool`, is seen — a regression guard on D4);
  `EngineHost_DoesNotAssertToolParity` (the ElsaServer host builder starts with no registry present).
  **Covers AC3, AC4, AC7.**
- **`ToolCatalogAllowlistTests`** — `NotDiRegisteredTools.Count == 1` and its single key is
  `tool:get_acceptance_rules`; a stale entry (a key that IS registered) fails with a message telling the
  developer to delete the line; `KnownDefensiveAliases.Count == 12`; every justification is non-empty and
  cites a file (the `ContractBindingTests` keyword-classification shape);
  `ShellToolNames_AreAllResolvableOrJustified` — all 13 members partition into
  resolvable ∪ justified with no leftovers. **Covers AC5, AC8 (first half).**
- **`ToolCallValidatorUntouchedTests`** — `KnownShellToolNames` has exactly the 13 expected members
  (explicit literals); `CommandFields` has the 6; the compiled `ToolCallValidator` type references neither
  `ActionCatalog` nor `ToolNameAliases` (assembly-reference / source scan). **Covers AC8.**
- **`GitOperationsSubcommandTests`** — the projected `AllowedSubcommands` equals the literal 14-name set by
  symmetric diff, with a failure message naming the missing/extra member and the `GitSubcommand` line to
  edit; `Description` contains each of the 14 exactly once; an unlisted subcommand (`reset`, `rebase`,
  `clean`) is still rejected; the existing `GitOperationsTool` behavioural tests still pass unmodified.
  **Covers AC6 — and is the tripwire that a bad `GitSubcommand` in 43-2 cannot widen git.**

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — resolution-only alias map | 1 | `ToolNameAliasesTests` |
| 2 — advertised names provably unchanged | 1 (by omission), 8 | `AdvertisedToolNamesAreUnchangedTests` incl. the source scan |
| 3 — four bidirectional checks, four codes | 5, 6 | `ActionCatalogStartupValidatorTests` (one per code) |
| 4 — runs at boot; engine gets touch only | 5, 7 | `Validator_Passes_OnTheRealContainer`, `EngineHost_DoesNotAssertToolParity` |
| 5 — shrink-only ratchet + count pin | 6 | `ToolCatalogAllowlistTests` (count pin + staleness) |
| 6 — `GitOperationsTool` consumes `GitSubcommand` | 4 | `GitOperationsSubcommandTests` (symmetric diff + behaviour) |
| 7 — one test per throw code, offender named | 8 | `ActionCatalogStartupValidatorTests` |
| 8 — `ShellToolNames` untouched | 2 (accessor only), 8 | `ToolCallValidatorUntouchedTests` |
| 9 — divergence documented, not silently closed | 9 | Reviewer check against the epic README section |

## Risks & Mitigations

- **The validator turns a mismatched tool into a total outage.** Adding a `ToolAction` member without an
  executor, or an executor without a member, stops Tamma.Api from starting — including every
  `WebApplicationFactory` test host. This is intentional (the `PromptFileLoader` posture) but it will bite
  the first developer who adds a tool and runs the app before the tests. Mitigation: the throw message names
  the exact file and line to edit, and D7's aggregation means one boot shows every problem.
- **`[CataloguedAs]` is a declaration, so it can be wrong.** A tool class annotated with another tool's key
  passes the type check. Mitigation: the uniqueness assertion plus the cross-check against the runtime
  `ToolName` for the six DI-registered ones; only the un-registered 7th is declaration-only. This is the same
  site-vs-effect hole the epic records as its honest ceiling — recorded, not claimed closed.
- **`GitSubcommand` is the one place a catalog error changes runtime behaviour.** A missing member silently
  removes a git capability; an extra one silently adds it. Mitigation: the literal-list symmetric-diff test,
  written against the 14 names copied from `GitOperationsTool.cs:20-25` as it stands today, not against the
  enum.
- **A reader will try to "finish the job" and wire the aliases into advertisement.** That is the privilege
  expansion. Mitigation: the source-scanning test fails the build, and both the type's doc comment and the
  story's first section say why.
- **The engine/API asymmetry looks like a bug to a future refactorer.** Mitigation:
  `EngineHost_DoesNotAssertToolParity` plus cross-referencing comments in both `Program.cs` files.
- **43-2 slip is a hard block.** Every check dereferences `ActionCatalog`. Mitigation: steps 1–2 and the
  `ToolNameAliases` tests can be written against a hand-stubbed `ActionKey` and rebased; steps 3–8 cannot.

## Blocks / Blocked by

- **Blocked by:** Story 43-2 (catalog core: `ActionKey`, `ToolAction`, `GitSubcommand`, `ActionCatalog`) —
  hard. Story 43-0 (deletes `ResolveToolsActivity`, vocabulary (c)) — soft; if it slips, the third
  vocabulary stays alive and unvalidated, which must be noted in review.
- **Blocks:** Story 43-5 (the resolver resolves emitted tool names through `ToolNameAliases` before it can
  look up an assignment), Story 43-9 Seam B (`InlineToolLoopRunner`'s gate call resolves the name at
  `:259-281`), Story 43-8 (this validator is the tool-plane member of the drift-harness set and its
  allowlists join the four-ratchet family).
- **Parallel-safe:** Story 43-3 (groups) — it assigns the `tool:*` members to groups but does not touch any
  file this story edits.

## Effort Breakdown

| Step(s) | Work | Days |
|---|---|---|
| 1 | `ToolNameAliases` incl. the git read/write split | 0.4 |
| 2, 3 | `KnownShellToolNames` accessor; `[CataloguedAs]` + 7 annotations | 0.3 |
| 4 | `GitOperationsTool` projection + generated description | 0.3 |
| 5, 6 | Validator (4 checks, aggregated throw) + the two ratchets | 0.8 |
| 7 | DI registration in both hosts + cross-reference comments | 0.2 |
| 8 | Test suites (5 files, incl. the real-container green case) | 0.8 |
| 9 | README section on the divergence not closed | 0.2 |
| **Total** | | **3.0** (story estimate: 3 days) |
