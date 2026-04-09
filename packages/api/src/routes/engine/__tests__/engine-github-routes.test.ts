/**
 * Engine GitHub Route Tests
 *
 * Tests GitHub integration endpoints called by Elsa activities:
 *   GET    /api/engine/issues
 *   GET    /api/engine/security-alerts
 *   POST   /api/engine/issue-comment
 *   POST   /api/engine/issue-labels
 *   DELETE /api/engine/issue-labels/:repo/:issueNumber/:label
 *   POST   /api/engine/create-issue
 *   POST   /api/engine/trigger-ci
 *
 * Uses a mock Octokit to avoid real GitHub API calls.
 *
 * Story 6-11: Context API Wiring
 */

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import Fastify from 'fastify';
import type { FastifyInstance } from 'fastify';
import { registerEngineGitHubRoutes } from '../engine-github-routes.js';

/** Create a mock Octokit with all required rest/request methods. */
function createMockOctokit() {
  return {
    rest: {
      issues: {
        listForRepo: vi.fn().mockResolvedValue({
          data: [
            { number: 1, title: 'Bug fix', state: 'open', labels: [] },
            { number: 2, title: 'Feature', state: 'open', labels: [] },
          ],
        }),
        createComment: vi.fn().mockResolvedValue({
          data: { id: 100, html_url: 'https://github.com/owner/repo/issues/1#comment-100' },
        }),
        addLabels: vi.fn().mockResolvedValue({
          data: [{ name: 'bug' }, { name: 'priority:high' }],
        }),
        removeLabel: vi.fn().mockResolvedValue({ data: {} }),
        create: vi.fn().mockResolvedValue({
          data: { number: 42, html_url: 'https://github.com/owner/repo/issues/42', title: 'New Issue' },
        }),
      },
      actions: {
        createWorkflowDispatch: vi.fn().mockResolvedValue({ data: {} }),
      },
    },
    request: vi.fn().mockResolvedValue({ data: [] }),
  };
}

