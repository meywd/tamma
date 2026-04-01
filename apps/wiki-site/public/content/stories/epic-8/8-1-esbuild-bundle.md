---
title: "Story 8-1: esbuild Bundle & Package Structure"
sidebar:
  order: 80
---

## User Story

As a **Tamma user**,
I want to install the CLI via `npx @tamma/cli init` without cloning the monorepo,
So that I can set up and run Tamma on any machine with just Node.js installed.

## Priority

P0 - Required for all distribution tiers (Tier 1 foundation)

## Acceptance Criteria

1. esbuild bundles all 7 workspace packages (`@tamma/shared`, `platforms`, `providers`, `orchestrator`, `observability`, `api`, `events`) into a single ESM file at `packages/cli/dist/index.js`
2. Third-party dependencies (ink, react, octokit, anthropic-sdk, fastify, pino, etc.) are kept external and listed in the published package's `dependencies`
3. The `createRequire` pattern in `index.tsx` is replaced with a build-time `TAMMA_VERSION` constant injected via esbuild `define`, with a development fallback that reads `package.json`
4. JSX transform works correctly for all Ink components (Banner, SessionLayout, EngineStatus, LogArea, PlanApproval, CommandInput, IssueSelector)
5. A `prepare-package.mjs` script generates a publish-ready `package.json` that replaces `workspace:*` references with collected external dependencies from all bundled packages
6. `pg` is listed as `optionalDependencies` (not `dependencies`) since it's only used by `@tamma/events`
7. Smoke test script validates: `--version`, `--help`, `init` (preflight failure expected outside git repo), `start` (config validation error expected)
8. Bundle size stays under 500KB uncompressed JS
9. Existing `tsc --build` development workflow continues to work unchanged
10. `import type` vs `import` correctness verified for all enum imports used at runtime (known issue: `EngineState`)

## Technical Design

### Build Script (`packages/cli/scripts/build.mjs`)

```js
import { build } from 'esbuild';

await build({
  entryPoints: ['src/index.tsx'],
  bundle: true,
  platform: 'node',
  target: 'node22',
  format: 'esm',
  outfile: 'dist/index.js',
  jsx: 'transform',
  jsxFactory: 'React.createElement',
  jsxFragment: 'React.Fragment',
  banner: { js: '#!/usr/bin/env node' },
  external: [
    'ink', 'ink-spinner', 'ink-text-input', 'ink-select-input',
    'react', 'react/jsx-runtime', 'node:*',
    'commander', 'dotenv',
    '@octokit/rest', '@gitbeaker/rest',
    '@anthropic-ai/sdk', 'openai',
    'pino', 'pino-pretty',
    'fastify', '@fastify/cors', '@fastify/helmet', '@fastify/jwt', 'fastify-plugin',
    'zod', 'dayjs', 'pg',
  ],
  define: { 'TAMMA_VERSION': JSON.stringify(version) },
  treeShaking: true,
  sourcemap: true,
  keepNames: true,
  minifyWhitespace: true,
});
```

### Source Code Changes

1. **`packages/cli/src/index.tsx`**: Replace `createRequire` with `TAMMA_VERSION` define
2. **`packages/cli/package.json`**: Add `build:bundle`, `build:publish`, `test:smoke` scripts
3. **New file**: `packages/cli/scripts/build.mjs` (esbuild config)
4. **New file**: `packages/cli/scripts/prepare-package.mjs` (dependency collector)
5. **New file**: `packages/cli/scripts/smoke-test.mjs` (post-bundle verification)

### Package Structure (Published)

```
@tamma/cli/
├── dist/
│   ├── index.js          # Single bundled ESM file
│   └── index.js.map      # Source map
├── package.json           # Generated (no workspace:* refs)
├── LICENSE
└── README.md
```

## Dependencies

- **Prerequisite**: None (first story in Epic 8)
- **Blocks**: Story 8-2 (publish pipeline needs bundle), Story 8-3 (binary compilation may reuse bundle)

## Testing Strategy

1. **Unit**: Verify esbuild config produces valid ESM output
2. **Smoke**: Run bundled CLI with `--version`, `--help`, `init`, `start --help`
3. **Integration**: `npm pack` → install in temp dir → run `npx tamma --version`
4. **Size**: Assert bundle < 500KB in CI

## Estimated Effort

2-3 days

## Files Created/Modified

| File | Action |
|------|--------|
| `packages/cli/scripts/build.mjs` | Create |
| `packages/cli/scripts/prepare-package.mjs` | Create |
| `packages/cli/scripts/smoke-test.mjs` | Create |
| `packages/cli/src/index.tsx` | Modify (version injection) |
| `packages/cli/package.json` | Modify (add scripts) |
