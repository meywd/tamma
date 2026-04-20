# Security

Tamma's security model is defence-in-depth: the database enforces tenant isolation via RLS, the API layer validates and sanitises every inbound edge, and the LLM pipeline sanitises every outbound prompt and every inbound agent output. This page is the map.

## Tenant isolation (Phase-3 RLS)

Every tenant-scoped table carries:

1. A `tenant_id` column (`uuid`, NOT NULL).
2. An EF query filter that reads the ambient tenant from `ITenantContextAccessor`.
3. A Postgres RLS policy `USING (tenant_id = current_setting('app.current_tenant_id')::uuid)`.

Per-request, a DbCommand interceptor runs `SET LOCAL app.current_tenant_id = '<tenantId>'` before the first query. If no tenant is in scope, the query filter **fails closed** to an empty result set — this is the `feat/auth-foundation` orgs/002 fix.

The app connects as the non-superuser **`tamma_app`** role (no `BYPASSRLS`). The admin/superuser connection string is only used for migrations and background services. See [Deployment → Phase-3 RLS runbook](Deployment#phase-3-rls-runbook).

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

## Fail-closed defaults

Several controls default to the safe option when unconfigured:

- RLS: no tenant in scope → empty result set.
- GitHub webhook signature: `WebhookSecret` unset → reject webhook (not accept).
- GitHub App client: `AppId`/`PrivateKey` unset → `NullGitHubAppClient` returns 503, not a silent success.
- GitHub Actions client: ditto → `NullGitHubActionsClient`.
- Tenant provisioner: `Cranl:ApiKey` unset → `NullTenantProvisioner` returns 501.
- LLM circuit breaker + budget guards: `LlmCallWorkflow` refuses to dispatch if either check returns unknown.

## Audit findings tracked

The TS → C# port audit tracks security regressions and fixes per-scope in `docs/audit/port-gaps/`. The auth-foundation sprint landed:

- All P0 findings under `auth/` (scrypt compat, JWT shape, API key hash, session cookie, email-verification, refresh-token rotation, OAuth callback, OAuth state CSRF, role-check permission map, `/me` cookie read).
- All findings under `github/` (webhook fail-closed, idempotency, rate limiting, install/rotation provisioner seam).
- All findings under `engine/` (cross-tenant guards, SaaS shape parity, idempotent upsert, context store, DTO realignment).

See [Port Audit](Port-Audit) for the full rollup.
