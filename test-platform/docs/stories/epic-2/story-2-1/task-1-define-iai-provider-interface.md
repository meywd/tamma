# Task 1: Define IAIProvider Interface

**Story**: 2.1 - AI Provider Abstraction Interface  
**Acceptance Criteria**: 1 - Abstract IAIProvider interface with standardized methods  
**Status**: Ready for Development

## Overview

This task involves creating the core `IAIProvider` interface that will serve as the contract for all AI provider implementations in the Tamma platform. This interface must provide a standardized way to interact with different AI providers while supporting both streaming and synchronous communication patterns.

## Subtasks

### Subtask 1.1: Create Core Interface Methods

**Objective**: Define the essential methods that every AI provider must implement.

**Implementation Details**:

1. **File Location**: `packages/providers/src/interfaces/IAIProvider.ts`

2. **Core Methods to Define**:

   ```typescript
   interface IAIProvider {
     // Lifecycle Management
     initialize(config: ProviderConfig): Promise<void>;
     dispose(): Promise<void>;

     // Communication Methods
     sendMessage(
       request: MessageRequest,
       options?: StreamOptions
     ): Promise<AsyncIterable<MessageChunk>>;
     sendMessageSync(request: MessageRequest): Promise<MessageResponse>;

     // Discovery Methods
     getCapabilities(): ProviderCapabilities;
     getModels(): Promise<ModelInfo[]>;

     // Health Check
     healthCheck(): Promise<ProviderHealthStatus>;
   }
   ```

3. **Method Specifications**:
   - `initialize()`: Sets up the provider with configuration, API keys, and connection parameters
   - `sendMessage()`: Sends a message and returns an async iterable for streaming responses
   - `sendMessageSync()`: Sends a message and returns a complete response (non-streaming)
   - `getCapabilities()`: Returns provider capabilities (supported languages, features, limits)
   - `getModels()`: Returns available models with their specifications
   - `dispose()`: Cleans up resources and connections
   - `healthCheck()`: Checks if the provider is accessible and functional

4. **Error Handling Requirements**:
   - All methods must throw `ProviderError` for provider-specific issues
   - Initialization failures should be clearly communicated
   - Network timeouts must be handled appropriately

### Subtask 1.2: Define Provider Capabilities Structure

**Objective**: Create a comprehensive structure to describe what each provider can do.

**Implementation Details**:

1. **File Location**: `packages/providers/src/types/ProviderCapabilities.ts`

2. **Capabilities Structure**:

   ```typescript
   interface ProviderCapabilities {
     // Basic Information
     name: string;
     version: string;
     providerType:
       | 'anthropic'
       | 'openai'
       | 'github-copilot'
       | 'google-gemini'
       | 'opencode'
       | 'z-ai'
       | 'zen-mcp'
       | 'openrouter'
       | 'local';

     // Feature Support
     features: {
       streaming: boolean;
       functionCalling: boolean;
       imageGeneration: boolean;
       codeExecution: boolean;
       fileUpload: boolean;
       systemMessages: boolean;
       toolUse: boolean;
     };

     // Language Support
     supportedLanguages: string[];
     preferredLanguages: string[];

     // Model Capabilities
     maxTokens: number;
     maxContextLength: number;
     supportsTemperature: boolean;
     supportsTopP: boolean;
     supportsFrequencyPenalty: boolean;
     supportsPresencePenalty: boolean;

     // Rate Limits
     rateLimits: {
       requestsPerMinute: number;
       tokensPerMinute: number;
       concurrentRequests: number;
     };

     // Cost Information
     pricing: {
       inputTokenCost: number;
       outputTokenCost: number;
       currency: string;
       billingUnit: 'tokens' | 'requests' | 'minutes';
     };
   }
   ```

3. **Validation Requirements**:
   - All capabilities must be accurately reported
   - Rate limits should reflect actual provider limits
   - Pricing information must be current and accurate

### Subtask 1.3: Specify Request/Response Models

**Objective**: Define standardized models for communication with AI providers.

**Implementation Details**:

1. **File Location**: `packages/providers/src/types/MessageModels.ts`

2. **Request Model**:

   ```typescript
   interface MessageRequest {
     // Core Message Content
     messages: Message[];
     model?: string;
     maxTokens?: number;
     temperature?: number;
     topP?: number;
     frequencyPenalty?: number;
     presencePenalty?: number;

     // Streaming Options
     stream?: boolean;
     streamOptions?: {
       includeUsage?: boolean;
       includeToolCalls?: boolean;
     };

     // Tool/Function Calling
     tools?: Tool[];
     toolChoice?: 'auto' | 'none' | { type: 'function'; function: { name: string } };

     // Metadata
     metadata?: {
       requestId?: string;
       userId?: string;
       sessionId?: string;
       [key: string]: unknown;
     };
   }

   interface Message {
     role: 'system' | 'user' | 'assistant' | 'tool';
     content:
       | string
       | Array<{
           type: 'text' | 'image_url';
           text?: string;
           image_url?: { url: string };
         }>;
     name?: string;
     toolCalls?: ToolCall[];
     toolCallId?: string;
   }
   ```

