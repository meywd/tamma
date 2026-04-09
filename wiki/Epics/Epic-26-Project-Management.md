# Epic 26: Project Management & Triage

**Status:** Drafted
**Stories:** 4
**Created:** As part of the ADL Orchestrator redesign

## Goal

Provide automated project management workflows that integrate with the ADL Orchestrator's priority-based work item selection. This epic covers issue triage (LLM-powered classification), scrum management, release management, and configurable priority rules.

## Background

The ADL Orchestrator was redesigned to select work items by priority and dispatch triage when untriaged items are found. Epic 26 provides the triage and project management workflows that the orchestrator depends on.

## Stories

### Story 26-1: Issue Triage Workflow

**Goal:** LLM-powered classification and labeling of untriaged GitHub issues.

**Scope:**
- ELSA workflow (`issue-triage`) dispatched by the ADL Orchestrator when `SelectWorkItemActivity` returns `NeedsTriage`
- Reads issue title, body, labels, and repository context
- Calls LLM to classify: bug, feature, enhancement, question, documentation, security, infrastructure
- Assigns priority label (P0-P3) based on classification and keywords
- Applies labels and updates issue metadata
- Emits `TRIAGE.CLASSIFY.STARTED/COMPLETED` events

**Acceptance Criteria:**
- Untriaged issues are classified within 30 seconds
- Priority assignment matches human judgment 80%+ of the time
- Labels are applied without duplicates
- Full audit trail via events

---

### Story 26-2: Scrum Management Workflow

**Goal:** Automated sprint planning, standup generation, and retrospective summaries.

**Scope:**
- Sprint planning: auto-assign issues to sprints based on priority and capacity
- Daily standup: generate progress summaries from recent events and PR activity
- Retrospective: analyze completed sprint for velocity, blockers, and improvement areas
- Integrates with GitHub Projects (v2) for board management

---

### Story 26-3: Release Management Workflow

**Goal:** Automated changelog generation and deployment orchestration.

**Scope:**
- Generate changelogs from merged PRs and closed issues since last release
- Categorize changes (features, fixes, breaking changes, dependencies)
- Create GitHub releases with semantic versioning
- Trigger deployment workflows after release creation
- Notify stakeholders via configured channels

---

### Story 26-4: Priority Configuration System

**Goal:** Configurable priority rules for the ADL Orchestrator's work item selection.

**Scope:**
- Priority rule engine with configurable weights
- Default rules: security alerts > CI failures > P0 bugs > P1 issues > P2 features > P3 tech debt > stale PRs
- Per-repository priority overrides
- Label-to-priority mapping configuration
- Time-based priority escalation (issues aging without attention get bumped)
- Configuration stored in `AdlConfig` and validated by `InitAdlConfigActivity`

## Dependencies

- **ADL Orchestrator** (Workflow #1) -- dispatches triage workflow, uses priority configuration
- **Single Issue Cycle** (Workflow #2) -- receives pre-selected work items from ADL
- **LLM Call** (Workflow #17) -- used by triage for classification
- **Event Sourcing** (Epic 4) -- all activities emit events

## Architecture Notes

- Triage workflow is dispatched as fire-and-forget by the ADL Orchestrator
- After triage completes, the next ADL loop iteration will find the now-triaged items as ready work items
- Priority configuration is loaded once by `InitAdlConfigActivity` and passed to `SelectWorkItemActivity`
- All activities extend `TammaActivity`/`TammaAsyncActivity` base classes for automatic event emission

---

_See also: [ADL Orchestrator](Workflow-ADL-Orchestrator) | [Workflows](Workflows) | [Roadmap](Roadmap)_
