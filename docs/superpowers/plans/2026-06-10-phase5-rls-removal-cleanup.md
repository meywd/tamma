# Phase 5 — RLS Removal + ProviderKey Disposition + Cleanup (final phase)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the legacy shared-tables RLS machinery (policies, ENABLE/FORCE, the
`prevent_tenant_id_change` function + triggers) from the CP baseline — isolation is schema + role
since Phase 2/3 — settle `ProviderKey` as a backend label (decision 3), and purge the last
"two-mode tenancy" language from code comments, scripts, and project docs (including CLAUDE.md).

**Architecture:** The RLS objects live ONLY in the hand-written trailing `Sql` block of
`Migrations/ControlPlane/20260609205701_InitialControlPlane.cs` (ported verbatim in Phase 0
precisely so this phase could remove them deliberately). Removal = edit that block (delete the
ENABLE/FORCE, policy, and function/trigger sections; KEEP the `tamma_app` role + grants — it is the
least-privilege runtime role independent of RLS — and KEEP the partial/expression indexes, legacy
CHECKs, and the api_keys self-FK). The EF model knows nothing of these objects, so
Designer/snapshot are untouched and `has-pending-model-changes` stays clean. Zero data + wipe-list
deploys mean no transitional migration is needed.

**Parent doc:** `docs/superpowers/plans/2026-06-09-unified-schema-per-tenant.md` (§4 Phase 5,
decision 3; §3 row "RLS removal").

---

## Environment facts (verified 2026-06-10 — do not re-derive)

- Repo `/home/meywd/tamma/apps/tamma-elsa`, branch `feat/wave-b`. Full-suite baseline after Phase 4:
  ~4540, 0 failures. Build/test commands as in prior phases (`sg docker -c` for tests).
- The ported Sql block (InitialControlPlane.cs, ~lines 1150-1360) sections: 1 tamma_app
  role/grants (KEEP), 2 prevent_tenant_id_change function (DROP), 3 four triggers (DROP),
  4 seven DROP-POLICY-guard + CREATE POLICY pairs (DROP), 5 ENABLE/FORCE on 7 tables (DROP),
  6 partial/expression indexes (KEEP), 7 legacy CHECKs + api_keys self-FK (KEEP). Read the actual
  section numbering before editing — it may differ slightly; identify by content. The Down() has a
  `DROP FUNCTION IF EXISTS prevent_tenant_id_change() CASCADE` prologue that goes with section 2/3.
- RLS-era test surface: `tests/Tamma.Api.Tests/Tenancy/AppRoleRegressionTests.cs` carries 2
  env-gated `[Ignore]`/skip tests about Story 28-1 RLS; `TenancySetUpFixture` provisions
  `tamma`/`tamma_app` roles partly for RLS. Audit what remains meaningful: tamma_app
  least-privilege checks stay; policy-dependent assertions go.
- `DbRoleLeastPrivilegeCheck` (referenced in docker-compose.prod.yml comments) asserts the runtime
  connects as tamma_app — INDEPENDENT of RLS; keep.
- `docker/init-db.sql` + `scripts/db/postgres-roles.sql` + `scripts/db/bootstrap-shared-dbs.sh`:
  check for RLS-era statements/comments (the roles themselves stay; policy/`FORCE ROW LEVEL
  SECURITY` statements and "RLS isolation" prose go/get reworded).
- `ProviderKey` (tenants shadow column) + `ITenantEndpointDirectory`/V2 provider path +
  Cranl provisioner + `ProvisioningState`: these are the Epic-30 per-tenant-infrastructure seam.
  Decision 3 (parent doc): ProviderKey = backend label for which provider minted hosting
  infrastructure; NOT a tenancy-mode flag. Phase 5 disposition: KEEP column + V2 path; fix any
  comment/doc that calls it a mode; record the decision as final in the parent doc.
- Two-mode language to purge: `/home/meywd/tamma/CLAUDE.md` ("Multi-tenant provisioning (Cranl)"
  section describes "Shared infrastructure (default) ... via Phase-3 RLS" and the routing
  paragraph; the "Operating Modes" table row "Typical tenancy" is fine), `docker-compose.prod.yml`
  comments ("SHARED-INFRASTRUCTURE mode ... Phase-3 RLS"), `.env.example`, wiki pages, story-doc
  inline claims that describe RLS as CURRENT state (historical narrative stays).

