# Task 5: Advanced Features

**Story**: 2.2 - Anthropic Claude Provider Implementation  
**Status**: ready-for-dev  
**Priority**: Medium

## Overview

Implement advanced features for the Anthropic Claude provider, including function calling and tool use capabilities. These features enable the AI to interact with external systems, execute code, and perform complex operations beyond text generation.

## Detailed Implementation Plan

### Subtask 5.1: Implement Function Calling Support

**File**: `src/providers/anthropic/function-calling/function-manager.ts`

```typescript
import { logger } from '@tamma/observability';
import type { FunctionCall, FunctionResult, ToolDefinition } from '@tamma/shared/contracts';
import type { AnthropicTool, AnthropicContentBlock } from '../types/anthropic-types';
import { AnthropicError } from '../errors/anthropic-error';

export interface FunctionRegistry {
  register(name: string, fn: FunctionDefinition): void;
  unregister(name: string): void;
  get(name: string): FunctionDefinition | undefined;
  list(): string[];
  has(name: string): boolean;
}

export interface FunctionDefinition {
  name: string;
  description: string;
  parameters: {
    type: 'object';
    properties: Record<string, any>;
    required: string[];
  };
  handler: (args: Record<string, unknown>) => Promise<unknown>;
  timeout?: number;
  dangerous?: boolean;
  permissions?: string[];
}

export interface FunctionExecutionResult {
  name: string;
  success: boolean;
  result?: unknown;
  error?: string;
  executionTime: number;
  metadata: Record<string, unknown>;
}

export class FunctionManager implements FunctionRegistry {
  private readonly functions: Map<string, FunctionDefinition> = new Map();
  private readonly executionHistory: FunctionExecutionResult[] = [];
  private readonly maxHistorySize: number = 1000;

  constructor(options: { maxHistorySize?: number } = {}) {
    this.maxHistorySize = options.maxHistorySize || 1000;
  }

  register(name: string, definition: FunctionDefinition): void {
    if (this.functions.has(name)) {
      logger.warn('Overwriting existing function', { name });
    }

    // Validate function definition
    this.validateFunctionDefinition(definition);

    this.functions.set(name, definition);

    logger.info('Function registered', {
      name,
      description: definition.description,
      parameterCount: Object.keys(definition.parameters.properties).length,
      timeout: definition.timeout,
      dangerous: definition.dangerous,
    });
  }

  unregister(name: string): void {
    if (!this.functions.has(name)) {
      logger.warn('Attempted to unregister non-existent function', { name });
      return;
    }

    this.functions.delete(name);
    logger.info('Function unregistered', { name });
  }

  get(name: string): FunctionDefinition | undefined {
    return this.functions.get(name);
  }

  list(): string[] {
    return Array.from(this.functions.keys());
  }

  has(name: string): boolean {
    return this.functions.has(name);
  }

  async executeFunction(
    name: string,
    args: Record<string, unknown>,
    context: Record<string, unknown> = {}
  ): Promise<FunctionExecutionResult> {
    const startTime = Date.now();
    const definition = this.functions.get(name);

    if (!definition) {
      const result: FunctionExecutionResult = {
        name,
        success: false,
        error: `Function '${name}' not found`,
        executionTime: Date.now() - startTime,
        metadata: { context },
      };

      this.recordExecution(result);
      return result;
    }

    // Validate arguments
    const validationResult = this.validateArguments(definition, args);
    if (!validationResult.valid) {
      const result: FunctionExecutionResult = {
        name,
        success: false,
        error: `Invalid arguments: ${validationResult.errors.join(', ')}`,
        executionTime: Date.now() - startTime,
        metadata: { context, args },
      };

      this.recordExecution(result);
      return result;
    }

    // Check permissions if required
    if (definition.permissions && definition.permissions.length > 0) {
      const userPermissions = (context.permissions as string[]) || [];
      const hasPermission = definition.permissions.some((perm) => userPermissions.includes(perm));

      if (!hasPermission) {
        const result: FunctionExecutionResult = {
          name,
          success: false,
          error: `Insufficient permissions. Required: ${definition.permissions.join(', ')}`,
          executionTime: Date.now() - startTime,
          metadata: { context, requiredPermissions: definition.permissions },
        };

        this.recordExecution(result);
        return result;
      }
    }

    try {
      logger.debug('Executing function', {
        name,
        args,
        timeout: definition.timeout,
      });

      // Execute with timeout
      const result = await this.executeWithTimeout(
        definition.handler,
        args,
        definition.timeout || 30000
      );

      const executionResult: FunctionExecutionResult = {
        name,
        success: true,
        result,
        executionTime: Date.now() - startTime,
        metadata: { context },
      };

      this.recordExecution(executionResult);

      logger.info('Function executed successfully', {
        name,
        executionTime: executionResult.executionTime,
      });

      return executionResult;
    } catch (error) {
      const executionResult: FunctionExecutionResult = {
        name,
        success: false,
        error: error.message || 'Unknown error',
        executionTime: Date.now() - startTime,
        metadata: { context, args },
      };

      this.recordExecution(executionResult);

      logger.error('Function execution failed', {
        name,
        error: error.message,
        executionTime: executionResult.executionTime,
      });

      return executionResult;
    }
  }

  convertToAnthropicTools(tools: ToolDefinition[]): AnthropicTool[] {
    return tools.map((tool) => ({
      name: tool.name,
      description: tool.description,
      input_schema: {
        type: 'object',
        properties: tool.parameters.properties,
        required: tool.parameters.required,
      },
    }));
  }

  parseFunctionCalls(contentBlocks: AnthropicContentBlock[]): FunctionCall[] {
    const calls: FunctionCall[] = [];

    for (const block of contentBlocks) {
      if (block.type === 'tool_use') {
        calls.push({
          id: block.id!,
          name: block.name!,
          arguments: JSON.parse(block.arguments || '{}'),
        });
      }
    }

    return calls;
  }

  formatFunctionResult(result: FunctionExecutionResult): AnthropicContentBlock {
    return {
      type: 'tool_result',
      tool_use_id: result.name, // In practice, this would be the actual tool_use_id
      content: JSON.stringify({
        success: result.success,
        result: result.result,
        error: result.error,
        executionTime: result.executionTime,
      }),
    };
  }

  getExecutionHistory(functionName?: string, limit?: number): FunctionExecutionResult[] {
    let history = [...this.executionHistory];

    if (functionName) {
      history = history.filter((result) => result.name === functionName);
    }

    // Sort by most recent first
    history.sort((a, b) => {
      // Extract timestamp from metadata or use execution time as proxy
      const aTime = (a.metadata as any).timestamp || 0;
      const bTime = (b.metadata as any).timestamp || 0;
      return bTime - aTime;
    });

    return limit ? history.slice(0, limit) : history;
  }

  getExecutionStats(): {
    totalExecutions: number;
    successRate: number;
    averageExecutionTime: number;
    functionStats: Record<
      string,
      {
        executions: number;
        successes: number;
        failures: number;
        averageTime: number;
      }
    >;
  } {
    const totalExecutions = this.executionHistory.length;
    const successes = this.executionHistory.filter((r) => r.success).length;
    const successRate = totalExecutions > 0 ? (successes / totalExecutions) * 100 : 0;

    const averageExecutionTime =
      totalExecutions > 0
        ? this.executionHistory.reduce((sum, r) => sum + r.executionTime, 0) / totalExecutions
        : 0;

    const functionStats: Record<string, any> = {};

    for (const result of this.executionHistory) {
      if (!functionStats[result.name]) {
        functionStats[result.name] = {
          executions: 0,
          successes: 0,
          failures: 0,
          totalTime: 0,
        };
      }

      const stats = functionStats[result.name];
      stats.executions++;
      stats.totalTime += result.executionTime;

      if (result.success) {
        stats.successes++;
      } else {
        stats.failures++;
      }
    }

    // Calculate averages
    for (const [name, stats] of Object.entries(functionStats)) {
      stats.averageTime = stats.totalTime / stats.executions;
      delete stats.totalTime;
    }

    return {
      totalExecutions,
      successRate,
      averageExecutionTime,
      functionStats,
    };
  }

  private validateFunctionDefinition(definition: FunctionDefinition): void {
    if (!definition.name || typeof definition.name !== 'string') {
      throw new AnthropicError(
        'INVALID_FUNCTION_DEFINITION',
        'Function name is required and must be a string'
      );
    }

    if (!definition.description || typeof definition.description !== 'string') {
      throw new AnthropicError(
        'INVALID_FUNCTION_DEFINITION',
        'Function description is required and must be a string'
      );
    }

    if (!definition.parameters || typeof definition.parameters !== 'object') {
      throw new AnthropicError(
        'INVALID_FUNCTION_DEFINITION',
        'Function parameters are required and must be an object'
      );
    }

    if (definition.parameters.type !== 'object') {
      throw new AnthropicError(
        'INVALID_FUNCTION_DEFINITION',
        'Function parameters type must be "object"'
      );
    }

    if (!definition.handler || typeof definition.handler !== 'function') {
      throw new AnthropicError(
        'INVALID_FUNCTION_DEFINITION',
        'Function handler is required and must be a function'
      );
    }
  }

  private validateArguments(
    definition: FunctionDefinition,
    args: Record<string, unknown>
  ): { valid: boolean; errors: string[] } {
    const errors: string[] = [];
    const { properties, required } = definition.parameters;

    // Check required arguments
    for (const requiredArg of required) {
      if (!(requiredArg in args)) {
        errors.push(`Missing required argument: ${requiredArg}`);
      }
    }

    // Check argument types
    for (const [argName, argValue] of Object.entries(args)) {
      const schema = properties[argName];
      if (!schema) {
        errors.push(`Unknown argument: ${argName}`);
        continue;
      }

      // Basic type validation
      const expectedType = schema.type;
      const actualType = typeof argValue;

      if (expectedType === 'string' && actualType !== 'string') {
        errors.push(`Argument ${argName} must be a string, got ${actualType}`);
      } else if (expectedType === 'number' && actualType !== 'number') {
        errors.push(`Argument ${argName} must be a number, got ${actualType}`);
      } else if (expectedType === 'boolean' && actualType !== 'boolean') {
        errors.push(`Argument ${argName} must be a boolean, got ${actualType}`);
      } else if (expectedType === 'array' && !Array.isArray(argValue)) {
        errors.push(`Argument ${argName} must be an array, got ${actualType}`);
      } else if (
        expectedType === 'object' &&
        (actualType !== 'object' || Array.isArray(argValue))
      ) {
        errors.push(`Argument ${argName} must be an object, got ${actualType}`);
      }
    }

    return {
      valid: errors.length === 0,
      errors,
    };
  }

  private async executeWithTimeout<T>(
    fn: (args: Record<string, unknown>) => Promise<T>,
    args: Record<string, unknown>,
    timeoutMs: number
  ): Promise<T> {
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        reject(new Error(`Function execution timed out after ${timeoutMs}ms`));
      }, timeoutMs);

      fn(args)
        .then((result) => {
          clearTimeout(timer);
          resolve(result);
        })
        .catch((error) => {
          clearTimeout(timer);
          reject(error);
        });
    });
  }

  private recordExecution(result: FunctionExecutionResult): void {
    // Add timestamp to metadata
    (result.metadata as any).timestamp = Date.now();

    this.executionHistory.push(result);

    // Trim history if it gets too large
    if (this.executionHistory.length > this.maxHistorySize) {
      const excess = this.executionHistory.length - this.maxHistorySize;
      this.executionHistory.splice(0, excess);
    }
  }

  // Static factory methods for common functions
  static createBuiltinFunctions(): FunctionRegistry {
    const manager = new FunctionManager();

    // Math functions
    manager.register('calculate', {
      name: 'calculate',
      description: 'Perform mathematical calculations',
      parameters: {
        type: 'object',
        properties: {
          expression: {
            type: 'string',
            description: 'Mathematical expression to evaluate',
          },
        },
        required: ['expression'],
      },
      handler: async (args) => {
        // Safe math evaluation (in production, use a proper math library)
        try {
          // This is a simplified example - use proper math parsing in production
          const result = Function('"use strict"; return (' + args.expression + ')')();
          return { result };
        } catch (error) {
          throw new Error(`Invalid expression: ${error.message}`);
        }
      },
      timeout: 5000,
    });

    // Date/time functions
    manager.register('get_current_time', {
      name: 'get_current_time',
      description: 'Get the current date and time',
      parameters: {
        type: 'object',
        properties: {
          timezone: {
            type: 'string',
            description: 'Timezone identifier (e.g., "UTC", "America/New_York")',
          },
        },
        required: [],
      },
      handler: async (args) => {
        const now = new Date();
        if (args.timezone) {
          return {
            time: now.toLocaleString('en-US', { timeZone: args.timezone }),
            timezone: args.timezone,
            iso: now.toISOString(),
          };
        }
        return {
          time: now.toISOString(),
          timezone: 'UTC',
          iso: now.toISOString(),
        };
      },
      timeout: 1000,
    });

    return manager;
  }
}
```

