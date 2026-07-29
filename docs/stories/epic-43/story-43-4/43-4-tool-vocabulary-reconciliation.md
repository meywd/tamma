# Story 43-4: Tool-Vocabulary Reconciliation + Fail-Loud Startup Validator

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then, before writing any code, check the knowledge base:

- `.dev/spikes/` — existing research
- `.dev/bugs/` — known bugs
- `.dev/findings/` — pitfalls and best practices
- `.dev/decisions/` — architecture decisions

## User Story

As a **platform owner** who has just been told the action catalog governs every tool the system can run,
I want the three disagreeing tool vocabularies reconciled behind one resolution map and a boot-time check that refuses to start when they diverge,
So that a `tool:*` catalog row actually corresponds to something that can execute — and so the divergence that today makes `Write` and `Bash` silently inert for five of seven roles becomes impossible to reintroduce.

## Priority

P0 — Blocks Story 5 (the resolver must be able to answer "what does the emitted name `Bash` resolve to?") and Story 9 Seam B (the tool-dispatch gate resolves an emitted tool name to an `ActionKey` before it can evaluate anything). Without it the `tool` namespace of the catalog is 8 members that cannot be matched to a running tool call.

## READ THIS FIRST: this is a privilege expansion, not a cleanup

**Three vocabularies disagree, and the disagreement is load-bearing today.**

