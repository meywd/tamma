# Wave 2 Implementation Plan Inventory

**Status**: In progress
**Authored**: 2026-04-20
**Scope**: every Layer 4 and Layer 5 story (plus Epics 28/29/30 and follow-up
story 19-6) that must have a per-story implementation plan in the shape of
`docs/stories/epic-19/19-1-phase-1-impl-plan.md`.

This inventory is the contract for the Wave 2 documentation task. Each row
records whether a plan exists today and the action taken in Wave 2.

## Legend

- **yes (existing)** — plan already committed prior to Wave 2.
- **yes (new)** — plan written in this wave.
- **blocker** — brief is too thin or story is explicitly deferred; a
  "planning blocker" note is substituted for a full plan. Requires a
  human decision before writing.
- **skip (covered)** — story already subsumed by a broader impl plan and
  does not need a standalone one.

All paths below are absolute-from-repo-root.

## Layer 4 Team A — Epic 9 Completion

| Story ID | Brief path | Has impl plan? | Action | Plan path |
|----------|-----------|:---:|--------|-----------|
| 9-5  | docs/stories/epic-9/story-9-5/9-5-provider-chain.md | yes (existing) | skip | docs/stories/epic-9/story-9-5/9-5-provider-chain-impl-plan.md |
| 9-9  | docs/stories/epic-9/story-9-9/9-9-engine-integration.md | yes (existing) | skip | docs/stories/epic-9/story-9-9/9-9-engine-integration-impl-plan.md |
| 9-10 | docs/stories/epic-9/story-9-10/9-10-cli-wiring.md | yes (existing) | skip | docs/stories/epic-9/story-9-10/9-10-cli-wiring-impl-plan.md |
| 9-11 | docs/stories/epic-9/story-9-11/9-11-diagnostics-queue-mcp-interceptors.md | yes (existing) | skip | docs/stories/epic-9/story-9-11/9-11-diagnostics-queue-elsa-integration-impl-plan.md |
| 9-12 | docs/stories/epic-9/story-9-12/9-12-cross-epic-integration-test.md | no | yes (new) | docs/stories/epic-9/story-9-12/9-12-cross-epic-integration-test-impl-plan.md |

## Layer 4 Team B — Prompt Store UIs

| Story ID | Brief path | Has impl plan? | Action | Plan path |
|----------|-----------|:---:|--------|-----------|
| 27-4 | docs/stories/epic-27/27-4-prompt-store-admin-ui.md | yes (existing) | skip | docs/stories/epic-27/27-4-prompt-store-admin-ui-impl-plan.md |
| 27-5 | docs/stories/epic-27/27-5-prompt-store-account-ui.md | yes (existing) | skip | docs/stories/epic-27/27-5-prompt-store-account-ui-impl-plan.md |

## Layer 4 Team C — Epic 18 Completion

| Story ID | Brief path | Has impl plan? | Action | Plan path |
|----------|-----------|:---:|--------|-----------|
| 18-3 | docs/stories/epic-18/18-3-organization-tenant-creation.md | yes (existing) | skip | docs/stories/epic-18/18-3-organization-tenant-creation-impl-plan.md |
| 18-4 | docs/stories/epic-18/18-4-github-app-installation-onboarding.md | no | yes (new) | docs/stories/epic-18/18-4-github-app-installation-onboarding-impl-plan.md |
| 18-5 | docs/stories/epic-18/18-5-user-facing-dashboard-shell.md | no | yes (new) | docs/stories/epic-18/18-5-user-facing-dashboard-shell-impl-plan.md |

## Layer 4 Team D — Epic 12 Prompt-Engineering + Context Tools

