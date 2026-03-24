import { defineConfig } from 'vitest/config';

/**
 * Vitest configuration for E2E tests.
 * Runs only *.e2e.test.ts files which are excluded from the main config.
 *
 * Usage:
 *   npx vitest run --config vitest.e2e.config.ts
 */
export default defineConfig({
  test: {
    globals: true,
    environment: 'node',
    include: ['packages/**/*.e2e.test.ts'],
    testTimeout: 30000,
    hookTimeout: 30000,
    teardownTimeout: 10000,
    isolate: true,
    maxConcurrency: 5,
  },
});
