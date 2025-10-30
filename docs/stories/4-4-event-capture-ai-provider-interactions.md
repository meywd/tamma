# Story 4.4: Event Capture - AI Provider Interactions

Status: ready-for-dev

## Story

As a **AI governance team**,
I want all AI provider requests and responses captured as events,
so that I can audit AI usage, costs, and decision-making processes.

## Acceptance Criteria

1. `AIRequestEvent` captured before each AI provider call including: provider name, model, prompt (truncated if >1000 chars), token count estimate
2. `AIResponseEvent` captured after response including: provider name, model, response (truncated), token count, latency, cost estimate
3. Events include full prompt/response in separate blob storage for detailed analysis (with retention policy)
4. Events mask sensitive data (API keys, passwords) before persistence
5. Events include provider selection rationale (why this provider was chosen)
6. Events persisted to event store synchronously (block on write completion)

## Tasks / Subtasks

- [ ] Task 1: Implement AI request event capture (AC: 1)
  - [ ] Subtask 1.1: Create AIRequestEvent schema and payload structure
  - [ ] Subtask 1.2: Integrate event capture into AI provider interface
  - [ ] Subtask 1.3: Add prompt truncation and token counting
  - [ ] Subtask 1.4: Implement provider selection rationale capture
  - [ ] Subtask 1.5: Add request event capture unit tests

- [ ] Task 2: Implement AI response event capture (AC: 2)
  - [ ] Subtask 2.1: Create AIResponseEvent schema and payload structure
  - [ ] Subtask 2.2: Integrate event capture into AI provider response handling
  - [ ] Subtask 2.3: Add response truncation and token counting
  - [ ] Subtask 2.4: Implement latency and cost calculation
  - [ ] Subtask 2.5: Add response event capture unit tests

- [ ] Task 3: Implement blob storage for full payloads (AC: 3)
  - [ ] Subtask 3.1: Design blob storage interface and configuration
  - [ ] Subtask 3.2: Implement local file blob storage
  - [ ] Subtask 3.3: Implement S3-compatible blob storage (optional)
  - [ ] Subtask 3.4: Add blob retention policy management
  - [ ] Subtask 3.5: Create blob storage cleanup and maintenance

- [ ] Task 4: Implement sensitive data masking (AC: 4)
  - [ ] Subtask 4.1: Create sensitive data detection patterns
  - [ ] Subtask 4.2: Implement data masking utilities
  - [ ] Subtask 4.3: Add configurable masking rules
  - [ ] Subtask 4.4: Create masking validation and testing
  - [ ] Subtask 4.5: Add masking audit logging

- [ ] Task 5: Implement provider selection rationale (AC: 5)
  - [ ] Subtask 5.1: Create provider selection decision tracking
  - [ ] Subtask 5.2: Implement selection criteria capture
  - [ ] Subtask 5.3: Add provider capability matching logic
  - [ ] Subtask 5.4: Create selection scoring and reasoning
  - [ ] Subtask 5.5: Add selection rationale validation

- [ ] Task 6: Implement synchronous event persistence (AC: 6)
  - [ ] Subtask 6.1: Create AI event capture service
  - [ ] Subtask 6.2: Implement transactional event writing
  - [ ] Subtask 6.3: Add event persistence validation
  - [ ] Subtask 6.4: Create event capture error handling
  - [ ] Subtask 6.5: Add synchronous persistence integration tests

## Dev Notes

### Requirements Context Summary

**Epic 4 Integration:** This story captures all AI interactions for comprehensive audit trails and cost tracking. AI events are critical for understanding autonomous decision-making processes and managing AI provider costs.

**AI Governance Requirements:** Events must capture provider selection rationale, usage patterns, and cost information to support AI governance and compliance. This enables tracking of AI model usage, cost optimization, and provider performance analysis.

**Data Privacy Requirements:** Sensitive data must be masked before event persistence while maintaining full payloads in secure blob storage for detailed analysis. This balances privacy needs with debugging and audit requirements.

### Implementation Guidance

**Event Schema Definitions:**

