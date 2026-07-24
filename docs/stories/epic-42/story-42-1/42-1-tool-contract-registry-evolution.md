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
catalog can grow per deployment/tenant and via MCP without a code change per tool.

## Priority

P0 / Wave 0 — **the contract every other Epic 42 story builds on.** No gating (42-3), secret binding
(42-4), audit (42-5), MCP path (42-6), or tool family (42-7/8/9) can exist until a tool can *declare*
what it is and the registry can hold tools added at runtime.

## The gap (READ FIRST)

`IToolExecutor` (`Tamma.Activities/LlmCall/Tools/IToolExecutor.cs`) is `{ ToolName, Description,
InputSchema, ExecuteAsync }` and **must never throw** (all failure is `ToolExecutionResult
{ Success = false }`). It carries **no governance metadata** — nothing says who may call the tool, at
what autonomy, what credential it needs, or how destructive it is.

`ToolExecutorRegistry` (`ToolExecutorRegistry.cs`) is populated **once** in its constructor from
`IEnumerable<IToolExecutor>` injected by DI (the six built-ins wired in `Tamma.Api/Program.cs` ~L753–763
via `AddSingleton<IToolExecutor, …>`). `IToolExecutorRegistry` exposes only read paths — `GetExecutor`,
`IsAllowed`, `GetAll`, `GetAllowed`. There is **no** `Register`/`Unregister`: the catalog is frozen at
startup.

## Scope

1. **`ToolDescriptor` on the executor surface.** Introduce a `ToolDescriptor` record and surface it from
   `IToolExecutor` via a new `ToolDescriptor Descriptor { get; }` member (a C# **default interface
   member** returning the fail-safe default, so the six built-ins and any existing external
   implementation compile unchanged, then are annotated explicitly).

   ```csharp
   public enum ToolCategory { Native, Mcp, ProviderAbstracted }
   public enum ToolPermissionClass { ReadOnly, Mutating, Command, Destructive }

   public sealed record SecretRequirement(
       SecretPurpose Purpose,   // Epic 29 taxonomy: ApiKey, SigningKey, …
       string Name,             // logical secret name, resolved to a SecretRef per mode by 42-4
       bool Required);          // hard-fail vs. best-effort

   public sealed record ToolDescriptor(
       ToolCategory Category,
       ToolPermissionClass PermissionClass,
       int AutonomyFloor,            // 70–100
       SecretRequirement? RequiredSecret,
       bool Suspends);
   ```

   The **fail-safe default** (returned by the default interface member) is
   `new ToolDescriptor(Native, Destructive, 100, null, false)` — **deny-by-default**: an un-annotated
   tool is treated as the most dangerous, never silently granted. `AutonomyFloor` is validated to
   `[70,100]` at registration (a floor of 70 = allowed at the supervised baseline).

2. **Annotate the six built-ins.** Each declares a real descriptor:

   | Tool | PermissionClass | AutonomyFloor | RequiredSecret |
   |---|---|---|---|
   | `SearchCodeTool` | `ReadOnly` | 70 | none |
   | `FileReadTool` | `ReadOnly` | 70 | none |
   | `FileWriteTool` | `Mutating` | 70 | none |
   | `GitOperationsTool` | `Mutating` | 75 | none (platform creds resolved elsewhere today) |
   | `RunTestsTool` | `Mutating` | 70 | none |
   | `ShellExecuteTool` | `Command` | 85 | none (stays `ActionGate`-gated) |

   These floors are defaults, overridable per principal by the 42-2 binding store.

3. **Dynamic registration seam.** Extend `IToolExecutorRegistry` with `Register(IToolExecutor)` /
   `Unregister(string toolName)` and make `ToolExecutorRegistry`'s backing map thread-safe
   (`ConcurrentDictionary`, case-insensitive as today). The DI-seeded set remains the base layer;
   dynamic registrations (42-6 MCP, per-deployment/per-tenant) layer on top. Duplicate-name handling
   keeps today's "keep first, warn" for the DI seed but a dynamic `Register` of an existing name is an
   explicit **replace-or-reject** decision (default reject; MCP refresh uses `Unregister`+`Register`).
   Registry read paths (`GetAll`/`GetAllowed`) see the merged set.

4. **Descriptor reaches the LLM tool definition.** `ResolveToolsActivity` / `ResolvedTool` already
   builds the tools array the LLM sees. Thread the descriptor through so downstream stories can read
   `PermissionClass`/`AutonomyFloor` at resolve time (42-3) — this story only *plumbs* it; 42-3 acts on
   it. No behavior change to the six built-ins' availability in this story.

## Acceptance Criteria

1. `IToolExecutor.Descriptor` exists as a default interface member returning the deny-by-default
   descriptor; the interface still compiles with only the four original members implemented (a
   regression test implements a bare `IToolExecutor` and asserts its resolved descriptor is
   `Destructive`/floor `100`).
2. All six built-ins declare explicit descriptors matching the table; a test asserts each tool's
   `PermissionClass`/`AutonomyFloor`.
3. `AutonomyFloor` outside `[70,100]` is rejected at registration with a loud error (not clamped).
4. `IToolExecutorRegistry.Register`/`Unregister` exist; a test registers a tool at runtime, resolves it
   via `GetExecutor`/`GetAll`, unregisters it, and asserts it is gone — with no restart.
5. Concurrent `Register`/`GetAll` calls are race-free (the backing store is concurrent); a stress test
   asserts no lost/duplicated entries.
6. A dynamic `Register` of a name already present rejects by default (test) and succeeds via
   `Unregister`+`Register` (test).
7. `ResolvedTool` carries the descriptor through `ResolveToolsActivity`; a test asserts a resolved
   built-in exposes its `PermissionClass`. **No change** to which of the six tools an unrestricted
   caller sees (back-compat: an empty allowlist still yields all six).

## Events

None new in this story (the `TOOL.*` DCB family is 42-5). Registry `Register`/`Unregister` log at
INFO with the tool name and category (never secrets).

## Single-user vs SaaS

No principal-scoped behavior here — the descriptor is a **static property of the tool**, identical in
both modes. Per-principal *overrides* of a tool's floor/enablement are 42-2; per-mode *resolution* is
42-3. This story is mode-agnostic by construction.

## Dependencies

- **Epic 29** `SecretPurpose` enum (for `SecretRequirement.Purpose`) — exists.
- **Unblocks:** 42-2 (store keys off descriptor names), 42-3 (reads `PermissionClass`/`AutonomyFloor`),
  42-4 (reads `RequiredSecret`), 42-5 (tags events with `PermissionClass`), 42-6 (registers via the new
  seam), 42-7/8/9 (each declares a descriptor).

## Risks

- **Default interface member reaching the wire.** If the LLM tools array is built off `GetAll()` and a
  tool forgets its descriptor, deny-by-default could make it silently un-callable. Mitigation: a
  startup drift test asserts every DI-registered tool declares an explicit (non-default) descriptor —
  fail-loud at boot, matching `PromptFileLoader`'s "refuse to start on a missing cell" posture.
- **Registry mutability + Elsa engine lifetime.** `ToolExecutorRegistry` is a singleton; dynamic
  registration must not leak across tenants. Scope tenant-specific registrations behind 42-6's
  per-tenant view rather than mutating the global singleton (flagged for 42-6).

## Estimated Effort

Medium (contract + registry change touching the hottest path; heavy on tests, light on new surface).
~3–4 days.
</content>
