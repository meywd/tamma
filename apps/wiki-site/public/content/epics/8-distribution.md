---
title: "Epic 8: Distribution & Installation"
sidebar:
  order: 8
---

**Status:** Done. All 8 stories landed across three tiers (npm / standalone binary / Docker full-stack).
**Stories:** 8 (8-1..8-8).
**Primary code:** `packages/cli/`, repository root `Dockerfile.*` variants, `docker-compose.yml`, `scripts/install*.sh`.

## Overview

Epic 8 is the install story. Before it, running Tamma required cloning the monorepo and running `pnpm install` — a four-step process that assumed the user already had Node 22, pnpm 9, and (for full-stack) Postgres / RabbitMQ / ELSA / Docker on-box. Epic 8 makes Tamma reachable from three on-ramps, each tuned for a different user: developers who already have Node (`npx @tamma/cli init`), CI or ops people who want one binary (`curl install.sh | bash`), and teams wanting the whole platform including ELSA + dashboards (`tamma init --full-stack && docker compose up -d`).

The epic is organized as three tiers that share a single package structure and a common `tamma init` wizard, so the UX is the same regardless of how you installed the tool. Tier 1 is the default cadence — npm publishing runs on every release tag. Tier 2 layers Bun's `--compile` target on top of the same bundle for zero-prereq binaries distributed through GitHub Releases and a Homebrew tap. Tier 3 packages the TypeScript runtime and the React dashboard as Docker images published to GHCR, and ships a Compose file that wires Postgres + RabbitMQ + ELSA + API + dashboard + CLI runner + nginx together.

## Architecture

```
                                Developer / CI / Ops
                                        |
                +-----------------------+-----------------------+
                |                       |                       |
                v                       v                       v
   TIER 1: npm              TIER 2: standalone           TIER 3: Docker
   --------------           ------------------           --------------

   npx @tamma/cli init      curl install.sh | bash      tamma init --full-stack
           |                         |                          |
           v                         v                          v
   esbuild bundle -----> Bun --compile -----> npm bundle + Dockerfiles
   (packages/cli/                |                                   |
    dist/cli.js)                 v                                   v
           |            binaries per OS/arch                 GHCR images:
   npm registry:          (darwin-arm64,                     ghcr.io/meywd/
   @tamma/cli              darwin-x64,                         tamma-cli
           |               linux-x64,                          tamma-api
           v               linux-arm64,                        tamma-dashboard
    node_modules/.bin/     win-x64)                            tamma-elsa
    tamma              GitHub Releases + Homebrew                    |
           |                     |                                   v
           v                     v                           docker-compose.yml
   Runs CLI directly     ~/.local/bin/tamma                 (7 services + nginx)
                         + auto-update                              |
                                                                     v
                                                            Full stack on
                                                            one host
```

All three tiers share the same runtime entrypoint:

```
                    packages/cli/src/index.tsx
                              |
                              v
                  +---------------------------+
                  | esbuild bundle            |
                  | (packages/cli/dist/)      |
                  +---------------------------+
                     /           |         \
                    v            v          v
             npm publish   bun --compile   COPY into
             (Tier 1)      (Tier 2)        Dockerfile
                                           (Tier 3)
```

## Components

| Component | Purpose | Key files | Tier / Status |
|-----------|---------|-----------|---------------|
| esbuild bundle config | Single-file IIFE bundle of `@tamma/cli` with workspace deps inlined | `packages/cli/build.config.js`, `packages/cli/tsconfig.json` | 8-1 / Done |
| npm publish pipeline | GH Actions workflow triggered on `v*.*.*` tags — build, test, publish, sign | `.github/workflows/publish-npm.yml` | 8-2 / Done |
| Bun binary compilation | `bun build --compile` across 5 OS/arch combinations | `packages/cli/scripts/build-binary.ts` | 8-3 / Done |
| Install script | Shell script that detects OS/arch, downloads from GitHub Releases, verifies checksum, installs to `~/.local/bin` | `scripts/install.sh`, `scripts/install.ps1` | 8-4 / Done |
| GitHub Releases pipeline | Tag-driven workflow that uploads binaries + checksums + SBOM | `.github/workflows/release-binary.yml` | 8-4 / Done |
| Auto-update check | CLI checks `https://api.github.com/repos/meywd/tamma/releases/latest` on start | `packages/cli/src/update-check.ts` | 8-5 / Done |
| Homebrew tap | `brew install meywd/tamma/tamma` via a separate `homebrew-tamma` formula repo | `.github/workflows/homebrew.yml` | 8-5 / Done |
| TypeScript service Dockerfile | Multi-stage build → Node 22 Alpine, non-root, healthcheck | `Dockerfile.cli`, `Dockerfile.api` | 8-6 / Done |
| Dashboard Dockerfile | Vite build → nginx:alpine serving static | `packages/dashboard/Dockerfile` | 8-6 / Done |
| Docker Compose full stack | 7 services (postgres, rabbitmq, elsa, api, cli-runner, dashboard, nginx) | `docker-compose.yml`, `docker-compose.production.yml` | 8-7 / Done |
| Docker CI pipeline | Build + push all images to GHCR on tag, sign with cosign | `.github/workflows/publish-docker.yml` | 8-8 / Done |
| `tamma init --full-stack` | CLI subcommand that writes a Compose file and `.env` into the current dir | `packages/cli/src/commands/init.ts` | 8-8 / Done |

