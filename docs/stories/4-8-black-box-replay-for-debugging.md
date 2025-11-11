# Story 4.8: Black-Box Replay for Debugging

Status: ready-for-dev

## Story

As a **developer**,
I want to replay system state at any point in time to understand past behavior,
so that I can diagnose why autonomous loop made specific decisions.

## Acceptance Criteria

1. CLI command: `tamma replay --correlation-id {id} --timestamp {timestamp}`
2. Command reconstructs system state by replaying events up to specified timestamp
3. Command displays: issue context, AI provider decisions, code changes, approval points, errors
4. Command supports step-by-step replay (pause at each event) via `--interactive` flag
5. Command exports replay to HTML report for sharing with team
6. Replay includes diff view showing state changes between events
7. Replay performance: complete reconstruction in <5 seconds for typical development cycle (50-100 events)

## Tasks / Subtasks

- [ ] Task 1: Design replay CLI interface and commands (AC: 1)
  - [ ] Subtask 1.1: Define CLI command structure and parameters
  - [ ] Subtask 1.2: Create command-line argument parsing
  - [ ] Subtask 1.3: Add help documentation and examples
  - [ ] Subtask 1.4: Implement command validation and error handling
  - [ ] Subtask 1.5: Add CLI command integration tests

- [ ] Task 2: Implement event replay engine (AC: 2)
  - [ ] Subtask 2.1: Create event replay service and state reconstruction
  - [ ] Subtask 2.2: Implement event ordering and validation
  - [ ] Subtask 2.3: Add state snapshot management for performance
  - [ ] Subtask 2.4: Create replay context and tracking
  - [ ] Subtask 2.5: Add replay error handling and recovery

- [ ] Task 3: Create replay display and visualization (AC: 3)
  - [ ] Subtask 3.1: Design replay output format and structure
  - [ ] Subtask 3.2: Implement issue context display
  - [ ] Subtask 3.3: Add AI provider decision visualization
  - [ ] Subtask 3.4: Create code change and diff display
  - [ ] Subtask 3.5: Add approval and error event display

- [ ] Task 4: Implement interactive replay mode (AC: 4)
  - [ ] Subtask 4.1: Create interactive replay controller
  - [ ] Subtask 4.2: Add pause, resume, and step controls
  - [ ] Subtask 4.3: Implement event navigation and jumping
  - [ ] Subtask 4.4: Add interactive state inspection
  - [ ] Subtask 4.5: Create interactive help and commands

- [ ] Task 5: Implement HTML report export (AC: 5)
  - [ ] Subtask 5.1: Design HTML report template and styling
  - [ ] Subtask 5.2: Create report generation service
  - [ ] Subtask 5.3: Add interactive timeline and navigation
  - [ ] Subtask 5.4: Implement collapsible event details
  - [ ] Subtask 5.5: Add report sharing and distribution

- [ ] Task 6: Create diff view and state comparison (AC: 6)
  - [ ] Subtask 6.1: Implement state diff calculation
  - [ ] Subtask 6.2: Create visual diff display
  - [ ] Subtask 6.3: Add side-by-side state comparison
  - [ ] Subtask 6.4: Implement state change highlighting
  - [ ] Subtask 6.5: Add diff export and sharing

- [ ] Task 7: Optimize replay performance (AC: 7)
  - [ ] Subtask 7.1: Implement event streaming and lazy loading
  - [ ] Subtask 7.2: Add replay caching and memoization
  - [ ] Subtask 7.3: Optimize state reconstruction algorithms
  - [ ] Subtask 7.4: Add parallel processing for independent events
  - [ ] Subtask 7.5: Create performance monitoring and metrics

## Dev Notes

### Requirements Context Summary

**Epic 4 Integration:** This story provides the debugging interface for the event sourcing system, enabling developers to understand and debug autonomous system behavior by replaying past events.

**Debugging Requirements:** The replay system must provide comprehensive visibility into autonomous decision-making processes, including AI reasoning, code changes, and human interactions. This is essential for maintaining and improving autonomous systems.

