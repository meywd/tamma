---
title: "Tamma Stories"
---

This directory contains all user stories organized by epic for the Tamma platform implementation.

## Organization Structure

Stories are organized into epic-specific subdirectories:

- **`epic-1/`** - Foundation & Core Infrastructure (15 stories + 10 infrastructure stories)
- **`epic-2/`** - Autonomous Development Loop - Core (16 stories)
- **`epic-3/`** - Quality Gates & Intelligence Layer (12 stories)
- **`epic-4/`** - Event Sourcing & Intelligence (7 stories)
- **`epic-5/`** - Observability Dashboard & Documentation (14 stories)
- **`epic-6/`** - Context & Knowledge Management (10 stories)
- **`epic-7/`** - Autonomous Mentorship Workflow (10 stories)
- **`epic-8/`** - Distribution & Installation (8 stories)
- **`epic-20/`** - Billing & Payments (5 stories) — **superseded by Epics 34 & 35** (re-targeted to the C# `apps/tamma-elsa` stack)
- **`epic-32/`** - Agents — First-Class Agent Entities, Managed Execution, Benchmarking & Learning (14 stories)
- **`epic-34/`** - Pricing, Plans & Entitlements (10 stories)
- **`epic-35/`** - Billing & Payments (C#) — Stripe, BYOK-Aware Metering, Subscriptions, Invoicing & Dunning (12 stories)
- **`epic-36/`** - Analytics & Reporting Platform (11 stories)
- **`epic-37/`** - Audit, Compliance & Data Governance (12 stories)

> Note: this list is illustrative, not exhaustive — see each `epic-N/README.md` for the canonical per-epic detail and `docs/sprint-status.yaml` for live status across all epics.

## File Types

Each epic directory contains:

- **`.md` files**: Story specifications with:
  - User stories and acceptance criteria
  - Technical context and implementation details
  - Dependencies and testing strategies
  - Success metrics and rollout plans

- **`.context.xml` files**: Generated context XML files containing:
  - Complete technical implementation context
  - Architecture patterns and code examples
  - Integration specifications
  - Reference implementations

## Story Status

All stories across all epics are **ready for implementation** with:

- ✅ Comprehensive technical specifications
- ✅ Detailed acceptance criteria
- ✅ Implementation context files
- ✅ Testing strategies and success metrics

## Implementation Priority

### Phase 1 (November 2025)

- **Epic 1**: Foundation & Core Infrastructure
- **Epic 2**: Autonomous Development Workflow

### Phase 2 (December 2025)

- **Epic 3**: Quality Gates & Testing
- **Epic 4**: Event Sourcing & Intelligence

### Phase 3 (January 2026)

- **Epic 5**: Observability Dashboard & Documentation

### Phase 4 (February 2026)

- **Epic 6**: Context & Knowledge Management (Vector DB, RAG, MCP)

### Phase 5 (March 2026)

- **Epic 7**: Autonomous Mentorship Workflow

### Phase 6 (April 2026)

- **Epic 8**: Distribution & Installation (npm, binary, Docker)

### Phase 7 (May 2026)

- **Epic 20**: Billing & Payments (Stripe subscriptions, usage metering, limits enforcement)

## Navigation

- See individual epic README files for detailed story breakdowns
- Refer to `../epics.md` for epic-level planning and dependencies
- Check `../PRD.md` for product requirements and acceptance criteria
- Review `../architecture.md` for technical architecture details

---

**Last Updated**: 2026-03-28
**Total Stories**: 97 stories across 9 epics
**Implementation Start**: November 2025
