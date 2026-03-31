---
title: "Story 10.1: Engine Static Workflow — Orchestrator Tool Loop"
sidebar:
  order: 100
---

Status: ready-for-dev

## Story

As a **platform architect**,
I want the engine to run a standard agentic tool-calling loop where the LLM (configured via the `orchestrator` role) receives tools for querying state, triggering workflows, signaling workflows, and answering users, and the LLM itself decides what to do on each turn,
so that the engine's behavior is driven by the LLM and system prompt — not by hardcoded routing logic — and all configuration (model, provider, prompt, budget) flows through the existing LLM Engine config.

## Acceptance Criteria

1. Engine runs the standard agentic loop: LLM responds with tool calls → engine executes tools → results fed back → LLM responds again → loop until LLM produces text-only response
2. The LLM uses the `orchestrator` role — model, provider chain, prompt, budget, and timeout are all determined by config via `RoleBasedAgentResolver`, same as every other role
3. No hardcoded routing, no fast paths, no decision trees — the LLM decides what to do based on tools available and context provided
4. Engine provides tools to the LLM: `query_state`, `trigger_workflow`, `signal_workflow`, `query_events`, `answer_user` — each tool is a well-defined operation the engine can execute
5. Tool definitions are provided via config (not hardcoded) — tools can be added, removed, or modified without code changes
6. Every tool execution is recorded to the event store (tool call event + tool result event)
7. Context assembly provides the LLM with: current project state (from projections), active workflows, recent events, and the incoming input — all injected into the conversation
8. The loop continues until the LLM produces a final text response (no more tool calls), at which point the response is sent to the client
9. Context window management: conversation history within a session, with compaction when approaching limits (following industry pattern)
10. The static workflow is the engine's own loop — it is NOT defined in Elsa or any external workflow provider
11. When workflow provider is down, tools that interact with it (`trigger_workflow`, `signal_workflow`) return error results — the LLM sees this and adapts (e.g., informs user, queues intent)

## Technical Context

### The Agentic Loop Pattern (Industry Standard)

Every major AI coding tool uses the same pattern. Research across Claude Code, OpenCode, Codex CLI, Cline, Aider, and Copilot confirms:

```
while LLM_response contains tool_calls:
    for each tool_call in response:
        result = execute_tool(tool_call)
        record_event(TOOL_EXECUTED, { tool, args, result })
    feed results back to LLM
# loop exits when LLM produces text-only response
```

**The LLM IS the router.** There is no external brain that decides what the LLM should do. The orchestration gives the LLM good tools, good context, and lets it loop.

Key findings from research:
- **Claude Code**: Single-threaded while-loop. Model decides to call tools or respond. 110+ conditionally-injected prompt sections. Sub-agents for isolated tasks.
- **OpenCode**: `SessionPrompt.loop()` — streams tool calls, executes immediately, feeds results back. Provider-specific prompts. SQLite-backed session persistence.
- **Codex CLI**: Stateless request-response turns. Full history per request. Auto-compaction when tokens exceed threshold.
- **Cline**: `attempt_completion` tool signals the loop is done. Plan/Act modes with different tool sets. Model-family-specific prompt variants.
- **Aider**: Two-model pipeline (architect proposes, editor implements). Repository map for compressed codebase context. Deliberately avoids structured output.
- **Copilot**: Four autonomy levels. Fleet mode for parallel subagents. Dual-format plans (Markdown + JSON).

### Engine Tool Definitions

The orchestrator LLM receives these tools. Each is an operation the engine can execute:

```typescript
// Tools provided to the orchestrator LLM
// Defined via config, not hardcoded — these are the defaults

interface OrchestratorTools {
  // Read current state from event store projections
  query_state: {
    input: { projection: 'project' | 'cost' | 'queue' | 'workflow'; filter?: Record<string, unknown> };
    output: { state: Record<string, unknown> };
  };

  // Query raw events from the event store
  query_events: {
    input: { filter: EventFilter; limit?: number };
    output: { events: TammaEvent[]; totalCount: number };
  };

  // Start a new workflow on the workflow provider
  trigger_workflow: {
    input: { workflowName: string; input: Record<string, unknown> };
    output: { instanceId: string; status: string } | { error: string; queued: boolean };
  };

  // Send a signal to a running workflow
  signal_workflow: {
    input: { instanceId: string; signal: string; payload?: unknown };
    output: { acknowledged: boolean } | { error: string; queued: boolean };
  };

  // Send a response to the user/client
  answer_user: {
    input: { message: string; data?: Record<string, unknown> };
    output: { delivered: boolean; channel: string };
  };

  // Queue an intent for later dispatch (when workflow provider is down)
  queue_intent: {
    input: { type: 'trigger' | 'signal'; workflowName?: string; instanceId?: string; signal?: string; payload?: unknown };
    output: { intentId: string; position: number };
  };
}
```

