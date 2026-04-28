import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const pkg = (name: string, sub = 'index.ts') =>
  resolve(__dirname, `packages/${name}/src/${sub}`);

export default defineConfig({
  plugins: [react()],
  // Resolve @tamma/* workspace imports straight from source during tests.
  // Each package's package.json points main/exports at ./dist/* (built
  // artifacts), but `pnpm vitest run` from the repo root does not run
  // `pnpm build` first, so cross-package imports fail to resolve. The
  // aliases below let vitest/vite read the TypeScript source directly,
  // which mirrors what users get when packages ARE built and avoids
  // requiring the entire workspace to compile cleanly before tests run.
  // Subpath aliases (e.g. @tamma/shared/telemetry) are listed first so
  // the longest-prefix match wins over the bare-package alias.
  resolve: {
    alias: [
      { find: /^@tamma\/shared\/contracts$/, replacement: pkg('shared', 'contracts/index.ts') },
      { find: /^@tamma\/shared\/telemetry$/, replacement: pkg('shared', 'telemetry/index.ts') },
      { find: /^@tamma\/shared\/types$/, replacement: pkg('shared', 'types/index.ts') },
      { find: /^@tamma\/shared\/utils$/, replacement: pkg('shared', 'utils/index.ts') },
      { find: /^@tamma\/shared\/errors$/, replacement: pkg('shared', 'errors.ts') },
      { find: /^@tamma\/shared\/config$/, replacement: pkg('shared', 'config/index.ts') },
      { find: /^@tamma\/shared$/, replacement: pkg('shared') },
      { find: /^@tamma\/intelligence\/vector-store$/, replacement: pkg('intelligence', 'vector-store/index.ts') },
      { find: /^@tamma\/intelligence\/indexer$/, replacement: pkg('intelligence', 'indexer/index.ts') },
      { find: /^@tamma\/intelligence\/knowledge-base$/, replacement: pkg('intelligence', 'knowledge-base/index.ts') },
      { find: /^@tamma\/intelligence\/rag$/, replacement: pkg('intelligence', 'rag/index.ts') },
      { find: /^@tamma\/intelligence\/context$/, replacement: pkg('intelligence', 'context/index.ts') },
      { find: /^@tamma\/intelligence$/, replacement: pkg('intelligence') },
      { find: /^@tamma\/mcp-client\/types$/, replacement: pkg('mcp-client', 'types.ts') },
      { find: /^@tamma\/mcp-client\/errors$/, replacement: pkg('mcp-client', 'errors.ts') },
      { find: /^@tamma\/mcp-client$/, replacement: pkg('mcp-client') },
      { find: /^@tamma\/providers$/, replacement: pkg('providers') },
      { find: /^@tamma\/orchestrator$/, replacement: pkg('orchestrator') },
      { find: /^@tamma\/platforms$/, replacement: pkg('platforms') },
      { find: /^@tamma\/events$/, replacement: pkg('events') },
      { find: /^@tamma\/gates$/, replacement: pkg('gates') },
      { find: /^@tamma\/cost-monitor$/, replacement: pkg('cost-monitor') },
      { find: /^@tamma\/observability$/, replacement: pkg('observability') },
      { find: /^@tamma\/scrum-master$/, replacement: pkg('scrum-master') },
      { find: /^@tamma\/workers$/, replacement: pkg('workers') },
    ],
  },
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
