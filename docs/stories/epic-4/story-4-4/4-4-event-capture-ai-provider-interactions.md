# Story 4.4: Event Capture - AI Provider Interactions

## Overview

Implement comprehensive event capture for all AI provider requests and responses to enable complete auditability of AI usage, costs, and decision-making processes, ensuring transparency and governance in autonomous development workflows.

## Acceptance Criteria

### AI Provider Event Capture

- [ ] `AIRequestEvent` captured before each AI provider call including: provider name, model, prompt (truncated if >1000 chars), token count estimate
- [ ] `AIResponseEvent` captured after response including: provider name, model, response (truncated), token count, latency, cost estimate
- [ ] Events include full prompt/response in separate blob storage for detailed analysis (with retention policy)
- [ ] Events mask sensitive data (API keys, passwords) before persistence
- [ ] Events include provider selection rationale (why this provider was chosen)
- [ ] Events persisted to event store synchronously (block on write completion)

## Technical Context

### Event Capture Integration Points

This story integrates with the AI provider abstraction from Epic 1 and the autonomous workflow from Epic 2:

**AI Request Event:**

```typescript
interface AIRequestEvent {
  eventId: string; // UUID v7
  timestamp: string; // ISO 8601 millisecond precision
  eventType: 'AIRequest';
  actorType: 'system' | 'ci-runner';
  actorId: string; // System ID or CI runner ID
  payload: {
    provider: {
      name: string; // anthropic-claude, openai-gpt, etc.
      version: string; // Provider API version
      endpoint: string; // API endpoint used
    };
    model: {
      name: string; // claude-3-5-sonnet, gpt-4, etc.
      version: string; // Model version
      capabilities: string[]; // text, code, vision, etc.
    };
    request: {
      type: 'code_generation' | 'analysis' | 'review' | 'planning' | 'debugging';
      prompt: string; // Truncated if >1000 chars for main event
      promptLength: number; // Full prompt length
      estimatedTokens: {
        input: number;
        output?: number; // For models that support it
      };
      context: {
        issueId?: string;
        prId?: string;
        workflowId?: string;
        correlationId?: string;
        step: string; // Workflow step
      };
      parameters: {
        temperature?: number;
        maxTokens?: number;
        topP?: number;
        frequencyPenalty?: number;
        presencePenalty?: number;
        [key: string]: unknown;
      };
    };
    providerSelection: {
      rationale: string; // Why this provider was chosen
      alternatives: string[]; // Other providers considered
      selectionCriteria: {
        cost?: number; // Cost consideration
        speed?: number; // Latency consideration
        quality?: number; // Quality consideration
        capabilities?: string[]; // Required capabilities
      };
    };
    security: {
      dataMasking: boolean; // Whether sensitive data was masked
      piiDetected: boolean; // Whether PII was detected
      complianceChecks: string[]; // Compliance checks performed
    };
  };
  metadata: {
    schemaVersion: '1.0.0';
    correlationId: string; // Links entire development cycle
    workflowId: string;
    source: 'orchestrator' | 'worker';
    mode: 'dev' | 'business';
    requestId: string; // Unique request identifier
  };
}
```

**AI Response Event:**

