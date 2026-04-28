/**
 * @tamma/mcp-client
 * Configuration validator
 */

import { z } from 'zod';
import type { MCPClientConfig, MCPServerConfig } from '../types.js';
import { MCPValidationError } from '../errors.js';

/**
 * Zod schema for server configuration
 */
const serverConfigSchema = z.object({
  name: z.string().min(1, 'Server name is required'),
  transport: z.enum(['stdio', 'sse', 'websocket']),

  // Stdio options
  command: z.string().optional(),
  args: z.array(z.string()).optional(),
  env: z.record(z.string(), z.string()).optional(),
  cwd: z.string().optional(),

  // SSE/WebSocket options
  url: z.url().optional(),
  headers: z.record(z.string(), z.string()).optional(),

  // Common options
  timeout: z.number().positive().optional(),
  enabled: z.boolean().optional(),
  autoConnect: z.boolean().optional(),
  reconnectOnError: z.boolean().optional(),
  maxReconnectAttempts: z.number().nonnegative().optional(),

  // Rate limiting
  rateLimitRpm: z.number().positive().optional(),

  // Security
  sandboxed: z.boolean().optional(),
}).refine(
  (data) => {
    // Stdio requires command
    if (data.transport === 'stdio' && !data.command) {
      return false;
    }
    return true;
  },
  { message: 'Stdio transport requires a command' }
).refine(
  (data) => {
    // SSE/WebSocket requires url
    if ((data.transport === 'sse' || data.transport === 'websocket') && !data.url) {
      return false;
    }
    return true;
  },
  { message: 'SSE/WebSocket transport requires a URL' }
);

/**
 * Zod schema for client configuration
 */
const clientConfigSchema = z.object({
  servers: z.array(serverConfigSchema).min(1, 'At least one server is required'),
  defaultTimeout: z.number().positive().default(30000),
  retryAttempts: z.number().nonnegative().default(3),
  retryDelayMs: z.number().positive().default(1000),
  enableCaching: z.boolean().default(true),
  cacheTTLMs: z.number().positive().default(300000),
  logLevel: z.enum(['debug', 'info', 'warn', 'error']).default('info'),
});

/**
 * Validate client configuration
 * Throws MCPValidationError if invalid
 */
export function validateClientConfig(config: unknown): MCPClientConfig {
  const result = clientConfigSchema.safeParse(config);

  if (!result.success) {
    const errors = result.error.issues.map((e) => ({
      path: e.path.join('.'),
      message: e.message,
    }));

    throw new MCPValidationError(
      `Invalid MCP client configuration: ${errors.map((e) => e.message).join('; ')}`,
      { errors }
    );
  }

  return result.data;
}

/**
 * Validate server configuration
 * Throws MCPValidationError if invalid
 */
export function validateServerConfig(config: unknown): MCPServerConfig {
  const result = serverConfigSchema.safeParse(config);

  if (!result.success) {
    const errors = result.error.issues.map((e) => ({
      path: e.path.join('.'),
      message: e.message,
    }));

    throw new MCPValidationError(
      `Invalid MCP server configuration: ${errors.map((e) => e.message).join('; ')}`,
      { errors }
    );
  }

  return result.data;
}

/**
 * Allowlist of commands for stdio transport
 */
const ALLOWED_COMMANDS = new Set([
  'npx',
  'node',
  'python',
  'python3',
  'uvx',
  'deno',
  'bun',
]);

/**
 * Validate stdio command against allowlist
 */
export function validateStdioCommand(command: string): void {
  // Extract the base command (first word)
  const baseCommand = command.split(/\s+/)[0]?.split('/').pop();

  if (!baseCommand || !ALLOWED_COMMANDS.has(baseCommand)) {
    throw new MCPValidationError(
      `Command '${command}' is not in the allowed list. Allowed commands: ${Array.from(ALLOWED_COMMANDS).join(', ')}`,
      { command, allowedCommands: Array.from(ALLOWED_COMMANDS) }
    );
  }
}