## Boundaries (YAGNI)

- NO dropping the `tamma_app` role or the three-role privilege split. NO ProviderKey column drop.
  NO Epic-30 work. NO touching tenant-schema-side anything (tenant chain has no RLS).
- CLAUDE.md edits: surgical — only the tenancy/RLS-as-current-state claims; do not restructure.

---

### Task 1: Remove RLS objects from the CP baseline

- Edit the trailing Sql block in `20260609205701_InitialControlPlane.cs`: delete the function,
  triggers, DROP-POLICY guards + CREATE POLICY statements, and ENABLE/FORCE statements; keep role/
  grants, indexes, CHECKs, FK. Update the block header comment (it currently says "RLS removal is
  deliberately deferred to unified-tenancy Phase 5" → now: removed in Phase 5; isolation is
  schema + per-tenant role; tamma_app stays as the least-privilege runtime role). Update Down()'s
  function-drop prologue (now unnecessary — remove with a comment if Down() otherwise stands).
- Verify: `dotnet ef migrations has-pending-model-changes -c ControlPlaneDbContext -p src/Tamma.Data -s src/Tamma.Data`
  → no changes. Bare-PG apply: throwaway container, `dotnet ef database update` (design-time env
  var pattern from Phase 0), then assert via psql: `pg_policy` count 0; `pg_class.relrowsecurity`
  false everywhere; `pg_proc` lacks prevent_tenant_id_change; tamma_app role EXISTS; the kept
  indexes/CHECKs/FK still present (spot-check 3). Remove container.
- Tests: run the Tenancy + Admin filters; delete/adjust RLS-policy-dependent tests
  (AppRoleRegressionTests' skipped RLS tests get DELETED — they test dropped objects; keep any
  least-privilege assertions). Full suite is Task 3.
- Commit: `feat(tenancy-p5)!: drop legacy shared-tables RLS — isolation is schema + per-tenant role`

### Task 2: ProviderKey disposition + two-mode language purge

- Grep `ProviderKey` comments/docs for "mode"/"shared-infra" framing → reword to backend-label
  semantics (code comments in TammaModelConfiguration, resolver, provisioner seams; do NOT change
  behavior).
- Purge "RLS isolation"/"shared-infrastructure mode" as CURRENT-state claims:
  `docker-compose.prod.yml` comments, `docker/init-db.sql`, `scripts/db/*`, `.env.example`,
  `/home/meywd/tamma/CLAUDE.md` (the "Multi-tenant provisioning (Cranl)" section: rewrite its two
  bullets to describe the unified model — every tenant = schema + encrypted conn string; placement
  via tenant_databases; Cranl/V2 = optional per-tenant infrastructure backend that REGISTERS pool
  rows; routing paragraph: resolver is unconditional now), wiki leftovers (grep "RLS" in wiki/ —
  keep history sections, fix current-state).
- Commit: `docs(tenancy-p5): ProviderKey is a backend label; two-mode tenancy language purged`

### Task 3: Full suite + parent-plan closure + final execution record

- Full suite (foreground): 0 failures.
- Parent plan: Phase 5 → `**Phase 5 — DONE <date>.**`; deviations: `21. tamma_app role + grants +
  three-role privilege split KEPT (least-privilege runtime role, independent of RLS). 22.
  ProviderKey + V2 endpoint directory KEPT as the Epic-30 backend-label seam (decision 3 final).`
  Add a final "ALL PHASES COMPLETE" note with the end-state invariants (§1 of the parent restated
  as DONE).
- Execution record in THIS plan; wiki/Security.md + wiki/Architecture.md RLS sections updated.
- Commit `docs(tenancy-p5): unified schema-per-tenant tenancy COMPLETE` (controller pushes + CI +
  deploy watch).

---

## Self-review notes

