# Task 7: Implement Mock Provider

**Story**: 2.1 - AI Provider Abstraction Interface  
**Acceptance Criteria**: 7 - Mock provider for testing and development  
**Status**: Ready for Development

## Overview

This task involves implementing a comprehensive mock provider that simulates AI provider behavior for testing, development, and demonstration purposes. The mock provider should support configurable responses, various scenarios, and realistic behavior patterns without requiring actual API calls.

## Subtasks

### Subtask 7.1: Create Mock Provider Class

**Objective**: Implement the core mock provider class that fulfills the IAIProvider interface.

**Implementation Details**:

1. **File Location**: `packages/providers/src/mock/MockProvider.ts`

2. **Mock Provider Implementation**:

   ```typescript
   import {
     IAIProvider,
     ProviderConfig,
     MessageRequest,
     MessageResponse,
     MessageChunk,
     ProviderCapabilities,
     ModelInfo,
     ProviderHealthStatus,
   } from '../types';
   import { EventEmitter } from 'events';

   export class MockProvider extends EventEmitter implements IAIProvider {
     private config: MockProviderConfig;
     private initialized: boolean = false;
     private responseGenerator: ResponseGenerator;
     private latencySimulator: LatencySimulator;
     private errorSimulator: ErrorSimulator;
     private requestHistory: MockRequest[] = [];

     constructor(config: MockProviderConfig = {}) {
       super();
       this.config = this.mergeWithDefaults(config);
       this.responseGenerator = new ResponseGenerator(this.config.responses);
       this.latencySimulator = new LatencySimulator(this.config.latency);
       this.errorSimulator = new ErrorSimulator(this.config.errors);
     }

     async initialize(config: ProviderConfig): Promise<void> {
       this.config = { ...this.config, ...config };

       // Simulate initialization delay
       await this.latencySimulator.simulate('initialization');

       // Simulate initialization errors if configured
       if (this.errorSimulator.shouldSimulateError('initialization')) {
         throw this.errorSimulator.createError('initialization');
       }

       this.initialized = true;
       this.emit('initialized', { config: this.config });
     }

     async sendMessage(
       request: MessageRequest,
       options?: StreamOptions
     ): Promise<AsyncIterable<MessageChunk>> {
       this.ensureInitialized();

       const mockRequest: MockRequest = {
         id: this.generateRequestId(),
         timestamp: new Date().toISOString(),
         request,
         options,
       };

       this.requestHistory.push(mockRequest);
       this.emit('request', mockRequest);

       // Simulate errors if configured
       if (this.errorSimulator.shouldSimulateError('request')) {
         throw this.errorSimulator.createError('request');
       }

       if (request.stream) {
         return this.generateStreamingResponse(mockRequest);
       } else {
         return this.generateNonStreamingResponse(mockRequest);
       }
     }

     async sendMessageSync(request: MessageRequest): Promise<MessageResponse> {
       this.ensureInitialized();

       const mockRequest: MockRequest = {
         id: this.generateRequestId(),
         timestamp: new Date().toISOString(),
         request,
       };

       this.requestHistory.push(mockRequest);
       this.emit('request', mockRequest);

       // Simulate errors if configured
       if (this.errorSimulator.shouldSimulateError('request')) {
         throw this.errorSimulator.createError('request');
       }

       // Simulate network latency
       await this.latencySimulator.simulate('response');

       const response = this.responseGenerator.generateResponse(mockRequest);

       this.emit('response', { request: mockRequest, response });

       return response;
     }

     getCapabilities(): ProviderCapabilities {
       return {
         providerInfo: {
           name: 'mock',
           displayName: 'Mock Provider',
           version: '1.0.0',
           providerType: 'mock',
           apiVersion: 'v1',
           documentationUrl: 'https://tamma.ai/docs/mock-provider',
         },
         features: {
           textGeneration: {
             supported: true,
             maxTokens: this.config.maxTokens || 4096,
             maxContextLength: this.config.maxContextLength || 8192,
             supportedFormats: ['text'],
             languages: ['en'],
             styles: ['conversational', 'formal', 'casual'],
           },
           codeGeneration: {
             supported: this.config.enableCodeGeneration !== false,
             languages: ['javascript', 'typescript', 'python', 'java', 'cpp'],
             frameworks: ['react', 'express', 'fastapi', 'spring'],
             maxCodeLength: 10000,
             syntaxHighlighting: true,
             codeCompletion: true,
             codeExplanation: true,
             codeRefactoring: true,
             testGeneration: true,
           },
           conversation: {
             supported: true,
             maxTurns: 100,
             maxConversationLength: this.config.maxContextLength || 8192,
             memoryPersistence: true,
             contextWindow: this.config.maxContextLength || 8192,
             systemMessages: true,
             conversationHistory: true,
           },
           functionCalling: {
             supported: this.config.enableFunctionCalling !== false,
             maxFunctions: 10,
             maxParameters: 50,
             parallelCalls: true,
             recursiveCalls: false,
             parameterTypes: ['string', 'number', 'boolean', 'object', 'array'],
             returnTypes: ['string', 'number', 'boolean', 'object', 'array'],
             validation: true,
           },
           toolUse: {
             supported: this.config.enableToolUse !== false,
             maxTools: 20,
             toolTypes: ['function'],
             parallelExecution: true,
             customTools: true,
             toolChaining: true,
           },
           reasoning: {
             supported: true,
             chainOfThought: true,
             stepByStep: true,
             logicalReasoning: true,
             mathematicalReasoning: true,
             causalReasoning: true,
           },
           streaming: {
             supported: true,
             chunkSize: this.config.chunkSize || 100,
             latency: this.config.streamingLatency || 50,
             reliability: 0.99,
             supportedFormats: ['text'],
             maxStreamDuration: 300000,
             concurrentStreams: 10,
           },
           imageProcessing: {
             supported: this.config.enableImageProcessing === true,
             formats: ['png', 'jpeg', 'gif', 'webp'],
             maxSize: 10485760, // 10MB
             analysis: true,
             generation: false,
           },
           audioProcessing: {
             supported: this.config.enableAudioProcessing === true,
             formats: ['mp3', 'wav', 'ogg'],
             maxSize: 52428800, // 50MB
             transcription: true,
             generation: false,
           },
           videoProcessing: {
             supported: false,
             formats: [],
             maxSize: 0,
             analysis: false,
             generation: false,
           },
           documentProcessing: {
             supported: this.config.enableDocumentProcessing === true,
             formats: ['pdf', 'docx', 'txt', 'md'],
             maxSize: 20971520, // 20MB
             extraction: true,
             analysis: true,
           },
           batchProcessing: {
             supported: true,
             maxBatchSize: 100,
             maxBatchTokens: 1000000,
             parallelProcessing: true,
           },
           fineTuning: {
             supported: false,
             maxTrainingData: 0,
             supportedModels: [],
           },
           embeddings: {
             supported: this.config.enableEmbeddings === true,
             dimensions: 1536,
             maxTokens: 8192,
             models: ['mock-embedding-model'],
           },
           moderation: {
             supported: this.config.enableModeration === true,
             categories: ['harassment', 'hate', 'violence', 'self-harm', 'sexual'],
             confidenceThreshold: 0.5,
           },
           debugging: {
             supported: true,
             stepThrough: true,
             breakpoints: true,
             variableInspection: true,
           },
           testing: {
             supported: true,
             unitTestGeneration: true,
             integrationTestGeneration: true,
             testExecution: false,
           },
           documentation: {
             supported: true,
             codeDocumentation: true,
             apiDocumentation: true,
             readmeGeneration: true,
           },
         },
         languages: {
           naturalLanguages: {
             supported: ['en', 'es', 'fr', 'de', 'it', 'pt', 'ru', 'ja', 'ko', 'zh'],
             primary: ['en'],
             translation: true,
             detection: true,
             proficiency: {
               en: LanguageProficiency.NATIVE,
               es: LanguageProficiency.ADVANCED,
               fr: LanguageProficiency.ADVANCED,
               de: LanguageProficiency.INTERMEDIATE,
               it: LanguageProficiency.INTERMEDIATE,
               pt: LanguageProficiency.ADVANCED,
               ru: LanguageProficiency.INTERMEDIATE,
               ja: LanguageProficiency.INTERMEDIATE,
               ko: LanguageProficiency.INTERMEDIATE,
               zh: LanguageProficiency.ADVANCED,
             },
           },
           programmingLanguages: {
             supported: [
               'javascript',
               'typescript',
               'python',
               'java',
               'cpp',
               'csharp',
               'go',
               'rust',
               'php',
               'ruby',
             ],
             primary: ['javascript', 'typescript', 'python'],
             syntaxUnderstanding: {
               javascript: true,
               typescript: true,
               python: true,
               java: true,
               cpp: true,
               csharp: true,
               go: true,
               rust: true,
               php: true,
               ruby: true,
             },
             codeGeneration: {
               javascript: true,
               typescript: true,
               python: true,
               java: true,
               cpp: true,
               csharp: true,
               go: true,
               rust: true,
               php: true,
               ruby: true,
             },
             codeAnalysis: {
               javascript: true,
               typescript: true,
               python: true,
               java: true,
               cpp: true,
               csharp: true,
               go: true,
               rust: true,
               php: true,
               ruby: true,
             },
             frameworks: {
               javascript: ['react', 'vue', 'angular', 'express', 'koa'],
               typescript: ['react', 'vue', 'angular', 'express', 'nest'],
               python: ['django', 'flask', 'fastapi', 'pandas', 'numpy'],
               java: ['spring', 'spring-boot', 'maven', 'gradle'],
               cpp: ['boost', 'qt', 'stl'],
               csharp: ['.net', 'asp.net', 'entity-framework'],
               go: ['gin', 'echo', 'chi'],
               rust: ['tokio', 'serde', 'rocket'],
               php: ['laravel', 'symfony', 'composer'],
               ruby: ['rails', 'sinatra', 'rspec'],
             },
           },
           markupLanguages: {
             supported: ['html', 'markdown', 'latex', 'asciidoc'],
             rendering: true,
             validation: true,
             conversion: true,
           },
           dataLanguages: {
             supported: ['sql', 'json', 'xml', 'yaml', 'csv'],
             querying: true,
             transformation: true,
             validation: true,
             analysis: true,
           },
         },
         models: {
           availableModels: [
             {
               id: 'mock-gpt-4',
               name: 'Mock GPT-4',
               displayName: 'Mock GPT-4',
               category: 'text-generation',
               version: '1.0.0',
               status: 'available',
               capabilities: {
                 maxTokens: 8192,
                 maxContextLength: 8192,
                 supportedLanguages: ['en'],
                 features: ['text-generation', 'code-generation', 'function-calling'],
                 inputTypes: ['text'],
                 outputTypes: ['text'],
               },
               limits: {
                 maxInputTokens: 8192,
                 maxOutputTokens: 4096,
                 maxRequestsPerMinute: 60,
                 maxTokensPerMinute: 150000,
                 maxConcurrentRequests: 5,
               },
               pricing: {
                 inputTokenPrice: 0.00003,
                 outputTokenPrice: 0.00006,
                 currency: 'USD',
                 billingUnit: 'tokens',
               },
             },
             {
               id: 'mock-code-model',
               name: 'Mock Code Model',
               displayName: 'Mock Code Model',
               category: 'code-generation',
               version: '1.0.0',
               status: 'available',
               capabilities: {
                 maxTokens: 16384,
                 maxContextLength: 16384,
                 supportedLanguages: ['javascript', 'typescript', 'python', 'java'],
                 features: ['code-generation', 'code-analysis', 'test-generation'],
                 inputTypes: ['text', 'code'],
                 outputTypes: ['text', 'code'],
               },
               limits: {
                 maxInputTokens: 16384,
                 maxOutputTokens: 8192,
                 maxRequestsPerMinute: 40,
                 maxTokensPerMinute: 200000,
                 maxConcurrentRequests: 3,
               },
               pricing: {
                 inputTokenPrice: 0.00001,
                 outputTokenPrice: 0.00002,
                 currency: 'USD',
                 billingUnit: 'tokens',
               },
             },
           ],
           defaultModel: 'mock-gpt-4',
           modelCategories: ['text-generation', 'code-generation'],
           modelSelection: {
             autoSelection: true,
             capabilityBased: true,
             costOptimized: true,
             performanceOptimized: true,
           },
           modelSwitching: true,
           customModels: false,
           modelVersioning: true,
         },
         io: {
           inputTypes: ['text', 'image', 'audio', 'document'],
           outputTypes: ['text', 'code', 'json'],
           maxInputSize: 104857600, // 100MB
           maxOutputSize: 10485760, // 10MB
           supportedFormats: {
             text: ['plain', 'markdown', 'html'],
             image: ['png', 'jpeg', 'gif', 'webp'],
             audio: ['mp3', 'wav', 'ogg'],
             document: ['pdf', 'docx', 'txt', 'md'],
           },
         },
         performance: {
           responseTime: {
             min: 100,
             max: 5000,
             average: 1000,
             p95: 2000,
             p99: 3000,
           },
           throughput: {
             requestsPerSecond: 10,
             tokensPerSecond: 1000,
             concurrentRequests: 5,
           },
           reliability: {
             uptime: 0.999,
             errorRate: 0.001,
             timeoutRate: 0.0005,
           },
         },
         limits: {
           requests: {
             perMinute: 60,
             perHour: 3600,
             perDay: 86400,
           },
           tokens: {
             perMinute: 150000,
             perHour: 9000000,
             perDay: 216000000,
           },
           storage: {
             maxConversationHistory: 1000,
             maxCacheSize: 104857600, // 100MB
           },
         },
         pricing: {
           model: 'per_token',
           currency: 'USD',
           billingUnit: 'tokens',
           models: {
             'mock-gpt-4': {
               inputTokenPrice: 0.00003,
               outputTokenPrice: 0.00006,
             },
             'mock-code-model': {
               inputTokenPrice: 0.00001,
               outputTokenPrice: 0.00002,
             },
           },
           discounts: [],
           freeQuota: {
             tokensPerMonth: 10000,
             requestsPerMonth: 100,
           },
         },
         security: {
           authentication: {
             methods: ['api_key', 'oauth'],
             encryption: ['tls-1.2', 'tls-1.3'],
             keyRotation: true,
           },
           dataPrivacy: {
             dataRetention: '30_days',
             dataDeletion: true,
             anonymization: true,
             gdprCompliant: true,
           },
           contentModeration: {
             enabled: this.config.enableModeration !== false,
             categories: ['harassment', 'hate', 'violence', 'self-harm', 'sexual'],
             autoFlag: true,
             customRules: true,
           },
         },
       };
     }

     async getModels(): Promise<ModelInfo[]> {
       this.ensureInitialized();

       // Simulate API delay
       await this.latencySimulator.simulate('model-list');

       return this.getCapabilities().models.availableModels;
     }

     async healthCheck(): Promise<ProviderHealthStatus> {
       const now = Date.now();
       const uptime = now - (this.config.startTime || now);

       return {
         status: 'healthy',
         timestamp: new Date().toISOString(),
         uptime,
         version: '1.0.0',
         metrics: {
           requestsTotal: this.requestHistory.length,
           requestsSuccessful: this.requestHistory.filter((r) => !r.error).length,
           requestsFailed: this.requestHistory.filter((r) => r.error).length,
           averageResponseTime: this.calculateAverageResponseTime(),
           lastRequestTime: this.getLastRequestTime(),
         },
         checks: {
           api: { status: 'pass', message: 'API responding normally' },
           database: { status: 'pass', message: 'Database connected' },
           authentication: { status: 'pass', message: 'Authentication working' },
         },
       };
     }

     async dispose(): Promise<void> {
       this.initialized = false;
       this.requestHistory = [];
       this.removeAllListeners();
       this.emit('disposed');
     }

     // Mock-specific methods
     getRequestHistory(): MockRequest[] {
       return [...this.requestHistory];
     }

     clearHistory(): void {
       this.requestHistory = [];
     }

     updateConfig(config: Partial<MockProviderConfig>): void {
       this.config = { ...this.config, ...config };
       this.responseGenerator = new ResponseGenerator(this.config.responses);
       this.latencySimulator = new LatencySimulator(this.config.latency);
       this.errorSimulator = new ErrorSimulator(this.config.errors);
     }

     // Private helper methods
     private ensureInitialized(): void {
       if (!this.initialized) {
         throw new Error('Mock provider not initialized. Call initialize() first.');
       }
     }

     private async generateStreamingResponse(
       request: MockRequest
     ): Promise<AsyncIterable<MessageChunk>> {
       const response = this.responseGenerator.generateResponse(request);
       const chunks = this.responseGenerator.chunkResponse(response, this.config.chunkSize || 100);

       return this.createAsyncIterable(chunks, request);
     }

     private async generateNonStreamingResponse(
       request: MockRequest
     ): Promise<AsyncIterable<MessageChunk>> {
       const response = this.responseGenerator.generateResponse(request);
       const chunk = this.responseGenerator.createChunk(response, 0, true);

       return this.createAsyncIterable([chunk], request);
     }

     private async *createAsyncIterable(
       chunks: MessageChunk[],
       request: MockRequest
     ): AsyncIterable<MessageChunk> {
       for (let i = 0; i < chunks.length; i++) {
         const chunk = chunks[i];

         // Simulate streaming latency
         if (i > 0) {
           await this.latencySimulator.simulate('streaming');
         }

         // Simulate streaming errors
         if (this.errorSimulator.shouldSimulateError('streaming')) {
           throw this.errorSimulator.createError('streaming');
         }

         yield chunk;

         this.emit('chunk', { request, chunk, index: i });
       }
     }

     private generateRequestId(): string {
       return `mock-req-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`;
     }

     private calculateAverageResponseTime(): number {
       if (this.requestHistory.length === 0) return 0;

       const totalTime = this.requestHistory.reduce((sum, req) => sum + (req.responseTime || 0), 0);
       return totalTime / this.requestHistory.length;
     }

     private getLastRequestTime(): string | null {
       if (this.requestHistory.length === 0) return null;

       const lastRequest = this.requestHistory[this.requestHistory.length - 1];
       return lastRequest.timestamp;
     }

     private mergeWithDefaults(config: MockProviderConfig): MockProviderConfig {
       return {
         maxTokens: 4096,
         maxContextLength: 8192,
         chunkSize: 100,
         streamingLatency: 50,
         enableCodeGeneration: true,
         enableFunctionCalling: true,
         enableToolUse: true,
         enableImageProcessing: false,
         enableAudioProcessing: false,
         enableDocumentProcessing: false,
         enableEmbeddings: false,
         enableModeration: false,
         startTime: Date.now(),
         latency: {
           min: 100,
           max: 2000,
           average: 500,
           distribution: 'normal',
         },
         responses: {
           mode: 'intelligent',
           templates: {},
           customResponses: {},
         },
         errors: {
           simulationMode: 'none',
           errorRate: 0,
           errorTypes: [],
           scenarios: {},
         },
         ...config,
       };
     }
   }

   export interface MockProviderConfig {
     maxTokens?: number;
     maxContextLength?: number;
     chunkSize?: number;
     streamingLatency?: number;
     enableCodeGeneration?: boolean;
     enableFunctionCalling?: boolean;
     enableToolUse?: boolean;
     enableImageProcessing?: boolean;
     enableAudioProcessing?: boolean;
     enableDocumentProcessing?: boolean;
     enableEmbeddings?: boolean;
     enableModeration?: boolean;
     startTime?: number;
     latency?: LatencyConfig;
     responses?: ResponseConfig;
     errors?: ErrorConfig;
   }

   export interface MockRequest {
     id: string;
     timestamp: string;
     request: MessageRequest;
     options?: StreamOptions;
     response?: MessageResponse;
     responseTime?: number;
     error?: Error;
   }

   export interface LatencyConfig {
     min: number;
     max: number;
     average: number;
     distribution: 'normal' | 'uniform' | 'exponential';
   }

   export interface ResponseConfig {
     mode: 'template' | 'intelligent' | 'recorded' | 'custom';
     templates?: Record<string, string>;
     customResponses?: Record<string, MessageResponse>;
     recordedResponses?: MessageResponse[];
   }

   export interface ErrorConfig {
     simulationMode: 'none' | 'random' | 'systematic' | 'scenario';
     errorRate: number;
     errorTypes: string[];
     scenarios?: Record<string, ErrorScenario>;
   }

   export interface ErrorScenario {
     trigger: string | ((request: MessageRequest) => boolean);
     error: Error;
     probability: number;
   }
   ```