/**
 * Validate URL for SSE/WebSocket transport
 */
export function validateTransportUrl(url: string): void {
  try {
    const parsed = new URL(url);

    // Only allow http(s) for SSE, ws(s) for WebSocket
    const allowedProtocols = ['http:', 'https:', 'ws:', 'wss:'];
    if (!allowedProtocols.includes(parsed.protocol)) {
      throw new MCPValidationError(
        `Invalid URL protocol '${parsed.protocol}'. Allowed: ${allowedProtocols.join(', ')}`,
        { url, protocol: parsed.protocol }
      );
    }

    // Block localhost alternatives that might bypass security
    const blockedHosts = new Set([
      '0.0.0.0',
      '[::]',
      '[::1]',
      '127.0.0.1',
      'localhost',
    ]);

    // Check for common private IP prefixes
    const isPrivateIP =
      parsed.hostname.startsWith('10.') ||
      parsed.hostname.startsWith('192.168.') ||
      /^172\.(1[6-9]|2[0-9]|3[01])\./.test(parsed.hostname);

    if (blockedHosts.has(parsed.hostname) || isPrivateIP) {
      throw new MCPValidationError(
        `Host '${parsed.hostname}' is not allowed for security reasons`,
        { url, hostname: parsed.hostname }
      );
    }
  } catch (error) {
    if (error instanceof MCPValidationError) {
      throw error;
    }
    throw new MCPValidationError(`Invalid URL: ${url}`, { url });
  }
}

/**
 * Sanitize environment variables
 * Removes sensitive variables that shouldn't be passed to child processes
 */
export function sanitizeEnv(env: Record<string, string>): Record<string, string> {
  const sensitiveKeys = new Set([
    // Common secret patterns
    'SECRET',
    'PASSWORD',
    'PRIVATE_KEY',
    'API_SECRET',
    'AUTH_SECRET',
    // AWS
    'AWS_SECRET_ACCESS_KEY',
    'AWS_SESSION_TOKEN',
    // SSH
    'SSH_AUTH_SOCK',
    'SSH_AGENT_PID',
  ]);

  const sanitized: Record<string, string> = {};

  /** Suffixes that indicate sensitive credentials and should always be filtered */
  const sensitiveSuffixes = ['_TOKEN', '_SECRET', '_CREDENTIAL'];

  for (const [key, value] of Object.entries(env)) {
    // Skip if key contains sensitive patterns
    const upperKey = key.toUpperCase();
    let isSensitive = false;

    // Check if the key ends with a sensitive suffix (e.g., GITHUB_TOKEN, API_SECRET, DB_CREDENTIAL)
    for (const suffix of sensitiveSuffixes) {
      if (upperKey.endsWith(suffix)) {
        isSensitive = true;
        break;
      }
    }

    // Check if the key contains any known sensitive substring
    if (!isSensitive) {
      for (const sensitiveKey of sensitiveKeys) {
        if (upperKey.includes(sensitiveKey)) {
          isSensitive = true;
          break;
        }
      }
    }

    if (!isSensitive) {
      sanitized[key] = value;
    }
  }

  return sanitized;
}

/**
 * Check if a config contains valid server names (unique, no reserved names)
 */
export function validateServerNames(configs: MCPServerConfig[]): void {
  const names = new Set<string>();
  const reserved = new Set(['default', 'all', 'none']);

  for (const config of configs) {
    if (reserved.has(config.name.toLowerCase())) {
      throw new MCPValidationError(
        `Server name '${config.name}' is reserved and cannot be used`,
        { serverName: config.name, reserved: Array.from(reserved) }
      );
    }

    if (names.has(config.name)) {
      throw new MCPValidationError(
        `Duplicate server name '${config.name}'`,
        { serverName: config.name }
      );
    }

    names.add(config.name);
  }
}