## Class / module structure

```
packages/cli/src/
  index.tsx              — Ink CLI entrypoint; picks subcommand
  commands/
    init.ts              — `tamma init [--full-stack]` wizard
    start.ts             — `tamma start` (self-hosted engine)
    server.ts            — `tamma server` (self-hosted HTTP)
    api.ts               — `tamma api` (SaaS / GitHub App mode)
  update-check.ts        — polls GitHub Releases for new version
  preflight.ts           — OS / node / docker detection
  config.ts              — loads .tamma/config.json
  file-logger.ts         — structured logs to ~/.tamma/logs

packages/cli/scripts/
  build-binary.ts        — orchestrates bun --compile across targets
  package-bundle.ts      — produces npm-ready tarball

Root-level distribution artifacts:
  Dockerfile.cli         — Tier 3 CLI image
  Dockerfile.api         — Tier 3 API image
  docker-compose.yml     — 7-service stack
  scripts/install.sh     — POSIX installer (Tier 2)
  scripts/install.ps1    — PowerShell installer (Tier 2, Windows)
  .github/workflows/
    publish-npm.yml      — Tier 1
    release-binary.yml   — Tier 2
    publish-docker.yml   — Tier 3
    homebrew.yml         — Tier 2 tap update
```

## Sequence — Tier 2 install

```
User              install.sh           GitHub Releases API     GitHub Releases CDN    Filesystem
  |                  |                         |                         |                |
  | curl | bash ---> |                         |                         |                |
  |                  | detect OS/arch          |                         |                |
  |                  | GET /repos/meywd/tamma/releases/latest ---------->|                |
  |                  | <---- { tag, assets[] }                           |                |
  |                  | pick tamma-<os>-<arch>                            |                |
  |                  | GET asset url ----------------------------------->|                |
  |                  | <---- binary bytes                                |                |
  |                  | GET checksums.txt ------------------------------->|                |
  |                  | <---- sha256 manifest                             |                |
  |                  | verify sha256 locally                             |                |
  |                  | chmod +x; mv to ~/.local/bin/tamma -------------->|--------------> |
  |                  | print "tamma v1.x.y installed"                    |                |
  | <--- shell back  |                                                   |                |
  |                                                                                       |
  | tamma --version                                                                       |
  | update-check.ts -> GET /releases/latest (cached 24h) -> silent if already up-to-date  |
```

## Sequence — Tier 3 full stack

```
User              tamma init           Filesystem             docker compose         Docker daemon       GHCR
 |                   |                       |                       |                     |              |
 | tamma init \      |                       |                       |                     |              |
 |  --full-stack --->|                       |                       |                     |              |
 |                   | prompt providers,     |                       |                     |              |
 |                   |   github token,       |                       |                     |              |
 |                   |   db password         |                       |                     |              |
 |                   | write .env ---------->|                       |                     |              |
 |                   | write docker-compose.yml ----------------->   |                     |              |
 |                   | "run: docker compose up -d"                   |                     |              |
 | docker compose    |                                               |                     |              |
 |  up -d ---------------------------------------------------------->|                     |              |
 |                                                                   | pull images --------|------------->|
 |                                                                   | <----- layers ------------------- |
 |                                                                   | create postgres  -->|              |
 |                                                                   | create rabbitmq  -->|              |
 |                                                                   | create elsa      -->|              |
 |                                                                   | create api       -->|              |
 |                                                                   | create cli-runner-->|              |
 |                                                                   | create dashboard -->|              |
 |                                                                   | create nginx     -->|              |
 |                                                                   | all healthy                        |
 | <------------------------------------------------------------ ready on :8080, :8081, :8082             |
```

## Use cases

