/**
 * `tamma execute-agent` command
 *
 * Shell-out target invoked by the C# LocalExecutor (Epic 19 / story 19-5).
 *
 * Contract (must stay in sync with
 *   apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/LocalExecutor.cs
 *   apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/AgentResultCollectorService.cs):
 *
 * Invocation:
 *   node packages/cli/dist/index.js execute-agent \
 *     --request <path-to-request.json> \
 *     [--output <path-to-result.json>]
 *
 * If --output is omitted the result is written next to the request file as
 * `exec-result-<sessionId>.json` (LocalExecutor always passes --output, so this
 * is just a developer-friendly default).
 *
 * Request JSON (snake_case, written by C# LocalExecutor.SerializeRequest):
 *   {
 *     "repository":        "owner/repo",
 *     "branch_name":       "tamma/issue-42",
 *     "issue_number":      42,
 *     "issue_title":       "Fix login flow",
 *     "task":              "implement",
 *     "plan_json":         "{...}",        // JSON-encoded string
 *     "tamma_session_id":  "sess_abc123",
 *     "agent_provider":    "claude-code",
 *     "agent_config_json": "{...}",        // JSON-encoded string
 *     "timeout_minutes":   30
 *   }
 *
 * Result JSON (snake_case, parsed by C# AgentResultCollectorService.ParseResultJson):
 *   {
 *     "success":           bool,
 *     "task":              string,
 *     "issue_number":      int,
 *     "branch_name":       string,
 *     "tamma_session_id":  string,
 *     "files_changed":     string[],
 *     "pr_number":         int | null,
 *     "commit_sha":        string,
 *     "error_message":     string | null,
 *     "agent_log_summary": string | null,
 *     "tokens_used":       int,
 *     "duration_seconds":  int,
 *     "agent_provider":    string,
 *     "agent_version":     string | null
 *   }
 *
 * Exit code is 0 on success (result file written, agent ran). It is 0 even if
 * the agent reports `success: false` — the failure is communicated through the
 * result file, which is the contract. Non-zero exit is reserved for protocol
 * failures (missing/invalid request file, factory blow-up before we can write
 * a result, etc.) so the C# side knows to surface a process-level diagnostic.
 */

import { execFile } from 'node:child_process';
import * as fs from 'node:fs/promises';
import * as path from 'node:path';
import { promisify } from 'node:util';
import type { AgentTaskResult } from '@tamma/shared';
import {
  AgentProviderFactory,
  type IAgentProviderFactory,
  type ProviderChainEntry,
} from '@tamma/providers';
import type { AgentTaskConfig } from '@tamma/providers';

const execFileAsync = promisify(execFile);

// ---- Protocol types ----

/** On-disk request file shape. snake_case to match C# LocalExecutor. */
export interface AgentExecutionRequestJson {
  repository: string;
  branch_name: string;
  issue_number: number;
  issue_title: string;
  task: string;
  plan_json: string;
  tamma_session_id: string;
  agent_provider: string;
  agent_config_json?: string;
  timeout_minutes: number;
}

/** On-disk result file shape. snake_case — matches AgentResultArtifact. */
export interface AgentExecutionResultJson {
  success: boolean;
  task: string;
  issue_number: number;
  branch_name: string;
  tamma_session_id: string;
  files_changed: string[];
  pr_number: number | null;
  commit_sha: string;
  error_message: string | null;
  agent_log_summary: string | null;
  tokens_used: number;
  duration_seconds: number;
  agent_provider: string;
  agent_version: string | null;
}

// ---- Options + result ----

export interface ExecuteAgentOptions {
  /** Path to the request JSON file. Required. */
  request: string;
  /** Optional path for the result file. Defaults to `<dir>/exec-result-<sessionId>.json`. */
  output?: string;
  /**
   * Optional provider factory override. Allows tests to inject a canned
   * provider without spawning the real Claude Code CLI. Default: construct a
   * fresh `AgentProviderFactory`.
   */
  providerFactory?: IAgentProviderFactory;
  /**
   * Optional working directory where the agent should run (typically the
   * cloned repo). Falls back to `process.env.TAMMA_REPO_DIR` and then
   * `process.cwd()`.
   */
  repoDir?: string;
}

