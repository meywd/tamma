import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  test: {
    globals: true,
    environment: 'node',
    include: ['packages/**/*.{test,spec}.{ts,tsx}'],
    exclude: [
      '**/node_modules/**', 'node_modules', 'dist',
      '**/*.integration.test.ts', '**/*.e2e.test.ts',
      // Dashboard has its own vitest.config.ts with jsdom + jest-dom setup.
      // Run via: pnpm --filter @tamma/dashboard test
      'packages/dashboard/**',
      // dashboard-user (Story 18-5) also has its own vitest.config.ts with
      // jsdom + jest-dom + matchMedia/ResizeObserver setup. Run via:
      // pnpm --filter @tamma/dashboard-user test
      'packages/dashboard-user/**',
    ],
    coverage: {
      provider: 'v8',
      reporter: ['text', 'json', 'html', 'lcov'],
      include: ['packages/*/src/**/*.ts'],
      exclude: [
        '**/*.test.ts',
        '**/*.spec.ts',
        '**/*.types.ts',
        '**/index.ts',
        '**/types/**',
      ],
      thresholds: {
        lines: 80,
        branches: 75,
        functions: 85,
        statements: 80,
      },
    },
    testTimeout: 10000,
    hookTimeout: 10000,
    teardownTimeout: 10000,
    isolate: true,
    maxConcurrency: 5,
  },
});