- **Developer evaluating Tamma for the first time** — `npx @tamma/cli init` pulls the package, runs the wizard, writes `.tamma/config.json`, and shows the TUI. No global installs, ~30 s end-to-end on a warm npm cache.
- **CI runner on an ephemeral VM** — `curl -fsSL https://tamma.dev/install.sh | bash` drops a single binary that runs without Node; useful for GitHub Actions `uses: run` blocks and Jenkins nodes where installing Node is policy-friction.
- **Team self-hosting on one VPS** — `tamma init --full-stack` scaffolds Compose + `.env`, then `docker compose up -d` brings up Postgres, RabbitMQ, ELSA, API, dashboard, and nginx on a single host. Matches Tamma's own Hetzner deployment.
- **Air-gapped / offline install** — GitHub Releases binaries can be copied to offline hosts; SHA256 + cosign signatures let ops verify integrity.
- **Rolling upgrades via Homebrew** — `brew upgrade tamma` or the in-process auto-update hint (`tamma update`) replaces the binary with the newest release; Docker users `docker compose pull && up -d`.

## Dependencies

**Upstream**
- Epic 1 — `@tamma/cli` package + engine API surface; Epic 8 packages what Epic 1 shipped.
- Epic 2 — `TammaEngine`, `processOneIssue` boot path that `tamma start` launches.
- Epic 5 — `createLogger` (Pino) wired into CLI + services; dashboard UI binary.
- Epic 6 — API routes (knowledge base, MCP) that get bundled into the CLI.
- Epic 7 — ELSA C# server; Tier 3 Compose includes `apps/tamma-elsa` image.

**Downstream**
- Epic 17 (multi-tenancy) — Tier 3 Compose is the reference for tenant-per-container.
- Epic 25 (wiki site) — shares the same Docker publish workflow pattern.
- Epic 26 (project management) — CLI commands consume the same bundle.

**External**
- `esbuild` ^0.24.2 (root dev dep) for Tier 1 bundling.
- `bun` ≥ 1.1 for Tier 2 binary compilation.
- Docker Engine + Compose v2 for Tier 3.
- npm registry (Tier 1 publish target).
- GitHub Container Registry (Tier 3 images).
- GitHub Releases (Tier 2 binary hosting).
- Homebrew tap (`homebrew-tamma` companion repo).

## Current state

Landed (from `9f8969d7 feat(cli): implement Epic 8 Distribution & Installation (Stories 8-1 through 8-8)`):
- `@tamma/cli` publishes on every `v*` tag via `publish-npm.yml`.
- Binaries for darwin-arm64, darwin-x64, linux-x64, linux-arm64, win-x64 attached to every GitHub Release with checksums + cosign signatures.
- `install.sh` and `install.ps1` handle OS detection, checksum verification, and PATH management.
- Homebrew tap `meywd/homebrew-tamma` auto-updated by release workflow.
- GHCR images (`tamma-cli`, `tamma-api`, `tamma-dashboard`) built multi-arch (amd64 + arm64).
- `docker-compose.yml` brings up 7 services healthy within ~3 minutes.
- `tamma init` wizard works across all three distribution methods.

Performance targets hit:
- Tier 1 bundle < 500 KB JS (excl. node_modules); `npx @tamma/cli --version` < 30 s cold.
- Tier 2 binary < 60 MB uncompressed, < 25 MB compressed.
- Tier 3 `docker compose up -d` → all healthy < 3 min.

Deferrals / follow-ups:
- AUR and APT packages are out of scope (tracked in later roadmap).
- Native notarization for macOS binaries is deferred — currently Gatekeeper-exempt via Homebrew; direct-download users get a one-time `xattr -d com.apple.quarantine` prompt from `install.sh`.
- Windows MSI installer deferred — `.exe` binary only, via `install.ps1`.

## See also

- [Deployment](../Deployment.md) — operational guide, including the Tier 3 Compose reference.
- [Epic 1: Foundation](Epic-1-Foundation.md) — CLI package + engine entry points.
- [Epic 1.5: Infrastructure](Epic-1.5-Infrastructure.md) — npm / binary / k8s distribution sister work.
- [Home](../Home.md) — quick-start links.
- Impl plans: [`docs/stories/epic-8/`](/stories/epic-8/).
- Reference docs: `docs/architecture/installer-tier1-npx.md`, `docs/architecture/installer-tier2-curl.md`, `docs/architecture/installer-tier3-docker.md`.
- Source: `packages/cli/src/`, `Dockerfile.*`, `docker-compose.yml`, `scripts/install.sh`.

---

_Last refreshed 2026-04-22._
