# Layer 5: Validation & Release

**Duration**: ~72 hours
**Team**: 1 coordinator + rotating reviewers
**Goal**: Validate that the combined work of Layers 1–4 meets production quality bars. Run cross-epic integration tests, performance benchmarks, security audit, staging rehearsal, and release preparation.

**Prerequisite**: Layer 4 merged to `main`. CI green. Migrations 008–017 applied on staging. Deploy Coordinator has a green staging environment.

## Activities

### 5.1 Cross-Epic Integration Test Harness

| Attribute | Value |
|-----------|-------|
| **Description** | Extend Story 9-12's integration test into a full cross-epic harness. Covers happy path + failure modes across Epics 9, 12, 16, 17, 18, 27. |
| **Estimated hours** | 16 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-5-integration-harness` |
| **Branch** | `test/layer-5-cross-epic-harness` |
| **Deploy** | NO |

**Test cases**:

1. **Registration → Onboarding → First Run**
   - New user registers
   - Verifies email
   - Creates an organization
   - Installs GitHub App
   - Selects a repo
   - Kicks off an issue assignment
   - Elsa runs the LLM workflow with the tool loop
   - Diagnostics recorded
   - Event stream shows the full audit trail, tenant-scoped

2. **Multi-Tenant Isolation**
   - Tenant A and Tenant B exist
   - Tenant A user cannot read Tenant B's prompts, diagnostics, events, workflows, users
   - RLS blocks cross-tenant queries even if app code has a bug
   - Platform admin can read both tenants

3. **Provider Failover**
   - Configure two providers in a chain
   - Primary returns 500
   - Circuit breaker opens
   - Secondary takes over
   - Diagnostics capture both calls
   - Health tracker state shared between TS engine and Elsa

4. **Prompt Override**
   - Tenant admin overrides the system default `coder/code-generation` prompt
   - New workflow run uses tenant override
   - Event sourcing captures the override creation

5. **CLI Fallback Mode**
   - Start `tamma` CLI without Postgres
   - Verify in-memory fallbacks engage per `cli-fallback-behavior.md`
   - Run a simple workflow
   - Verify default-tenant scoping

6. **RBAC Enforcement**
   - Member attempts to edit prompts → 403
   - Admin edits prompts → 200
   - Owner deletes a user → 200
   - Member attempts to delete a user → 403
   - Platform admin accesses another tenant → 200

### 5.2 Performance Benchmarks

| Attribute | Value |
|-----------|-------|
| **Description** | Measure p95 latency and throughput for critical paths. Enforce rate limits. |
| **Estimated hours** | 12 |

**Benchmarks** (use Artillery.io, run against staging):

| Endpoint / Operation | Target | Tool |
|---------------------|--------|------|
| `GET /api/v1/agents/:role/resolve` | p95 < 200ms | Artillery |
| `POST /api/v1/diagnostics` | p95 < 150ms | Artillery |
| `POST /api/v1/sanitize` | p95 < 100ms | Artillery |
| `GET /api/v1/prompts/:role/:action` | p95 < 100ms (cached) | Artillery |
| Provider API chain resolution | p95 < 500ms | Custom harness |
| Engine → API → Elsa → API → LLM (full workflow step) | p95 < 3s (excluding LLM latency) | k6 |
| Rate limit enforcement (write endpoints) | 30 req/min per tenant observed | Artillery |
| CLI cold start | p95 < 1000ms | hyperfine |

**Output**: `.dev/findings/layer-5-perf-report.md`

### 5.3 Security Audit

| Attribute | Value |
|-----------|-------|
| **Description** | Manual + automated review of auth flows, RLS, sanitization, secrets handling. |
| **Estimated hours** | 16 |

**Audit checklist**:

- [ ] **Auth flows**
  - oauth2-proxy redirect flow rejects unsigned responses
  - Session cookie has `Secure`, `HttpOnly`, `SameSite=Lax`, domain `.tamma.dev`
  - JWT signature verification rejects tampered tokens
  - Refresh token rotation prevents replay
  - Password reset tokens single-use and expire in 1 hour
  - Email verification tokens single-use and expire in 24 hours
  - Argon2 parameters meet OWASP guidelines (`memoryCost ≥ 64MB`, `timeCost ≥ 3`)
- [ ] **Service-to-service auth**
  - No long-lived shared API keys between services
  - Service JWTs expire within 5 minutes
  - Services rotate their signing secret on deploy
- [ ] **RLS**
  - Every tenant-scoped table has RLS enabled
  - `tamma_app` role is used by runtime connections
  - `SET app.current_tenant_id` is called at the start of every request
  - Missing `SET` results in zero rows (not an error), verified by integration test
- [ ] **Sanitization**
  - Default rules block prompt injection attempts (`ignore previous instructions`, etc.)
  - Credential patterns (AWS keys, GitHub tokens, API keys) are redacted
  - Per-tenant rule overrides cannot disable system-critical rules
- [ ] **Secrets**
  - No secrets in logs (`grep -i "sk_" logs/`, etc.)
  - No secrets in error messages
  - No secrets in event data (events are immutable — catastrophic if leaked)
  - `.env.example` is committed; `.env` is gitignored
  - Docker secrets / env vars not printed during deploy
- [ ] **Input validation**
  - All JSON body endpoints validate against JSON Schema
  - File path inputs reject `..` and absolute paths
  - SQL parameters use placeholders (no string concatenation)
- [ ] **CodeQL / Dependabot**
  - Run CodeQL on `main`; fix any new alerts
  - Update vulnerable dependencies
- [ ] **OWASP Top 10 review**
  - Injection, broken auth, sensitive data exposure, XSS, CSRF, SSRF, broken access control, security misconfig, insecure deserialization, components with known vulns

**Output**: `.dev/findings/layer-5-security-audit.md` with checklist status and remediation items.

### 5.4 Staging Deploy Rehearsal

| Attribute | Value |
|-----------|-------|
| **Description** | Do a full from-scratch deploy on staging to catch any missing env vars, config, migrations. |
| **Estimated hours** | 8 |

**Steps**:

1. Destroy staging DB; recreate empty
2. Run all migrations 001–017 in order
3. Deploy containers in layered order (postgres → rabbitmq → elsa → APIs → dashboard + nginx + oauth2-proxy)
4. Smoke test the cross-epic harness (section 5.1) against staging
5. Verify DNS (`app.tamma.dev`, `dash.tamma.dev`, `elsa.tamma.dev`, `logs.tamma.dev`, `api.tamma.dev`) all load
6. Verify GitHub App webhook delivery
7. Deploy Coordinator signs off

**Output**: `.dev/findings/layer-5-staging-rehearsal.md` with timing and any issues encountered.

### 5.5 Wiki/Docs Refresh

| Attribute | Value |
|-----------|-------|
| **Description** | Update `wiki/` and in-repo docs to reflect the new multi-tenant, unified-auth architecture. |
| **Estimated hours** | 12 |

**Docs to update**:

- `wiki/Architecture.md` — new multi-tenant diagram, auth flow
- `wiki/Deployment-Guide.md` — oauth2-proxy, env vars, migration sequence
- `wiki/User-Guide.md` — registration, org creation, GitHub App install, prompts
- `wiki/Admin-Guide.md` — admin dashboard, system prompts, user management
- `wiki/CLI-Guide.md` — CLI fallback mode, `tamma.config.json`
- `docs/architecture.md` — update sections on auth, tenancy, prompt store, agent resolution
- `README.md` — new setup steps, pointers to wiki

### 5.6 PR Organization & Release Notes

| Attribute | Value |
|-----------|-------|
| **Description** | Compile release notes from merged PRs, organize them by epic, write migration notes for operators. |
| **Estimated hours** | 8 |

**Release notes sections**:

- **What's New**: top-line features (self-service registration, unified auth, multi-tenant prompts, agentic tool loop)
- **Breaking Changes**: migration 008–017 required; new env vars; oauth2-proxy replaces direct OAuth
- **Migration Guide**: for existing single-tenant deploys upgrading to multi-tenant
- **Bug Fixes**: 12-5c, 12-5e
- **Deprecations**: direct GitHub OAuth routes (now proxied)
- **Contributors**: thank contributors across the layers

**Output**: `CHANGELOG.md` update + `RELEASE_NOTES.md` for the specific release version.

## Success Criteria (All)

- [ ] Cross-epic integration tests pass on `main`
- [ ] Performance benchmarks meet targets; rate limits enforced
- [ ] Security audit checklist complete with zero high-severity open items
- [ ] Staging deploy rehearsal successful
- [ ] Wiki/docs updated
- [ ] Release notes compiled
- [ ] CODEOWNERS file updated if new packages/teams formed
- [ ] Deploy Coordinator approves release
- [ ] Epic 9, 12 (12-5 + 12-7 scope), 16, 17, 18, 27 marked Complete in `docs/epics.md`

## Exit Criteria

Once success criteria are met:

1. Coordinator announces: `Layer 5 complete. Ready for production release.`
2. Open a release PR (`release/vX.Y.0`) that bumps versions in `package.json`, updates `CHANGELOG.md`
3. After merge, tag the release and publish.
4. Close all Epic milestones in GitHub.
5. Archive worktrees:
   ```bash
   cd /home/meywd/tamma
   git worktree list | awk '/layer-/ {print $1}' | xargs -I{} git worktree remove {}
   ```

## Post-Release Monitoring

In the first 48 hours after release:

- Monitor error rates on each API endpoint
- Monitor auth failures (oauth2-proxy, login, service-to-service)
- Monitor DB connection count (per-request `SET app.current_tenant_id` adds overhead)
- Monitor circuit breaker state (are providers flapping?)
- Monitor rate limit hit rate (are defaults appropriate?)

Set up alerts on:

- `ERROR` log rate > baseline + 50%
- API p95 latency > target + 100%
- Any 5xx response from `/auth/*`, `/agents/*`, `/prompts/*`
- RLS policy violations (should be zero)

---

**End of Layer 5**. Congratulations — Epics 9, 12, 16, 17, 18, 27 are shipped.

See [`README.md`](./README.md) for the overall layered plan and dependency graph.
