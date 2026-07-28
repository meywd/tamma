/**
 * Infrastructure configuration validation tests (Story 16-1).
 *
 * These tests parse Docker and nginx configuration files and verify
 * structural invariants required by the OAuth2 proxy architecture:
 *
 *  - nginx template has auth_request on protected routes
 *  - nginx template does NOT have auth_request on /api/ routes
 *  - nginx template has sign-out location on all oauth2-enabled server blocks
 *  - nginx template has ELSA API key injection
 *  - oauth2-proxy.cfg has correct settings
 *  - docker-compose.yml has oauth2-proxy service and template mount
 *  - deploy workflows include OAUTH2_PROXY_COOKIE_SECRET and ELSA_ADMIN_API_KEY
 */

import { readFile } from 'fs/promises';
import { resolve, dirname } from 'path';
import { fileURLToPath } from 'url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);

// Resolve paths relative to project root (4 levels up from __tests__)
const PROJECT_ROOT = resolve(__dirname, '..', '..', '..', '..');
const DOCKER_DIR = resolve(PROJECT_ROOT, 'docker');
const WORKFLOWS_DIR = resolve(PROJECT_ROOT, '.github', 'workflows');

/**
 * Read a file from the project and return its contents.
 * Throws a clear error if the file is missing.
 */
async function readProjectFile(relativePath: string): Promise<string> {
  const fullPath = resolve(PROJECT_ROOT, relativePath);
  return readFile(fullPath, 'utf-8');
}

/**
 * Extract a brace-delimited block starting at the `{` at position `start`.
 * Returns the substring from `start` to the matching `}` (inclusive).
 */
function extractBraceBlock(text: string, start: number): string {
  let depth = 0;
  for (let i = start; i < text.length; i++) {
    if (text[i] === '{') depth++;
    else if (text[i] === '}') {
      depth--;
      if (depth === 0) return text.slice(start, i + 1);
    }
  }
  return text.slice(start);
}

/**
 * Extract server blocks from an nginx config by server_name.
 * Returns the raw text of each `server { ... }` block keyed by server_name.
 */
