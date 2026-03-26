# Story 10.1: Engine Static Workflow & Brain

Status: ready-for-dev

## Story

As a **platform architect**,
I want the engine to have its own static workflow that receives all inputs, consults an LLM to understand context, and decides whether to answer directly, trigger a workflow, or reject a duplicate,
so that the engine acts as an intelligent brain that routes work rather than executing it, and continues functioning even when the workflow provider is unavailable.

## Acceptance Criteria

1. Engine implements a static workflow loop: intake -> load state -> LLM decision -> route -> record
2. The static workflow is hardcoded in the engine (not defined in Elsa or any external workflow provider)
3. Engine receives inputs from all channels (UI commands, platform events, workflow callbacks) through a single normalized intake
4. Engine calls LLM (via existing LLM Engine from Epic 1) to classify intent and decide routing
5. LLM decision produces one of four outcomes: answer directly, trigger workflow, signal running workflow, reject as duplicate/invalid
6. Every decision is recorded to the event store before any action is taken
7. Engine responds to queries (status, history, plan review) directly from event store without needing the workflow provider
8. Engine uses a fast/lightweight LLM model for routing decisions (configurable, defaults to Haiku-class)
9. Unambiguous commands bypass LLM (e.g., "approve" when exactly one plan is pending) for sub-100ms response
10. Engine brain has access to full conversation context and project state when making decisions

## Technical Context

### Static Workflow Steps

```typescript
interface EngineIntakeEvent {
  source: 'cli' | 'web' | 'mobile' | 'desktop' | 'webhook' | 'callback';
  channel: string; // e.g., 'github', 'gitea', 'gitlab', 'direct'
  actor: {
    type: 'user' | 'system' | 'platform' | 'workflow-provider';
    id: string;
    name?: string;
  };
  payload: NormalizedInput;
  rawEvent?: unknown; // Original event before normalization
  receivedAt: string; // ISO 8601
}

interface BrainDecision {
  action: 'answer' | 'trigger_workflow' | 'signal_workflow' | 'reject';
  confidence: number; // 0-1
  reasoning: string; // LLM's explanation

  // For 'answer': direct response to client
  response?: string;

  // For 'trigger_workflow': what to start
  workflowName?: string;
  workflowInput?: Record<string, unknown>;

  // For 'signal_workflow': what to send
  workflowInstanceId?: string;
  signal?: string;
  signalPayload?: unknown;

  // For 'reject': why
  rejectionReason?: string;
}

type NormalizedInput =
  | { type: 'command'; command: string; args: Record<string, unknown> }
  | { type: 'query'; question: string }
  | { type: 'approval'; decision: 'approve' | 'reject' | 'skip'; target?: string }
  | { type: 'platform_event'; event: string; data: Record<string, unknown> }
  | { type: 'workflow_callback'; instanceId: string; step: string; result: unknown };
```

### Static Workflow Sequence

```
1. INTAKE
   - Receive raw input from any channel
   - Normalize to EngineIntakeEvent
   - Record INPUT_RECEIVED event to event store

2. STATE LOAD
   - Query event store for current state
   - Build WorkflowState for all active workflows
   - Build ProjectState (issues in progress, pending approvals, etc.)
   - Record CONTEXT_LOADED event

3. FAST-PATH CHECK
   - Is this an unambiguous command? (approve with one pending, simple status query)
   - If yes: skip LLM, produce decision directly
   - If no: proceed to LLM decision

4. LLM DECISION
   - Build prompt with: current state + input + conversation history
   - Call LLM via existing RoleBasedAgentResolver (role: 'engine_brain')
   - Parse structured response into BrainDecision
   - Record LLM_DECISION_REQUESTED and LLM_DECISION_RECEIVED events

5. ROUTE
   - answer: Send response directly to client transport
   - trigger_workflow: Enqueue to Smart Queue (Story 10.4)
   - signal_workflow: Enqueue signal to Smart Queue
   - reject: Send rejection reason to client
   - Record ACTION_DECIDED event

6. RESPOND
   - Send response/acknowledgment back to originating client
   - Record RESPONSE_SENT event
```

