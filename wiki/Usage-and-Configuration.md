# Usage & Configuration

How to run Tamma and configure its behaviour: the two operating modes and their entry points, the CLI commands, the per-repo `.tamma/config.json`, convention templates, the prompt-override store, provider/platform configuration, and BYOK (bring-your-own-key).

Related: [Installation & Setup](Installation) · [API Reference](API-Reference) · [Architecture](Architecture) · [Prompt Store (Epic 27)](Epics/Epic-27-Prompt-Store).

## Operating modes

Tamma runs in exactly one of two modes, settled at process start. The mode decides who the **principal** is (who owns prompts, providers, secrets) and what RBAC applies.

| Mode | Entry point(s) | Principal | RBAC | Tenancy |
|------|----------------|-----------|------|---------|
| **single-user** | `tamma start` (engine), `tamma server` (HTTP) | The sole user | none — the user owns everything | one user per instance |
| **saas** | `tamma api` (GitHub App auth), or the `tamma-api` container | The tenant (org) | `tenant_owner` / `tenant_admin` / `member` | many tenants per instance |

### Mode detection (C# API)

`TammaModeProvider.Resolve(IConfiguration)` settles the mode once, for the process lifetime:

1. Explicit `Tamma:Mode` wins — `"saas"` → SaaS; `"single-user"` (also `singleuser` / `single_user`) → SingleUser; any other value throws.
2. Otherwise inferred **SaaS** if `Tamma:TenantSharedSecret` is set **or** the `ControlPlane` connection string is set.
3. Default: **SingleUser**.

> Design rule for any tenant-aware feature: answer "in single-user mode, who owns this?" **and** "in SaaS mode, who owns this?" separately. The C# type is `TammaMode { SingleUser, SaaS }`. (This is distinct from the TypeScript CLI's `TammaConfig.mode` = `standalone | orchestrator | worker`, which selects the CLI *process role*, not the principal model.)

## CLI commands

The CLI binary is `tamma` (from `packages/cli`). Commands (from `packages/cli/src/index.tsx`):

