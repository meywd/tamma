# Task 1: IntentClassifier with LLM-based Classification

**Story:** 24-4-intent-engine - Intent Classification + Engine Integration
**Epic:** 24

## Task Description

Create the `IntentClassifier` class that uses the existing `ILLMProvider` to classify user speech into three categories: engine commands, status questions, and conversational feedback. The classifier produces a structured `ClassifiedIntent` result that the `VoiceSession` uses to route actions.

## Acceptance Criteria

- `IntentClassifier` classifies user speech into: `engine-command`, `question`, `conversation`
- Engine commands mapped to `EngineCommand` types: start, approve, reject (with feedback), skip, cancel
- Questions identified for read-only engine state queries: "what's the status?", "show me the plan"
- Conversational feedback detected and prepared for plan rejection with context
- Classification uses the existing `ILLMProvider.complete()` with a structured system prompt
- Multi-turn context from `ConversationContext` included in classification for ambiguity resolution
- Classification latency target: <300ms (uses small context, short response)
- Unit tests with mock LLM provider

## Implementation Details

### Technical Requirements

- [ ] Create `packages/voice/src/intent-classifier.ts`:

```typescript
import type { ILLMProvider, MessageRequest, Message } from '@tamma/providers';
import type { EngineCommand } from '@tamma/shared/contracts';
import type { ConversationContext } from './conversation-context.js';

// ---- Intent Types ----

export type IntentType = 'engine-command' | 'question' | 'conversation';

export interface EngineCommandIntent {
  type: 'engine-command';
  command: EngineCommand;
  confidence: number;
}

export interface QuestionIntent {
  type: 'question';
  query: string;
  confidence: number;
}

export interface ConversationIntent {
  type: 'conversation';
  feedback: string;
  /** If true, this feedback implies rejection of the current plan. */
  impliesRejection: boolean;
  confidence: number;
}

export type ClassifiedIntent = EngineCommandIntent | QuestionIntent | ConversationIntent;

// ---- Classifier ----

export interface IntentClassifierConfig {
  /** Model to use for classification (prefer fast model). */
  model?: string;
  /** Minimum confidence to act on. Below this, treat as conversation. */
  confidenceThreshold?: number;  // default: 0.7
}

const INTENT_SYSTEM_PROMPT = `You are an intent classifier for a voice-controlled software development assistant called Tamma.

The user is speaking to control a development orchestrator that processes GitHub issues, generates code, runs tests, and creates PRs.

Classify the user's speech into one of three categories:

1. ENGINE_COMMAND - The user wants to execute an action:
   - "start" / "begin" / "run" / "go" -> { "command": "start" }
   - "approve" / "looks good" / "ship it" / "yes" -> { "command": "approve" }
   - "reject" / "no" / "redo" / "try again" -> { "command": "reject", "feedback": "<reason if given>" }
   - "skip" / "next" / "move on" -> { "command": "skip" }
   - "stop" / "cancel" / "halt" -> { "command": "stop" }
   - "process issue <number>" -> { "command": "process-issue", "issueNumber": <number> }

2. QUESTION - The user is asking about current state (read-only, no command):
   - "what's the status?" / "where are we?" / "what's happening?"
   - "show me the plan" / "what's the plan?"
   - "how long has it been running?" / "what's the cost?"
   - "what issue are you working on?"

3. CONVERSATION - The user is providing feedback, discussing approach, or chatting:
   - "that approach won't work because..." -> impliesRejection: true
   - "I think we should use a different library" -> impliesRejection: true
   - "good job" / "thanks" -> impliesRejection: false
   - General discussion about the code/approach

