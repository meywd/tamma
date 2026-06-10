# Security

Tamma's security model is defence-in-depth: the database enforces tenant isolation via schema-per-tenant + per-tenant Postgres roles, the API layer validates and sanitises every inbound edge, and the LLM pipeline sanitises every outbound prompt and every inbound agent output. This page is the map.

## Tenant isolation (unified schema-per-tenant)

Each tenant is physically isolated inside its assigned pool database:

1. A dedicated `t_<hex>` schema holding all of the tenant's application tables.
2. A dedicated per-tenant Postgres role that owns only that schema — its connection string pins `Search Path` to the tenant schema, so cross-tenant reads are impossible at the role level.
3. An AES-GCM-encrypted per-tenant connection string (`tenants.EncryptedConnectionString`), resolved per request by `LruPooledTenantConnectionResolver` against the `tenant_databases` pool.

Control-plane tables that remain shared (tenants, users, memberships) carry EF query filters that read the ambient tenant from `ITenantContextAccessor`; if no tenant is in scope they **fail closed** to an empty result set. The legacy shared-tables RLS layer was removed in unified-tenancy Phase 5 — isolation is schema + per-tenant role.

The control-plane API connects as the non-superuser **`tamma_app`** role (least-privilege: plain DML, no DDL). The admin/superuser connection string is only used for migrations and background services.

## API key hashing

API keys ship in **two** prefixes with different purposes:

| Prefix | Type | Source |
|--------|------|--------|
| `tamma_sk_` | user-scoped key | `POST /api/admin/users/{id}/keys` (user manages own) |
| `tamma_svc_` | service/installation scope | Admin-issued; binds to an org + optional installation |

All API keys are hashed before storage — the raw secret is shown **once** at creation time only. `ApiKeyHasher` uses scrypt with a per-app pepper (N=16384, r=8, p=1 — matches the TS pre-port hash so legacy keys keep verifying). `ApiKeyAuthHandler`:

- Reads `Authorization: Bearer tamma_*_…` or `X-Api-Key`.
- Sanitises presented key into logs (redacts everything after the prefix).
- Populates `HttpContext.User` with a scope-aware `ClaimsPrincipal` (user / installation / service).
- Warns when a key is in its post-rotation grace window.

## Rate limiting

Two layers:

1. **Per-endpoint rate limit** via `RateLimitService` on resend-verification, password-reset, login, and register.
2. **Distributed backend** via `IDistributedRateLimitBackend`:
   - `InMemoryDistributedRateLimitBackend` — single-pod default.
   - `RedisDistributedRateLimitBackend` — Lua `INCR + EXPIRE`. Activated by `ConnectionStrings:Redis`. Multi-pod-safe.

See `apps/tamma-elsa/src/Tamma.Api/Services/RateLimit/`.

## Password strength

`PasswordStrengthValidator` (`apps/tamma-elsa/src/Tamma.Api/Services/Auth/`) enforces:

- Length ≥ 8
- At least one uppercase, one lowercase, one digit
- Not in the **top-1000 common-password list** (embedded from SecLists; auth/013 fix)