### Subtask 5.2: Add Tool Use Capabilities

**File**: `src/providers/anthropic/tools/tool-manager.ts`

```typescript
import { logger } from '@tamma/observability';
import type { ToolDefinition, ToolCall, ToolResult } from '@tamma/shared/contracts';
import type { AnthropicTool, AnthropicContentBlock } from '../types/anthropic-types';
import { FunctionManager } from '../function-calling/function-manager';
import { AnthropicError } from '../errors/anthropic-error';

export interface ToolExecutionContext {
  requestId?: string;
  userId?: string;
  sessionId?: string;
  permissions?: string[];
  metadata?: Record<string, unknown>;
}

export interface ToolExecutionPlan {
  toolCalls: ToolCall[];
  parallelizable: boolean;
  estimatedDuration: number;
  dependencies: string[][];
}

export interface ToolExecutionResult {
  toolCall: ToolCall;
  result: ToolResult;
  executionTime: number;
  success: boolean;
  error?: string;
}

export class ToolManager {
  private readonly functionManager: FunctionManager;
  private readonly toolRegistry: Map<string, ToolDefinition> = new Map();
  private readonly executionHistory: ToolExecutionResult[] = [];
  private readonly maxHistorySize: number = 1000;

  constructor(functionManager?: FunctionManager) {
    this.functionManager = functionManager || new FunctionManager();
  }

  registerTool(tool: ToolDefinition): void {
    // Validate tool definition
    this.validateToolDefinition(tool);

    // Register as function if it has handler
    if (tool.handler) {
      this.functionManager.register(tool.name, {
        name: tool.name,
        description: tool.description,
        parameters: tool.parameters,
        handler: tool.handler,
        timeout: tool.timeout,
        dangerous: tool.dangerous,
        permissions: tool.permissions,
      });
    }

    this.toolRegistry.set(tool.name, tool);

    logger.info('Tool registered', {
      name: tool.name,
      description: tool.description,
      hasHandler: !!tool.handler,
      dangerous: tool.dangerous,
    });
  }

  unregisterTool(name: string): void {
    this.toolRegistry.delete(name);
    this.functionManager.unregister(name);

    logger.info('Tool unregistered', { name });
  }

  getTool(name: string): ToolDefinition | undefined {
    return this.toolRegistry.get(name);
  }

  listTools(): ToolDefinition[] {
    return Array.from(this.toolRegistry.values());
  }

  convertToAnthropicTools(tools?: ToolDefinition[]): AnthropicTool[] {
    const toolsToConvert = tools || this.listTools();
    return this.functionManager.convertToAnthropicTools(toolsToConvert);
  }

  parseToolCalls(contentBlocks: AnthropicContentBlock[]): ToolCall[] {
    return this.functionManager.parseFunctionCalls(contentBlocks);
  }

  async executeToolCall(
    toolCall: ToolCall,
    context: ToolExecutionContext = {}
  ): Promise<ToolExecutionResult> {
    const startTime = Date.now();

    try {
      logger.debug('Executing tool call', {
        toolName: toolCall.name,
        toolCallId: toolCall.id,
        arguments: toolCall.arguments,
      });

      // Check if tool exists
      const tool = this.getTool(toolCall.name);
      if (!tool) {
        throw new AnthropicError('TOOL_NOT_FOUND', `Tool '${toolCall.name}' not found`, {
          toolName: toolCall.name,
        });
      }

      // Execute using function manager
      const functionResult = await this.functionManager.executeFunction(
        toolCall.name,
        toolCall.arguments,
        context
      );

      const result: ToolResult = {
        toolCallId: toolCall.id,
        success: functionResult.success,
        result: functionResult.result,
        error: functionResult.error,
      };

      const executionResult: ToolExecutionResult = {
        toolCall,
        result,
        executionTime: Date.now() - startTime,
        success: functionResult.success,
        error: functionResult.error,
      };

      this.recordExecution(executionResult);

      logger.info('Tool call executed', {
        toolName: toolCall.name,
        toolCallId: toolCall.id,
        success: functionResult.success,
        executionTime: executionResult.executionTime,
      });

      return executionResult;
    } catch (error) {
      const result: ToolResult = {
        toolCallId: toolCall.id,
        success: false,
        error: error.message || 'Unknown error',
      };

      const executionResult: ToolExecutionResult = {
        toolCall,
        result,
        executionTime: Date.now() - startTime,
        success: false,
        error: error.message,
      };

      this.recordExecution(executionResult);

      logger.error('Tool call failed', {
        toolName: toolCall.name,
        toolCallId: toolCall.id,
        error: error.message,
        executionTime: executionResult.executionTime,
      });

      return executionResult;
    }
  }

  async executeToolCalls(
    toolCalls: ToolCall[],
    context: ToolExecutionContext = {},
    options: {
      parallel?: boolean;
      maxConcurrency?: number;
    } = {}
  ): Promise<ToolExecutionResult[]> {
    if (toolCalls.length === 0) {
      return [];
    }

    const { parallel = false, maxConcurrency = 5 } = options;

    if (parallel && toolCalls.length > 1) {
      return this.executeToolCallsParallel(toolCalls, context, maxConcurrency);
    } else {
      return this.executeToolCallsSequential(toolCalls, context);
    }
  }

  createExecutionPlan(toolCalls: ToolCall[]): ToolExecutionPlan {
    // Analyze dependencies and parallelization opportunities
    const dependencies: string[][] = [];
    let parallelizable = true;

    // Simple heuristic: if tools don't have obvious dependencies, they can be parallel
    // In a more sophisticated implementation, you'd analyze tool descriptions
    // and execution patterns to determine actual dependencies

    for (const toolCall of toolCalls) {
      const tool = this.getTool(toolCall.name);
      if (tool?.sequential) {
        parallelizable = false;
        break;
      }
    }

    // Estimate duration based on historical data
    const estimatedDuration = this.estimateExecutionDuration(toolCalls);

    return {
      toolCalls,
      parallelizable,
      estimatedDuration,
      dependencies,
    };
  }

  formatToolResults(results: ToolExecutionResult[]): AnthropicContentBlock[] {
    return results.map((result) => ({
      type: 'tool_result' as const,
      tool_use_id: result.toolCall.id,
      content: JSON.stringify({
        success: result.result.success,
        result: result.result.result,
        error: result.result.error,
        executionTime: result.executionTime,
      }),
      is_error: !result.success,
    }));
  }

  getExecutionHistory(toolName?: string, limit?: number): ToolExecutionResult[] {
    let history = [...this.executionHistory];

    if (toolName) {
      history = history.filter((result) => result.toolCall.name === toolName);
    }

    // Sort by most recent first
    history.sort((a, b) => b.executionTime - a.executionTime);

    return limit ? history.slice(0, limit) : history;
  }

  getToolUsageStats(): Record<
    string,
    {
      calls: number;
      successes: number;
      failures: number;
      averageExecutionTime: number;
      lastUsed: number;
    }
  > {
    const stats: Record<string, any> = {};

    for (const result of this.executionHistory) {
      const toolName = result.toolCall.name;

      if (!stats[toolName]) {
        stats[toolName] = {
          calls: 0,
          successes: 0,
          failures: 0,
          totalExecutionTime: 0,
          lastUsed: 0,
        };
      }

      const stat = stats[toolName];
      stat.calls++;
      stat.totalExecutionTime += result.executionTime;
      stat.lastUsed = Math.max(stat.lastUsed, Date.now() - result.executionTime);

      if (result.success) {
        stat.successes++;
      } else {
        stat.failures++;
      }
    }

    // Calculate averages
    for (const [toolName, stat] of Object.entries(stats)) {
      stat.averageExecutionTime = stat.totalExecutionTime / stat.calls;
      delete stat.totalExecutionTime;
    }

    return stats;
  }

  private async executeToolCallsSequential(
    toolCalls: ToolCall[],
    context: ToolExecutionContext
  ): Promise<ToolExecutionResult[]> {
    const results: ToolExecutionResult[] = [];

    for (const toolCall of toolCalls) {
      const result = await this.executeToolCall(toolCall, context);
      results.push(result);

      // If a tool call fails and it's critical, stop execution
      if (!result.success && this.isCriticalFailure(toolCall, result)) {
        logger.warn('Stopping sequential execution due to critical failure', {
          toolName: toolCall.name,
          error: result.error,
        });
        break;
      }
    }

    return results;
  }

  private async executeToolCallsParallel(
    toolCalls: ToolCall[],
    context: ToolExecutionContext,
    maxConcurrency: number
  ): Promise<ToolExecutionResult[]> {
    const results: ToolExecutionResult[] = [];

    // Process in batches to control concurrency
    for (let i = 0; i < toolCalls.length; i += maxConcurrency) {
      const batch = toolCalls.slice(i, i + maxConcurrency);

      const batchPromises = batch.map((toolCall) => this.executeToolCall(toolCall, context));

      const batchResults = await Promise.all(batchPromises);
      results.push(...batchResults);
    }

    return results;
  }

  private estimateExecutionDuration(toolCalls: ToolCall[]): number {
    let totalEstimatedTime = 0;

    for (const toolCall of toolCalls) {
      const tool = this.getTool(toolCall.name);
      const baseTime = tool?.timeout || 30000; // Default 30 seconds

      // Use historical data if available
      const history = this.getExecutionHistory(toolCall.name, 10);
      if (history.length > 0) {
        const avgTime = history.reduce((sum, h) => sum + h.executionTime, 0) / history.length;
        totalEstimatedTime += avgTime;
      } else {
        totalEstimatedTime += baseTime / 2; // Estimate half of timeout as average
      }
    }

    return totalEstimatedTime;
  }

  private isCriticalFailure(toolCall: ToolCall, result: ToolExecutionResult): boolean {
    const tool = this.getTool(toolCall.name);
    return tool?.critical || false;
  }

  private validateToolDefinition(tool: ToolDefinition): void {
    if (!tool.name || typeof tool.name !== 'string') {
      throw new AnthropicError(
        'INVALID_TOOL_DEFINITION',
        'Tool name is required and must be a string'
      );
    }

    if (!tool.description || typeof tool.description !== 'string') {
      throw new AnthropicError(
        'INVALID_TOOL_DEFINITION',
        'Tool description is required and must be a string'
      );
    }

    if (!tool.parameters || typeof tool.parameters !== 'object') {
      throw new AnthropicError(
        'INVALID_TOOL_DEFINITION',
        'Tool parameters are required and must be an object'
      );
    }

    if (tool.parameters.type !== 'object') {
      throw new AnthropicError('INVALID_TOOL_DEFINITION', 'Tool parameters type must be "object"');
    }
  }

  private recordExecution(result: ToolExecutionResult): void {
    this.executionHistory.push(result);

    // Trim history if it gets too large
    if (this.executionHistory.length > this.maxHistorySize) {
      const excess = this.executionHistory.length - this.maxHistorySize;
      this.executionHistory.splice(0, excess);
    }
  }

  // Static factory methods for common tools
  static createBuiltinTools(): ToolManager {
    const functionManager = FunctionManager.createBuiltinFunctions();
    const toolManager = new ToolManager(functionManager);

    // File system tools (would need proper implementation in production)
    toolManager.registerTool({
      name: 'read_file',
      description: 'Read the contents of a file',
      parameters: {
        type: 'object',
        properties: {
          path: {
            type: 'string',
            description: 'Path to the file to read',
          },
        },
        required: ['path'],
      },
      dangerous: true,
      permissions: ['file_read'],
      handler: async (args) => {
        // In production, implement proper file reading with security checks
        throw new Error('File system access not implemented in demo');
      },
      timeout: 10000,
    });

    // Web search tool (would need actual implementation)
    toolManager.registerTool({
      name: 'web_search',
      description: 'Search the web for information',
      parameters: {
        type: 'object',
        properties: {
          query: {
            type: 'string',
            description: 'Search query',
          },
          max_results: {
            type: 'number',
            description: 'Maximum number of results to return',
            default: 5,
          },
        },
        required: ['query'],
      },
      permissions: ['web_search'],
      handler: async (args) => {
        // In production, implement actual web search
        return {
          results: [
            {
              title: 'Demo Result',
              url: 'https://example.com',
              snippet: 'This is a demo search result',
            },
          ],
        };
      },
      timeout: 15000,
    });

    return toolManager;
  }
}
```

