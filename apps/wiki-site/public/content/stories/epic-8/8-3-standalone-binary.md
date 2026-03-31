---
title: "Story 8-3: Standalone Binary Compilation"
sidebar:
  order: 80
---

## User Story

As a **Tamma user**,
I want a single standalone binary that runs without Node.js or any runtime dependencies,
So that I can install Tamma on any machine regardless of what development tools are available.

## Priority

P1 - Required for Tier 2 distribution

## Acceptance Criteria

1. `bun build --compile` produces standalone executables for 4 target platforms: darwin-arm64, darwin-x64, linux-x64, linux-arm64
2. Build script (`scripts/build-binary.ts`) orchestrates cross-platform builds with SHA256 checksum generation and a `manifest.json`
3. All Ink/React TUI components render correctly in the compiled binary (Banner, SessionLayout, IssueSelector, PlanApproval)
4. `pino` worker thread issue resolved: binary uses a synchronous logger fallback (`createSimpleLogger`) that implements the `ILogger` interface without thread-stream
5. Version is embedded at build time via a generated `version.ts` file (no runtime `package.json` read)
6. `isStandaloneBinary()` detection function added to `preflight.ts` to skip Node.js version check when running as binary
7. Binary size under 60MB uncompressed per platform, under 25MB compressed (.tar.gz)
8. Smoke tests pass on each platform: `--version`, `--help`, `init` (preflight), `status`
9. Windows x64 support is P2/experimental with `.exe` output
10. Two-stage esbuild fallback exists if Bun workspace resolution fails

## Technical Design

### Build Script (`scripts/build-binary.ts`)

```typescript
#!/usr/bin/env bun
const TARGETS = {
  'darwin-arm64': 'bun-darwin-arm64',
  'darwin-x64': 'bun-darwin-x64',
  'linux-x64': 'bun-linux-x64',
  'linux-arm64': 'bun-linux-arm64',
};

// For each target:
// bun build --compile --minify --sourcemap packages/cli/src/index.tsx
//   --target={target} --outfile dist/binaries/tamma-{version}-{platform}
// Generate SHA256 checksum file
// Write manifest.json with all checksums and sizes
```

### Pino Fallback

```typescript
// packages/observability/src/simple-logger.ts
export function createSimpleLogger(name: string, level: string): ILogger {
  // Writes directly to stderr, no worker threads
  // Implements: debug, info, warn, error, fatal, child
}
```

### Preflight Binary Detection

```typescript
export function isStandaloneBinary(): boolean {
  return typeof Bun !== 'undefined' && Bun.main === process.execPath;
}
```

### Version Embedding

```bash
# Build script generates before compilation:
VERSION=$(jq -r .version packages/cli/package.json)
echo "export const VERSION = '${VERSION}';" > packages/cli/src/version.ts
```

## Dependencies

- **Prerequisite**: Story 8-1 (version injection pattern reused)
- **Blocks**: Story 8-4 (install scripts need binaries), Story 8-5 (upgrade needs binary detection)
- **External**: Bun runtime (for compilation only, not at runtime)

## Testing Strategy

1. **Build**: CI matrix builds binaries on native runners (macos-14, macos-13, ubuntu-latest, ubuntu-24.04-arm)
2. **Smoke**: Each binary tested with `--version`, `--help`, `init` in a temp git repo
3. **File type**: `file dist/tamma` verifies correct executable format
4. **Size**: Assert < 60MB per binary in CI
5. **Ink rendering**: Manual verification that TUI renders correctly in compiled binary

## Estimated Effort

3-4 days

## Risks

| Risk | Likelihood | Mitigation |
|------|-----------|------------|
| Ink/yoga-wasm incompatible with Bun compile | Low | Proven in OpenCode; add smoke test |
| pino thread-stream breaks in binary | Medium | createSimpleLogger fallback |
| Workspace resolution fails in Bun | Medium | Two-stage esbuild pre-bundle fallback |
| Binary > 100MB | Low | --minify, remove pino-pretty, profile |

## Files Created/Modified

| File | Action |
|------|--------|
| `scripts/build-binary.ts` | Create |
| `packages/cli/src/version.ts` | Create (generated) |
| `packages/observability/src/simple-logger.ts` | Create |
| `packages/cli/src/preflight.ts` | Modify (add isStandaloneBinary) |
| `packages/cli/src/index.tsx` | Modify (use version.ts) |
