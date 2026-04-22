---
title: "SUPERSEDED by Epic 27"
sidebar:
  order: 90
---

> **This task file is superseded by Epic 27 (Prompt Store -- Multi-Tenant Prompt Management).**
> The prompt registry functionality defined here has been absorbed into the Postgres-backed prompt store.
> See `/home/meywd/tamma/docs/stories/epic-27/README.md` for the replacement.
> This file is retained for historical reference only. Do not implement.

---

# Task 1: Define Built-in Role Prompt Templates, Constants, and Interfaces

**Story:** 9-6-agent-prompt-registry - Agent Prompt Registry
**Epic:** 9

## Task Description

Create the `packages/providers/src/agent-prompt-registry.ts` file and define the `BUILTIN_TEMPLATES` constant (frozen, covering all 9 AgentType roles), `GENERIC_FALLBACK` string, size limit constants (`MAX_TEMPLATE_LENGTH`, `MAX_VAR_VALUE_LENGTH`), `FORBIDDEN_KEYS` set, the `IAgentPromptRegistry` interface, and the `AgentPromptRegistryOptions` interface. These are the default prompt preambles for each agent role, extracted from the existing inline prompts in `packages/orchestrator/src/engine.ts`. Templates use `{{variable}}` placeholder syntax for dynamic content that the engine will supply at render time.

## Acceptance Criteria

- `BUILTIN_TEMPLATES` is typed as `Partial<Record<AgentType, string>>`, frozen via `Object.freeze()`, and contains entries for all 9 `AgentType` roles: `architect`, `implementer`, `reviewer`, `tester`, `analyst`, `scrum_master`, `researcher`, `planner`, `documenter`
- `GENERIC_FALLBACK` is a non-empty string used when no built-in template exists for a role
- `MAX_TEMPLATE_LENGTH = 1_000_000` (1MB) and `MAX_VAR_VALUE_LENGTH = 100_000` (100KB) size limit constants are defined
- `FORBIDDEN_KEYS` is a `Set` containing `__proto__`, `constructor`, `prototype`
- `IAgentPromptRegistry` interface is exported with `render()`, `resolveTemplate()`, `registerBuiltin()` methods
- `AgentPromptRegistryOptions` interface is exported with `config: AgentsConfig`, optional `logger?: ILogger`, optional `immutableRoles?: ReadonlySet<string>`
- Templates use `{{variable}}` placeholder syntax (e.g., `{{context}}`, `{{issueNumber}}`)
- Prompt preambles are extracted from `engine.ts` `generatePlan()` and `implementCode()` methods
- `AgentType` is imported from `@tamma/shared` using `import type`
- `AgentsConfig` is imported from `@tamma/shared/src/types/agent-config.js` using `import type`
- `ILogger` is imported from `@tamma/shared/contracts` using `import type`
- File compiles under TypeScript strict mode

## Implementation Details

### Technical Requirements

- [ ] Create `packages/providers/src/agent-prompt-registry.ts`
- [ ] Add `import type { AgentType } from '@tamma/shared';`
- [ ] Add `import type { AgentsConfig } from '@tamma/shared/src/types/agent-config.js';`
- [ ] Add `import type { ILogger } from '@tamma/shared/contracts';`
- [ ] Define `BUILTIN_TEMPLATES` as a frozen module-level constant with all 9 roles:
  ```typescript
  const BUILTIN_TEMPLATES: Partial<Record<AgentType, string>> = Object.freeze({
    architect: 'You are analyzing a GitHub issue to create a development plan.\n\n{{context}}',
    implementer: 'You are an autonomous coding agent. Implement the following plan for issue #{{issueNumber}}.',
    reviewer: 'You are a code reviewer. Review the changes for correctness, style, and security.',
    tester: 'You are a testing agent. Write and run tests for the described changes.',
    analyst: 'You are analyzing project context to understand codebase structure and conventions.',
    scrum_master: 'You are a project coordinator. Select the most appropriate issue to work on next.',
    researcher: 'You are a research agent. Investigate and gather information about the topic at hand.',
    planner: 'You are a planning agent. Create structured plans and organize work breakdown.',
    documenter: 'You are a documentation agent. Write clear, comprehensive documentation for the codebase.',
  });
  ```