### Subtask 7.2: Implement Mock Response Generation

**Objective**: Create intelligent response generation that simulates realistic AI behavior.

**Implementation Details**:

1. **File Location**: `packages/providers/src/mock/ResponseGenerator.ts`

2. **Response Generator Implementation**:

   ```typescript
   export class ResponseGenerator {
     private config: ResponseConfig;
     private templateEngine: TemplateEngine;
     private intelligentGenerator: IntelligentGenerator;
     private recordedResponses: RecordedResponseManager;

     constructor(config: ResponseConfig) {
       this.config = config;
       this.templateEngine = new TemplateEngine(config.templates || {});
       this.intelligentGenerator = new IntelligentGenerator();
       this.recordedResponses = new RecordedResponseManager(config.recordedResponses || []);
     }

     generateResponse(request: MockRequest): MessageResponse {
       switch (this.config.mode) {
         case 'template':
           return this.generateFromTemplate(request);
         case 'intelligent':
           return this.generateIntelligent(request);
         case 'recorded':
           return this.generateFromRecording(request);
         case 'custom':
           return this.generateCustom(request);
         default:
           return this.generateIntelligent(request);
       }
     }

     chunkResponse(response: MessageResponse, chunkSize: number): MessageChunk[] {
       const content = response.choices[0]?.message.content || '';
       const chunks: MessageChunk[] = [];

       if (content.length === 0) {
         return [this.createChunk(response, 0, true)];
       }

       const words = content.split(' ');
       let currentChunk = '';
       let chunkIndex = 0;

       for (let i = 0; i < words.length; i++) {
         const word = words[i];
         const testChunk = currentChunk ? `${currentChunk} ${word}` : word;

         if (testChunk.length <= chunkSize || i === words.length - 1) {
           currentChunk = testChunk;
         } else {
           // Create chunk from accumulated content
           chunks.push(this.createChunkFromContent(currentChunk, chunkIndex, false, response));
           currentChunk = word;
           chunkIndex++;
         }
       }

       // Add final chunk if there's remaining content
       if (currentChunk) {
         chunks.push(this.createChunkFromContent(currentChunk, chunkIndex, true, response));
       }

       return chunks;
     }

     createChunk(response: MessageResponse, index: number, isFinal: boolean): MessageChunk {
       return {
         id: response.id,
         object: 'chat.completion.chunk',
         created: response.created,
         model: response.model,
         choices: [{
           index,
           delta: {
             role: response.choices[0]?.message.role,
             content: response.choices[0]?.message.content
           },
           finishReason: isFinal ? response.choices[0]?.finishReason : undefined
         }],
         usage: isFinal ? response.usage : undefined
       };
     }

     private createChunkFromContent(content: string, index: number, isFinal: boolean, originalResponse: MessageResponse): MessageChunk {
       return {
         id: originalResponse.id,
         object: 'chat.completion.chunk',
         created: originalResponse.created,
         model: originalResponse.model,
         choices: [{
           index,
           delta: {
             role: index === 0 ? 'assistant' : undefined,
             content: content
           },
           finishReason: isFinal ? originalResponse.choices[0]?.finishReason : undefined
         }],
         usage: isFinal ? originalResponse.usage : undefined
       };
     }

     private generateFromTemplate(request: MockRequest): MessageResponse {
       const template = this.selectTemplate(request);
       const content = this.templateEngine.render(template, request);

       return this.createResponse(request, content);
     }

     private generateIntelligent(request: MockRequest): MessageResponse {
       const content = this.intelligentGenerator.generate(request);

       return this.createResponse(request, content);
     }

     private generateFromRecording(request: MockRequest): MessageResponse {
       const recording = this.recordedResponses.findMatching(request);

       if (recording) {
         return recording;
       }

       // Fallback to intelligent generation
       return this.generateIntelligent(request);
     }

     private generateCustom(request: MockRequest): MessageResponse {
       const customKey = this.generateCustomKey(request);
       const customResponse = this.config.customResponses?.[customKey];

       if (customResponse) {
         return customResponse;
       }

       // Fallback to intelligent generation
       return this.generateIntelligent(request);
     }

     private selectTemplate(request: MockRequest): string {
       const lastMessage = request.request.messages[request.request.messages.length - 1];
       const content = lastMessage?.content || '';

       // Check for specific patterns
       if (this.isCodeRequest(content)) {
         return this.config.templates?.['code'] || this.getDefaultCodeTemplate();
       }

       if (this.isQuestionRequest(content)) {
         return this.config.templates?.['question'] || this.getDefaultQuestionTemplate();
       }

       if (this.isCreativeRequest(content)) {
         return this.config.templates?.['creative'] || this.getDefaultCreativeTemplate();
       }

       // Default template
       return this.config.templates?.['default'] || this.getDefaultTemplate();
     }

     private isCodeRequest(content: string): boolean {
       const codeKeywords = ['function', 'class', 'def ', 'import ', 'const ', 'let ', 'var ', 'write code', 'create function', 'implement'];
       return codeKeywords.some(keyword => content.toLowerCase().includes(keyword));
     }

     private isQuestionRequest(content: string): boolean {
       return content.includes('?') || content.toLowerCase().startsWith('what') ||
              content.toLowerCase().startsWith('how') || content.toLowerCase().startsWith('why');
     }

     private isCreativeRequest(content: string): boolean {
       const creativeKeywords = ['write a story', 'create a poem', 'imagine', 'creative', 'brainstorm'];
       return creativeKeywords.some(keyword => content.toLowerCase().includes(keyword));
     }

     private generateCustomKey(request: MockRequest): string {
       const lastMessage = request.request.messages[request.request.messages.length - 1];
       return JSON.stringify({
         content: lastMessage?.content,
         tools: request.request.tools?.map(t => t.function.name),
         model: request.request.model
       });
     }

     private createResponse(request: MockRequest, content: string): MessageResponse {
       const id = this.generateResponseId();
       const created = Math.floor(Date.now() / 1000);
       const model = request.request.model || 'mock-gpt-4';

       // Handle function calling
       let toolCalls: ToolCall[] | undefined;
       if (request.request.tools && request.request.toolChoice !== 'none') {
         toolCalls = this.generateToolCalls(request);
       }

       const message: Message = {
         role: 'assistant',
         content: toolCalls ? undefined : content,
         toolCalls
       };

       const usage = this.calculateUsage(request, content);

       return {
         id,
         object: 'chat.completion',
         created,
         model,
         choices: [{
           index: 0,
           message,
           finishReason: toolCalls ? 'tool_calls' : 'stop'
         }],
         usage
       };
     }

     private generateToolCalls(request: MockRequest): ToolCall[] {
       const tools = request.request.tools || [];
       const toolChoice = request.request.toolChoice;

       if (toolChoice === 'none') {
         return [];
       }

       if (toolChoice && typeof toolChoice === 'object' && toolChoice.type === 'function') {
         // Specific function requested
         const tool = tools.find(t => t.function.name === toolChoice.function.name);
         if (tool) {
           return [this.createToolCall(tool)];
         }
       }

       // Auto-select tools based on context
       const selectedTools = tools.slice(0, Math.min(3, tools.length)); // Max 3 tools
       return selectedTools.map(tool => this.createToolCall(tool));
     }

     private createToolCall(tool: Tool): ToolCall {
       return {
         id: `call_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`,
         type: 'function',
         function: {
           name: tool.function.name,
           arguments: this.generateFunctionArguments(tool.function)
         }
       };
     }

     private generateFunctionArguments(functionDef: ToolFunction): string {
       const params: Record<string, any> = {};

       if (functionDef.properties) {
         for (const [key, schema] of Object.entries(functionDef.properties)) {
           params[key] = this.generateParameterValue(schema);
         }
       }

       return JSON.stringify(params);
     }

     private generateParameterValue(schema: any): any {
       switch (schema.type) {
         case 'string':
           if (schema.enum) {
             return schema.enum[Math.floor(Math.random() * schema.enum.length)];
           }
           return schema.example || 'sample value';
         case 'number':
           return schema.example || Math.floor(Math.random() * 100);
         case 'boolean':
           return schema.example || Math.random() > 0.5;
         case 'array':
           return schema.example || [this.generateParameterValue(schema.items || { type: 'string' })];
         case 'object':
           return schema.example || {};
         default:
           return null;
       }
     }

     private calculateUsage(request: MockRequest, responseContent: string): TokenUsage {
       const promptTokens = this.estimateTokens(JSON.stringify(request.request));
       const completionTokens = this.estimateTokens(responseContent);

       return {
         promptTokens,
         completionTokens,
         totalTokens: promptTokens + completionTokens
       };
     }

     private estimateTokens(text: string): number {
       // Rough estimation: ~4 characters per token
       return Math.ceil(text.length / 4);
     }

     private generateResponseId(): string {
       return `chatcmpl-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`;
     }

     // Default templates
     private getDefaultTemplate(): string {
       return "I understand your request. Based on the context and your message, here's my response: {{request.messages[request.messages.length - 1].content}}";
     }

     private getDefaultCodeTemplate(): string {
       return `Here's the code you requested:
   ```

\`\`\`{{detectLanguage(request.messages[request.messages.length - 1].content)}}
{{generateCode(request.messages[request.messages.length - 1].content)}}
\`\`\`

This code implements the functionality you described. Let me know if you need any modifications or explanations.`;
}

     private getDefaultQuestionTemplate(): string {
       return "That's a great question. Based on my understanding, {{answerQuestion(request.messages[request.messages.length - 1].content)}}. Is there anything specific about this topic you'd like me to elaborate on?";
     }

     private getDefaultCreativeTemplate(): string {
       return "{{generateCreativeResponse(request.messages[request.messages.length - 1].content)}}";
     }

}

