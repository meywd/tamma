# Story 4-6: Event Replay

## Overview

Implement comprehensive event replay capabilities that enable time-travel debugging, state reconstruction at any point in time, and workflow re-execution for testing and analysis purposes.

## Acceptance Criteria

### Event Replay Engine

- [ ] Implement forward event replay from any starting point
- [ ] Create reverse event replay for undo operations
- [ ] Support selective replay based on event filters
- [ ] Add replay performance optimization with batching
- [ ] Implement replay progress tracking and resumption

### Time-Travel Debugging

- [ ] Enable state inspection at any historical point
- [ ] Create event-by-event stepping capabilities
- [ ] Implement conditional breakpoints on event patterns
- [ ] Add state diff visualization between time points
- [ ] Create replay session recording and playback

### Workflow Re-execution

- [ ] Implement workflow replay from historical events
- [ ] Create what-if scenario analysis
- [ ] Support parameter modification during replay
- [ ] Add replay result comparison with original execution
- [ ] Implement replay performance benchmarking

### Replay Safety and Isolation

- [ ] Create replay sandbox environment
- [ ] Implement replay side-effect isolation
- [ ] Add replay permission controls
- [ ] Create replay audit logging
- [ ] Implement replay resource limits

## Technical Context

### Event Replay Interface

```typescript
interface IEventReplayer {
  // Basic replay operations
  replayEvents(fromEventId: string, options?: ReplayOptions): Promise<ReplayResult>;
  replayToTimestamp(timestamp: string, options?: ReplayOptions): Promise<ReplayResult>;
  replayAggregate(
    aggregateId: string,
    fromVersion?: number,
    toVersion?: number
  ): Promise<ReplayResult>;

  // Selective replay
  replayEventsMatching(filter: EventFilter, options?: ReplayOptions): Promise<ReplayResult>;
  replayEventTypes(eventTypes: string[], options?: ReplayOptions): Promise<ReplayResult>;

  // Debugging operations
  stepThroughEvents(filter: EventFilter): AsyncIterable<ReplayStep>;
  setBreakpoint(condition: BreakpointCondition): Promise<string>;
  removeBreakpoint(breakpointId: string): Promise<void>;

  // What-if analysis
  replayWithModifications(
    modifications: ReplayModification[],
    options?: ReplayOptions
  ): Promise<ReplayResult>;
  compareReplays(original: ReplayResult, modified: ReplayResult): Promise<ReplayComparison>;

  // Session management
  createReplaySession(config: ReplaySessionConfig): Promise<ReplaySession>;
  resumeReplaySession(sessionId: string): Promise<ReplaySession>;
  saveReplaySession(sessionId: string): Promise<void>;
}

interface ReplayOptions {
  mode: 'forward' | 'reverse' | 'selective';
  batchSize?: number;
  stopOnError?: boolean;
  timeout?: number;
  dryRun?: boolean;
  isolateEffects?: boolean;
  trackState?: boolean;
  saveSnapshots?: boolean;
}

interface ReplayResult {
  sessionId: string;
  eventsProcessed: number;
  eventsSkipped: number;
  errors: ReplayError[];
  finalState: unknown;
  executionTime: number;
  snapshots: Snapshot[];
  metrics: ReplayMetrics;
}

interface ReplayStep {
  event: DomainEvent;
  stateBefore: unknown;
  stateAfter: unknown;
  executionTime: number;
  breakpoints: Breakpoint[];
  errors: ReplayError[];
}
```

### Time-Travel Debugging

```typescript
interface ITimeTravelDebugger {
  // State inspection
  getStateAt(timestamp: string, aggregateId?: string): Promise<HistoricalState>;
  getStateDiff(fromTimestamp: string, toTimestamp: string): Promise<StateDiff>;
  getEventHistory(
    aggregateId: string,
    fromTimestamp?: string,
    toTimestamp?: string
  ): Promise<DomainEvent[]>;

  // Debugging controls
  startDebugging(config: DebugConfig): Promise<DebugSession>;
  stepForward(sessionId: string): Promise<DebugStep>;
  stepBackward(sessionId: string): Promise<DebugStep>;
  continueToNextBreakpoint(sessionId: string): Promise<DebugStep>;

  // Breakpoint management
  setEventBreakpoint(eventType: string, condition?: string): Promise<string>;
  setStateBreakpoint(aggregateId: string, condition: string): Promise<string>;
  setTimeBreakpoint(timestamp: string): Promise<string>;

  // Visualization
  generateStateTimeline(aggregateId: string): Promise<StateTimeline>;
  generateEventFlowDiagram(filter: EventFilter): Promise<EventFlowDiagram>;
}

interface HistoricalState {
  timestamp: string;
  aggregateId: string;
  version: number;
  state: unknown;
  eventsToThisPoint: number;
  lastEvent: DomainEvent;
}

interface StateDiff {
  fromTimestamp: string;
  toTimestamp: string;
  changes: StateChange[];
  summary: DiffSummary;
}

interface StateChange {
  path: string;
  oldValue: unknown;
  newValue: unknown;
  changeType: 'added' | 'modified' | 'deleted';
  eventId: string;
}
```

### Replay Sandbox