function extractServerBlocks(config: string): Map<string, string> {
  const blocks = new Map<string, string>();
  const re = /server\s*\{/g;
  let match: RegExpExecArray | null;
  while ((match = re.exec(config)) !== null) {
    const braceStart = config.indexOf('{', match.index);
    const block = 'server ' + extractBraceBlock(config, braceStart);
    const nameMatch = /server_name\s+([^;]+);/.exec(block);
    if (nameMatch) {
      const name = nameMatch[1].trim();
      blocks.set(name, block);
    }
    // Advance past this block to avoid re-matching nested content
    re.lastIndex = match.index + block.length;
  }
  return blocks;
}

/**
 * Extract location blocks from a server block.
 * Returns an array of { path, body } objects.
 */
function extractLocationBlocks(serverBlock: string): Array<{ path: string; body: string }> {
  const locations: Array<{ path: string; body: string }> = [];
  const re = /location\s+([^{]+?)\s*\{/g;
  let match: RegExpExecArray | null;
  while ((match = re.exec(serverBlock)) !== null) {
    const path = match[1].trim();
    const braceStart = serverBlock.indexOf('{', match.index + match[0].length - 1);
    const fullBlock = extractBraceBlock(serverBlock, braceStart);
    // body is the content between the outer braces
    const body = fullBlock.slice(1, -1);
    locations.push({ path, body });
    re.lastIndex = braceStart + fullBlock.length;
  }
  return locations;
}

// ---------------------------------------------------------------------------
// nginx-proxy.conf.template
// ---------------------------------------------------------------------------
describe('nginx-proxy.conf.template', () => {
  let nginxConfig: string;
  let serverBlocks: Map<string, string>;

  beforeAll(async () => {
    nginxConfig = await readProjectFile('docker/nginx-proxy.conf.template');
    serverBlocks = extractServerBlocks(nginxConfig);
  });

  it('is a template file (not a plain .conf)', async () => {
    // Verify the .conf file does NOT exist (it was renamed to .template)
    await expect(
      readFile(resolve(DOCKER_DIR, 'nginx-proxy.conf'), 'utf-8'),
    ).rejects.toThrow();
  });

  it('contains the $ELSA_ADMIN_API_KEY envsubst variable', () => {
    expect(nginxConfig).toContain('$ELSA_ADMIN_API_KEY');
  });

  // Hostname re-layout (2026-07-28): the ADMIN console (oauth2-proxy'd)
  // moved from app.tamma.dev to admin.tamma.dev; app.tamma.dev now serves
  // the customer app alongside dash.tamma.dev (one block, two names).
  describe('admin.tamma.dev server block (admin console)', () => {
    let adminBlock: string;

    beforeAll(() => {
      adminBlock = serverBlocks.get('admin.tamma.dev') ?? '';
      expect(adminBlock).not.toBe('');
    });

    it('has auth_request on the root / location (dashboard)', () => {
      const locations = extractLocationBlocks(adminBlock);
      const rootLoc = locations.find((l) => l.path === '/');
      expect(rootLoc).toBeDefined();
      expect(rootLoc?.body).toContain('auth_request /oauth2/auth');
    });

    it('does NOT have auth_request on /api/ routes', () => {
      const locations = extractLocationBlocks(adminBlock);
      const apiLoc = locations.find((l) => l.path === '/api/');
      expect(apiLoc).toBeDefined();
      expect(apiLoc?.body).not.toContain('auth_request');
    });

    it('has the /sign-out location that clears tamma_session', () => {
      const locations = extractLocationBlocks(adminBlock);
      const signOutLoc = locations.find((l) => l.path === '= /sign-out');
      expect(signOutLoc).toBeDefined();
      expect(signOutLoc?.body).toContain('tamma_session=');
      expect(signOutLoc?.body).toContain('Max-Age=0');
      expect(signOutLoc?.body).toContain('/oauth2/sign_out');
    });
  });

  describe('customer server block (dash.tamma.dev + app.tamma.dev)', () => {
    let customerBlock: string;

    beforeAll(() => {
      // One server block, two names — keyed by the full server_name value.
      customerBlock = serverBlocks.get('dash.tamma.dev app.tamma.dev') ?? '';
      expect(customerBlock).not.toBe('');
    });

    it('does NOT have auth_request anywhere (customer app ships its own login)', () => {
      // Epic 45 D1 / hostname re-layout: signup/verify/reset pages must be
      // reachable anonymously on BOTH hosts — no GitHub OAuth wall.
      expect(customerBlock).not.toContain('auth_request');
    });

    it('serves app.tamma.dev from the customer block, not the admin one', () => {
      // app.tamma.dev must appear ONLY here (plus the port-80 redirect),
      // never as the admin console's server_name.
      const adminBlock = serverBlocks.get('admin.tamma.dev') ?? '';
      expect(adminBlock).not.toContain('app.tamma.dev');
    });
  });

  describe('elsa.tamma.dev server block', () => {
    let elsaBlock: string;

    beforeAll(() => {
      elsaBlock = serverBlocks.get('elsa.tamma.dev') ?? '';
      expect(elsaBlock).not.toBe('');
    });

    it('has auth_request on /elsa/api/ location (role-check for RBAC)', () => {
      const locations = extractLocationBlocks(elsaBlock);
      const elsaApiLoc = locations.find((l) => l.path === '/elsa/api/');
      expect(elsaApiLoc).toBeDefined();
      expect(elsaApiLoc?.body).toContain('auth_request /auth/role-check');
    });

    it('has auth_request on the root / location (role-check for RBAC)', () => {
      const locations = extractLocationBlocks(elsaBlock);
      const rootLoc = locations.find((l) => l.path === '/');
      expect(rootLoc).toBeDefined();
      expect(rootLoc?.body).toContain('auth_request /auth/role-check');
    });

    it('injects ELSA admin API key in /elsa/api/ location', () => {
      const locations = extractLocationBlocks(elsaBlock);
      const elsaApiLoc = locations.find((l) => l.path === '/elsa/api/');
      expect(elsaApiLoc).toBeDefined();
      expect(elsaApiLoc?.body).toContain('proxy_set_header Authorization');
      expect(elsaApiLoc?.body).toContain('ApiKey $ELSA_ADMIN_API_KEY');
    });

    it('has the /sign-out location that clears tamma_session', () => {
      const locations = extractLocationBlocks(elsaBlock);
      const signOutLoc = locations.find((l) => l.path === '= /sign-out');
      expect(signOutLoc).toBeDefined();
      expect(signOutLoc?.body).toContain('tamma_session=');
      expect(signOutLoc?.body).toContain('Max-Age=0');
      expect(signOutLoc?.body).toContain('/oauth2/sign_out');
    });
  });

  describe('logs.tamma.dev server block', () => {
    let logsBlock: string;

    beforeAll(() => {
      logsBlock = serverBlocks.get('logs.tamma.dev') ?? '';
      expect(logsBlock).not.toBe('');
    });

    it('has auth_request on the root / location (role-check for RBAC)', () => {
      const locations = extractLocationBlocks(logsBlock);
      const rootLoc = locations.find((l) => l.path === '/');
      expect(rootLoc).toBeDefined();
      expect(rootLoc?.body).toContain('auth_request /auth/role-check');
    });

    it('has the /sign-out location that clears tamma_session', () => {
      const locations = extractLocationBlocks(logsBlock);
      const signOutLoc = locations.find((l) => l.path === '= /sign-out');
      expect(signOutLoc).toBeDefined();
      expect(signOutLoc?.body).toContain('tamma_session=');
      expect(signOutLoc?.body).toContain('Max-Age=0');
      expect(signOutLoc?.body).toContain('/oauth2/sign_out');
    });
  });

  describe('api.tamma.dev server block', () => {
    let apiBlock: string;

    beforeAll(() => {
      apiBlock = serverBlocks.get('api.tamma.dev') ?? '';
      expect(apiBlock).not.toBe('');
    });

    it('does NOT have auth_request (API-only, no oauth2-proxy)', () => {
      expect(apiBlock).not.toContain('auth_request');
    });

    it('does NOT have a /sign-out location (no oauth2 on this subdomain)', () => {
      const locations = extractLocationBlocks(apiBlock);
      const signOutLoc = locations.find((l) => l.path === '= /sign-out');
      expect(signOutLoc).toBeUndefined();
    });
  });
});

// ---------------------------------------------------------------------------
// oauth2-proxy.cfg
// ---------------------------------------------------------------------------
describe('oauth2-proxy.cfg', () => {
  let cfg: string;

  beforeAll(async () => {
    cfg = await readProjectFile('docker/oauth2-proxy.cfg');
  });

  it('uses the GitHub provider', () => {
    expect(cfg).toMatch(/provider\s*=\s*"github"/);
  });

  it('has reverse_proxy mode enabled', () => {
    expect(cfg).toMatch(/reverse_proxy\s*=\s*true/);
  });

  it('sets cookie domain to .tamma.dev', () => {
    expect(cfg).toContain('.tamma.dev');
    expect(cfg).toMatch(/cookie_domains\s*=\s*\["\.tamma\.dev"\]/);
  });

  it('has the static 202 upstream (auth_request mode)', () => {
    expect(cfg).toContain('static://202');
  });

  it('sets xauthrequest headers', () => {
    expect(cfg).toMatch(/set_xauthrequest\s*=\s*true/);
  });

  it('skips auth for health and webhook routes', () => {
    expect(cfg).toContain('skip_auth_routes');
    expect(cfg).toContain('/health');
    expect(cfg).toContain('/api/github/webhooks');
  });

  it('has secure cookie settings', () => {
    expect(cfg).toMatch(/cookie_secure\s*=\s*true/);
    expect(cfg).toMatch(/cookie_httponly\s*=\s*true/);
    expect(cfg).toMatch(/cookie_samesite\s*=\s*"lax"/);
  });
});

// ---------------------------------------------------------------------------
// docker-compose.yml
// ---------------------------------------------------------------------------
describe('docker-compose.yml', () => {
  let compose: string;

  beforeAll(async () => {
    compose = await readProjectFile('docker/docker-compose.yml');
  });

  it('defines the oauth2-proxy service', () => {
    expect(compose).toContain('oauth2-proxy:');
    expect(compose).toContain('quay.io/oauth2-proxy/oauth2-proxy');
  });

  it('mounts nginx template to /etc/nginx/templates/', () => {
    expect(compose).toContain('nginx-proxy.conf.template:/etc/nginx/templates/default.conf.template');
  });

  it('does NOT mount a plain .conf to /etc/nginx/conf.d/', () => {
    // Ensure the old mount style is gone
    expect(compose).not.toContain('nginx-proxy.conf:/etc/nginx/conf.d/default.conf');
  });

  it('passes ELSA_ADMIN_API_KEY to nginx-proxy environment', () => {
    // Find the nginx-proxy service section and check for the env var
    expect(compose).toContain('ELSA_ADMIN_API_KEY');
  });

  it('passes OAUTH2_PROXY_COOKIE_SECRET to oauth2-proxy', () => {
    expect(compose).toContain('OAUTH2_PROXY_COOKIE_SECRET');
  });
});

// ---------------------------------------------------------------------------
// .env.example
// ---------------------------------------------------------------------------
describe('.env.example', () => {
  let envExample: string;

  beforeAll(async () => {
    envExample = await readProjectFile('docker/.env.example');
  });

  it('documents OAUTH2_PROXY_COOKIE_SECRET', () => {
    expect(envExample).toContain('OAUTH2_PROXY_COOKIE_SECRET');
  });

  it('documents ELSA_ADMIN_API_KEY', () => {
    expect(envExample).toContain('ELSA_ADMIN_API_KEY');
  });
});

// ---------------------------------------------------------------------------
// Deploy workflows
// ---------------------------------------------------------------------------
describe('deploy workflows', () => {
  let deployYml: string;
  let dockerPublishYml: string;

  beforeAll(async () => {
    deployYml = await readProjectFile('.github/workflows/deploy.yml');
    dockerPublishYml = await readProjectFile('.github/workflows/docker-publish.yml');
  });

  describe('deploy.yml', () => {
    it('writes OAUTH2_PROXY_COOKIE_SECRET to .env', () => {
      expect(deployYml).toContain('OAUTH2_PROXY_COOKIE_SECRET');
    });

    it('writes ELSA_ADMIN_API_KEY to .env', () => {
      expect(deployYml).toContain('ELSA_ADMIN_API_KEY');
    });
  });

  describe('docker-publish.yml', () => {
    it('writes OAUTH2_PROXY_COOKIE_SECRET to .env', () => {
      expect(dockerPublishYml).toContain('OAUTH2_PROXY_COOKIE_SECRET');
    });

    it('writes ELSA_ADMIN_API_KEY to .env', () => {
      expect(dockerPublishYml).toContain('ELSA_ADMIN_API_KEY');
    });
  });
});
