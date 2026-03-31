import { describe, it, expect } from 'vitest';
import { validateProvidersConfig } from '../validate-providers-config.js';
import type { IProvidersConfig } from '../../types/providers-config.js';

describe('validateProvidersConfig', () => {
  it('accepts valid config with one provider', () => {
    const config: IProvidersConfig = {
      providers: {
        anthropic: { apiKey: 'sk-test', defaultModel: 'claude-sonnet-4-5' },
      },
    };
    const warnings = validateProvidersConfig(config);
    expect(warnings).toHaveLength(0);
  });

  it('accepts config with multiple providers', () => {
    const config: IProvidersConfig = {
      providers: {
        anthropic: { apiKey: 'sk-test' },
        openrouter: { apiKey: 'sk-or-test', baseUrl: 'https://openrouter.ai/api' },
      },
      maxBudgetUsd: 10.0,
    };
    const warnings = validateProvidersConfig(config);
    expect(warnings).toHaveLength(0);
  });

  it('throws for empty providers object', () => {
    expect(() => validateProvidersConfig({ providers: {} })).toThrow('At least one provider');
  });

  it('throws for null providers', () => {
    expect(() => validateProvidersConfig({ providers: null } as any)).toThrow('non-null object');
  });

  it('throws for forbidden provider name __proto__', () => {
    expect(() => validateProvidersConfig({
      providers: { ['__proto__']: {} },
    } as any)).toThrow('forbidden');
  });

  it('throws for invalid provider name', () => {
    expect(() => validateProvidersConfig({
      providers: { 'INVALID NAME': {} },
    })).toThrow('must match');
  });

  it('warns for empty apiKey', () => {
    const config: IProvidersConfig = {
      providers: {
        anthropic: { apiKey: '' },
      },
    };
    const warnings = validateProvidersConfig(config);
    expect(warnings).toHaveLength(1);
    expect(warnings[0]).toContain('empty apiKey');
  });

  it('throws for negative maxBudgetUsd', () => {
    expect(() => validateProvidersConfig({
      providers: { anthropic: {} },
      maxBudgetUsd: -1,
    })).toThrow('maxBudgetUsd');
  });

  it('throws for invalid permissionMode', () => {
    expect(() => validateProvidersConfig({
      providers: { anthropic: {} },
      permissionMode: 'invalid' as any,
    })).toThrow('permissionMode');
  });

  it('throws for non-positive timeoutSeconds', () => {
    expect(() => validateProvidersConfig({
      providers: { anthropic: { timeoutSeconds: 0 } },
    })).toThrow('timeoutSeconds');
  });
});
