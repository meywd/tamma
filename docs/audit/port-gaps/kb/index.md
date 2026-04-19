# Knowledge Base Port-Gap Findings

**Scope**: `kb` — six Knowledge Base service surfaces (Index, VectorDB, RAG, MCP, Context, Analytics) exposed as 30 `/api/kb/*` routes.
**Status**: Every surface is 🟡 — sidecar-wired but returns empty / zero / literal-stub state in production.
**Estimated total port effort**: ~20-30h (composition root wiring + strict-mode fix + end-to-end compose validation).

## Architectural context

Unlike other audit scopes, KB is **not a TS → C# port**. Epic 19 deleted the TS API's KB routes and services, but re-exposed them through a new TypeScript sidecar (`packages/intelligence-server/`) that the C# API delegates to over HTTP. The C# layer is a contract-faithful passthrough; the gap is in the sidecar's composition root, which never constructs the real ChromaDB / embedder / RAG / MCP backends.

Two consequences:

1. The deleted TS API (9e9a57c~1) already shipped with **the same empty-state fallback** — this bug predates Epic 19. The sidecar preserves that fallback (sometimes as literal "stub" strings, see #002), so the user-visible regression from the TS → C# cut is zero. The underlying feature gap is ~2 years old and tracked by Epic 6 stories that were never completed.
2. The C# integration tests (`KbEndpointsIntegrationTests.cs`) exercise only the C# → sidecar contract with a mocked `HttpClient`. They provide zero signal on whether the sidecar's adapters are wired to real backends. See #016.

## Findings

| #   | Title                                                       | Severity | Classification                  |
|-----|-------------------------------------------------------------|----------|---------------------------------|
| 001 | Sidecar composition root never constructs real backends     | P1       | Incomplete                      |
| 002 | User-visible literal "(stub …)" strings in API responses    | P1       | Behavioral drift (vs TS throw)  |
| 003 | CHROMADB_URL / OPENAI_API_KEY / EMBEDDING env vars unread   | P1       | Incomplete                      |
| 004 | `adapters.ts` factories never called anywhere in source     | P1       | Incomplete                      |
| 005 | `server.ts:210` starts server without `IntelligenceServicesBundle` | P1 | Incomplete                 |
| 006 | `IndexManagementService.triggerIndex` returns stub string   | P1       | Behavioral drift                |
| 007 | `VectorDbManagementService.upsert` returns stub string      | P1       | Behavioral drift                |
| 008 | `VectorDbManagementService.delete` returns stub string      | P1       | Behavioral drift                |
| 009 | `McpManagementService.startServer/stopServer` return stubs  | P1       | Behavioral drift                |
| 010 | `McpManagementService.invokeTool` always errors             | P1       | Not-yet-implemented             |
| 011 | `AnalyticsService` returns zero state for all three routes  | P2       | Not-yet-implemented             |
| 012 | `ContextTestingService`: empty history, in-process feedback | P2       | Not-yet-implemented             |
| 013 | `RagManagementService.query` returns blank answers          | P2       | Not-yet-implemented             |
| 014 | `@tamma/intelligence` strict-mode build errors blocked      | P2       | Blocker for composition root    |
| 015 | `packages/intelligence/` real impls exist but never imported| P2       | Dead wiring                     |
| 016 | `KbEndpointsIntegrationTests` mock the client, skip backends| P2       | Test gap                        |

## Not-a-gap

- All 30 C# endpoints correctly map path/verb/body to `IIntelligenceHttpClient` calls. Contract layer is fine.
- `IntelligenceHttpClient` correctly handles sidecar 5xx / timeout with a `degraded=true` envelope so the dashboard can render a banner.
- Docker Compose correctly wires `chromadb` → `intelligence-server` with dependency health checks.

## Remediation order

1. Fix `@tamma/intelligence` strict-mode errors (#014 — blocks everything else).
2. Wire composition root in `server.ts` entrypoint (#001, #005) calling adapter factories (#004) against env vars (#003).
3. Once real backends flow, the user-visible stub strings (#002, #006-#010) vanish naturally.
4. Add analytics/feedback persistence (#011, #012).
5. Replace mock-client integration tests with at least one live compose smoke test (#016).

## References

- Audit summary: `/tmp/tamma-audit/35-kb.md`
- C# endpoints: `apps/tamma-elsa/src/Tamma.Api/Endpoints/KbEndpoints.cs`
- C# HTTP client: `apps/tamma-elsa/src/Tamma.Api/Services/KnowledgeBase/IntelligenceHttpClient.cs`
- Sidecar entrypoint: `packages/intelligence-server/src/server.ts`
- Unused adapters: `packages/intelligence-server/src/adapters.ts`
- Real impls: `packages/intelligence/src/{vector-store,rag,indexer,context,knowledge-base}/`
- TS pre-delete snapshot: `git show 9e9a57c~1:packages/api/src/{routes,services,schemas}/knowledge-base/`

## Remediation status (2026-04-18)

**All 16 findings: Deferred — out of scope for the C# port-gap pass.**

| #   | Status   | Reason                                                      |
|-----|----------|-------------------------------------------------------------|
| 001 | Deferred | TS sidecar composition root — no C# analog                  |
| 002 | Deferred | Stub strings emitted from TS sidecar service classes        |
| 003 | Deferred | Sidecar (Node) env vars; C# already reads its own scoped config |
| 004 | Deferred | TS adapter factories in `packages/intelligence-server/`     |
| 005 | Invalid  | TypeScript-only call site (`server.ts:210`)                 |
| 006 | Deferred | TS sidecar service                                          |
| 007 | Deferred | TS sidecar service                                          |
| 008 | Deferred | TS sidecar service                                          |
| 009 | Deferred | TS sidecar service                                          |
| 010 | Deferred | TS sidecar service + missing MCP discovery contract         |
| 011 | Deferred | TS sidecar service                                          |
| 012 | Deferred | TS sidecar service + sidecar Postgres schema                |
| 013 | Deferred | TS sidecar service                                          |
| 014 | Invalid  | `@tamma/intelligence` TypeScript strict-mode debt           |
| 015 | Deferred | TS sidecar import wiring                                    |
| 016 | Deferred | Mocked HTTP boundary is correct for the C# layer; live-compose smoke is forbidden by the remediation pass constraints AND blocked on 001 |

### Why this scope is not a C# port-gap scope

The Knowledge Base subsystem is, by Epic 19's explicit architectural
decision, **not a C# port**. It is delegated to a TypeScript sidecar
(`packages/intelligence-server/`) that the C# API talks to over HTTP. The
C# port surface for KB is exactly:

1. `apps/tamma-elsa/src/Tamma.Api/Endpoints/KbEndpoints.cs` — 30
   forwarding handlers (verified 1-to-1 against the sidecar contract).
2. `apps/tamma-elsa/src/Tamma.Api/Services/KnowledgeBase/IntelligenceHttpClient.cs` —
   typed HTTP client with degraded-payload fallback on 5xx/timeout.
3. `apps/tamma-elsa/src/Tamma.Api/Extensions/KnowledgeBaseServiceCollectionExtensions.cs` —
   reads `IntelligenceServer:Url` and `IntelligenceServer:TimeoutSeconds`.
4. `apps/tamma-elsa/tests/Tamma.Api.Tests/KnowledgeBase/` — integration
   tests exercising the C# → sidecar contract with a mocked
   `HttpMessageHandler` (the correct boundary for a passthrough layer).

The audit explicitly classifies (1)-(3) as "Not-a-gap" and confirms
"Contract layer is fine". All 16 findings target work outside this
surface area: the TS sidecar's own composition root, its services, its
adapter factories, and its strict-mode build debt. A C# port-gap pass
cannot remediate any of them without leaving the C# port and rewriting
TypeScript.

### How to actually close these findings

The shortest path is a dedicated TypeScript work item against
`packages/intelligence-server/` covering findings 001/003/004/005/014/015
together (one composition-root chain, ~13-22h). Once landed,
findings 002/006/007/008/009 become dead code, and 010/011/012/013/016
fall out as ~10-13h of follow-up.

### Build / test impact of this pass

No C# code changes were made. Build remains green
(`Tamma.sln` 0 errors, 6 warnings — all pre-existing CVE warnings on
`MailKit` and `System.Text.Json` 8.0.0 unchanged from baseline). Test
count baseline preserved: 1608 passing (7 + 882 + 719) across
`Tamma.Core.Tests`, `Tamma.Activities.Tests`, `Tamma.Api.Tests`.
