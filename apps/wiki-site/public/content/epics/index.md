---
title: "Epics Index"
sidebar:
  order: 0
---

All 37 epics for the Tamma project, organized by implementation status.

_Last audited: 2026-07-24_

## Completed Epics (10)

| Epic | Name | Stories | Page |
|------|------|---------|------|
| 8 | Distribution & Installation | 8 | [Epic-8-Distribution](/epics/8-distribution/) |
| 9 | Config-Driven Multi-Agent Management | 12 | [Epic-9-Agent-Management](/epics/9-agent-management/) |
| 11 | Security Hardening (Elsa) | 5 | [Epic-11-Security](/epics/11-security/) |
| 13 | Workflow Decomposition | 3 | [Epic-13-Workflow-Decomposition](/epics/13-workflow-decomposition/) |
| 14 | Custom Elsa Studio | 3 | [Epic-14-ELSA-Studio](/epics/14-elsa-studio/) |
| 15 | Observability & Log Aggregation | 1 (done) + 2 (planned) | [Epic-15-Log-Aggregation](/epics/15-log-aggregation/) |
| 16 | Unified Auth, User Management & Admin | 6 | [Epic-16-Auth-Admin](/epics/16-auth-admin/) |
| **19** | **GitHub App Agent Dispatch** | **5 (+ 19-6 follow-up)** | [Epic-19-Agent-Dispatch](/epics/19-agent-dispatch/) |
| 21 | Marketing Site (landing page only) | 1 of 5 | [Epic-21-Marketing-Dashboard](/epics/21-marketing-dashboard/) |
| 25 | Documentation & Wiki Site | 1 | [Epic-25-Wiki-Site](/epics/25-wiki-site/) |

**Combined page**: [Epic-11-14-ELSA](/epics/11-14-elsa/) covers Epics 11-14 together (15 stories total)

## Near Complete (7)

| Epic | Name | Done | Stories | Page |
|------|------|------|---------|------|
| 1 | Foundation & Core Infrastructure | 10/15 | 15 | [Epic-1-Foundation](/epics/1-foundation/) |
| 1.5 | Infrastructure & Deployment | core 15/15; secret-mgmt 0/30 | 45 | [Epic-1.5-Infrastructure](/epics/1.5-infrastructure/) |
| 2 | Autonomous Development Loop | 13/20 | 20 | [Epic-2-Autonomous-Loop](/epics/2-autonomous-loop/) |
| 3 | Quality Gates & Intelligence | 8/12 | 12 | [Epic-3-Quality-Gates](/epics/3-quality-gates/) |
| 4 | Event Sourcing & Audit Trail | 6/8 | 8 | [Epic-4-Event-Sourcing](/epics/4-event-sourcing/) |
| 6 | Context & Knowledge Management | 9/10 | 10 | [Epic-6-Context-Knowledge](/epics/6-context-knowledge/) |
| 7 | Autonomous Mentorship Workflow | 18/19 | 19 | [Epic-7-Mentorship](/epics/7-mentorship/) |
| 12 | Agentic Tool Loop | 4/7 (+ 5 sub-stories) | 7 | [Epic-12-Tool-Loop](/epics/12-tool-loop/) |

## Partially Implemented (5)

| Epic | Name | Done | In Progress | Stories | Page |
|------|------|------|-------------|---------|------|
| 5 | Observability Dashboard & Docs | 4 | 3 | 14 | [Epic-5-Observability](/epics/5-observability/) |
| 17 | Multi-Tenancy Foundation | Phase-2 RLS shipped | Phase-3 wiring (19-6) | 5 | [Epic-17-Multi-Tenancy](/epics/17-multi-tenancy/) |
| 18 | End-User Auth & Registration | 1 (18-4) | 2 | 8 | [Epic-18-User-Auth](/epics/18-user-auth/) |
| 21 | Marketing Site & User Dashboard | 1 | 1 | 5 | [Epic-21-Marketing-Dashboard](/epics/21-marketing-dashboard/) |
| 22 | CLI Mode Preservation (mostly absorbed by 19) | 22-1 + 22-2 superseded | — | 4 | [Epic-22-CLI-Standalone](/epics/22-cli-standalone/) |

## Planned / Drafted Epics (5)

| Epic | Name | Stories | Status | Page |
|------|------|---------|--------|------|
| 10 | Engine Core — Workflow-Driven Architecture | 9 | Drafted (10-9 in progress) | [Epic-10-Engine-Core](/epics/10-engine-core/) |
| 20 | Billing & Payments | 5 | Drafted | [Epic-20-Billing](/epics/20-billing/) |
| 23 | System Monitoring & Observability Dashboard | 12 | Drafted (26 task plans) | [Epic-23-System-Monitoring](/epics/23-system-monitoring/) |
| 24 | Realtime Voice Conversation | 7 | Drafted (24 task plans) | [Epic-24-Voice-Conversation](/epics/24-voice-conversation/) |
| 26 | Project Management & Triage | 4 | Drafted | [Epic-26-Project-Management](/epics/26-project-management/) |

