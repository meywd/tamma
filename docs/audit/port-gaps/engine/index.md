# Engine / Workflows / SaaS / Dashboard Port Gaps

Scope: ENGINE / WORKFLOWS / SAAS / DASHBOARD — the engine-callback surface, workflow
orchestration, GitHub-App SaaS tier, and admin dashboard. Reconstructed from the
pre-delete snapshot at commit `9e9a57c~1` vs the current C# implementation on
branch `feat/auth-foundation`.

Reference audit: `/tmp/tamma-audit/33-engine.md` (37 endpoints, ~80–110h estimate).

Template: `docs/audit/port-gaps/TEMPLATE.md`.

## Severity index

### P0 — cutover-blocking

| # | Title | Est |
| --- | --- | --- |
| [001](001-execute-task-stub.md) | `POST /api/engine/execute-task` one-line stub with wrong DTO shape | 8–12h |
| [005](005-repo-config-stub.md) | `GET /api/engine/repo-config` returns `{configured:false}` | 3h |
| [006](006-issues-list-stub.md) | `GET /api/engine/issues` returns `[]` | 3h |
| [007](007-security-alerts-stub.md) | `GET /api/engine/security-alerts` returns `[]` | 3h |
| [008](008-issue-comment-stub.md) | `POST /api/engine/issue-comment` stub | 2h |
| [009](009-issue-labels-stub.md) | `POST/DELETE /api/engine/issue-labels` stubs | 3h |
| [010](010-create-issue-stub.md) | `POST /api/engine/create-issue` stub | 2h |
| [011](011-trigger-ci-stub.md) | `POST /api/engine/trigger-ci` stub | 2h |
| [016](016-instance-events-cross-tenant-leak.md) | `GetInstanceEvents` leaks cross-tenant events | 4h |
| [021](021-key-rotation-no-reprovision.md) | Key rotation does not re-provision repo secrets | 8h |
| [028](028-eventrepo-rls-bypass.md) | `EventRepository.IgnoreQueryFilters` everywhere — RLS bypass risk | 6h |

### P1 — feature broken

| # | Title | Est |
| --- | --- | --- |
| [002](002-agent-available-verb-mismatch.md) | `GET /api/engine/agent-available` registered as POST with invented body | 1h |
| [003](003-cycle-result-fields-dropped.md) | `POST /api/engine/cycle-result` drops `exitReason` + `error` | 2h |
| [004](004-context-endpoints-semantics.md) | `store-context` / `GET context` / `query-context` return empty results | 6h |
| [012](012-engine-lifecycle-sse-to-json.md) | Engine lifecycle: SSE→one-shot JSON, no real engine binding | 12–16h |
| [013](013-engine-registry-missing.md) | Engine Registry does not exist | 16–20h |
| [014](014-workflow-definition-id-guid-mismatch.md) | Workflow definition id string→Guid breaks Elsa IDs | 4h |
| [018](018-saas-workflow-status-drops-fields.md) | SaaS workflow-status drops step / progress / message | 3h |
| [019](019-saas-workflow-result-tri-to-binary.md) | SaaS workflow-result collapses completed/failed/cancelled → binary | 3h |
| [020](020-saas-key-rotation-id-type.md) | SaaS key rotation id type: Guid vs numeric installation_id | 2h |
| [023](023-dashboard-engines-empty.md) | Dashboard `/engines` hardcoded empty | 2h |
| [029](029-installation-router-no-cache.md) | Installation router has no 60s-TTL cache | 4h |

### P2 — correctness / observability

| # | Title | Est |
| --- | --- | --- |
| [015](015-upsert-definition-find-empty-guid.md) | `UpsertDefinitionAsync` always misses on Guid.Empty | 1h |
| [017](017-saas-llm-proxy-shape-drift.md) | SaaS LLM proxy response shape changed from OpenAI-compatible to flat | 2h |
| [022](022-dashboard-summary-shape-drift.md) | Dashboard `/summary` missing recentEvents, different field names | 3h |
| [024](024-dashboard-workflows-semantics.md) | Dashboard `/workflows`: definitions→instances semantic flip | 3h |
| [025](025-task-queue-pull-to-push.md) | Task Queue: pull→push model change | 4h |
| [026](026-task-queue-no-visibility-timeout.md) | Task Queue: no visibility timeout → zombie processing rows | 3h |

### P3 — drift / contract

| # | Title | Est |
| --- | --- | --- |
| [027](027-task-queue-cross-tenant-processor.md) | Task Queue BackgroundService runs cross-tenant by design | 2h |
| [030](030-installation-soft-delete-vs-hard.md) | Installation soft-delete semantics drift | 2h |

## Cross-scope references

- `docs/audit/port-gaps/orgs/` — RLS bypass cross-reference for finding #016 / #028
- `docs/audit/port-gaps/github/` — Key rotation re-provisioning depends on a GitHub
  App client and the missing `ApiKeyEncrypted` column (finding #021)
- `docs/audit/port-gaps/admin-db/` — Schema drift on `github_installations`

## Total estimated effort

~100h (close to the audit summary's 80–110h band) — concentrated in (a) the engine
callback surface, which is almost entirely stubbed, (b) the registry/SSE lifecycle
surface, which is structurally missing, and (c) a handful of data-model / contract
drifts that silently change what deployed clients observe.