- [ ] Define `GENERIC_FALLBACK` constant:
  ```typescript
  const GENERIC_FALLBACK = 'You are an AI assistant working on a software development task.';
  ```
- [ ] Define size limit constants:
  ```typescript
  /** Maximum rendered template length (1MB). Templates exceeding this are truncated with a warning. */
  const MAX_TEMPLATE_LENGTH = 1_000_000;

  /** Maximum variable value length (100KB). Variables exceeding this are skipped with a warning. */
  const MAX_VAR_VALUE_LENGTH = 100_000;
  ```
- [ ] Define `FORBIDDEN_KEYS` set:
  ```typescript
  /** Keys that cannot be used as role names or provider names (prototype pollution guard). */
  const FORBIDDEN_KEYS = new Set(['__proto__', 'constructor', 'prototype']);
  ```
- [ ] Define `AgentPromptRegistryOptions` interface:
  ```typescript
  export interface AgentPromptRegistryOptions {
    config: AgentsConfig;
    logger?: ILogger;
    immutableRoles?: ReadonlySet<string>;
  }
  ```
- [ ] Define `IAgentPromptRegistry` interface:
  ```typescript
  export interface IAgentPromptRegistry {
    render(role: AgentType, providerName: string, vars?: Record<string, string>): string;
    resolveTemplate(role: AgentType, providerName: string): string;
    registerBuiltin(role: string, template: string): void;
  }
  ```
- [ ] Stub the `AgentPromptRegistry` class implementing `IAgentPromptRegistry` with constructor accepting `AgentPromptRegistryOptions` and copying `BUILTIN_TEMPLATES` into a prototype-free instance field:
  ```typescript
  export class AgentPromptRegistry implements IAgentPromptRegistry {
    private readonly config: AgentsConfig;
    private readonly logger?: ILogger;
    private readonly immutableRoles: ReadonlySet<string>;
    private builtinTemplates: Record<string, string>;

    constructor(options: AgentPromptRegistryOptions) {
      this.config = options.config;
      this.logger = options.logger;
      this.immutableRoles = options.immutableRoles ?? new Set();
      // Use Object.create(null) for prototype-free backing (prototype pollution guard)
      this.builtinTemplates = Object.create(null) as Record<string, string>;
      for (const [key, value] of Object.entries(BUILTIN_TEMPLATES)) {
        this.builtinTemplates[key] = value;
      }
    }
  }
  ```
- [ ] Export the class and interfaces (the constants remain module-private)

### Files to Modify/Create

- `packages/providers/src/agent-prompt-registry.ts` -- **CREATE** -- New file with BUILTIN_TEMPLATES (frozen, 9 roles), GENERIC_FALLBACK, MAX_TEMPLATE_LENGTH, MAX_VAR_VALUE_LENGTH, FORBIDDEN_KEYS, IAgentPromptRegistry, AgentPromptRegistryOptions, and AgentPromptRegistry class stub

### Dependencies

- [ ] `packages/shared/src/types/knowledge.ts` must export `AgentType` (already does)
- [ ] `packages/shared/src/types/agent-config.ts` must export `AgentsConfig` (Story 9-1)
- [ ] `packages/shared/contracts` must export `ILogger`

### Source Material

The built-in templates are derived from these inline prompts in `packages/orchestrator/src/engine.ts`:

- **architect** template: from `generatePlan()` line 412 -- `"You are analyzing a GitHub issue to create a development plan.\n\n${context}"`
- **implementer** template: from `implementCode()` line 654 -- `"You are an autonomous coding agent. Implement the following plan for issue #${issue.number}."`
- **reviewer**, **tester**, **analyst**, **scrum_master** templates: new role-appropriate preambles (no existing engine prompts for these roles yet)