```typescript
interface AIResponseEvent {
  eventId: string;
  timestamp: string;
  eventType: 'AIResponse';
  actorType: 'system' | 'ci-runner';
  actorId: string;
  payload: {
    provider: {
      name: string;
      version: string;
      endpoint: string;
    };
    model: {
      name: string;
      version: string;
    };
    request: {
      requestId: string; // Links to AIRequestEvent
      type: string;
    };
    response: {
      content: string; // Truncated for main event
      contentLength: number; // Full response length
      actualTokens: {
        input: number;
        output: number;
        total: number;
      };
      finishReason: 'stop' | 'length' | 'content_filter' | 'function_call' | 'tool_calls';
      success: boolean;
      errorMessage?: string;
      errorCode?: string;
    };
    performance: {
      latency: number; // Response time in milliseconds
      firstTokenTime?: number; // Time to first token
      throughput: number; // Tokens per second
      queueTime?: number; // Time spent in provider queue
    };
    cost: {
      inputCost: number; // Cost in USD
      outputCost: number;
      totalCost: number;
      currency: string;
      pricingModel: 'per-token' | 'per-request' | 'per-minute';
    };
    quality: {
      coherenceScore?: number; // 0-1
      relevanceScore?: number; // 0-1
      completenessScore?: number; // 0-1
      confidenceScore?: number; // Provider's confidence
    };
    security: {
      dataMasking: boolean;
      piiDetected: boolean;
      contentFilter: {
        blocked: boolean;
        categories: string[];
        severity: 'low' | 'medium' | 'high';
      };
    };
  };
  metadata: {
    schemaVersion: '1.0.0';
    correlationId: string; // Same as AIRequestEvent
    workflowId: string;
    source: 'orchestrator' | 'worker';
    mode: 'dev' | 'business';
    requestId: string; // Same as AIRequestEvent
  };
}
```

### Full Content Storage

For detailed analysis, full prompts and responses are stored separately:

```typescript
interface AIContentStorage {
  async storeFullContent(
    eventId: string,
    type: 'prompt' | 'response',
    content: string,
    metadata: ContentMetadata
  ): Promise<string>; // Returns storage path/ID

  async retrieveFullContent(
    storageId: string
  ): Promise<{ content: string; metadata: ContentMetadata }>;

  async deleteExpiredContent(): Promise<void>; // Cleanup based on retention policy
}

interface ContentMetadata {
  eventId: string;
  type: 'prompt' | 'response';
  provider: string;
  model: string;
  timestamp: string;
  size: number;
  contentType: string;
  retentionPeriod: number; // days
  classification: 'public' | 'internal' | 'confidential' | 'restricted';
}
```

### Data Masking and Security

```typescript
class AIDataMasker {
  private sensitivePatterns = [
    /api[_-]?key[_-]?=\s*[a-zA-Z0-9\-_]{20,}/gi,
    /password[_-]?=\s*[^\s&;]{8,}/gi,
    /token[_-]?=\s*[a-zA-Z0-9\-_]{20,}/gi,
    /secret[_-]?=\s*[^\s&;]{8,}/gi,
    // Add more patterns as needed
  ];

  maskSensitiveData(text: string): {
    maskedText: string;
    maskingReport: MaskingReport;
  } {
    const maskingReport: MaskingReport = {
      originalLength: text.length,
      maskedItems: [],
      piiDetected: false,
    };

    let maskedText = text;

    // Mask API keys, passwords, tokens
    this.sensitivePatterns.forEach((pattern) => {
      const matches = text.match(pattern);
      if (matches) {
        matches.forEach((match) => {
          maskedText = maskedText.replace(match, '***REDACTED***');
          maskingReport.maskedItems.push({
            type: 'sensitive_data',
            original: match.substring(0, 10) + '...',
            replacement: '***REDACTED***',
          });
        });
      }
    });

    // Detect PII (simplified - in production use proper PII detection)
    const piiPatterns = [
      /\b\d{3}-\d{2}-\d{4}\b/g, // SSN
      /\b\d{4}[-\s]?\d{4}[-\s]?\d{4}[-\s]?\d{4}\b/g, // Credit card
      /\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b/g, // Email
    ];

    piiPatterns.forEach((pattern) => {
      const matches = text.match(pattern);
      if (matches) {
        maskingReport.piiDetected = true;
        matches.forEach((match) => {
          maskedText = maskedText.replace(match, '***PII_REDACTED***');
          maskingReport.maskedItems.push({
            type: 'pii',
            original: match.substring(0, 5) + '...',
            replacement: '***PII_REDACTED***',
          });
        });
      }
    });

    return { maskedText, maskingReport };
  }
}
```

### Event Capture Integration