export interface ExecuteAgentCommandResult {
  /** Exit code to report to the parent process. */
  exitCode: number;
  /** The result file we wrote (absolute path), if any. */
  resultPath: string | null;
  /** The result artifact we wrote. Useful for tests. */
  result: AgentExecutionResultJson | null;
  /** Human-readable explanation — written to stderr on non-zero exit. */
  diagnostic?: string;
}

// ---- Helpers ----

function resolveOutputPath(requestPath: string, output: string | undefined, sessionId: string): string {
  if (output !== undefined && output.length > 0) {
    return path.resolve(output);
  }
  const dir = path.dirname(path.resolve(requestPath));
  const safeId = sessionId.length > 0 ? sessionId.replace(/[^a-zA-Z0-9_-]/g, '_') : 'unknown';
  return path.join(dir, `exec-result-${safeId}.json`);
}

function resolveRepoDir(override: string | undefined): string {
  if (override !== undefined && override.length > 0) return override;
  const fromEnv = process.env['TAMMA_REPO_DIR'];
  if (fromEnv !== undefined && fromEnv.length > 0) return fromEnv;
  return process.cwd();
}

/**
 * Compose a single prompt string the agent will execute.
 *
 * We keep this deliberately simple — the C# pipeline already did all the heavy
 * lifting (planning, issue analysis, config). execute-agent's job is to hand
 * the agent the final brief.
 */
function buildAgentPrompt(request: AgentExecutionRequestJson): string {
  const lines: string[] = [];
  lines.push(`Task: ${request.task}`);
  lines.push(`Repository: ${request.repository}`);
  lines.push(`Branch: ${request.branch_name}`);
  lines.push(`Issue #${request.issue_number}: ${request.issue_title}`);
  if (request.plan_json.length > 0 && request.plan_json !== '{}' && request.plan_json !== 'null') {
    lines.push('');
    lines.push('Plan:');
    lines.push(request.plan_json);
  }
  return lines.join('\n');
}

/** Extract optional agent config (`model`, `max_budget_usd`, `allowed_tools`). */
interface AgentConfig {
  model?: string;
  max_budget_usd?: number;
  allowed_tools?: string[];
  permission_mode?: 'bypassPermissions' | 'default';
  api_key_ref?: string;
}

function parseAgentConfig(agentConfigJson: string | undefined): AgentConfig {
  if (agentConfigJson === undefined || agentConfigJson.length === 0) return {};
  try {
    const parsed = JSON.parse(agentConfigJson) as unknown;
    if (parsed === null || typeof parsed !== 'object') return {};
    return parsed as AgentConfig;
  } catch {
    return {};
  }
}

/**
 * Detect `claude` CLI availability. Used to emit a clean error instead of a
 * cryptic ENOENT when someone runs execute-agent without Claude Code installed.
 *
 * `claude-code` is the only built-in provider that requires an external binary
 * on PATH (`opencode` / `openrouter` / `zen-mcp` are SDK-based).
 */
async function isClaudeCliAvailable(): Promise<boolean> {
  try {
    await execFileAsync('claude', ['--version'], { timeout: 5000 });
    return true;
  } catch {
    return false;
  }
}

/**
 * Collect files changed in the repo working tree vs HEAD.
 *
 * Best-effort: if `git` isn't on PATH or repoDir is not a git work tree we
 * silently return [] — the agent may still have succeeded (e.g. a pure-analysis
 * task). The C# collector falls back to the GitHub compare API for GHA runs,
 * but for local runs we rely on this output.
 */
async function collectFilesChanged(repoDir: string): Promise<string[]> {
  try {
    const { stdout } = await execFileAsync(
      'git',
      ['diff', '--name-only', 'HEAD'],
      { cwd: repoDir, timeout: 10_000 },
    );
    const untrackedResult = await execFileAsync(
      'git',
      ['ls-files', '--others', '--exclude-standard'],
      { cwd: repoDir, timeout: 10_000 },
    );
    const lines = (stdout + '\n' + untrackedResult.stdout)
      .split('\n')
      .map((l) => l.trim())
      .filter((l) => l.length > 0);
    // De-duplicate while preserving order
    return Array.from(new Set(lines));
  } catch {
    return [];
  }
}

