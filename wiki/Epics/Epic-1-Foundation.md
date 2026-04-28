# Epic 1: Foundation & Core Infrastructure

**Status:** Near Complete (12/15 done; 1-10, 1-13, 1-14 in-flight)
**Stories:** 15 (1-0 through 1-14)
**Milestone:** [Epic 1 Milestone](https://github.com/meywd/tamma/milestone/1)
**Tech Spec:** [tech-spec-epic-1.md](https://github.com/meywd/tamma/blob/main/docs/stories/epic-1/tech-spec-epic-1.md)

## Overview

Epic 1 lays the two foundational abstractions that keep Tamma vendor-neutral: an **AI provider interface** that every model or coding-agent integration implements, and a **Git platform interface** that every forge integration implements. Anything higher up the stack — the autonomous loop, quality gates, event sourcing, observability — is written against the interfaces, never a concrete provider.

The epic also delivers the reference implementations the rest of the system boots on: Claude Code as the primary CLI-agent provider, GitHub as the primary Git platform, and the first version of the CLI binary (`tamma start / server / api`) that bundles engine, worker, and service modes. Together these give Tamma a working "one happy path" end-to-end before additional providers and platforms are added.

A third track in Epic 1 adds extensibility: additional AI provider adapters (OpenCode, OpenRouter, Zen MCP delivered; OpenAI / Copilot / Gemini / z.ai / local LLMs still planned), additional Git platforms as ready-but-unimplemented stories (Gitea, Forgejo, Bitbucket, Azure DevOps, plain Git), and an agent-customization layer (`AgentPromptRegistry`, `RoleBasedAgentResolver`) that lets users override per-role prompts without forking the provider.

## Architecture

Two parallel adapter hierarchies sit under a small set of interfaces. `IProvider` is the common base; it branches into `ILLMProvider` (chat/complete/analyze/review against a cloud or local LLM API) and `ICLIAgentProvider` (execute a headless coding agent subprocess with session resume). A legacy `IAgentProvider` alias still maps onto `ICLIAgentProvider` for back-compat. On the Git side, `IGitPlatform` is the only seam — one interface covers repos, branches, PRs, issues, comments, commits, and CI status, with platform-specific rate-limiting and error mapping wrapped behind factory classes.

The CLI (`@tamma/cli`) wires the provider and platform factories into three runtime modes: engine (`tamma start` — polls GitHub for labelled issues), server (`tamma server` — HTTP API for a self-hosted deployment), and API (`tamma api` — SaaS/GitHub-App mode that dispatches `workflow_dispatch` events). The orchestrator package (`@tamma/orchestrator`) contains `TammaEngine`, which threads the interfaces together: it takes an `IGitPlatform` plus an `IAgentProvider` and runs one issue end-to-end.

## Components

| Component | Purpose | Key files | Status |
|-----------|---------|-----------|--------|
| `@tamma/providers` — Interfaces | `IProvider`, `ILLMProvider`, `ICLIAgentProvider`, `IAgentProvider`, `ProviderCapabilities`, `MessageRequest/Response/Chunk` | `packages/providers/src/types.ts`, `agent-types.ts` | Done |
| `@tamma/providers` — Registry | Register/lookup provider by name; capability discovery | `packages/providers/src/registry.ts`, `factory.ts` | Done |
| `ClaudeAgentProvider` | Reference CLI-agent implementation over `@anthropic-ai/claude-agent-sdk` | `packages/providers/src/claude-agent-provider.ts` | Done |
| `OpenCodeProvider` | Local/cloud CLI-agent via OpenCode | `packages/providers/src/opencode-provider.ts` | Done |
| `OpenRouterProvider` | Multi-model LLM API gateway | `packages/providers/src/openrouter-provider.ts` | Done |
| `ZenMCPProvider` | LLM API via Zen MCP | `packages/providers/src/zen-mcp-provider.ts` | Done |
| `SecureAgentProvider` | Generic decorator that adds input/output sanitization + redaction to any `IAgentProvider` | `packages/providers/src/secure-agent-provider.ts` | Done |
| `AgentPromptRegistry` + `RoleBasedAgentResolver` | Role→agent mapping with per-role prompt overrides | `packages/providers/src/agent-prompt-registry.ts`, `role-based-agent-resolver.ts` | In progress (1-13) |
| `@tamma/platforms` — `IGitPlatform` | Single interface for repo / branch / PR / issue / comment / commit / CI | `packages/platforms/src/types/git-platform.interface.ts` | Done |
| `GitHubPlatform` | Octokit-based reference implementation with rate limiter and error mapper | `packages/platforms/src/github/*` | Done |
| GitLab / Gitea / Forgejo / Bitbucket / Azure DevOps | Story briefs + context XML; code not yet landed | `docs/stories/epic-1/story-1-6`, `story-1-11` | Drafted |
| `@tamma/cli` | Three-mode CLI (`start`/`server`/`api`) with init, status, execute-agent commands | `packages/cli/src/commands/*`, `index.tsx` | Done |
| `@tamma/orchestrator` — `TammaEngine` | Single-issue pipeline: select → analyze → plan → approve → implement → PR → monitor → merge | `packages/orchestrator/src/engine.ts` | Done |
| `@tamma/observability` | Pino logger with OpenSearch transport option | `packages/observability/src/logger.ts` | Done |
| Marketing site | Landing pages on Cloudflare Workers (tamma.dev) | `apps/marketing-site/` | Done |

## Class diagram

```
                    IProvider  <<interface>>
                    + name : string
                    + isAvailable() : Promise<boolean>
                    + dispose() : Promise<void>
                         ^                ^
                         |                |
           +-------------+                +----------------+
           |                                               |
  ILLMProvider <<interface>>              ICLIAgentProvider <<interface>>
  + type = 'llm-api'                      + type = 'cli-agent'
  + capabilities : LLMCapabilities        + capabilities : CLIAgentCapabilities
  + chat(req) : AsyncIterable             + execute(cfg,cb) : Promise<AgentTaskResult>
  + complete(req) : Promise               + resumeSession(id,p,cb)
  + analyze(req) : Promise
  + review(req)  : Promise
  + listModels() : Promise<ModelInfo[]>
           ^                                               ^
           |                                               |
           |                                +--------------+--------------+
           |                                |                             |
  OpenRouterProvider                ClaudeAgentProvider          OpenCodeProvider
  + sendMessage(req) : Iterable     + execute(cfg)               + execute(cfg)
  + sendMessageSync(req) : Promise  - session : AgentSession     - runtime : Subprocess

  ZenMCPProvider  (ILLMProvider)

  SecureAgentProvider  (implements IAgentProvider, wraps IAgentProvider)
  - inner : IAgentProvider
  - sanitizer : IContentSanitizer
  + executeTask(cfg,cb) : Promise<AgentTaskResult>

                    IGitPlatform  <<interface>>
                    + platformName : string
                    + getRepository(owner,repo)
                    + createBranch / getBranch / deleteBranch
                    + createPR / getPR / updatePR / mergePR / addPRComment
                    + listIssues / getIssue / updateIssue / addIssueComment / assignIssue
                    + listCommits / getCIStatus
                         ^
                         |
                  GitHubPlatform
                  - octokit : Octokit
                  - rateLimiter : GitHubRateLimiter
                  - mappers : GitHubMappers
                  + initialize(cfg) ; dispose()

                  TammaEngine  (orchestrator)
                  - platform : IGitPlatform
                  - agent    : IAgentProvider
                  - eventStore? : IEventStore
                  + initialize() / run() / processOneIssue() / dispose()
```

See source — `packages/providers/src/types.ts` for the full interface bodies, `packages/platforms/src/types/git-platform.interface.ts` for the Git contract, and `packages/orchestrator/src/engine.ts` for how they compose.

## Data flow — "process one issue" happy path

```
User CLI        TammaEngine        IGitPlatform (GitHub)     IAgentProvider (Claude)
   |                |                      |                         |
   | tamma start    |                      |                         |
   |--------------->|                      |                         |
   |                | initialize()         |                         |
   |                |--------------------->| verify auth             |
   |                |                      |                         |
   |                | run() loop           |                         |
   |                |--> selectIssue()     |                         |
   |                |    listIssues(label) |                         |
   |                |--------------------->| returns IssueData       |
   |                |<---------------------|                         |
   |                |                      |                         |
   |                |--> analyzeIssue() ───┼────── execute(prompt) ──>|
   |                |                      |                         | Claude reads repo,
   |                |                      |                         | emits plan JSON
   |                |<─────────────────────┼─────── AgentTaskResult ─|
   |                |                      |                         |
   | prompt approve |                      |                         |
   |<───────────────|  awaitApproval()     |                         |
   |   (y/n)        |                      |                         |
   |───────────────>|                      |                         |
   |                |                      |                         |
   |                | createBranch(fromRef)|                         |
   |                |--------------------->| -> Branch               |
   |                |<---------------------|                         |
   |                |                      |                         |
   |                |--> implementCode() ──┼──── execute(plan) ─────>|
   |                |                      |                         | Claude writes
   |                |                      |                         | files + commits
   |                |<─────────────────────┼──── AgentTaskResult ────|
   |                |                      |                         |
   |                | createPR(options)    |                         |
   |                |--------------------->| -> PullRequest          |
   |                |                      |                         |
   |                | monitorAndMerge()    |                         |
   |                |--> poll getCIStatus()|                         |
   |                |     until success    |                         |
   |                |--> mergePR()         |                         |
   |                |--------------------->| -> MergeResult          |
   |                |                      |                         |
   | "merged #123"  |                      |                         |
   |<───────────────|                      |                         |
```

Every step between `TammaEngine` and the external world goes through an interface — a different `IGitPlatform` or `IAgentProvider` plugs in without touching engine code.

## Use cases

- **Solo developer** wants **Tamma to close labelled issues on their GitHub repo**: `tamma init` configures `github.token` + `provider.anthropic.apiKey` → `tamma start` polls for `tamma-auto`-labelled issues → engine plans / approves / implements / PRs / merges → loop continues.
- **SaaS tenant** wants **Tamma to run as a GitHub App across their org**: admin installs GitHub App → `SaaSCoordinator` (Epic 1.5) discovers the installation → `tamma api` mode dispatches `workflow_dispatch` to tenant repos → worker calls back through `IAgentProvider` + `IGitPlatform`.
- **Framework builder** wants **a new AI provider (e.g. local Ollama) to plug in**: implement `ILLMProvider` or `ICLIAgentProvider` → register via `ProviderRegistry.register('ollama', provider)` → set `provider.selected = 'ollama'` in config → no engine changes.
- **Security-conscious operator** wants **every agent call redacted and sanitized**: wrap the concrete agent in `SecureAgentProvider(inner, sanitizer)` at DI time → every `executeTask` input/output flows through the sanitizer → no per-provider code changes.
- **Platform migration** wants **to try GitLab instead of GitHub**: when `GitLabPlatform` (Story 1-6) lands, set `platform.selected = 'gitlab'` in config → engine uses the same pipeline with a different `IGitPlatform` implementation.

## Dependencies

**Upstream:** None — this is the root foundation epic.

**Downstream:**
- [Epic 1.5](Epic-1.5-Infrastructure.md) builds CLI/service modes, Docker, K8s on top of `@tamma/cli` and `@tamma/orchestrator`.
- [Epic 2](Epic-2-Autonomous-Loop.md) turns the engine skeleton into the full 14-step loop.
- [Epic 3](Epic-3-Quality-Gates.md) adds build/test/security gates around the loop.
- [Epic 4](Epic-4-Event-Sourcing.md) wires `IEventStore` into the engine.
- [Epic 5](Epic-5-Observability.md) adds structured logging + metrics + dashboard on top of `@tamma/observability`.
- [Epic 6](Epic-6-Context-Knowledge.md) adds context/RAG/MCP layer consumed by providers.
- [Epic 9](Epic-9-Agent-Management.md) extends `AgentPromptRegistry` / `RoleBasedAgentResolver` into full multi-agent orchestration.
- [Epic 31](Epic-31-Multi-Git-Platform.md) completes the Git-platform implementations for GitLab / Gitea / Forgejo / Bitbucket / Azure DevOps.

## Current state

**Landed** (in `main`):

- Interfaces `IProvider`, `ILLMProvider`, `ICLIAgentProvider`, `IAgentProvider`, `IGitPlatform` — all with unit-test coverage.
- `ClaudeAgentProvider`, `OpenCodeProvider`, `OpenRouterProvider`, `ZenMCPProvider` — 108+ tests passing (per `MEMORY.md`).
- `SecureAgentProvider` decorator — per the content-sanitization plan, wraps any `IAgentProvider` generically.
- `GitHubPlatform` with rate limiter and error mapper — used in production by the deployed engine.
- CLI with `start`, `server`, `api`, `init`, `status`, `execute-agent`, `process-issue`, `upgrade` commands.
- `TammaEngine` orchestrator with state machine (`EngineState` enum) and injectable `IEventStore`.
- `AgentPromptRegistry` + `RoleBasedAgentResolver` (Story 1-13 in progress — benchmarks and A/B testing still TODO).
- Marketing site at tamma.dev on Cloudflare Workers (Story 1-12).

**Stubbed / drafted only:**

- `GitLabPlatform`, `GiteaPlatform`, `ForgejoPlatform`, `BitbucketPlatform`, `AzureDevOpsPlatform`, `PlainGitPlatform` — story briefs + context XML exist under `docs/stories/epic-1/story-1-6/` and `story-1-11/`; no TS code.
- OpenAI, GitHub Copilot, Gemini, z.ai, local-LLM providers — Story 1-10 is in progress; briefs exist under `docs/stories/epic-1/story-1-10/`.
- Performance impact analysis (Story 1-14) — context XML only, ready for dev.

**Drift from briefs:**

- The original Story 1-2 brief named the class `AnthropicClaudeProvider`; the actual implementation is named `ClaudeAgentProvider` and is a CLI-agent (`ICLIAgentProvider`) rather than a plain LLM API (`ILLMProvider`). This is intentional — Claude Code is always run as a subprocess agent, not a chat completion — but the wiki page now reflects the real class name.
- The wiki previously listed "Claude Code" as `AnthropicClaudeProvider`; corrected to `ClaudeAgentProvider`.
- Epic 4 mentions an Emmett/PostgreSQL event store "implemented in Epic 10"; in the current TypeScript tree `@tamma/events` is still a stub (`packages/events/src/index.ts` exports only a placeholder) and `IEventStore` lives in `@tamma/shared/event-store.ts` as `InMemoryEventStore`. The production event store is in the .NET Elsa tree (`apps/tamma-elsa/src/Tamma.Data/`).

## See also

- **Docs:** [docs/stories/epic-1/](https://github.com/meywd/tamma/tree/main/docs/stories/epic-1) — all 15 story briefs and context XML.
- **Tech spec:** [tech-spec-epic-1.md](https://github.com/meywd/tamma/blob/main/docs/stories/epic-1/tech-spec-epic-1.md).
- **Related wiki pages:**
  - [Architecture](Architecture) — overall system architecture.
  - [GitHub Integration](GitHub-Integration) — GitHub-specific operations.
  - [Multi-Git-Platform](Multi-Git-Platform) — progress on non-GitHub platforms (Epic 31).
  - [Epic 1.5: Infrastructure](Epic-1.5-Infrastructure.md) — how the CLI gets packaged and deployed.
  - [Epic 2: Autonomous Loop](Epic-2-Autonomous-Loop.md) — how the engine skeleton becomes the 14-step loop.
- **Code paths:**
  - `packages/providers/src/` — AI provider adapters.
  - `packages/platforms/src/` — Git platform adapters.
  - `packages/orchestrator/src/engine.ts` — `TammaEngine` reference composition.
  - `packages/cli/src/` — CLI entry points.