```typescript
class AIEventCapture {
  constructor(
    private eventStore: IEventStore,
    private contentStorage: AIContentStorage,
    private dataMasker: AIDataMasker,
    private costCalculator: AICostCalculator
  ) {}

  async captureAIRequest(
    request: AIRequest,
    providerSelection: ProviderSelection,
    correlationId: string
  ): Promise<string> {
    // Mask sensitive data in prompt
    const { maskedText, maskingReport } = this.dataMasker.maskSensitiveData(request.prompt);

    // Store full prompt in blob storage
    const promptStorageId = await this.contentStorage.storeFullContent(
      generateUUIDv7(),
      'prompt',
      request.prompt,
      {
        eventId: generateUUIDv7(),
        type: 'prompt',
        provider: request.provider.name,
        model: request.model.name,
        timestamp: new Date().toISOString(),
        size: request.prompt.length,
        contentType: 'text/plain',
        retentionPeriod: 30, // days
        classification: this.classifyContent(request.prompt),
      }
    );

    // Create event
    const event: AIRequestEvent = {
      eventId: generateUUIDv7(),
      timestamp: new Date().toISOString(),
      eventType: 'AIRequest',
      actorType: this.getActorType(),
      actorId: this.getActorId(),
      payload: {
        provider: {
          name: request.provider.name,
          version: request.provider.version,
          endpoint: request.provider.endpoint,
        },
        model: {
          name: request.model.name,
          version: request.model.version,
          capabilities: request.model.capabilities,
        },
        request: {
          type: request.type,
          prompt: maskedText.length > 1000 ? maskedText.substring(0, 1000) + '...' : maskedText,
          promptLength: request.prompt.length,
          estimatedTokens: this.estimateTokens(request.prompt),
          context: request.context,
          parameters: request.parameters,
        },
        providerSelection,
        security: {
          dataMasking: maskingReport.maskedItems.length > 0,
          piiDetected: maskingReport.piiDetected,
          complianceChecks: ['data_masking', 'pii_detection'],
        },
      },
      metadata: {
        schemaVersion: '1.0.0',
        correlationId,
        workflowId: this.getWorkflowId(),
        source: this.getSource(),
        mode: this.getMode(),
        requestId: request.id,
      },
    };

    // Persist event synchronously
    await this.eventStore.append(event);

    return event.eventId;
  }

  async captureAIResponse(
    requestEventId: string,
    response: AIResponse,
    startTime: number,
    correlationId: string
  ): Promise<string> {
    // Calculate cost and performance metrics
    const cost = this.costCalculator.calculateCost(response);
    const latency = Date.now() - startTime;

    // Mask sensitive data in response
    const { maskedText, maskingReport } = this.dataMasker.maskSensitiveData(response.content);

    // Store full response in blob storage
    const responseStorageId = await this.contentStorage.storeFullContent(
      generateUUIDv7(),
      'response',
      response.content,
      {
        eventId: generateUUIDv7(),
        type: 'response',
        provider: response.provider.name,
        model: response.model.name,
        timestamp: new Date().toISOString(),
        size: response.content.length,
        contentType: 'text/plain',
        retentionPeriod: 30,
        classification: this.classifyContent(response.content),
      }
    );

    // Create event
    const event: AIResponseEvent = {
      eventId: generateUUIDv7(),
      timestamp: new Date().toISOString(),
      eventType: 'AIResponse',
      actorType: this.getActorType(),
      actorId: this.getActorId(),
      payload: {
        provider: {
          name: response.provider.name,
          version: response.provider.version,
          endpoint: response.provider.endpoint,
        },
        model: {
          name: response.model.name,
          version: response.model.version,
        },
        request: {
          requestId: requestEventId,
          type: response.type,
        },
        response: {
          content: maskedText.length > 1000 ? maskedText.substring(0, 1000) + '...' : maskedText,
          contentLength: response.content.length,
          actualTokens: response.usage,
          finishReason: response.finishReason,
          success: response.success,
          errorMessage: response.errorMessage,
          errorCode: response.errorCode,
        },
        performance: {
          latency,
          firstTokenTime: response.firstTokenTime,
          throughput: response.usage.total / (latency / 1000),
          queueTime: response.queueTime,
        },
        cost,
        quality: {
          coherenceScore: response.coherenceScore,
          relevanceScore: response.relevanceScore,
          completenessScore: response.completenessScore,
          confidenceScore: response.confidenceScore,
        },
        security: {
          dataMasking: maskingReport.maskedItems.length > 0,
          piiDetected: maskingReport.piiDetected,
          contentFilter: response.contentFilter,
        },
      },
      metadata: {
        schemaVersion: '1.0.0',
        correlationId,
        workflowId: this.getWorkflowId(),
        source: this.getSource(),
        mode: this.getMode(),
        requestId: requestEventId,
      },
    };

    // Persist event synchronously
    await this.eventStore.append(event);

    return event.eventId;
  }
}
```