describe('Engine GitHub Routes', () => {
  let app: FastifyInstance;
  let octokit: ReturnType<typeof createMockOctokit>;

  beforeEach(async () => {
    octokit = createMockOctokit();
    app = Fastify({ logger: false });
    await registerEngineGitHubRoutes(app, { octokit: octokit as any });
    await app.ready();
  });

  afterEach(async () => {
    await app.close();
  });

  // -----------------------------------------------------------------------
  // GET /api/engine/issues
  // -----------------------------------------------------------------------

  describe('GET /api/engine/issues', () => {
    it('returns issues for a repository', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/engine/issues?repo=owner/repo',
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.issues).toHaveLength(2);
      expect(octokit.rest.issues.listForRepo).toHaveBeenCalledWith(
        expect.objectContaining({
          owner: 'owner',
          repo: 'repo',
          state: 'open',
        }),
      );
    });

    it('passes labels and state parameters', async () => {
      await app.inject({
        method: 'GET',
        url: '/api/engine/issues?repo=owner/repo&labels=bug,priority:high&state=closed',
      });

      expect(octokit.rest.issues.listForRepo).toHaveBeenCalledWith(
        expect.objectContaining({
          labels: 'bug,priority:high',
          state: 'closed',
        }),
      );
    });

    it('returns 400 when repo is missing', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/engine/issues',
      });

      expect(response.statusCode).toBe(400);
    });

    it('returns 400 for invalid repo format', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/engine/issues?repo=invalid-format',
      });

      expect(response.statusCode).toBe(400);
    });

    it('filters out pull requests', async () => {
      octokit.rest.issues.listForRepo.mockResolvedValueOnce({
        data: [
          { number: 1, title: 'Issue', state: 'open' },
          { number: 2, title: 'PR', state: 'open', pull_request: { url: 'https://...' } },
        ],
      });

      const response = await app.inject({
        method: 'GET',
        url: '/api/engine/issues?repo=owner/repo',
      });

      expect(response.json().issues).toHaveLength(1);
    });
  });

  // -----------------------------------------------------------------------
  // GET /api/engine/security-alerts
  // -----------------------------------------------------------------------

  describe('GET /api/engine/security-alerts', () => {
    it('fetches both dependabot and code scanning alerts', async () => {
      octokit.request
        .mockResolvedValueOnce({ data: [{ id: 1, severity: 'high' }] })
        .mockResolvedValueOnce({ data: [{ id: 2, rule: { severity: 'error' } }] });

      const response = await app.inject({
        method: 'GET',
        url: '/api/engine/security-alerts?repo=owner/repo',
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.dependabot).toHaveLength(1);
      expect(body.codeScanning).toHaveLength(1);
    });

    it('fetches only dependabot alerts when type=dependabot', async () => {
      octokit.request.mockResolvedValueOnce({ data: [{ id: 1 }] });

      const response = await app.inject({
        method: 'GET',
        url: '/api/engine/security-alerts?repo=owner/repo&type=dependabot',
      });

      expect(response.statusCode).toBe(200);
      expect(response.json().dependabot).toHaveLength(1);
      expect(response.json().codeScanning).toHaveLength(0);
    });

    it('returns 400 when repo is missing', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/engine/security-alerts',
      });

      expect(response.statusCode).toBe(400);
    });

    it('handles dependabot API errors gracefully', async () => {
      octokit.request
        .mockRejectedValueOnce(new Error('Dependabot not enabled'))
        .mockResolvedValueOnce({ data: [] });

      const response = await app.inject({
        method: 'GET',
        url: '/api/engine/security-alerts?repo=owner/repo',
      });

      // Should not fail — dependabot errors are logged and skipped
      expect(response.statusCode).toBe(200);
      expect(response.json().dependabot).toHaveLength(0);
    });
  });

  // -----------------------------------------------------------------------
  // POST /api/engine/issue-comment
  // -----------------------------------------------------------------------

  describe('POST /api/engine/issue-comment', () => {
    it('creates a comment on an issue', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/issue-comment',
        payload: {
          repository: 'owner/repo',
          issueNumber: 1,
          body: 'Automated analysis complete.',
        },
      });

      expect(response.statusCode).toBe(200);
      expect(response.json().id).toBe(100);
      expect(octokit.rest.issues.createComment).toHaveBeenCalledWith({
        owner: 'owner',
        repo: 'repo',
        issue_number: 1,
        body: 'Automated analysis complete.',
      });
    });

    it('rejects missing body (400)', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/issue-comment',
        payload: {
          repository: 'owner/repo',
          issueNumber: 1,
        },
      });

      expect(response.statusCode).toBe(400);
    });

    it('rejects invalid repository format (400)', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/issue-comment',
        payload: {
          repository: 'badrepo',
          issueNumber: 1,
          body: 'test',
        },
      });

      expect(response.statusCode).toBe(400);
    });
  });

  // -----------------------------------------------------------------------
  // POST /api/engine/issue-labels
  // -----------------------------------------------------------------------

  describe('POST /api/engine/issue-labels', () => {
    it('adds labels to an issue', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/issue-labels',
        payload: {
          repository: 'owner/repo',
          issueNumber: 1,
          labels: ['bug', 'priority:high'],
        },
      });

      expect(response.statusCode).toBe(200);
      expect(response.json().labels).toEqual(['bug', 'priority:high']);
    });

    it('rejects empty labels array (400)', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/issue-labels',
        payload: {
          repository: 'owner/repo',
          issueNumber: 1,
          labels: [],
        },
      });

      expect(response.statusCode).toBe(400);
    });
  });

  // -----------------------------------------------------------------------
  // DELETE /api/engine/issue-labels/:repo/:issueNumber/:label
  // -----------------------------------------------------------------------

  describe('DELETE /api/engine/issue-labels/:repo/:issueNumber/:label', () => {
    it('removes a label from an issue', async () => {
      const response = await app.inject({
        method: 'DELETE',
        url: '/api/engine/issue-labels/owner%2Frepo/1/bug',
      });

      expect(response.statusCode).toBe(200);
      expect(response.json().removed).toBe(true);
      expect(response.json().label).toBe('bug');
    });

    it('returns 400 for invalid issueNumber', async () => {
      const response = await app.inject({
        method: 'DELETE',
        url: '/api/engine/issue-labels/owner%2Frepo/abc/bug',
      });

      expect(response.statusCode).toBe(400);
    });
  });

  // -----------------------------------------------------------------------
  // POST /api/engine/create-issue
  // -----------------------------------------------------------------------

  describe('POST /api/engine/create-issue', () => {
    it('creates a new issue', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/create-issue',
        payload: {
          repository: 'owner/repo',
          title: 'Auto-created issue',
          body: 'This issue was created by an Elsa workflow.',
          labels: ['auto-triage'],
        },
      });

      expect(response.statusCode).toBe(201);
      const body = response.json();
      expect(body.number).toBe(42);
      expect(body.title).toBe('New Issue');
    });

    it('creates an issue with minimal fields', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/create-issue',
        payload: {
          repository: 'owner/repo',
          title: 'Minimal issue',
        },
      });

      expect(response.statusCode).toBe(201);
    });

    it('rejects missing title (400)', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/create-issue',
        payload: {
          repository: 'owner/repo',
        },
      });

      expect(response.statusCode).toBe(400);
    });
  });

  // -----------------------------------------------------------------------
  // POST /api/engine/trigger-ci
  // -----------------------------------------------------------------------

  describe('POST /api/engine/trigger-ci', () => {
    it('dispatches a GitHub Actions workflow', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/trigger-ci',
        payload: {
          repository: 'owner/repo',
          branchName: 'feature/test',
          workflowFile: 'ci.yml',
        },
      });

      expect(response.statusCode).toBe(200);
      expect(response.json().dispatched).toBe(true);
      expect(octokit.rest.actions.createWorkflowDispatch).toHaveBeenCalledWith(
        expect.objectContaining({
          owner: 'owner',
          repo: 'repo',
          workflow_id: 'ci.yml',
          ref: 'feature/test',
        }),
      );
    });

    it('passes inputs when provided', async () => {
      await app.inject({
        method: 'POST',
        url: '/api/engine/trigger-ci',
        payload: {
          repository: 'owner/repo',
          branchName: 'main',
          workflowFile: 'deploy.yml',
          inputs: { environment: 'staging' },
        },
      });

      expect(octokit.rest.actions.createWorkflowDispatch).toHaveBeenCalledWith(
        expect.objectContaining({
          inputs: { environment: 'staging' },
        }),
      );
    });

    it('rejects missing workflowFile (400)', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/trigger-ci',
        payload: {
          repository: 'owner/repo',
          branchName: 'main',
        },
      });

      expect(response.statusCode).toBe(400);
    });
  });

  // -----------------------------------------------------------------------
  // Service unavailable when no Octokit
  // -----------------------------------------------------------------------

  describe('without Octokit', () => {
    let noOctokitApp: FastifyInstance;

    beforeEach(async () => {
      noOctokitApp = Fastify({ logger: false });
      await registerEngineGitHubRoutes(noOctokitApp, {});
      await noOctokitApp.ready();
    });

    afterEach(async () => {
      await noOctokitApp.close();
    });

    it('GET /api/engine/issues returns 503', async () => {
      const response = await noOctokitApp.inject({
        method: 'GET',
        url: '/api/engine/issues?repo=owner/repo',
      });

      expect(response.statusCode).toBe(503);
    });

    it('POST /api/engine/issue-comment returns 503', async () => {
      const response = await noOctokitApp.inject({
        method: 'POST',
        url: '/api/engine/issue-comment',
        payload: { repository: 'owner/repo', issueNumber: 1, body: 'test' },
      });

      expect(response.statusCode).toBe(503);
    });
  });
});
