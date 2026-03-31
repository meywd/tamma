/**
 * Playwright E2E configuration for Tamma production services.
 *
 * Targets:
 *   - app.tamma.dev  (Dashboard)
 *   - api.tamma.dev  (Fastify API)
 *   - elsa.tamma.dev (ELSA Studio / Blazor WASM)
 *
 * All URLs are configurable via environment variables for testing
 * against staging or local environments.
 */

import { defineConfig, devices } from '@playwright/test';

const APP_URL = process.env['E2E_BASE_URL'] ?? 'https://app.tamma.dev';
const API_URL = process.env['E2E_API_URL'] ?? 'https://api.tamma.dev';
const ELSA_URL = process.env['E2E_ELSA_URL'] ?? 'https://elsa.tamma.dev';

export { APP_URL, API_URL, ELSA_URL };

export default defineConfig({
  testDir: './tests',
  fullyParallel: true,

  /* Fail CI on test.only */
  forbidOnly: !!process.env['CI'],

  /* Retry twice — services may be warming up after deploy */
  retries: process.env['CI'] ? 2 : 0,

  /* Single worker in CI to avoid flaky parallel network tests */
  workers: process.env['CI'] ? 1 : undefined,

  /* Reporter */
  reporter: process.env['CI']
    ? [['html', { open: 'never' }], ['github']]
    : [['html', { open: 'never' }]],

  /* Shared settings for all projects */
  use: {
    /* Base URL for navigation — dashboard */
    baseURL: APP_URL,

    /* Collect trace on first retry for debugging */
    trace: 'on-first-retry',

    /* Screenshot on failure */
    screenshot: 'only-on-failure',

    /* Ignore TLS errors (Cloudflare origin certs) */
    ignoreHTTPSErrors: true,

    /* Action timeout */
    actionTimeout: 15_000,
  },

  /* Global test timeout: 30 seconds */
  timeout: 30_000,

  /* Expect timeout */
  expect: {
    timeout: 10_000,
  },

  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],

  /* Output directory for screenshots and traces */
  outputDir: './test-results',
});
