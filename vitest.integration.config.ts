import { defineConfig } from 'vitest/config';

/**
 * Vitest configuration for integration tests.
 * Runs only *.integration.test.ts files which are excluded from the main config.
 *
 * Usage:
 *   INTEGRATION_TEST_PG=true npx vitest run --config vitest.integration.config.ts
 */
export default defineConfig({
  test: {
    globals: true,
    environment: 'node',
    include: ['packages/**/*.integration.test.ts'],
    testTimeout: 30000,
    hookTimeout: 30000,
    teardownTimeout: 10000,
    isolate: true,
    maxConcurrency: 1, // Serial execution for DB tests
  },
});
