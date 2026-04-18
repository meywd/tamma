# Finding 016: `KbEndpointsIntegrationTests` mock the HTTP client — no sidecar / backend coverage

**Scope**: kb
**Severity**: P2 (test gap — explains why production bug went undetected)
**Status**: Incomplete (contract-level coverage only; backend paths untested)
**Estimated port effort**: 2-3h (add real-compose end-to-end test)

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/__tests__/routes/knowledge-base/`.

The deleted TS API had unit tests for each KB service that **injected fake dependencies** (fake `ICodebaseIndexer`, fake `IVectorStoreService`, etc.) — so tests always exercised the "real dep present" branch. The "real dep absent" branch (the production path) was never covered by a test.

There were no integration tests that spun up a real ChromaDB or OpenAI embedder to verify end-to-end semantics.

- Dependencies: none (this is about test-suite coverage).
- Tests: `packages/api/src/__tests__/services/knowledge-base/*.test.ts` — unit tests with injected fakes.

## 2. What's in C#

### C# side — 30 routes have integration tests, all mock the HTTP client

```csharp
// apps/tamma-elsa/tests/Tamma.Api.Tests/KnowledgeBase/KbEndpointsIntegrationTests.cs:34-63 (current)
[SetUp]
public async Task SetUp()
{
    await ApiTestFixture.ResetDatabaseAsync();
    _handler = new SharedSidecarHandler();

    _factory = ApiTestFixture.Factory.WithWebHostBuilder(b =>
        b.ConfigureTestServices(s =>
        {
            // Register the typed client with the test handler. This
            // replaces any previous wiring because AddHttpClient<T, TImpl>
            // with the same name overwrites the last TryAddTransient.
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["IntelligenceServer:Url"] = "http://intelligence-server:4100",
                })
                .Build();
            s.AddKnowledgeBaseServices(config);
            s.AddHttpClient<IIntelligenceHttpClient, IntelligenceHttpClient>(
                    KnowledgeBaseServiceCollectionExtensions.HttpClientName,
                    client =>
                    {
                        client.BaseAddress = new Uri("http://intelligence-server:4100");
                    })
                .ConfigurePrimaryHttpMessageHandler(() => _handler);
        }));

    _client = _factory.CreateClient();
}
```

Each test records a canned sidecar response and asserts the C# path → sidecar path mapping:

```csharp
// apps/tamma-elsa/tests/Tamma.Api.Tests/KnowledgeBase/KbEndpointsIntegrationTests.cs:75-86 (current)
[Test]
public async Task GetIndexStatus_ForwardsToSidecar_AndReturnsPayload()
{
    _handler.Respond("/kb/index/status", HttpStatusCode.OK, new { status = "idle", indexed = 7 });

    var resp = await _client.GetAsync("/api/kb/index/status");

    resp.StatusCode.Should().Be(HttpStatusCode.OK);
    var body = await resp.Content.ReadAsStringAsync();
    body.Should().Contain("\"indexed\":7");
    _handler.LastPath.Should().Be("/kb/index/status");
}
```

The test verifies:
- C# `/api/kb/index/status` calls sidecar `/kb/index/status` (path mapping).
- C# forwards the sidecar body verbatim.
- HTTP status is propagated.

The test does NOT verify:
- The sidecar is actually running.
- The sidecar has a real backend wired.
- `{ "indexed": 7 }` is achievable from a real sidecar in any scenario (the canned fixture invents the number).

### Sidecar side — unit tests inject fake adapters

```
packages/intelligence-server/src/__tests__/services/*.test.ts
```

Each service is unit-tested with object mocks satisfying `IVectorStoreAdapter` / `IRagPipeline` / etc. The `null` fallback paths are also tested — asserting the stub strings and empty responses. But no test wires the **real** `ChromaVectorStore` or `RAGPipeline` from `packages/intelligence/`.

- Dependencies: `SharedSidecarHandler` in-process recorder (replaces `HttpMessageHandler`).
- Tests: 30 C# tests + ~100 sidecar unit tests. Neither layer exercises a real ChromaDB, OpenAI, MCP server, or cost tracker.

## 3. The gap

- TS did: same — TS unit tests used injected fakes. Production composition was never tested because there was no composition root.
- C# + sidecar does: worse — two test layers that each "pass" while never testing that the two layers integrate against real backends. The C# layer passes because it only validates contract mapping with a recorded fake. The sidecar layer passes because its null-fallback is the "expected" behavior per the test assertions.

Consequence: **findings #001 - #015 would all have been caught by even one end-to-end test that started a real ChromaDB testcontainer, let the sidecar boot against it, and issued `POST /api/kb/vector-db/upsert`**. No such test exists.

For a developer working on KB features:
- Full green test suite.
- No signal that `{ "message": "Vectors upserted (stub — no store configured)" }` is being served in production.

Error paths:
- The test suite actively asserts on the stub strings, which means any fix to #002 will fail tests and surface the underlying gap — a small positive side effect.

## 4. Gap from stories

No story directly specifies "integration tests use real backends" — but the CLAUDE.md § "Testing Strategy" prescribes:

> **Integration Tests**:
> - Real API calls to test providers and platforms
> - Requires test credentials: `ANTHROPIC_API_KEY_TEST`, `GITHUB_TOKEN_TEST`, `GITLAB_TOKEN_TEST`
> - Test repositories: `tamma-test-github`, `tamma-test-gitlab`

The same principle should apply to KB — real ChromaDB via testcontainers, real embeddings via a sandboxed OpenAI key.

Story alignment:
- [x] Matches TS behavior (both skip real-backend integration)
- [x] Matches C# behavior (same)
- [ ] Describes a third behavior
- [x] No story — implicit CLAUDE.md spec, not carved into a dedicated story.

## 5. Status

- **Classification**: Incomplete (test gap). Contract layer is covered; integration and backend layers are not.
- **What's needed to finish**:
  1. Add `packages/intelligence-server/tests/integration/chromadb.integration.test.ts` using `@testcontainers/chromadb` (or equivalent).
  2. Add `apps/tamma-elsa/tests/Tamma.Api.Tests/KnowledgeBase/KbSmokeTests.cs` that runs against a full docker-compose (opt-in via env flag so it doesn't run in every CI loop).
  3. Add CI job `kb-compose-smoke` that spins up `chromadb + intelligence-server + tamma-api`, issues upsert → search → assert → cleanup.
- **Is it "just a stub" or is scope missing?** Test scope was never written down; this is a missing gate, not drift.
- **Blockers**:
  - Most KB integration tests are meaningless until #001 lands (no composition root → no real backend to test against). So this work blocks on #001.
  - Testcontainers-Chroma or running ChromaDB in CI requires runner compute budget.

## Remediation

- Files to modify:
  - CI config (`.github/workflows/*.yml`) — add `kb-compose-smoke` job.
- Files to create:
  - `packages/intelligence-server/tests/integration/chromadb.integration.test.ts`
  - `packages/intelligence-server/tests/integration/rag.integration.test.ts`
  - `apps/tamma-elsa/tests/Tamma.Api.Tests/KnowledgeBase/KbSmokeTests.cs`
- Tests to add (listed above):
  - Upsert → search round-trip against real ChromaDB.
  - RAG query with seeded documents returns non-empty answer.
  - MCP tool invoke against `@modelcontextprotocol/server-filesystem` returns real content.
  - Analytics endpoint returns non-zero after a simulated workload.
- Estimated effort: 2-3h
  - Testcontainers setup + first chromadb test: 1h
  - RAG + MCP smoke tests: 1h
  - CI job + skip semantics: 30m-1h

## References

- C# integration tests: `apps/tamma-elsa/tests/Tamma.Api.Tests/KnowledgeBase/KbEndpointsIntegrationTests.cs`
- Sidecar unit tests: `packages/intelligence-server/src/__tests__/services/`
- Real impls: `packages/intelligence/src/vector-store/providers/chromadb.ts`, `packages/intelligence/src/rag/rag-pipeline.ts`
- CLAUDE.md section: "Testing Strategy"
- Related findings: #001, #006-#013 — all would have been caught by the missing tests this finding describes.