async function collectHeadSha(repoDir: string): Promise<string> {
  try {
    const { stdout } = await execFileAsync(
      'git',
      ['rev-parse', 'HEAD'],
      { cwd: repoDir, timeout: 5000 },
    );
    return stdout.trim();
  } catch {
    return '';
  }
}

async function readRequest(requestPath: string): Promise<AgentExecutionRequestJson> {
  const raw = await fs.readFile(requestPath, 'utf-8');
  const parsed = JSON.parse(raw) as unknown;
  if (parsed === null || typeof parsed !== 'object') {
    throw new Error(`Request file is not a JSON object: ${requestPath}`);
  }
  // Minimal structural validation — the C# side is the source of truth for the
  // full schema, we only need the fields we actually use.
  const record = parsed as Record<string, unknown>;
  const requiredStrings = [
    'repository',
    'branch_name',
    'task',
    'tamma_session_id',
    'agent_provider',
  ] as const;
  for (const key of requiredStrings) {
    if (typeof record[key] !== 'string') {
      throw new Error(`Request is missing required string field: ${key}`);
    }
  }
  if (typeof record['issue_number'] !== 'number') {
    throw new Error('Request is missing required number field: issue_number');
  }
  if (typeof record['timeout_minutes'] !== 'number') {
    throw new Error('Request is missing required number field: timeout_minutes');
  }
  const result: AgentExecutionRequestJson = {
    repository: record['repository'] as string,
    branch_name: record['branch_name'] as string,
    issue_number: record['issue_number'] as number,
    issue_title: (record['issue_title'] as string | undefined) ?? '',
    task: record['task'] as string,
    plan_json: (record['plan_json'] as string | undefined) ?? '',
    tamma_session_id: record['tamma_session_id'] as string,
    agent_provider: record['agent_provider'] as string,
    timeout_minutes: record['timeout_minutes'] as number,
  };
  if (typeof record['agent_config_json'] === 'string') {
    result.agent_config_json = record['agent_config_json'];
  }
  return result;
}

function buildFailedResult(
  request: AgentExecutionRequestJson | null,
  provider: string,
  errorMessage: string,
  durationSeconds: number,
  logSummary?: string,
): AgentExecutionResultJson {
  return {
    success: false,
    task: request?.task ?? '',
    issue_number: request?.issue_number ?? 0,
    branch_name: request?.branch_name ?? '',
    tamma_session_id: request?.tamma_session_id ?? '',
    files_changed: [],
    pr_number: null,
    commit_sha: '',
    error_message: errorMessage,
    agent_log_summary: logSummary ?? null,
    tokens_used: 0,
    duration_seconds: durationSeconds,
    agent_provider: provider,
    agent_version: null,
  };
}

async function writeResult(resultPath: string, result: AgentExecutionResultJson): Promise<void> {
  await fs.mkdir(path.dirname(resultPath), { recursive: true });
  await fs.writeFile(resultPath, JSON.stringify(result, null, 2), 'utf-8');
}

// ---- Main entry point ----

/**
 * Run the execute-agent command.
 *
 * Does NOT call `process.exit` — returns the exit code so tests can assert on
 * it without tearing down the test runner. The CLI wrapper in index.tsx
 * translates the return value into `process.exit`.
 */