- Spec coverage (parent Phase 5): RLS dropped ✓ (T1); ProviderKey retirement-decision finalized ✓
  (T2, keep-as-label per decision 3); backup --schema already done in Phase 2 (note in record).
- The baseline edit is the same hand-edit procedure used successfully for C1/draining; the model
  is untouched so snapshot stays consistent.
- Risk: a test or runtime path that silently DEPENDED on RLS/triggers (e.g., a test asserting a
  TenantId-mutation is blocked by trigger). T1's test-filter run + T3's full suite catch it; if a
  trigger-dependent invariant matters (one-way personal-tenant TenantId), the fix is an app-layer
  guard, not keeping the trigger — decide there and record.

---

## Execution record (2026-06-10)

**Commits:** `6854093b` (T1, RLS drop), `e906b339` (T2, ProviderKey/two-mode purge), plus the
closing docs commit (`docs(tenancy-p5): unified schema-per-tenant tenancy COMPLETE`) on
`feat/wave-b`.

**Task 1 — RLS removal (`6854093b`, 4 files, +34/-425):** From the trailing Sql block of
`20260609205701_InitialControlPlane.cs` (-156 lines net) DELETED: the
`prevent_tenant_id_change()` function + its 4 triggers, 7 DROP-POLICY-guard + CREATE POLICY
pairs, and ENABLE/FORCE ROW LEVEL SECURITY on the 7 shared tables (plus the matching Down()
function-drop prologue). KEPT: `tamma_app` role + grants (least-privilege runtime role,
independent of RLS), partial/expression indexes, legacy CHECKs, api_keys self-FK.
`has-pending-model-changes` clean; bare-PG apply verified (pg_policy=0, relrowsecurity=false
everywhere, function gone, tamma_app present, kept objects spot-checked).
`AppRoleRegressionTests.cs` deleted whole (272 lines — RLS-policy assertions incl. the 2
env-gated Story-28-1 skips; least-privilege enforcement remains via the runtime
`DbRoleLeastPrivilegeCheck`); `TenancySetUpFixture` trimmed of policy provisioning;
`SwitchOrgEndpointTests` pins the app-layer TenantId-immutability convention (deviation 23).

**Task 2 — ProviderKey + language purge (`e906b339`, 29 files, +170/-154):** ProviderKey
reworded everywhere to backend-label semantics (Tenant.cs, TammaModelConfiguration,
ITenantConnectionResolver, provisioner seams, V2 lookup — no behavior change); two-mode /
"Phase-3 RLS" current-state claims purged from `CLAUDE.md`, `docker-compose.prod.yml`,
`scripts/db/*` (4 scripts), `Program.cs`/endpoints comments, and 8 wiki pages (Home,
Architecture, Security, Deployment, Testing, Agent-Dispatch, Secret-Management,
Epic-4-Event-Sourcing). 28-5 backup `--schema` adaptation was already done in Phase 2
(recorded there).

**Task 3 — full suite (post-`e906b339`):** 0 failures. Per project:

| Project | Passed | Skipped | Total |
|---|---|---|---|
| Tamma.Api.Tests | 2855 | 6 | 2861 |
| Tamma.Activities.Tests | 1237 | 0 | 1237 |
| Tamma.Core.Tests | 23 | 0 | 23 |
| Tamma.Platforms.Abstractions.Tests | 66 | 0 | 66 |
| Tamma.Platforms.Gitea.Tests | 96 | 0 | 96 |
| Tamma.Platforms.GitHub.Tests | 63 | 0 | 63 |
| Tamma.Platforms.GitLab.Tests | 97 | 0 | 97 |
| Tamma.Platforms.IntegrationTests | 18 | 3 | 21 |
| Tamma.Platforms.Tests | 90 | 0 | 90 |
| Tamma.Studio.Tests | 30 | 0 | 30 |
| **Total** | **4575** | **9** | **4584** |

(Baseline ~4586 minus the 2 deleted RLS skip-tests → 4584.) Parent plan updated: Phase 5 DONE,
deviations 21-23 recorded, ALL-PHASES-COMPLETE status banner added. wiki/Security.md +
wiki/Architecture.md RLS sections were updated in T2.
