import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import * as fs from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import type { AgentTaskResult } from '@tamma/shared';
import type {
  IAgentProviderFactory,
  ProviderChainEntry,
} from '@tamma/providers';
import type { IAgentProvider } from '@tamma/providers';
import {
  executeAgentCommand,
  type AgentExecutionRequestJson,
  type AgentExecutionResultJson,
} from './execute-agent.js';

// ---- Test helpers ----

async function makeTempDir(): Promise<string> {
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'tamma-execagent-'));
  return dir;
}

async function rmDir(dir: string): Promise<void> {
  try {
    await fs.rm(dir, { recursive: true, force: true });
  } catch {
    // ignore — best-effort cleanup
  }
}

function makeRequest(overrides: Partial<AgentExecutionRequestJson> = {}): AgentExecutionRequestJson {
  return {
    repository: 'meywd/tamma',
    branch_name: 'tamma/issue-42',
    issue_number: 42,
    issue_title: 'Fix login flow',
    task: 'implement',
    plan_json: '{"steps":[]}',
    tamma_session_id: 'sess_abc123',
    agent_provider: 'claude-code',
    agent_config_json: '{}',
    timeout_minutes: 30,
    ...overrides,
  };
}

async function writeRequestFile(
  dir: string,
  name: string,
  req: AgentExecutionRequestJson,
): Promise<string> {
  const p = path.join(dir, name);
  await fs.writeFile(p, JSON.stringify(req), 'utf-8');
  return p;
}

interface StubProviderResult {
  task: AgentTaskResult;
  throwOnExecute?: Error;
  throwOnCreate?: Error;
}

function stubFactory(
  result: StubProviderResult,
  capture?: { entry?: ProviderChainEntry },
): IAgentProviderFactory {
  return {
    async create(entry: ProviderChainEntry): Promise<IAgentProvider> {
      if (capture) capture.entry = entry;
      if (result.throwOnCreate) throw result.throwOnCreate;
      return {
        async executeTask(_config, _onProgress) {
          if (result.throwOnExecute) throw result.throwOnExecute;
          return result.task;
        },
        async isAvailable() {
          return true;
        },
        async dispose() {
          return;
        },
      };
    },
    register(_name, _creator) {
      /* not used in tests */
    },
    async dispose() {
      return;
    },
  };
}

// ---- Tests ----

