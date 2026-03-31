---
title: "Story 8-5: Auto-Update & Package Manager Distribution"
sidebar:
  order: 80
---

## User Story

As a **Tamma user**,
I want the CLI to check for updates and allow me to upgrade with a single command,
So that I always have the latest features and bug fixes without manual version tracking.

## Priority

P2 - Enhancement for Tier 2 distribution

## Acceptance Criteria

1. `tamma upgrade` command downloads and atomically replaces the current binary with the latest version from GitHub Releases
2. `tamma upgrade --version 0.3.0` installs a specific version
3. `tamma upgrade --force` re-installs even if already on latest
4. Background update check runs on startup (non-blocking, 3s timeout) and prints a notification if a newer version is available
5. Update check respects a 24-hour cooldown stored in `~/.local/state/tamma/update-check.json`
6. SHA256 checksum verified before replacing binary; update aborts if verification fails
7. Atomic replacement: write to `{binary}.update`, then `mv` to replace (no partial binary risk)
8. Homebrew tap repository (`meywd/homebrew-tap`) created with a formula for `tamma`
9. Release workflow automatically updates the Homebrew formula with new version and checksums after each release
10. `brew tap meywd/tap && brew install tamma` installs correctly

## Technical Design

### Upgrade Command (`packages/cli/src/commands/upgrade.ts`)

```typescript
export async function upgradeCommand(options: { version?: string; force?: boolean }): Promise<void> {
  // 1. Fetch latest release from GitHub API
  // 2. Compare versions (skip if same, unless --force)
  // 3. Detect current platform (darwin-arm64, linux-x64, etc.)
  // 4. Download binary + checksum to temp location
  // 5. Verify SHA256
  // 6. Atomic replacement: rename new over current process.execPath
}
```

### Background Update Check (`packages/cli/src/update-check.ts`)

```typescript
export async function checkForUpdates(): Promise<string | null> {
  // Non-blocking, never throws
  // Check cooldown (24h), fetch latest tag, compare versions
  // Return message or null
}
```

### Homebrew Formula (`meywd/homebrew-tap/Formula/tamma.rb`)

```ruby
class Tamma < Formula
  desc "AI-powered autonomous development platform"
  homepage "https://github.com/meywd/tamma"
  # Platform-specific URLs and SHA256s auto-updated by CI
end
```

## Dependencies

- **Prerequisite**: Story 8-4 (GitHub Releases must exist with binary assets)
- **Blocks**: None

## Testing Strategy

1. **Unit**: Test version comparison logic
2. **Integration**: Mock GitHub API response, verify upgrade downloads correct asset
3. **Cooldown**: Verify update check respects 24h cooldown
4. **Homebrew**: `brew install` from tap and verify `tamma --version`

## Estimated Effort

2 days

## Files Created/Modified

| File | Action |
|------|--------|
| `packages/cli/src/commands/upgrade.ts` | Create |
| `packages/cli/src/update-check.ts` | Create |
| `packages/cli/src/index.tsx` | Modify (add upgrade command, wire update check) |
| `meywd/homebrew-tap/Formula/tamma.rb` | Create (separate repo) |
| `.github/workflows/release.yml` | Modify (add Homebrew formula update job) |