### Subtask 5.3: Create Tool Definition and Validation System

**File**: `src/providers/anthropic/tools/tool-validator.ts`

```typescript
import { logger } from '@tamma/observability';
import type { ToolDefinition } from '@tamma/shared/contracts';
import { AnthropicError } from '../errors/anthropic-error';

export interface ValidationResult {
  valid: boolean;
  errors: string[];
  warnings: string[];
}

export interface ToolSchema {
  name: string;
  version: string;
  schema: {
    type: 'object';
    properties: Record<string, ToolPropertySchema>;
    required: string[];
  };
  examples?: ToolExample[];
}

export interface ToolPropertySchema {
  type: 'string' | 'number' | 'boolean' | 'array' | 'object';
  description?: string;
  enum?: any[];
  items?: ToolPropertySchema; // For array types
  properties?: Record<string, ToolPropertySchema>; // For object types
  required?: string[]; // For object types
  minimum?: number; // For numbers
  maximum?: number; // For numbers
  minLength?: number; // For strings
  maxLength?: number; // For strings
  pattern?: string; // For strings (regex)
  format?: string; // For strings (email, date, etc.)
  default?: any;
}

export interface ToolExample {
  name: string;
  description: string;
  parameters: Record<string, any>;
  expected_output?: any;
}

export class ToolValidator {
  private readonly schemas: Map<string, ToolSchema> = new Map();
  private readonly customValidators: Map<string, (value: any) => boolean> = new Map();

  registerSchema(schema: ToolSchema): void {
    this.validateSchema(schema);
    this.schemas.set(schema.name, schema);

    logger.info('Tool schema registered', {
      name: schema.name,
      version: schema.version,
      propertyCount: Object.keys(schema.schema.properties).length,
    });
  }

  validateTool(tool: ToolDefinition): ValidationResult {
    const errors: string[] = [];
    const warnings: string[] = [];

    // Basic structure validation
    if (!tool.name || typeof tool.name !== 'string') {
      errors.push('Tool name is required and must be a string');
    }

    if (!tool.description || typeof tool.description !== 'string') {
      errors.push('Tool description is required and must be a string');
    }

    if (tool.name && !/^[a-zA-Z][a-zA-Z0-9_]*$/.test(tool.name)) {
      errors.push(
        'Tool name must start with a letter and contain only letters, numbers, and underscores'
      );
    }

    // Parameters validation
    if (!tool.parameters) {
      errors.push('Tool parameters are required');
    } else {
      const paramValidation = this.validateParameters(tool.parameters);
      errors.push(...paramValidation.errors);
      warnings.push(...paramValidation.warnings);
    }

    // Handler validation
    if (tool.handler && typeof tool.handler !== 'function') {
      errors.push('Tool handler must be a function');
    }

    // Security validation
    if (tool.dangerous && !tool.permissions?.length) {
      warnings.push('Dangerous tool should have permissions defined');
    }

    // Timeout validation
    if (tool.timeout && (typeof tool.timeout !== 'number' || tool.timeout <= 0)) {
      errors.push('Tool timeout must be a positive number');
    }

    // Check for common issues
    if (tool.name) {
      const commonIssues = this.checkCommonIssues(tool);
      warnings.push(...commonIssues);
    }

    return {
      valid: errors.length === 0,
      errors,
      warnings,
    };
  }

  validateParameters(parameters: any): ValidationResult {
    const errors: string[] = [];
    const warnings: string[] = [];

    if (parameters.type !== 'object') {
      errors.push('Parameters type must be "object"');
      return { valid: false, errors, warnings };
    }

    if (!parameters.properties || typeof parameters.properties !== 'object') {
      errors.push('Parameters properties are required and must be an object');
      return { valid: false, errors, warnings };
    }

    // Validate each property
    for (const [propName, propSchema] of Object.entries(parameters.properties)) {
      const propValidation = this.validateProperty(propName, propSchema);
      errors.push(...propValidation.errors);
      warnings.push(...propValidation.warnings);
    }

    // Validate required array
    if (parameters.required) {
      if (!Array.isArray(parameters.required)) {
        errors.push('Parameters required must be an array');
      } else {
        for (const requiredProp of parameters.required) {
          if (typeof requiredProp !== 'string') {
            errors.push('Required property names must be strings');
          } else if (!parameters.properties[requiredProp]) {
            errors.push(`Required property '${requiredProp}' is not defined in properties`);
          }
        }
      }
    }

    return {
      valid: errors.length === 0,
      errors,
      warnings,
    };
  }

  validateProperty(name: string, schema: any): ValidationResult {
    const errors: string[] = [];
    const warnings: string[] = [];

    if (!schema.type || !this.isValidType(schema.type)) {
      errors.push(`Property '${name}' must have a valid type`);
      return { valid: false, errors, warnings };
    }

    if (!schema.description) {
      warnings.push(`Property '${name}' should have a description`);
    }

    // Type-specific validation
    switch (schema.type) {
      case 'string':
        if (
          schema.minLength !== undefined &&
          (typeof schema.minLength !== 'number' || schema.minLength < 0)
        ) {
          errors.push(`Property '${name}' minLength must be a non-negative number`);
        }
        if (
          schema.maxLength !== undefined &&
          (typeof schema.maxLength !== 'number' || schema.maxLength < 0)
        ) {
          errors.push(`Property '${name}' maxLength must be a non-negative number`);
        }
        if (
          schema.minLength !== undefined &&
          schema.maxLength !== undefined &&
          schema.minLength > schema.maxLength
        ) {
          errors.push(`Property '${name}' minLength cannot be greater than maxLength`);
        }
        if (schema.pattern && typeof schema.pattern !== 'string') {
          errors.push(`Property '${name}' pattern must be a string`);
        }
        break;

      case 'number':
        if (schema.minimum !== undefined && typeof schema.minimum !== 'number') {
          errors.push(`Property '${name}' minimum must be a number`);
        }
        if (schema.maximum !== undefined && typeof schema.maximum !== 'number') {
          errors.push(`Property '${name}' maximum must be a number`);
        }
        if (
          schema.minimum !== undefined &&
          schema.maximum !== undefined &&
          schema.minimum > schema.maximum
        ) {
          errors.push(`Property '${name}' minimum cannot be greater than maximum`);
        }
        break;

      case 'array':
        if (schema.items) {
          const itemsValidation = this.validateProperty(`${name}[items]`, schema.items);
          errors.push(...itemsValidation.errors);
          warnings.push(...itemsValidation.warnings);
        } else {
          warnings.push(`Array property '${name}' should specify items schema`);
        }
        break;

      case 'object':
        if (schema.properties) {
          for (const [subPropName, subPropSchema] of Object.entries(schema.properties)) {
            const subValidation = this.validateProperty(`${name}.${subPropName}`, subPropSchema);
            errors.push(...subValidation.errors);
            warnings.push(...subValidation.warnings);
          }
        }
        break;
    }

    return {
      valid: errors.length === 0,
      errors,
      warnings,
    };
  }

  validateArguments(toolName: string, args: Record<string, any>): ValidationResult {
    const schema = this.schemas.get(toolName);
    if (!schema) {
      return {
        valid: false,
        errors: [`No schema registered for tool: ${toolName}`],
        warnings: [],
      };
    }

    return this.validateArgumentsAgainstSchema(args, schema.schema);
  }

  validateArgumentsAgainstSchema(args: Record<string, any>, schema: any): ValidationResult {
    const errors: string[] = [];
    const warnings: string[] = [];

    // Check required properties
    if (schema.required) {
      for (const requiredProp of schema.required) {
        if (!(requiredProp in args)) {
          errors.push(`Missing required property: ${requiredProp}`);
        }
      }
    }

    // Validate provided properties
    for (const [propName, propValue] of Object.entries(args)) {
      const propSchema = schema.properties[propName];
      if (!propSchema) {
        warnings.push(`Unknown property: ${propName}`);
        continue;
      }

      const propValidation = this.validateValue(propName, propValue, propSchema);
      errors.push(...propValidation.errors);
      warnings.push(...propValidation.warnings);
    }

    return {
      valid: errors.length === 0,
      errors,
      warnings,
    };
  }

  validateValue(name: string, value: any, schema: ToolPropertySchema): ValidationResult {
    const errors: string[] = [];
    const warnings: string[] = [];

    // Type validation
    if (!this.validateType(value, schema.type)) {
      errors.push(`Property '${name}' must be of type ${schema.type}, got ${typeof value}`);
      return { valid: false, errors, warnings };
    }

    // Enum validation
    if (schema.enum && !schema.enum.includes(value)) {
      errors.push(`Property '${name}' must be one of: ${schema.enum.join(', ')}`);
    }

    // Type-specific validation
    switch (schema.type) {
      case 'string':
        if (schema.minLength !== undefined && value.length < schema.minLength) {
          errors.push(`Property '${name}' must be at least ${schema.minLength} characters long`);
        }
        if (schema.maxLength !== undefined && value.length > schema.maxLength) {
          errors.push(`Property '${name}' must be at most ${schema.maxLength} characters long`);
        }
        if (schema.pattern && !new RegExp(schema.pattern).test(value)) {
          errors.push(`Property '${name}' does not match required pattern`);
        }
        break;

      case 'number':
        if (schema.minimum !== undefined && value < schema.minimum) {
          errors.push(`Property '${name}' must be at least ${schema.minimum}`);
        }
        if (schema.maximum !== undefined && value > schema.maximum) {
          errors.push(`Property '${name}' must be at most ${schema.maximum}`);
        }
        break;

      case 'array':
        if (schema.items) {
          for (let i = 0; i < value.length; i++) {
            const itemValidation = this.validateValue(`${name}[${i}]`, value[i], schema.items);
            errors.push(...itemValidation.errors);
            warnings.push(...itemValidation.warnings);
          }
        }
        break;

      case 'object':
        if (schema.properties) {
          const objectValidation = this.validateArgumentsAgainstSchema(value, {
            type: 'object',
            properties: schema.properties,
            required: schema.required || [],
          });
          errors.push(...objectValidation.errors);
          warnings.push(...objectValidation.warnings);
        }
        break;
    }

    return {
      valid: errors.length === 0,
      errors,
      warnings,
    };
  }

  generateToolSchema(tool: ToolDefinition): ToolSchema {
    return {
      name: tool.name,
      version: '1.0.0',
      schema: {
        type: 'object',
        properties: tool.parameters.properties,
        required: tool.parameters.required || [],
      },
      examples: tool.examples,
    };
  }

  private validateSchema(schema: ToolSchema): void {
    if (!schema.name || typeof schema.name !== 'string') {
      throw new AnthropicError('INVALID_SCHEMA', 'Schema name is required and must be a string');
    }

    if (!schema.version || typeof schema.version !== 'string') {
      throw new AnthropicError('INVALID_SCHEMA', 'Schema version is required and must be a string');
    }

    if (!schema.schema || schema.schema.type !== 'object') {
      throw new AnthropicError('INVALID_SCHEMA', 'Schema must have an object type schema');
    }
  }

  private isValidType(type: string): boolean {
    return ['string', 'number', 'boolean', 'array', 'object'].includes(type);
  }

  private validateType(value: any, type: string): boolean {
    switch (type) {
      case 'string':
        return typeof value === 'string';
      case 'number':
        return typeof value === 'number' && !isNaN(value);
      case 'boolean':
        return typeof value === 'boolean';
      case 'array':
        return Array.isArray(value);
      case 'object':
        return typeof value === 'object' && value !== null && !Array.isArray(value);
      default:
        return false;
    }
  }

  private checkCommonIssues(tool: ToolDefinition): string[] {
    const warnings: string[] = [];

    // Check for generic descriptions
    if (tool.description.length < 20) {
      warnings.push('Tool description should be more descriptive');
    }

    // Check for missing examples
    if (!tool.examples || tool.examples.length === 0) {
      warnings.push('Tool should include examples for better usability');
    }

    // Check for security considerations
    if (tool.dangerous && !tool.description.toLowerCase().includes('danger')) {
      warnings.push('Dangerous tool should mention security risks in description');
    }

    // Check parameter naming
    if (tool.parameters.properties) {
      for (const [propName] of Object.entries(tool.parameters.properties)) {
        if (!/^[a-z][a-z0-9_]*$/.test(propName)) {
          warnings.push(`Parameter '${propName}' should use snake_case naming`);
        }
      }
    }

    return warnings;
  }
}
```