| Story ID | Brief path | Has impl plan? | Action | Plan path |
|----------|-----------|:---:|--------|-----------|
| 12-5a | docs/stories/epic-12/story-12-5/12-5-prompt-engineering-framework.md §12-5a | no | yes (new) | docs/stories/epic-12/story-12-5/12-5a-context-truncation-impl-plan.md |
| 12-5b | docs/stories/epic-12/story-12-5/12-5-prompt-engineering-framework.md §12-5b | no | yes (new) | docs/stories/epic-12/story-12-5/12-5b-few-shot-injection-impl-plan.md |
| 12-5d | docs/stories/epic-12/story-12-5/12-5-prompt-engineering-framework.md §12-5d | no | yes (new) | docs/stories/epic-12/story-12-5/12-5d-ab-testing-hooks-impl-plan.md |
| 12-7a | docs/stories/epic-12/story-12-7/12-7a-vector-db-search-tools.md | yes (existing) | skip | docs/stories/epic-12/story-12-7/12-7a-impl-plan.md |
| 12-7b | docs/stories/epic-12/story-12-7/12-7b-convention-and-history-tools.md | yes (existing) | skip | docs/stories/epic-12/story-12-7/12-7b-impl-plan.md |
| 12-7c | docs/stories/epic-12/story-12-7/12-7c-context-budget-manager.md | yes (existing) | skip | docs/stories/epic-12/story-12-7/12-7c-impl-plan.md |
| 12-7d | docs/stories/epic-12/story-12-7/12-7d-tool-access-config-per-role.md | yes (existing) | skip | docs/stories/epic-12/story-12-7/12-7d-impl-plan.md |
| 12-7e | docs/stories/epic-12/story-12-7/12-7e-elsa-tool-loop-integration.md | yes (existing) | skip | docs/stories/epic-12/story-12-7/12-7e-impl-plan.md |

## Layer 4 Post-Epic-19 Follow-up

| Story ID | Brief path | Has impl plan? | Action | Plan path |
|----------|-----------|:---:|--------|-----------|
| 19-6 | docs/stories/epic-19/story-19-6-wire-app-role-context.md | no | yes (new) | docs/stories/epic-19/story-19-6-wire-app-role-context-impl-plan.md |

## Layer 4 Phase A/B/C — Epic 28 Database-per-Tenant

| Story ID | Brief path | Has impl plan? | Action | Plan path |
|----------|-----------|:---:|--------|-----------|
| 28-1  | docs/stories/epic-28/story-28-1/28-1-ef-migration-scripts.md | no | yes (new) | docs/stories/epic-28/story-28-1/28-1-ef-migration-scripts-impl-plan.md |
| 28-2  | docs/stories/epic-28/story-28-2/28-2-control-plane-dbcontext-split.md | no | yes (new) | docs/stories/epic-28/story-28-2/28-2-control-plane-dbcontext-split-impl-plan.md |
| 28-3  | docs/stories/epic-28/story-28-3/28-3-tenant-dbcontext-factory.md | no | yes (new) | docs/stories/epic-28/story-28-3/28-3-tenant-dbcontext-factory-impl-plan.md |
| 28-4  | docs/stories/epic-28/story-28-4/28-4-connection-pool-resolver.md | no | yes (new) | docs/stories/epic-28/story-28-4/28-4-connection-pool-resolver-impl-plan.md |
| 28-5  | docs/stories/epic-28/story-28-5/28-5-create-delete-tenant-workflows.md | no | yes (new) | docs/stories/epic-28/story-28-5/28-5-create-delete-tenant-workflows-impl-plan.md |
| 28-6  | docs/stories/epic-28/story-28-6/28-6-platform-events-queue-outbox.md | no | yes (new) | docs/stories/epic-28/story-28-6/28-6-platform-events-queue-outbox-impl-plan.md |
| 28-7  | docs/stories/epic-28/story-28-7/28-7-api-key-prefix-routing.md | no | yes (new) | docs/stories/epic-28/story-28-7/28-7-api-key-prefix-routing-impl-plan.md |
| 28-8  | docs/stories/epic-28/story-28-8/28-8-tenant-context-middleware.md | no | yes (new) | docs/stories/epic-28/story-28-8/28-8-tenant-context-middleware-impl-plan.md |
| 28-9  | docs/stories/epic-28/story-28-9/28-9-jwt-claims-switch-org.md | no | yes (new) | docs/stories/epic-28/story-28-9/28-9-jwt-claims-switch-org-impl-plan.md |
| 28-10 | docs/stories/epic-28/story-28-10/28-10-platform-analytics-rollup.md | no | yes (new) | docs/stories/epic-28/story-28-10/28-10-platform-analytics-rollup-impl-plan.md |
| 28-11 | docs/stories/epic-28/story-28-11/28-11-admin-tenant-status-ux.md | no | yes (new) | docs/stories/epic-28/story-28-11/28-11-admin-tenant-status-ux-impl-plan.md |
| 28-12 | docs/stories/epic-28/story-28-12/28-12-postgres-roles-kek-rotation.md | no | yes (new) | docs/stories/epic-28/story-28-12/28-12-postgres-roles-kek-rotation-impl-plan.md |
| 28-13 | docs/stories/epic-28/story-28-13/28-13-openbao-kms-backend.md | no | blocker | docs/stories/epic-28/story-28-13/28-13-openbao-kms-backend-planning-blocker.md |

