---
title: "Tamma Project Roadmap"
sidebar:
  order: 1
---

Comprehensive roadmap covering **37 epics** from foundation through SaaS platform, including the newly scoped Wave-2 epics (28, 29, 30, 31, 33) and the workflow-platform wave (39, 40, 41, 42). _(Epics 27 and 32 are now defined; previous roadmap text marking them "reserved" was stale.)_

_Last audited: 2026-07-24 (workflow-platform wave added: Epic 39 implemented — document-lifecycle spine + producer migrations 39-12…39-15; Epics 40/41/42 scoped/planned)._

## Prioritization (Wave A → D)

Full ranking with scoring methodology, dependency graph, and 10 open product questions: [Layer 4-5 Prioritization (2026-04-21)](https://github.com/meywd/tamma/blob/feat/auth-foundation/docs/stories/plans/layer-4-5-prioritization-2026-04-21.md).

| Wave | Theme | Stories | Hours | Notes |
|------|-------|--------:|------:|-------|
| **A — Launch-critical** | First-customer blockers + P0/P1 security | 20 | ~370 | 19-6 (P0 RLS wiring) leads; Epic 28 phase A/B; 9-5; 18-7/18-8; 29-1; 28-9 |
| **B — Core SaaS features** | Secret mgmt, prompt UIs, Epic 9 completion | 15 | ~290 | 27-4/27-5; remainder of Epic 29; 9-9..9-12; 18-4/18-5; 28-10..28-12 |
| **C — Scale enablement** | Epic 12 agent effectiveness + Epic 31 multi-platform | 20 | ~325 | 12-5a/b/d; 12-7a..e; 31-1..31-5 (GitHub refactor + Gitea/Forgejo); 31-7..31-10 |
| **D — Operational quality** | Multi-backend drivers, Layer 5 validation, P2 cleanup | 16 | ~260 | Epic 30 (Cranl/Hetzner/Cloudflare/BYO); 31-6 GitLab; review P2 backlog |
| **Deferred** | Trigger-gated | 4 | — | 28-13 OpenBao, 31-11 Bitbucket, 31-12 Azure DevOps, Epic 33 Per-Tenant IdP |

**Top 5 next**: 19-6 → 28-1 → 28-2 → 28-3 → 9-5. Detailed rationale in the prioritization doc.

**Wall-clock estimate** at current 4-team parallel model: ~22 weeks (5 months) from Wave A start to Layer 5 complete.

**10 open product questions** gate ordering — see prioritization doc §6 (single vs multi-tenant launch, Cranl-only vs Hetzner-now, SOC 2 timeline, SSO horizon, GitLab at launch, etc.).

## Current scoping inventory (at a glance)

| Layer | Scope | Hours (est.) |
|-------|-------|--------------|
| Layer 4 (integration/UI) | Epic 28 Phase A–C (251h) + Epic 29 (166h) + Epic 31 L4 portion (220h of 284h total) + Epic 18 extensions (46h) | ~683h |
| Layer 5 (validation/scale-out) | Epic 30 (216h) + Epic 31-6 GitLab (36h) + cross-epic harness | ~260h |
| Deferred | Epic 31-11 Bitbucket, Epic 31-12 Azure DevOps, Epic 33 Per-Tenant IdP | trigger-gated |

Review-finding cross-reference: see [Port Audit → Code review (2026-04-20)](Port-Audit#code-review-2026-04-20--18-findings) for how each new epic closes specific findings.

## Epic Overview

| Epic | Name | Stories | Done | Status |
|------|------|---------|------|--------|
| **Epic 1** | Foundation & Core Infrastructure | 15 | 10 | Near Complete |
| **Epic 1.5** | Infrastructure & Deployment | 10 | 9 | Near Complete (K8s in progress) |
| **Epic 2** | Autonomous Development Loop | 20 | 14 | Near Complete (2-14 decomposition landed 2026-07) |
| **Epic 3** | Quality Gates & Intelligence | 13 | 12 | Near Complete (3-4/3-5/3-6/3-7 landed 2026-07; 3-13 remains) |
| **Epic 4** | Event Sourcing & Audit Trail | 8 | 7 | Near Complete (4-5 capture + 4-8 replay landed 2026-07) |
| **Epic 5** | Observability Dashboard & Docs | 14 | 4 | Partially Implemented |
| **Epic 6** | Context & Knowledge Management | 10 | 9 | Near Complete (KB/RAG sidecar live w/ local Ollama embeddings, 2026-07) |
| **Epic 7** | Autonomous Mentorship Workflow | 9 | 8 | Near Complete |
| **Epic 8** | Distribution & Installation | 8 | 8 | Completed |
| **Epic 9** | Config-Driven Multi-Agent Management | 11 | 11 | Completed |
| **Epic 10** | Engine Core -- Workflow-Driven Architecture | 9 | 0 | Drafted (10-9 in progress) |
| **Epic 11** | Security Hardening (ELSA) | 5 | 5 | Completed |
| **Epic 12** | Agentic Tool Loop | 4 | 4 | Completed (+ 3 new L4 plans: 12-5a/b/d) |
| **Epic 13** | Workflow Decomposition | 3 | 3 | Completed |
| **Epic 14** | Custom ELSA Studio | 3 | 3 | Completed |
| **Epic 15** | Observability & Log Aggregation | 1 | 1 | Completed |
| **Epic 16** | Unified Auth, User Management & Admin | 6 | 6 | Completed |
| **Epic 17** | Multi-Tenancy Foundation | 5 | 0 | Drafted |
| **Epic 18** | End-User Auth & Registration | 5 (+ 18-6/18-7/18-8) | 4 | Partially Implemented (18-4 onboarding slices landed 2026-07; 18-4 still in progress) |
| **Epic 19** | GitHub App Agent Dispatch | 6 | 5 | **Completed** (19-6 follow-up drafted) |
| **Epic 20** | Billing & Payments | 5 | 0 | Drafted |
| **Epic 21** | Marketing Site & User Dashboard | 5 | 2 | Partially (21-4 Repos & Runs pages landed 2026-07; marketing site extracted to standalone repo) |
| **Epic 22** | CLI Mode Preservation | 5 | 0 | Drafted |
| **Epic 23** | System Monitoring & Observability Dashboard | 12 | 8 | In Progress — 23-1..23-6, 23-8, 23-12 landed 2026-07; 23-7/23-9/23-10 remain (23-11 superseded) |
| **Epic 24** | Realtime Voice Conversation | 7 | 1 | Drafted (24 task plans) |
| **Epic 25** | Documentation & Wiki Site | 1 | 1 | Completed |
| **Epic 26** | Project Management & Triage | 4 | 0 | Drafted |
| **Epic 28** | Database-per-Tenant Foundation | 13 (1 blocker) | 0 | **New — briefs + 12 impl plans + 28-13 blocker** |
| **Epic 29** | Platform Secret Management | 10 | 0 | **New — briefs + 10 impl plans** |
| **Epic 30** | Pluggable Tenant Infrastructure Provisioning | 10 | 0 | **New — briefs + 10 impl plans** |
| **Epic 31** | Multi Git Platform Support | 10 core + 2 optional | 0 | **New — briefs only** |
| **Epic 33** | Per-Tenant Identity Providers | — | 0 | **New — deferred stub** |
| **Epic 39** | Typed Work Documents & the Universal Lifecycle | 21 | 17 | **Implemented** — spine complete, 39-12…39-15 merged; 39-1/16/17/21 remain |
| **Epic 40** | Resumable Coding Execution | 7 | 0 | Planned / docs — backlog |
| **Epic 41** | Full-Team Workflow Coverage | 28 + 41-29 router | 0 | Planned / docs — backlog |
| **Epic 42** | Agent Capability & Tool Layer | 9 | 0 | Planned / docs — backlog |

_(Epic 32 reserved for future use.)_

---

## Newly scoped epics (2026-04-20 / 2026-04-21)

### Epic 28 — Database-per-Tenant Foundation

**Layer 4** — 13 stories, ~251h (plus 1 blocker).

Infrastructure foundation for the tenant plane: control-plane DbContext split (28-2), tenant DbContext factory (28-3), connection-pool resolver (28-4), create/delete workflows (28-5), platform-events outbox (28-6), API-key prefix routing (28-7), tenant context middleware (28-8), JWT `switch-org` claim (28-9), platform analytics rollup (28-10), admin tenant-status UX (28-11), Postgres roles + KEK rotation (28-12).

Story **28-13 OpenBao KMS backend** is a planning blocker; OpenBao adoption deferred per `project_epic28_kek_decision.md` until trigger conditions fire (paying tenant, compliance finding, 10+ tenants, OpenBao LF graduation).

Source: [`docs/stories/epic-28/`](/stories/epic-28/).

### Epic 29 — Platform Secret Management

**Layer 4** — 10 stories, **166h**.

Typed secret cabinet, envelope encryption (KEK from env per Doc 01 §8.2), reveal-once-on-create UX, rotation workflow primitive + backend-specific handlers, migration of six stopgap secrets, deletion of `TenantSecretProtector`.

**Closes review findings 4, 15, 16** from 2026-04-20 code review. See [Secret Management](Secret-Management).

### Epic 30 — Pluggable Tenant Infrastructure Provisioning

**Layer 5** — 10 stories, **216h**.

`ITenantInfrastructureProvider` v2 + `ProvisioningTopology` enum. Four backend drivers: Cranl refactor (30-3), Hetzner Cloud (30-4), Cloudflare D1+Workers (30-5), BYO (30-6). Provisioning + deprovisioning sagas, onboarding UI, per-tenant routing resolver, cost/quota dashboard.

**Closes review finding 14** (Cranl resource-name collisions via 30-3) + the Cranl-only-coupling design-intent gap. 30-8 closes half of review finding 1 (per-tenant wiring — paired with Story 19-6). See [Multi-Tenant Provisioning](Multi-Tenant-Provisioning).

### Epic 31 — Multi Git Platform Support

**Layer 4 + Layer 5** — 10 core + 2 optional stories, **~228h core**.

`IGitPlatformClient` abstraction + capability matrix + per-tenant platform registry. GitHub refactor + Gitea driver + Forgejo compat shim + GitLab driver + webhook receiver abstraction + `ICiSecretsProvisioner` abstraction + onboarding picker UI + integration test harness. Bitbucket and Azure DevOps drivers deferred.

Research surprises reshaped the story set: Forgejo is a compat shim (not full driver); libsodium is GitHub-only; GitLab's variable model is richer; Azure DevOps mid-migration. See [Multi Git Platform](Multi-Git-Platform).

### Epic 33 — Per-Tenant Identity Providers (deferred)

Forward-looking stub. Three pre-scoped tiers (Lean OIDC ~100h / Full SAML+OIDC ~250h / Full+LDAP ~400h); tier selection happens at activation. Trigger conditions: first enterprise customer with SSO contract term, compliance finding, ≥5 tenants ask in 60 days, SCIM becomes routine sales objection, Tamma Enterprise plan launch. See [Identity Providers](Identity-Providers).

---

## Workflow-platform wave (Epics 39–42)

This wave makes the platform's implicit domain language explicit and extends the single quality lifecycle across the whole team. **Epic 39 is implemented; 40, 41, and 42 are scoped/planned (backlog, not built).**

### Epic 39 — Typed Work Documents & the Universal Lifecycle (Implemented)

**Layer 4** — 21 stories, 17 landed. Defines ~10 typed work documents (Decomposition, Plan, Review, Findings, …) as static C# types in `Tamma.Core`, one generic `produce → validate → review → revise → accept` lifecycle, an orchestrator that routes acceptance by a 70–100 autonomy dial, and a resumable-by-design authoring standard with a structural test. **The producer-migration spine is complete** — 39-12 (pilot) → 39-13 (assessment family) → 39-14 (planning family) → 39-15 (remaining producers) are all merged, so every document-producing workflow now rides `DocumentLifecycleWorkflow`. Remaining: 39-1 (I/O audit), 39-16 (generated prompt contracts), 39-17 (resident orchestrator agent), 39-21 (C# RAG). See [Document Lifecycle](Document-Lifecycle).

### Epic 40 — Resumable Coding Execution (Planned / docs)

**Layer 4** — 7 stories, backlog. Ships the missing `tamma-agent.yml` runner contract (SaaS + single-user) that Story 19-1 specified but never built, and makes the coding step resumable on the 39-10 mechanism — replacing the inline ~35-min monitor with a durable bookmark suspend, the in-memory webhook signal registry with a persisted cross-pod signal, and adding git+events-based per-task re-entry. Code is deliberately NOT a document type; re-entry reads git + DCB events, not the document store. See [Resumable Workflows](Resumable-Workflows).

### Epic 41 — Full-Team Workflow Coverage (Planned / docs)

**Layer 4** — 28 stories + 41-29 the task-level flow router, backlog. Turns every remaining recurring SDLC activity (ADR authoring, sprint planning, standup synthesis, threat modeling, release notes, UX specs, …) into a first-class lifecycle workflow on the Epic 39 spine — thin bindings, no new architecture. Adds three new `AgentRole`s (`scrum_master`, `project_manager`, `ux_designer`) and six new document types via 41-1. **41-29** is the activation story: it adds a task `kind` to the `Plan` and dispatches each task to the workflow matching its kind (code→TDD, docs→docs, infra→deploy, design→UX). Every story can run human-assigned before its agent path lands.

### Epic 42 — Agent Capability & Tool Layer (Planned / docs)

**Layer 4** — 9 stories, backlog. Makes the coding-only tool framework first-class: a `ToolDescriptor` (category / permission class / autonomy floor / required secret / suspends) on `IToolExecutor` with deny-by-default governance, a dynamic + MCP registry seam, a two-scoping `tool_bindings` store, per-tool autonomy gating in `ResolveToolsActivity` that routes destructive/above-floor calls through the Epic 39 `AcceptanceRequest` channel, secret binding via Epic 29's `ISecretStore`, and durable `TOOL.*` DCB audit. Tool families: cloud/VPS ops, feature-flag & deploy control, authenticated HTTP, and MCP-exposed tools — the missing foundation under Epic 41's non-code task kinds.

---

## Review findings cross-reference

The 2026-04-20 senior code review (`docs/review/session-2026-04-20.md`) surfaced 18 findings. Merge-blockers closed this sprint; follow-ups mapped into new epics.

| Finding | Severity | Status | Closes via |
|---------|----------|--------|------------|
| #1 Phase-3 fail-closed plane is dead code | P0 | merge-blocker closed via audit downgrade | Story 19-6 (app-role wiring) + Story 30-8 (per-tenant routing) |
| #2 Users RLS policy leaks NULL-tenant rows | P1 | **closed** (`aab36e3`) | dropped NULL branch from app-role policies |
| #4 `Cranl:EncryptionKey` HKDF fallback | P2 | follow-up | Story 29-2 + 29-10 |
| #5 Webhook signal registry not tenant-scoped | P1 | **closed** (`9160db1`) | `install:{id}:` prefix on all alias forms |
| #6 Artifact download unbounded → OOM | P1 | **closed** (`ced59bc`) | 4 MB cap + string clamps |
| #7 LocalExecutor temp path predictable | P2 | follow-up | `TAMMA_AGENT_TMP` env override |
| #8 Unbounded strings in `AgentResultArtifact` | P2 | **closed** (in `ced59bc`) | per-field clamps |
| #9 `DefaultProcessRunner` stream-drain race | P2 | follow-up | trivial code change |
| #11 Installation token cache lacks 401 invalidation | P2 | follow-up | retry-once wrapper |
| #12 Rate-limit handling does not back off | P2 | follow-up | honor `RetryAfterSeconds` |
| #13 Sealed-box plaintext not wiped from memory | P2 | follow-up | `CryptographicOperations.ZeroMemory` |
| #14 8-hex Cranl resource names (~65k birthday) | P2 | follow-up | **Story 30-3** |
| #15 `tamma_app` `PASSWORD 'changeme'` literal | P0 | follow-up | **Story 29-9** |
| #16 `TAMMA_SHARED_SECRET` plaintext env | P1 | follow-up | **Story 29-9 + 29-8** |
| #18 `InvalidOperationException` on no-tenant path → 500 | smell | follow-up | typed `TenantNotFound` + endpoint mapper |

Beyond the numbered findings, Epic 31 closes the "GitHub-hard-coding" design-intent items (driver refactor 31-3, webhook abstraction 31-7), and Stories 18-7 / 18-8 close the tenant-user-management gap.

---

## Phase view

```
Completed / Near Complete:
  Epic 1   (Foundation)              [NEAR COMPLETE - 10/15]
  Epic 1.5 (Infrastructure)          [NEAR COMPLETE - 9/10]
  Epic 2   (Autonomous Loop)         [NEAR COMPLETE - 14/20 — 2-14 decomposition landed 2026-07]
  Epic 3   (Quality Gates)           [NEAR COMPLETE - 12/13 — 3-4/3-5/3-6/3-7 landed 2026-07]
  Epic 4   (Event Sourcing)          [NEAR COMPLETE - 7/8 — 4-5 capture + 4-8 replay landed 2026-07]
  Epic 6   (Context & Knowledge)     [NEAR COMPLETE - 9/10 — KB/RAG sidecar live w/ Ollama embeddings]
  Epic 7   (Mentorship Workflow)     [NEAR COMPLETE - 8/9]
  Epic 8   (Distribution)            [COMPLETED]
  Epic 9   (Multi-Agent Management)  [COMPLETED]
  Epic 11  (Security Hardening)      [COMPLETED]
  Epic 12  (Agentic Tool Loop)       [COMPLETED]
  Epic 13  (Workflow Decomposition)  [COMPLETED]
  Epic 14  (Custom ELSA Studio)      [COMPLETED]
  Epic 15  (Log Aggregation)         [COMPLETED]
  Epic 16  (Unified Auth & Admin)    [COMPLETED]
  Epic 19  (Agent Dispatch)          [COMPLETED - 19-6 follow-up drafted]
  Epic 21  (Marketing/User Dashboard) [PARTIAL - 21-4 Repos & Runs pages live]
  Epic 25  (Wiki Site)               [COMPLETED]

In progress:
  Epic 18  (End-User Auth)           [4 done — 18-4 onboarding slices landed 2026-07, still in progress]
  Epic 23  (Monitoring Dashboard)    [IN PROGRESS - 8/12 pages landed 2026-07]
  Epic 39  (Document Lifecycle)      [IMPLEMENTED - spine complete, 39-12..39-15 merged; 39-1/16/17/21 remain]

Active planning (Wave-2):
  Epic 28  (Database-per-Tenant)     [NEW - briefs + 12 plans]
  Epic 29  (Secret Management)       [NEW - briefs + 10 plans]
  Epic 30  (Multi-Tenant Provisioning) [NEW - briefs + 10 plans]
  Epic 31  (Multi Git Platform)      [NEW - briefs only]

Workflow-platform wave (planned / docs):
  Epic 40  (Resumable Coding)        [BACKLOG - briefs only]
  Epic 41  (Full-Team Workflows)     [BACKLOG - briefs only]
  Epic 42  (Tool Layer)              [BACKLOG - briefs only]

Drafted / Future:
  Epic 5, 10, 17, 20, 22, 24, 26
  Epic 33  (Per-Tenant IdP)          [DEFERRED STUB - trigger-gated]
```

---

## Key Success Metrics

- **Autonomous Completion Rate:** 70%+ (target)
- **Time to Resolution:** <24 hours for most issues
- **Quality Gate Pass Rate:** 95%+
- **System Uptime:** 99.5%+ (orchestrator mode)
- **Test Coverage:** 80%+ line coverage, 75%+ branch coverage
- **Current test count:** 1817 tests passing on `feat/auth-foundation`

---

## Related documentation

- [Architecture](/architecture/) — three-mode architecture, `IAgentExecutor`
- [Agent Dispatch](Agent-Dispatch) — Epic 19 completion
- [Port Audit](Port-Audit) — TS → C# port gaps + 2026-04-20 code review
- [Security](Security) — hardening summary
- [Secret Management](Secret-Management) — Epic 29
- [Multi-Tenant Provisioning](Multi-Tenant-Provisioning) — Epic 30
- [Multi Git Platform](Multi-Git-Platform) — Epic 31
- [Identity Providers](Identity-Providers) — Epic 33 (deferred)
- [Document Lifecycle](Document-Lifecycle) — Epic 39
- [Resumable Workflows](Resumable-Workflows) — Epics 39/40/41 resumable-by-design standard
- [Wave-2 impl plan inventory](https://github.com/meywd/tamma/blob/main/docs/stories/plans/wave-2-impl-plan-inventory.md)
- [Layer 4/5 placement for Epics 29/30](https://github.com/meywd/tamma/blob/main/docs/stories/plans/epic-29-30-placement.md)
- [Layer 4/5 placement for Epics 31/33](https://github.com/meywd/tamma/blob/main/docs/stories/plans/epic-31-33-placement.md)

_For detailed technical specifications, see the tech-spec documents in the [docs/](/epics/) directory._