### Engine Brain Prompt Template

The engine brain uses a dedicated agent role (`engine_brain`) registered in the prompt registry:

```
You are the Tamma Engine Brain. Your job is to understand what the user or system
wants and decide the appropriate action.

CURRENT STATE:
{{project_state}}

ACTIVE WORKFLOWS:
{{active_workflows}}

PENDING APPROVALS:
{{pending_approvals}}

RECENT EVENTS (last 20):
{{recent_events}}

INPUT:
{{normalized_input}}

Decide ONE of:
1. ANSWER - You can respond directly (status queries, information requests)
2. TRIGGER_WORKFLOW - A new workflow should be started (specify which and with what input)
3. SIGNAL_WORKFLOW - An existing workflow needs a signal (approval, data, resume)
4. REJECT - This is invalid or duplicate (explain why)

Respond in JSON: { action, confidence, reasoning, ...action-specific fields }
```

### Relationship to Existing Code

| Current | New |
|---------|-----|
| `TammaEngine.run()` polling loop | Removed -- engine reacts to events, not polls |
| `TammaEngine.runPipeline()` 8 steps | Removed -- replaced by static workflow + Elsa workflows |
| `TammaEngine.selectIssue()` | Moved to Elsa workflow activity |
| `TammaEngine.analyzeIssue()` | Moved to Elsa workflow activity |
| `TammaEngine.generatePlan()` | Moved to Elsa workflow activity |
| `TammaEngine.awaitApproval()` | Engine brain handles approval routing |
| `TammaEngine.implementCode()` | Moved to Elsa workflow activity |
| `TammaEngine.createPR()` | Moved to Elsa workflow activity |
| `TammaEngine.monitorAndMerge()` | Moved to Elsa workflow activity |
| `EngineState` enum | Derived from event store, not stored in memory |

## Tasks / Subtasks

- [ ] Task 1: Define Engine Brain interfaces and types (AC: 1, 3)
  - [ ] Subtask 1.1: Define `EngineIntakeEvent` type with all source/channel variants
  - [ ] Subtask 1.2: Define `NormalizedInput` discriminated union for all input types
  - [ ] Subtask 1.3: Define `BrainDecision` type with all action variants
  - [ ] Subtask 1.4: Define `IEngineBrain` interface with `decide(intake, state): Promise<BrainDecision>`
  - [ ] Subtask 1.5: Define `IInputNormalizer` interface for converting raw inputs to `NormalizedInput`

- [ ] Task 2: Implement static workflow orchestrator (AC: 1, 2, 6)
  - [ ] Subtask 2.1: Create `StaticWorkflow` class implementing the 6-step sequence
  - [ ] Subtask 2.2: Wire intake -> state load -> decide -> route -> record pipeline
  - [ ] Subtask 2.3: Record events at each step (INPUT_RECEIVED, CONTEXT_LOADED, ACTION_DECIDED, RESPONSE_SENT)
  - [ ] Subtask 2.4: Handle errors at each step with ERROR_OCCURRED events
  - [ ] Subtask 2.5: Implement timeout handling for LLM decisions (configurable, default 10s)

- [ ] Task 3: Implement LLM brain integration (AC: 4, 5, 8, 10)
  - [ ] Subtask 3.1: Register `engine_brain` role in `AgentPromptRegistry` with routing prompt
  - [ ] Subtask 3.2: Implement `LLMEngineBrain` class using `RoleBasedAgentResolver`
  - [ ] Subtask 3.3: Build context assembly: project state + active workflows + recent events
  - [ ] Subtask 3.4: Parse LLM structured JSON response into `BrainDecision`
  - [ ] Subtask 3.5: Handle LLM parsing failures with fallback (re-prompt once, then reject)
  - [ ] Subtask 3.6: Configure default model as Haiku-class via engine config