## Newly Scoped — Wave-2 (5 + 1 deferred)

| Epic | Name | Stories | Effort | Layer | Page |
|------|------|---------|--------|-------|------|
| 27 | Prompt Store — Multi-Tenant Prompt Management | 7 + 12 taxonomy stories (27-8..27-19) | 86h+ | 4 | [Epic-27-Prompt-Store](/epics/27-prompt-store/) |
| 28 | Database-per-Tenant Isolation | 12 + 1 deferred | 265h | 4 | [Epic-28-DB-Per-Tenant](/epics/28-db-per-tenant/) |
| 29 | Platform Secret Management | 10 | 166h | 4 | [Epic-29-Secret-Management](/epics/29-secret-management/) |
| 30 | Pluggable Tenant Infrastructure Provisioning | 10 | 216h | 5 | [Epic-30-Pluggable-Provisioning](/epics/30-pluggable-provisioning/) |
| 31 | Multi Git Platform Support | 10 core + 2 deferred | 220h core / 284h with optionals | 4 + 5 | [Epic-31-Multi-Git-Platform](/epics/31-multi-git-platform/) |
| 33 | Per-Tenant Identity Providers | (deferred — trigger-gated) | 100–400h depending on tier | 5+ | [Epic-33-Per-Tenant-IdP](/epics/33-per-tenant-idp/) |

> **Note**: Epic 32 is intentionally skipped (no folder under `docs/stories/`).

## Workflow-Platform Wave (39–42)

The document-lifecycle wave makes the platform's domain language explicit and extends the single quality lifecycle across the whole team. **Epic 39 is implemented** (spine + all producer migrations); **40, 41, 42 are scoped/planned (backlog, not built).**

| Epic | Name | Stories | Status | Page |
|------|------|---------|--------|------|
| **39** | Typed Work Documents & the Universal Lifecycle | 21 | **Implemented** — spine complete, producer migrations 39-12…39-15 merged; 39-1/16/17/21 remain | [Epic-39-Document-Lifecycle](/epics/39-document-lifecycle/) |
| 40 | Resumable Coding Execution | 7 | Planned / docs — backlog | [Epic-40-Resumable-Coding](/epics/40-resumable-coding/) |
| 41 | Full-Team Workflow Coverage | 28 + 41-29 router | Planned / docs — backlog | [Epic-41-Full-Team-Workflows](/epics/41-full-team-workflows/) |
| 42 | Agent Capability & Tool Layer | 9 | Planned / docs — backlog | [Epic-42-Tool-Layer](/epics/42-tool-layer/) |

> **Epic 27 taxonomy stories (27-8..27-19):** 27-8 Convention Store Schema, 27-9 Convention Store Service, 27-10 Convention Store API, 27-11 Convention Store Admin UI, 27-12 Convention Store Tenant UI, 27-13 Convention Store Integration, 27-14 Convention Store Events, 27-15 AgentRole/AgentAction Taxonomy + RolePhaseMap Rebuild, 27-16 Taxonomy Codegen (Prompt + Convention Seed), 27-17 Taxonomy Drift Build Test, 27-18 Prompt Store Taxonomy Reshape, 27-19 Workflow Dispatch-Site Migration. See [Role/Action Taxonomy](Role-Action-Taxonomy.md) for the (role,action) exact lookup design.

## Audit

- [Wiki/Epics audit — 2026-04-21](Epics/AUDIT-2026-04-21) — drift matrix and refresh decisions

## Related Pages

- [Roadmap](/roadmap/) — full timeline and status
- [Stories](/stories/) — all user stories across all epics
- [Architecture](/architecture/) — system architecture overview
- [Document Lifecycle](Document-Lifecycle) — Epic 39 root topic page
- [Resumable Workflows](Resumable-Workflows) — the resumable-by-design standard (Epics 39/40/41)
- [Secret Management](Secret-Management) — Epic 29 root topic page
- [Multi-Tenant Provisioning](Multi-Tenant-Provisioning) — Epic 30 root topic page
- [Multi Git Platform](Multi-Git-Platform) — Epic 31 root topic page
- [Identity Providers](Identity-Providers) — Epic 33 root topic page
- [Agent Dispatch](Agent-Dispatch) — Epic 19 root topic page
