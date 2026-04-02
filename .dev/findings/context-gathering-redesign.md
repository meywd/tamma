# Context Gathering Redesign

**When**: During sub-workflow #4 (ContextGatheringWorkflow) optimization
**Related**: SingleIssueCycle step 2, Story 2-20, Epic 6

## Design

Context gathering should NOT pass raw data through workflow variables. Instead:

### Step 1: Gather & Store
- Fetch raw data (issue, commits, files, patterns, tests, security alerts)
- Chunk and embed into vector DB (keyed by issue/cycle ID)
- Store structured metadata (issue number, file paths, labels, relationships)

### Step 2: Product Owner LLM Review
- A **Product Owner role LLM** reviews the gathered context
- Produces:
  - **Summary** of the issue/work item and what needs to happen
  - **Context IDs** — references to vector DB chunks the next step should fetch
  - **Relevant links** — PRs, docs, related issues, architecture docs
  - **Scope assessment** — what's in scope, what's out
  - **Risk flags** — dependencies, breaking changes, security concerns
- This is the PO making a judgment call on what matters, not a raw data dump

### Step 3: Downstream Steps
- Each subsequent step (Plan, TDD, Review, etc.) receives:
  - The PO summary
  - Context IDs to fetch from vector DB
  - Links
- Each step queries the vector DB with its own role-specific needs:
  - Planner: architecture, file structure, dependencies
  - Tester: existing tests, coverage, test patterns
  - Reviewer: coding standards, CLAUDE.md, style rules

### Why PO LLM
- Applies Minimum Viable Context (MVC) — doses each step with what it needs
- Catches scope creep early
- Identifies risks before implementation starts
- Acts as the "product brain" between raw data and dev execution

### Infrastructure
Already exists in packages/intelligence/src/:
- CodebaseIndexer (943 lines)
- Vector stores (ChromaDB, PgVector)
- RAG pipeline (387 lines)
- Context aggregator (259 lines)

Just needs wiring into the Elsa workflow.

## References
- https://ragaboutit.com/why-context-engineering-is-replacing-your-rag-architecture/
- https://pub.spillwave.com/agent-brain-a-code-first-rag-system-for-ai-coding-assistants
- Gartner 2026: "the year of context"