### Subtask 5.4: Handle Tool Execution Results and Errors

**File**: `src/providers/anthropic/tools/result-handler.ts`

```typescript
import { logger } from '@tamma/observability';
import type { ToolResult, ToolExecutionResult } from '@tamma/shared/contracts';
import type { AnthropicContentBlock } from '../types/anthropic-types';
import { AnthropicError } from '../errors/anthropic-error';

export interface ResultProcessingOptions {
  includeMetadata?: boolean;
  formatOutput?: 'json' | 'text' | 'both';
  maxOutputLength?: number;
  sanitizeOutput?: boolean;
}

export interface ProcessedResult {
  content: AnthropicContentBlock;
  metadata: {
    toolName: string;
    executionTime: number;
    success: boolean;
    outputSize: number;
    truncated: boolean;
  };
}

export class ResultHandler {
  private readonly defaultOptions: ResultProcessingOptions = {
    includeMetadata: true,
    formatOutput: 'json',
    maxOutputLength: 10000,
    sanitizeOutput: true,
  };

  processToolResult(
    result: ToolExecutionResult,
    options: ResultProcessingOptions = {}
  ): ProcessedResult {
    const config = { ...this.defaultOptions, ...options };

    try {
      const processedContent = this.formatResultContent(result, config);
      const metadata = this.generateMetadata(result);

      logger.debug('Tool result processed', {
        toolName: result.toolCall.name,
        success: result.success,
        outputSize: metadata.outputSize,
        truncated: metadata.truncated,
      });

      return {
        content: processedContent,
        metadata,
      };
    } catch (error) {
      logger.error('Failed to process tool result', {
        toolName: result.toolCall.name,
        error: error.message,
      });

      // Return error result
      return {
        content: {
          type: 'tool_result',
          tool_use_id: result.toolCall.id,
          content: JSON.stringify({
            success: false,
            error: `Result processing failed: ${error.message}`,
          }),
          is_error: true,
        },
        metadata: {
          toolName: result.toolCall.name,
          executionTime: result.executionTime,
          success: false,
          outputSize: 0,
          truncated: false,
        },
      };
    }
  }

  processMultipleResults(
    results: ToolExecutionResult[],
    options: ResultProcessingOptions = {}
  ): ProcessedResult[] {
    return results.map((result) => this.processToolResult(result, options));
  }

  aggregateResults(results: ProcessedResult[]): {
    totalResults: number;
    successfulResults: number;
    failedResults: number;
    totalExecutionTime: number;
    totalOutputSize: number;
    summary: string;
  } {
    const totalResults = results.length;
    const successfulResults = results.filter((r) => r.metadata.success).length;
    const failedResults = totalResults - successfulResults;
    const totalExecutionTime = results.reduce((sum, r) => sum + r.metadata.executionTime, 0);
    const totalOutputSize = results.reduce((sum, r) => sum + r.metadata.outputSize, 0);

    const successRate = totalResults > 0 ? (successfulResults / totalResults) * 100 : 0;
    const averageExecutionTime = totalResults > 0 ? totalExecutionTime / totalResults : 0;

    const summary =
      `Executed ${totalResults} tool calls: ${successfulResults} successful, ${failedResults} failed. ` +
      `Success rate: ${successRate.toFixed(1)}%, Average time: ${averageExecutionTime.toFixed(0)}ms`;

    return {
      totalResults,
      successfulResults,
      failedResults,
      totalExecutionTime,
      totalOutputSize,
      summary,
    };
  }

  createErrorResult(toolCall: any, error: Error, executionTime: number = 0): ProcessedResult {
    const errorContent = {
      type: 'tool_result' as const,
      tool_use_id: toolCall.id,
      content: JSON.stringify({
        success: false,
        error: this.sanitizeErrorMessage(error.message),
        errorType: error.constructor.name,
        timestamp: new Date().toISOString(),
      }),
      is_error: true,
    };

    return {
      content: errorContent,
      metadata: {
        toolName: toolCall.name,
        executionTime,
        success: false,
        outputSize: JSON.stringify(errorContent.content).length,
        truncated: false,
      },
    };
  }

  validateResult(result: ToolResult): {
    valid: boolean;
    errors: string[];
    warnings: string[];
  } {
    const errors: string[] = [];
    const warnings: string[] = [];

    if (!result.toolCallId || typeof result.toolCallId !== 'string') {
      errors.push('Tool result must have a valid toolCallId');
    }

    if (typeof result.success !== 'boolean') {
      errors.push('Tool result must have a success boolean');
    }

    if (!result.success && !result.error) {
      errors.push('Failed tool result must have an error message');
    }

    if (result.success && result.error) {
      warnings.push('Successful tool result should not have an error message');
    }

    // Check output size
    const outputSize = JSON.stringify(result.result || result.error).length;
    if (outputSize > 50000) {
      warnings.push(`Tool result output is large (${outputSize} bytes), consider truncating`);
    }

    return {
      valid: errors.length === 0,
      errors,
      warnings,
    };
  }

  private formatResultContent(
    result: ToolExecutionResult,
    config: ResultProcessingOptions
  ): AnthropicContentBlock {
    let content: string;
    let truncated = false;

    switch (config.formatOutput) {
      case 'json':
        content = JSON.stringify({
          success: result.success,
          result: result.result,
          error: result.error,
          executionTime: result.executionTime,
        });
        break;

      case 'text':
        content = this.formatAsText(result);
        break;

      case 'both':
        content = this.formatWithBoth(result);
        break;

      default:
        content = JSON.stringify({
          success: result.success,
          result: result.result,
          error: result.error,
        });
    }

    // Apply length limit
    if (config.maxOutputLength && content.length > config.maxOutputLength) {
      content = content.substring(0, config.maxOutputLength - 3) + '...';
      truncated = true;
    }

    // Sanitize if requested
    if (config.sanitizeOutput) {
      content = this.sanitizeOutput(content);
    }

    return {
      type: 'tool_result',
      tool_use_id: result.toolCall.id,
      content,
      is_error: !result.success,
    };
  }

  private formatAsText(result: ToolExecutionResult): string {
    if (result.success) {
      let text = `✅ Tool '${result.toolCall.name}' executed successfully\n`;
      text += `Execution time: ${result.executionTime}ms\n\n`;

      if (result.result !== undefined && result.result !== null) {
        if (typeof result.result === 'string') {
          text += `Result:\n${result.result}`;
        } else {
          text += `Result:\n${JSON.stringify(result.result, null, 2)}`;
        }
      }

      return text;
    } else {
      return (
        `❌ Tool '${result.toolCall.name}' failed\n` +
        `Error: ${result.error}\n` +
        `Execution time: ${result.executionTime}ms`
      );
    }
  }

  private formatWithBoth(result: ToolExecutionResult): string {
    const textFormat = this.formatAsText(result);
    const jsonFormat = JSON.stringify(
      {
        success: result.success,
        result: result.result,
        error: result.error,
        executionTime: result.executionTime,
      },
      null,
      2
    );

    return `${textFormat}\n\n--- JSON Data ---\n${jsonFormat}`;
  }

  private generateMetadata(result: ToolExecutionResult): {
    toolName: string;
    executionTime: number;
    success: boolean;
    outputSize: number;
    truncated: boolean;
  } {
    const outputSize = JSON.stringify(result.result || result.error).length;

    return {
      toolName: result.toolCall.name,
      executionTime: result.executionTime,
      success: result.success,
      outputSize,
      truncated: false, // Will be updated in formatResultContent if needed
    };
  }

  private sanitizeOutput(output: string): string {
    // Remove potential sensitive information
    return output
      .replace(/password["\s]*[:=]["\s]*[^"\\s}]+/gi, 'password: [REDACTED]')
      .replace(/token["\s]*[:=]["\s]*[^"\\s}]+/gi, 'token: [REDACTED]')
      .replace(/key["\s]*[:=]["\s]*[^"\\s}]+/gi, 'key: [REDACTED]')
      .replace(/secret["\s]*[:=]["\s]*[^"\\s}]+/gi, 'secret: [REDACTED]');
  }

  private sanitizeErrorMessage(message: string): string {
    // Remove potential sensitive information from error messages
    return this.sanitizeOutput(message);
  }

  // Static utility methods
  static createSuccessResult(
    toolCallId: string,
    result: any,
    executionTime: number = 0
  ): ToolResult {
    return {
      toolCallId,
      success: true,
      result,
      executionTime,
    };
  }

  static createErrorResult(
    toolCallId: string,
    error: string,
    executionTime: number = 0
  ): ToolResult {
    return {
      toolCallId,
      success: false,
      error,
      executionTime,
    };
  }
}
```