describe('executeAgentCommand', () => {
  let workDir: string;
  let repoDir: string;

  beforeEach(async () => {
    workDir = await makeTempDir();
    repoDir = await makeTempDir();
  });

  afterEach(async () => {
    await rmDir(workDir);
    await rmDir(repoDir);
    vi.restoreAllMocks();
  });

  it('parses request, runs stub provider, writes snake_case result file, exits 0', async () => {
    const req = makeRequest();
    const reqPath = await writeRequestFile(workDir, 'req.json', req);
    const factory = stubFactory({
      task: { success: true, output: 'done', costUsd: 0, durationMs: 1234 },
    });

    const outcome = await executeAgentCommand({
      request: reqPath,
      providerFactory: factory,
      repoDir,
    });

    expect(outcome.exitCode).toBe(0);
    expect(outcome.resultPath).not.toBeNull();
    const raw = await fs.readFile(outcome.resultPath!, 'utf-8');
    const parsed = JSON.parse(raw) as AgentExecutionResultJson;

    // Shape: exact snake_case keys the C# collector expects.
    expect(parsed).toMatchObject({
      success: true,
      task: 'implement',
      issue_number: 42,
      branch_name: 'tamma/issue-42',
      tamma_session_id: 'sess_abc123',
      agent_provider: 'claude-code',
    });
    expect(Array.isArray(parsed.files_changed)).toBe(true);
    expect(parsed).toHaveProperty('pr_number');
    expect(parsed).toHaveProperty('commit_sha');
    expect(parsed).toHaveProperty('error_message');
    expect(parsed).toHaveProperty('agent_log_summary');
    expect(parsed).toHaveProperty('tokens_used');
    expect(parsed).toHaveProperty('duration_seconds');
    expect(parsed).toHaveProperty('agent_version');
  });

  it('writes success=false when the agent reports failure, still exits 0', async () => {
    const req = makeRequest();
    const reqPath = await writeRequestFile(workDir, 'req.json', req);
    const factory = stubFactory({
      task: {
        success: false,
        output: '',
        costUsd: 0,
        durationMs: 500,
        error: 'rate limited',
      },
    });

    const outcome = await executeAgentCommand({
      request: reqPath,
      providerFactory: factory,
      repoDir,
    });

    expect(outcome.exitCode).toBe(0);
    const parsed = JSON.parse(
      await fs.readFile(outcome.resultPath!, 'utf-8'),
    ) as AgentExecutionResultJson;
    expect(parsed.success).toBe(false);
    expect(parsed.error_message).toBe('rate limited');
  });

  it('writes success=false when the provider factory throws', async () => {
    const req = makeRequest({ agent_provider: 'bogus-provider' });
    const reqPath = await writeRequestFile(workDir, 'req.json', req);
    const factory = stubFactory({
      task: { success: true, output: '', costUsd: 0, durationMs: 0 },
      throwOnCreate: new Error('Unknown provider: bogus-provider'),
    });

    const outcome = await executeAgentCommand({
      request: reqPath,
      providerFactory: factory,
      repoDir,
    });

    expect(outcome.exitCode).toBe(0);
    const parsed = JSON.parse(
      await fs.readFile(outcome.resultPath!, 'utf-8'),
    ) as AgentExecutionResultJson;
    expect(parsed.success).toBe(false);
    expect(parsed.error_message).toContain('Unknown provider: bogus-provider');
    expect(parsed.agent_provider).toBe('bogus-provider');
  });

  it('writes success=false when executeTask throws', async () => {
    const req = makeRequest();
    const reqPath = await writeRequestFile(workDir, 'req.json', req);
    const factory = stubFactory({
      task: { success: true, output: '', costUsd: 0, durationMs: 0 },
      throwOnExecute: new Error('network unreachable'),
    });

    const outcome = await executeAgentCommand({
      request: reqPath,
      providerFactory: factory,
      repoDir,
    });

    expect(outcome.exitCode).toBe(0);
    const parsed = JSON.parse(
      await fs.readFile(outcome.resultPath!, 'utf-8'),
    ) as AgentExecutionResultJson;
    expect(parsed.success).toBe(false);
    expect(parsed.error_message).toContain('network unreachable');
  });

  it('honors --output flag and writes to the explicit path', async () => {
    const req = makeRequest();
    const reqPath = await writeRequestFile(workDir, 'req.json', req);
    const outputPath = path.join(workDir, 'custom-result-location.json');
    const factory = stubFactory({
      task: { success: true, output: 'ok', costUsd: 0, durationMs: 10 },
    });

    const outcome = await executeAgentCommand({
      request: reqPath,
      output: outputPath,
      providerFactory: factory,
      repoDir,
    });

    expect(outcome.exitCode).toBe(0);
    expect(outcome.resultPath).toBe(outputPath);
    const stat = await fs.stat(outputPath);
    expect(stat.isFile()).toBe(true);
  });

  it('defaults output path to exec-result-<sessionId>.json next to the request', async () => {
    const req = makeRequest({ tamma_session_id: 'sess_xyz' });
    const reqPath = await writeRequestFile(workDir, 'req.json', req);
    const factory = stubFactory({
      task: { success: true, output: '', costUsd: 0, durationMs: 0 },
    });

    const outcome = await executeAgentCommand({
      request: reqPath,
      providerFactory: factory,
      repoDir,
    });

    expect(outcome.resultPath).toBe(path.join(workDir, 'exec-result-sess_xyz.json'));
  });

  it('sanitizes session id in default output filename', async () => {
    const req = makeRequest({ tamma_session_id: 'sess/../../etc/passwd' });
    const reqPath = await writeRequestFile(workDir, 'req.json', req);
    const factory = stubFactory({
      task: { success: true, output: '', costUsd: 0, durationMs: 0 },
    });

    const outcome = await executeAgentCommand({
      request: reqPath,
      providerFactory: factory,
      repoDir,
    });

    // All `/` and `.` characters must be replaced — result file stays in workDir.
    expect(path.dirname(outcome.resultPath!)).toBe(workDir);
    expect(path.basename(outcome.resultPath!)).not.toContain('/');
    expect(path.basename(outcome.resultPath!)).toMatch(/^exec-result-[a-zA-Z0-9_-]+\.json$/);
  });

  it('returns exit code 2 with diagnostic when request file is missing', async () => {
    const missing = path.join(workDir, 'does-not-exist.json');
    const outcome = await executeAgentCommand({
      request: missing,
      providerFactory: stubFactory({
        task: { success: true, output: '', costUsd: 0, durationMs: 0 },
      }),
      repoDir,
    });

    expect(outcome.exitCode).toBe(2);
    expect(outcome.resultPath).toBeNull();
    expect(outcome.result).toBeNull();
    expect(outcome.diagnostic).toBeDefined();
    expect(outcome.diagnostic).toContain('failed to read request file');
  });

  it('returns exit code 2 when request file is malformed JSON', async () => {
    const p = path.join(workDir, 'bad.json');
    await fs.writeFile(p, '{not valid json', 'utf-8');

    const outcome = await executeAgentCommand({
      request: p,
      providerFactory: stubFactory({
        task: { success: true, output: '', costUsd: 0, durationMs: 0 },
      }),
      repoDir,
    });

    expect(outcome.exitCode).toBe(2);
    expect(outcome.diagnostic).toContain('failed to read request file');
  });

  it('returns exit code 2 when request file is missing a required field', async () => {
    const p = path.join(workDir, 'partial.json');
    // missing "agent_provider"
    const partial = {
      repository: 'meywd/tamma',
      branch_name: 'tamma/issue-1',
      issue_number: 1,
      task: 'implement',
      tamma_session_id: 'sess_1',
      timeout_minutes: 10,
    };
    await fs.writeFile(p, JSON.stringify(partial), 'utf-8');

    const outcome = await executeAgentCommand({
      request: p,
      providerFactory: stubFactory({
        task: { success: true, output: '', costUsd: 0, durationMs: 0 },
      }),
      repoDir,
    });

    expect(outcome.exitCode).toBe(2);
    expect(outcome.diagnostic).toContain('agent_provider');
  });

  it('passes model and apiKeyRef from agent_config_json to the provider factory', async () => {
    const req = makeRequest({
      agent_provider: 'openrouter',
      agent_config_json: JSON.stringify({
        model: 'anthropic/claude-3.5-sonnet',
        api_key_ref: 'OPENROUTER_API_KEY',
      }),
    });
    const reqPath = await writeRequestFile(workDir, 'req.json', req);
    const captured: { entry?: ProviderChainEntry } = {};
    const factory = stubFactory(
      { task: { success: true, output: 'ok', costUsd: 0, durationMs: 1 } },
      captured,
    );

    const outcome = await executeAgentCommand({
      request: reqPath,
      providerFactory: factory,
      repoDir,
    });

    expect(outcome.exitCode).toBe(0);
    expect(captured.entry).toBeDefined();
    expect(captured.entry?.provider).toBe('openrouter');
    expect(captured.entry?.model).toBe('anthropic/claude-3.5-sonnet');
    expect(captured.entry?.apiKeyRef).toBe('OPENROUTER_API_KEY');
  });

  it('result JSON contains exactly the AgentResultArtifact fields the C# collector expects', async () => {
    const req = makeRequest();
    const reqPath = await writeRequestFile(workDir, 'req.json', req);
    const factory = stubFactory({
      task: { success: true, output: 'ok', costUsd: 0, durationMs: 2000 },
    });

    const outcome = await executeAgentCommand({
      request: reqPath,
      providerFactory: factory,
      repoDir,
    });

    const parsed = JSON.parse(
      await fs.readFile(outcome.resultPath!, 'utf-8'),
    ) as Record<string, unknown>;

    // These are the property names read by
    // AgentResultCollectorService.ParseResultJson (C# uses snake_case).
    const expectedKeys = [
      'success',
      'task',
      'issue_number',
      'branch_name',
      'tamma_session_id',
      'files_changed',
      'pr_number',
      'commit_sha',
      'error_message',
      'agent_log_summary',
      'tokens_used',
      'duration_seconds',
      'agent_provider',
      'agent_version',
    ];
    for (const key of expectedKeys) {
      expect(parsed).toHaveProperty(key);
    }
    // Should not leak any camelCase keys that would desync the contract.
    for (const key of Object.keys(parsed)) {
      expect(key).not.toMatch(/[A-Z]/);
    }
  });

  it('happy-path integration: canned provider result round-trips through the result file', async () => {
    // Simulates what the C# LocalExecutor does:
    //   1. Write request file
    //   2. Shell out to execute-agent
    //   3. Read result file
    //   4. Parse with AgentResultCollectorService rules
    const req = makeRequest({
      task: 'refactor',
      issue_number: 101,
      branch_name: 'tamma/issue-101',
      tamma_session_id: 'sess_integration_01',
      agent_provider: 'claude-code',
    });
    const reqPath = await writeRequestFile(workDir, 'request.json', req);
    const outputPath = path.join(workDir, 'result.json');

    const cannedResult: AgentTaskResult = {
      success: true,
      output: 'Refactored auth module.',
      costUsd: 0.42,
      durationMs: 5_500,
    };
    const factory = stubFactory({ task: cannedResult });

    const outcome = await executeAgentCommand({
      request: reqPath,
      output: outputPath,
      providerFactory: factory,
      repoDir,
    });

    expect(outcome.exitCode).toBe(0);
    expect(outcome.result).not.toBeNull();

    const onDisk = JSON.parse(
      await fs.readFile(outputPath, 'utf-8'),
    ) as AgentExecutionResultJson;

    // Contract assertions — these mirror what the C#
    // AgentResultCollectorService.ParseResultJson reads.
    expect(onDisk.success).toBe(true);
    expect(onDisk.task).toBe('refactor');
    expect(onDisk.issue_number).toBe(101);
    expect(onDisk.branch_name).toBe('tamma/issue-101');
    expect(onDisk.tamma_session_id).toBe('sess_integration_01');
    expect(onDisk.agent_provider).toBe('claude-code');
    expect(onDisk.duration_seconds).toBeGreaterThanOrEqual(5); // durationMs=5500 → rounds to 6 or 5
    expect(onDisk.error_message).toBeNull();
    expect(onDisk.pr_number).toBeNull();
    expect(Array.isArray(onDisk.files_changed)).toBe(true);
  });
});
