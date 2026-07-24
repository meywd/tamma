# Epics Index

All 37 epics for the Tamma project, organized by implementation status.

_Last audited: 2026-07-24_

## Completed Epics (10)

| Epic | Name | Stories | Page |
|------|------|---------|------|
| 8 | Distribution & Installation | 8 | [Epic-8-Distribution](Epics/Epic-8-Distribution) |
| 9 | Config-Driven Multi-Agent Management | 12 | [Epic-9-Agent-Management](Epics/Epic-9-Agent-Management) |
| 11 | Security Hardening (Elsa) | 5 | [Epic-11-Security](Epics/Epic-11-Security) |
| 13 | Workflow Decomposition | 3 | [Epic-13-Workflow-Decomposition](Epics/Epic-13-Workflow-Decomposition) |
| 14 | Custom Elsa Studio | 3 | [Epic-14-ELSA-Studio](Epics/Epic-14-ELSA-Studio) |
| 15 | Observability & Log Aggregation | 1 (done) + 2 (planned) | [Epic-15-Log-Aggregation](Epics/Epic-15-Log-Aggregation) |
| 16 | Unified Auth, User Management & Admin | 6 | [Epic-16-Auth-Admin](Epics/Epic-16-Auth-Admin) |
| **19** | **GitHub App Agent Dispatch** | **5 (+ 19-6 follow-up)** | [Epic-19-Agent-Dispatch](Epics/Epic-19-Agent-Dispatch) |
| 21 | Marketing Site (landing page only) | 1 of 5 | [Epic-21-Marketing-Dashboard](Epics/Epic-21-Marketing-Dashboard) |
| 25 | Documentation & Wiki Site | 1 | [Epic-25-Wiki-Site](Epics/Epic-25-Wiki-Site) |

**Combined page**: [Epic-11-14-ELSA](Epics/Epic-11-14-ELSA) covers Epics 11-14 together (15 stories total)

## Near Complete (7)

| Epic | Name | Done | Stories | Page |
|------|------|------|---------|------|
| 1 | Foundation & Core Infrastructure | 10/15 | 15 | [Epic-1-Foundation](Epics/Epic-1-Foundation) |
| 1.5 | Infrastructure & Deployment | core 15/15; secret-mgmt 0/30 | 45 | [Epic-1.5-Infrastructure](Epics/Epic-1.5-Infrastructure) |
| 2 | Autonomous Development Loop | 13/20 | 20 | [Epic-2-Autonomous-Loop](Epics/Epic-2-Autonomous-Loop) |
| 3 | Quality Gates & Intelligence | 8/12 | 12 | [Epic-3-Quality-Gates](Epics/Epic-3-Quality-Gates) |
| 4 | Event Sourcing & Audit Trail | 6/8 | 8 | [Epic-4-Event-Sourcing](Epics/Epic-4-Event-Sourcing) |
| 6 | Context & Knowledge Management | 9/10 | 10 | [Epic-6-Context-Knowledge](Epics/Epic-6-Context-Knowledge) |
| 7 | Autonomous Mentorship Workflow | 18/19 | 19 | [Epic-7-Mentorship](Epics/Epic-7-Mentorship) |
| 12 | Agentic Tool Loop | 4/7 (+ 5 sub-stories) | 7 | [Epic-12-Tool-Loop](Epics/Epic-12-Tool-Loop) |

## Partially Implemented (5)

| Epic | Name | Done | In Progress | Stories | Page |
|------|------|------|-------------|---------|------|
| 5 | Observability Dashboard & Docs | 4 | 3 | 14 | [Epic-5-Observability](Epics/Epic-5-Observability) |
| 17 | Multi-Tenancy Foundation | Phase-2 RLS shipped | Phase-3 wiring (19-6) | 5 | [Epic-17-Multi-Tenancy](Epics/Epic-17-Multi-Tenancy) |
| 18 | End-User Auth & Registration | 1 (18-4) | 2 | 8 | [Epic-18-User-Auth](Epics/Epic-18-User-Auth) |
| 21 | Marketing Site & User Dashboard | 1 | 1 | 5 | [Epic-21-Marketing-Dashboard](Epics/Epic-21-Marketing-Dashboard) |
| 22 | CLI Mode Preservation (mostly absorbed by 19) | 22-1 + 22-2 superseded | — | 4 | [Epic-22-CLI-Standalone](Epics/Epic-22-CLI-Standalone) |

## Planned / Drafted Epics (5)

