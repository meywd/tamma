# Finding 003: CHROMADB_URL / OPENAI_API_KEY / EMBEDDING env vars unread by sidecar

**Scope**: kb
**Severity**: P1 (no path from infrastructure config to running backend)
**Status**: Incomplete (env vars declared in compose, never read in code)
**Estimated port effort**: 1-2h (trivial once composition root lands — see #001)

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/services/knowledge-base/`.

The deleted TS API did not read ChromaDB / OpenAI env vars either — it relied on the caller passing in pre-constructed `ICodebaseIndexer`, `IVectorStoreService`, etc., through `createKBServices(deps)`. The env-var → real-backend wiring was expected to happen at the Fastify server bootstrap (e.g. `packages/api/src/index.ts` or a deploy harness), not inside the service layer.

In practice: the bootstrap code never constructed the deps either — same gap as the sidecar. This is a pre-existing bug across the TS → C# boundary.

- Dependencies: none (this is about the absence of env-var reads).
- Tests: none exercised the env-var wiring.

## 2. What's in C#

### docker-compose declares the env vars

```yaml
# docker/docker-compose.yml (current)
intelligence-server:
  build:
    context: ..
    dockerfile: packages/intelligence-server/Dockerfile
  environment:
    INTELLIGENCE_PORT: "4100"
    INTELLIGENCE_HOST: "0.0.0.0"
    CHROMADB_URL: http://chromadb:8000
    LOG_LEVEL: ${INTELLIGENCE_LOG_LEVEL:-info}
  depends_on:
    chromadb:
      condition: service_healthy
```

### Sidecar source reads only INTELLIGENCE_PORT / INTELLIGENCE_HOST / LOG_LEVEL

The entrypoint reads exactly three env vars:

```typescript
// packages/intelligence-server/src/server.ts:197-206 (current)
export async function startServer(opts: {
  port?: number;
  host?: string;
  services?: IntelligenceServicesBundle;
} = {}): Promise<FastifyInstance> {
  const app = await buildServer({ ...(opts.services ? { services: opts.services } : {}) });
  const port = opts.port ?? Number.parseInt(process.env['INTELLIGENCE_PORT'] ?? '4100', 10);
  const host = opts.host ?? process.env['INTELLIGENCE_HOST'] ?? '0.0.0.0';
  await app.listen({ port, host });
  return app;
}
```

Grep confirms zero references to `CHROMADB`, `OPENAI_API_KEY`, or `EMBEDDING` anywhere in the sidecar source:

```
$ grep -rn 'CHROMADB\|OPENAI_API_KEY\|EMBEDDING' packages/intelligence-server/src/
# (no output — these strings do not appear)
```

The only mention of `CHROMADB_URL` in the whole repo's runtime code is the docker-compose declaration; nothing reads it.

- Dependencies: none; this is an absence.
- Tests: `KbEndpointsIntegrationTests.cs` mocks the HTTP client (see #016) — never validates that sidecar connects to real ChromaDB.

## 3. The gap

- TS did: no env-var reads; expected an external harness to pass deps in. That harness was never built.
- C# + sidecar does: same — no env-var reads. Infra declared `CHROMADB_URL` but the sidecar discards it.

For an operator who has:
- ChromaDB running at `http://chromadb:8000` (docker-compose healthy check green)
- `OPENAI_API_KEY` set in `.env` for the embedder
- Hit `/api/kb/vector-db/status`

TS API response: `{"degraded":true,"results":[], ...}` (via sidecar → degraded envelope from C#)
C# + sidecar response: `{"status":"not_configured"}` — correct shape but with the wrong reason. The operator assumes they forgot to set a flag somewhere, not that the app has no wiring code to read it.

Error paths:
- TS: —
- C# + sidecar: `GET /kb/vector-db/status` → `{"status":"not_configured"}`. No log warning that `CHROMADB_URL` was set but ignored.

In production:
- Operator correctly sets every documented env var. Sidecar still behaves as if none were set. No diagnostic helps them discover this.

## 4. Gap from stories

`docs/stories/epic-6/story-6-2/6-2-vector-database-integration.md` AC2:

> **AC2: ChromaDB Integration (Default)**
> - [ ] Implement ChromaDB adapter (embedded mode)
> - [ ] Support persistent storage
> - [ ] Support collection management (create, delete, list)
> - [ ] Handle connection pooling

The ChromaDB adapter exists in `packages/intelligence/src/vector-store/providers/chromadb.ts`. The composition-root plumbing that reads `CHROMADB_URL` and hands it to that adapter does not.

Epic 6 does not explicitly spell out the env-var contract, but the implication (adapter supports embedded mode AND remote) requires some bootstrap that selects between modes based on config.

Story alignment:
- [ ] Matches TS behavior (same gap in both — but TS was never prod)
- [x] Matches C# behavior (C# side is correct; sidecar env-var gap is the issue)
- [ ] Describes a third behavior
- [x] Partial — story says "ChromaDB as default" but doesn't spec the env-var loader. Must backfill.

## 5. Status

- **Classification**: Incomplete — the `IVectorStoreAdapter` type and the real `chromadb.ts` provider exist; the glue that says "read `CHROMADB_URL` → build `ChromaVectorStore` → wrap via `adaptVectorStore()` → pass to `startServer`" is missing entirely.
- **What's needed to finish**:
  1. Add a `loadConfigFromEnv()` helper to the sidecar that validates required env vars and returns a typed config.
  2. Use that config to instantiate real backends at the entrypoint.
  3. Log at startup which backends were successfully wired (so the absence is visible in ops).
- **Is it "just a stub" or is scope missing?** Scope is partially spec'd (real provider exists) but the env-var contract is not written down. Minor spec gap.
- **Blockers**: #001 (entrypoint composition root), #014 (strict-mode errors blocking `@tamma/intelligence` import).

## Remediation

- Files to modify:
  - `packages/intelligence-server/src/server.ts` — wire env-var reads into `startServer()`.
  - `packages/intelligence-server/src/adapters.ts` — implement the `createVectorStoreFromEnv()` / `createRagPipelineFromEnv()` factories the JSDoc alludes to.
  - `docker/docker-compose.yml` — add `OPENAI_API_KEY`, `EMBEDDING_MODEL` to `intelligence-server.environment`.
- Files to create:
  - `packages/intelligence-server/src/config.ts` — env-var schema + validation (Zod), similar to other `@tamma/*` packages.
- Tests to add:
  - Unit: `config.test.ts` — missing `CHROMADB_URL` → throws at startup; malformed URL → throws; all vars present → returns parsed config.
  - Integration: `intelligence-server` boots with env vars pointing at testcontainers ChromaDB and responds `"status":"ready"` on `/kb/vector-db/status`.
- Estimated effort: 1-2h
  - `config.ts` + tests: 1h
  - Docker compose update + end-to-end smoke: 0.5-1h

## References

- Sidecar entrypoint: `packages/intelligence-server/src/server.ts:197-207`
- Sidecar adapters (factory stubs): `packages/intelligence-server/src/adapters.ts:12-18`
- docker-compose: `docker/docker-compose.yml:82-102`
- Real impl: `packages/intelligence/src/vector-store/providers/chromadb.ts`
- Story: `docs/stories/epic-6/story-6-2/6-2-vector-database-integration.md`
- Related findings: #001, #004, #005, #014
