import { describe, it, expect } from 'vitest';
import { resolveConfig } from '../resolve-config.js';
import type { IProvidersConfig } from '../../types/providers-config.js';
import type { IRepoConfig } from '../../types/repo-config.js';

describe('resolveConfig', () => {
  const minimalProviders: IProvidersConfig = {
    providers: {
      anthropic: { apiKey: 'sk-test', defaultModel: 'claude-sonnet-4-5' },
    },
  };

  it('resolves with minimal providers and empty repo config', () => {
    const { config, warnings } = resolveConfig(minimalProviders, {});

    expect(warnings).toHaveLength(0);
    expect(config.mode).toBe('standalone');
    expect(config.logLevel).toBe('info');
    expect(config.agents).toBeDefined();
    expect(config.agents!.defaults.providerChain).toHaveLength(1);
    expect(config.agents!.defaults.providerChain[0]!.provider).toBe('anthropic');
    expect(config.agents!.defaults.providerChain[0]!.model).toBe('claude-sonnet-4-5');
  });

  it('wires provider credentials into chain entry config', () => {
    const providers: IProvidersConfig = {
      providers: {
        anthropic: {
          apiKey: 'sk-test-key',
          baseUrl: 'https://custom.api.com',
          timeoutSeconds: 30,
        },
      },
    };

    const { config } = resolveConfig(providers, {});
    const entry = config.agents!.defaults.providerChain[0]!;
    expect(entry.config).toBeDefined();
    expect(entry.config!['apiKey']).toBe('sk-test-key');
    expect(entry.config!['baseUrl']).toBe('https://custom.api.com');
    expect(entry.config!['timeoutSeconds']).toBe(30);
  });

  it('applies global budget from providers config', () => {
    const providers: IProvidersConfig = {
      providers: { anthropic: {} },
      maxBudgetUsd: 5.0,
      permissionMode: 'bypassPermissions',
    };

    const { config } = resolveConfig(providers, {});
    expect(config.agents!.defaults.maxBudgetUsd).toBe(5.0);
    expect(config.agents!.defaults.permissionMode).toBe('bypassPermissions');
  });

  it('resolves repo roles against providers', () => {
    const repoConfig: IRepoConfig = {
      roles: {
        implementer: {
          provider: 'anthropic',
          model: 'claude-opus-4-6',
          maxBudgetUsd: 10.0,
          allowedTools: ['Read', 'Write'],
        },
      },
    };

    const { config, warnings } = resolveConfig(minimalProviders, repoConfig);
    expect(warnings).toHaveLength(0);
    expect(config.agents!.roles).toBeDefined();
    const impl = config.agents!.roles!['implementer' as keyof typeof config.agents.roles];
    expect(impl).toBeDefined();
    expect(impl!.providerChain![0]!.provider).toBe('anthropic');
    expect(impl!.providerChain![0]!.model).toBe('claude-opus-4-6');
    expect(impl!.maxBudgetUsd).toBe(10.0);
    expect(impl!.allowedTools).toEqual(['Read', 'Write']);
  });

  it('warns and falls back when role references unknown provider', () => {
    const repoConfig: IRepoConfig = {
      roles: {
        implementer: {
          provider: 'nonexistent',
          model: 'some-model',
        },
      },
    };

    const { config, warnings } = resolveConfig(minimalProviders, repoConfig);
    expect(warnings).toHaveLength(1);
    expect(warnings[0]).toContain('nonexistent');
    expect(warnings[0]).toContain('anthropic');
    // Falls back to first available provider
    const impl = config.agents!.roles!['implementer' as keyof typeof config.agents.roles];
    expect(impl!.providerChain![0]!.provider).toBe('anthropic');
  });

  it('applies repo engine settings', () => {
    const repoConfig: IRepoConfig = {
      engine: {
        approvalMode: 'auto',
        pollIntervalMs: 60_000,
      },
    };

    const { config } = resolveConfig(minimalProviders, repoConfig);
    expect(config.engine.approvalMode).toBe('auto');
    expect(config.engine.pollIntervalMs).toBe(60_000);
    // Defaults still applied for unset fields
    expect(config.engine.ciPollIntervalMs).toBe(30_000);
  });

  it('applies repo security settings', () => {
    const repoConfig: IRepoConfig = {
      security: {
        sanitizeContent: false,
        maxFetchSizeBytes: 1024,
      },
    };

    const { config } = resolveConfig(minimalProviders, repoConfig);
    expect(config.security).toBeDefined();
    expect(config.security!.sanitizeContent).toBe(false);
    expect(config.security!.maxFetchSizeBytes).toBe(1024);
  });

  it('applies repo github settings', () => {
    const repoConfig: IRepoConfig = {
      github: {
        issueLabels: ['custom-label'],
        botUsername: 'my-bot',
      },
    };

    const { config } = resolveConfig(minimalProviders, repoConfig);
    expect(config.github.issueLabels).toEqual(['custom-label']);
    expect(config.github.botUsername).toBe('my-bot');
  });

  it('applies env overrides last', () => {
    const repoConfig: IRepoConfig = {
      engine: { approvalMode: 'cli' },
    };

    const { config } = resolveConfig(minimalProviders, repoConfig, {
      logLevel: 'debug',
      engine: { approvalMode: 'auto' } as any,
    });

    expect(config.logLevel).toBe('debug');
    expect(config.engine.approvalMode).toBe('auto');
  });

  it('applies phase role map from repo config', () => {
    const repoConfig: IRepoConfig = {
      phaseRoleMap: {
        ISSUE_SELECTION: 'analyst',
      },
    };

    const { config } = resolveConfig(minimalProviders, repoConfig);
    expect(config.agents!.phaseRoleMap).toBeDefined();
    expect(config.agents!.phaseRoleMap!['ISSUE_SELECTION' as keyof typeof config.agents.phaseRoleMap]).toBe('analyst');
  });

  it('handles empty providers gracefully', () => {
    const { config } = resolveConfig({ providers: {} }, {});
    expect(config.agents!.defaults.providerChain[0]!.provider).toBe('claude-code');
  });
});
