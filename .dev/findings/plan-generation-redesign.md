# Plan Generation Redesign

**When**: During sub-workflow #5 (PlanGenerationWorkflow) optimization
**Related**: SingleIssueCycle step 3, Story 2-17

## Design

The plan is an implementation blueprint, not a todo list. The architect/planner LLM produces:

### Plan Output Structure

```
Plan:
  summary: "Brief description of what we're building"

  tasks:
    - id: "T1"
      title: "Define interfaces"
      description: "..."
      dependencies: []          # DAG — no blockers
      files: ["src/types.ts"]
      type: "design"

    - id: "T2"
      title: "Implement service"
      dependencies: ["T1"]      # blocked by T1
      files: ["src/service.ts"]
      type: "implementation"

    - id: "T3"
      title: "Write tests"
      dependencies: ["T1"]      # can run parallel with T2
      files: ["src/service.test.ts"]
      type: "test"

  techStack:
    language: "TypeScript"
    framework: "Fastify"
    testFramework: "Vitest"
    patterns: ["repository pattern", "dependency injection"]

  externalDependencies:
    - name: "zod"
      reason: "Schema validation for API inputs"
      version: "^3.23"
    - name: "@octokit/rest"
      reason: "GitHub API calls"
      existing: true             # already in package.json

  designDecisions:
    - decision: "Use repository pattern for data access"
      reasoning: "Consistent with existing codebase (packages/intelligence/src/)"
      alternatives: ["Direct DB queries", "Active Record"]

    - decision: "Add new interface, don't modify existing"
      reasoning: "Backward compatibility, existing consumers unaffected"

  testStrategy:
    unit: "Test each method in isolation with mocked dependencies"
    integration: "Test against real DB with test fixtures"
    coverage: "80% line, 75% branch target"
    framework: "Vitest (project standard)"

  riskAssessment:
    - risk: "Breaking change to API response format"
      impact: "high"
      mitigation: "Add new fields, don't remove existing"

    - risk: "New dependency adds bundle size"
      impact: "low"
      mitigation: "zod is already used elsewhere"

  fileMap:
    create: ["src/service.ts", "src/types.ts", "src/service.test.ts"]
    modify: ["src/index.ts"]     # add export
    delete: []
```

### Inputs
- PO summary from context gathering
- Context IDs to fetch from vector DB
- Links to related issues/PRs
- Work item type (bug/feature/security/chore)

### Role
- Phase: PLANNING
- Role: architect/planner
- Needs: project conventions (CLAUDE.md), architecture docs, existing patterns

### Task DAG
Tasks form a directed acyclic graph. The TDD cycle processes them in dependency order:
- Independent tasks can run in parallel (future optimization)
- Blocked tasks wait for dependencies
- Each task is a unit of work for one TDD red-green-refactor cycle

### Approval
After plan generation, a human or auto-approval checkpoint reviews:
- Are the design decisions sound?
- Is the scope correct?
- Are risks acceptable?
- Should any tasks be removed/added?