## Integration with Main Provider

**Update to**: `src/providers/anthropic/anthropic-claude-provider.ts`

```typescript
// Add these imports and update the provider
import { ToolManager } from './tools/tool-manager';
import { ToolValidator } from './tools/tool-validator';
import { ResultHandler } from './tools/result-handler';

export class AnthropicClaudeProvider implements IAIProvider {
  private readonly toolManager: ToolManager;
  private readonly toolValidator: ToolValidator;
  private readonly resultHandler: ResultHandler;

  constructor(config: AnthropicProviderConfig) {
    // ... existing constructor code ...

    // Initialize tool management
    this.toolManager = ToolManager.createBuiltinTools();
    this.toolValidator = new ToolValidator();
    this.resultHandler = new ResultHandler();
  }

  async sendMessage(
    request: MessageRequest,
    options: StreamOptions = {}
  ): Promise<AsyncIterable<MessageChunk>> {
    // Convert tools to Anthropic format
    const anthropicTools = request.tools
      ? this.toolManager.convertToAnthropicTools(request.tools)
      : undefined;

    // Create enhanced request with tool support
    const enhancedRequest = {
      ...request,
      tools: anthropicTools,
    };

    // Create base stream
    const baseStream = await this.messageStream.createStream(this.client, enhancedRequest, options);

    // Wrap stream to handle tool calls
    return this.createToolAwareStream(baseStream, request, options);
  }

  private createToolAwareStream(
    baseStream: AsyncIterable<MessageChunk>,
    request: MessageRequest,
    options: StreamOptions
  ): AsyncIterable<MessageChunk> {
    const context: ToolExecutionContext = {
      requestId: options.requestId,
      userId: options.userId,
      sessionId: options.sessionId,
      permissions: options.permissions,
    };

    return {
      [Symbol.asyncIterator]() {
        const iterator = baseStream[Symbol.asyncIterator]();
        let toolCallsBuffer: any[] = [];
        let processingTools = false;

        return {
          async next(): Promise<IteratorResult<MessageChunk>> {
            const result = await iterator.next();

            if (result.done) {
              return result;
            }

            const chunk = result.value;

            // Check for tool calls in the chunk
            if (chunk.toolCalls && chunk.toolCalls.length > 0) {
              toolCallsBuffer.push(...chunk.toolCalls);

              // Don't return this chunk yet, wait to process tools
              if (!processingTools) {
                processingTools = true;

                // Process tool calls
                const toolResults = await this.processToolCalls(toolCallsBuffer, context);

                toolCallsBuffer = [];
                processingTools = false;

                // Return a chunk with tool results
                return {
                  value: {
                    ...chunk,
                    toolResults: toolResults.map((r) => r.content),
                  },
                  done: false,
                };
              }
            }

            return result;
          },

          async processToolCalls(toolCalls: any[], context: ToolExecutionContext) {
            const results = await this.toolManager.executeToolCalls(toolCalls, context, {
              parallel: true,
            });

            return this.resultHandler.processMultipleResults(results);
          },
        };
      },
    };
  }

  // Public methods for tool management
  registerTool(tool: ToolDefinition): void {
    const validation = this.toolValidator.validateTool(tool);
    if (!validation.valid) {
      throw new AnthropicError(
        'INVALID_TOOL',
        `Tool validation failed: ${validation.errors.join(', ')}`,
        { errors: validation.errors, warnings: validation.warnings }
      );
    }

    this.toolManager.registerTool(tool);
  }

  getToolStats(): any {
    return this.toolManager.getToolUsageStats();
  }

  getExecutionHistory(toolName?: string, limit?: number): any {
    return this.toolManager.getExecutionHistory(toolName, limit);
  }
}
```

