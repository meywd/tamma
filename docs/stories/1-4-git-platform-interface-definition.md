# Story 1.4: Git Platform Interface Definition

Status: ready-for-dev

## Story

As a **system architect**,
I want to define abstract interface contracts for Git platform operations,
so that the system can support GitHub, GitLab, Gitea, and Forgejo without platform-specific logic in core workflows.

## Acceptance Criteria

1. Interface defines core operations: `createPR()`, `commentOnPR()`, `mergePR()`, `getIssue()`, `createBranch()`, `triggerCI()`
2. Interface includes platform capabilities discovery (review workflows, CI/CD integration, webhook support)
3. Interface normalizes platform-specific models (PR structure, issue format, CI status)
4. Documentation includes integration guide for adding new platforms
5. Interface supports pagination and rate limit handling

## Tasks / Subtasks

- [ ] Task 1: Design core Git platform interface structure (AC: 1)
  - [ ] Subtask 1.1: Create IGitPlatform interface with core method signatures
  - [ ] Subtask 1.2: Define repository, branch, PR, and issue data structures
  - [ ] Subtask 1.3: Add TypeScript documentation for all interface methods
- [ ] Task 2: Implement platform capabilities discovery (AC: 2)
  - [ ] Subtask 2.1: Define PlatformCapabilities interface for discovery
  - [ ] Subtask 2.2: Add getCapabilities() method to IGitPlatform
  - [ ] Subtask 2.3: Create capability enums for review workflows, CI/CD, webhooks
- [ ] Task 3: Normalize platform-specific data models (AC: 3)
  - [ ] Subtask 3.1: Create unified PR, Issue, Branch, and CI status interfaces
  - [ ] Subtask 3.2: Define mapping patterns for platform-specific differences
  - [ ] Subtask 3.3: Add transformation utilities for data normalization
- [ ] Task 4: Add pagination and rate limit support (AC: 5)
  - [ ] Subtask 4.1: Define pagination interfaces and cursor-based navigation
  - [ ] Subtask 4.2: Add rate limit detection and handling methods
  - [ ] Subtask 4.3: Implement retry logic with exponential backoff for rate limits
- [ ] Task 5: Create integration documentation (AC: 4)
  - [ ] Subtask 5.1: Write platform integration guide for new implementations
  - [ ] Subtask 5.2: Create example platform implementation template
  - [ ] Subtask 5.3: Document testing procedures for new platforms
- [ ] Task 6: Add comprehensive interface testing (AC: 1, 2, 3, 5)
  - [ ] Subtask 6.1: Write interface contract tests for all methods
  - [ ] Subtask 6.2: Create mock implementations for testing consumers
  - [ ] Subtask 6.3: Add tests for pagination and rate limit handling

## Dev Notes

### Requirements Context Summary

**Epic 1 Foundation:** This story defines the Git platform abstraction layer that enables Tamma to work with multiple Git hosting services (GitHub, GitLab, Gitea, Forgejo) without platform-specific logic in core workflows. This is a foundational story parallel to Story 1.1.

**Interface Design:** The IGitPlatform interface must provide unified operations for repository management, branch operations, pull request workflows, issue tracking, and CI/CD integration. Platform-specific differences must be abstracted away through normalized data models.

**Capabilities Discovery:** Each platform must expose its capabilities (supported features, rate limits, webhook support) through a standardized interface, enabling the system to adapt behavior based on platform capabilities.

**Data Normalization:** Platform-specific data structures (GitHub PRs vs GitLab Merge Requests) must be normalized into unified interfaces to ensure consistent behavior across platforms.

**Pagination and Rate Limits:** The interface must handle large result sets through pagination and respect platform rate limits with proper retry logic.

### Project Structure Notes

**Package Location:** `packages/platforms/src/types.ts` following monorepo structure defined in architecture section 4.3.

**Dependencies:** Uses TypeScript 5.7+ strict mode for interface definitions. No external dependencies required for interface definitions.

**TypeScript Configuration:** Strict mode TypeScript 5.7+ with proper interface definitions and generic types for extensibility.

**Naming Conventions:** Follow established patterns: IGitPlatform interface, PlatformCapabilities type, unified data models with descriptive names.

### References

- [Source: docs/tech-spec-epic-1.md#Git-Platform-Abstraction-packagesplatforms](F:\Code\Repos\Tamma\docs\tech-spec-epic-1.md#Git-Platform-Abstraction-packagesplatforms)
- [Source: docs/epics.md#Story-14-Git-Platform-Interface-Definition](F:\Code\Repos\Tamma\docs\epics.md#Story-14-Git-Platform-Interface-Definition)
- [Source: docs/PRD.md#Git-Platform-Integration](F:\Code\Repos\Tamma\docs\PRD.md#Git-Platform-Integration)
- [Source: docs/architecture.md#Technology-Stack](F:\Code\Repos\Tamma\docs\architecture.md#Technology-Stack)

## Change Log

| Date | Version | Changes | Author |
|------|---------|----------|--------|
| 2025-10-28 | 1.0.0 | Initial story creation | BMad (Scrum Master) |

## Dev Agent Record

### Context Reference

- docs/stories/1-4-git-platform-interface-definition.context.xml

### Agent Model Used

Claude-3.5-Sonnet

### Debug Log References

### Completion Notes List

### File List