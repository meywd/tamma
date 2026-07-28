// @vitest-environment jsdom
/**
 * Router pins. The page-level tests (e.g. ProvidersAdminPage.test.tsx)
 * exercise components in isolation and ASSUME the route wraps them in
 * AdminGuard — this file pins that the router actually does, so an RBAC
 * regression in router.tsx cannot pass silently.
 */
import type { ReactElement } from 'react';
import type { RouteObject } from 'react-router-dom';
import { router } from '../router.js';
import { AdminGuard } from '../guards/AdminGuard.js';

function flatten(routes: RouteObject[]): RouteObject[] {
  return routes.flatMap((r) => [r, ...(r.children != null ? flatten(r.children) : [])]);
}

describe('router — /admin/providers (Story 46-2)', () => {
  it('registers /admin/providers wrapped in AdminGuard', () => {
    const route = flatten(router.routes as RouteObject[]).find(
      (r) => r.path === '/admin/providers',
    );
    expect(route).toBeDefined();
    // The route element's OUTERMOST wrapper is the guard — platform-owner
    // RBAC applies before anything of the page (even its Suspense) renders.
    const element = route?.element as ReactElement;
    expect(element.type).toBe(AdminGuard);
  });
});
