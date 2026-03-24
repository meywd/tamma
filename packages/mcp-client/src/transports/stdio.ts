/**
 * @tamma/mcp-client
 * Stdio transport implementation
 *
 * Communicates with MCP servers via subprocess stdin/stdout.
 * Messages are newline-delimited JSON.
 */

import { spawn, type ChildProcess } from 'node:child_process';
import { createInterface, type Interface as ReadlineInterface } from 'node:readline';
import {
  BaseTransport,
  type StdioTransportOptions,
} from './base.js';
import {
  type JSONRPCRequest,
  type JSONRPCNotification,
  serializeMessage,
  parseMessage,
} from '../utils/json-rpc.js';
import { MCPConnectionError, MCPTransportError } from '../errors.js';
import {
  createSandboxEnv,
  OutputCollector,
  ResourceMonitor,
  DEFAULT_SANDBOX_OPTIONS,
} from '../security/sandbox.js';

/**
 * Default timeout for connection (ms)
 */
const DEFAULT_TIMEOUT = 30000;

/**
 * Stdio transport for MCP servers
 *
 * Spawns a subprocess and communicates via stdin/stdout using
 * newline-delimited JSON (NDJSON) format.
 */
export class StdioTransport extends BaseTransport {
  private process?: ChildProcess;
  private readline?: ReadlineInterface;
  private readonly command: string;
  private readonly args: string[];
  private readonly env?: Record<string, string>;
  private readonly cwd?: string;
  private readonly timeout: number;
  private readonly sandboxed: boolean;
  private stderrCollector?: OutputCollector;
  private resourceMonitor?: ResourceMonitor;

  constructor(options: StdioTransportOptions) {
    super(options.serverName);
    this.command = options.command;
    this.args = options.args ?? [];
    this.env = options.env;
    this.cwd = options.cwd;
    this.timeout = options.timeout ?? DEFAULT_TIMEOUT;
    this.sandboxed = options.sandboxed ?? true;
  }

  /**
   * Connect to the MCP server by spawning the subprocess
   */
  async connect(): Promise<void> {
    if (this.connected) {
      return;
    }

    return new Promise<void>((resolve, reject) => {
      const timeoutId = setTimeout(() => {
        this.cleanup();
        reject(
          new MCPConnectionError(
            this.serverName,
            `Connection timed out after ${this.timeout}ms`
          )
        );
      }, this.timeout);

      try {
        // Build environment with sandbox settings if enabled
        let processEnv = {
          ...process.env,
          ...this.env,
        };

        if (this.sandboxed) {
          processEnv = createSandboxEnv(processEnv);
        }

        // Set up output collector for stderr
        this.stderrCollector = new OutputCollector(DEFAULT_SANDBOX_OPTIONS.maxOutputSize);

        // Set up resource monitor if sandboxed
        if (this.sandboxed) {
          this.resourceMonitor = new ResourceMonitor(
            {
              maxCpuTime: DEFAULT_SANDBOX_OPTIONS.maxCpuTime,
              maxMemory: DEFAULT_SANDBOX_OPTIONS.maxMemory,
            },
            (reason) => {
              this.emitError(
                new MCPTransportError(
                  this.serverName,
                  'stdio',
                  `Resource limit exceeded: ${reason}`
                )
              );
              this.forceKill();
            }
          );
        }

        // Spawn the process
        this.process = spawn(this.command, this.args, {
          env: processEnv,
          cwd: this.cwd,
          stdio: ['pipe', 'pipe', 'pipe'],
          // Detached allows killing the entire process group
          detached: !this.sandboxed,
        });

        // Start resource monitoring
        this.resourceMonitor?.start();

        // Handle process errors
        this.process.on('error', (error) => {
          clearTimeout(timeoutId);
          this.connected = false;
          this.emitError(
            new MCPConnectionError(
              this.serverName,
              `Failed to spawn process: ${error.message}`,
              { cause: error }
            )
          );
          reject(
            new MCPConnectionError(
              this.serverName,
              `Failed to spawn process: ${error.message}`,
              { cause: error }
            )
          );
        });

        // Handle process exit
        this.process.on('exit', (code, signal) => {
          this.connected = false;
          this.cleanup();
          this.emitClose();

          if (code !== 0 && code !== null) {
            this.emitError(
              new MCPTransportError(
                this.serverName,
                'stdio',
                `Process exited with code ${code}`
              )
            );
          } else if (signal) {
            this.emitError(
              new MCPTransportError(
                this.serverName,
                'stdio',
                `Process killed with signal ${signal}`
              )
            );
          }
        });

        // Handle stderr for debugging with output collection
        this.process.stderr?.on('data', (data: Buffer) => {
          // Collect stderr output with size limit
          const withinLimit = this.stderrCollector?.add(data) ?? true;

          if (!withinLimit && this.sandboxed) {
            this.emitError(
              new MCPTransportError(
                this.serverName,
                'stdio',
                'Stderr output limit exceeded'
              )
            );
          }

          // Log stderr but don't treat as error
          // MCP servers may output debug info to stderr
          const message = data.toString().trim();
          if (message) {
            console.debug(`[${this.serverName}] stderr: ${message}`);
          }
        });

        // Set up readline for stdout
        if (this.process.stdout) {
          this.readline = createInterface({
            input: this.process.stdout,
            crlfDelay: Infinity,
          });

          this.readline.on('line', (line) => {
            this.handleLine(line);
          });

          this.readline.on('close', () => {
            this.connected = false;
            this.emitClose();
          });
        }

        // Mark as connected once the process is spawned
        this.connected = true;
        clearTimeout(timeoutId);
        resolve();
      } catch (error) {
        clearTimeout(timeoutId);
        const err = error instanceof Error ? error : new Error(String(error));
        reject(
          new MCPConnectionError(
            this.serverName,
            `Failed to connect: ${err.message}`,
            { cause: err }
          )
        );
      }
    });
  }