**Performance Requirements:** Replay must be fast enough for interactive use while handling complex workflows with hundreds of events. This requires efficient state reconstruction and smart caching.

### Implementation Guidance

**CLI Interface Design:**

```typescript
interface ReplayCommand {
  // Basic replay options
  correlationId: string; // Workflow correlation ID to replay
  timestamp?: string; // Point in time to replay to
  eventId?: string; // Specific event to replay to

  // Display options
  format: 'text' | 'json' | 'html'; // Output format
  verbose: boolean; // Detailed output
  quiet: boolean; // Minimal output

  // Interactive options
  interactive: boolean; // Interactive step-by-step mode
  speed: number; // Replay speed (events per second)
  pauseOnError: boolean; // Pause on errors

  // Filtering options
  eventTypes?: string[]; // Event types to include
  excludeTypes?: string[]; // Event types to exclude
  actorFilter?: string; // Filter by actor

  // Export options
  output?: string; // Output file path
  exportFormat?: 'html' | 'json' | 'csv';
  includeDiffs: boolean; // Include state diffs
  includeSnapshots: boolean; // Include state snapshots
}

// CLI command structure
// tamma replay --correlation-id <id> [options]
// tamma replay --timestamp <iso-timestamp> [options]
// tamma replay --event-id <id> [options]
```

**Replay Engine Implementation:**