export class TemplateEngine {
private templates: Record<string, string>;

     constructor(templates: Record<string, string>) {
       this.templates = templates;
     }

     render(template: string, context: MockRequest): string {
       return template.replace(/\{\{([^}]+)\}\}/g, (match, expression) => {
         try {
           return this.evaluateExpression(expression, context);
         } catch (error) {
           return `[Error: ${error.message}]`;
         }
       });
     }

     private evaluateExpression(expression: string, context: MockRequest): string {
       // Simple template evaluation - in a real implementation, you might use a proper template engine
       if (expression.startsWith('request.')) {
         const path = expression.replace('request.', '');
         return this.getNestedValue(context.request, path) || '';
       }

       if (expression.includes('detectLanguage')) {
         return this.detectLanguage(context);
       }

       if (expression.includes('generateCode')) {
         return this.generateCode(context);
       }

       if (expression.includes('answerQuestion')) {
         return this.answerQuestion(context);
       }

       if (expression.includes('generateCreativeResponse')) {
         return this.generateCreativeResponse(context);
       }

       return expression;
     }

     private getNestedValue(obj: any, path: string): any {
       return path.split('.').reduce((current, key) => current?.[key], obj);
     }

     private detectLanguage(context: MockRequest): string {
       const content = context.request.messages[context.request.messages.length - 1]?.content || '';

       if (content.includes('javascript') || content.includes('const ') || content.includes('let ')) return 'javascript';
       if (content.includes('python') || content.includes('def ')) return 'python';
       if (content.includes('java') || content.includes('public class')) return 'java';
       if (content.includes('cpp') || content.includes('#include')) return 'cpp';

       return 'javascript'; // Default
     }

     private generateCode(context: MockRequest): string {
       const language = this.detectLanguage(context);
       const content = context.request.messages[context.request.messages.length - 1]?.content || '';

       // Simple code generation based on language and content
       switch (language) {
         case 'javascript':
           return `function example() {

// Generated JavaScript code
console.log("Hello, World!");
}`;
         case 'python':
           return `def example(): # Generated Python code
print("Hello, World!")`;
         case 'java':
           return `public class Example {
public static void main(String[] args) {
// Generated Java code
System.out.println("Hello, World!");
}
}`;
         default:
           return `// Generated code in ${language}
console.log("Hello, World!");`;
}
}

     private answerQuestion(context: MockRequest): string {
       const content = context.request.messages[context.request.messages.length - 1]?.content || '';

       // Simple question answering based on keywords
       if (content.toLowerCase().includes('what is')) {
         return "it refers to a concept or object that can be defined based on the context provided";
       }

       if (content.toLowerCase().includes('how to')) {
         return "you can achieve this by following a systematic approach with clear steps and best practices";
       }

       if (content.toLowerCase().includes('why')) {
         return "this occurs due to various factors that influence the outcome in specific ways";
       }

       return "this is an interesting topic that deserves careful consideration and analysis";
     }

     private generateCreativeResponse(context: MockRequest): string {
       const content = context.request.messages[context.request.messages.length - 1]?.content || '';

       if (content.includes('story')) {
         return "Once upon a time, in a world where technology and nature coexisted in perfect harmony, there lived a community that had discovered the secret to sustainable innovation. They understood that progress wasn't about replacing nature, but about working alongside it to create something beautiful and lasting.";
       }

       if (content.includes('poem')) {
         return "In circuits bright and code so clean,\nA digital world, a vibrant scene.\nWhere logic flows and ideas bloom,\nDispelling darkness, ending gloom.";
       }

       return "Imagine a place where creativity knows no bounds, where ideas flow like rivers and innovation springs from every corner. This is the realm of possibility, where dreams take shape and the impossible becomes reality.";
     }

}

