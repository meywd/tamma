# Task 6: Function Calling Support

## Overview

Implement comprehensive function calling support for OpenAI's function calling API, enabling the Tamma platform to execute tools, APIs, and external functions through AI-driven decisions.

## Objectives

- Implement OpenAI's function calling API with full parameter validation
- Add tool definition management and registration system
- Provide function execution sandbox with security controls
- Support parallel function calling and streaming responses

## Implementation Steps

### Subtask 6.1: Implement Function Calling API Integration

**Description**: Create a robust function calling system that integrates with OpenAI's function calling capabilities, including tool definitions, parameter validation, and response parsing.

**Implementation Details**:

1. **Create Function Calling Interface**:

```typescript
// packages/providers/src/interfaces/function-calling.interface.ts
export interface IFunctionCallingManager {
  registerTool(tool: ToolDefinition): void;
  unregisterTool(toolName: string): void;
  executeFunctionCall(call: FunctionCall): Promise<FunctionResult>;
  processFunctionCalls(response: ChatCompletionResponse): Promise<FunctionResult[]>;
  getAvailableTools(): ToolDefinition[];
  validateToolDefinition(tool: ToolDefinition): ValidationResult;
}

export interface ToolDefinition {
  type: 'function';
  function: {
    name: string;
    description: string;
    parameters: JSONSchema;
    strict?: boolean;
  };
  metadata?: {
    category: string;
    permissions: string[];
    timeout?: number;
    rateLimit?: RateLimit;
  };
}

export interface FunctionCall {
  id: string;
  type: 'function';
  function: {
    name: string;
    arguments: string; // JSON string
  };
}

export interface FunctionResult {
  toolCallId: string;
  functionName: string;
  success: boolean;
  result?: any;
  error?: string;
  executionTime: number;
  timestamp: string;
}

export interface JSONSchema {
  type: string;
  properties?: Record<string, JSONSchema>;
  required?: string[];
  items?: JSONSchema;
  enum?: any[];
  description?: string;
  format?: string;
  minimum?: number;
  maximum?: number;
  minLength?: number;
  maxLength?: number;
  pattern?: string;
  additionalProperties?: boolean | JSONSchema;
  anyOf?: JSONSchema[];
  allOf?: JSONSchema[];
  oneOf?: JSONSchema[];
  not?: JSONSchema;
}

export interface RateLimit {
  callsPerMinute: number;
  callsPerHour: number;
  callsPerDay: number;
}

export interface ValidationResult {
  valid: boolean;
  errors: ValidationError[];
}

export interface ValidationError {
  path: string;
  message: string;
  code: string;
}
```

2. **Implement Function Calling Manager**:

