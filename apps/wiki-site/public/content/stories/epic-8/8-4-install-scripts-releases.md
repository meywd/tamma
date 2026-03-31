---
title: "Story 8-4: Install Scripts & GitHub Releases"
sidebar:
  order: 80
---

## User Story

As a **Tamma user**,
I want to install Tamma with a single curl command that downloads the correct binary for my platform,
So that I can get started without managing Node.js, npm, or any other prerequisites.

## Priority

P1 - Required for Tier 2 distribution

## Acceptance Criteria

1. `install.sh` script detects OS (Linux, macOS) and architecture (x64, arm64) via `uname`
2. Script downloads the correct binary from GitHub Releases, verifies SHA256 checksum, and installs to `~/.local/bin/tamma`
3. Script offers to add `~/.local/bin` to PATH by appending to the detected shell RC file (`.zshrc`, `.bashrc`, `.bash_profile`, `.config/fish/config.fish`)
4. Script supports: `TAMMA_VERSION` (pin version), `TAMMA_INSTALL_DIR` (custom location), `HTTPS_PROXY` (corporate proxy), `GITHUB_TOKEN` (rate limit bypass), `NO_COLOR` (disable colors)
5. `install.ps1` PowerShell script provides equivalent functionality for Windows
6. GitHub Actions release workflow (`.github/workflows/release.yml`) builds binaries on 4 platforms via matrix strategy and creates a GitHub Release with all assets
7. Release assets follow naming convention: `tamma-{version}-{os}-{arch}[.exe]` with corresponding `.sha256` files
8. A `manifest.json` is included in each release with version, timestamp, and per-asset checksums/sizes
9. Post-release verification job runs the install script against the newly created release on ubuntu-latest, macos-14, and macos-13
10. `shellcheck` passes on `install.sh` in CI

## Technical Design

### Install Script Features

| Feature | Implementation |
|---------|---------------|
| OS detection | `uname -s` → Linux/Darwin/CYGWIN |
| Arch detection | `uname -m` → x86_64/arm64/aarch64 |
| Download | curl (primary) with wget fallback |
| Checksum | sha256sum / shasum -a 256 |
| Atomic install | Write to `.tmp`, `mv` to final location |
| PATH | Auto-detect shell RC, offer to append |
| Banner | ASCII art with `NO_COLOR` respect |
| Cleanup | `trap` removes temp dir on exit |
| JSON parsing | grep/cut (no jq dependency) |

### Release Workflow Matrix

```yaml
matrix:
  include:
    - platform: darwin-arm64, runner: macos-14
    - platform: darwin-x64, runner: macos-13
    - platform: linux-x64, runner: ubuntu-latest
    - platform: linux-arm64, runner: ubuntu-24.04-arm
```

### Asset Naming

```
tamma-0.2.0-darwin-arm64
tamma-0.2.0-darwin-arm64.sha256
tamma-0.2.0-darwin-x64
tamma-0.2.0-darwin-x64.sha256
tamma-0.2.0-linux-x64
tamma-0.2.0-linux-x64.sha256
tamma-0.2.0-linux-arm64
tamma-0.2.0-linux-arm64.sha256
tamma-0.2.0-checksums.txt
manifest.json
```

## Dependencies

- **Prerequisite**: Story 8-3 (binary compilation must work)
- **Blocks**: Story 8-5 (auto-update uses same release infrastructure)

## Testing Strategy

1. **Shellcheck**: Lint `install.sh` in CI
2. **Platform detection**: Source script functions, verify correct OS/arch on each runner
3. **Integration**: Run full install script against a release on CI matrix
4. **Checksum**: Verify SHA256 verification catches tampered binaries
5. **Proxy**: Test `HTTPS_PROXY` passthrough (manual)

## Estimated Effort

2-3 days

## Files Created/Modified

| File | Action |
|------|--------|
| `install.sh` | Create (repo root) |
| `install.ps1` | Create (repo root) |
| `.github/workflows/release.yml` | Create |
| `.github/workflows/ci.yml` | Modify (add shellcheck, binary smoke test) |