export class IntelligentGenerator {
generate(request: MockRequest): string {
const lastMessage = request.request.messages[request.request.messages.length - 1];
const content = lastMessage?.content || '';

       // Analyze the request and generate appropriate response
       if (this.isCodeGenerationRequest(content)) {
         return this.generateCodeResponse(content);
       }

       if (this.isQuestionRequest(content)) {
         return this.generateQuestionResponse(content);
       }

       if (this.isCreativeRequest(content)) {
         return this.generateCreativeResponse(content);
       }

       if (this.isAnalysisRequest(content)) {
         return this.generateAnalysisResponse(content);
       }

       return this.generateGeneralResponse(content);
     }

     private isCodeGenerationRequest(content: string): boolean {
       const codePatterns = [
         /write\s+(a\s+)?function/i,
         /create\s+(a\s+)?class/i,
         /implement/i,
         /def\s+\w+/i,
         /function\s+\w+/i,
         /class\s+\w+/i
       ];

       return codePatterns.some(pattern => pattern.test(content));
     }

     private isQuestionRequest(content: string): boolean {
       return content.includes('?') || /^(what|how|why|when|where|who|which)/i.test(content);
     }

     private isCreativeRequest(content: string): boolean {
       const creativeKeywords = ['story', 'poem', 'creative', 'imagine', 'brainstorm', 'invent'];
       return creativeKeywords.some(keyword => content.toLowerCase().includes(keyword));
     }

