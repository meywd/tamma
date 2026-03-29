# Task 2: Wire IntentClassifier into VoiceSession + Engine Command Dispatch

**Story:** 24-4-intent-engine - Intent Classification + Engine Integration
**Epic:** 24

## Task Description

Wire the `IntentClassifier` into the `VoiceSession` so that final transcripts are classified before processing. Engine commands are dispatched to the `VoiceEngineTransport`, questions are answered from engine state, and conversational feedback is processed through the LLM with context.

## Acceptance Criteria

- Final transcripts routed through `IntentClassifier` before LLM response
- Engine commands dispatched to `VoiceEngineTransport.sendCommand()`
- Command result spoken back to user ("Starting engine", "Plan approved", etc.)
- Questions answered by reading engine state via transport (no command dispatched)
- Conversational feedback with `impliesRejection: true` routed as reject command with feedback text
- Approval flow: engine emits `approval.request`, user says "approve"/"reject", routed through classifier to transport
- Multi-turn context maintained: classifier sees previous turns for disambiguation
- Hybrid mode: typed text also goes through classifier
- Unit tests for the routing logic

## Implementation Details

### Technical Requirements

- [ ] Modify `packages/voice/src/voice-session.ts` to add intent classification:

```typescript
import { IntentClassifier, type ClassifiedIntent } from './intent-classifier.js';

// In VoiceSession constructor, add:
private readonly intentClassifier: IntentClassifier;

// In VoiceSessionDeps, add:
export interface VoiceSessionDeps {
  llmProvider: ILLMProvider;
  sttAdapter?: ISTTAdapter;
  ttsAdapter?: ITTSAdapter;
  userId: string;
  sessionId: string;
  transport?: VoiceEngineTransport;  // NEW: for engine command dispatch
}

// Replace handleTextInput with intent-aware version:
async handleTextInput(text: string, source: 'voice' | 'text'): Promise<void> {
  this.context.addTurn({ role: 'user', content: text, source });
  this.setState('processing');

  // Step 1: Classify intent
  const engineState = this.transport
    ? { state: 'unknown', hasActivePlan: this.pendingApproval !== null }
    : undefined;
  const intent = await this.intentClassifier.classify(text, this.context, engineState);

  // Step 2: Route based on intent
  switch (intent.type) {
    case 'engine-command':
      await this.handleEngineCommand(intent);
      break;
    case 'question':
      await this.handleQuestion(intent);
      break;
    case 'conversation':
      if (intent.impliesRejection && this.pendingApproval) {
        // Route as rejection with feedback
        await this.handleEngineCommand({
          type: 'engine-command',
          command: { type: 'reject', feedback: intent.feedback },
          confidence: intent.confidence,
        });
      } else {
        await this.handleConversation(intent);
      }
      break;
  }

  this.setState('idle');
}

private async handleEngineCommand(intent: EngineCommandIntent): Promise<void> {
  if (!this.transport) {
    // No engine transport -- respond with text explaining
    await this.respondWithText('Engine is not connected. I can only have a conversation.');
    return;
  }

  const result = await this.transport.sendCommand(intent.command);

  // Generate spoken confirmation
  const confirmation = this.getCommandConfirmation(intent.command, result);
  await this.respondWithText(confirmation);
}

private getCommandConfirmation(command: EngineCommand, result: CommandResult): string {
  if (!result.ok) return `I couldn't do that: ${result.error ?? 'unknown error'}`;

  switch (command.type) {
    case 'start': return 'Starting the engine now.';
    case 'stop': return 'Engine stopped.';
    case 'approve': return 'Plan approved. I\'ll start implementing.';
    case 'reject': return `Plan rejected${command.feedback ? ` because: ${command.feedback}` : ''}. I\'ll generate a new plan.`;
    case 'skip': return 'Skipping this issue.';
    case 'pause': return 'Engine paused.';
    case 'resume': return 'Resuming the engine.';
    case 'process-issue': return `Processing issue ${(command as { issueNumber: number }).issueNumber}.`;
    default: return 'Done.';
  }
}

private async handleQuestion(intent: QuestionIntent): Promise<void> {
  // Build context about current engine state
  // Use LLM to generate a natural language answer
  const stateContext = this.buildEngineStateContext();

  const messages = this.context.toMessages();
  messages.push({
    role: 'system',
    content: `The user is asking about the current state. Here is the engine context:\n${stateContext}\n\nAnswer concisely.`,
  });

  const response = await this.llm.complete({ messages, maxTokens: 300 });
  await this.respondWithText(response.content);
}

private async handleConversation(intent: ConversationIntent): Promise<void> {
  // Standard LLM conversation response
  const messages = this.context.toMessages();
  await this.respondWithText(messages);
}

/**
 * Respond with text: send as response.text and optionally speak via TTS.
 */