3. **Response Models**:

   ```typescript
   interface MessageResponse {
     id: string;
     object: 'chat.completion';
     created: number;
     model: string;
     choices: Choice[];
     usage: TokenUsage;
     finishReason: 'stop' | 'length' | 'tool_calls' | 'content_filter' | 'function_call';
     systemFingerprint?: string;
   }

   interface MessageChunk {
     id: string;
     object: 'chat.completion.chunk';
     created: number;
     model: string;
     choices: DeltaChoice[];
     usage?: TokenUsage;
     delta?: {
       role?: string;
       content?: string;
       toolCalls?: ToolCall[];
     };
   }

   interface Choice {
     index: number;
     message: Message;
     finishReason: string;
   }

   interface DeltaChoice {
     index: number;
     delta: {
       role?: string;
       content?: string;
       toolCalls?: ToolCall[];
     };
     finishReason?: string;
   }
   ```

4. **Supporting Types**:

   ```typescript
   interface TokenUsage {
     promptTokens: number;
     completionTokens: number;
     totalTokens: number;
   }

   interface Tool {
     type: 'function';
     function: {
       name: string;
       description?: string;
       parameters?: Record<string, unknown>;
     };
   }

   interface ToolCall {
     id: string;
     type: 'function';
     function: {
       name: string;
       arguments: string;
     };
   }
   ```

## Technical Requirements

### TypeScript Configuration

- Use TypeScript 5.7+ strict mode
- All interfaces must be properly exported
- Include comprehensive JSDoc comments
- Use generic types where appropriate for flexibility

### Interface Design Principles

1. **Dependency Inversion**: High-level modules should not depend on low-level modules
2. **Interface Segregation**: Clients should not be forced to depend on unused methods
3. **Liskov Substitution**: Implementations must be substitutable for the interface
4. **Open/Closed**: Interface should be open for extension but closed for modification

### Performance Considerations

- Streaming responses should use `AsyncIterable` for memory efficiency
- Large payloads should support chunked transfer
- Connection pooling should be considered for provider implementations

### Security Requirements

- API keys and sensitive configuration must not be exposed in interface methods
- All inputs should be validated at the implementation level
- Error messages must not leak sensitive information

## Testing Strategy

### Unit Tests

- Test interface compliance with mock implementations
- Verify all method signatures match expected contracts
- Test type safety with TypeScript compiler

### Integration Tests

- Test interface with real provider implementations
- Verify streaming functionality works correctly
- Test error handling across different scenarios

### Test Coverage Requirements

- 100% interface coverage (all methods must have tests)
- Edge cases for all parameters
- Error condition testing

## Dependencies

### Internal Dependencies

- `@tamma/shared` - Shared types and utilities
- `@tamma/observability` - Logging and monitoring

### External Dependencies

- TypeScript 5.7+ - Interface definitions and type safety
- No runtime dependencies for interface definitions

## Deliverables

1. **Core Interface File**: `packages/providers/src/interfaces/IAIProvider.ts`
2. **Capabilities Types**: `packages/providers/src/types/ProviderCapabilities.ts`
3. **Message Models**: `packages/providers/src/types/MessageModels.ts`
4. **Supporting Types**: `packages/providers/src/types/index.ts` (barrel export)
5. **Unit Tests**: `packages/providers/src/interfaces/__tests__/IAIProvider.test.ts`
6. **Type Tests**: `packages/providers/src/interfaces/__tests__/type-tests.test.ts`

## Acceptance Criteria Verification

- [ ] All core methods defined with proper signatures
- [ ] Provider capabilities structure comprehensive and accurate
- [ ] Request/response models support all required features
- [ ] TypeScript compilation passes with strict mode
- [ ] All interfaces properly documented with JSDoc
- [ ] Unit tests achieve 100% coverage
- [ ] Integration tests validate interface contracts

## Implementation Notes

### File Organization

```
packages/providers/src/
├── interfaces/
│   ├── IAIProvider.ts
│   └── __tests__/
│       ├── IAIProvider.test.ts
│       └── type-tests.test.ts
├── types/
│   ├── ProviderCapabilities.ts
│   ├── MessageModels.ts
│   ├── index.ts
│   └── __tests__/
│       └── types.test.ts
```

### Naming Conventions

- Interfaces: `I` prefix (e.g., `IAIProvider`)
- Types: PascalCase (e.g., `MessageRequest`)
- Enums: PascalCase (e.g., `ProviderType`)
- Constants: SCREAMING_SNAKE_CASE

### Version Compatibility

- Interface must support multiple provider API versions
- Backward compatibility should be maintained where possible
- Version information should be included in capabilities

## Next Steps

After completing this task:

1. Move to Task 2: Implement Provider Registry
2. Use the defined interfaces in the registry implementation
3. Ensure all future provider implementations comply with these interfaces

## Risk Mitigation

### Technical Risks

- **Interface Too Restrictive**: Design interfaces to be flexible enough for different provider APIs
- **Future Compatibility**: Include version fields and extension mechanisms
- **Performance Impact**: Design streaming interfaces to be memory-efficient

### Mitigation Strategies

- Regular interface reviews with provider implementation teams
- Comprehensive testing with multiple provider types
- Performance benchmarking during implementation
