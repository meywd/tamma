# Story 42-6 §0 — the MCP client: port `packages/mcp-client/` or adopt the C# SDK

**Date**: 2026-07-30
**Status**: Accepted, with one unproved prerequisite (P1 below) that blocks the dependency landing
**Deciders**: Claude, under the product owner's standing "handle all remaining" authorization.
Story 32-21 §7.3 had already recommended this outcome; this record decides it, and adds the
dependency evidence that recommendation did not have.

## Context

Epic 42 Open Question 3 / Story 42-6 §0 / that story's implementation-plan **D3** all record the same
question as open: build Part B's governed MCP catalog on a **C# port of `packages/mcp-client/`**, or on
an **MCP C# SDK**. Every document says the effort estimate for Part B is meaningless until it is
answered, and that the answer must be recorded in `.dev/decisions/` before Part B opens. This is that
record.

Proxying through the TS sidecar was already ruled out on evidence and is not reopened: it puts tool
execution behind an HTTP hop on the far side of the process the tool envelope does not cover, which
re-creates in Part B exactly the bypass Part A deletes, and keeps a second governance path alive
permanently.

## Decision

**Adopt the official C# SDK — the `ModelContextProtocol.Core` package — behind an `IMcpTransport` /
`IMcpConnectionPool` seam, and delete `packages/mcp-client/` outright.**

The protocol transport is commodity and it churns; owning it is cost with no differentiation. The
differentiated work — per-tenant enablement, cabinet-held credentials, allowlist ∩ agent-allowed,
sanitization hooks, rate limiting, audit — is ours regardless of which transport sits underneath, and
stays in `Tamma.Api` where the 42-1–42-5 + Epic 43 envelope already lives. The adapter seam keeps the
choice reversible per transport without disturbing the catalog or the security layer.

`packages/mcp-client/` is **deleted, not ported**. It is the behavioural reference for the layers we
re-implement in C# (`security/validator.ts`, `security/rate-limiter.ts`, `audit.ts`, `cache/`), and git
history preserves it for that purpose. Every prior document agrees that leaving it orphaned — unbuilt,
zero dependents, looking live — is strictly the worst of the three outcomes.

## Evidence gathered for this decision (verified 2026-07-30)

**E1 — the LOC figure in every existing document is wrong.** `packages/mcp-client/src/**/*.ts` is
**9,662** lines of non-test source; 15,121 including `__tests__`. The figure **7,865** appears in the
42-6 story §(a) and §0, in that story's cost note, and twice in `epic-42/README.md` plus its Open
Question 3. The implementation plan already caught this as **X1**; the story and README still carry the
wrong number, and every port-side cost estimate keyed on it understates the port by ~23%. Corrected in
this pass.

**E2 — the SDK is real, stable, current, and targets our framework.** `modelcontextprotocol/csharp-sdk`
is the official C# SDK, maintained in collaboration with Microsoft, Tier 1 in the MCP ecosystem; stable
1.0 shipped March 2026 against the 2025-11-25 spec. Latest **2.0.0 (2026-07-28)**, stable, not
prerelease; last 1.x stable **1.4.1 (2026-07-09)**; ~20.9M downloads across the family. Target
frameworks include **net8.0**, which is ours.

**E3 — the right package is `ModelContextProtocol.Core`, not `ModelContextProtocol`.** `.Core` is the
minimal-dependency client/low-level-server package — the client is all Part B needs. The umbrella
`ModelContextProtocol` package adds `Microsoft.Extensions.Hosting.Abstractions` and
`Microsoft.Extensions.Caching.Abstractions` for DI/hosting sugar we do not need.
`ModelContextProtocol.AspNetCore` is for *hosting* an MCP server and is out of scope.

**E4 — one transport in the TS client has no spec counterpart.** The spec transports are **stdio** and
**Streamable HTTP** (which replaces the legacy HTTP+SSE); the SDK covers both. `packages/mcp-client/`
also ships a 246-line **WebSocket** transport that current MCP does not define. Porting the TS client
would port a transport the protocol no longer has.

**E5 — the prerequisite nobody had recorded: adopting the SDK forces a major-version bump on
`Microsoft.Extensions.AI.Abstractions`, underneath Elsa.** On net8.0 **both** candidate versions declare
the 10.x line:

| Dependency (net8.0 group) | Core 1.4.1 | Core 2.0.0 | `Tamma.Api` resolves today |
|---|---|---|---|
| `Microsoft.Extensions.AI.Abstractions` | 10.5.2 | 10.8.3 | **9.5.0** |
| `Microsoft.Extensions.Logging.Abstractions` | 10.0.7 | 10.0.10 | **9.0.11** |
| `System.IO.Pipelines` | 10.0.7 | 10.0.10 | **9.0.11** |
| `System.Net.ServerSentEvents` | 10.0.7 | 10.0.10 | *(absent)* |

The 9.5.0 is not incidental. It arrives by
`Tamma.Api` → `Tamma.Activities` → **`Elsa.Agents.Core 3.5.3`** → `Microsoft.SemanticKernel 1.57.0` →
`Microsoft.Extensions.AI 9.5.0` (`Microsoft.Extensions.AI.OpenAI 9.5.0-preview.1.25265.7` and
`Microsoft.Extensions.VectorData.Abstractions 9.6.0` bind the same 9.5.0). **Elsa.Agents is
load-bearing** — `Tamma.Activities/LlmCall/ResolveAgentConfigActivity.cs` and
`Tamma.ElsaServer/AgentSeeder.cs` use it — so it cannot simply be dropped to break the chain. No
Tamma `.cs` file references `Microsoft.Extensions.AI` directly and no `.csproj` names Semantic Kernel;
the whole graph is transitive, which is exactly why this was invisible until now.

Consequence: adding the SDK anywhere in the `Tamma.Api` graph makes NuGet's highest-wins resolution
hand **Semantic Kernel 1.57.0 a major-version-newer `Microsoft.Extensions.AI.Abstractions` than it was
compiled against**. Restore stays quiet; the failure, if it comes, is at type load or first use.

**Pinning does not avoid it** — 1.4.1 and 2.0.0 both require the 10.x line, so there is no older SDK to
retreat to. **A separate assembly does not avoid it either**: `Tamma.Api` is one process with one
dependency graph, so isolating the MCP client behind a project boundary changes nothing about which
assembly version loads. Saying otherwise would be the comfortable answer, not the true one.

## P1 — the prerequisite that gates the dependency

**The SDK package reference does not merge until a full, unfiltered `Tamma.Api.Tests` run proves
Semantic Kernel still loads and Elsa's agent path still works under the 10.x Extensions line.** A
filtered run is not acceptable evidence: a filter that skips the LLM-call and agent-config suites is
precisely the filter that would hide this. Build success is not evidence either — the break is a
load-time one.

If P1 fails, in order of preference:

1. **Upgrade Elsa** to a 3.x that pins a Semantic Kernel built against the 10.x line, in the same
   change. Cost lands on the Elsa upgrade, not on MCP.
2. **Hand-roll a narrow client** covering stdio + Streamable HTTP only, against the `IMcpTransport`
   seam. This is option (a) scoped to two transports and no WebSocket — a small fraction of 9,662 LOC,
   because the rate limiter, path validator, audit log and connection pool it would otherwise carry are
   already provided by the 42-1–42-5 + Epic 43 envelope.

Option 2 is the reason the seam is non-negotiable: it must be possible to swap the transport
implementation without touching the catalog or the security layer.

## Rejected

**Port `packages/mcp-client/` to C# (option a, in full).** 9,662 LOC of protocol and transport we would
then own and keep in step with a spec that has already replaced one of its transports since this client
was written — and one of the three transports it implements (WebSocket, E4) is not in the spec at all.
Several of its value-adds (rate limiter, path validator, resource monitor, audit log, connection pool)
duplicate the 42-1/42-4/42-5 + Epic 43 envelope outright, so a faithful port would import governance
that then has to be deleted to avoid two enforcement paths. Retained as the P1 fallback, scoped down.

**Host the TS client as an API-managed sidecar (option b).** Adds a process and an IPC hop *inside* the
trust boundary, puts tenant credentials in one more place, and fights the one-process API model. No
security benefit for the surface it adds.

**Proxy through the existing TS sidecar.** Ruled out on evidence before this decision; see Context.

## Consequences

- 42-6 §0 closes; Part B's effort estimate becomes meaningful and is keyed to "SDK + our security
  layer", not to a 9,662-LOC port.
- Part A (retire the ungoverned `/api/kb/mcp/*` invoke surface) is unaffected — it depends on nothing
  here and stays P0.
- `packages/mcp-client/` is deleted when Part B lands, not before; until then it stays orphaned but is
  now documented as dead rather than pending.
- The `IMcpTransport` seam is a hard requirement of this decision, not a nicety.
- P1 is a real risk carried into Part B's first task, with a stated fallback rather than an assumption.