  /**
   * Disconnect from the MCP server
   */
  async disconnect(): Promise<void> {
    if (!this.connected || !this.process) {
      return;
    }

    return new Promise<void>((resolve) => {
      const timeoutId = setTimeout(() => {
        // Force kill if graceful shutdown doesn't work
        this.forceKill();
        resolve();
      }, 5000);

      this.process?.once('exit', () => {
        clearTimeout(timeoutId);
        this.cleanup();
        resolve();
      });

      // Try graceful shutdown first
      this.process?.stdin?.end();
      this.process?.kill('SIGTERM');
    });
  }

  /**
   * Send a message to the server
   */
  async send(message: JSONRPCRequest | JSONRPCNotification): Promise<void> {
    if (!this.connected || !this.process?.stdin?.writable) {
      throw new MCPTransportError(
        this.serverName,
        'stdio',
        'Transport not connected'
      );
    }

    return new Promise<void>((resolve, reject) => {
      const serialized = serializeMessage(message) + '\n';

      this.process?.stdin?.write(serialized, 'utf8', (error) => {
        if (error) {
          reject(
            new MCPTransportError(
              this.serverName,
              'stdio',
              `Failed to send message: ${error.message}`,
              { cause: error }
            )
          );
        } else {
          resolve();
        }
      });
    });
  }

  /**
   * Handle a line from stdout
   */
  private handleLine(line: string): void {
    const trimmed = line.trim();
    if (!trimmed) {
      return;
    }

    try {
      const message = parseMessage(trimmed);
      this.emitMessage(message);
    } catch (error) {
      // Invalid JSON - emit error but don't crash
      const err = error instanceof Error ? error : new Error(String(error));
      this.emitError(
        new MCPTransportError(
          this.serverName,
          'stdio',
          `Failed to parse message: ${err.message}`,
          { cause: err }
        )
      );
    }
  }

  /**
   * Force kill the process
   */
  private forceKill(): void {
    if (this.process) {
      try {
        // Kill the process group if detached
        if (this.process.pid && !this.sandboxed) {
          process.kill(-this.process.pid, 'SIGKILL');
        } else {
          this.process.kill('SIGKILL');
        }
      } catch {
        // Process may already be dead
      }
    }
    this.cleanup();
  }

  /**
   * Clean up resources
   */
  private cleanup(): void {
    this.connected = false;

    // Stop resource monitoring
    this.resourceMonitor?.stop();
    delete this.resourceMonitor;

    // Clear output collector
    this.stderrCollector?.reset();
    delete this.stderrCollector;

    if (this.readline) {
      this.readline.close();
      delete this.readline;
    }

    if (this.process) {
      this.process.stdin?.destroy();
      this.process.stdout?.destroy();
      this.process.stderr?.destroy();
      delete this.process;
    }
  }

  /**
   * Get collected stderr output
   */
  getStderr(): string {
    return this.stderrCollector?.toString() ?? '';
  }

  /**
   * Check if stderr was truncated
   */
  wasStderrTruncated(): boolean {
    return this.stderrCollector?.wasTruncated() ?? false;
  }

  /**
   * Get the process PID (useful for debugging)
   */
  getPid(): number | undefined {
    return this.process?.pid;
  }
}