     private isAnalysisRequest(content: string): boolean {
       const analysisKeywords = ['analyze', 'explain', 'review', 'evaluate', 'compare'];
       return analysisKeywords.some(keyword => content.toLowerCase().includes(keyword));
     }

     private generateCodeResponse(content: string): string {
       const language = this.detectLanguage(content);
       const functionName = this.extractFunctionName(content);

       switch (language) {
         case 'javascript':
           return `function ${functionName || 'example'}() {

// Implementation based on your request
console.log('${this.extractPurpose(content)}');
return true;
}

// Usage example
${functionName || 'example'}();`;

         case 'python':
           return `def ${functionName || 'example'}():
    """${this.extractPurpose(content)}"""
    # Implementation based on your request
    print("${this.extractPurpose(content)}")
    return True

# Usage example

${functionName || 'example'}()`;

         default:
           return `// Generated ${language} code

function ${functionName || 'example'}() {
// ${this.extractPurpose(content)}
return true;
}`;
}
}

     private generateQuestionResponse(content: string): string {
       if (content.toLowerCase().includes('what is')) {
         return "Based on the context, this refers to a concept or system that can be understood through its components and relationships. It typically involves specific characteristics and behaviors that define its nature and purpose.";
       }

       if (content.toLowerCase().includes('how to')) {
         return "To accomplish this, you should follow a structured approach: 1) Understand the requirements clearly, 2) Break down the problem into smaller components, 3) Implement each component systematically, 4) Test and validate your solution, and 5) Refine based on feedback.";
       }

       if (content.toLowerCase().includes('why')) {
         return "This occurs due to underlying principles and factors that influence the outcome. The reasons can be traced to fundamental mechanisms and interactions that produce the observed behavior or result.";
       }

       return "This is an interesting question that requires careful consideration of multiple factors and perspectives. The answer depends on the specific context and the particular aspects you're focusing on.";
     }