```typescript
interface AIRequestEvent extends BaseEvent {
  eventType: 'AI.REQUEST.STARTED';
  payload: {
    provider: {
      name: string; // 'anthropic', 'openai', 'github-copilot'
      model: string; // 'claude-3-sonnet', 'gpt-4', etc.
      endpoint?: string; // Custom endpoint if applicable
    };
    request: {
      id: string; // Unique request identifier
      type: 'chat' | 'completion' | 'embedding' | 'tool-use';
      prompt: string; // Truncated to 1000 chars for event
      promptLength: number; // Full prompt length
      estimatedTokens: number; // Token count estimate
      temperature?: number;
      maxTokens?: number;
      tools?: Array<{
        name: string;
        description: string;
        parameters: Record<string, unknown>;
      }>;
    };
    selection: {
      rationale: string; // Why this provider was chosen
      alternatives: Array<{
        provider: string;
        model: string;
        reason: string; // Why not chosen
        score: number;
      }>;
      criteria: {
        cost: number; // Cost weighting
        performance: number; // Performance weighting
        capabilities: string[]; // Required capabilities
      };
    };
    context: {
      correlationId: string; // Workflow correlation
      stepId: string; // Current workflow step
      issueId?: string; // Related issue if applicable
      prId?: string; // Related PR if applicable
    };
    blobStorage?: {
      fullPromptKey: string; // Key for full prompt in blob storage
      retentionDays: number; // Retention policy for blob
    };
  };
}

interface AIResponseEvent extends BaseEvent {
  eventType: 'AI.RESPONSE.RECEIVED';
  payload: {
    provider: {
      name: string;
      model: string;
    };
    request: {
      id: string; // Matches AI request ID
      type: string;
    };
    response: {
      content: string; // Truncated to 1000 chars for event
      contentLength: number; // Full response length
      actualTokens: {
        prompt: number;
        completion: number;
        total: number;
      };
      finishReason: 'stop' | 'length' | 'content_filter' | 'tool_calls' | 'error';
      toolCalls?: Array<{
        id: string;
        name: string;
        arguments: string;
      }>;
    };
    performance: {
      latency: number; // Response time in milliseconds
      startTime: string; // Request start timestamp
      endTime: string; // Response received timestamp
      retryCount: number; // Number of retries
    };
    cost: {
      promptCost: number; // Cost for prompt tokens
      completionCost: number; // Cost for completion tokens
      totalCost: number; // Total cost for request
      currency: string; // 'USD' or other currency
    };
    quality: {
      success: boolean; // Request succeeded
      errorCode?: string; // Error code if failed
      errorMessage?: string; // Error message if failed
      rating?: number; // Optional quality rating (1-5)
    };
    blobStorage?: {
      fullResponseKey: string; // Key for full response in blob storage
      retentionDays: number;
    };
  };
}
```

**Blob Storage Interface:**

```typescript
interface IBlobStorage {
  store(key: string, data: string | Buffer, options?: StorageOptions): Promise<string>;
  retrieve(key: string): Promise<Buffer | null>;
  delete(key: string): Promise<void>;
  list(prefix: string): Promise<string[]>;
  getMetadata(key: string): Promise<StorageMetadata | null>;
}

interface StorageOptions {
  contentType?: string;
  retentionDays?: number;
  encryption?: 'none' | 'server-side' | 'client-side';
  compression?: 'none' | 'gzip';
}

// Local file implementation
class LocalBlobStorage implements IBlobStorage {
  constructor(private config: LocalStorageConfig) {}

  async store(key: string, data: string | Buffer, options?: StorageOptions): Promise<string> {
    const filePath = path.join(this.config.dataDirectory, key);
    await fs.ensureDir(path.dirname(filePath));

    let content = data;
    if (options?.compression === 'gzip') {
      content = await gzip(data);
    }

    await fs.writeFile(filePath, content);

    // Store metadata separately
    const metadata = {
      key,
      size: content.length,
      contentType: options?.contentType || 'text/plain',
      retentionDays: options?.retentionDays || 30,
      encryption: options?.encryption || 'none',
      compression: options?.compression || 'none',
      createdAt: new Date().toISOString(),
    };

    await fs.writeFile(`${filePath}.meta`, JSON.stringify(metadata));
    return key;
  }
}
```

**Sensitive Data Masking:**

