# Story 1.9: Basic CLI Scaffolding with Mode Selection

Status: ready-for-dev

## Story

As a developer,
I want a basic CLI entry point that supports both orchestrator and worker modes,
so that I can test mode switching and validate the hybrid architecture design.

## Acceptance Criteria

1. CLI supports `--mode orchestrator` flag for autonomous coordinator behavior
2. CLI supports `--mode worker` flag for CI/CD-invoked single-task execution
3. CLI loads configuration from config file and environment variables
4. CLI initializes AI provider abstraction and Git platform abstraction
5. CLI outputs mode selection to logs for debugging
6. CLI includes `--version` and `--help` commands with usage examples
7. Integration test demonstrates launching in both modes

## Tasks / Subtasks

- [ ] Task 1: CLI argument parsing and mode selection (AC: 1, 2, 6)
  - [ ] Subtask 1.1: Implement commander.js-based argument parser
  - [ ] Subtask 1.2: Add --mode flag validation (orchestrator|worker|standalone)
  - [ ] Subtask 1.3: Add --version and --help commands
  - [ ] Subtask 1.4: Add usage examples and error handling

- [ ] Task 2: Configuration loading and validation (AC: 3)
  - [ ] Subtask 2.1: Implement config file loading from ~/.tamma/config.json
  - [ ] Subtask 2.2: Add environment variable override support
  - [ ] Subtask 2.3: Add configuration validation with clear error messages
  - [ ] Subtask 2.4: Add config initialization wizard for first-time setup

- [ ] Task 3: Abstraction initialization (AC: 4)
  - [ ] Subtask 3.1: Initialize AI provider registry from config
  - [ ] Subtask 3.2: Initialize Git platform registry from config
  - [ ] Subtask 3.3: Validate provider and platform credentials
  - [ ] Subtask 3.4: Handle initialization failures gracefully

- [ ] Task 4: Mode-specific startup logic (AC: 1, 2, 5)
  - [ ] Subtask 4.1: Implement orchestrator mode startup (Fastify server)
  - [ ] Subtask 4.2: Implement worker mode startup (task polling)
  - [ ] Subtask 4.3: Implement standalone mode startup (direct execution)
  - [ ] Subtask 4.4: Add mode selection logging with debug information

- [ ] Task 5: Integration testing (AC: 7)
  - [ ] Subtask 5.1: Create integration test for orchestrator mode
  - [ ] Subtask 5.2: Create integration test for worker mode
  - [ ] Subtask 5.3: Create integration test for standalone mode
  - [ ] Subtask 5.4: Add test coverage for error scenarios

## Dev Notes

### Architecture Patterns
- Interface-based design pattern for provider/platform abstractions [Source: docs/tech-spec-epic-1.md#Interface-Based-Design-Pattern]
- Hybrid orchestrator/worker architecture pattern [Source: docs/tech-spec-epic-1.md#Hybrid-Architecture-Pattern]
- Plugin architecture with capability-based sandboxing [Source: docs/architecture.md#Plugin-Architecture-with-Capability-Based-Sandboxing]

### Project Structure Alignment
- CLI package location: `packages/cli/` [Source: docs/architecture.md#Project-Structure]
- Shared configuration package: `@tamma/config` [Source: docs/architecture.md#Project-Structure]
- Provider abstraction: `@tamma/providers` [Source: docs/architecture.md#Project-Structure]
- Platform abstraction: `@tamma/platforms` [Source: docs/architecture.md#Project-Structure]

### Technology Stack
- CLI Framework: commander.js [Source: docs/tech-spec-epic-1.md#Technology-Stack]
- Configuration: cosmiconfig for config discovery [Source: docs/tech-spec-epic-1.md#Technology-Stack]
- Interactive prompts: inquirer for setup wizard [Source: docs/tech-spec-epic-1.md#Technology-Stack]
- Validation: ajv for JSON Schema validation [Source: docs/tech-spec-epic-1.md#Technology-Stack]

### Implementation Constraints
- TypeScript 5.7+ strict mode required [Source: docs/architecture.md#Technology-Stack]
- Node.js 22 LTS runtime target [Source: docs/architecture.md#Technology-Stack]
- pnpm workspace monorepo structure [Source: docs/architecture.md#Technology-Stack]
- Exit code conventions: 0=success, 1=error, 2=config error [Source: docs/tech-spec-epic-1.md#CLI-Application]

### Configuration Schema
- TammaConfig interface with mode, providers, platforms sections [Source: docs/tech-spec-epic-1.md#Configuration-Models]
- Environment variable support: TAMMA_MODE, TAMMA_CONFIG_PATH [Source: docs/tech-spec-epic-1.md#Configuration-Dependencies]
- Default config location: ~/.tamma/config.json [Source: docs/tech-spec-epic-1.md#Configuration-Initialization-Workflow]

### Testing Requirements
- Unit tests with Jest 29+ and TypeScript support [Source: docs/tech-spec-epic-1.md#Unit-Testing-Strategy]
- Integration tests with real services [Source: docs/tech-spec-epic-1.md#Integration-Testing-Strategy]
- Coverage targets: 80% line, 75% branch, 85% function [Source: docs/tech-spec-epic-1.md#Coverage-Targets]

### Security Requirements
- Config file permissions set to 600 [Source: docs/tech-spec-epic-1.md#Security-Requirements]
- No credentials logged to stdout/stderr [Source: docs/tech-spec-epic-1.md#Security-Requirements]
- Input validation against injection attacks [Source: docs/tech-spec-epic-1.md#Security-Requirements]

### Performance Requirements
- CLI cold start < 1000ms (p95) [Source: docs/tech-spec-epic-1.md#Performance]
- Warm start < 300ms (p95) [Source: docs/tech-spec-epic-1.md#Performance]
- Memory usage < 256MB RSS for standalone mode [Source: docs/tech-spec-epic-1.md#Memory-Constraints]

## References

- [Source: docs/epics.md#Story-1.9-Basic-CLI-Scaffolding-with-Mode-Selection]
- [Source: docs/tech-spec-epic-1.md#CLI-Application]
- [Source: docs/architecture.md#Project-Structure]
- [Source: docs/architecture.md#Technology-Stack]
- [Source: docs/architecture.md#Epic-to-Architecture-Mapping]

## Dev Agent Record

### Context Reference

- docs/stories/1-9-basic-cli-scaffolding-with-mode-selection.context.xml

### Agent Model Used

Claude 3.5 Sonnet (2024-10-22)

### Debug Log References

### Completion Notes List

### File List