Respond with JSON only:
{
  "type": "ENGINE_COMMAND" | "QUESTION" | "CONVERSATION",
  "command"?: { "type": string, "feedback"?: string, "issueNumber"?: number },
  "query"?: string,
  "feedback"?: string,
  "impliesRejection"?: boolean,
  "confidence": number (0-1)
}`;

export class IntentClassifier {
  private readonly llm: ILLMProvider;
  private readonly model?: string;
  private readonly confidenceThreshold: number;

  constructor(llm: ILLMProvider, config?: IntentClassifierConfig) {
    this.llm = llm;
    this.model = config?.model;
    this.confidenceThreshold = config?.confidenceThreshold ?? 0.7;
  }

  /**
   * Classify a user utterance into an intent.
   *
   * @param text - The transcribed user speech
   * @param context - Optional conversation context for disambiguation
   * @param engineState - Optional current engine state for context
   */
  async classify(
    text: string,
    context?: ConversationContext,
    engineState?: { state: string; hasActivePlan: boolean },
  ): Promise<ClassifiedIntent> {
    const messages: Message[] = [
      { role: 'system', content: INTENT_SYSTEM_PROMPT },
    ];

    // Add recent conversation context for disambiguation
    if (context) {
      const recent = context.getRecentTurns(3);
      for (const turn of recent) {
        messages.push({ role: turn.role, content: turn.content });
      }
    }

    // Add engine state context
    if (engineState) {
      messages.push({
        role: 'system',
        content: `Current engine state: ${engineState.state}. ` +
          (engineState.hasActivePlan ? 'There is an active development plan awaiting review.' : 'No active plan.'),
      });
    }

    messages.push({ role: 'user', content: text });

    try {
      const response = await this.llm.complete({
        messages,
        model: this.model,
        maxTokens: 200,
        temperature: 0.1,  // Low temperature for deterministic classification
      });

      return this.parseClassification(response.content, text);
    } catch {
      // On LLM failure, default to conversation intent
      return { type: 'conversation', feedback: text, impliesRejection: false, confidence: 0.5 };
    }
  }

  private parseClassification(raw: string, originalText: string): ClassifiedIntent {
    try {
      // Extract JSON from response (may have markdown fences)
      const jsonMatch = raw.match(/\{[\s\S]*\}/);
      if (!jsonMatch) throw new Error('No JSON found');

      const parsed = JSON.parse(jsonMatch[0]) as {
        type: string;
        command?: { type: string; feedback?: string; issueNumber?: number };
        query?: string;
        feedback?: string;
        impliesRejection?: boolean;
        confidence: number;
      };

      const confidence = typeof parsed.confidence === 'number' ? parsed.confidence : 0.5;

      // Below confidence threshold -> treat as conversation
      if (confidence < this.confidenceThreshold && parsed.type === 'ENGINE_COMMAND') {
        return { type: 'conversation', feedback: originalText, impliesRejection: false, confidence };
      }

      switch (parsed.type) {
        case 'ENGINE_COMMAND': {
          if (!parsed.command) {
            return { type: 'conversation', feedback: originalText, impliesRejection: false, confidence };
          }
          const command = this.mapCommand(parsed.command);
          return { type: 'engine-command', command, confidence };
        }
        case 'QUESTION':
          return { type: 'question', query: parsed.query ?? originalText, confidence };
        case 'CONVERSATION':
        default:
          return {
            type: 'conversation',
            feedback: parsed.feedback ?? originalText,
            impliesRejection: parsed.impliesRejection ?? false,
            confidence,
          };
      }
    } catch {
      return { type: 'conversation', feedback: originalText, impliesRejection: false, confidence: 0.3 };
    }
  }

  private mapCommand(raw: { type: string; feedback?: string; issueNumber?: number }): EngineCommand {
    switch (raw.type) {
      case 'start': return { type: 'start' };
      case 'stop': return { type: 'stop' };
      case 'approve': return { type: 'approve' };
      case 'reject': return { type: 'reject', feedback: raw.feedback };
      case 'skip': return { type: 'skip' };
      case 'pause': return { type: 'pause' };
      case 'resume': return { type: 'resume' };
      case 'process-issue':
        return raw.issueNumber
          ? { type: 'process-issue', issueNumber: raw.issueNumber }
          : { type: 'start' };
      default:
        return { type: 'start' };
    }
  }
}
```