```typescript
class DataMasker {
  private readonly patterns = [
    { name: 'api_key', regex: /(?:api[_-]?key|apikey)[\s:=]+['"]?([a-zA-Z0-9_-]{20,})['"]?/gi },
    { name: 'password', regex: /(?:password|pwd|pass)[\s:=]+['"]?([^'"\s]{8,})['"]?/gi },
    { name: 'token', regex: /(?:token|bearer)[\s:=]+['"]?([a-zA-Z0-9._-]{20,})['"]?/gi },
    { name: 'secret', regex: /(?:secret|private[_-]?key)[\s:=]+['"]?([a-zA-Z0-9_-]{20,})['"]?/gi },
    { name: 'email', regex: /\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b/g },
  ];

  maskSensitiveData(text: string): string {
    let masked = text;

    for (const pattern of this.patterns) {
      masked = masked.replace(pattern.regex, (match, captured) => {
        const maskedValue = this.maskValue(captured, pattern.name);
        return match.replace(captured, maskedValue);
      });
    }

    return masked;
  }

  private maskValue(value: string, type: string): string {
    switch (type) {
      case 'email':
        return value.replace(/(.{2}).*(@.*)/, '$1***$2');
      case 'api_key':
      case 'token':
      case 'secret':
        return value.substring(0, 8) + '***';
      case 'password':
        return '***';
      default:
        return '***';
    }
  }
}
```

**AI Provider Integration:**

```typescript
class EventCapturingAIProvider implements IAIProvider {
  constructor(
    private baseProvider: IAIProvider,
    private eventStore: IEventStore,
    private blobStorage: IBlobStorage,
    private dataMasker: DataMasker,
    private correlationManager: CorrelationManager
  ) {}

  async sendMessage(request: MessageRequest): Promise<AsyncIterable<MessageChunk>> {
    const requestId = generateRequestId();
    const startTime = Date.now();

    // Capture AI request event
    const requestEvent: AIRequestEvent = {
      eventId: generateEventId(),
      timestamp: new Date().toISOString(),
      eventType: 'AI.REQUEST.STARTED',
      actorType: 'system',
      actorId: 'tamma-orchestrator',
      correlationId: this.correlationManager.getCurrentCorrelationId(),
      schemaVersion: '1.0.0',
      payload: {
        provider: {
          name: this.baseProvider.getName(),
          model: request.model,
          endpoint: request.endpoint,
        },
        request: {
          id: requestId,
          type: this.getRequestType(request),
          prompt: this.dataMasker.maskSensitiveData(request.message.substring(0, 1000)),
          promptLength: request.message.length,
          estimatedTokens: this.estimateTokens(request.message),
          temperature: request.temperature,
          maxTokens: request.maxTokens,
          tools: request.tools,
        },
        selection: {
          rationale: request.selectionRationale || 'Default provider',
          alternatives: request.alternatives || [],
          criteria: request.selectionCriteria || { cost: 0.5, performance: 0.5, capabilities: [] },
        },
        context: {
          correlationId: this.correlationManager.getCurrentCorrelationId(),
          stepId: this.correlationManager.getCurrentStepId(),
          issueId: request.context?.issueId,
          prId: request.context?.prId,
        },
      },
      metadata: {
        source: 'orchestrator',
        version: process.env.TAMMA_VERSION || '1.0.0',
        environment: process.env.NODE_ENV || 'development',
      },
    };

    // Store full prompt in blob storage
    if (request.message.length > 1000) {
      const blobKey = `ai-requests/${requestId}/prompt.txt`;
      await this.blobStorage.store(blobKey, request.message, {
        retentionDays: 30,
        contentType: 'text/plain',
      });
      requestEvent.payload.blobStorage = {
        fullPromptKey: blobKey,
        retentionDays: 30,
      };
    }

    // Persist request event synchronously
    await this.eventStore.append(requestEvent);

    try {
      // Execute actual AI request
      const responseStream = await this.baseProvider.sendMessage(request);

      // Capture response and create response event
      return this.captureResponse(requestId, startTime, responseStream);
    } catch (error) {
      // Capture error event
      await this.captureError(requestId, startTime, error as Error);
      throw error;
    }
  }

  private async *captureResponse(
    requestId: string,
    startTime: number,
    responseStream: AsyncIterable<MessageChunk>
  ): AsyncIterable<MessageChunk> {
    const chunks: MessageChunk[] = [];
    let fullResponse = '';

    try {
      for await (const chunk of responseStream) {
        chunks.push(chunk);
        fullResponse += chunk.content;
        yield chunk;
      }

      // Capture successful response event
      const endTime = Date.now();
      const latency = endTime - startTime;

      const responseEvent: AIResponseEvent = {
        eventId: generateEventId(),
        timestamp: new Date().toISOString(),
        eventType: 'AI.RESPONSE.RECEIVED',
        actorType: 'system',
        actorId: 'tamma-orchestrator',
        correlationId: this.correlationManager.getCurrentCorrelationId(),
        schemaVersion: '1.0.0',
        payload: {
          provider: {
            name: this.baseProvider.getName(),
            model: chunks[0]?.model || 'unknown',
          },
          request: {
            id: requestId,
            type: 'chat',
          },
          response: {
            content: this.dataMasker.maskSensitiveData(fullResponse.substring(0, 1000)),
            contentLength: fullResponse.length,
            actualTokens: this.calculateTokens(fullResponse),
            finishReason: chunks[chunks.length - 1]?.finishReason || 'stop',
          },
          performance: {
            latency,
            startTime: new Date(startTime).toISOString(),
            endTime: new Date(endTime).toISOString(),
            retryCount: 0,
          },
          cost: {
            promptCost: this.calculateCost(chunks, 'prompt'),
            completionCost: this.calculateCost(chunks, 'completion'),
            totalCost: this.calculateCost(chunks, 'total'),
            currency: 'USD',
          },
          quality: {
            success: true,
            rating: this.calculateQualityRating(chunks),
          },
        },
        metadata: {
          source: 'orchestrator',
          version: process.env.TAMMA_VERSION || '1.0.0',
          environment: process.env.NODE_ENV || 'development',
        },
      };

      // Store full response in blob storage
      if (fullResponse.length > 1000) {
        const blobKey = `ai-responses/${requestId}/response.txt`;
        await this.blobStorage.store(blobKey, fullResponse, {
          retentionDays: 30,
          contentType: 'text/plain',
        });
        responseEvent.payload.blobStorage = {
          fullResponseKey: blobKey,
          retentionDays: 30,
        };
      }

      // Persist response event synchronously
      await this.eventStore.append(responseEvent);
    } catch (error) {
      await this.captureError(requestId, startTime, error as Error);
      throw error;
    }
  }
}
```