| Command | Purpose | Key options |
|---------|---------|-------------|
| `tamma start` | Start the Tamma engine (single-user). | `--config`, `--dry-run`, `--approval cli\|auto`, `--once`, `-i/--interactive`, `--mode interactive\|service`, `--verbose`, `--debug` |
| `tamma server` | Start the single-user HTTP server. | `--port 3001`, `--host 127.0.0.1` |
| `tamma api` | Start the API in **SaaS** mode (GitHub App auth). Spawns the C# `Tamma.Api.dll` (located via `TAMMA_API_BINARY`, or `apps/tamma-elsa/src/Tamma.Api/bin/{Release,Debug}/net8.0/`, else `dotnet run`). | `--port 3100`, `--host 0.0.0.0`, `--private-key-path` |
| `tamma init` | Scaffold `.tamma/config.json` in the current repo. | `--full-stack`, `--force` |
| `tamma status` | Show engine status. | |
| `tamma process-issue` | Process a single issue (CI / worker). | `--issue`, `--installation-id` |
| `tamma execute-agent` | Run one agent request (invoked by the C# `LocalExecutor`). | `--request`, `--output`, `--repo-dir` |
| `tamma upgrade` | Self-upgrade. | |

## Per-repo config: `.tamma/config.json`

Each repository customises Tamma via `.tamma/config.json`. The schema is `IRepoConfig` (`packages/shared/src/types/repo-config.ts`); the CLI loads it through `loadRepoConfig()` in `packages/cli/src/config.ts`. All fields are optional.

| Field | Type | Purpose |
|-------|------|---------|
| `engine` | object | `approvalMode` (`cli`\|`auto`), `pollIntervalMs`, `ciPollIntervalMs`, `ciMonitorTimeoutMs`. |
| `roles` | `Record<roleName, IRepoRoleConfig>` | Per-role overrides (see below). |
| `phaseRoleMap` | `Record<phase, roleName>` | Maps a workflow phase to an agent role. |
| `security` | object | `sanitizeContent`, `validateUrls`, `gateActions`, `maxFetchSizeBytes`, `blockedCommandPatterns[]`. |
| `conventions` | `string` | **Project coding conventions injected into every LLM prompt via `{{conventions}}`.** |
| `github` | object | `issueLabels[]`, `excludeLabels[]`, `botUsername`. |

`IRepoRoleConfig` (per role): `provider` (required — a key into `providers.json`), `model?`, `allowedTools?[]`, `maxBudgetUsd?`, `systemPrompt?`, `providerPrompts?`.

Config resolution is layered (lowest → highest precedence): **defaults → `~/.tamma/providers.json` → `.tamma/config.json` → environment variables → CLI flags**. A legacy `tamma.config.json` (with top-level `github`/`agent`/`mode`) is still honoured with a deprecation warning.

### The `conventions` field and `{{conventions}}`

The `conventions` string is rendered into every LLM prompt through the `{{conventions}}` template variable (the role templates in `SystemPrompts.cs` embed a `## Conventions\n{{conventions}}` block; `LlmCallWorkflow` supplies the resolved value). You typically start from a **convention template** (below), customise it, and paste it into `conventions`.

> Server-side note: the C# engine's `GET /api/engine/repo-config` reader is currently a **stub returning `{configured: false}`**. The `conventions` string is still resolved for prompts through the convention store / the callback JSON that `ReadRepoConventionsActivity` parses.

## Convention templates

Starter convention documents you can adopt and edit. Exposed **unauthenticated** (metadata is `{key, name, description}`; the full body is under `conventions`):

```
GET /api/convention-templates            # list all (metadata only)
GET /api/convention-templates/{key}      # full template (404 on unknown key)
```

Definitions live in `apps/tamma-elsa/src/Tamma.Api/Services/Conventions/ConventionTemplates.cs`. The catalogue ships **46** templates in four groups (the "20 templates" you may have seen refers only to the language/framework subset):

- **Language / framework (20):** `typescript-node`, `typescript-react`, `typescript-react-native`, `python`, `python-django`, `python-fastapi`, `go`, `rust`, `java`, `kotlin`, `csharp`, `swift`, `swift-uikit`, `dart-flutter`, `c`, `cpp`, `ruby-rails`, `php-laravel`, `elixir-phoenix`, `scala`
- **Action-triggered (11):** `action-write-code`, `action-review-code`, `action-design`, `action-write-tests`, `action-debug`, `action-refactor`, `action-document`, `action-plan`, `action-context-scan`, `action-triage`, `action-deploy`
- **Role-triggered (8):** `role-security-reviewer`, `role-architect`, `role-qa-engineer`, `role-devops-engineer`, `role-tech-lead`, `role-developer`, `role-product-owner`, `role-tech-writer`
- **Cross-cutting (7):** `universal-safety`, `universal-quality`, `git-conventions`, `error-handling`, `api-design`, `database-conventions`, `observability`

### Convention store (DB-backed, tenant/system overrides)

Separate from the static templates, a DB-backed convention store resolves conventions per `(role, action)` with tenant and system-default layers:

```
GET  /api/conventions/                          # list resolved
GET  /api/conventions/defaults                  # system defaults
GET  /api/conventions/defaults/{role}/{action}
POST /api/conventions/resolve                    # resolve for (role, action)
GET  /api/conventions/registry/{roles|actions|role-actions}
GET  /api/conventions/{role}/{action}            # resolved
PUT  /api/conventions/{role}/{action}            # tenant override  (ConventionManage)
DELETE /api/conventions/{role}/{action}          # (ConventionManage)
# platform-owner system defaults:
PUT/DELETE /api/admin/conventions/{role}/{action}
POST       /api/admin/conventions/{role}/{action}/reset
```

## Prompt-override store

Tamma ships immutable **system default** prompts in code and lets you override them per mode. Data model:

```
System defaults (shipped, immutable):
  ├── role identity prompts        (8 roles)
  ├── action base templates        (safety-net defaults)
  └── role+action templates        (good defaults per eligible cell)

Overrides (Postgres `prompt_overrides`):
  ├── single-user mode → keyed by user_id
  └── saas mode        → keyed by tenant_id (owner/admin edit; members read-only)
```

**Roles (8, from `AgentRole`):** `developer`, `tester`, `security`, `devops`, `architect`, `product_owner`, `senior_developer`, `tech_writer`.

**Actions:** the action vocabulary is the **72-token `AgentAction` enum** (not a flat 8×10 grid). `RolePhaseMap` holds the per-role eligibility (jagged) — e.g. developer 14 actions, product_owner 12, architect 11, senior_developer 11, devops 12, tester 10, security 9, tech_writer 8 — for roughly **72** non-empty role+action prompt cells.

### Resolution order

Single-user, for `(userId, role, action)`: user override → system role+action default → **error** (never falls back to empty/plain). System prompt `(userId, role)`: user override → system default.

SaaS, for `(tenantId, role, action)`: tenant override → system role+action default → error. No per-user layer — members see the tenant admin's resolved prompt without edit access.

### Endpoints

```
GET  /api/prompts/                               # list all (resolved for principal)
GET  /api/prompts/defaults                       # list system defaults  (alias: /api/prompts/system)
GET  /api/prompts/defaults/{role}/{action}       #   (alias: /api/prompts/system/{role}/{action})
GET  /api/prompts/{role}/{action}                # resolved
PUT  /api/prompts/{role}/{action}                # create/update override   (PromptManage)
DELETE /api/prompts/{role}/{action}              # delete override → fall back to system (PromptManage)
POST /api/prompts/{role}/{action}/reset          # alias for DELETE          (PromptManage)
PUT/DELETE /api/prompts/system/{role}            # role-system-prompt override (PromptManage)
POST /api/prompts/{role}/{action}/render         # render with variables
```

RBAC: reads require `SettingsView`; writes require `PromptManage` (in SaaS = `tenant_owner`/`tenant_admin`; members get 403). There is **no** generic `GET /api/prompts/defaults/:action` (action-only) route — that tier was removed; resolution is override → system default → error.

## Provider & platform configuration

### AI providers

- **TypeScript / CLI:** `~/.tamma/providers.json` (`IProvidersConfig` — `providers: Record<name, {apiKey?, defaultModel?, baseUrl?, timeoutSeconds?}>`, plus `maxBudgetUsd?`, `permissionMode?`), loaded by `loadProvidersConfig()`.
- **C# API (`appsettings.json`):** `Anthropic:ApiKey` / `Anthropic:Model` (default `claude-sonnet-4-20250514`) / `Anthropic:UseMock`; `GitHub:Token` / `Owner` / `Repo` / `ApiBaseUrl`; the connection strings (`DefaultConnection`, `TammaDb`, `TammaAppDb`, `ControlPlane`); plus `Elsa`, `Engine:CallbackUrl`, `Jira`, `Dashboard`, `TammaServer`.
- **Provider cost entity** (`Tamma.Data/Entities/Provider.cs`): platform-global, keyed by canonical `Key` (`anthropic`, `openai`, `google`, `openrouter`, `local`, `claude-code`), with `AuthModel` (`api-key` | `cli-token`) and `Status` (`active` | `retired`). Only `api-key` providers are SaaS-eligible.

Provider health/diagnostics endpoints live under `/api/providers/*`; settings under `/api/config/providers`. See [API Reference](API-Reference#providers).

> Note: some older docs reference `packages/config/src/schemas/provider.schema.ts` / `platform.schema.ts` and `GitHub:AppId` / `GitLab` config keys. Those paths/keys do not exist in the current tree — provider/repo config types live in `packages/shared/`, and GitHub App credentials are supplied via env / the SaaS installation record, not `appsettings.json`.

### Git platforms

GitHub is the live platform (Octokit App client, activated by `GitHub:AppId` + `GitHub:PrivateKey` env — see [GitHub Integration](GitHub-Integration)). Gitea / Forgejo / GitLab drivers are covered by [Multi Git Platform](Multi-Git-Platform); their inbound webhooks are documented in [API Reference → Webhooks](API-Reference#webhooks).

## BYOK — bring your own key

In SaaS, a tenant can register its own AI-provider API keys instead of billing against the platform key. BYOK is **tenant-scoped only** (no per-user layer, mirroring the prompt store); response bodies never contain the raw key (reveal-once metadata only).

```
GET    /api/v1/agents/providers                              # list configured BYOK providers (metadata only)
POST   /api/v1/agents/providers/{provider}/credential        # register     (AgentManage)
POST   /api/v1/agents/providers/{provider}/credential/rotate # rotate       (AgentManage)
DELETE /api/v1/agents/providers/{provider}/credential        # remove → fall back to platform key (AgentManage)
```

**Resolution at call time is BYOK → platform** (`DefaultProviderCredentialResolver`). Each mutation writes a `PROVIDER_KEY.CHANGED.SUCCESS` audit event.

**Gating** (`SaaSProviderGate`, step 1 of `POST /api/v1/llm/call`):

- **single-user** → hard no-op / allow (no lookup, no event).
- **saas** → the provider must be `AuthModel == "api-key"` (a `cli-token` or unknown provider → 400), and the tenant must be entitled (`ITenantProviderEntitlement`), else 403.

Third-party integration credentials (Jira, e-mail) follow the same reveal-once pattern under `/api/v1/integrations/{jira|email}/credential` (`PlatformsManage`).

## Related

- [Installation & Setup](Installation) — Docker stack, `.env`, deploy.
- [API Reference](API-Reference) — the full REST surface, RBAC policies, SSE, webhooks, DCB events.
- [Prompt Store (Epic 27)](Epics/Epic-27-Prompt-Store) · [GitHub Integration](GitHub-Integration) · [Security](Security).