```typescript
// packages/providers/src/openai/openai-function-calling-manager.ts
import {
  IFunctionCallingManager,
  ToolDefinition,
  FunctionCall,
  FunctionResult,
  JSONSchema,
  ValidationResult,
  ValidationError,
} from '../interfaces/function-calling.interface';
import { EventEmitter } from 'events';
import { validate } from 'jsonschema';

export class OpenAIFunctionCallingManager extends EventEmitter implements IFunctionCallingManager {
  private tools: Map<string, ToolDefinition> = new Map();
  private toolExecutors: Map<string, ToolExecutor> = new Map();
  private executionHistory: FunctionResult[] = [];

  constructor() {
    super();
  }

  registerTool(tool: ToolDefinition): void {
    const validation = this.validateToolDefinition(tool);
    if (!validation.valid) {
      throw new Error(
        `Invalid tool definition: ${validation.errors.map((e) => e.message).join(', ')}`
      );
    }

    this.tools.set(tool.function.name, tool);
    this.emit('toolRegistered', { toolName: tool.function.name });
  }

  unregisterTool(toolName: string): void {
    if (this.tools.has(toolName)) {
      this.tools.delete(toolName);
      this.toolExecutors.delete(toolName);
      this.emit('toolUnregistered', { toolName });
    }
  }

  async executeFunctionCall(call: FunctionCall): Promise<FunctionResult> {
    const startTime = Date.now();
    const toolName = call.function.name;

    try {
      const tool = this.tools.get(toolName);
      if (!tool) {
        throw new Error(`Tool '${toolName}' not found`);
      }

      // Parse and validate arguments
      let args;
      try {
        args = JSON.parse(call.function.arguments);
      } catch (error) {
        throw new Error(`Invalid JSON in function arguments: ${error.message}`);
      }

      const validationResult = this.validateArguments(args, tool.function.parameters);
      if (!validationResult.valid) {
        throw new Error(
          `Argument validation failed: ${validationResult.errors.map((e) => e.message).join(', ')}`
        );
      }

      // Check permissions and rate limits
      await this.checkPermissions(tool);
      await this.checkRateLimit(tool);

      // Execute the function
      const executor = this.toolExecutors.get(toolName);
      if (!executor) {
        throw new Error(`No executor registered for tool '${toolName}'`);
      }

      const result = await this.executeWithTimeout(executor, args, tool.metadata?.timeout);
      const executionTime = Date.now() - startTime;

      const functionResult: FunctionResult = {
        toolCallId: call.id,
        functionName: toolName,
        success: true,
        result,
        executionTime,
        timestamp: new Date().toISOString(),
      };

      this.executionHistory.push(functionResult);
      this.emit('functionExecuted', functionResult);

      return functionResult;
    } catch (error) {
      const executionTime = Date.now() - startTime;

      const functionResult: FunctionResult = {
        toolCallId: call.id,
        functionName: toolName,
        success: false,
        error: error.message,
        executionTime,
        timestamp: new Date().toISOString(),
      };

      this.executionHistory.push(functionResult);
      this.emit('functionExecutionFailed', functionResult);

      return functionResult;
    }
  }

  async processFunctionCalls(response: ChatCompletionResponse): Promise<FunctionResult[]> {
    const toolCalls = response.choices[0]?.message?.tool_calls;
    if (!toolCalls || toolCalls.length === 0) {
      return [];
    }

    const results: FunctionResult[] = [];

    // Execute function calls in parallel if possible
    const promises = toolCalls.map((toolCall) => this.executeFunctionCall(toolCall));
    const resolvedResults = await Promise.allSettled(promises);

    for (const promiseResult of resolvedResults) {
      if (promiseResult.status === 'fulfilled') {
        results.push(promiseResult.value);
      } else {
        // This shouldn't happen as executeFunctionCall catches all errors
        results.push({
          toolCallId: 'unknown',
          functionName: 'unknown',
          success: false,
          error: promiseResult.reason?.message || 'Unknown error',
          executionTime: 0,
          timestamp: new Date().toISOString(),
        });
      }
    }

    return results;
  }

  getAvailableTools(): ToolDefinition[] {
    return Array.from(this.tools.values());
  }

  validateToolDefinition(tool: ToolDefinition): ValidationResult {
    const errors: ValidationError[] = [];

    // Validate basic structure
    if (!tool.function.name || typeof tool.function.name !== 'string') {
      errors.push({
        path: 'function.name',
        message: 'Tool name is required and must be a string',
        code: 'REQUIRED',
      });
    }

    if (!tool.function.description || typeof tool.function.description !== 'string') {
      errors.push({
        path: 'function.description',
        message: 'Tool description is required and must be a string',
        code: 'REQUIRED',
      });
    }

    if (!tool.function.parameters || typeof tool.function.parameters !== 'object') {
      errors.push({
        path: 'function.parameters',
        message: 'Tool parameters are required and must be an object',
        code: 'REQUIRED',
      });
    } else {
      // Validate JSON Schema
      const schemaValidation = this.validateJSONSchema(tool.function.parameters);
      if (!schemaValidation.valid) {
        errors.push(...schemaValidation.errors);
      }
    }

    // Validate tool name format
    if (tool.function.name && !/^[a-zA-Z_][a-zA-Z0-9_]*$/.test(tool.function.name)) {
      errors.push({
        path: 'function.name',
        message:
          'Tool name must be a valid identifier (letters, numbers, underscores, cannot start with number)',
        code: 'INVALID_FORMAT',
      });
    }

    return {
      valid: errors.length === 0,
      errors,
    };
  }

  registerExecutor(toolName: string, executor: ToolExecutor): void {
    if (!this.tools.has(toolName)) {
      throw new Error(`Cannot register executor for unknown tool '${toolName}'`);
    }
    this.toolExecutors.set(toolName, executor);
    this.emit('executorRegistered', { toolName });
  }

  private validateArguments(args: any, schema: JSONSchema): ValidationResult {
    const result = validate(args, schema);
    return {
      valid: result.valid,
      errors: result.errors.map((error) => ({
        path: error.property || 'root',
        message: error.message || 'Validation error',
        code: error.name || 'VALIDATION_ERROR',
      })),
    };
  }

  private validateJSONSchema(schema: JSONSchema): ValidationResult {
    const errors: ValidationError[] = [];

    // Basic JSON Schema validation
    if (!schema.type) {
      errors.push({
        path: 'type',
        message: 'Schema type is required',
        code: 'REQUIRED',
      });
    }

    if (schema.type === 'object' && schema.properties) {
      for (const [propName, propSchema] of Object.entries(schema.properties)) {
        const propValidation = this.validateJSONSchema(propSchema);
        if (!propValidation.valid) {
          errors.push(
            ...propValidation.errors.map((err) => ({
              ...err,
              path: `properties.${propName}.${err.path}`,
            }))
          );
        }
      }
    }

    return {
      valid: errors.length === 0,
      errors,
    };
  }

  private async checkPermissions(tool: ToolDefinition): Promise<void> {
    // In a real implementation, this would check user permissions
    // For now, we'll just log the check
    if (tool.metadata?.permissions) {
      this.emit('permissionChecked', {
        toolName: tool.function.name,
        permissions: tool.metadata.permissions,
      });
    }
  }

  private async checkRateLimit(tool: ToolDefinition): Promise<void> {
    // In a real implementation, this would check rate limits
    if (tool.metadata?.rateLimit) {
      const recentCalls = this.executionHistory.filter(
        (result) =>
          result.functionName === tool.function.name &&
          Date.now() - new Date(result.timestamp).getTime() < 60000 // Last minute
      );

      if (recentCalls.length >= (tool.metadata.rateLimit.callsPerMinute || Infinity)) {
        throw new Error(`Rate limit exceeded for tool '${tool.function.name}'`);
      }
    }
  }

  private async executeWithTimeout(
    executor: ToolExecutor,
    args: any,
    timeout?: number
  ): Promise<any> {
    if (!timeout) {
      return executor(args);
    }

    return Promise.race([
      executor(args),
      new Promise<never>((_, reject) => {
        setTimeout(() => reject(new Error('Function execution timeout')), timeout);
      }),
    ]);
  }
}

// Tool executor interface
export interface ToolExecutor {
  (args: any): Promise<any>;
}
```

