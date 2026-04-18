# Finding 014: `@tamma/intelligence` strict-mode build errors block composition root

**Scope**: kb
**Severity**: P2 (blocker for P1 findings but not itself user-visible)
**Status**: Incomplete (pre-existing strict-mode errors deferred by Dockerfile workaround)
**Estimated port effort**: 5-10h (unknown — size of the strict-mode fix backlog)

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/intelligence/`.

The `@tamma/intelligence` package (which contains real `ChromaVectorStore`, `RAGPipeline`, `CodebaseIndexer`, `ContextAggregator` implementations) was in the repo long before Epic 19 and has accumulated strict-mode violations. When the sidecar was introduced in Epic 19, the decision was explicitly made to NOT require `@tamma/intelligence` to compile cleanly:

- The deleted TS API's `packages/api/` had a similar workaround — its `tsconfig.json` referenced `@tamma/intelligence` via `paths` but excluded it from type-checking through `skipLibCheck: true`.
- The pre-existing errors were tracked as a "known issue" but never fixed.

- Dependencies: strict-mode fixes touch every file in `packages/intelligence/src/{vector-store,rag,indexer,context,knowledge-base}/`.
- Tests: `packages/intelligence/src/__tests__/` pass at runtime; the blocker is compile-time strict-mode only.

## 2. What's in C#

### C# side
N/A — this is a TS sidecar issue.

### Sidecar side — Dockerfile explicitly skips building `@tamma/intelligence`

```dockerfile
# packages/intelligence-server/Dockerfile (current, lines 44-49)
# Build only the intelligence-server. It references @tamma/shared via
# `references` in tsconfig, so shared is built first. @tamma/intelligence is
# NOT required for compilation (sidecar uses narrow local interfaces), so we
# skip its pre-existing strict-mode build errors.
RUN pnpm --filter @tamma/shared --filter @tamma/intelligence-server run build
```

The comment is candid: the sidecar was designed to depend on narrow local types (`IVectorStoreAdapter`, `IRagPipeline` etc. in `packages/intelligence-server/src/types.ts`) precisely so it could ship without requiring the real implementations to compile.

The adapter factories in `adapters.ts` use dynamic `import()` (not static) for the same reason:

```typescript
// packages/intelligence-server/src/adapters.ts:1-18 (current)
/**
 * Adapter factories that bridge concrete @tamma/intelligence implementations
 * to the narrow interfaces the sidecar services depend on.
 *
 * These use dynamic `import()` so the sidecar can be compiled and tested
 * WITHOUT requiring @tamma/intelligence to typecheck cleanly. Intelligence
 * has pre-existing strict-mode build errors (documented in the CONCERNS
 * section of the bridge delivery) — the sidecar deliberately depends only
 * on the runtime JS output.
 * ...
 */
```

This intentional decoupling is elegant for testing, but it **blocks** the composition root from actually calling the real constructors: as long as `@tamma/intelligence` doesn't compile, the real `ChromaVectorStore` class cannot be imported and instantiated.

- Dependencies: the fix cascades — every strict-mode violation in `@tamma/intelligence` must be resolved.
- Tests: existing `@tamma/intelligence` runtime tests will pass once types align; likely no new tests needed.

## 3. The gap

- TS did: sidestepped the issue via `skipLibCheck`; live API also never wired `@tamma/intelligence` so the shortcut went unnoticed.
- C# + sidecar does: same sidestep at the build level (Dockerfile skip). Consequence: the JSDoc-referenced `createVectorStoreFromEnv()` (see #004) cannot be implemented because its body would need `import { ChromaVectorStore } from '@tamma/intelligence/...'` to compile.

For the port-gap remediation plan:
- #001 (composition root), #003 (env vars), #004 (adapter factories), #005 (server entrypoint), #015 (dead `packages/intelligence`) are all blocked on this finding.
- Per the audit summary `/tmp/tamma-audit/35-kb.md:40`: **"Must fix before real adapters can be imported."**

This is not user-visible on its own. It becomes user-visible only via its downstream findings (#001 et al).

Error paths:
- Compile time: `pnpm --filter @tamma/intelligence run build` currently errors out (per Dockerfile comment — actual error list not captured in this audit; should be run once to scope).
- Runtime: zero impact; the sidecar runs because it uses dynamic imports and narrow types.

## 4. Gap from stories

No story governs "fix strict-mode errors". This is pure technical debt accumulation.

CLAUDE.md § "TypeScript Strict Mode":
> All code must compile with strict TypeScript settings:
> ```json
> { "compilerOptions": { "strict": true, "noImplicitAny": true, ... } }
> ```

`@tamma/intelligence` violates this project-wide rule. The shortcut in `packages/intelligence-server/Dockerfile:47-49` is a documented deviation.

Story alignment:
- [ ] Matches TS behavior
- [ ] Matches C# behavior
- [ ] Describes a third behavior
- [x] No story — CLAUDE.md is the governing spec; `@tamma/intelligence` violates it.

## 5. Status

- **Classification**: Incomplete (technical debt).
- **What's needed to finish**:
  1. Run `pnpm --filter @tamma/intelligence run build` and capture the error list.
  2. Fix each error. Common strict-mode issues per the project's own CLAUDE.md `MEMORY.md` notes:
     - `exactOptionalPropertyTypes: true` — `undefined` not assignable to optional props. Fix: conditional property assignment.
     - `noUncheckedIndexedAccess: true` — indexed access returns `T | undefined`. Fix: add guards or non-null assertions.
     - Add type assertions through `unknown` where narrowing fails.
  3. Remove the skip from `packages/intelligence-server/Dockerfile:48`: change to `pnpm --filter @tamma/intelligence --filter @tamma/intelligence-server run build`.
  4. Switch `adapters.ts` from dynamic to static imports where compile-time type-checking adds value.
- **Is it "just a stub" or is scope missing?** Neither — this is debt. Estimated 5-10h but opaque without running the build to get the error count.
- **Blockers**:
  - None upstream; this is the deepest blocker for the KB port.
  - Unknown risk: fixing strict-mode errors may reveal latent bugs in `@tamma/intelligence` runtime behavior.

## Remediation

- Files to modify:
  - Every file in `packages/intelligence/src/{vector-store,rag,indexer,context,knowledge-base}/` — individual type fixes.
  - `packages/intelligence/tsconfig.json` — verify it inherits repo strict config.
  - `packages/intelligence-server/Dockerfile:48` — remove skip.
  - `packages/intelligence-server/src/adapters.ts` — once compile is clean, consider converting dynamic imports to static.
- Files to create: none.
- Tests to add:
  - No new tests — existing `@tamma/intelligence` runtime tests must continue passing after strict-mode fixes.
  - CI gate: `pnpm build` must succeed on `packages/intelligence` in the monorepo build step.
- Estimated effort: 5-10h (unknown)
  - Error capture + triage: 1h
  - Fix: 3-8h depending on error volume
  - Dockerfile + adapters cleanup: 1h

## References

- Dockerfile skip: `packages/intelligence-server/Dockerfile:44-49`
- Adapter dynamic-import rationale: `packages/intelligence-server/src/adapters.ts:1-18`
- Strict-mode policy: CLAUDE.md § "TypeScript Strict Mode"
- Strict-mode gotchas: `MEMORY.md` § "TypeScript Strict Mode Gotchas"
- Related findings: #001, #003, #004, #005, #015 — all blocked by this one.
