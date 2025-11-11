# Story 1.7: Git Platform Configuration Management

Status: in_progress

## ⚠️ MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

📖 **[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)**

This mandatory guide includes:

- 7-Phase Development Workflow (Read → Research → Break Down → TDD → Quality Gates → Failure Handling)
- Knowledge Base Usage (.dev/ directory: spikes, bugs, findings, decisions)
- TRACE/DEBUG Logging Requirements for all functions
- Test-Driven Development (TDD) mandatory workflow
- 100% Test Coverage requirement
- Build Success enforcement
- Automatic retry and developer alert procedures

**Failure to follow this process will result in rework.**

## Story

As a **DevOps engineer**,
I want centralized configuration for Git platform settings,
so that I can easily specify which platform to use and configure platform-specific parameters.

## Acceptance Criteria

1. Configuration file supports platform entries (GitHub, GitLab, Gitea, Forgejo)
2. Each platform entry includes: type, base URL, authentication method, webhook secret
3. Configuration validates on load (reachable endpoints, valid credentials)
4. System supports environment variable overrides for sensitive values (tokens)
5. Configuration includes default branch name, PR template path, and label conventions
6. Documentation includes example configurations for all supported platforms

## Tasks / Subtasks

- [ ] Task 1: Design configuration schema and interfaces (AC: 1, 2)
  - [ ] Subtask 1.1: Create PlatformConfig interface with platform-specific fields
  - [ ] Subtask 1.2: Define authentication method types (PAT, OAuth, App)
  - [ ] Subtask 1.3: Create configuration validation schemas
  - [ ] Subtask 1.4: Design platform detection logic from repository URLs

- [ ] Task 2: Implement configuration loading and validation (AC: 3, 4)
  - [ ] Subtask 2.1: Create PlatformConfigManager class
  - [ ] Subtask 2.2: Implement configuration file loading (JSON/YAML)
  - [ ] Subtask 2.3: Add environment variable override support
  - [ ] Subtask 2.4: Implement endpoint reachability validation
  - [ ] Subtask 2.5: Add credential validation with test API calls

- [ ] Task 3: Implement platform registry and selection (AC: 1, 5)
  - [ ] Subtask 3.1: Create PlatformRegistry for platform registration
  - [ ] Subtask 3.2: Implement platform detection from repository URLs
  - [ ] Subtask 3.3: Add default branch configuration per platform
  - [ ] Subtask 3.4: Implement PR template path configuration
  - [ ] Subtask 3.5: Add label conventions configuration

- [ ] Task 4: Create comprehensive documentation and examples (AC: 6)
  - [ ] Subtask 4.1: Write configuration guide with all platform examples
  - [ ] Subtask 4.2: Create troubleshooting section for common issues
  - [ ] Subtask 4.3: Document environment variable reference
  - [ ] Subtask 4.4: Add migration guide from single-platform to multi-platform
  - [ ] Subtask 4.5: Include security best practices for credential storage

- [ ] Task 5: Implement comprehensive testing (AC: 3, 4)
  - [ ] Subtask 5.1: Test configuration loading with valid/invalid files
  - [ ] Subtask 5.2: Test environment variable overrides
  - [ ] Subtask 5.3: Test credential validation with mock APIs
  - [ ] Subtask 5.4: Test platform detection logic
  - [ ] Subtask 5.5: Integration test with real platform credentials

## Dev Notes

### ⚠️ Development Process Reminder

**Before implementing this story, ensure you have:**

1. ✅ Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. ✅ Searched `.dev/` directory for related spikes, bugs, findings, and decisions
3. ✅ Reviewed relevant documentation in `docs/` directory
4. ✅ Checked existing code patterns for similar functionality
5. ✅ Planned TDD approach (Red-Green-Refactor cycle)

### Architecture Context

- Follows configuration patterns from Story 1.3 (Provider Configuration Management)
- Integrates with platform abstractions from Stories 1.4-1.6
- Uses shared configuration infrastructure from @tamma/config package
- Implements validation patterns consistent with provider configuration

### Project Structure Notes

- Implementation location: packages/platforms/src/config/
- Configuration schemas: packages/platforms/src/config/types.ts
- Manager class: packages/platforms/src/config/platform-config-manager.ts
- Tests: packages/platforms/src/config/\*.test.ts

### Security Considerations

- Sensitive values (tokens, secrets) must support environment variable overrides
- Configuration files with credentials should have restricted permissions (600)
- No plaintext credentials in logs or error messages
- Credential validation should use test API calls, not store responses

### References

- **🔴 MANDATORY PROCESS:** [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
- **Knowledge Base:** [.dev/README.md](../../.dev/README.md) - Search spikes, bugs, findings, decisions
- [Source: docs/epics.md#Story-1.7](../epics.md#story-17-git-platform-configuration-management)
- [Source: docs/tech-spec-epic-1.md#Platform-Configuration-Service](../tech-spec-epic-1.md#platform-configuration-service)
- [Source: docs/architecture.md#Project-Structure](../architecture.md#project-structure)
- [Reference: Story 1.3 - Provider Configuration Management](1-3-provider-configuration-management.md)
- [Reference: Story 1.4-1.6 - Platform Implementations](1-4-git-platform-interface-definition.md)

## Dev Agent Record

### Context Reference

- [F:\Code\Repos\Tamma\docs\stories\1-7-git-platform-configuration-management.context.xml](1-7-git-platform-configuration-management.context.xml)

### Agent Model Used

<!-- Model information will be added by development agent -->

### Debug Log References

<!-- Debug references will be added during development -->

### Completion Notes List

<!-- Completion notes will be added during development -->

### File List

<!-- File list will be added during development -->

## Task Breakdowns

### Task 1: Configuration Schema and Interfaces

**File**: `1-7-git-platform-configuration-management-task-1.md`
**Status**: ✅ Complete
**Description**: Define TypeScript interfaces and JSON schemas for multi-platform Git configuration, including platform types, authentication methods, validation rules, and configuration loading interfaces.

### Task 2: Configuration Loading and Validation

**File**: `1-7-git-platform-configuration-management-task-2.md`
**Status**: ✅ Complete
**Description**: Implement configuration loading from files and environment variables, validation against schemas, error handling, and configuration merging capabilities.

### Task 3: Platform Registry and Selection

**File**: `1-7-git-platform-configuration-management-task-3.md`
**Status**: ✅ Complete
**Description**: Create platform registry for managing multiple Git platforms, platform detection from URLs, automatic platform selection, and platform switching capabilities.

### Task 4: Create Comprehensive Documentation and Examples

**File**: `1-7-git-platform-configuration-management-task-4.md`
**Status**: ✅ Complete
**Description**: Create comprehensive documentation and examples for Git platform configuration management, including configuration guides, troubleshooting sections, environment variable references, migration guides, and security best practices.

### Task 5: Implement Comprehensive Testing

**File**: `1-7-git-platform-configuration-management-task-5.md`
**Status**: ✅ Complete
**Description**: Implement comprehensive testing for Git platform configuration management, including unit tests for all configuration components, integration tests for platform connections, performance tests for configuration loading, security tests for credential handling, and end-to-end tests for complete workflows.
