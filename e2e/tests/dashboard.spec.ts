/**
 * Dashboard UI tests — verify the React SPA at app.tamma.dev.
 *
 * Tests that the dashboard loads, renders navigation, and handles
 * authentication correctly. Designed to fail clearly when the
 * dashboard service is down.
 */

import { test, expect } from '@playwright/test';
import { APP_URL } from '../playwright.config';

test.describe('Dashboard UI Tests', () => {
  test('page loads without JavaScript errors', async ({ page }) => {
    const jsErrors: string[] = [];

    page.on('pageerror', (error) => {
      jsErrors.push(`${error.name}: ${error.message}`);
    });

    try {
      const response = await page.goto(APP_URL, {
        waitUntil: 'networkidle',
        timeout: 20_000,
      });

      if (!response) {
        throw new Error(`No response from dashboard at ${APP_URL}`);
      }

      expect(
        response.status(),
        `Dashboard at ${APP_URL} returned HTTP ${response.status()}. Service may be down.`,
      ).toBeLessThan(400);
    } catch (error) {
      const err = error as Error;
      if (err.message.includes('ERR_CONNECTION') ||
          err.message.includes('ERR_NAME_NOT_RESOLVED') ||
          err.message.includes('No response from')) {
        throw new Error(
          `SERVICE DOWN: Dashboard at ${APP_URL} is unreachable. Error: ${err.message}`,
        );
      }
      throw err;
    }

    // Allow a brief moment for React to hydrate and any async errors to surface
    await page.waitForTimeout(2_000);

    // Filter out known benign errors (e.g., analytics, third-party scripts)
    const criticalErrors = jsErrors.filter(
      (e) =>
        !e.includes('ResizeObserver') &&
        !e.includes('Loading chunk') &&
        !e.includes('Failed to fetch dynamically imported module'),
    );

    expect(
      criticalErrors,
      `Dashboard at ${APP_URL} had JavaScript errors:\n${criticalErrors.join('\n')}`,
    ).toHaveLength(0);
  });

  test('navigation renders (sidebar or header links)', async ({ page }) => {
    let response;
    try {
      response = await page.goto(APP_URL, {
        waitUntil: 'networkidle',
        timeout: 20_000,
      });
    } catch (error) {
      throw new Error(
        `SERVICE DOWN: Dashboard at ${APP_URL} is unreachable. ` +
          `Error: ${(error as Error).message}`,
      );
    }

    // First verify the page loaded successfully (not a Cloudflare/proxy error)
    const httpStatus = response?.status() ?? 0;
    expect(
      httpStatus,
      `Dashboard at ${APP_URL} returned HTTP ${httpStatus}. ` +
        `A 5xx status indicates the service is down.`,
    ).toBeLessThan(400);

    // The dashboard should have some kind of navigation —
    // check for common navigation patterns: nav element, sidebar, header links
    const hasNav = await page.locator('nav').count();
    const hasSidebar = await page.locator('[class*="sidebar"], [class*="Sidebar"], aside, [role="navigation"]').count();
    const hasHeaderLinks = await page.locator('header a, [class*="header"] a, [class*="Header"] a').count();
    const hasMenuItems = await page.locator('[role="menuitem"], [class*="menu"] a, [class*="Menu"] a').count();

    // At least one navigation pattern should be present, OR we may be
    // on a login page (which is also valid)
    const hasAnyNavigation = hasNav > 0 || hasSidebar > 0 || hasHeaderLinks > 0 || hasMenuItems > 0;
    const hasLoginForm = await page.locator(
      'form, [class*="login"], [class*="Login"], button:has-text("Sign in"), button:has-text("Log in"), a:has-text("Sign in"), a:has-text("Log in")',
    ).count();

    expect(
      hasAnyNavigation || hasLoginForm > 0,
      `Dashboard at ${APP_URL} has no navigation elements and no login form. ` +
        `The page may be blank or broken. ` +
        `Found: nav=${hasNav}, sidebar=${hasSidebar}, headerLinks=${hasHeaderLinks}, ` +
        `menuItems=${hasMenuItems}, loginForm=${hasLoginForm}. ` +
        `Page title: "${await page.title()}"`,
    ).toBeTruthy();
  });

  test('login redirect works (unauthenticated users)', async ({ page }) => {
    // Visit the dashboard root — if auth is enabled, expect either:
    // 1. A redirect to a login page
    // 2. A login form/button on the page
    // 3. A redirect to GitHub OAuth
    // If no auth, the page should load normally

    try {
      const response = await page.goto(APP_URL, {
        waitUntil: 'domcontentloaded',
        timeout: 20_000,
      });

      if (!response) {
        throw new Error(`No response from dashboard at ${APP_URL}`);
      }

      const status = response.status();
      const finalUrl = page.url();

      // If we got redirected to a login page or OAuth, that is correct behavior
      const isOnLoginPage =
        finalUrl.includes('/login') ||
        finalUrl.includes('/auth') ||
        finalUrl.includes('github.com/login') ||
        finalUrl.includes('github.com/sessions') ||
        finalUrl.includes('/signin') ||
        finalUrl.includes('/sign-in');

      if (isOnLoginPage) {
        // Auth redirect is working correctly
        return;
      }

      // If we stayed on the dashboard, check it loaded correctly
      expect(
        status,
        `Dashboard at ${APP_URL} returned HTTP ${status}. ` +
          `Expected either a working page or auth redirect.`,
      ).toBeLessThan(400);

      // Check if there is a sign-in prompt on the page itself
      const pageContent = await page.content();
      const hasAuthPrompt =
        pageContent.includes('Sign in') ||
        pageContent.includes('Log in') ||
        pageContent.includes('Login') ||
        pageContent.includes('Authorize');

      // Either the page loads with content, or has an auth prompt — both are valid
      const bodyText = await page.locator('body').innerText();
      expect(
        bodyText.length > 0 || hasAuthPrompt,
        `Dashboard at ${APP_URL} rendered an empty page with no auth prompt. ` +
          `The application may be broken. Final URL: ${finalUrl}`,
      ).toBeTruthy();
    } catch (error) {
      const err = error as Error;
      if (err.message.includes('ERR_CONNECTION') ||
          err.message.includes('ERR_NAME_NOT_RESOLVED')) {
        throw new Error(
          `SERVICE DOWN: Dashboard at ${APP_URL} is unreachable. Error: ${err.message}`,
        );
      }
      throw err;
    }
  });
});