### Files to Modify/Create

- CREATE `packages/voice/src/intent-classifier.ts`
- CREATE `packages/voice/src/intent-classifier.test.ts`
- MODIFY `packages/voice/src/index.ts` -- add intent classifier export

### Dependencies

- [ ] `ILLMProvider`, `Message`, `MessageRequest` from `@tamma/providers`
- [ ] `EngineCommand` from `@tamma/shared/contracts`
- [ ] `ConversationContext` from Story 24-1

## Testing Strategy

### Unit Tests -- intent-classifier.test.ts

- [ ] Test "start the engine" classified as engine-command with `{ type: 'start' }`
- [ ] Test "approve the plan" classified as engine-command with `{ type: 'approve' }`
- [ ] Test "reject it, the tests are wrong" classified as engine-command with reject + feedback
- [ ] Test "skip this issue" classified as engine-command with `{ type: 'skip' }`
- [ ] Test "stop" classified as engine-command with `{ type: 'stop' }`
- [ ] Test "process issue 42" classified as engine-command with `{ type: 'process-issue', issueNumber: 42 }`
- [ ] Test "what's the status?" classified as question
- [ ] Test "show me the plan" classified as question
- [ ] Test "that approach won't work because the API changed" classified as conversation with impliesRejection: true
- [ ] Test "good job, thanks" classified as conversation with impliesRejection: false
- [ ] Test low confidence engine command falls back to conversation
- [ ] Test conversation context included in LLM messages
- [ ] Test engine state context included in LLM messages
- [ ] Test LLM failure defaults to conversation intent
- [ ] Test malformed LLM response defaults to conversation intent
- [ ] Test JSON extraction from markdown-fenced response
- [ ] Test temperature set to 0.1 for deterministic classification
- [ ] Test maxTokens set to 200 for fast response

### Mocking Strategy

```typescript
// Mock ILLMProvider
const mockLLM = {
  complete: vi.fn().mockResolvedValue({
    id: 'test',
    content: '{ "type": "ENGINE_COMMAND", "command": { "type": "approve" }, "confidence": 0.95 }',
    model: 'test',
    usage: { inputTokens: 0, outputTokens: 0, totalTokens: 0 },
    finishReason: 'stop' as const,
  }),
} as unknown as ILLMProvider;
```

### Validation Steps

1. [ ] Create IntentClassifier with system prompt and parsing logic
2. [ ] Test all engine command types
3. [ ] Test question detection
4. [ ] Test conversational feedback with rejection detection
5. [ ] Test confidence threshold filtering
6. [ ] Test error handling for LLM failures
7. [ ] Run all unit tests
8. [ ] Verify TypeScript strict mode compilation

## Notes & Considerations

- The classification LLM call is intentionally lightweight: short system prompt, minimal context (last 3 turns), low max_tokens (200), low temperature (0.1). This keeps latency under 300ms.
- The confidence threshold (0.7) prevents ambiguous utterances from accidentally triggering engine commands. Better to ask for clarification than to start/stop the engine by mistake.
- `impliesRejection` on conversation intents enables natural plan feedback: "that won't work because the API changed" is routed as a reject command with the feedback text, without the user needing to say "reject".
- The system prompt includes examples of natural speech patterns mapped to commands. This is more robust than keyword matching because users say things like "looks good" (approve) or "nah, try again" (reject).
- On LLM failure, the classifier defaults to `conversation` intent. This is the safest fallback -- it will not accidentally trigger engine commands.

## Completion Checklist

- [ ] IntentClassifier with LLM-based classification
- [ ] All engine commands mapped correctly
- [ ] Question detection works
- [ ] Conversation detection with rejection signals
- [ ] Confidence threshold filtering
- [ ] Graceful fallback on LLM errors
- [ ] All unit tests passing
- [ ] TypeScript strict mode compiles