### The Loop in Practice

**Example: User says "start working on issue #42"**

```
Turn 1: LLM receives input + tools + system prompt
  LLM decides: "I should check if there's already a workflow for #42"
  Tool call: query_state({ projection: 'project' })
  Result: { activeWorkflows: [], pendingApprovals: [], issuesInProgress: [] }

Turn 2: LLM sees no active workflow for #42
  LLM decides: "I should start the autonomous-dev workflow"
  Tool call: trigger_workflow({ workflowName: 'autonomous-dev', input: { issueNumber: 42 } })
  Result: { instanceId: 'xyz-123', status: 'started' }

Turn 3: LLM sees workflow started successfully
  LLM decides: "I should inform the user"
  Tool call: answer_user({ message: 'Started working on issue #42. Workflow xyz-123 is running.' })
  Result: { delivered: true, channel: 'cli' }

Turn 4: LLM produces text-only response (loop ends)
  "Issue #42 is now being processed."
```

**Example: User says "start working on #42" but it's already running**

```
Turn 1: LLM receives input
  Tool call: query_state({ projection: 'project' })
  Result: { activeWorkflows: [{ instanceId: 'xyz', issue: 42, step: 'implementation' }] }

Turn 2: LLM sees workflow already running
  Tool call: answer_user({ message: 'Issue #42 is already being worked on (currently in implementation step).' })
  Result: { delivered: true }

Turn 3: Text-only response (loop ends)
```

**Example: Workflow provider is down**

```
Turn 1: LLM receives input
  Tool call: query_state({ projection: 'project' })
  Result: { activeWorkflows: [], ... }

Turn 2: LLM decides to start workflow
  Tool call: trigger_workflow({ workflowName: 'autonomous-dev', input: { issueNumber: 42 } })
  Result: { error: 'Workflow provider unavailable', queued: false }

Turn 3: LLM sees the error, decides to queue
  Tool call: queue_intent({ type: 'trigger', workflowName: 'autonomous-dev', payload: { issueNumber: 42 } })
  Result: { intentId: 'abc', position: 1 }

Turn 4: LLM informs user
  Tool call: answer_user({ message: 'Workflow engine is temporarily unavailable. Your request to work on #42 has been queued and will execute when the engine recovers.' })
  Result: { delivered: true }

Turn 5: Text-only (loop ends)
```

**No hardcoded logic decided any of this. The LLM did.**

### Configuration — Everything Through Existing LLM Engine

The `orchestrator` role uses the same config system as all other roles:

```yaml
agents:
  roles:
    orchestrator:
      providerChain:
        - provider: anthropic
          model: claude-sonnet-4-20250514  # or whatever the operator chooses
        - provider: openrouter
          model: anthropic/claude-sonnet   # fallback
      systemPrompt: |
        You are the Tamma Orchestrator. You manage autonomous development workflows.
        Use the provided tools to query state, trigger workflows, and communicate
        with users. Always check current state before taking action.
      maxBudgetUsd: 1.0
      timeout: 30000
      tools:
        - query_state
        - query_events
        - trigger_workflow
        - signal_workflow
        - answer_user
        - queue_intent
```

The system prompt, model, provider, budget, timeout, and available tools are ALL config. The engine code just runs the loop.

### Session Management

Following the industry pattern (Claude Code, OpenCode, Codex all do this):

- **Session per interaction**: Each user command or webhook event starts a session (or continues one)
- **Conversation history**: Tool calls and results accumulate as conversation turns
- **Context assembly**: Before each LLM call, inject current state as system context
- **Compaction**: When context approaches limits, summarize older turns (configurable strategy)
- **Persistence**: Sessions stored in event store (SESSION_STARTED, SESSION_TURN, SESSION_COMPLETED events)

### Relationship to Existing Code

| Current | New |
|---------|-----|
| `TammaEngine.run()` polling loop | Removed — engine reacts to intake events |
| `TammaEngine.runPipeline()` 8 steps | Removed — replaced by orchestrator tool loop + Elsa workflows |
| `TammaEngine.selectIssue()` | Moved to Elsa workflow activity |
| `TammaEngine.analyzeIssue()` | Moved to Elsa workflow activity |
| `TammaEngine.generatePlan()` | Moved to Elsa workflow activity |
| `TammaEngine.awaitApproval()` | Orchestrator LLM handles via tools |
| `TammaEngine.implementCode()` | Moved to Elsa workflow activity |
| `TammaEngine.createPR()` | Moved to Elsa workflow activity |
| `TammaEngine.monitorAndMerge()` | Moved to Elsa workflow activity |
| `EngineState` enum | Derived from event store projections |
| Hardcoded decision logic | LLM decides via tool loop |

