/**
 * Tests for the layered configuration system.
 *
 * Covers loadProvidersConfig, loadRepoConfig, the layered loadConfig path,
 * generateRepoConfigFile, and generateProvidersFile.
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import {
  loadConfig,
  loadProvidersConfig,
  loadRepoConfig,
  generateRepoConfigFile,
  generateProvidersFile,
} from './config.js';

vi.mock('node:fs');

describe('loadProvidersConfig', () => {
  beforeEach(() => {
    vi.mocked(fs.existsSync).mockReturnValue(false);
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('returns empty config when file does not exist', () => {
    const { config, warnings } = loadProvidersConfig();
    expect(config.providers).toEqual({});
    expect(warnings).toHaveLength(0);
  });

  it('parses valid providers.json', () => {
    const providersJson = JSON.stringify({
      providers: {
        anthropic: { apiKey: 'sk-test', defaultModel: 'claude-sonnet-4-5' },
      },
      maxBudgetUsd: 5.0,
    });

    vi.mocked(fs.existsSync).mockImplementation((p) => {
      return String(p).endsWith('providers.json');
    });
    vi.mocked(fs.readFileSync).mockReturnValue(providersJson);

    const { config, warnings } = loadProvidersConfig();
    expect(config.providers['anthropic']).toBeDefined();
    expect(config.providers['anthropic']!.apiKey).toBe('sk-test');
    expect(config.maxBudgetUsd).toBe(5.0);
    expect(warnings).toHaveLength(0);
  });

  it('returns empty config with warning when file is not valid JSON', () => {
    vi.mocked(fs.existsSync).mockReturnValue(true);
    vi.mocked(fs.readFileSync).mockReturnValue('not json {{{');

    const { config, warnings } = loadProvidersConfig();
    expect(config.providers).toEqual({});
    expect(warnings.length).toBeGreaterThan(0);
    expect(warnings[0]).toContain('JSON');
  });

  it('returns empty config when file has no providers field', () => {
    vi.mocked(fs.existsSync).mockReturnValue(true);
    vi.mocked(fs.readFileSync).mockReturnValue(JSON.stringify({ mode: 'standalone' }));

    const { config } = loadProvidersConfig();
    expect(config.providers).toEqual({});
  });

  it('returns empty config with warning when validation fails', () => {
    vi.mocked(fs.existsSync).mockReturnValue(true);
    // Empty providers object triggers "at least one provider" validation error
    vi.mocked(fs.readFileSync).mockReturnValue(JSON.stringify({ providers: {} }));

    const { config, warnings } = loadProvidersConfig();
    expect(config.providers).toEqual({});
    expect(warnings.length).toBeGreaterThan(0);
    expect(warnings[0]).toContain('Invalid');
  });

  it('warns for empty apiKey values', () => {
    vi.mocked(fs.existsSync).mockImplementation((p) => {
      return String(p).endsWith('providers.json');
    });
    vi.mocked(fs.readFileSync).mockReturnValue(JSON.stringify({
      providers: { anthropic: { apiKey: '' } },
    }));

    const { config, warnings } = loadProvidersConfig();
    expect(config.providers['anthropic']).toBeDefined();
    expect(warnings.length).toBeGreaterThan(0);
    expect(warnings[0]).toContain('empty apiKey');
  });

  it('returns empty config when file cannot be read', () => {
    vi.mocked(fs.existsSync).mockReturnValue(true);
    vi.mocked(fs.readFileSync).mockImplementation(() => { throw new Error('EACCES'); });

    const { config, warnings } = loadProvidersConfig();
    expect(config.providers).toEqual({});
    expect(warnings.length).toBeGreaterThan(0);
    expect(warnings[0]).toContain('Could not read');
  });
});

describe('loadRepoConfig', () => {
  beforeEach(() => {
    vi.mocked(fs.existsSync).mockReturnValue(false);
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('returns empty config when no config files exist', () => {
    const { config, legacyFallback } = loadRepoConfig();
    expect(config).toEqual({});
    expect(legacyFallback).toBe(false);
  });

  it('returns legacyFallback when custom path is provided', () => {
    const { legacyFallback } = loadRepoConfig('/custom/path.json');
    expect(legacyFallback).toBe(true);
  });

  it('reads .tamma/config.json when it exists', () => {
    const repoJson = JSON.stringify({
      engine: { approvalMode: 'auto' },
      roles: { implementer: { provider: 'anthropic' } },
    });

    vi.mocked(fs.existsSync).mockImplementation((p) => {
      return String(p).includes('.tamma') && String(p).endsWith('config.json');
    });
    vi.mocked(fs.readFileSync).mockReturnValue(repoJson);

    const { config, legacyFallback } = loadRepoConfig();
    expect(legacyFallback).toBe(false);
    expect(config.engine?.approvalMode).toBe('auto');
    expect(config.roles?.['implementer']?.provider).toBe('anthropic');
  });

  it('detects legacy TammaConfig in .tamma/config.json and falls back', () => {
    vi.mocked(fs.existsSync).mockImplementation((p) => {
      return String(p).includes('.tamma') && String(p).endsWith('config.json');
    });
    vi.mocked(fs.readFileSync).mockReturnValue(JSON.stringify({
      github: { token: 't', owner: 'o', repo: 'r' },
      agent: { model: 'claude-sonnet-4-5' },
    }));

    const { config, warnings, legacyFallback } = loadRepoConfig();
    expect(legacyFallback).toBe(true);
    expect(config).toEqual({});
    expect(warnings.some((w) => w.includes('full TammaConfig'))).toBe(true);
  });

  it('falls back to tamma.config.json with deprecation warning', () => {
    vi.mocked(fs.existsSync).mockImplementation((p) => {
      return String(p).endsWith('tamma.config.json');
    });

    const { warnings, legacyFallback } = loadRepoConfig();
    expect(legacyFallback).toBe(true);
    expect(warnings.some((w) => w.includes('DEPRECATED'))).toBe(true);
  });

  it('throws on invalid JSON in .tamma/config.json', () => {
    vi.mocked(fs.existsSync).mockImplementation((p) => {
      return String(p).includes('.tamma') && String(p).endsWith('config.json');
    });
    vi.mocked(fs.readFileSync).mockReturnValue('not json');

    expect(() => loadRepoConfig()).toThrow('valid JSON');
  });

  it('rejects repo config containing embedded secrets', () => {
    vi.mocked(fs.existsSync).mockImplementation((p) => {
      return String(p).includes('.tamma') && String(p).endsWith('config.json');
    });
    vi.mocked(fs.readFileSync).mockReturnValue(JSON.stringify({
      roles: { implementer: { provider: 'sk-ant-secret123' } },
    }));

    expect(() => loadRepoConfig()).toThrow('secrets');
  });
});

describe('loadConfig - layered path', () => {
  beforeEach(() => {
    vi.mocked(fs.existsSync).mockReturnValue(false);
    for (const key of Object.keys(process.env)) {
      if (key.startsWith('TAMMA_')) delete process.env[key];
    }
    delete process.env['GITHUB_TOKEN'];
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('uses new layered path when providers.json exists', () => {
    const homeTamma = path.join(os.homedir(), '.tamma', 'providers.json');

    vi.mocked(fs.existsSync).mockImplementation((p) => {
      return String(p) === homeTamma;
    });
    vi.mocked(fs.readFileSync).mockReturnValue(JSON.stringify({
      providers: {
        anthropic: { apiKey: 'sk-test', defaultModel: 'claude-opus-4-6' },
      },
      maxBudgetUsd: 10.0,
    }));

    const config = loadConfig({});
    // Should use the new layered path and wire the provider
    expect(config.agents).toBeDefined();
    expect(config.agents!.defaults.providerChain[0]!.provider).toBe('anthropic');
    expect(config.agents!.defaults.providerChain[0]!.model).toBe('claude-opus-4-6');
    expect(config.agents!.defaults.maxBudgetUsd).toBe(10.0);
  });

  it('uses new layered path when .tamma/config.json exists', () => {
    const repoConfig = path.resolve('.tamma', 'config.json');

    vi.mocked(fs.existsSync).mockImplementation((p) => {
      return String(p) === repoConfig;
    });
    vi.mocked(fs.readFileSync).mockReturnValue(JSON.stringify({
      engine: { approvalMode: 'auto' },
      security: { sanitizeContent: false },
    }));

    const config = loadConfig({});
    expect(config.engine.approvalMode).toBe('auto');
    expect(config.security?.sanitizeContent).toBe(false);
  });

  it('merges providers.json and .tamma/config.json together', () => {
    const homeTamma = path.join(os.homedir(), '.tamma', 'providers.json');
    const repoConfig = path.resolve('.tamma', 'config.json');

    vi.mocked(fs.existsSync).mockImplementation((p) => {
      const s = String(p);
      return s === homeTamma || s === repoConfig;
    });
    vi.mocked(fs.readFileSync).mockImplementation((p) => {
      const s = String(p);
      if (s === homeTamma) {
        return JSON.stringify({
          providers: { anthropic: { apiKey: 'sk-test', defaultModel: 'claude-sonnet-4-5' } },
        });
      }
      if (s === repoConfig) {
        return JSON.stringify({
          roles: { implementer: { provider: 'anthropic', model: 'claude-opus-4-6' } },
          engine: { approvalMode: 'auto' },
        });
      }
      return '';
    });

    const config = loadConfig({});
    expect(config.agents!.defaults.providerChain[0]!.provider).toBe('anthropic');
    expect(config.agents!.roles).toBeDefined();
    expect(config.engine.approvalMode).toBe('auto');
  });

  it('CLI flags override layered config', () => {
    const homeTamma = path.join(os.homedir(), '.tamma', 'providers.json');

    vi.mocked(fs.existsSync).mockImplementation((p) => {
      return String(p) === homeTamma;
    });
    vi.mocked(fs.readFileSync).mockReturnValue(JSON.stringify({
      providers: { anthropic: { apiKey: 'sk-test' } },
    }));

    const config = loadConfig({ approval: 'auto', verbose: true });
    expect(config.engine.approvalMode).toBe('auto');
    expect(config.logLevel).toBe('debug');
  });

  it('falls back to legacy path when --config flag is given', () => {
    const customPath = '/custom/my-config.json';
    vi.mocked(fs.existsSync).mockImplementation((p) => {
      return String(p) === path.resolve(customPath);
    });
    vi.mocked(fs.readFileSync).mockReturnValue(JSON.stringify({
      github: { token: 't', owner: 'o', repo: 'r' },
      agent: { model: 'claude-opus-4-6' },
    }));

    const config = loadConfig({ config: customPath });
    // Should use legacy path — agent field should be populated
    expect(config.agent.model).toBe('claude-opus-4-6');
  });
});

describe('generateRepoConfigFile', () => {
  it('generates valid JSON with engine and github settings', () => {
    const result = generateRepoConfigFile({
      owner: 'test-owner',
      repo: 'test-repo',
      labels: 'tamma,bug',
      approvalMode: 'auto',
    });

    const parsed = JSON.parse(result);
    expect(parsed.engine.approvalMode).toBe('auto');
    expect(parsed.github.issueLabels).toEqual(['tamma', 'bug']);
    expect(parsed.github.botUsername).toBe('tamma-bot');
    // Should NOT contain credentials
    expect(result).not.toContain('token');
    expect(result).not.toContain('apiKey');
  });

  it('includes role config when providerName is given', () => {
    const result = generateRepoConfigFile({
      owner: 'o',
      repo: 'r',
      labels: 'tamma',
      approvalMode: 'cli',
      providerName: 'anthropic',
      model: 'claude-opus-4-6',
      maxBudgetUsd: 5.0,
    });

    const parsed = JSON.parse(result);
    expect(parsed.roles.implementer.provider).toBe('anthropic');
    expect(parsed.roles.implementer.model).toBe('claude-opus-4-6');
    expect(parsed.roles.implementer.maxBudgetUsd).toBe(5.0);
  });

  it('defaults approvalMode to cli', () => {
    const result = generateRepoConfigFile({
      owner: 'o', repo: 'r', labels: 'tamma', approvalMode: 'other',
    });
    const parsed = JSON.parse(result);
    expect(parsed.engine.approvalMode).toBe('cli');
  });
});

describe('generateProvidersFile', () => {
  it('generates valid JSON with provider entries', () => {
    const result = generateProvidersFile([
      { name: 'anthropic', apiKey: 'sk-test', defaultModel: 'claude-sonnet-4-5' },
      { name: 'openrouter', apiKey: 'sk-or-test' },
    ]);

    const parsed = JSON.parse(result);
    expect(parsed.providers.anthropic.apiKey).toBe('sk-test');
    expect(parsed.providers.anthropic.defaultModel).toBe('claude-sonnet-4-5');
    expect(parsed.providers.openrouter.apiKey).toBe('sk-or-test');
    expect(parsed.providers.openrouter.defaultModel).toBeUndefined();
  });

  it('handles empty apiKey gracefully', () => {
    const result = generateProvidersFile([
      { name: 'anthropic', apiKey: '' },
    ]);

    const parsed = JSON.parse(result);
    // Empty apiKey should not be set
    expect(parsed.providers.anthropic.apiKey).toBeUndefined();
  });

  it('handles empty provider list', () => {
    const result = generateProvidersFile([]);
    const parsed = JSON.parse(result);
    expect(parsed.providers).toEqual({});
  });
});