3. **Add Built-in Tools**:

```typescript
// packages/providers/src/openai/built-in-tools/index.ts
import { ToolExecutor } from '../openai-function-calling-manager';

// File system tools
export const readFileTool: ToolExecutor = async (args: { path: string }) => {
  const fs = await import('fs/promises');
  try {
    const content = await fs.readFile(args.path, 'utf-8');
    return { content, success: true };
  } catch (error) {
    return { error: error.message, success: false };
  }
};

export const writeFileTool: ToolExecutor = async (args: { path: string; content: string }) => {
  const fs = await import('fs/promises');
  try {
    await fs.writeFile(args.path, args.content, 'utf-8');
    return { success: true };
  } catch (error) {
    return { error: error.message, success: false };
  }
};

export const listFilesTool: ToolExecutor = async (args: { path: string; pattern?: string }) => {
  const fs = await import('fs/promises');
  const path = await import('path');

  try {
    const files = await fs.readdir(args.path);
    const filteredFiles = args.pattern
      ? files.filter((file) => file.includes(args.pattern!))
      : files;

    const fileStats = await Promise.all(
      filteredFiles.map(async (file) => {
        const fullPath = path.join(args.path, file);
        const stats = await fs.stat(fullPath);
        return {
          name: file,
          isDirectory: stats.isDirectory(),
          size: stats.size,
          modified: stats.mtime.toISOString(),
        };
      })
    );

    return { files: fileStats, success: true };
  } catch (error) {
    return { error: error.message, success: false };
  }
};

// HTTP request tools
export const httpRequestTool: ToolExecutor = async (args: {
  url: string;
  method?: 'GET' | 'POST' | 'PUT' | 'DELETE';
  headers?: Record<string, string>;
  body?: string;
}) => {
  const https = await import('https');
  const http = await import('http');

  return new Promise((resolve) => {
    const client = args.url.startsWith('https:') ? https : http;
    const url = new URL(args.url);

    const options = {
      hostname: url.hostname,
      port: url.port || (args.url.startsWith('https:') ? 443 : 80),
      path: url.pathname + url.search,
      method: args.method || 'GET',
      headers: args.headers || {},
    };

    const req = client.request(options, (res) => {
      let data = '';
      res.on('data', (chunk) => (data += chunk));
      res.on('end', () => {
        resolve({
          status: res.statusCode,
          headers: res.headers,
          body: data,
          success: true,
        });
      });
    });

    req.on('error', (error) => {
      resolve({ error: error.message, success: false });
    });

    if (args.body) {
      req.write(args.body);
    }
    req.end();
  });
};

// System information tools
export const getSystemInfoTool: ToolExecutor = async () => {
  const os = await import('os');
  const process = await import('process');

  return {
    platform: os.platform(),
    arch: os.arch(),
    nodeVersion: process.version,
    totalMemory: os.totalmem(),
    freeMemory: os.freemem(),
    uptime: os.uptime(),
    success: true,
  };
};

// Tool definitions
export const builtInToolDefinitions = [
  {
    type: 'function' as const,
    function: {
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
    },
    metadata: {
      category: 'filesystem',
      permissions: ['file:read'],
      timeout: 10000,
    },
  },
  {
    type: 'function' as const,
    function: {
      name: 'write_file',
      description: 'Write content to a file',
      parameters: {
        type: 'object',
        properties: {
          path: {
            type: 'string',
            description: 'Path to the file to write',
          },
          content: {
            type: 'string',
            description: 'Content to write to the file',
          },
        },
        required: ['path', 'content'],
      },
    },
    metadata: {
      category: 'filesystem',
      permissions: ['file:write'],
      timeout: 10000,
    },
  },
  {
    type: 'function' as const,
    function: {
      name: 'list_files',
      description: 'List files and directories in a path',
      parameters: {
        type: 'object',
        properties: {
          path: {
            type: 'string',
            description: 'Path to the directory to list',
          },
          pattern: {
            type: 'string',
            description: 'Optional pattern to filter files',
          },
        },
        required: ['path'],
      },
    },
    metadata: {
      category: 'filesystem',
      permissions: ['file:read'],
      timeout: 5000,
    },
  },
  {
    type: 'function' as const,
    function: {
      name: 'http_request',
      description: 'Make an HTTP request to a URL',
      parameters: {
        type: 'object',
        properties: {
          url: {
            type: 'string',
            description: 'URL to make the request to',
          },
          method: {
            type: 'string',
            enum: ['GET', 'POST', 'PUT', 'DELETE'],
            description: 'HTTP method to use',
          },
          headers: {
            type: 'object',
            description: 'HTTP headers to include',
          },
          body: {
            type: 'string',
            description: 'Request body for POST/PUT requests',
          },
        },
        required: ['url'],
      },
    },
    metadata: {
      category: 'network',
      permissions: ['network:request'],
      timeout: 30000,
    },
  },
  {
    type: 'function' as const,
    function: {
      name: 'get_system_info',
      description: 'Get system information',
      parameters: {
        type: 'object',
        properties: {},
      },
    },
    metadata: {
      category: 'system',
      permissions: ['system:info'],
      timeout: 5000,
    },
  },
];
```

### Subtask 6.2: Add Tool Definition Management

**Description**: Create a comprehensive tool definition management system with registration, validation, categorization, and permission controls.

**Implementation Details**:

1. **Create Tool Registry**:

```typescript
// packages/providers/src/openai/tool-registry.ts
import { ToolDefinition, ValidationResult } from '../interfaces/function-calling.interface';
import { EventEmitter } from 'events';

export interface ToolRegistryConfig {
  allowUnknownTools: boolean;
  requirePermissions: boolean;
  defaultTimeout: number;
  maxTools: number;
}

export class ToolRegistry extends EventEmitter {
  private tools: Map<string, ToolDefinition> = new Map();
  private categories: Map<string, Set<string>> = new Map();
  private permissions: Map<string, Set<string>> = new Map();

  constructor(private config: ToolRegistryConfig) {
    super();
  }

  registerTool(tool: ToolDefinition): void {
    if (this.tools.size >= this.config.maxTools) {
      throw new Error(`Maximum number of tools (${this.config.maxTools}) reached`);
    }

    const validation = this.validateTool(tool);
    if (!validation.valid) {
      throw new Error(`Invalid tool: ${validation.errors.map((e) => e.message).join(', ')}`);
    }

    const toolName = tool.function.name;

    // Check for conflicts
    if (this.tools.has(toolName)) {
      throw new Error(`Tool '${toolName}' is already registered`);
    }

    // Register the tool
    this.tools.set(toolName, tool);

    // Update categories
    const category = tool.metadata?.category || 'uncategorized';
    if (!this.categories.has(category)) {
      this.categories.set(category, new Set());
    }
    this.categories.get(category)!.add(toolName);

    // Update permissions
    if (tool.metadata?.permissions) {
      for (const permission of tool.metadata.permissions) {
        if (!this.permissions.has(permission)) {
          this.permissions.set(permission, new Set());
        }
        this.permissions.get(permission)!.add(toolName);
      }
    }

    this.emit('toolRegistered', { toolName, tool });
  }

  unregisterTool(toolName: string): void {
    const tool = this.tools.get(toolName);
    if (!tool) {
      throw new Error(`Tool '${toolName}' is not registered`);
    }

    // Remove from tools
    this.tools.delete(toolName);

    // Remove from categories
    const category = tool.metadata?.category || 'uncategorized';
    const categoryTools = this.categories.get(category);
    if (categoryTools) {
      categoryTools.delete(toolName);
      if (categoryTools.size === 0) {
        this.categories.delete(category);
      }
    }

    // Remove from permissions
    if (tool.metadata?.permissions) {
      for (const permission of tool.metadata.permissions) {
        const permissionTools = this.permissions.get(permission);
        if (permissionTools) {
          permissionTools.delete(toolName);
          if (permissionTools.size === 0) {
            this.permissions.delete(permission);
          }
        }
      }
    }

    this.emit('toolUnregistered', { toolName });
  }

  getTool(toolName: string): ToolDefinition | undefined {
    return this.tools.get(toolName);
  }

  getAllTools(): ToolDefinition[] {
    return Array.from(this.tools.values());
  }

  getToolsByCategory(category: string): ToolDefinition[] {
    const toolNames = this.categories.get(category);
    if (!toolNames) return [];

    return Array.from(toolNames)
      .map((name) => this.tools.get(name))
      .filter((tool) => tool !== undefined) as ToolDefinition[];
  }

  getToolsByPermission(permission: string): ToolDefinition[] {
    const toolNames = this.permissions.get(permission);
    if (!toolNames) return [];

    return Array.from(toolNames)
      .map((name) => this.tools.get(name))
      .filter((tool) => tool !== undefined) as ToolDefinition[];
  }

  getCategories(): string[] {
    return Array.from(this.categories.keys());
  }

  getPermissions(): string[] {
    return Array.from(this.permissions.keys());
  }

  hasPermission(toolName: string, permission: string): boolean {
    const tool = this.tools.get(toolName);
    if (!tool) return false;

    return tool.metadata?.permissions?.includes(permission) || false;
  }

  validateTool(tool: ToolDefinition): ValidationResult {
    const errors: ValidationError[] = [];

    // Basic structure validation
    if (!tool.type || tool.type !== 'function') {
      errors.push({
        path: 'type',
        message: 'Tool type must be "function"',
        code: 'INVALID_TYPE',
      });
    }

    if (!tool.function) {
      errors.push({
        path: 'function',
        message: 'Tool function definition is required',
        code: 'REQUIRED',
      });
      return { valid: false, errors };
    }

    // Function validation
    if (!tool.function.name || typeof tool.function.name !== 'string') {
      errors.push({
        path: 'function.name',
        message: 'Function name is required and must be a string',
        code: 'REQUIRED',
      });
    }

    if (!tool.function.description || typeof tool.function.description !== 'string') {
      errors.push({
        path: 'function.description',
        message: 'Function description is required and must be a string',
        code: 'REQUIRED',
      });
    }

    if (!tool.function.parameters || typeof tool.function.parameters !== 'object') {
      errors.push({
        path: 'function.parameters',
        message: 'Function parameters are required and must be an object',
        code: 'REQUIRED',
      });
    }

    // Name format validation
    if (tool.function.name && !/^[a-zA-Z_][a-zA-Z0-9_]*$/.test(tool.function.name)) {
      errors.push({
        path: 'function.name',
        message: 'Function name must be a valid identifier',
        code: 'INVALID_FORMAT',
      });
    }

    // Description length validation
    if (tool.function.description && tool.function.description.length > 1000) {
      errors.push({
        path: 'function.description',
        message: 'Function description must be 1000 characters or less',
        code: 'TOO_LONG',
      });
    }

    // Metadata validation
    if (tool.metadata) {
      if (
        tool.metadata.timeout &&
        (typeof tool.metadata.timeout !== 'number' || tool.metadata.timeout <= 0)
      ) {
        errors.push({
          path: 'metadata.timeout',
          message: 'Timeout must be a positive number',
          code: 'INVALID_VALUE',
        });
      }

      if (tool.metadata.rateLimit) {
        const rateLimit = tool.metadata.rateLimit;
        if (
          rateLimit.callsPerMinute &&
          (typeof rateLimit.callsPerMinute !== 'number' || rateLimit.callsPerMinute <= 0)
        ) {
          errors.push({
            path: 'metadata.rateLimit.callsPerMinute',
            message: 'callsPerMinute must be a positive number',
            code: 'INVALID_VALUE',
          });
        }
      }
    }

    return {
      valid: errors.length === 0,
      errors,
    };
  }

  searchTools(query: string): ToolDefinition[] {
    const lowerQuery = query.toLowerCase();

    return Array.from(this.tools.values()).filter(
      (tool) =>
        tool.function.name.toLowerCase().includes(lowerQuery) ||
        tool.function.description.toLowerCase().includes(lowerQuery) ||
        tool.metadata?.category?.toLowerCase().includes(lowerQuery)
    );
  }

  exportTools(): string {
    const exportData = {
      version: '1.0',
      timestamp: new Date().toISOString(),
      tools: Array.from(this.tools.values()),
      categories: Object.fromEntries(
        Array.from(this.categories.entries()).map(([cat, tools]) => [cat, Array.from(tools)])
      ),
      permissions: Object.fromEntries(
        Array.from(this.permissions.entries()).map(([perm, tools]) => [perm, Array.from(tools)])
      ),
    };

    return JSON.stringify(exportData, null, 2);
  }

  importTools(exportData: string): void {
    try {
      const data = JSON.parse(exportData);

      if (!data.tools || !Array.isArray(data.tools)) {
        throw new Error('Invalid export data format');
      }

      for (const tool of data.tools) {
        try {
          this.registerTool(tool);
        } catch (error) {
          this.emit('importError', { tool: tool.function.name, error: error.message });
        }
      }

      this.emit('importCompleted', { imported: data.tools.length });
    } catch (error) {
      throw new Error(`Failed to import tools: ${error.message}`);
    }
  }
}
```