```typescript
@Injectable()
export class ReplayEngine {
  constructor(
    private eventStore: IEventStore,
    private stateReconstructor: StateReconstructor,
    private diffCalculator: DiffCalculator,
    private reportGenerator: ReportGenerator
  ) {}

  async replay(options: ReplayCommand): Promise<ReplayResult> {
    // Validate options
    await this.validateReplayOptions(options);

    // Get events for replay
    const events = await this.getReplayEvents(options);

    // Create replay context
    const context = this.createReplayContext(options, events);

    // Execute replay
    if (options.interactive) {
      return await this.interactiveReplay(context);
    } else {
      return await this.batchReplay(context);
    }
  }

  private async getReplayEvents(options: ReplayCommand): Promise<BaseEvent[]> {
    let events: BaseEvent[];

    if (options.correlationId) {
      events = await this.eventStore.getEventsByCorrelation(options.correlationId);
    } else if (options.timestamp) {
      events = await this.eventStore.getEventsUntil(options.timestamp);
    } else if (options.eventId) {
      events = await this.eventStore.getEventsUpToEvent(options.eventId);
    } else {
      throw new Error('Must specify correlation-id, timestamp, or event-id');
    }

    // Apply filters
    events = this.applyEventFilters(events, options);

    // Sort by timestamp
    events.sort((a, b) => new Date(a.timestamp).getTime() - new Date(b.timestamp).getTime());

    return events;
  }

  private createReplayContext(options: ReplayCommand, events: BaseEvent[]): ReplayContext {
    return {
      options,
      events,
      currentIndex: 0,
      currentState: {},
      stateHistory: [],
      snapshots: new Map(),
      startTime: Date.now(),
      paused: false,
      speed: options.speed || 1,
      errors: [],
    };
  }

  private async interactiveReplay(context: ReplayContext): Promise<ReplayResult> {
    const readline = require('readline');
    const rl = readline.createInterface({
      input: process.stdin,
      output: process.stdout,
    });

    console.log('\n🎬 Interactive Replay Mode');
    console.log('Commands: [n]ext, [p]ause, [r]esume, [s]tep <n>, [j]ump <n>, [q]uit, [h]elp\n');

    while (context.currentIndex < context.events.length) {
      if (!context.paused) {
        await this.processNextEvent(context);
        this.displayCurrentState(context);
      }

      const command = await this.promptForCommand(rl);
      await this.handleInteractiveCommand(command, context, rl);
    }

    rl.close();
    return this.generateReplayResult(context);
  }

  private async batchReplay(context: ReplayContext): Promise<ReplayResult> {
    console.log(`\n🔄 Replaying ${context.events.length} events...\n`);

    const progressBar = new ProgressBar('Replaying [:bar] :percent :etas', {
      total: context.events.length,
      width: 40,
      complete: '=',
      incomplete: ' ',
    });

    for (let i = 0; i < context.events.length; i++) {
      context.currentIndex = i;
      await this.processNextEvent(context);

      if (!context.options.quiet) {
        progressBar.tick();
      }

      // Add delay for replay speed
      if (context.options.speed && context.options.speed < 10) {
        await this.sleep(1000 / context.options.speed);
      }
    }

    progressBar.complete();
    console.log('\n✅ Replay completed\n');

    if (!context.options.quiet) {
      this.displayFinalState(context);
    }

    return this.generateReplayResult(context);
  }

  private async processNextEvent(context: ReplayContext): Promise<void> {
    const event = context.events[context.currentIndex];
    const previousState = { ...context.currentState };

    try {
      // Apply event to current state
      context.currentState = await this.stateReconstructor.applyEvent(context.currentState, event);

      // Calculate diff
      const diff = this.diffCalculator.calculate(previousState, context.currentState);

      // Store state history
      context.stateHistory.push({
        index: context.currentIndex,
        event,
        previousState,
        newState: context.currentState,
        diff,
        timestamp: Date.now(),
      });

      // Create snapshot for performance
      if (context.currentIndex % 10 === 0) {
        context.snapshots.set(context.currentIndex, { ...context.currentState });
      }
    } catch (error) {
      context.errors.push({
        eventIndex: context.currentIndex,
        eventId: event.eventId,
        error: error.message,
        timestamp: new Date().toISOString(),
      });

      if (context.options.pauseOnError) {
        context.paused = true;
        console.log(`\n❌ Error processing event ${event.eventId}: ${error.message}`);
        console.log('Replay paused. Use [r]esume to continue or [q]uit to exit.\n');
      }
    }
  }

  private displayCurrentState(context: ReplayContext): void {
    const event = context.events[context.currentIndex];
    const stateEntry = context.stateHistory[context.currentIndex];

    console.log(`\n📍 Event ${context.currentIndex + 1}/${context.events.length}`);
    console.log(`🕒 ${event.timestamp}`);
    console.log(`📝 ${event.eventType}`);
    console.log(`👤 ${event.actorType}:${event.actorId}`);

    if (context.options.verbose) {
      console.log(`\n📄 Event Details:`);
      console.log(JSON.stringify(event.payload, null, 2));
    }

    // Display state changes
    if (stateEntry && stateEntry.diff && Object.keys(stateEntry.diff).length > 0) {
      console.log(`\n🔄 State Changes:`);
      this.displayDiff(stateEntry.diff);
    }

    // Display current state summary
    console.log(`\n📊 Current State:`);
    this.displayStateSummary(context.currentState);

    console.log('─'.repeat(80));
  }

  private displayDiff(diff: StateDiff): void {
    Object.entries(diff).forEach(([key, change]) => {
      switch (change.type) {
        case 'added':
          console.log(`  ✅ ${key}: ${JSON.stringify(change.value)}`);
          break;
        case 'modified':
          console.log(
            `  🔄 ${key}: ${JSON.stringify(change.oldValue)} → ${JSON.stringify(change.newValue)}`
          );
          break;
        case 'deleted':
          console.log(`  ❌ ${key}: ${JSON.stringify(change.oldValue)}`);
          break;
      }
    });
  }

  private displayStateSummary(state: any): void {
    // Issue context
    if (state.issue) {
      console.log(`  🎯 Issue: ${state.issue.title} (#${state.issue.number})`);
    }

    // AI decisions
    if (state.aiDecisions && state.aiDecisions.length > 0) {
      console.log(`  🤖 AI Decisions: ${state.aiDecisions.length} made`);
      state.aiDecisions.forEach((decision: any, index: number) => {
        console.log(`    ${index + 1}. ${decision.provider} - ${decision.action}`);
      });
    }

    // Code changes
    if (state.codeChanges) {
      console.log(
        `  📝 Code Changes: ${state.codeChanges.filesAdded + state.codeChanges.filesModified} files`
      );
    }

    // Approvals
    if (state.approvals) {
      console.log(`  ✅ Approvals: ${state.approvals.length} requested`);
    }

    // Errors
    if (state.errors && state.errors.length > 0) {
      console.log(`  ❌ Errors: ${state.errors.length} encountered`);
    }
  }

  private async handleInteractiveCommand(
    command: string,
    context: ReplayContext,
    rl: readline.Interface
  ): Promise<void> {
    const [cmd, ...args] = command.toLowerCase().trim().split(' ');

    switch (cmd) {
      case 'n':
      case 'next':
        context.paused = false;
        break;

      case 'p':
      case 'pause':
        context.paused = true;
        console.log('⏸️  Replay paused');
        break;

      case 'r':
      case 'resume':
        context.paused = false;
        console.log('▶️  Replay resumed');
        break;

      case 's':
      case 'step':
        const steps = parseInt(args[0]) || 1;
        for (let i = 0; i < steps && context.currentIndex < context.events.length; i++) {
          await this.processNextEvent(context);
          this.displayCurrentState(context);
        }
        break;

      case 'j':
      case 'jump':
        const targetIndex = parseInt(args[0]);
        if (targetIndex >= 0 && targetIndex < context.events.length) {
          context.currentIndex = targetIndex;
          // Restore state from snapshot or recalculate
          await this.restoreStateAtIndex(context, targetIndex);
          this.displayCurrentState(context);
        } else {
          console.log(`❌ Invalid event index: ${targetIndex}`);
        }
        break;

      case 'd':
      case 'diff':
        const fromIndex = parseInt(args[0]) || 0;
        const toIndex = parseInt(args[1]) || context.currentIndex;
        await this.showStateDiff(context, fromIndex, toIndex);
        break;

      case 'e':
      case 'export':
        const format = args[0] || 'html';
        await this.exportReplay(context, format);
        break;

      case 'h':
      case 'help':
        this.showInteractiveHelp();
        break;

      case 'q':
      case 'quit':
        rl.close();
        process.exit(0);
        break;

      default:
        console.log(`❌ Unknown command: ${cmd}. Type [h]elp for available commands.`);
    }
  }

  private async exportReplay(context: ReplayContext, format: string): Promise<void> {
    console.log(`📤 Exporting replay as ${format.toUpperCase()}...`);

    const report = await this.reportGenerator.generateReport(context, format);
    const filename = `replay-${context.options.correlationId}-${Date.now()}.${format}`;

    await fs.writeFile(filename, report);
    console.log(`✅ Replay exported to: ${filename}`);
  }
}
```

**HTML Report Generation:**

```typescript
@Injectable()
export class ReportGenerator {
  async generateReport(context: ReplayContext, format: string): Promise<string> {
    switch (format) {
      case 'html':
        return this.generateHTMLReport(context);
      case 'json':
        return this.generateJSONReport(context);
      case 'csv':
        return this.generateCSVReport(context);
      default:
        throw new Error(`Unsupported export format: ${format}`);
    }
  }