     private generateCreativeResponse(content: string): string {
       if (content.includes('story')) {
         return "In a not-so-distant future, where artificial intelligence had become humanity's creative partner, a young developer discovered something remarkable. The AI she was working with wasn't just processing code—it was understanding the poetry within it, finding beauty in algorithms, and suggesting innovations that blended technical excellence with artistic expression. Together, they created systems that didn't just work, but inspired.";
       }

       if (content.includes('poem')) {
         return "In silicon valleys where data streams flow,\nWhere algorithms learn and grow,\nA digital mind begins to dream,\nOf worlds beyond the screen's regime.\n\nLines of code like verses bright,\nIlluminating endless night,\nWhere human thought and machine meet,\nCreating futures, bittersweet.";
       }

       return "Imagine standing at the intersection of creativity and technology, where ideas take flight on digital wings. Here, innovation isn't just about solving problems—it's about painting possibilities, composing solutions, and architecting dreams into reality. This is where the future is born, one creative spark at a time.";
     }

     private generateAnalysisResponse(content: string): string {
       return "After careful analysis of the subject matter, several key observations emerge: First, there are underlying patterns that govern the behavior and characteristics. Second, the relationships between different components create a complex but coherent system. Third, there are opportunities for optimization and improvement that could enhance overall effectiveness. Finally, the context and environment play crucial roles in determining outcomes and success metrics.";
     }

     private generateGeneralResponse(content: string): string {
       return "I understand your request and I'm here to help. Based on what you've shared, I can provide assistance and guidance tailored to your specific needs. Feel free to ask for more details or clarification on any aspect, and I'll do my best to provide comprehensive and useful information.";
     }

