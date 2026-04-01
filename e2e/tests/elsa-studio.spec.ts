/**
 * ELSA Studio tests — verify the Blazor WASM app at elsa.tamma.dev.
 *
 * Tests that the ELSA Studio loads correctly, including the Blazor
 * framework, login page, and workflow list. Designed to fail clearly
 * when the ELSA services are down.
 */

import { test, expect } from '@playwright/test';
import { ELSA_URL } from '../playwright.config';

test.describe('ELSA Studio Tests', () => {
  test('Studio WASM loads (Blazor framework JS present)', async ({ page }) => {
    try {
      const response = await page.goto(ELSA_URL, {
        waitUntil: 'domcontentloaded',
        timeout: 25_000,
      });

      if (!response) {
        throw new Error(`No response from ELSA Studio at ${ELSA_URL}`);
      }

      expect(
        response.status(),
        `ELSA Studio at ${ELSA_URL} returned HTTP ${response.status()}. ` +
          `Expected a success status. The service may be down.`,
      ).toBeLessThan(400);
    } catch (error) {
      const err = error as Error;
      if (err.message.includes('ERR_CONNECTION') ||
          err.message.includes('ERR_NAME_NOT_RESOLVED') ||
          err.message.includes('No response from')) {
        throw new Error(
          `SERVICE DOWN: ELSA Studio at ${ELSA_URL} is unreachable. ` +
            `Error: ${err.message}`,
        );
      }
      throw err;
    }

    // Blazor WASM apps include specific framework files
    // OR the page may be an auth redirect (nginx auth_request -> GitHub OAuth)
    const html = await page.content();
    const url = page.url();
    const hasBlazorFramework =
      html.includes('blazor.webassembly.js') ||
      html.includes('blazor.web.js') ||
      html.includes('_framework') ||
      html.includes('Blazor') ||
      html.includes('blazor');
    const isAuthRedirect =
      url.includes('github.com/login') ||
      url.includes('github.com/sessions') ||
      html.includes('html-auth') ||
      html.includes('Sign in to GitHub');

    expect(
      hasBlazorFramework || isAuthRedirect,
      `ELSA Studio at ${ELSA_URL} returned neither Blazor content nor auth redirect. ` +
        `URL: ${url}. HTML snippet: ${html.substring(0, 500)}...`,
    ).toBeTruthy();
  });

  test('login page or studio UI appears', async ({ page }) => {
    let response;
    try {
      response = await page.goto(ELSA_URL, {
        waitUntil: 'networkidle',
        timeout: 30_000,
      });
    } catch (error) {
      throw new Error(
        `SERVICE DOWN: ELSA Studio at ${ELSA_URL} is unreachable. ` +
          `Error: ${(error as Error).message}`,
      );
    }

    // First verify the page loaded successfully (not a Cloudflare/proxy error)
    const httpStatus = response?.status() ?? 0;
    expect(
      httpStatus,
      `ELSA Studio at ${ELSA_URL} returned HTTP ${httpStatus}. ` +
        `A 5xx status indicates the service is down.`,
    ).toBeLessThan(400);

    const finalUrl = page.url();

    // After Blazor loads, we should see either:
    // 1. A login page (if auth is enabled)
    // 2. The ELSA Studio UI directly
    // 3. A redirect to an external login

    const isRedirectedToLogin =
      finalUrl.includes('/login') ||
      finalUrl.includes('/auth') ||
      finalUrl.includes('/signin') ||
      finalUrl.includes('github.com/login') ||
      finalUrl.includes('github.com/sessions');

    if (isRedirectedToLogin) {
      // Auth redirect is working — this is valid
      return;
    }

    // Check for login elements on the page
    const hasLoginElements = await page
      .locator(
        [
          'input[type="password"]',
          'button:has-text("Sign in")',
          'button:has-text("Log in")',
          'button:has-text("Login")',
          'a:has-text("Sign in")',
          '[class*="login"]',
          '[class*="Login"]',
        ].join(', '),
      )
      .count();

    // Check for ELSA Studio UI elements
    const hasStudioElements = await page
      .locator(
        [
          '[class*="workflow"]',
          '[class*="Workflow"]',
          '[class*="elsa"]',
          '[class*="Elsa"]',
          '[class*="studio"]',
          '[class*="Studio"]',
          '[class*="sidebar"]',
          '[class*="Sidebar"]',
          'nav',
        ].join(', '),
      )
      .count();

    // Check for Blazor loading indicator (Blazor may still be initializing)
    const hasBlazorLoading = await page
      .locator('#blazor-error-ui, .loading, [class*="loading"], [class*="Loading"]')
      .count();

    const hasExpectedContent =
      hasLoginElements > 0 || hasStudioElements > 0 || hasBlazorLoading > 0;

    // If none of the above, the page body should at least have meaningful content
    const bodyText = await page.locator('body').innerText();

    expect(
      hasExpectedContent || bodyText.trim().length > 10,
      `ELSA Studio at ${ELSA_URL} shows neither login page nor studio UI. ` +
        `Login elements: ${hasLoginElements}, Studio elements: ${hasStudioElements}, ` +
        `Blazor loading: ${hasBlazorLoading}, Body text length: ${bodyText.trim().length}. ` +
        `Final URL: ${finalUrl}`,
    ).toBeTruthy();
  });

  test('workflow list page is accessible after potential login', async ({
    page,
  }) => {
    // Try to navigate to a workflow-related path
    const workflowUrl = `${ELSA_URL}/workflows`;

    try {
      const response = await page.goto(workflowUrl, {
        waitUntil: 'domcontentloaded',
        timeout: 25_000,
      });

      if (!response) {
        throw new Error(`No response from ${workflowUrl}`);
      }

      const status = response.status();

      // Should not be a server error
      expect(
        status,
        `ELSA Studio workflow page at ${workflowUrl} returned HTTP ${status}. ` +
          `A 5xx error indicates the ELSA service is down or misconfigured.`,
      ).toBeLessThan(500);

      // If we got redirected to login, that is fine (auth-protected)
      const finalUrl = page.url();
      const wasRedirected =
        finalUrl.includes('/login') ||
        finalUrl.includes('/auth') ||
        finalUrl.includes('/signin');

      if (wasRedirected || status === 401 || status === 403) {
        // Auth is working — workflow page requires login
        return;
      }

      // If we reached the page, verify it has some content
      const html = await page.content();
      expect(
        html.length,
        `ELSA Studio workflow page returned minimal HTML (${html.length} chars). ` +
          `The Blazor app may not be rendering correctly.`,
      ).toBeGreaterThan(100);
    } catch (error) {
      const err = error as Error;
      if (err.message.includes('ERR_CONNECTION') ||
          err.message.includes('ERR_NAME_NOT_RESOLVED') ||
          err.message.includes('No response from')) {
        throw new Error(
          `SERVICE DOWN: ELSA Studio at ${ELSA_URL} is unreachable. ` +
            `Error: ${err.message}`,
        );
      }
      throw err;
    }
  });
});