- [ ] Task 4: Implement fast-path bypass for unambiguous commands (AC: 9)
  - [ ] Subtask 4.1: Define fast-path rules engine (pattern matching on normalized input + state)
  - [ ] Subtask 4.2: Implement: "approve" when exactly one pending approval -> auto-route
  - [ ] Subtask 4.3: Implement: "status" -> answer from event store directly
  - [ ] Subtask 4.4: Implement: "stop" with one active workflow -> signal cancel
  - [ ] Subtask 4.5: Record FAST_PATH_USED event when LLM is bypassed

- [ ] Task 5: Implement direct-answer capability (AC: 7)
  - [ ] Subtask 5.1: Status queries answered from event store state reconstruction
  - [ ] Subtask 5.2: History queries answered from event store with pagination
  - [ ] Subtask 5.3: Plan review answered from last PLAN_GENERATED event
  - [ ] Subtask 5.4: Cost queries answered from aggregated LLM_CALL_COMPLETED events
  - [ ] Subtask 5.5: Verify all query responses work without workflow provider

- [ ] Task 6: Implement routing to Smart Queue (AC: 5)
  - [ ] Subtask 6.1: Define `ISmartQueue` interface (implemented in Story 10.4)
  - [ ] Subtask 6.2: Route `trigger_workflow` decisions to queue with workflow name and input
  - [ ] Subtask 6.3: Route `signal_workflow` decisions to queue with instance ID and signal
  - [ ] Subtask 6.4: Handle queue-full scenarios (backpressure to client)
  - [ ] Subtask 6.5: Record INTENT_QUEUED event for every queued item

- [ ] Task 7: Refactor TammaEngine to use static workflow (AC: 1, 2)
  - [ ] Subtask 7.1: Replace `run()` polling loop with event-driven intake
  - [ ] Subtask 7.2: Remove `runPipeline()` and all 8 inline step methods
  - [ ] Subtask 7.3: Inject `IEngineBrain`, `ISmartQueue`, `IEventStore` dependencies
  - [ ] Subtask 7.4: Preserve existing transport interfaces (InProcess, Remote)
  - [ ] Subtask 7.5: Update engine API routes to use new static workflow
  - [ ] Subtask 7.6: Ensure backward compatibility for CLI and web clients

- [ ] Task 8: Testing (AC: all)
  - [ ] Subtask 8.1: Unit test static workflow with mocked brain and queue
  - [ ] Subtask 8.2: Unit test LLM brain with mocked provider
  - [ ] Subtask 8.3: Unit test fast-path rules for all unambiguous commands
  - [ ] Subtask 8.4: Integration test: user command -> decision -> event recorded
  - [ ] Subtask 8.5: Integration test: engine answers queries without workflow provider
  - [ ] Subtask 8.6: Test error handling: LLM timeout, parse failure, queue full

## Dev Notes

### Requirements Context Summary

This story is the centerpiece of Epic 10. It replaces the current imperative engine loop with an event-driven brain. The existing `TammaEngine` class (1132 lines in `packages/orchestrator/src/engine.ts`) will be substantially refactored -- the 8 pipeline steps become Elsa workflow activities (Story 10.5), and the engine becomes a thin decision-making layer.

### Project Structure Notes

- New types: `packages/shared/src/types/engine-brain.ts`
- New implementation: `packages/orchestrator/src/brain/static-workflow.ts`
- New implementation: `packages/orchestrator/src/brain/llm-engine-brain.ts`
- New implementation: `packages/orchestrator/src/brain/fast-path.ts`
- Modified: `packages/orchestrator/src/engine.ts` (major refactor)
- Modified: `packages/providers/src/agent-prompt-registry.ts` (add engine_brain role)

### References

- **Epic 10 Tech Spec:** `docs/stories/epic-10/tech-spec-epic-10.md`
- **Current Engine:** `packages/orchestrator/src/engine.ts`
- **LLM Engine:** `packages/providers/src/role-based-agent-resolver.ts`
- **Prompt Registry:** `packages/providers/src/agent-prompt-registry.ts`
- **Existing Transports:** `packages/orchestrator/src/transports/`

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-26 | 1.0 | Initial story creation | Architecture Team |