## Layer 4 — Epic 29 Platform Secret Management

| Story ID | Brief path | Has impl plan? | Action | Plan path |
|----------|-----------|:---:|--------|-----------|
| 29-1  | docs/stories/epic-29/29-1-secret-store-abstraction.md | no | yes (new) | docs/stories/epic-29/29-1-secret-store-abstraction-impl-plan.md |
| 29-2  | docs/stories/epic-29/29-2-postgres-backed-store.md | no | yes (new) | docs/stories/epic-29/29-2-postgres-backed-store-impl-plan.md |
| 29-3  | docs/stories/epic-29/29-3-reveal-once-on-create.md | no | yes (new) | docs/stories/epic-29/29-3-reveal-once-on-create-impl-plan.md |
| 29-4  | docs/stories/epic-29/29-4-platform-admin-ui.md | no | yes (new) | docs/stories/epic-29/29-4-platform-admin-ui-impl-plan.md |
| 29-5  | docs/stories/epic-29/29-5-tenant-admin-ui.md | no | yes (new) | docs/stories/epic-29/29-5-tenant-admin-ui-impl-plan.md |
| 29-6  | docs/stories/epic-29/29-6-rotation-workflow-primitive.md | no | yes (new) | docs/stories/epic-29/29-6-rotation-workflow-primitive-impl-plan.md |
| 29-7  | docs/stories/epic-29/29-7-db-credential-rotation.md | no | yes (new) | docs/stories/epic-29/29-7-db-credential-rotation-impl-plan.md |
| 29-8  | docs/stories/epic-29/29-8-cranl-env-rotation.md | no | yes (new) | docs/stories/epic-29/29-8-cranl-env-rotation-impl-plan.md |
| 29-9  | docs/stories/epic-29/29-9-migrate-stopgap-secrets.md | no | yes (new) | docs/stories/epic-29/29-9-migrate-stopgap-secrets-impl-plan.md |
| 29-10 | docs/stories/epic-29/29-10-delete-stopgaps.md | no | yes (new) | docs/stories/epic-29/29-10-delete-stopgaps-impl-plan.md |

## Layer 5 — Epic 30 Pluggable Tenant Infrastructure Provisioning

| Story ID | Brief path | Has impl plan? | Action | Plan path |
|----------|-----------|:---:|--------|-----------|
| 30-1  | docs/stories/epic-30/30-1-provisioner-interface-v2.md | no | yes (new) | docs/stories/epic-30/30-1-provisioner-interface-v2-impl-plan.md |
| 30-2  | docs/stories/epic-30/30-2-provisioning-workflow-dispatch.md | no | yes (new) | docs/stories/epic-30/30-2-provisioning-workflow-dispatch-impl-plan.md |
| 30-3  | docs/stories/epic-30/30-3-cranl-provider-refactor.md | no | yes (new) | docs/stories/epic-30/30-3-cranl-provider-refactor-impl-plan.md |
| 30-4  | docs/stories/epic-30/30-4-hetzner-cloud-provider.md | no | yes (new) | docs/stories/epic-30/30-4-hetzner-cloud-provider-impl-plan.md |
| 30-5  | docs/stories/epic-30/30-5-cloudflare-provider.md | no | yes (new) | docs/stories/epic-30/30-5-cloudflare-provider-impl-plan.md |
| 30-6  | docs/stories/epic-30/30-6-byo-provider.md | no | yes (new) | docs/stories/epic-30/30-6-byo-provider-impl-plan.md |
| 30-7  | docs/stories/epic-30/30-7-onboarding-ui.md | no | yes (new) | docs/stories/epic-30/30-7-onboarding-ui-impl-plan.md |
| 30-8  | docs/stories/epic-30/30-8-per-tenant-routing.md | no | yes (new) | docs/stories/epic-30/30-8-per-tenant-routing-impl-plan.md |
| 30-9  | docs/stories/epic-30/30-9-deprovisioning-workflow.md | no | yes (new) | docs/stories/epic-30/30-9-deprovisioning-workflow-impl-plan.md |
| 30-10 | docs/stories/epic-30/30-10-cost-quota-dashboard.md | no | yes (new) | docs/stories/epic-30/30-10-cost-quota-dashboard-impl-plan.md |

