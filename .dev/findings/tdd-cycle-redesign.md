# TDD Cycle Redesign

**When**: During sub-workflow (TddWorkflow) optimization
**Related**: SingleIssueCycleWorkflow step 10

## Design

TDD cycle processes tasks in dependency order. Each task goes through:

```
For each task (in dependency order):
  1. Write failing tests (from test cases created in step 9)
  2. Run tests → verify they fail (RED)
  3. Write implementation (minimum to pass)
  4. Run full CI (tests + lint + security + build)
     ├─ Pass → 5. Refactor
     └─ Fail → Debug → retry (max 3)
  5. Analyze code → Refactor if needed
  6. Run CI again → verify still passes
  7. Commit
```

### CI is INSIDE the TDD loop
No separate CI step after TDD. Each task runs full CI:
- Unit tests (new + existing)
- Integration tests
- Linting / formatting
- Security scan (npm audit, CodeQL)
- Build verification

### Test Cases
Test cases are created in step 9 (before TDD). TDD step 1 uses them as
the specification for what to implement. The LLM writes the actual test
code based on the test case descriptions.

### Debug Retry
On CI failure:
- Analyze the failure (which check failed?)
- If test failure → debug and fix implementation
- If lint failure → auto-fix
- If security issue → assess and fix
- Max 3 retries per task before escalating

### Parallel Tasks
Tasks with no dependencies can potentially run in parallel (future optimization).
For now: sequential in topological order.