### Technical Specifications

**Performance Requirements:**

- Event capture overhead: <10ms per AI request
- Blob storage latency: <100ms for typical payloads
- Data masking performance: <5ms for typical prompts
- Memory usage: <50MB for AI event buffers

**Security Requirements:**

- Sensitive data detection: >95% accuracy
- Data masking: Irreversible for sensitive fields
- Blob storage encryption: AES-256 at rest
- Access control: Role-based permissions

**Retention Requirements:**

- Event storage: Infinite (configurable)
- Blob storage: 30 days default (configurable)
- Data cleanup: Automated retention enforcement
- Archive support: Long-term storage options

**Monitoring Requirements:**

- AI usage metrics: Token count, cost, latency
- Provider performance: Success rate, error patterns
- Event capture metrics: Success rate, storage usage
- Cost tracking: Per-provider, per-model costs

### Dependencies

**Internal Dependencies:**

- Story 4.1: Event schema design (provides event structures)
- Story 4.2: Event store backend selection (provides storage)
- Story 1.1: AI provider interface definition (integration point)
- Story 2.12: Intelligent provider selection (selection rationale)

**External Dependencies:**

- Blob storage service (local file system, S3-compatible)
- Data masking library
- Token counting library
- Cost calculation service

### Risks and Mitigations

| Risk                      | Severity | Mitigation                                 |
| ------------------------- | -------- | ------------------------------------------ |
| Sensitive data leakage    | High     | Comprehensive masking patterns, validation |
| Blob storage performance  | Medium   | Async operations, compression              |
| Event capture overhead    | Medium   | Efficient algorithms, batching             |
| Cost calculation accuracy | Medium   | Provider-specific pricing models           |

### Success Metrics

- [ ] Event capture success rate: >99.9%
- [ ] Sensitive data masking: >95% accuracy
- [ ] Event capture overhead: <10ms per request
- [ ] Cost calculation accuracy: >99%
- [ ] Blob storage retention: 100% compliance

## Related

- Related story: `docs/stories/4-1-event-schema-design.md`
- Related story: `docs/stories/4-2-event-store-backend-selection.md`
- Related story: `docs/stories/1-1-ai-provider-interface-definition.md`
- Related story: `docs/stories/2-12-intelligent-provider-selection.md`
- Technical specification: `docs/tech-spec-epic-4.md`

## References

- [AI Governance Guidelines](https://nist.gov/ai-framework)
- [Data Privacy Best Practices](https://gdpr.eu/what-is-gdpr/)
- [Blob Storage Patterns](https://docs.aws.amazon.com/AmazonS3/latest/dev/UsingObjects.html)
- [Token Counting Algorithms](https://github.com/openai/tiktoken)