     private detectLanguage(content: string): string {
       if (content.includes('javascript') || content.includes('const ') || content.includes('let ')) return 'javascript';
       if (content.includes('python') || content.includes('def ')) return 'python';
       if (content.includes('java') || content.includes('public class')) return 'java';
       if (content.includes('cpp') || content.includes('#include')) return 'cpp';
       if (content.includes('typescript') || content.includes(': string')) return 'typescript';

       return 'javascript';
     }

     private extractFunctionName(content: string): string {
       const match = content.match(/(?:function|def|class)\s+(\w+)/i);
       return match ? match[1] : '';
     }

     private extractPurpose(content: string): string {
       // Simple purpose extraction - in a real implementation, this would be more sophisticated
       if (content.includes('hello')) return 'Hello World implementation';
       if (content.includes('calculate')) return 'Calculation logic';
       if (content.includes('sort')) return 'Sorting algorithm';
       if (content.includes('search')) return 'Search functionality';

       return 'Custom implementation based on requirements';
     }

}

export class RecordedResponseManager {
private recordings: MessageResponse[];

     constructor(recordings: MessageResponse[]) {
       this.recordings = recordings;
     }

     findMatching(request: MockRequest): MessageResponse | null {
       // Simple matching based on request content
       const content = request.request.messages[request.request.messages.length - 1]?.content || '';

       for (const recording of this.recordings) {
         if (this.isMatch(recording, request)) {
           return recording;
         }
       }

       return null;
     }

     private isMatch(recording: MessageResponse, request: MockRequest): boolean {
       // Simple matching logic - in a real implementation, this would be more sophisticated
       const recordingContent = recording.choices[0]?.message.content || '';
       const requestContent = request.request.messages[request.request.messages.length - 1]?.content || '';

       // Check for keyword similarity
       const recordingWords = recordingContent.toLowerCase().split(' ');
       const requestWords = requestContent.toLowerCase().split(' ');

       const commonWords = recordingWords.filter(word => requestWords.includes(word));
       const similarity = commonWords.length / Math.max(recordingWords.length, requestWords.length);

       return similarity > 0.3; // 30% similarity threshold
     }

}

````

### Subtask 7.3: Add Mock Configuration Options

**Objective**: Provide comprehensive configuration options for customizing mock behavior.

**Implementation Details**:

1. **File Location**: `packages/providers/src/mock/MockProviderFactory.ts`

2. **Mock Provider Factory**:
```typescript
export class MockProviderFactory {
  static createDevelopmentProvider(): MockProvider {
    return new MockProvider({
      maxTokens: 4096,
      maxContextLength: 8192,
      enableCodeGeneration: true,
      enableFunctionCalling: true,
      enableToolUse: true,
      latency: {
        min: 50,
        max: 500,
        average: 200,
        distribution: 'normal'
      },
      responses: {
        mode: 'intelligent'
      },
      errors: {
        simulationMode: 'none'
      }
    });
  }

  static createTestingProvider(scenarios?: TestScenario[]): MockProvider {
    return new MockProvider({
      maxTokens: 2048,
      maxContextLength: 4096,
      enableCodeGeneration: true,
      enableFunctionCalling: true,
      enableToolUse: true,
      latency: {
        min: 10,
        max: 100,
        average: 30,
        distribution: 'uniform'
      },
      responses: {
        mode: 'template',
        templates: {
          'default': 'Test response for: {{request.messages[request.messages.length - 1].content}}',
          'code': '```javascript\n// Test code\nfunction test() { return "test"; }\n```',
          'question': 'Test answer to: {{request.messages[request.messages.length - 1].content}}'
        }
      },
      errors: {
        simulationMode: 'scenario',
        scenarios: this.createTestScenarios(scenarios)
      }
    });
  }

  static createDemoProvider(): MockProvider {
    return new MockProvider({
      maxTokens: 8192,
      maxContextLength: 16384,
      enableCodeGeneration: true,
      enableFunctionCalling: true,
      enableToolUse: true,
      enableImageProcessing: true,
      enableAudioProcessing: true,
      enableDocumentProcessing: true,
      enableEmbeddings: true,
      enableModeration: true,
      latency: {
        min: 200,
        max: 1000,
        average: 400,
        distribution: 'normal'
      },
      responses: {
        mode: 'intelligent'
      },
      errors: {
        simulationMode: 'none'
      }
    });
  }

  static createLoadTestingProvider(requestsPerSecond: number = 100): MockProvider {
    return new MockProvider({
      maxTokens: 1024,
      maxContextLength: 2048,
      enableCodeGeneration: false,
      enableFunctionCalling: false,
      enableToolUse: false,
      latency: {
        min: 5,
        max: 50,
        average: 20,
        distribution: 'exponential'
      },
      responses: {
        mode: 'template',
        templates: {
          'default': 'Load test response {{request.id}}'
        }
      },
      errors: {
        simulationMode: 'random',
        errorRate: 0.01, // 1% error rate
        errorTypes: ['timeout', 'rate_limit', 'server_error']
      }
    });
  }

  static createErrorTestingProvider(errorScenarios: ErrorScenario[]): MockProvider {
    return new MockProvider({
      maxTokens: 4096,
      maxContextLength: 8192,
      latency: {
        min: 100,
        max: 2000,
        average: 500,
        distribution: 'normal'
      },
      responses: {
        mode: 'intelligent'
      },
      errors: {
        simulationMode: 'scenario',
        scenarios: errorScenarios.reduce((acc, scenario) => {
          acc[scenario.name] = scenario;
          return acc;
        }, {} as Record<string, ErrorScenario>)
      }
    });
  }

  static createBenchmarkingProvider(): MockProvider {
    return new MockProvider({
      maxTokens: 4096,
      maxContextLength: 8192,
      enableCodeGeneration: true,
      enableFunctionCalling: true,
      enableToolUse: true,
      latency: {
        min: 100,
        max: 300,
        average: 150,
        distribution: 'normal'
      },
      responses: {
        mode: 'recorded',
        recordedResponses: this.getBenchmarkResponses()
      },
      errors: {
        simulationMode: 'none'
      }
    });
  }

  private static createTestScenarios(customScenarios?: TestScenario[]): Record<string, ErrorScenario> {
    const defaultScenarios: Record<string, ErrorScenario> = {
      'timeout': {
        trigger: (request) => request.messages.some(m => m.content.includes('timeout')),
        error: new Error('Request timeout'),
        probability: 1.0
      },
      'rate_limit': {
        trigger: (request) => request.messages.some(m => m.content.includes('rate limit')),
        error: new Error('Rate limit exceeded'),
        probability: 1.0
      },
      'auth_error': {
        trigger: (request) => request.messages.some(m => m.content.includes('auth')),
        error: new Error('Authentication failed'),
        probability: 1.0
      },
      'server_error': {
        trigger: (request) => request.messages.some(m => m.content.includes('server error')),
        error: new Error('Internal server error'),
        probability: 1.0
      }
    };

    if (customScenarios) {
      customScenarios.forEach(scenario => {
        defaultScenarios[scenario.name] = {
          trigger: scenario.trigger,
          error: scenario.error,
          probability: scenario.probability || 1.0
        };
      });
    }

    return defaultScenarios;
  }

  private static getBenchmarkResponses(): MessageResponse[] {
    return [
      // Code generation benchmark
      {
        id: 'bench-1',
        object: 'chat.completion',
        created: Date.now(),
        model: 'mock-gpt-4',
        choices: [{
          index: 0,
          message: {
            role: 'assistant',
            content: `function fibonacci(n) {
if (n <= 1) return n;
return fibonacci(n - 1) + fibonacci(n - 2);
}`
          },
          finishReason: 'stop'
        }],
        usage: {
          promptTokens: 50,
          completionTokens: 100,
          totalTokens: 150
        }
      },
      // Question answering benchmark
      {
        id: 'bench-2',
        object: 'chat.completion',
        created: Date.now(),
        model: 'mock-gpt-4',
        choices: [{
          index: 0,
          message: {
            role: 'assistant',
            content: 'The Fibonacci sequence is a series of numbers where each number is the sum of the two preceding ones, usually starting with 0 and 1.'
          },
          finishReason: 'stop'
        }],
        usage: {
          promptTokens: 30,
          completionTokens: 80,
          totalTokens: 110
        }
      }
    ];
  }
}