## Testing Strategy

### Unit Tests

- [ ] Verify `BUILTIN_TEMPLATES` (accessed via an `AgentPromptRegistry` instance with minimal config) covers all 9 expected roles: architect, implementer, reviewer, tester, analyst, scrum_master, researcher, planner, documenter
- [ ] Verify each built-in template is a non-empty string
- [ ] Verify `GENERIC_FALLBACK` is returned for roles not in `BUILTIN_TEMPLATES` (no such roles now -- all 9 AgentType values have built-ins)
- [ ] Verify the class constructor does not throw with a valid `AgentPromptRegistryOptions`
- [ ] Verify the class constructor accepts `AgentPromptRegistryOptions` (not bare `AgentsConfig`)
- [ ] Verify `IAgentPromptRegistry` interface is exported and `AgentPromptRegistry` implements it

### Validation Steps

1. [ ] Create the file with imports, constants, and class stub
2. [ ] Run `pnpm --filter @tamma/providers run typecheck` -- must pass
3. [ ] Verify imports resolve correctly (AgentType, AgentsConfig)
4. [ ] Begin writing tests in `packages/providers/src/agent-prompt-registry.test.ts` (tests will be extended in Tasks 2 and 3)

## Notes & Considerations

- The class is named `AgentPromptRegistry` implementing `IAgentPromptRegistry` to avoid collision with `PromptRegistry` in `packages/mcp-client/src/registry.ts` which manages MCP prompt discovery
- `IAgentPromptRegistry` interface follows the same pattern as `IProviderHealthTracker` (9-3), `IAgentProviderFactory` (9-4), and `IProviderChain` (9-5)
- `builtinTemplates` uses `Record<string, string>` (not `Partial<Record<AgentType, string>>`) for the instance field because `registerBuiltin()` (Task 2) accepts arbitrary `string` keys for extensibility
- The instance field uses `Object.create(null)` as a prototype-free backing store to prevent prototype pollution
- `BUILTIN_TEMPLATES` is frozen via `Object.freeze()` consistent with `DEFAULT_PHASE_ROLE_MAP` in Story 9-1 and `LEGACY_PROVIDER_MAP`
- The constructor accepts `AgentPromptRegistryOptions` (not bare `AgentsConfig`) to allow optional `logger` and `immutableRoles` -- the options object pattern is acceptable since the registry is a leaf dependency with no complex initialization
- Templates intentionally use `{{variable}}` mustache-style placeholders instead of JS template literals because they are stored as data and rendered later by `render()`
- Only the role preamble (static system context) lives in the registry; the engine appends dynamic content (issue details, plan JSON schema, etc.) after rendering
- The `reviewer` role template is security-critical (controls code review behavior); callers should consider including it in `immutableRoles`

## Completion Checklist

- [ ] `packages/providers/src/agent-prompt-registry.ts` created
- [ ] `BUILTIN_TEMPLATES` defined with 9 role entries and frozen via `Object.freeze()`
- [ ] `GENERIC_FALLBACK` defined
- [ ] `MAX_TEMPLATE_LENGTH` and `MAX_VAR_VALUE_LENGTH` size limit constants defined
- [ ] `FORBIDDEN_KEYS` set defined
- [ ] `IAgentPromptRegistry` interface exported
- [ ] `AgentPromptRegistryOptions` interface exported
- [ ] `AgentPromptRegistry` class stub implementing `IAgentPromptRegistry` with constructor accepting `AgentPromptRegistryOptions`
- [ ] `builtinTemplates` uses `Object.create(null)` prototype-free backing
- [ ] Imports use `import type` and `.js` extensions
- [ ] TypeScript strict mode compilation passes
- [ ] Initial unit tests written and passing