  private generateHTMLReport(context: ReplayContext): Promise<string> {
    const template = `
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Tamma Replay Report - ${context.options.correlationId}</title>
    <style>
        body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; margin: 0; padding: 20px; background: #f5f5f5; }
        .container { max-width: 1200px; margin: 0 auto; background: white; border-radius: 8px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); }
        .header { padding: 20px; border-bottom: 1px solid #e0e0e0; background: #fafafa; }
        .timeline { padding: 20px; }
        .event { margin-bottom: 20px; border-left: 3px solid #2196F3; padding-left: 20px; }
        .event-header { display: flex; align-items: center; margin-bottom: 10px; }
        .event-time { color: #666; font-size: 14px; margin-right: 15px; }
        .event-type { background: #2196F3; color: white; padding: 4px 8px; border-radius: 4px; font-size: 12px; font-weight: bold; }
        .event-actor { color: #666; margin-left: 10px; }
        .event-content { background: #f8f9fa; padding: 15px; border-radius: 4px; font-family: 'Monaco', 'Menlo', monospace; font-size: 12px; }
        .diff { background: #fff3cd; border: 1px solid #ffeaa7; padding: 10px; border-radius: 4px; margin-top: 10px; }
        .diff-added { color: #28a745; }
        .diff-removed { color: #dc3545; }
        .state-summary { background: #e3f2fd; padding: 15px; border-radius: 4px; margin-top: 10px; }
        .navigation { position: fixed; top: 20px; right: 20px; background: white; padding: 10px; border-radius: 4px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); }
        .nav-button { background: #2196F3; color: white; border: none; padding: 8px 12px; margin: 2px; border-radius: 4px; cursor: pointer; }
        .nav-button:hover { background: #1976D2; }
        .nav-button:disabled { background: #ccc; cursor: not-allowed; }
    </style>
</head>
<body>
    <div class="container">
        <div class="header">
            <h1>🔄 Tamma Replay Report</h1>
            <p><strong>Correlation ID:</strong> ${context.options.correlationId}</p>
            <p><strong>Events:</strong> ${context.events.length}</p>
            <p><strong>Duration:</strong> ${
              context.events.length > 0
                ? this.formatDuration(
                    new Date(context.events[0].timestamp),
                    new Date(context.events[context.events.length - 1].timestamp)
                  )
                : 'N/A'
            }</p>
            <p><strong>Generated:</strong> ${new Date().toISOString()}</p>
        </div>
        
        <div class="navigation">
            <button class="nav-button" onclick="previousEvent()">⬅️ Previous</button>
            <button class="nav-button" onclick="nextEvent()">Next ➡️</button>
            <button class="nav-button" onclick="toggleDiff()">🔄 Toggle Diff</button>
        </div>
        
        <div class="timeline">
            ${context.stateHistory.map((entry, index) => this.generateEventHTML(entry, index)).join('')}
        </div>
    </div>
    
    <script>
        let currentEvent = 0;
        const totalEvents = ${context.stateHistory.length};
        
        function showEvent(index) {
            // Hide all events
            document.querySelectorAll('.event').forEach(el => el.style.display = 'none');
            // Show selected event
            document.querySelectorAll('.event')[index].style.display = 'block';
            currentEvent = index;
            updateNavigation();
        }
        
        function nextEvent() {
            if (currentEvent < totalEvents - 1) {
                showEvent(currentEvent + 1);
            }
        }
        
        function previousEvent() {
            if (currentEvent > 0) {
                showEvent(currentEvent - 1);
            }
        }
        
        function updateNavigation() {
            document.querySelector('.nav-button').disabled = currentEvent >= totalEvents - 1;
            document.querySelectorAll('.nav-button')[1].disabled = currentEvent <= 0;
        }
        
        function toggleDiff() {
            document.querySelectorAll('.diff').forEach(el => {
                el.style.display = el.style.display === 'none' ? 'block' : 'none';
            });
        }
        
        // Initialize
        showEvent(0);
        
        // Keyboard shortcuts
        document.addEventListener('keydown', (e) => {
            if (e.key === 'ArrowRight') nextEvent();
            if (e.key === 'ArrowLeft') previousEvent();
            if (e.key === 'd') toggleDiff();
        });
    </script>
</body>
</html>`;

    return template;
  }

