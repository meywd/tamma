# Story 1.6: GitLab Platform Implementation

Status: drafted

## Story

As a developer,
I want GitLab implemented as second Git platform,
so that teams using GitLab can adopt system without platform migration.

## Acceptance Criteria

1. GitLab provider implements all interface operations from Story 1.4
2. Provider handles authentication via Personal Access Token or OAuth
3. Provider integrates with GitLab CI API for pipeline triggering
4. Provider integrates with GitLab Merge Request API for review workflows
5. Unit tests cover happy path, error cases, and GitLab-specific differences from GitHub
6. Integration test demonstrates end-to-end Merge Request creation and merge

## Tasks / Subtasks

- [ ] Task 1: Implement GitLabPlatform class with IGitPlatform interface (AC: 1)
  - [ ] Subtask 1.1: Set up @gitbeaker/node SDK dependency
  - [ ] Subtask 1.2: Implement core platform operations (getRepository, listIssues, getIssue)
  - [ ] Subtask 1.3: Implement branch operations (createBranch, getBranch)
  - [ ] Subtask 1.4: Implement MR operations (createPullRequest, getPullRequest, updatePullRequest, mergePullRequest)
  - [ ] Subtask 1.5: Implement status check operations (getPRStatus, getChecks)

- [ ] Task 2: Implement authentication handling (AC: 2)
  - [ ] Subtask 2.1: Support Personal Access Token authentication
  - [ ] Subtask 2.2: Support OAuth2 authentication flow
  - [ ] Subtask 2.3: Implement token validation and refresh logic

- [ ] Task 3: Integrate GitLab CI/CD API (AC: 3)
  - [ ] Subtask 3.1: Implement pipeline status retrieval
  - [ ] Subtask 3.2: Implement pipeline triggering functionality
  - [ ] Subtask 3.3: Map GitLab pipeline status to normalized CI status model

- [ ] Task 4: Integrate GitLab Merge Request API for review workflows (AC: 4)
  - [ ] Subtask 4.1: Implement MR comments and discussions
  - [ ] Subtask 4.2: Implement reviewer assignment and approval workflows
  - [ ] Subtask 4.3: Handle GitLab-specific review concepts (approvals vs merge requests)

- [ ] Task 5: Implement comprehensive unit testing (AC: 5)
  - [ ] Subtask 5.1: Test happy path operations with mock GitLab API
  - [ ] Subtask 5.2: Test error cases (authentication failures, API errors, rate limits)
  - [ ] Subtask 5.3: Test GitLab-specific differences from GitHub (namespace handling, MR vs PR)

- [ ] Task 6: Implement integration testing (AC: 6)
  - [ ] Subtask 6.1: Set up test GitLab project and credentials
  - [ ] Subtask 6.2: Test end-to-end MR creation workflow
  - [ ] Subtask 6.3: Test MR merge workflow with status checks

## Dev Notes

### Architecture Patterns and Constraints

- Interface-based design: GitLabPlatform must implement IGitPlatform interface [Source: docs/tech-spec-epic-1.md#Services and Modules]
- API parity mapping: GitLab merge requests → PR abstraction [Source: docs/tech-spec-epic-1.md#Git Platform Abstraction]
- Plugin architecture: GitLab platform as plugin in platforms package [Source: docs/architecture.md#Project Structure]
- Group/subgroup namespace handling for GitLab's hierarchical project structure [Source: docs/tech-spec-epic-1.md#Git Platform Abstraction]
- Self-hosted GitLab support via custom base URL configuration [Source: docs/tech-spec-epic-1.md#Git Platform Abstraction]

### Source Tree Components to Touch

- `packages/platforms/src/gitlab.ts` - Main GitLab platform implementation
- `packages/platforms/src/index.ts` - Export GitLab platform
- `packages/platforms/src/types.ts` - IGitPlatform interface (already exists from Story 1.4)
- `packages/platforms/package.json` - Add @gitbeaker/node dependency
- `packages/platforms/src/gitlab.test.ts` - Unit tests
- `packages/platforms/src/gitlab.integration.test.ts` - Integration tests

### Testing Standards Summary

- Unit tests with Vitest framework [Source: docs/architecture.md#Technology Stack]
- Mock external APIs using MSW (Mock Service Worker) [Source: docs/tech-spec-epic-1.md#Unit Testing Strategy]
- Integration tests with real GitLab test project [Source: docs/tech-spec-epic-1.md#Integration Testing Strategy]
- Coverage targets: 80% line, 75% branch, 85% function [Source: docs/tech-spec-epic-1.md#Unit Testing Strategy]

### Project Structure Notes

- Follow package structure defined in architecture [Source: docs/architecture.md#Project Structure]
- Use TypeScript strict mode with proper type definitions [Source: docs/architecture.md#Implementation Patterns]
- Implement proper error handling with TammaError base class [Source: docs/architecture.md#Implementation Patterns]
- Use structured logging with Pino [Source: docs/architecture.md#Technology Stack]

### References

- [Source: docs/epics.md#Story 1.6: GitLab Platform Implementation]
- [Source: docs/tech-spec-epic-1.md#Git Platform Abstraction]
- [Source: docs/tech-spec-epic-1.md#Services and Modules]
- [Source: docs/architecture.md#Project Structure]
- [Source: docs/architecture.md#Technology Stack]
- [Source: docs/tech-spec-epic-1.md#Dependencies and Integrations]

## Dev Agent Record

### Context Reference

<!-- Path(s) to story context XML will be added here by context workflow -->

### Agent Model Used

Claude-3.5-Sonnet

### Debug Log References

### Completion Notes List

### File List