export interface TestScenario {
  name: string;
  trigger: string | ((request: MessageRequest) => boolean);
  error: Error;
  probability?: number;
}

// Predefined configurations for common use cases
export const MockProviderPresets = {
  development: {
    name: 'Development',
    description: 'Optimized for development with realistic responses and low latency',
    factory: () => MockProviderFactory.createDevelopmentProvider()
  },

  testing: {
    name: 'Testing',
    description: 'Optimized for automated testing with predictable responses',
    factory: () => MockProviderFactory.createTestingProvider()
  },

  demo: {
    name: 'Demo',
    description: 'Full-featured provider for demonstrations',
    factory: () => MockProviderFactory.createDemoProvider()
  },

  loadTest: {
    name: 'Load Testing',
    description: 'High-performance provider for load testing',
    factory: () => MockProviderFactory.createLoadTestingProvider()
  },

  benchmark: {
    name: 'Benchmarking',
    description: 'Consistent provider for performance benchmarking',
    factory: () => MockProviderFactory.createBenchmarkingProvider()
  }
};
````

## Technical Requirements

### Mock Provider Requirements

- Full IAIProvider interface compliance
- Configurable response generation modes
- Realistic latency simulation
- Comprehensive error simulation
- Request history tracking and analysis

### Response Generation Requirements

- Template-based responses for predictable testing
- Intelligent responses for realistic behavior
- Recorded responses for consistency
- Custom responses for specific scenarios

### Configuration Requirements

- Multiple preset configurations for common use cases
- Flexible configuration system
- Performance tuning options
- Error scenario configuration

## Testing Strategy

### Unit Tests

```typescript
describe('MockProvider', () => {
  describe('initialization', () => {
    it('should initialize with default config');
    it('should initialize with custom config');
    it('should simulate initialization errors');
  });

  describe('messaging', () => {
    it('should generate streaming responses');
    it('should generate non-streaming responses');
    it('should handle function calling');
    it('should simulate network latency');
  });

  describe('capabilities', () => {
    it('should return comprehensive capabilities');
    it('should reflect configuration in capabilities');
    it('should list available models');
  });
});
```

### Integration Tests

- Test mock provider with real client code
- Test configuration scenarios
- Test performance under load
- Test error simulation scenarios

### Performance Tests

- Measure response generation time
- Test memory usage with large request histories
- Benchmark streaming performance
- Test concurrent request handling

## Dependencies

### Internal Dependencies

- `@tamma/shared` - Shared utilities and types
- Task 1 output - IAIProvider interface
- Task 3 output - Message models

### External Dependencies

- TypeScript 5.7+ - Type safety
- Node.js EventEmitter - Event handling

## Deliverables

1. **Mock Provider**: `packages/providers/src/mock/MockProvider.ts`
2. **Response Generator**: `packages/providers/src/mock/ResponseGenerator.ts`
3. **Provider Factory**: `packages/providers/src/mock/MockProviderFactory.ts`
4. **Supporting Classes**: `packages/providers/src/mock/`
5. **Configuration Types**: `packages/providers/src/mock/types.ts`
6. **Unit Tests**: `packages/providers/src/mock/__tests__/`
7. **Integration Tests**: `packages/providers/src/mock/__integration__/`

## Acceptance Criteria Verification

- [ ] Mock provider implements full IAIProvider interface
- [ ] Response generation supports multiple modes
- [ ] Latency simulation is realistic and configurable
- [ ] Error simulation covers common scenarios
- [ ] Configuration options are comprehensive
- [ ] Request history tracking works correctly
- [ ] Performance is suitable for testing and development
- [ ] Integration with existing systems works seamlessly

## Implementation Notes

### Response Generation Strategy

Use a multi-tier approach:

1. **Template Mode**: Fast, predictable responses for testing
2. **Intelligent Mode**: Realistic responses for development
3. **Recorded Mode**: Consistent responses for benchmarking
4. **Custom Mode**: User-defined responses for specific scenarios

### Performance Optimization

- Lazy initialization of expensive components
- Efficient request history management
- Optimized response generation algorithms
- Memory-efficient streaming implementation

### Extensibility

- Plugin architecture for custom response generators
- Configurable latency and error simulators
- Modular template system
- Extensible scenario system

## Next Steps

After completing this task:

1. Move to Task 8: Create Plugin System
2. Integrate mock provider with testing frameworks
3. Add CLI tools for mock provider management

## Risk Mitigation

### Technical Risks

- **Performance Impact**: Ensure mock provider doesn't become a bottleneck
- **Memory Leaks**: Proper cleanup of request history and event listeners
- **Unrealistic Behavior**: Ensure mock behavior closely resembles real providers

### Mitigation Strategies

- Performance profiling and optimization
- Memory monitoring and cleanup
- Regular comparison with real provider behavior
- User feedback integration for realism improvements