  private generateEventHTML(entry: StateHistoryEntry, index: number): string {
    const event = entry.event;
    const diff = entry.diff;

    return `
        <div class="event" style="display: ${index === 0 ? 'block' : 'none'};" data-event-index="${index}">
            <div class="event-header">
                <span class="event-time">${new Date(event.timestamp).toLocaleString()}</span>
                <span class="event-type">${event.eventType}</span>
                <span class="event-actor">${event.actorType}:${event.actorId}</span>
            </div>
            <div class="event-content">
                <pre>${JSON.stringify(event.payload, null, 2)}</pre>
            </div>
            ${diff && Object.keys(diff).length > 0 ? this.generateDiffHTML(diff) : ''}
            <div class="state-summary">
                ${this.generateStateSummaryHTML(entry.newState)}
            </div>
        </div>`;
  }

  private generateDiffHTML(diff: StateDiff): string {
    return `
        <div class="diff">
            <h4>🔄 State Changes</h4>
            ${Object.entries(diff)
              .map(([key, change]) => {
                switch (change.type) {
                  case 'added':
                    return `<div class="diff-added">✅ ${key}: ${JSON.stringify(change.value)}</div>`;
                  case 'modified':
                    return `<div>🔄 ${key}: <span class="diff-removed">${JSON.stringify(change.oldValue)}</span> → <span class="diff-added">${JSON.stringify(change.newValue)}</span></div>`;
                  case 'deleted':
                    return `<div class="diff-removed">❌ ${key}: ${JSON.stringify(change.oldValue)}</div>`;
                  default:
                    return '';
                }
              })
              .join('')}
        </div>`;
  }
}
```

### Technical Specifications

**Performance Requirements:**

- Replay initialization: <1 second
- Event processing: <50ms per event
- State reconstruction: <5 seconds for 100 events
- HTML report generation: <10 seconds for typical replay

**Memory Requirements:**

- State storage: <100MB for typical workflows
- Event caching: <50MB for replay session
- Report generation: <20MB for HTML export
- CLI interface: <10MB additional memory

**Usability Requirements:**

- Interactive response time: <200ms for commands
- Navigation responsiveness: <100ms for event jumping
- Report loading: <3 seconds for HTML reports
- Help documentation: Complete and accessible

**Export Requirements:**

- HTML reports: Self-contained, interactive, shareable
- JSON exports: Complete replay data, machine-readable
- CSV exports: Event summary, spreadsheet-compatible
- Report size: <10MB for typical workflows

### Dependencies

**Internal Dependencies:**

- Story 4.1: Event schema design (provides event structures)
- Story 4.2: Event store backend selection (provides event access)
- Story 4.7: Event query API (provides event retrieval)
- All event capture stories (4.3-4.6) for complete event data

**External Dependencies:**

- CLI framework (Commander.js or similar)
- Terminal UI library (Ink or similar)
- HTML template engine
- File system utilities

### Risks and Mitigations

| Risk                            | Severity | Mitigation                               |
| ------------------------------- | -------- | ---------------------------------------- |
| Large replay performance issues | Medium   | Event streaming, lazy loading, snapshots |
| State reconstruction errors     | High     | Event validation, error recovery         |
| Memory usage for large replays  | Medium   | Streaming processing, garbage collection |
| Complex state diff calculation  | Low      | Efficient diff algorithms, caching       |

### Success Metrics

- [ ] Replay performance: <5 seconds for 100 events
- [ ] Interactive responsiveness: <200ms command response
- [ ] Report generation: <10 seconds for HTML export
- [ ] Memory efficiency: <100MB for typical workflows
- [ ] User satisfaction: >90% positive feedback

## Related

- Related story: `docs/stories/4-1-event-schema-design.md`
- Related story: `docs/stories/4-2-event-store-backend-selection.md`
- Related story: `docs/stories/4-7-event-query-api-for-time-travel.md`
- Technical specification: `docs/tech-spec-epic-4.md`
- CLI documentation: `docs/5-9b-usage-configuration-documentation.md`

## References

- [Interactive CLI Design Patterns](https://clig.dev/)
- [State Machine Replay Patterns](https://statecharts.dev/)
- [HTML Report Generation Best Practices](https://web.dev/reporting-api/)
- [Event Sourcing Debugging](https://eventstore.com/docs/debugging/)