export async function executeAgentCommand(
  options: ExecuteAgentOptions,
): Promise<ExecuteAgentCommandResult> {
  const startMs = Date.now();
  const durationSoFar = (): number => Math.max(0, Math.round((Date.now() - startMs) / 1000));

  // Step 1 — read request (any failure here is a protocol-level error: we
  // cannot even produce a well-formed result file because we don't know the
  // session id / task / issue_number).
  let request: AgentExecutionRequestJson;
  try {
    request = await readRequest(options.request);
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err);
    const diagnostic = `execute-agent: failed to read request file (${options.request}): ${message}`;
    return {
      exitCode: 2,
      resultPath: null,
      result: null,
      diagnostic,
    };
  }

  const providerName = request.agent_provider.length > 0 ? request.agent_provider : 'claude-code';
  const resultPath = resolveOutputPath(options.request, options.output, request.tamma_session_id);
  const repoDir = resolveRepoDir(options.repoDir);
  const agentConfig = parseAgentConfig(request.agent_config_json);

  // Step 2 — claude-code pre-flight.
  if (providerName === 'claude-code' && options.providerFactory === undefined) {
    const available = await isClaudeCliAvailable();
    if (!available) {
      const result = buildFailedResult(
        request,
        providerName,
        'claude-code CLI not found on PATH. Install it from https://docs.anthropic.com/claude/docs/claude-code or set agent_provider to a different provider.',
        durationSoFar(),
      );
      await writeResult(resultPath, result);
      return { exitCode: 0, resultPath, result };
    }
  }

  // Step 3 — build the agent.
  const factory = options.providerFactory ?? new AgentProviderFactory();
  const chainEntry: ProviderChainEntry = {
    provider: providerName,
    ...(agentConfig.model !== undefined ? { model: agentConfig.model } : {}),
    ...(agentConfig.api_key_ref !== undefined ? { apiKeyRef: agentConfig.api_key_ref } : {}),
  };

  // Step 4 — execute. We guard the whole section so any throw becomes a
  // well-formed failure result rather than a stack trace on stderr.
  let taskResult: AgentTaskResult;
  let agentLogSummary = '';
  try {
    const agent = await factory.create(chainEntry);
    const taskConfig: AgentTaskConfig = {
      prompt: buildAgentPrompt(request),
      cwd: repoDir,
      ...(agentConfig.model !== undefined ? { model: agentConfig.model } : {}),
      ...(agentConfig.max_budget_usd !== undefined ? { maxBudgetUsd: agentConfig.max_budget_usd } : {}),
      ...(agentConfig.allowed_tools !== undefined && agentConfig.allowed_tools.length > 0
        ? { allowedTools: agentConfig.allowed_tools }
        : {}),
      ...(agentConfig.permission_mode !== undefined
        ? { permissionMode: agentConfig.permission_mode }
        : {}),
    };
    try {
      taskResult = await agent.executeTask(taskConfig, (event) => {
        // Aggregate a compact log summary. We intentionally cap it to avoid
        // writing megabytes of progress events into the result file — the
        // agent's own tool logs are the authoritative record.
        if (agentLogSummary.length < 4096) {
          agentLogSummary += `[${event.type}] ${event.message}\n`;
        }
      });
    } finally {
      try {
        await agent.dispose();
      } catch {
        // ignore dispose failures — they don't affect the task result
      }
    }
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err);
    const result = buildFailedResult(
      request,
      providerName,
      `Agent execution failed: ${message}`,
      durationSoFar(),
      agentLogSummary.length > 0 ? agentLogSummary : undefined,
    );
    await writeResult(resultPath, result);
    return { exitCode: 0, resultPath, result };
  }

  // Step 5 — collect post-execution git state.
  const [filesChanged, headSha] = await Promise.all([
    collectFilesChanged(repoDir),
    collectHeadSha(repoDir),
  ]);

  const durationSeconds = Math.max(
    0,
    Math.round(taskResult.durationMs / 1000),
  ) || durationSoFar();

  const result: AgentExecutionResultJson = {
    success: taskResult.success,
    task: request.task,
    issue_number: request.issue_number,
    branch_name: request.branch_name,
    tamma_session_id: request.tamma_session_id,
    files_changed: filesChanged,
    pr_number: null, // local executor doesn't open PRs; that's a later workflow step
    commit_sha: headSha,
    error_message: taskResult.error ?? null,
    agent_log_summary:
      agentLogSummary.length > 0
        ? agentLogSummary
        : taskResult.output.length > 0
        ? taskResult.output.slice(-2048)
        : null,
    tokens_used: 0, // AgentTaskResult only carries costUsd; token accounting lives in cost-monitor
    duration_seconds: durationSeconds,
    agent_provider: providerName,
    agent_version: null,
  };

  await writeResult(resultPath, result);
  return { exitCode: 0, resultPath, result };
}