| Epic | Name | Stories | Status | Page |
|------|------|---------|--------|------|
| 10 | Engine Core — Workflow-Driven Architecture | 9 | Drafted (10-9 in progress) | [Epic-10-Engine-Core](Epics/Epic-10-Engine-Core) |
| 20 | Billing & Payments | 5 | Drafted | [Epic-20-Billing](Epics/Epic-20-Billing) |
| 23 | System Monitoring & Observability Dashboard | 12 | Drafted (26 task plans) | [Epic-23-System-Monitoring](Epics/Epic-23-System-Monitoring) |
| 24 | Realtime Voice Conversation | 7 | Drafted (24 task plans) | [Epic-24-Voice-Conversation](Epics/Epic-24-Voice-Conversation) |
| 26 | Project Management & Triage | 4 | Drafted | [Epic-26-Project-Management](Epics/Epic-26-Project-Management) |

## Newly Scoped — Wave-2 (5 + 1 deferred)

| Epic | Name | Stories | Effort | Layer | Page |
|------|------|---------|--------|-------|------|
| 27 | Prompt Store — Multi-Tenant Prompt Management | 7 + 12 taxonomy stories (27-8..27-19) | 86h+ | 4 | [Epic-27-Prompt-Store](Epics/Epic-27-Prompt-Store) |
| 28 | Database-per-Tenant Isolation | 12 + 1 deferred | 265h | 4 | [Epic-28-DB-Per-Tenant](Epics/Epic-28-DB-Per-Tenant) |
| 29 | Platform Secret Management | 10 | 166h | 4 | [Epic-29-Secret-Management](Epics/Epic-29-Secret-Management) |
| 30 | Pluggable Tenant Infrastructure Provisioning | 10 | 216h | 5 | [Epic-30-Pluggable-Provisioning](Epics/Epic-30-Pluggable-Provisioning) |
| 31 | Multi Git Platform Support | 10 core + 2 deferred | 220h core / 284h with optionals | 4 + 5 | [Epic-31-Multi-Git-Platform](Epics/Epic-31-Multi-Git-Platform) |
| 33 | Per-Tenant Identity Providers | (deferred — trigger-gated) | 100–400h depending on tier | 5+ | [Epic-33-Per-Tenant-IdP](Epics/Epic-33-Per-Tenant-IdP) |

> **Note**: Epic 32 is intentionally skipped (no folder under `docs/stories/`).

## Workflow-Platform Wave (39–42)

The document-lifecycle wave makes the platform's domain language explicit and extends the single quality lifecycle across the whole team. **Epic 39 is implemented** (spine + all producer migrations); **40, 41, 42 are scoped/planned (backlog, not built).**

| Epic | Name | Stories | Status | Page |
|------|------|---------|--------|------|
| **39** | Typed Work Documents & the Universal Lifecycle | 21 | **Implemented** — spine complete, producer migrations 39-12…39-15 merged; 39-1/16/17/21 remain | [Epic-39-Document-Lifecycle](Epics/Epic-39-Document-Lifecycle) |
| 40 | Resumable Coding Execution | 7 | Planned / docs — backlog | [Epic-40-Resumable-Coding](Epics/Epic-40-Resumable-Coding) |
| 41 | Full-Team Workflow Coverage | 28 + 41-29 router | Planned / docs — backlog | [Epic-41-Full-Team-Workflows](Epics/Epic-41-Full-Team-Workflows) |
| 42 | Agent Capability & Tool Layer | 9 | Planned / docs — backlog | [Epic-42-Tool-Layer](Epics/Epic-42-Tool-Layer) |

> **Epic 27 taxonomy stories (27-8..27-19):** 27-8 Convention Store Schema, 27-9 Convention Store Service, 27-10 Convention Store API, 27-11 Convention Store Admin UI, 27-12 Convention Store Tenant UI, 27-13 Convention Store Integration, 27-14 Convention Store Events, 27-15 AgentRole/AgentAction Taxonomy + RolePhaseMap Rebuild, 27-16 Taxonomy Codegen (Prompt + Convention Seed), 27-17 Taxonomy Drift Build Test, 27-18 Prompt Store Taxonomy Reshape, 27-19 Workflow Dispatch-Site Migration. See [Role/Action Taxonomy](Role-Action-Taxonomy.md) for the (role,action) exact lookup design.

## Audit

- [Wiki/Epics audit — 2026-04-21](Epics/AUDIT-2026-04-21) — drift matrix and refresh decisions

## Related Pages

- [Roadmap](Roadmap) — full timeline and status
- [Stories](Stories) — all user stories across all epics
- [Architecture](Architecture) — system architecture overview
- [Document Lifecycle](Document-Lifecycle) — Epic 39 root topic page
- [Resumable Workflows](Resumable-Workflows) — the resumable-by-design standard (Epics 39/40/41)
- [Secret Management](Secret-Management) — Epic 29 root topic page
- [Multi-Tenant Provisioning](Multi-Tenant-Provisioning) — Epic 30 root topic page
- [Multi Git Platform](Multi-Git-Platform) — Epic 31 root topic page
- [Identity Providers](Identity-Providers) — Epic 33 root topic page
- [Agent Dispatch](Agent-Dispatch) — Epic 19 root topic page
