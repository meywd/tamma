import { describe, it, expect } from 'vitest';
import { resolveJwtSecret } from '../serve.js';

describe('resolveJwtSecret', () => {
  it('returns the real secret when JWT_SECRET is set in production', () => {
    const env: NodeJS.ProcessEnv = { NODE_ENV: 'production', JWT_SECRET: 'super-secret-prod-value' };
    expect(resolveJwtSecret(env)).toBe('super-secret-prod-value');
  });

  it('throws when JWT_SECRET is missing in production', () => {
    const env: NodeJS.ProcessEnv = { NODE_ENV: 'production' };
    expect(() => resolveJwtSecret(env)).toThrowError(
      /JWT_SECRET environment variable is required in production/,
    );
  });

  it('throws when JWT_SECRET is an empty string in production', () => {
    const env: NodeJS.ProcessEnv = { NODE_ENV: 'production', JWT_SECRET: '' };
    expect(() => resolveJwtSecret(env)).toThrowError(
      /JWT_SECRET environment variable is required in production/,
    );
  });

  it('returns the dev fallback when NODE_ENV is undefined and JWT_SECRET is unset', () => {
    const env: NodeJS.ProcessEnv = {};
    expect(resolveJwtSecret(env)).toBe('tamma-dev-jwt-secret');
  });

  it('returns the dev fallback when NODE_ENV is development and JWT_SECRET is unset', () => {
    const env: NodeJS.ProcessEnv = { NODE_ENV: 'development' };
    expect(resolveJwtSecret(env)).toBe('tamma-dev-jwt-secret');
  });

  it('returns the dev fallback when NODE_ENV is test and JWT_SECRET is unset', () => {
    const env: NodeJS.ProcessEnv = { NODE_ENV: 'test' };
    expect(resolveJwtSecret(env)).toBe('tamma-dev-jwt-secret');
  });

  it('returns the real secret when JWT_SECRET is set outside production', () => {
    const env: NodeJS.ProcessEnv = { NODE_ENV: 'development', JWT_SECRET: 'custom-dev-secret' };
    expect(resolveJwtSecret(env)).toBe('custom-dev-secret');
  });
});
