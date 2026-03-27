import { describe, it, expect } from 'vitest';
import { validateRepoConfig } from '../validate-repo-config.js';
import type { IRepoConfig } from '../../types/repo-config.js';

describe('validateRepoConfig', () => {
  it('accepts empty config', () => {
    expect(() => validateRepoConfig({})).not.toThrow();
  });

  it('accepts valid full config', () => {
    const config: IRepoConfig = {
      engine: { approvalMode: 'auto', pollIntervalMs: 60_000 },
      roles: { implementer: { provider: 'anthropic', model: 'claude-sonnet-4-5' } },
      security: { sanitizeContent: true, blockedCommandPatterns: ['rm\\s+-rf'] },
      github: { issueLabels: ['tamma'], botUsername: 'tamma-bot' },
    };
    expect(() => validateRepoConfig(config)).not.toThrow();
  });

  it('rejects config with embedded API key (sk-)', () => {
    const config: IRepoConfig = {
      roles: {
        implementer: { provider: 'sk-ant-abc123' },
      },
    };
    expect(() => validateRepoConfig(config)).toThrow('secrets');
  });

  it('rejects config with embedded GitHub token (ghp_)', () => {
    const config: IRepoConfig = {
      roles: {
        implementer: { provider: 'anthropic', systemPrompt: 'Use token ghp_abcdef123456' },
      },
    };
    expect(() => validateRepoConfig(config)).toThrow('secrets');
  });

  it('rejects config with embedded GitLab token (glpat-)', () => {
    const config = {
      security: { note: 'glpat-sometoken' },
    } as unknown as IRepoConfig;
    expect(() => validateRepoConfig(config)).toThrow('secrets');
  });

  it('rejects secret embedded after a slash in a URL', () => {
    const config: IRepoConfig = {
      roles: {
        implementer: { provider: 'anthropic', systemPrompt: 'proxy at https://proxy.io/sk-ant-abc123' },
      },
    };
    expect(() => validateRepoConfig(config)).toThrow('secrets');
  });

  it('rejects secret embedded after a colon', () => {
    const config: IRepoConfig = {
      roles: {
        implementer: { provider: 'anthropic', systemPrompt: 'key:ghp_abc123def456' },
      },
    };
    expect(() => validateRepoConfig(config)).toThrow('secrets');
  });

  it('rejects secret embedded after a double-quote', () => {
    const config = {
      roles: {
        implementer: { provider: 'anthropic', systemPrompt: 'token="sk-ant-secret"' },
      },
    };
    expect(() => validateRepoConfig(config)).toThrow('secrets');
  });

  it('rejects AWS access key embedded in text', () => {
    const config: IRepoConfig = {
      roles: {
        implementer: { provider: 'anthropic', systemPrompt: 'aws key AKIAxxxxxxxxxxxxxxxx' },
      },
    };
    expect(() => validateRepoConfig(config)).toThrow('secrets');
  });

  it('does not false-positive on words containing secret prefixes', () => {
    const config: IRepoConfig = {
      roles: {
        implementer: { provider: 'anthropic', systemPrompt: 'Use the skill-set for tasking' },
      },
    };
    expect(() => validateRepoConfig(config)).not.toThrow();
  });

  it('rejects invalid approvalMode', () => {
    const config: IRepoConfig = {
      engine: { approvalMode: 'invalid' as any },
    };
    expect(() => validateRepoConfig(config)).toThrow('approvalMode');
  });

  it('rejects pollIntervalMs < 1000', () => {
    const config: IRepoConfig = {
      engine: { pollIntervalMs: 500 },
    };
    expect(() => validateRepoConfig(config)).toThrow('pollIntervalMs');
  });

  it('rejects ciPollIntervalMs < 1000', () => {
    const config: IRepoConfig = {
      engine: { ciPollIntervalMs: 100 },
    };
    expect(() => validateRepoConfig(config)).toThrow('ciPollIntervalMs');
  });

  it('rejects ciMonitorTimeoutMs < 10000', () => {
    const config: IRepoConfig = {
      engine: { ciMonitorTimeoutMs: 5000 },
    };
    expect(() => validateRepoConfig(config)).toThrow('ciMonitorTimeoutMs');
  });

  it('rejects invalid regex in blockedCommandPatterns', () => {
    const config: IRepoConfig = {
      security: { blockedCommandPatterns: ['[invalid'] },
    };
    expect(() => validateRepoConfig(config)).toThrow('not a valid regex');
  });

  it('rejects too many blocked patterns', () => {
    const config: IRepoConfig = {
      security: { blockedCommandPatterns: Array(101).fill('pattern') },
    };
    expect(() => validateRepoConfig(config)).toThrow('exceeds maximum');
  });

  it('rejects role with empty provider string', () => {
    const config: IRepoConfig = {
      roles: { implementer: { provider: '' } },
    };
    expect(() => validateRepoConfig(config)).toThrow('non-empty string');
  });

  it('rejects negative maxFetchSizeBytes', () => {
    const config: IRepoConfig = {
      security: { maxFetchSizeBytes: -1 },
    };
    expect(() => validateRepoConfig(config)).toThrow('maxFetchSizeBytes');
  });
});