## Dependencies

### Internal Dependencies

- `@tamma/shared/contracts` - ToolDefinition, ToolCall interfaces
- `@tamma/observability` - Logging utilities
- Story 2.1 tool and function calling interfaces

### External Dependencies

- None additional

## Testing Strategy

### Unit Tests

```typescript
// tests/providers/anthropic/function-calling/function-manager.test.ts
describe('FunctionManager', () => {
  describe('function registration', () => {
    it('should register valid functions');
    it('should reject invalid function definitions');
    it('should handle function overrides');
  });

  describe('function execution', () => {
    it('should execute functions with valid arguments');
    it('should handle execution timeouts');
    it('should validate function permissions');
  });
});

// tests/providers/anthropic/tools/tool-manager.test.ts
describe('ToolManager', () => {
  describe('tool execution', () => {
    it('should execute single tool calls');
    it('should execute multiple tools in parallel');
    it('should handle tool execution failures');
  });
});
```

### Integration Tests

```typescript
// tests/providers/anthropic/tools/integration.test.ts
describe('Tools Integration', () => {
  it('should handle real tool calls with Claude API');
  it('should process tool results correctly');
  it('should handle complex tool workflows');
});
```

## Risk Mitigation

### Security Risks

1. **Code Execution**: Arbitrary code execution through tools
   - Mitigation: Sandboxing, permission system, dangerous tool marking