### Subtask 6.3: Add Function Execution Sandbox

**Description**: Implement a secure sandbox environment for function execution with resource limits, security controls, and isolation.

**Implementation Details**:

1. **Create Execution Sandbox**:

```typescript
// packages/providers/src/openai/execution-sandbox.ts
import { ToolExecutor, FunctionResult } from './openai-function-calling-manager';
import { ToolDefinition } from '../interfaces/function-calling.interface';

export interface SandboxConfig {
  maxExecutionTime: number;
  maxMemoryUsage: number;
  maxFileSize: number;
  allowedDomains: string[];
  blockedDomains: string[];
  allowedPaths: string[];
  blockedPaths: string[];
  enableNetworkAccess: boolean;
  enableFileSystemAccess: boolean;
  enableSystemCommands: boolean;
}

export interface SandboxContext {
  userId?: string;
  sessionId?: string;
  permissions: string[];
  resourceLimits: ResourceLimits;
}

export interface ResourceLimits {
  maxExecutionTime: number;
  maxMemoryUsage: number;
  maxFileSize: number;
  networkRequestsAllowed: number;
  fileOperationsAllowed: number;
}

export class ExecutionSandbox {
  private activeExecutions: Map<string, ExecutionState> = new Map();

  constructor(private config: SandboxConfig) {}

  async executeFunction(
    tool: ToolDefinition,
    executor: ToolExecutor,
    args: any,
    context: SandboxContext
  ): Promise<FunctionResult> {
    const executionId = this.generateExecutionId();
    const startTime = Date.now();

    const executionState: ExecutionState = {
      id: executionId,
      toolName: tool.function.name,
      startTime,
      context,
      status: 'running',
    };

    this.activeExecutions.set(executionId, executionState);

    try {
      // Apply security checks
      await this.performSecurityChecks(tool, args, context);

      // Apply resource limits
      const effectiveLimits = this.applyResourceLimits(tool, context);

      // Execute with timeout and monitoring
      const result = await this.executeWithMonitoring(executor, args, effectiveLimits, executionId);

      const executionTime = Date.now() - startTime;

      const functionResult: FunctionResult = {
        toolCallId: executionId,
        functionName: tool.function.name,
        success: true,
        result,
        executionTime,
        timestamp: new Date().toISOString(),
      };

      executionState.status = 'completed';
      executionState.result = functionResult;

      return functionResult;
    } catch (error) {
      const executionTime = Date.now() - startTime;

      const functionResult: FunctionResult = {
        toolCallId: executionId,
        functionName: tool.function.name,
        success: false,
        error: error.message,
        executionTime,
        timestamp: new Date().toISOString(),
      };

      executionState.status = 'failed';
      executionState.result = functionResult;

      return functionResult;
    } finally {
      this.activeExecutions.delete(executionId);
    }
  }

  private async performSecurityChecks(
    tool: ToolDefinition,
    args: any,
    context: SandboxContext
  ): Promise<void> {
    // Check permissions
    if (tool.metadata?.permissions) {
      for (const permission of tool.metadata.permissions) {
        if (!context.permissions.includes(permission)) {
          throw new Error(`Permission '${permission}' required for tool '${tool.function.name}'`);
        }
      }
    }

    // Check file system access
    if (!this.config.enableFileSystemAccess && this.requiresFileSystemAccess(tool, args)) {
      throw new Error(`File system access is disabled for tool '${tool.function.name}'`);
    }

    // Check network access
    if (!this.config.enableNetworkAccess && this.requiresNetworkAccess(tool, args)) {
      throw new Error(`Network access is disabled for tool '${tool.function.name}'`);
    }

    // Check system commands
    if (!this.config.enableSystemCommands && this.requiresSystemCommands(tool)) {
      throw new Error(`System commands are disabled for tool '${tool.function.name}'`);
    }

    // Validate file paths
    if (this.requiresFileSystemAccess(tool, args)) {
      this.validateFilePaths(args);
    }

    // Validate network domains
    if (this.requiresNetworkAccess(tool, args)) {
      this.validateNetworkDomains(args);
    }
  }

  private applyResourceLimits(tool: ToolDefinition, context: SandboxContext): ResourceLimits {
    return {
      maxExecutionTime: Math.min(
        tool.metadata?.timeout || this.config.maxExecutionTime,
        context.resourceLimits.maxExecutionTime
      ),
      maxMemoryUsage: Math.min(this.config.maxMemoryUsage, context.resourceLimits.maxMemoryUsage),
      maxFileSize: Math.min(this.config.maxFileSize, context.resourceLimits.maxFileSize),
      networkRequestsAllowed: context.resourceLimits.networkRequestsAllowed,
      fileOperationsAllowed: context.resourceLimits.fileOperationsAllowed,
    };
  }

  private async executeWithMonitoring(
    executor: ToolExecutor,
    args: any,
    limits: ResourceLimits,
    executionId: string
  ): Promise<any> {
    let memoryMonitor: MemoryMonitor | null = null;
    let timeoutHandle: NodeJS.Timeout | null = null;

    try {
      // Start memory monitoring
      memoryMonitor = new MemoryMonitor(limits.maxMemoryUsage);
      memoryMonitor.start();

      // Set up timeout
      const timeoutPromise = new Promise<never>((_, reject) => {
        timeoutHandle = setTimeout(() => {
          reject(new Error(`Execution timeout after ${limits.maxExecutionTime}ms`));
        }, limits.maxExecutionTime);
      });

      // Execute the function
      const executionPromise = executor(args);

      // Race between execution and timeout
      const result = await Promise.race([executionPromise, timeoutPromise]);

      // Check memory usage
      if (memoryMonitor && memoryMonitor.isMemoryExceeded()) {
        throw new Error(`Memory usage exceeded limit of ${limits.maxMemoryUsage} bytes`);
      }

      return result;
    } finally {
      if (memoryMonitor) {
        memoryMonitor.stop();
      }
      if (timeoutHandle) {
        clearTimeout(timeoutHandle);
      }
    }
  }

  private requiresFileSystemAccess(tool: ToolDefinition, args: any): boolean {
    const fileSystemTools = ['read_file', 'write_file', 'list_files'];
    return fileSystemTools.includes(tool.function.name) || tool.metadata?.category === 'filesystem';
  }

  private requiresNetworkAccess(tool: ToolDefinition, args: any): boolean {
    const networkTools = ['http_request', 'fetch_url', 'api_call'];
    return networkTools.includes(tool.function.name) || tool.metadata?.category === 'network';
  }

  private requiresSystemCommands(tool: ToolDefinition): boolean {
    const systemTools = ['execute_command', 'run_script', 'shell_command'];
    return systemTools.includes(tool.function.name) || tool.metadata?.category === 'system';
  }

  private validateFilePaths(args: any): void {
    const paths = this.extractFilePaths(args);

    for (const path of paths) {
      // Normalize path
      const normalizedPath = require('path').resolve(path);

      // Check blocked paths
      for (const blockedPath of this.config.blockedPaths) {
        if (normalizedPath.startsWith(blockedPath)) {
          throw new Error(`Access to path '${path}' is blocked`);
        }
      }

      // Check allowed paths (if specified)
      if (this.config.allowedPaths.length > 0) {
        const isAllowed = this.config.allowedPaths.some((allowedPath) =>
          normalizedPath.startsWith(allowedPath)
        );
        if (!isAllowed) {
          throw new Error(`Access to path '${path}' is not allowed`);
        }
      }
    }
  }

  private validateNetworkDomains(args: any): void {
    const urls = this.extractUrls(args);

    for (const url of urls) {
      try {
        const parsedUrl = new URL(url);
        const domain = parsedUrl.hostname;

        // Check blocked domains
        for (const blockedDomain of this.config.blockedDomains) {
          if (domain === blockedDomain || domain.endsWith(`.${blockedDomain}`)) {
            throw new Error(`Access to domain '${domain}' is blocked`);
          }
        }

        // Check allowed domains (if specified)
        if (this.config.allowedDomains.length > 0) {
          const isAllowed = this.config.allowedDomains.some(
            (allowedDomain) => domain === allowedDomain || domain.endsWith(`.${allowedDomain}`)
          );
          if (!isAllowed) {
            throw new Error(`Access to domain '${domain}' is not allowed`);
          }
        }
      } catch (error) {
        if (error.message.includes('blocked') || error.message.includes('not allowed')) {
          throw error;
        }
        throw new Error(`Invalid URL: ${url}`);
      }
    }
  }

  private extractFilePaths(args: any, paths: string[] = []): string[] {
    if (typeof args === 'string') {
      if (args.includes('/') || args.includes('\\')) {
        paths.push(args);
      }
    } else if (Array.isArray(args)) {
      for (const item of args) {
        this.extractFilePaths(item, paths);
      }
    } else if (typeof args === 'object' && args !== null) {
      for (const value of Object.values(args)) {
        this.extractFilePaths(value, paths);
      }
    }
    return paths;
  }

  private extractUrls(args: any, urls: string[] = []): string[] {
    if (typeof args === 'string') {
      if (args.startsWith('http://') || args.startsWith('https://')) {
        urls.push(args);
      }
    } else if (Array.isArray(args)) {
      for (const item of args) {
        this.extractUrls(item, urls);
      }
    } else if (typeof args === 'object' && args !== null) {
      for (const value of Object.values(args)) {
        this.extractUrls(value, urls);
      }
    }
    return urls;
  }

  private generateExecutionId(): string {
    return `exec_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
  }

  getActiveExecutions(): ExecutionState[] {
    return Array.from(this.activeExecutions.values());
  }

  cancelExecution(executionId: string): boolean {
    const execution = this.activeExecutions.get(executionId);
    if (execution && execution.status === 'running') {
      execution.status = 'cancelled';
      this.activeExecutions.delete(executionId);
      return true;
    }
    return false;
  }
}

