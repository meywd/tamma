# Finding 005: `server.ts:210` starts server without `IntelligenceServicesBundle`

**Scope**: kb
**Severity**: P1 (single-line bug that deactivates all KB backends)
**Status**: Incomplete (the "bug site" that wires the composition-root gap of #001)
**Estimated port effort**: trivial (5 minutes once #001 factories exist)

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/knowledge-base/index.ts`.

The deleted TS API registered KB routes via `registerKnowledgeBaseRoutes(app, services?)`. It was called from the main API bootstrap with `services` unspecified:

```typescript
// packages/api/src/routes/knowledge-base/index.ts (9e9a57c~1)
export async function registerKnowledgeBaseRoutes(
  app: FastifyInstance,
  services?: KBServices,
): Promise<void> {
  const svc = services ?? createKBServices();
  // ...
}
```

`createKBServices()` called with no args → all deps null → empty-state. Same gap pattern.

- Dependencies: none (this is about the call-site).
- Tests: the TS route tests passed `services` explicitly with fakes. Production path never exercised.

## 2. What's in C#

### C# side
N/A — this is sidecar-internal.

### Sidecar side — direct-invocation entry point

```typescript
// packages/intelligence-server/src/server.ts:197-216 (current)
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

// Entry point: only run when invoked directly (not when imported by tests).
if (import.meta.url === `file://${process.argv[1]}` || process.argv[1] === fileURLToPath(import.meta.url)) {
  startServer().catch((err) => {
    // eslint-disable-next-line no-console
    console.error('Failed to start intelligence-server:', err);
    process.exit(1);
  });
}
```

Line 211: `startServer()` — invoked with **zero arguments**. `opts.services` is `undefined`, cascades through to `buildServices(undefined)` at line 192, cascades to `new IndexManagementService(undefined)` etc. at line 43. All six services construct with null adapters.

The Dockerfile at `packages/intelligence-server/Dockerfile:78` runs this file:

```
CMD ["node", "packages/intelligence-server/dist/server.js"]
```

So the production container hits exactly the no-bundle code path.

- Dependencies: `buildServer` (line 185), `buildServices` (line 41). Both accept `bundle`; neither has a fallback path to construct real deps.
- Tests: `intelligence-server/src/__tests__/server.test.ts` calls `buildServer({ services: mockBundle })` directly — entry-point line 211 is not covered.

## 3. The gap

- TS did: bootstrap called `registerKnowledgeBaseRoutes(app)` with no services → empty state. (Broken.)
- C# + sidecar does: `node packages/intelligence-server/dist/server.js` → `startServer()` with no services → same empty state via different plumbing.

For operational monitoring:
- The sidecar logs `listening on 0.0.0.0:4100` — looks healthy.
- The `/health` endpoint returns `{"status":"ok"}` — looks healthy.
- Every KB endpoint returns a 200 with zero / empty / stub data — looks healthy.
- No log line on startup indicates "intelligence services: NONE configured". No ops signal distinguishes "misconfigured" from "working".

Error paths:
- Both TS and sidecar: no error. This silent-failure is the whole bug.

## 4. Gap from stories

No story explicitly specifies startup-time diagnostic output. Epic 6 stories (`6-1` through `6-5`) assume the services work; they don't describe the bootstrap path.

`CLAUDE.md` section "Self-Maintenance Goal" requires "100% test coverage on critical paths" and "complete audit trail". A silent composition-root skip violates both.

Story alignment:
- [ ] Matches TS behavior (same gap)
- [x] Matches C# behavior (C# correctly passes through; sidecar bootstrap is the gap)
- [ ] Describes a third behavior
- [x] No story — Epic 6 implicitly assumes bootstrap wires deps. Must backfill spec.

## 5. Status

- **Classification**: Incomplete — trivial call-site fix, blocked by upstream (#001, #003, #004) composition-root factories.
- **What's needed to finish**:
  1. Once `createVectorStoreFromEnv()` and siblings exist (#004), change line 211 to:
     ```typescript
     startServer({
       services: {
         vectorStore: await createVectorStoreFromEnv(),
         ragPipeline: await createRagPipelineFromEnv(),
         indexer: await createIndexerFromEnv(),
         mcpClient: await createMcpClientFromEnv(),
         contextAggregator: await createContextAggregatorFromEnv(),
         costTracker: await createCostTrackerFromEnv(),
       },
     }).catch((err) => { /* ... */ });
     ```
  2. Add structured log line at startup: `logger.info({ services: { vectorStore: !!bundle.vectorStore, /* ... */ } }, 'intelligence-server starting')`. If any is missing, log at WARN level.
  3. Consider a `--strict` flag that refuses to start unless all six services are wired (good for production, bad for dev).
- **Is it "just a stub" or is scope missing?** Call-site change is trivial (5 lines). Hard part is the prerequisites (#001, #003, #004, #014).
- **Blockers**: #001, #003, #004, #014. Must land all four first, then this is a one-line follow-up.

## Remediation

- Files to modify:
  - `packages/intelligence-server/src/server.ts:210-216` — add the composition call.
- Files to create: none.
- Tests to add:
  - Integration test: run the compiled `dist/server.js` as a child process with env vars for testcontainers ChromaDB; assert `/kb/vector-db/status` returns `"ready"`. This is the first test that exercises the entry-point path at all.
- Estimated effort: 0.5h
  - Change: 5m
  - Test: 20-30m

## References

- Sidecar entrypoint: `packages/intelligence-server/src/server.ts:197-216`
- Dockerfile CMD: `packages/intelligence-server/Dockerfile:78`
- Related findings: #001 (root cause), #003 (env-var gap), #004 (factory gap), #014 (strict-mode blocker)
- CLAUDE.md: "Self-Maintenance Goal" section requires 100% coverage of critical paths.

## Remediation status

**Status (2026-04-18):** Invalid for the C# port pass — TypeScript-only call site.

`server.ts:210` is the entrypoint of the Node.js sidecar process; there is
no equivalent code path in the C# port. The C# `Tamma.Api.Program.cs` and
`KnowledgeBaseServiceCollectionExtensions.AddKnowledgeBaseServices()`
correctly bootstrap the C# side (typed HTTP client → sidecar URL); they do
not — and structurally cannot — boot the sidecar's intelligence services.

**To unblock:** trivial 5-minute follow-up to findings 001/004 once those
land.