## Tasks / Subtasks

- [ ] Task 1: Define orchestrator tool interfaces (AC: 4, 5)
  - [ ] Subtask 1.1: Define tool input/output types for each orchestrator tool
  - [ ] Subtask 1.2: Define `IOrchestratorTool` interface with `name`, `description`, `inputSchema`, `execute()`
  - [ ] Subtask 1.3: Define tool registry that loads tool definitions from config
  - [ ] Subtask 1.4: Implement `QueryStateTool` — reads from projection engine
  - [ ] Subtask 1.5: Implement `QueryEventsTool` — reads from event store with filters
  - [ ] Subtask 1.6: Implement `TriggerWorkflowTool` — dispatches to workflow provider or returns error
  - [ ] Subtask 1.7: Implement `SignalWorkflowTool` — sends signal to workflow provider or returns error
  - [ ] Subtask 1.8: Implement `AnswerUserTool` — sends response to originating client transport
  - [ ] Subtask 1.9: Implement `QueueIntentTool` — enqueues to Smart Queue

- [ ] Task 2: Implement the agentic tool loop (AC: 1, 8, 10)
  - [ ] Subtask 2.1: Create `OrchestratorLoop` class implementing the standard while-loop pattern
  - [ ] Subtask 2.2: Call LLM via `RoleBasedAgentResolver.getAgentForRole('orchestrator')` — model/provider from config
  - [ ] Subtask 2.3: Parse LLM response: extract tool calls or detect text-only termination
  - [ ] Subtask 2.4: Execute tool calls, collect results, feed back as next turn
  - [ ] Subtask 2.5: Handle loop termination: LLM produces text-only response → send to client
  - [ ] Subtask 2.6: Implement configurable max turns safety cap (from config, not hardcoded)
  - [ ] Subtask 2.7: Handle LLM errors: provider failure → retry via provider chain fallback

- [ ] Task 3: Implement context assembly (AC: 7)
  - [ ] Subtask 3.1: Build system context block: project state from projections, active workflows, recent events
  - [ ] Subtask 3.2: Inject incoming input (normalized EngineIntakeEvent) as user message
  - [ ] Subtask 3.3: Maintain conversation history within session (tool calls + results as turns)
  - [ ] Subtask 3.4: Inject project-specific instructions (equivalent of CLAUDE.md/AGENTS.md pattern)

- [ ] Task 4: Implement session management (AC: 9)
  - [ ] Subtask 4.1: Create session on intake, assign session ID
  - [ ] Subtask 4.2: Persist session turns to event store (SESSION_STARTED, SESSION_TURN events)
  - [ ] Subtask 4.3: Implement context compaction when approaching context window limits
  - [ ] Subtask 4.4: Support session continuation (subsequent inputs in same interaction)
  - [ ] Subtask 4.5: Record SESSION_COMPLETED on loop termination

- [ ] Task 5: Record all tool executions to event store (AC: 6)
  - [ ] Subtask 5.1: Record TOOL_CALL_REQUESTED event before each tool execution
  - [ ] Subtask 5.2: Record TOOL_CALL_COMPLETED event after each tool execution (with result)
  - [ ] Subtask 5.3: Record TOOL_CALL_FAILED event on tool execution errors
  - [ ] Subtask 5.4: Link tool events via correlationId to the session

- [ ] Task 6: Handle workflow provider unavailability (AC: 11)
  - [ ] Subtask 6.1: `trigger_workflow` tool returns error result when provider unhealthy
  - [ ] Subtask 6.2: `signal_workflow` tool returns error result when provider unhealthy
  - [ ] Subtask 6.3: LLM sees error results and can choose to use `queue_intent` or `answer_user`
  - [ ] Subtask 6.4: No special handling code — the LLM adapts based on tool results

- [ ] Task 7: Refactor TammaEngine (AC: 1, 2, 10)
  - [ ] Subtask 7.1: Replace `run()` polling loop with event-driven intake
  - [ ] Subtask 7.2: Remove `runPipeline()` and all 8 inline step methods
  - [ ] Subtask 7.3: Inject `RoleBasedAgentResolver`, `ISmartQueue`, `IEventStore`, tool registry
  - [ ] Subtask 7.4: Preserve existing transport interfaces (InProcess, Remote) — they produce intake events
  - [ ] Subtask 7.5: Update engine API routes to trigger orchestrator loop on intake
  - [ ] Subtask 7.6: Register `orchestrator` role in agent config with default prompt and tools