| # | Vocabulary | Names | Canonical site |
|---|---|---|---|
| (a) | Executor registry — what can actually run | `file_read`, `file_write`, `search_code`, `shell_execute`, `git_operations`, `run_tests`, `get_acceptance_rules` | 7 classes implementing `IToolExecutor`; 6 DI-registered at `apps/tamma-elsa/src/Tamma.Api/Program.cs:753-764`, registry at `:765` |
| (b) | Per-role agent config — what is **advertised to the model** | `Read`, `Write`, `Edit`, `Bash`, `Grep`, `Glob` (Claude-Code names) | `apps/tamma-elsa/src/Tamma.Api/Services/Agents/DefaultAgentConfig.cs:53,70,85,102,118,149,165` |
| (c) | Dead built-in map | `search_code`, `read_file`, `run_tests` | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolveToolsActivity.cs:161-224` (`"read_file"` at `:188`) — **zero callers**; deleted by Story 43-0 |

The advertised list reaches the model verbatim and is never checked against the registry:
`ManagedAgent.ToResolvedTools` (`apps/tamma-elsa/src/Tamma.Api/Services/Agents/ManagedAgent.cs:923-937`) does
`names.Select(n => new ResolvedTool { Name = n })` and passes it at `:328`. The registry's `InputSchema`
values are never consulted for advertisement.

**The consequence, stated plainly: `Read`, `Write`, `Edit`, `Bash`, `Grep` and `Glob` cannot execute.**
`ToolExecutorRegistry.GetExecutor` (`ToolExecutorRegistry.cs:42-54`) is an ordinal-ignore-case dictionary
keyed on `IToolExecutor.ToolName` — the registry names. A model that emits `Bash` gets a `null` executor and
a logged warning. Five of the seven seeded roles (`senior_developer`, `qa_engineer`, `code_reviewer`,
`architect`, `tech_lead` — the `Tools = new[] { "Read", "Write", "Edit", "Bash", "Grep", "Glob" }` and
`{ "Read", "Grep", "Glob" }` rows) advertise a tool surface that does nothing.

**Therefore reconciliation makes those tools work for the first time.** Anything in this story that changes
what a name resolves to is a *privilege expansion shipped inside a governance epic*. It is scoped
deliberately:

- **`ToolNameAliases` is RESOLUTION-ONLY.** It maps an emitted/advertised name to a catalog `ActionKey`
  for policy purposes. It MUST NOT be applied to `ManagedAgent.ToResolvedTools`' output, to
  `ResolvedTool.Name`, or to the dictionary key in `ToolExecutorRegistry`. Advertised names are byte-identical
  before and after this story, and that is pinned by a test.
- **The divergence becomes boot-visible, not silently repaired.** The validator's job is to make the
  mismatch a startup failure so a human decides. Actually rewriting advertisement (so `Bash` executes) is a
  separate, reviewed story **outside Epic 43** — it is a capability change and must not ride a validator.

## Architectural Context (READ FIRST)

**The validator's host is Tamma.Api only.** `Tamma.ElsaServer` registers **no** `IToolExecutor` and no
`IToolExecutorRegistry` — Story 32-5 (AC9) removed the whole catalog from the engine
(`apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs:286-289`: "the tool executors are registered there, not
here"). The tool-vocabulary half of `ActionCatalogStartupValidator` therefore runs in Tamma.Api; the engine
host runs only the catalog-index touch from Story 43-2. Asserting registry↔catalog parity in the engine
would throw on every engine boot.

**`GetAcceptanceRulesTool` is deliberately not DI-registered, and stays that way.**
`apps/tamma-elsa/src/Tamma.Api/Services/AcceptanceRules/GetAcceptanceRulesTool.cs:27` implements
`IToolExecutor`, but `Program.cs:411-417` documents the reason it is absent from the container: Story 39-5
Design Decision D6 — `GetAcceptanceRulesToolFactory` mints **principal-bound instances per tenant-agent
session**, so a singleton registration would be wrong (it would carry no principal). This story does not
"fix" it. It is the one seeded entry in the shrink-only `NotDiRegisteredTools` allowlist, with that
justification.

**`ToolCallValidator.ShellToolNames` is NOT derived from the catalog and is not replaced.**
`apps/tamma-elsa/src/Tamma.Activities/Security/ToolCallValidator.cs:35-40` is a defensive set of **13**
shell-ish aliases (`execute_shell_command`, `run_command`, `shell`, `exec`, `bash`, `terminal`, `run_shell`,
`execute_command`, `system_command`, `run_code`, `execute`, `cmd`, `shell_execute`), only one of which
(`shell_execute`) names a real executor. Its consumer (`:240`) decides whether to run `ActionGate`'s regex
denylist. Deriving it from the catalog would **delete 12 defensive aliases** and would newly subject
`run_tests` — which does expose a `command` field, and `CommandFields` at `:44-45` is
`{command, cmd, script, code, shell_command, input}` — to `ActionGate`'s `rm -rf` / `.env` / `printenv`
regexes, producing false-positive blocks. The set stays exactly as it is; the validator adds a check *in
front of* it (every member must resolve through the aliases to a catalog member or be an explicitly
justified defensive alias).

**Git subcommands are a private `HashSet`.** `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/GitOperationsTool.cs:20-25`
holds 14 allowed subcommands (`status, diff, log, add, commit, push, branch, checkout, stash, show, fetch,
pull, rev-parse, ls-files`) with no drift protection, and the same list is restated in prose in the
`Description` property at `:31-32`. Story 43-2 introduces `GitSubcommand` (14 `[Wire]` members) in
`Tamma.Core/Actions/`; this story makes `GitOperationsTool` **consume** it so the private set and the
description string stop being a third independent copy.

**Naming.** Everything this story adds is `ActionCatalog*` / `ToolNameAliases`. `ActionGate` is taken —
`apps/tamma-elsa/src/Tamma.Activities/Security/ActionGate.cs:17`, DI-registered at
`apps/tamma-elsa/src/Tamma.Api/Program.cs:750`.

**The fail-loud precedent.** `PromptFileLoader` (`Tamma.Api/Services/Prompts/`, proven over 101 files) and
`DocumentTypeRegistry.BuildIndex` are the house posture: a vocabulary mismatch refuses to boot. This story
applies that posture to tools, which has never been done.

## Acceptance Criteria

1. **`ToolNameAliases` exists and is resolution-only.** `apps/tamma-elsa/src/Tamma.Api/Services/Actions/ToolNameAliases.cs`
   exposes `bool TryResolve(string emittedName, out ActionKey key)` mapping, at minimum:
   `Read → tool:file_read`; `Write | Edit → tool:file_write`; `Bash → tool:shell_execute`;
   `Grep | Glob → tool:search_code`; plus identity for every registry name. Matching is
   `OrdinalIgnoreCase` (matching `ToolExecutorRegistry`'s comparer, `ToolExecutorRegistry.cs:19`).
   The type has **no** public mutator and is not referenced from `ManagedAgent`, `ResolvedTool`, or
   `ToolExecutorRegistry`.

2. **A test pins that advertised names are unchanged.** `Aliases_DoNotChangeAdvertisedToolNames` asserts the
   exact string arrays at `DefaultAgentConfig.cs:53,70,85,102,118,149,165` are byte-identical to the
   pre-story values, and that `ManagedAgent.ToResolvedTools` output names equal its input names for a
   Claude-Code-named config. A grep-shaped assertion additionally fails if `ToolNameAliases` is referenced
   from `ManagedAgent.cs` or `ToolExecutorRegistry.cs`.

3. **`ActionCatalogStartupValidator : IHostedService` ships in Tamma.Api and refuses to boot on any of four
   bidirectional failures**, each with its own `TammaError` code and a message naming the offending symbol:
   - every `IToolExecutorRegistry.GetAll()` name resolves to a `tool:*` catalog member →
     `ACTION.CATALOG.TOOL_NOT_IN_CATALOG`
   - every `tool:*` catalog member resolves to a registered executor, **modulo the shrink-only
     `NotDiRegisteredTools` allowlist** → `ACTION.CATALOG.CATALOG_TOOL_HAS_NO_EXECUTOR`
   - every name in `ToolCallValidator.ShellToolNames` and every name in every `DefaultAgentConfig.Tools`
     array resolves through `ToolNameAliases` to a catalog member, or is on the justified
     `KnownDefensiveAliases` list → `ACTION.CATALOG.UNRESOLVABLE_TOOL_ALIAS`
   - **reflection over every `IToolExecutor` implementation in the loaded assemblies** (not `GetAll()`,
     which structurally cannot see the 7th) maps to exactly one catalog member →
     `ACTION.CATALOG.EXECUTOR_TYPE_NOT_IN_CATALOG`

4. **The validator runs before the app serves traffic and is registered once.** It is an `IHostedService`
   whose `StartAsync` performs the checks and throws; it also touches `ActionCatalog.ByKey.Count` so the
   Story 43-2 static-ctor index build is forced at boot rather than at first request. `Tamma.ElsaServer`
   gets the **catalog touch only** — a documented, tested asymmetry (`EngineHost_DoesNotAssertToolParity`),
   because the engine registers no tool catalog (`ElsaServer/Program.cs:286-289`).

5. **`NotDiRegisteredTools` is a shrink-only ratchet with a count pin.** Seeded with exactly one entry:
   `tool:get_acceptance_rules`, justification citing `Program.cs:411-417` / Story 39-5 D6 (factory-minted,
   principal-bound). Adding an entry fails the count pin; an entry that now *is* DI-registered fails as
   stale. Same mechanism as `ContractBindingTests.cs:263-271`, with the count assertion that harness lacks.

6. **`GitOperationsTool` consumes `GitSubcommand`.** The private `AllowedSubcommands` HashSet
   (`GitOperationsTool.cs:20-25`) is replaced by a projection over the Story 43-2 `GitSubcommand` wire set,
   and the `Description` string (`:31-32`) is generated from the same source rather than restating the list.
   A test asserts the executed-subcommand set equals the enum's wire set exactly (symmetric diff, naming the
   missing member), and that no behaviour changed: the same 14 subcommands are permitted, no more.
   `git_operations` resolves to `tool:git_operations.read` / `tool:git_operations.write` per the descriptor
   split from 43-2.

7. **One test per throw code**, each asserting the failure is at startup and the message names the offender:
   `Boot_Throws_WhenExecutorHasNoCatalogMember`, `Boot_Throws_WhenCatalogToolHasNoExecutor`,
   `Boot_Throws_WhenAdvertisedNameIsUnresolvable`, `Boot_Throws_WhenAnImplementationTypeIsUncatalogued`.
   Plus `AllIToolExecutorImplementations_HaveACatalogMember` (reflection, **not** `GetAll()`) and
   `ShellToolNames_AreAllResolvableOrJustified`.

8. **`ToolCallValidator.ShellToolNames` is untouched.** A test asserts the set still contains all 13
   members and that `ToolCallValidator` has no reference to `ActionCatalog` or `ToolNameAliases` — the
   validator checks it from outside; it does not become derived. `CommandFields` is likewise untouched.

9. **The story documents the divergence it does not close.** A short section in
   `docs/stories/epic-43/README.md` (or a linked note) records: advertised Claude-Code names still do not
   execute; the alias map exists for policy only; the capability change is filed as a separate story with
   its own review. No code silently narrows or widens what the model can run.

## Dependencies

- **Story 43-2 (Catalog core)** — `ActionKey`, `ActionNamespace`, `ToolAction` (8 members), `GitSubcommand`
  (14), `ActionCatalog.ByKey`, `ActionCatalog.TryGet`. **Blocking**; every check in AC3 dereferences it.
- **Story 43-0 (Prerequisite fixes)** — deletes `Tamma.Activities/LlmCall/ResolveToolsActivity.cs`
  (vocabulary (c), zero callers). Not strictly blocking, but if it has not landed the validator must either
  ignore that file or Story 43-0 must land first; ignoring it leaves a third vocabulary alive.
- **Existing, verified:** `IToolExecutorRegistry` / `ToolExecutorRegistry`
  (`Tamma.Activities/LlmCall/Tools/`), the six DI registrations (`Tamma.Api/Program.cs:753-764`),
  `GetAcceptanceRulesToolFactory` (`Program.cs:422`), `ToolCallValidator`
  (`Tamma.Activities/Security/ToolCallValidator.cs`), `DefaultAgentConfig`, `ManagedAgent.ToResolvedTools`.
- **Feeds:** Story 43-5 (the resolver resolves emitted names through `ToolNameAliases`), Story 43-9 Seam B
  (`InlineToolLoopRunner` gate resolves the tool name before evaluating), Story 43-8 (the validator is the
  tool-plane half of the drift harness set).

## Out of Scope

- **Rewriting what the model is advertised.** `ManagedAgent.ToResolvedTools` is not modified. Making `Bash`
  resolve to `shell_execute` at execution time is the privilege expansion; it ships separately.
- **Merging the two shell denylists.** `CommandValidator.cs` (16 regexes) and `ActionGate.cs` (20) remain
  separate and un-catalogued. Recorded as an open hole in the epic README, not fixed here.
- **A protected-path selector for `file_write`.** `PathValidator` enforces workspace-root containment only;
  a `file_write.protected` member would be a row that can never be selected, so it is not shipped.
- **MCP tool granularity.** MCP stays one coarse catalog member with no drift signal.
- **Registering `GetAcceptanceRulesTool` in DI.** Deliberately excluded (39-5 D6); it is an allowlist entry,
  not a bug.
- **Anything in `Tamma.ElsaServer`'s tool surface.** There is none.

## Estimated Effort

3 days

## Follow-ups from review (2026-07-29) — all closed 2026-07-29

- **Git grading hole recorded in the catalog.** The `git_operations` read/write split grades by
  SUBCOMMAND ONLY while args are screened only for shell metacharacters — a read-graded call can still
  mutate (`{"subcommand":"log","args":"--output=FILE"}` writes a file; `branch -D x` deletes local
  refs; `fetch`/`branch` are graded Read by the documented local-refs rationale in
  `GitSubcommand.cs:60-64`). Now candidly disclosed next to the existing `file_write`/`shell_execute`
  hole disclosures: a comment block + description note on `tool:git_operations.read` in
  `ActionCatalog.Descriptors.cs`, stating it MUST be revisited when `tool:git_operations.write` is
  human-gated (at that point the Read grade is a gate bypass, not a nuance).
- **Two gate-suite test gaps closed.** (i) The `Enforceable=false` short-circuit in
  `CatalogDefaultToolLoopAutonomyGate.Evaluate` is now driven directly: the internal rehearsal seam
  gained an `enforceableOverride` (no shipped tool descriptor is non-enforceable), pinned by
  `ToolLoopAutonomyGateTests.Evaluate_short_circuits_a_non_enforceable_descriptor_before_any_threshold`.
  (ii) The PARALLEL execution fork is now proven to exclude denied calls end-to-end
  (`ToolLoopAutonomyGateSeamTests.A_denied_tool_call_is_excluded_from_the_parallel_execution_path_too`,
  with `EnableParallelTools` + a real `ParallelToolExecutor`) — the earlier seam tests only exercised
  the sequential path.
- **Malformed denial message fixed.** A gate denial with `MinAutonomy=null` and a reason other than
  `always-human` rendered "requires minimum autonomy , above the current autonomy level 70". The
  composition now lives in `InlineToolLoopRunner.ComposeDenialMessage` (internal for tests); the null
  case omits the threshold clause ("is not permitted at the current autonomy level 70"). Message shape
  pinned for null, non-null, and always-human decisions in `ToolLoopAutonomyGateSeamTests`.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-25 | 1.0.0   | Initial story creation | Claude |
| 2026-07-29 | 1.0.1   | Review follow-ups closed: git-grading hole recorded in catalog; non-enforceable + parallel-path gate tests added; null-threshold denial message fixed | Claude |