2. **Data Exposure**: Sensitive data in tool outputs
   - Mitigation: Output sanitization, access controls
3. **Resource Exhaustion**: Tools consuming excessive resources
   - Mitigation: Timeouts, resource limits, monitoring

### Technical Risks

1. **Tool Dependencies**: Tools having complex dependencies
   - Mitigation: Dependency injection, isolation
2. **Error Propagation**: Tool errors breaking workflows
   - Mitigation: Error handling, graceful degradation
3. **Performance Impact**: Tool execution slowing responses
   - Mitigation: Parallel execution, caching, optimization

## Deliverables

1. **Function Manager**: Function registration and execution
2. **Tool Manager**: Tool orchestration and execution
3. **Tool Validator**: Schema validation and verification
4. **Result Handler**: Result processing and formatting
5. **Integration**: Updated provider with tool support
6. **Unit Tests**: Comprehensive test coverage
7. **Integration Tests**: Real API tool execution validation
8. **Documentation**: Tool development and usage guide

## Success Criteria

- [ ] Function calling with proper validation and execution
- [ ] Tool use capabilities with parallel execution
- [ ] Comprehensive tool definition and validation
- [ ] Robust result processing and error handling
- [ ] Security controls and permission system
- [ ] Performance optimization for tool workflows
- [ ] Comprehensive test coverage
- [ ] Clear documentation for tool developers

## File Structure

```
src/providers/anthropic/function-calling/
├── function-manager.ts         # Function registration and execution
└── index.ts                   # Public exports

src/providers/anthropic/tools/
├── tool-manager.ts           # Tool orchestration
├── tool-validator.ts         # Schema validation
├── result-handler.ts         # Result processing
└── index.ts                   # Public exports

tests/providers/anthropic/function-calling/
├── function-manager.test.ts
└── integration.test.ts

tests/providers/anthropic/tools/
├── tool-manager.test.ts
├── tool-validator.test.ts
├── result-handler.test.ts
└── integration.test.ts
```

This task provides comprehensive function calling and tool use capabilities that enable the Anthropic provider to interact with external systems, execute code, and perform complex operations while maintaining security, reliability, and performance.