- [ ] Task 8: Testing (AC: all)
  - [ ] Subtask 8.1: Unit test tool loop with mocked LLM (scripted tool call sequences)
  - [ ] Subtask 8.2: Unit test each orchestrator tool in isolation
  - [ ] Subtask 8.3: Unit test context assembly includes state + input + history
  - [ ] Subtask 8.4: Unit test loop termination on text-only response
  - [ ] Subtask 8.5: Unit test max turns safety cap
  - [ ] Subtask 8.6: Integration test: user command → loop → tool calls → events recorded
  - [ ] Subtask 8.7: Integration test: workflow provider down → LLM queues intent via tools
  - [ ] Subtask 8.8: Integration test: session persistence and continuation

## Dev Notes

### Requirements Context Summary

This story is the centerpiece of Epic 10. It replaces the current imperative engine loop with an LLM-driven agentic tool loop. The key insight from researching Claude Code, OpenCode, Codex, Cline, Aider, and Copilot is that **none of them use external routing logic** — the LLM itself decides via tool calls. The engine's job is to provide good tools, good context, and run the loop.

### Design Principles (from research)

1. **The LLM IS the router** — no external decision trees, no fast paths, no hardcoded routing
2. **Tools are the API** — well-defined tools with clear descriptions let the LLM understand what it can do
3. **Context is king** — assemble rich context (state, history, events) so the LLM can make informed decisions
4. **Config controls everything** — model, provider, prompt, tools, budget, timeout — all via existing LLM Engine config
5. **The loop is simple** — while has tool calls → execute → feed back. Complexity lives in the tools and prompt, not the loop.

### Project Structure Notes

- New types: `packages/shared/src/types/orchestrator.ts`
- New implementation: `packages/orchestrator/src/loop/orchestrator-loop.ts`
- New implementation: `packages/orchestrator/src/loop/session.ts`
- New implementation: `packages/orchestrator/src/tools/query-state.ts`
- New implementation: `packages/orchestrator/src/tools/query-events.ts`
- New implementation: `packages/orchestrator/src/tools/trigger-workflow.ts`
- New implementation: `packages/orchestrator/src/tools/signal-workflow.ts`
- New implementation: `packages/orchestrator/src/tools/answer-user.ts`
- New implementation: `packages/orchestrator/src/tools/queue-intent.ts`
- Modified: `packages/orchestrator/src/engine.ts` (major refactor)
- Modified: `packages/shared/src/types/agent-config.ts` (add orchestrator to DEFAULT_PHASE_ROLE_MAP)

### References

- **Epic 10 Tech Spec:** `docs/stories/epic-10/tech-spec-epic-10.md`
- **Current Engine:** `packages/orchestrator/src/engine.ts`
- **LLM Engine:** `packages/providers/src/role-based-agent-resolver.ts`
- **Prompt Registry:** `packages/providers/src/agent-prompt-registry.ts`
- **Existing Transports:** `packages/orchestrator/src/transports/`
- **Claude Code Architecture:** Single-threaded tool loop, 110+ prompt sections, sub-agents
- **OpenCode Architecture:** `SessionPrompt.loop()`, provider-specific prompts, SQLite sessions
- **Codex CLI Architecture:** Stateless turns, auto-compaction, OS-level sandboxing

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-26 | 1.0 | Initial story creation | Architecture Team |
| 2026-03-26 | 2.0 | Complete rewrite: removed fast paths, role is orchestrator, LLM-driven tool loop pattern based on industry research (Claude Code, OpenCode, Codex, Cline, Aider, Copilot) | Architecture Team |

## Logging Requirements

Engine core is the most critical path — logging must be comprehensive without being noisy.

- **INFO**: Engine started/stopped, workflow dispatched (workflow ID, issue ID), step transition (from state -> to state), queue item enqueued/dequeued
- **DEBUG**: State reconstruction details, event replay progress, queue deduplication decisions, ELSA workflow variable snapshots
- **WARN**: Queue backpressure detected, state reconstruction took >5s, event gap in stream, workflow execution slow
- **ERROR**: Engine crash (with full context for restart), state reconstruction failed, event store unreachable, workflow dispatch failed, queue corruption
- **Structured context**: Always include `{ workflowInstanceId, issueId, engineState, queueDepth }`
- **Idempotency**: Log enough context to verify idempotent replay (event IDs, sequence numbers, dedup keys)