## Implementation Tasks

### 1. Event Schema Implementation

- [ ] Create `AIRequestEvent` and `AIResponseEvent` schemas
- [ ] Implement TypeScript interfaces for AI event payloads
- [ ] Add JSON schema validation for both event types
- [ ] Create event builders with proper validation

### 2. Data Masking System

- [ ] Implement `AIDataMasker` with pattern-based masking
- [ ] Add PII detection capabilities
- [ ] Create masking reports and audit trails
- [ ] Implement content classification system

### 3. Content Storage System

- [ ] Implement `AIContentStorage` for full prompt/response storage
- [ ] Add retention policy management
- [ ] Create content retrieval and cleanup mechanisms
- [ ] Implement storage backend abstraction

### 4. Event Capture Service

- [ ] Implement `AIEventCapture` class
- [ ] Add integration with AI provider abstraction
- [ ] Implement cost calculation and performance tracking
- [ ] Add synchronous event persistence

### 5. Provider Integration

- [ ] Integrate event capture into all AI provider implementations
- [ ] Ensure events are captured before/after each API call
- [ ] Add provider selection rationale tracking
- [ ] Implement error handling for failed captures

### 6. Testing

- [ ] Unit tests for event capture service
- [ ] Integration tests with AI providers
- [ ] Data masking and security tests
- [ ] Content storage and retrieval tests

## Dependencies

### Internal Dependencies

- `@tamma/events` - Event schemas and validation
- `@tamma/providers` - AI provider abstraction
- `@tamma/workflow` - Autonomous workflow integration
- Story 4.2 - Event Store Backend (for persistence)

### External Dependencies

- AI provider SDKs (Anthropic, OpenAI, etc.)
- Blob storage (local filesystem, S3, etc.)
- Content classification service (optional)

## Success Metrics

- 100% of AI requests captured as events
- 100% of AI responses captured as events
- Zero sensitive data leakage in persisted events
- Event capture adds <200ms overhead to AI calls
- Full content storage with proper retention policies

## Risks and Mitigations

### Security Risks

- **Risk**: Sensitive data may leak through event storage
- **Mitigation**: Comprehensive data masking, PII detection, content classification

### Performance Risks

- **Risk**: Event capture may slow down AI interactions
- **Mitigation**: Async content storage, optimized masking, efficient serialization

### Storage Risks

- **Risk**: Full content storage may become expensive
- **Mitigation**: Retention policies, compression, tiered storage

### Compliance Risks

- **Risk**: AI usage may not be fully auditable
- **Mitigation**: Complete event capture, immutable storage, correlation tracking

## Notes

This story is critical for AI governance and compliance. Every AI interaction must be captured with complete context including provider selection rationale, cost tracking, and performance metrics. The synchronous persistence ensures no AI interactions are lost, while the separate content storage allows for detailed analysis without bloating the main event store.

The data masking and PII detection are essential for security and compliance, ensuring that sensitive information is not stored in plain text while still maintaining auditability of the AI decision-making process.