## Totals

- Layer 4 Team A: 5 stories — 4 existing, 1 new (9-12).
- Layer 4 Team B: 2 stories — 2 existing.
- Layer 4 Team C: 3 stories — 1 existing, 2 new (18-4, 18-5).
- Layer 4 Team D: 8 stories — 5 existing, 3 new (12-5a, 12-5b, 12-5d).
- Post-Epic-19 follow-up: 1 story — 1 new (19-6).
- Epic 28: 13 stories — 0 existing, 12 new, 1 blocker (28-13).
- Epic 29: 10 stories — 0 existing, 10 new.
- Epic 30: 10 stories — 0 existing, 10 new.
- Layer 5 validation activities (sections 5.1–5.6) are owned by a single
  coordinator, not turned into stories with impl plans. They are
  execution checklists in `layer-5-validation.md`; no per-activity plan
  is written in Wave 2. Activity 5.1's cross-epic harness **extends**
  Story 9-12 and is covered by that story's plan.

Grand total: 52 stories. 12 already had plans, 39 newly written, 1
explicitly marked as a planning blocker (28-13). 0 stories are
"subsumed" — every row in scope has a row here.

## Commit sequence

1. **This inventory** — one commit: `docs(plans): wave-2 impl plan inventory (pre-write)`.
2. One commit per epic of newly-written plans:
   - `docs(stories): Epic 9 Layer-4 impl plan (9-12)`
   - `docs(stories): Epic 12 Layer-4 impl plans (12-5a, 12-5b, 12-5d)`
   - `docs(stories): Epic 18 Layer-4 impl plans (18-4, 18-5)`
   - `docs(stories): Epic 19 follow-up impl plan (19-6)`
   - `docs(stories): Epic 28 impl plans (28-1..28-12) + 28-13 blocker`
   - `docs(stories): Epic 29 impl plans (29-1..29-10)`
   - `docs(stories): Epic 30 impl plans (30-1..30-10)`
3. **Summary commit** — update this inventory to reflect "yes (new) →
   yes (written)"; commit: `docs(plans): wave-2 impl plan inventory + summary`.

## Planning blocker — 28-13

**Why blocked**: Story 28-13 (OpenBao KMS backend for tenant KEK) is
explicitly marked `Status: DEFERRED` in the brief and in
`~/.claude/projects/-home-meywd-tamma/memory/MEMORY.md`
(`project_epic28_kek_decision.md`). A full impl plan would lock in
OpenBao-specific topology, operator tooling, and rotation runbooks
**before** any of the four trigger conditions have fired:

1. First paying tenant onboarded (today: none — Tamma dogfoods itself).
2. Compliance finding (SOC 2 / ISO 27001 auditor flags env-var KEK).
3. 10+ tenants (today: ~0 — blast-radius argument has not shifted).
4. OpenBao reaches LF graduation **and** operators agree to adopt.

**What's needed to unblock**:

- Product decision: does a trigger now apply, or are we still
  in "defer" mode? If still deferred, keep the blocker note; if
  unblocked, we can write the plan.
- Operator decision: OpenBao topology (single HA cluster vs.
  per-region vs. sidecar). Affects migration cost by ~15 hours.
- Security decision: keep env-var KEK as a break-glass fallback
  behind a feature flag, or hard-delete it after 28-13 merges?

Until those three decisions are made, the blocker note at
`docs/stories/epic-28/story-28-13/28-13-openbao-kms-backend-planning-blocker.md`
captures the open questions and references the trigger-condition
table in the brief.

## Change log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-04-20 | 1.0 | Initial inventory — pre-write | Wave 2 docs |