// Memory monitoring utility
class MemoryMonitor {
  private startMemory: number;
  private interval: NodeJS.Timeout | null = null;

  constructor(private maxMemory: number) {
    this.startMemory = process.memoryUsage().heapUsed;
  }

  start(): void {
    this.interval = setInterval(() => {
      const currentMemory = process.memoryUsage().heapUsed;
      if (currentMemory > this.maxMemory) {
        this.stop();
        throw new Error(`Memory limit exceeded: ${currentMemory} > ${this.maxMemory}`);
      }
    }, 100);
  }

  stop(): void {
    if (this.interval) {
      clearInterval(this.interval);
      this.interval = null;
    }
  }

  isMemoryExceeded(): boolean {
    const currentMemory = process.memoryUsage().heapUsed;
    return currentMemory > this.maxMemory;
  }
}

interface ExecutionState {
  id: string;
  toolName: string;
  startTime: number;
  context: SandboxContext;
  status: 'running' | 'completed' | 'failed' | 'cancelled';
  result?: FunctionResult;
}
```

## Files to Create

1. **Core Interfaces**:
   - `packages/providers/src/interfaces/function-calling.interface.ts`

2. **Function Calling Implementation**:
   - `packages/providers/src/openai/openai-function-calling-manager.ts`
   - `packages/providers/src/openai/tool-registry.ts`
   - `packages/providers/src/openai/execution-sandbox.ts`

3. **Built-in Tools**:
   - `packages/providers/src/openai/built-in-tools/index.ts`
   - `packages/providers/src/openai/built-in-tools/filesystem-tools.ts`
   - `packages/providers/src/openai/built-in-tools/network-tools.ts`
   - `packages/providers/src/openai/built-in-tools/system-tools.ts`

4. **Updated Files**:
   - `packages/providers/src/openai/openai-provider.ts` (integrate function calling)

## Testing Requirements

### Unit Tests

```typescript
// packages/providers/src/__tests__/openai-function-calling-manager.test.ts
describe('OpenAIFunctionCallingManager', () => {
  let manager: OpenAIFunctionCallingManager;

  beforeEach(() => {
    manager = new OpenAIFunctionCallingManager();
  });

  describe('registerTool', () => {
    it('should register valid tool', () => {
      const tool: ToolDefinition = {
        type: 'function',
        function: {
          name: 'test_tool',
          description: 'A test tool',
          parameters: {
            type: 'object',
            properties: {
              input: { type: 'string' },
            },
          },
        },
      };

      expect(() => manager.registerTool(tool)).not.toThrow();
      expect(manager.getAvailableTools()).toContainEqual(tool);
    });

    it('should reject invalid tool', () => {
      const tool = {
        type: 'function',
        function: {
          name: 'invalid-name!',
          description: '',
          parameters: {},
        },
      } as ToolDefinition;

      expect(() => manager.registerTool(tool)).toThrow();
    });
  });

  describe('executeFunctionCall', () => {
    it('should execute function call successfully', async () => {
      const tool: ToolDefinition = {
        type: 'function',
        function: {
          name: 'echo_tool',
          description: 'Echo input',
          parameters: {
            type: 'object',
            properties: {
              message: { type: 'string' },
            },
            required: ['message'],
          },
        },
      };

      manager.registerTool(tool);
      manager.registerExecutor('echo_tool', async (args) => ({ echo: args.message }));

      const call: FunctionCall = {
        id: 'call-1',
        type: 'function',
        function: {
          name: 'echo_tool',
          arguments: JSON.stringify({ message: 'hello' }),
        },
      };

      const result = await manager.executeFunctionCall(call);
      expect(result.success).toBe(true);
      expect(result.result).toEqual({ echo: 'hello' });
    });
  });
});
```

```typescript
// packages/providers/src/__tests__/execution-sandbox.test.ts
describe('ExecutionSandbox', () => {
  let sandbox: ExecutionSandbox;
  let config: SandboxConfig;

  beforeEach(() => {
    config = {
      maxExecutionTime: 5000,
      maxMemoryUsage: 100 * 1024 * 1024, // 100MB
      maxFileSize: 10 * 1024 * 1024, // 10MB
      allowedDomains: ['example.com'],
      blockedDomains: ['malicious.com'],
      allowedPaths: ['/tmp'],
      blockedPaths: ['/etc'],
      enableNetworkAccess: true,
      enableFileSystemAccess: true,
      enableSystemCommands: false,
    };

    sandbox = new ExecutionSandbox(config);
  });

  describe('executeFunction', () => {
    it('should execute function within limits', async () => {
      const tool: ToolDefinition = {
        type: 'function',
        function: {
          name: 'simple_tool',
          description: 'Simple tool',
          parameters: { type: 'object', properties: {} },
        },
      };

      const executor = async () => ({ result: 'success' });
      const context: SandboxContext = {
        permissions: [],
        resourceLimits: {
          maxExecutionTime: 1000,
          maxMemoryUsage: 1024 * 1024,
          maxFileSize: 1024,
          networkRequestsAllowed: 1,
          fileOperationsAllowed: 1,
        },
      };

      const result = await sandbox.executeFunction(tool, executor, {}, context);
      expect(result.success).toBe(true);
      expect(result.result).toEqual({ result: 'success' });
    });

    it('should block unauthorized file access', async () => {
      const tool: ToolDefinition = {
        type: 'function',
        function: {
          name: 'file_tool',
          description: 'File tool',
          parameters: {
            type: 'object',
            properties: {
              path: { type: 'string' },
            },
          },
        },
        metadata: {
          category: 'filesystem',
          permissions: ['file:read'],
        },
      };

      const executor = async () => ({ result: 'success' });
      const context: SandboxContext = {
        permissions: [], // No file permissions
        resourceLimits: {
          maxExecutionTime: 1000,
          maxMemoryUsage: 1024 * 1024,
          maxFileSize: 1024,
          networkRequestsAllowed: 0,
          fileOperationsAllowed: 0,
        },
      };

      await expect(
        sandbox.executeFunction(tool, executor, { path: '/etc/passwd' }, context)
      ).rejects.toThrow("Permission 'file:read' required");
    });
  });
});
```

### Integration Tests

```typescript
// packages/providers/src/__tests__/integration/openai-function-calling.test.ts
describe('OpenAI Function Calling Integration', () => {
  let provider: OpenAIProvider;
  let testConfig: OpenAIProviderConfig;

  beforeAll(() => {
    testConfig = {
      apiKey: process.env.OPENAI_API_KEY || 'test-key',
      functionCalling: {
        enabled: true,
        autoExecute: true,
        maxConcurrentCalls: 5,
      },
    };
  });

  beforeEach(() => {
    provider = new OpenAIProvider(testConfig);
  });

  it('should handle function calling in chat completion', async () => {
    const request: MessageRequest = {
      id: 'func-test-1',
      userId: 'test-user',
      model: 'gpt-4',
      messages: [
        {
          role: 'user',
          content: 'What is the current time? Use the get_system_info tool to find out.',
        },
      ],
      tools: [
        {
          type: 'function',
          function: {
            name: 'get_system_info',
            description: 'Get system information',
            parameters: {
              type: 'object',
              properties: {},
            },
          },
        },
      ],
      temperature: 0.7,
    };

    const chunks = [];
    for await (const chunk of provider.sendMessage(request)) {
      chunks.push(chunk);
    }

    expect(chunks.length).toBeGreaterThan(0);
    // Verify function calling was triggered and executed
  }, 15000);
});
```

## Security Considerations

1. **Sandbox Security**:
   - Strict resource limits (CPU, memory, time)
   - File system access controls
   - Network access restrictions
   - Command injection prevention

2. **Input Validation**:
   - Comprehensive parameter validation
   - SQL injection prevention
   - XSS prevention in outputs
   - Path traversal protection

3. **Permission System**:
   - Granular permission controls
   - Role-based access control
   - Audit logging for all function calls
   - Permission escalation prevention

## Dependencies

### New Dependencies

```json
{
  "jsonschema": "^1.4.1"
}
```

### Dev Dependencies

```json
{
  "@types/jsonschema": "^1.1.1"
}
```

## Monitoring and Observability

1. **Metrics to Track**:
   - Function call success/failure rates
   - Execution times per tool
   - Resource usage patterns
   - Security violations

2. **Logging**:
   - All function executions with parameters
   - Security violations and blocks
   - Performance metrics
   - Error details and stack traces

3. **Alerts**:
   - High failure rates for specific tools
   - Security violations
   - Resource limit breaches
   - Performance degradation

## Acceptance Criteria

1. ✅ **Function Calling**: Full OpenAI function calling API support
2. ✅ **Tool Management**: Comprehensive tool registration and management
3. ✅ **Security**: Secure sandbox execution with proper controls
4. ✅ **Validation**: Complete parameter validation and type checking
5. ✅ **Performance**: Efficient execution with minimal overhead
6. ✅ **Built-in Tools**: Common tools for file, network, and system operations
7. ✅ **Testing**: Comprehensive unit and integration test coverage
8. ✅ **Documentation**: Clear API documentation and examples
9. ✅ **Monitoring**: Complete observability for function execution
10. ✅ **Error Handling**: Graceful handling of all error scenarios

## Success Metrics

- Function call success rate > 95%
- Average execution time < 2 seconds
- Zero security violations in production
- Complete audit trail for all function calls
- Tool registration validation accuracy > 99%
- Performance overhead < 10% on normal operations