Validation runs on register AND password-reset-confirm (TS had it only on register; the C# port closes the gap).

## Sole-owner delete guard

`DELETE /api/v1/orgs/{tenantId}` will refuse to delete a tenant whose sole owner is the authenticated user unless they first transfer ownership (`POST /api/v1/orgs/{tenantId}/transfer-ownership`). Membership cascade cleans up stale rows. See audit finding orgs/019.

## Content sanitizer

`ContentSanitizer` is a C# port (~360 LoC) of the original TS module. Applied at two boundaries:

1. **Inbound** (user input → LLM prompt): strip null bytes, HTML, zero-width characters (bidi-override protection), NFKD normalise, detect prompt-injection patterns in 4 categories + encoding-evasion.
2. **Outbound** (LLM response / MCP tool result → engine logic): strip HTML outside code blocks, remove zero-width characters.

`SecureAgentProvider` wraps any `IAgentProvider` generically, so every provider picks up sanitization without per-provider glue. See `docs/stories/epic-9/story-9-7/` for the original design.

## Error / log sanitization

- `LogSanitizer` strips control characters, bearer tokens, and `tamma_*_` prefixes from any string logged through the structured logger.
- `ErrorRedactor` (used in LLM diagnostics) strips file paths, installation IDs, and provider API keys from error envelopes returned to the client.
- `ApiKeyAuthHandler` sanitises the presented key **before** logging the rejection (CodeQL finding #88-89 fix).

## Outbound HTTP (SSRF protection)

`secureFetch` (TS engine) and the C# equivalents for engine webhooks reject:

- Private IP space (RFC 1918, IPv6 ULA, link-local, loopback).
- Non-allowlisted Content-Type responses.
- Bodies over a configurable size cap.
- Redirect destinations that re-enter any of the above, even after the first hop.

## Shell command gating

`ActionGate` matches shell commands against a **substring blocklist** (no regex — ReDoS-free). Blocks destructive operations (`rm -rf /`, `:(){ :|:& };:`, `curl | sh`, etc.) before they reach `ShellExecuteTool`.

## GitHub secrets provisioning (libsodium)

When Tamma writes secrets into a tenant's GitHub repo (for the `GitHubActionsExecutor` to pick up), it uses `LibsodiumGitHubSecretsProvisioner`:

1. Fetch the repo's public key from the GitHub API.
2. Seal the plaintext secret in a `libsodium` sealed box (public-key cryptography, no shared secret).
3. PUT the ciphertext via `PUT /repos/{owner}/{repo}/actions/secrets/{name}`.

Depends on `Sodium.Core` (native libsodium bindings). Null seam when sodium isn't wired — reports a clean `secrets_provisioner_not_configured` error instead of writing plaintext.

**libsodium is GitHub-only.** Research (`docs/stories/research/multi-git-platform-2026.md §2`) confirms every other platform (Gitea / Forgejo / GitLab / Bitbucket / Azure DevOps) accepts plaintext secrets over TLS and encrypts at rest server-side. Epic 31 Story 31-8 moves libsodium to a GitHub-driver private detail; the `ICiSecretsProvisioner` abstraction is plaintext-in.

## Webhook signal tenant-scoping

`WebhookSignalRegistry` maps GitHub `workflow_run` webhook events to suspended `MonitorAgentWorkflowActivity` bookmarks. Three alias forms, all prefixed with `install:{installId}:` as of commit `9160db1` (code-review finding 5):

| Form | Shape |
|------|-------|
| `run` | `install:{installId}:run:{repo}:{runId}` |
| `branch` | `install:{installId}:branch:{repo}:{branch}` |
| `branch-session` | `install:{installId}:branch:{repo}:{branch}:{sessionId}` |

Before the fix, two tenants with Tamma installed on the same `owner/repo` could cross-wake each other's `AgentMonitorService` via the branch alias and the collector would attempt to download the sibling tenant's artifacts with the wrong installation token. `InstallationRouterService.PublishWebhookSignalAsync` propagates the installation id on every publish.

## Fail-closed defaults

Several controls default to the safe option when unconfigured:

- Tenant scoping: no tenant in scope → EF query filters return an empty result set.
- GitHub webhook signature: `WebhookSecret` unset → reject webhook (not accept).
- GitHub App client: `AppId`/`PrivateKey` unset → `NullGitHubAppClient` returns 503, not a silent success.
- GitHub Actions client: ditto → `NullGitHubActionsClient`.
- Tenant provisioner: `Cranl:ApiKey` unset → `NullTenantProvisioner` returns 501.
- LLM circuit breaker + budget guards: `LlmCallWorkflow` refuses to dispatch if either check returns unknown.

## Artifact download size cap

`AgentResultCollectorService.DownloadResultArtifactAsync` and `OctokitGitHubActionsClient.DownloadArtifactZipAsync` enforce a **4 MB cap** on the downloaded ZIP stream (commit `ced59bc`, review finding 6). GitHub Actions artifacts can be up to 10 GB; a compromised agent that uploads a giant `tamma-result` artifact would OOM the API process and drop every other tenant's request without the cap.

Implementation: a `LimitedStream` wrapper in `DownloadArtifactZipAsync` throws when the cap is exceeded; `AgentResultCollectorService` catches and returns `null` so the compare-API fallback kicks in. `ParseResultJson` additionally clamps each string field to its practical ceiling (2 KB for `error_message` / `branch_name` / `commit_sha`, 32 KB for `agent_log_summary`).

## Dependency hardening (2026-04-20)

Dependabot bumps merged during the sprint:

| Package | From | To | Rationale |
|---------|------|----|-----------|
| `System.Text.Json` | 8.0.0 | 8.0.6 | Closes NuGet advisory (JSON deserialization DoS) |
| `MailKit` | 4.15.1 | 4.16.0 | Closes NuGet advisory (IMAP buffer handling) |

Both advisories are now closed. CI green; no behaviour change.

## Connection-string resolver fix

`ConnectionStringResolver` in `apps/tamma-elsa/src/Tamma.Api/Infrastructure/` now treats `IsNullOrWhiteSpace` values as absent when resolving `ConnectionStrings:TammaDb` / `TammaAppDb`. The `appsettings.json` default for `TammaDb` was cleared to an empty string so the resolver falls back to the env-provided value rather than the (empty-but-non-null) baseline.

Before the fix, deploy-to-VPS was regressing because the empty-string default was being preferred over the production `TAMMA_*__CONNECTIONSTRINGS__TAMMADB` env var.

## Audit findings tracked

The TS → C# port audit tracks security regressions and fixes per-scope in `docs/audit/port-gaps/`. The auth-foundation sprint landed:

- All P0 findings under `auth/` (scrypt compat, JWT shape, API key hash, session cookie, email-verification, refresh-token rotation, OAuth callback, OAuth state CSRF, role-check permission map, `/me` cookie read).
- All findings under `github/` (webhook fail-closed, idempotency, rate limiting, install/rotation provisioner seam).
- All findings under `engine/` (cross-tenant guards, SaaS shape parity, idempotent upsert, context store, DTO realignment).

The **2026-04-20 code review** added 18 findings on top; 4 merge-blockers closed in this sprint (findings 1, 2, 5, 6) and 14 scheduled follow-ups mapped into Epics 29 / 30 / future. See [Port Audit](Port-Audit) for the full rollup.
