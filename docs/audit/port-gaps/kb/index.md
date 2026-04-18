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
