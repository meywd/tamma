# Epic 1.5: Infrastructure & Deployment

**Status:** Core infra complete (1.5-1..1.5-15 shipped); secret-management track (1.5-16..1.5-45) in active design
**Stories:** 45 (1.5-1 through 1.5-45)
**Packages:** `@tamma/cli`, `@tamma/api`, `@tamma/orchestrator`, Docker Compose stack, Elsa secret broker, GitHub Actions secret-fetcher
**Tech Spec:** [tech-spec-epic-1.5.md](https://github.com/meywd/tamma/blob/main/docs/stories/epic-1.5/tech-spec-epic-1.5.md)

## Overview

Epic 1.5 is the "how do we ship it" epic: it takes the Epic-1 CLI and turns it into three fully-packaged deployment topologies — **CLI mode** (single binary, `tamma start`), **service mode** (long-running HTTP daemon, `tamma server`), and **SaaS mode** (GitHub-App-authenticated coordinator, `tamma api`). Each mode uses the same `TammaEngine` core, so downstream epics never branch on runtime shape.

The epic also grew a second track after initial scoping: the **LLM-safe secret-management pipeline** (Stories 1.5-16..1.5-45). Because autonomous agents are constantly feeding prompts to LLMs, the platform needs a way to let agents reference secrets by name without ever leaking plaintext into an LLM window. The chosen answer is a *commitment-hash protocol* — workflow variables carry only hashes, and CI/runtime consumers fetch the real plaintext from a secret broker via short-lived OIDC tokens. Epic 1.5 owns this pipeline end-to-end (broker, protocol, platform mirrors, rotation cascade, leak detection); Epic 29 builds the operator-facing cabinet on top.

Production deployment today lives on a Hetzner CPX42 VPS (16 GB, amd64) with a 10-service Docker Compose stack fronted by nginx and Cloudflare. Kubernetes manifests exist (`apps/tamma-elsa/k8s/`) and Story 1.5-10 is in-progress for the full K8s story. GitHub Actions CI covers build/lint/test (`ci.yml`), layered Hetzner deploy (`deploy.yml`), GHCR image publishing (`docker-publish.yml`), smoke tests (`docker-smoke-test.yml`), releases (`release.yml`), and the worker template (`tamma-worker.yml`).

## Architecture

The CLI package carries a thin mode-selector that dispatches to three distinct runtime shapes. `tamma start` instantiates `TammaEngine` in-process and polls GitHub directly — the self-hosted engine. `tamma server` boots a Fastify HTTP server that wraps the same engine plus an API/dashboard surface. `tamma api` is the SaaS coordinator — it authenticates as a GitHub App, discovers installations, and dispatches `workflow_dispatch` events to tenant repos, where a `tamma-worker.yml` runs the engine as a GitHub Action worker.

The secret-management pipeline sits behind an `ISecretStore` seam. The primary store is `TammaVaultStore` (envelope-encrypted rows in Postgres, AES-256-GCM with KEK from env var or later KMS). Platform mirrors (`GitHubSecretStore` via libsodium, GitLab/Gitea/Forgejo/Bitbucket/Azure DevOps) register as additional `ISecretStore` backends. Elsa workflows emit only **commitment hashes**, never plaintext; `FetchSecretsEndpoint` validates OIDC tokens and returns plaintext at execution time. `ProbeSecretWorkflow`, `LeakDetectionWorkflow`, and `RotationCascadeWorkflow` close the loop for validation + rotation.

## Components

| Component | Purpose | Key files | Status |
|-----------|---------|-----------|--------|
| CLI mode selector | Dispatch `start` / `server` / `api` to correct runtime | `packages/cli/src/index.tsx`, `commands/start.tsx` | Done |
| CLI `start` command | Run `TammaEngine` in-process against a local checkout | `packages/cli/src/commands/start.tsx` | Done |
| CLI `server` command | Boot Fastify HTTP server + engine | `packages/cli/src/commands/server.ts` | Done |
| CLI `api` command | SaaS coordinator entrypoint | `packages/cli/src/commands/api.ts` | Done |
| Config loader | Layered config (env → file → flags) with secret redaction | `packages/cli/src/config.ts`, `config-layered.test.ts` | Done |
| Preflight check | Validate provider/platform creds before loop starts | `packages/cli/src/preflight.ts` | Done |
| `TammaEngine` | Single-issue pipeline engine | `packages/orchestrator/src/engine.ts` | Done |
| `SaaSCoordinator` | Discover GitHub App installations, dispatch workflows | `packages/orchestrator/src/saas-coordinator.ts` | Done |
| `WorkflowEngine` | Elsa client that maps to C# workflows | `packages/orchestrator/src/workflow-engine.ts`, `elsa-client.ts` | Done |
| Docker stack | `docker-compose.yml` + `.prod.yml` with 10 services | `apps/tamma-elsa/docker-compose*.yml` | Done |
| Kubernetes manifests | Initial K8s deployment spec | `apps/tamma-elsa/k8s/tamma-deployment.yml` | In progress (1.5-10) |
| CI workflow (`ci.yml`) | Build, lint, test on PR | `.github/workflows/ci.yml` | Done |
| CI workflow (`deploy.yml`) | Layered Hetzner deploy (postgres → rabbitmq → elsa → APIs → dashboard) | `.github/workflows/deploy.yml` | Done |
| CI workflow (`docker-publish.yml`) | Build + push images to GHCR on tag | `.github/workflows/docker-publish.yml` | Done |
| CI workflow (`release.yml`) | GitHub Releases | `.github/workflows/release.yml` | Done |
| Worker template | GitHub Actions worker that runs engine in a tenant repo | `.github/workflows/tamma-worker.yml` | Done |
| GitHub App auth | JWT + installation token refresh via `@octokit/auth-app` | `packages/platforms/src/github/*` | Done |
| Secret broker HTTP service | `POST /secrets/commit`, `POST /secrets/fetch`, `POST /secrets/rotate` | Planned (1.5-17) | Planned |
| `TammaVaultStore` | Envelope-encrypted Postgres-backed secret store | Planned (1.5-17) | Planned |
| Secret Activities | Elsa C# wrappers over broker HTTP API | Planned (1.5-18) | Planned |
| OIDC Trust Registry | Validate short-lived tokens from CI runners | Planned (1.5-20) | Planned |
| Platform secret mirrors | `GitHubSecretStore` / GitLab / Gitea / Forgejo / Bitbucket / Azure DevOps | Planned (1.5-23..1.5-26) | Planned |
| `LeakDetectionWorkflow` | Scan LLM outputs + GitHub secret-scanning webhook | Planned (1.5-28) | Planned |
| `RotationCascadeWorkflow` | Saga-shaped multi-store rotation with compensation | Planned (1.5-30) | Planned |

## Class diagram

```
     CLI entry (packages/cli/src/index.tsx)
            |
            |  dispatch by command
            +--- "start"  ---> EngineMode  ---> TammaEngine
            +--- "server" ---> ServerMode  ---> Fastify + TammaEngine + API routes
            +--- "api"    ---> SaaSMode    ---> SaaSCoordinator

     SaaSCoordinator
     - appAuth : GitHubAppAuth
     - platform : IGitPlatform  (per-installation)
     + discoverInstallations() : Promise<Installation[]>
     + dispatchWorker(repo, workItem) : Promise<WorkflowRun>
     + reconcileLoop()

     TammaEngine  (see Epic 1)
     + run()
     + processOneIssue()

     ISecretStore  <<interface>>                        IRotationHandler  <<interface>>
     + get(ref) : Promise<Plaintext>                    + canHandle(store, kind) : boolean
     + put(ref, plaintext, meta) : Promise<void>        + rotate(ctx) : Promise<RotationResult>
     + delete(ref) : Promise<void>                             ^
     + rotate(ref, newValue) : Promise<void>                   |
     + commit(ref) : Promise<Hash>                    +--------+---------+---------------+
            ^                                         |                  |               |
            |                                 GitHubRotator       PostgresRotator   ApiKeyRotator
   +--------+--------+-----------+--------+
   |                 |           |        |
 TammaVault    GitHubSecret  GitLab     Gitea
 Store         Store         Store      SecretStore
 - kek : KEK
 - pg  : Pool

     LeakDetectionWorkflow  --> AutoRotateWorkflow --> RotationCascadeWorkflow
            ^                                                ^
     GitHub secret-scanning webhook                     probe success
     LLM output scanner                                 probe failure => compensation
```

## Data flow — "CI job needs a secret" happy path

```
Elsa workflow        Secret Broker          TammaVaultStore       GitHub Actions runner
    |                      |                      |                       |
    | Activity: useSecret("stripe_api_key")        |                       |
    | --> commit()         |                      |                       |
    | ------------------>  |                      |                       |
    |                      | lookup ref,          |                       |
    |                      | return hash          |                       |
    |<-------------------- | (no plaintext)       |                       |
    |                      |                      |                       |
    | writes hash to       |                      |                       |
    | workflow variable    |                      |                       |
    |                      |                      |                       |
    | dispatch             |                      |                       |
    | workflow_dispatch    |                      |                       |
    | ------------------------------------ workflow starts ------------->|
    |                      |                      |                       |
    |                      |                      |    fetch-secrets@v1   |
    |                      |   POST /secrets/fetch (OIDC token + hash)   |
    |                      |<-----------------------------------------|   |
    |                      | validate OIDC against trust registry       |
    |                      | get by hash          |                       |
    |                      | --------------------->| decrypt (KEK)        |
    |                      |<----------------------| plaintext            |
    |                      | ---------- ciphertext-in-transit ----------->|
    |                      |                      |          (TLS)        |
    |                      |                      |                       | set as masked env var
    |                      |                      |                       | run job step
    |                      |                      |                       |
    |    (if leak detected later)                  |                       |
    |                      |                      |                       |
    |  LeakDetectionWorkflow <--- scan output --- |                       |
    |                      |                      |                       |
    |  AutoRotateWorkflow ----> RotationCascade --> rotate all mirrors    |
```

## Use cases

- **Solo dev running self-hosted** wants **Tamma on their laptop**: `npm install -g @tamma/cli` → `tamma init` → `tamma start --label tamma-auto` → engine polls their repo in-process.
- **Team running on Hetzner** wants **shared self-hosted deployment**: clone repo → copy env template → `docker compose up -d` → Cloudflare DNS → nginx reverse-proxies to `tamma-api`, `elsa-server`, `tamma-dashboard` → team hits `app.tamma.dev`.
- **SaaS tenant** wants **zero-install onboarding**: install Tamma GitHub App on their org → `SaaSCoordinator` in `tamma api` discovers the installation → pushes a `workflow_dispatch` to a tenant repo → `tamma-worker.yml` spins up a runner → engine runs inside GitHub Actions using installation token.
- **Platform operator** wants **to rotate a leaked API key across all mirrors atomically**: `LeakDetectionWorkflow` fires on GitHub secret-scanning webhook → `AutoRotateWorkflow` resolves the `IRotationHandler` for that store kind → `RotationCascadeWorkflow` rotates in vault, then GitHub secret, then GitLab mirror → compensation reverts on partial failure.
- **Compliance auditor** wants **proof no LLM ever saw a secret plaintext**: inspect workflow variables — only hashes present; inspect broker audit log — every fetch has OIDC-attested consumer identity + short-lived TTL.
- **K8s tenant** wants **to scale engine pods horizontally** (planned, Story 1.5-10): apply `tamma-deployment.yml` → Elsa scheduler distributes `SingleIssueCycle` instances → workers pull from shared RabbitMQ.

## Dependencies

**Upstream:**
- [Epic 1](Epic-1-Foundation.md) — provides `@tamma/cli`, `TammaEngine`, `IGitPlatform`, `IAgentProvider`.

**Downstream:**
- [Epic 2](Epic-2-Autonomous-Loop.md) — runs inside the service/CLI/SaaS modes shipped here.
- [Epic 19](Epic-19-Agent-Dispatch.md) — uses the worker-mode dispatch pattern.
- [Epic 23](Epic-23-System-Monitoring.md) — monitors the Hetzner Docker stack.
- [Epic 25](Epic-25-Wiki-Site.md) — wiki site is deployed via the same CI pipeline.
- [Epic 28](Epic-28-DB-Per-Tenant.md) — consumes the 1.5-16 KEK primitives for per-tenant envelope encryption.
- [Epic 29](Epic-29-Secret-Management.md) — operator-facing cabinet builds on the 1.5-16..1.5-45 track.
- [Epic 31](Epic-31-Multi-Git-Platform.md) — the platform secret mirrors in 1.5-23..1.5-26 are per-platform stores.

## Current state

**Landed** (all in production on Hetzner):

- `tamma start` / `tamma server` / `tamma api` CLI modes (1.5-1..1.5-4).
- Docker Compose stack (1.5-5): Postgres 17, RabbitMQ, ChromaDB, elsa-server (.NET 8), `tamma-api-dotnet`, `tamma-api` (Fastify), `tamma-engine`, `tamma-dashboard`, `elsa-studio`, `nginx-proxy`, optional OpenSearch.
- Health checks + webhook integration (1.5-6), backup/recovery (1.5-7), doc/template/NPM publishing (1.5-8), binary installers (1.5-9).
- GitHub App auth + SaaS Coordinator (1.5-11, 1.5-12).
- GitHub Actions worker mode (1.5-13) — `tamma-worker.yml` template.
- Multi-tenant task queue + webhook routing (1.5-14).
- SaaS API key provisioning into repo Actions secrets (1.5-15).

**Planned** (design complete, not yet implemented):

- 1.5-10 Kubernetes deployment — initial manifests in `apps/tamma-elsa/k8s/`; full story still open.
- 1.5-16..1.5-22 Secret broker + commitment-hash protocol + OIDC trust + CI fetch endpoint + `actions/fetch-secrets/` action.
- 1.5-23..1.5-26 Platform secret mirrors for GitHub / GitLab / Gitea / Forgejo / Bitbucket / Azure DevOps.
- 1.5-27..1.5-31 Rotation & leak detection — `ProbeSecretWorkflow`, `LeakDetectionWorkflow`, `IRotationHandler`, `RotationCascadeWorkflow`, `AutoRotateWorkflow`.
- 1.5-32..1.5-36 Advanced crypto — secret import, drift detection, KMS-backed root key (deferred until trigger fires; env-var KEK is v1).
- 1.5-37..1.5-45 Ops & observability — notification channels, cascade scheduling, operator dashboard, mTLS, self-hosted platform variants, MCP tool surface.

**Drift from briefs:**

- The original Epic 1.5 scope was 10 stories (Core Deployment). It grew to 45 stories after the secret-management track was pulled in from what was initially planned as Epic 29. Epic 29 now owns only the operator-facing cabinet; Epic 1.5 owns the pipeline.
- The original Story 1.5-4 brief bundled "Web Server API" and "Secret Management Integration" together. In practice, secret-management moved entirely into the 1.5-16..1.5-45 track and 1.5-4 delivered only the Fastify API scaffolding.
- KMS-backed root key (1.5-36) is deferred per project decision (`MEMORY.md`: `Epic 28 KEK backend — ship env-var KEK, defer OpenBao/KMS`). The story stays in the epic but does not ship until a trigger fires (paying tenant with breach clause, compliance finding, threat-model change).
- Production deployment uses `elsa-server` for workflow execution; the TypeScript `@tamma/orchestrator` package dispatches to Elsa rather than re-implementing the state machine in TS. Stories assume TS engine; actual execution is a 60/40 split TS/C#.

## See also

- **Docs:** [docs/stories/epic-1.5/](https://github.com/meywd/tamma/tree/main/docs/stories/epic-1.5) — all 45 story briefs.
- **Tech spec:** [tech-spec-epic-1.5.md](https://github.com/meywd/tamma/blob/main/docs/stories/epic-1.5/tech-spec-epic-1.5.md).
- **Related wiki pages:**
  - [Deployment](Deployment) — Hetzner deployment runbook.
  - [Secret Management](Secret-Management) — end-user view of the secret pipeline.
  - [Architecture](Architecture) — overall deployment topology.
  - [Epic 29: Secret Management](Epic-29-Secret-Management.md) — operator cabinet on top of this track.
  - [Epic 1: Foundation](Epic-1-Foundation.md) — the CLI + engine this epic packages.
- **Code paths:**
  - `packages/cli/src/commands/` — CLI mode commands.
  - `packages/orchestrator/src/saas-coordinator.ts` — SaaS coordinator.
  - `apps/tamma-elsa/docker-compose*.yml` — Docker stack.
  - `apps/tamma-elsa/k8s/` — K8s manifests.
  - `.github/workflows/` — CI pipelines.