```typescript
interface IReplaySandbox {
  // Sandbox management
  createSandbox(config: SandboxConfig): Promise<Sandbox>;
  destroySandbox(sandboxId: string): Promise<void>;

  // Isolation controls
  isolateDatabase(sandboxId: string): Promise<DatabaseIsolation>;
  isolateFileSystem(sandboxId: string): Promise<FileSystemIsolation>;
  isolateNetwork(sandboxId: string): Promise<NetworkIsolation>;

  // Resource limits
  setResourceLimits(sandboxId: string, limits: ResourceLimits): Promise<void>;
  getResourceUsage(sandboxId: string): Promise<ResourceUsage>;

  // Side-effect capture
  captureSideEffects(sandboxId: string): Promise<SideEffectCapture>;
  replaySideEffects(sandboxId: string, effects: SideEffectCapture): Promise<void>;
}

interface SandboxConfig {
  name: string;
  isolationLevel: 'full' | 'partial' | 'none';
  resourceLimits: ResourceLimits;
  persistenceMode: 'memory' | 'temporary' | 'persistent';
  networkAccess: boolean;
  fileSystemAccess: boolean;
}

interface ResourceLimits {
  maxMemory: number; // bytes
  maxCpuTime: number; // milliseconds
  maxDiskSpace: number; // bytes
  maxNetworkRequests: number;
  maxExecutionTime: number; // milliseconds
}
```

### Database Schema for Replay

```sql
-- Replay sessions table
CREATE TABLE replay_sessions (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name VARCHAR(255) NOT NULL,
  config JSONB NOT NULL DEFAULT '{}',
  status VARCHAR(50) NOT NULL DEFAULT 'created',
  created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
  started_at TIMESTAMP WITH TIME ZONE,
  completed_at TIMESTAMP WITH TIME ZONE,
  created_by VARCHAR(255)
);

-- Replay checkpoints table
CREATE TABLE replay_checkpoints (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  session_id UUID NOT NULL REFERENCES replay_sessions(id),
  event_id VARCHAR(36) NOT NULL,
  timestamp TIMESTAMP WITH TIME ZONE NOT NULL,
  state JSONB NOT NULL,
  created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),

  UNIQUE(session_id, event_id)
);

-- Replay breakpoints table
CREATE TABLE replay_breakpoints (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  session_id UUID NOT NULL REFERENCES replay_sessions(id),
  condition JSONB NOT NULL,
  enabled BOOLEAN NOT NULL DEFAULT true,
  hit_count INTEGER NOT NULL DEFAULT 0,
  created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
);

-- Replay audit log table
CREATE TABLE replay_audit_log (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  session_id UUID REFERENCES replay_sessions(id),
  event_id VARCHAR(36),
  action VARCHAR(100) NOT NULL,
  details JSONB NOT NULL DEFAULT '{}',
  timestamp TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
  user_id VARCHAR(255)
);
```

## Implementation Tasks

### 1. Core Replay Engine

- [ ] Create `packages/events/src/replay/event-replayer.ts`
- [ ] Implement forward and reverse replay logic
- [ ] Add selective replay capabilities
- [ ] Create replay performance optimization

### 2. Time-Travel Debugger

- [ ] Create `packages/events/src/replay/time-travel-debugger.ts`
- [ ] Implement state inspection and diffing
- [ ] Add breakpoint management system
- [ ] Create visualization components

### 3. Replay Sandbox

- [ ] Create `packages/events/src/replay/replay-sandbox.ts`
- [ ] Implement isolation mechanisms
- [ ] Add resource limit enforcement
- [ ] Create side-effect capture system

### 4. Session Management

- [ ] Create `packages/events/src/replay/session-manager.ts`
- [ ] Implement session lifecycle management
- [ ] Add session persistence and recovery
- [ ] Create session collaboration features

### 5. What-If Analysis

- [ ] Create `packages/events/src/replay/what-if-analyzer.ts`
- [ ] Implement modification system
- [ ] Add result comparison capabilities
- [ ] Create scenario management

### 6. Performance Optimization

- [ ] Implement replay batching and parallelization
- [ ] Add intelligent caching for replay states
- [ ] Create replay performance monitoring
- [ ] Optimize memory usage for large replays

### 7. Testing

- [ ] Unit tests for replay operations
- [ ] Integration tests with sandbox isolation
- [ ] Performance tests for large-scale replays
- [ ] Security tests for isolation guarantees

## Dependencies

### Internal Dependencies

- `@tamma/events` - Event store interface
- Event schema from Story 4-1
- Event store implementation from Story 4-2
- Snapshot management from Story 4-4

### External Dependencies

- `vm2` - Sandbox isolation
- `node-cron` - Session cleanup
- `bull` - Replay job queue

## Success Metrics

- Replay performance: >1000 events/second for forward replay
- Memory efficiency: <1GB for replaying 1M events
- Isolation guarantee: 100% sandbox isolation
- Debugging responsiveness: <100ms for state inspection
- Session reliability: 99.9% session recovery success

## Risks and Mitigations

### Performance Issues

- **Risk**: Large replays may consume excessive resources
- **Mitigation**: Implement batching, resource limits, and intelligent caching

### Isolation Breaches

- **Risk**: Sandbox isolation may be incomplete
- **Mitigation**: Use multiple isolation layers, regular security audits

### State Consistency

- **Risk**: Replay state may diverge from original execution
- **Mitigation**: Implement deterministic replay, state validation

## Notes

Event replay is a critical capability for debugging autonomous development workflows and understanding system behavior. The replay system must provide both high performance for large-scale replays and fine-grained control for detailed debugging scenarios.

The time-travel debugging capabilities enable developers to understand exactly what happened at any point in the workflow, which is essential for maintaining and improving autonomous development systems.