private async respondWithText(text: string | Message[]): Promise<void> {
  let responseText: string;

  if (typeof text === 'string') {
    responseText = text;
  } else {
    // Full LLM call with conversation messages
    const response = await this.llm.complete({ messages: text });
    responseText = response.content;
  }

  this.context.addTurn({ role: 'assistant', content: responseText, source: 'voice' });

  if (this.tts) {
    // Stream TTS (reuse existing sentence-split + TTS pipeline)
    await this.streamTTSResponse(responseText);
  } else {
    this.send({ type: 'response.text', text: responseText, isFinal: true });
  }
}
```

- [ ] Wire approval request handling:

```typescript
// When VoiceEngineTransport receives approval request:
// The transport calls session.handleApprovalRequest(plan)

async handleApprovalRequest(plan: DevelopmentPlan): Promise<void> {
  this.pendingApproval = plan;

  // Send engine.approval message to client
  this.send({ type: 'engine.approval', plan });

  // Speak the approval prompt
  const summary = `I've generated a development plan for this issue. ` +
    `It has ${plan.steps?.length ?? 0} steps. ` +
    `Say "approve" to proceed, "reject" with feedback to revise, or "skip" to move on.`;
  await this.respondWithText(summary);
}
```

### Files to Modify/Create

- MODIFY `packages/voice/src/voice-session.ts` -- add intent routing, engine command dispatch, approval flow
- CREATE `packages/voice/src/voice-session-intent.test.ts`
- MODIFY `packages/orchestrator/src/transports/voice.ts` -- wire approval requests to session

### Dependencies

- [ ] Task 1: IntentClassifier
- [ ] Story 24-1 Task 3: VoiceEngineTransport
- [ ] Story 24-3 Task 3: TTS streaming (for spoken confirmations)
- [ ] `EngineCommand`, `CommandResult` from `@tamma/shared/contracts`

## Testing Strategy

### Unit Tests -- voice-session-intent.test.ts

- [ ] Test "start the engine" -> engine-command -> transport.sendCommand({ type: 'start' })
- [ ] Test "approve" -> engine-command -> transport.sendCommand({ type: 'approve' })
- [ ] Test "reject, the tests are wrong" -> engine-command -> transport.sendCommand({ type: 'reject', feedback: '...' })
- [ ] Test "skip" -> engine-command -> transport.sendCommand({ type: 'skip' })
- [ ] Test "what's the status?" -> question -> LLM response with engine state
- [ ] Test "that won't work because..." with pending approval -> reject with feedback
- [ ] Test "that won't work because..." without pending approval -> conversation
- [ ] Test "good job" -> conversation -> LLM response
- [ ] Test engine command confirmation spoken to user
- [ ] Test engine command failure message spoken to user
- [ ] Test no transport -> conversation-only mode
- [ ] Test approval request triggers spoken prompt
- [ ] Test approval request followed by "approve" -> transport.sendCommand('approve')
- [ ] Test typed text.input also goes through classifier
- [ ] Test multi-turn disambiguation: "yes" after approval prompt -> approve
- [ ] Test multi-turn disambiguation: "no" after approval prompt -> reject

### Validation Steps

1. [ ] Wire IntentClassifier into VoiceSession.handleTextInput
2. [ ] Implement engine command dispatch with confirmations
3. [ ] Implement question answering from engine state
4. [ ] Implement conversational feedback with rejection detection
5. [ ] Wire approval request -> spoken prompt
6. [ ] Test all intent routing paths
7. [ ] Run all unit tests
8. [ ] Verify TypeScript strict mode compilation

## Notes & Considerations

- The classifier adds ~300ms to the processing pipeline (before the LLM response). This is acceptable because it prevents misrouted actions.
- Multi-turn disambiguation is important: "yes" means different things depending on context. If there's a pending approval, "yes" = approve. Otherwise, it's conversational. The classifier uses conversation context to disambiguate.
- The `impliesRejection` flag on conversation intents is powerful: "I think the approach is wrong, we should use React Query instead" gets routed as a reject with that feedback, without the user needing explicit "reject" phrasing.
- Spoken confirmations are kept short (one sentence) to minimize TTS latency for command acknowledgments.
- When no transport is available (e.g., standalone voice chat), the session operates in conversation-only mode. Engine commands are explained as unavailable.

## Completion Checklist

- [ ] IntentClassifier wired into VoiceSession
- [ ] Engine commands dispatched to transport
- [ ] Spoken confirmations for engine commands
- [ ] Questions answered from engine state
- [ ] Conversational feedback with rejection routing
- [ ] Approval flow via voice
- [ ] Multi-turn context for disambiguation
- [ ] Hybrid mode (text + voice through classifier)
- [ ] All unit tests passing
- [ ] TypeScript strict mode compiles
