/**
 * Engine GitHub Routes
 *
 * Endpoints called by C# Elsa workflow activities for GitHub operations:
 * issue listing, security alerts, comments, labels, issue creation,
 * and CI trigger.
 *
 * Routes:
 *   GET    /api/engine/issues                              — list issues
 *   GET    /api/engine/security-alerts                     — dependabot/codeql alerts
 *   POST   /api/engine/issue-comment                       — add issue comment
 *   POST   /api/engine/issue-labels                        — add labels to issue
 *   DELETE /api/engine/issue-labels/:repo/:issueNumber/:label — remove label
 *   POST   /api/engine/create-issue                        — create a new issue
 *   POST   /api/engine/trigger-ci                          — dispatch GitHub Actions workflow
 *
 * GitHub access: Uses an Octokit instance injected via options.
 * When no Octokit is provided the routes return 503 (service unavailable).
 *
 * Story 6-11: Context API Wiring
 */

import { z } from 'zod';
import type { FastifyInstance, FastifyRequest, FastifyReply } from 'fastify';
import type { Octokit } from '@octokit/rest';

// ---------------------------------------------------------------------------
// Zod Schemas
// ---------------------------------------------------------------------------

const IssueCommentBodySchema = z.object({
  repository: z.string().min(1),
  issueNumber: z.number().int().positive(),
  body: z.string().min(1),
});

const IssueLabelsBodySchema = z.object({
  repository: z.string().min(1),
  issueNumber: z.number().int().positive(),
  labels: z.array(z.string().min(1)).min(1),
});

const CreateIssueBodySchema = z.object({
  repository: z.string().min(1),
  title: z.string().min(1),
  body: z.string().optional(),
  labels: z.array(z.string()).optional(),
  assignees: z.array(z.string()).optional(),
});

const TriggerCIBodySchema = z.object({
  repository: z.string().min(1),
  branchName: z.string().min(1),
  workflowFile: z.string().min(1),
  inputs: z.record(z.string(), z.string()).optional(),
});

// ---------------------------------------------------------------------------
// Options
// ---------------------------------------------------------------------------

export interface EngineGitHubRouteOptions {
  /**
   * Octokit instance for making GitHub API calls.
   * When undefined the routes return 503 Service Unavailable.
   */
  octokit?: Octokit;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/** Split "owner/repo" into { owner, repo }. */
function parseRepo(repository: string): { owner: string; repo: string } {
  const parts = repository.split('/');
  if (parts.length !== 2 || parts[0] === '' || parts[1] === '') {
    throw new Error(`Invalid repository format: "${repository}". Expected "owner/repo".`);
  }
  return { owner: parts[0]!, repo: parts[1]! };
}

// ---------------------------------------------------------------------------
// Plugin
// ---------------------------------------------------------------------------

export async function registerEngineGitHubRoutes(
  fastify: FastifyInstance,
  opts: EngineGitHubRouteOptions = {},
): Promise<void> {
  const { octokit } = opts;

  /** Pre-handler that rejects requests when no Octokit is available. */
  function requireOctokit(_request: FastifyRequest, reply: FastifyReply): Octokit {
    if (octokit === undefined) {
      reply.status(503).send({ error: 'GitHub integration not configured' });
      // The cast is safe — the reply is already sent and the handler will
      // short-circuit after checking the return value.
      return undefined as unknown as Octokit;
    }
    return octokit;
  }

  // ---------- GET /api/engine/issues ----------
  fastify.get(
    '/api/engine/issues',
    async (
      request: FastifyRequest<{
        Querystring: {
          repo?: string;
          labels?: string;
          state?: string;
          per_page?: string;
          page?: string;
        };
      }>,
      reply: FastifyReply,
    ) => {
      const client = requireOctokit(request, reply);
      if (client === undefined) return;

      const repoParam = request.query.repo;
      if (repoParam === undefined || repoParam === '') {
        return reply.status(400).send({ error: 'Missing required query parameter: repo' });
      }

      let parsed: { owner: string; repo: string };
      try {
        parsed = parseRepo(repoParam);
      } catch (err: unknown) {
        return reply.status(400).send({ error: (err as Error).message });
      }

      const state = (request.query.state ?? 'open') as 'open' | 'closed' | 'all';
      const labels = request.query.labels ?? undefined;
      const perPage = parseInt(request.query.per_page ?? '30', 10);
      const page = parseInt(request.query.page ?? '1', 10);

      try {
        const { data } = await client.rest.issues.listForRepo({
          owner: parsed.owner,
          repo: parsed.repo,
          state,
          labels,
          per_page: perPage,
          page,
        });

        // Filter out pull requests (GitHub includes them in the issues endpoint)
        const issues = data.filter(
          (item) => !('pull_request' in item && item.pull_request !== undefined),
        );

        fastify.log.info(
          { repository: repoParam, count: issues.length, state },
          'Listed issues',
        );

        return reply.send({ issues, total: issues.length });
      } catch (err: unknown) {
        fastify.log.error({ err, repository: repoParam }, 'Failed to list issues');
        return reply.status(502).send({ error: 'GitHub API error', details: (err as Error).message });
      }
    },
  );

  // ---------- GET /api/engine/security-alerts ----------
  fastify.get(
    '/api/engine/security-alerts',
    async (
      request: FastifyRequest<{
        Querystring: { repo?: string; type?: string };
      }>,
      reply: FastifyReply,
    ) => {
      const client = requireOctokit(request, reply);
      if (client === undefined) return;

      const repoParam = request.query.repo;
      if (repoParam === undefined || repoParam === '') {
        return reply.status(400).send({ error: 'Missing required query parameter: repo' });
      }

      let parsed: { owner: string; repo: string };
      try {
        parsed = parseRepo(repoParam);
      } catch (err: unknown) {
        return reply.status(400).send({ error: (err as Error).message });
      }

      const alertType = request.query.type ?? 'all';
      const alerts: { dependabot: unknown[]; codeScanning: unknown[] } = {
        dependabot: [],
        codeScanning: [],
      };

      try {
        if (alertType === 'dependabot' || alertType === 'all') {
          try {
            const { data } = await client.request(
              'GET /repos/{owner}/{repo}/dependabot/alerts',
              {
                owner: parsed.owner,
                repo: parsed.repo,
                state: 'open',
                per_page: 100,
              },
            );
            alerts.dependabot = data as unknown[];
          } catch (err: unknown) {
            // Dependabot alerts may not be enabled — log and continue
            fastify.log.warn(
              { err, repository: repoParam },
              'Failed to fetch dependabot alerts (may not be enabled)',
            );
          }
        }

        if (alertType === 'codeql' || alertType === 'code_scanning' || alertType === 'all') {
          try {
            const { data } = await client.request(
              'GET /repos/{owner}/{repo}/code-scanning/alerts',
              {
                owner: parsed.owner,
                repo: parsed.repo,
                state: 'open',
                per_page: 100,
              },
            );
            alerts.codeScanning = data as unknown[];
          } catch (err: unknown) {
            // Code scanning may not be enabled — log and continue
            fastify.log.warn(
              { err, repository: repoParam },
              'Failed to fetch code scanning alerts (may not be enabled)',
            );
          }
        }

        fastify.log.info(
          {
            repository: repoParam,
            dependabotCount: alerts.dependabot.length,
            codeScanningCount: alerts.codeScanning.length,
          },
          'Fetched security alerts',
        );

        return reply.send(alerts);
      } catch (err: unknown) {
        fastify.log.error({ err, repository: repoParam }, 'Failed to fetch security alerts');
        return reply.status(502).send({ error: 'GitHub API error', details: (err as Error).message });
      }
    },
  );

  // ---------- POST /api/engine/issue-comment ----------
  fastify.post(
    '/api/engine/issue-comment',
    async (
      request: FastifyRequest<{
        Body: { repository: string; issueNumber: number; body: string };
      }>,
      reply: FastifyReply,
    ) => {
      const client = requireOctokit(request, reply);
      if (client === undefined) return;

      const validated = IssueCommentBodySchema.safeParse(request.body);
      if (!validated.success) {
        return reply.status(400).send({ error: validated.error.message });
      }

      const { repository, issueNumber, body } = validated.data;

      let parsed: { owner: string; repo: string };
      try {
        parsed = parseRepo(repository);
      } catch (err: unknown) {
        return reply.status(400).send({ error: (err as Error).message });
      }

      try {
        const { data } = await client.rest.issues.createComment({
          owner: parsed.owner,
          repo: parsed.repo,
          issue_number: issueNumber,
          body,
        });

        fastify.log.info(
          { repository, issueNumber, commentId: data.id },
          'Issue comment created',
        );

        return reply.send({ id: data.id, htmlUrl: data.html_url });
      } catch (err: unknown) {
        fastify.log.error({ err, repository, issueNumber }, 'Failed to create issue comment');
        return reply.status(502).send({ error: 'GitHub API error', details: (err as Error).message });
      }
    },
  );

  // ---------- POST /api/engine/issue-labels ----------
  fastify.post(
    '/api/engine/issue-labels',
    async (
      request: FastifyRequest<{
        Body: { repository: string; issueNumber: number; labels: string[] };
      }>,
      reply: FastifyReply,
    ) => {
      const client = requireOctokit(request, reply);
      if (client === undefined) return;

      const validated = IssueLabelsBodySchema.safeParse(request.body);
      if (!validated.success) {
        return reply.status(400).send({ error: validated.error.message });
      }

      const { repository, issueNumber, labels } = validated.data;

      let parsed: { owner: string; repo: string };
      try {
        parsed = parseRepo(repository);
      } catch (err: unknown) {
        return reply.status(400).send({ error: (err as Error).message });
      }

      try {
        const { data } = await client.rest.issues.addLabels({
          owner: parsed.owner,
          repo: parsed.repo,
          issue_number: issueNumber,
          labels,
        });

        fastify.log.info(
          { repository, issueNumber, labels },
          'Labels added to issue',
        );

        return reply.send({ labels: data.map((l) => l.name) });
      } catch (err: unknown) {
        fastify.log.error({ err, repository, issueNumber, labels }, 'Failed to add issue labels');
        return reply.status(502).send({ error: 'GitHub API error', details: (err as Error).message });
      }
    },
  );

  // ---------- DELETE /api/engine/issue-labels/:repo/:issueNumber/:label ----------
  fastify.delete(
    '/api/engine/issue-labels/:repo/:issueNumber/:label',
    async (
      request: FastifyRequest<{
        Params: { repo: string; issueNumber: string; label: string };
      }>,
      reply: FastifyReply,
    ) => {
      const client = requireOctokit(request, reply);
      if (client === undefined) return;

      const { repo: repoParam, issueNumber: issueNumStr, label } = request.params;
      const issueNumber = parseInt(issueNumStr, 10);

      if (Number.isNaN(issueNumber) || issueNumber <= 0) {
        return reply.status(400).send({ error: 'Invalid issueNumber parameter' });
      }

      let parsed: { owner: string; repo: string };
      try {
        parsed = parseRepo(repoParam);
      } catch (err: unknown) {
        return reply.status(400).send({ error: (err as Error).message });
      }

      try {
        await client.rest.issues.removeLabel({
          owner: parsed.owner,
          repo: parsed.repo,
          issue_number: issueNumber,
          name: label,
        });

        fastify.log.info(
          { repository: repoParam, issueNumber, label },
          'Label removed from issue',
        );

        return reply.send({ removed: true, label });
      } catch (err: unknown) {
        fastify.log.error(
          { err, repository: repoParam, issueNumber, label },
          'Failed to remove issue label',
        );
        return reply.status(502).send({ error: 'GitHub API error', details: (err as Error).message });
      }
    },
  );

  // ---------- POST /api/engine/create-issue ----------
  fastify.post(
    '/api/engine/create-issue',
    async (
      request: FastifyRequest<{
        Body: {
          repository: string;
          title: string;
          body?: string;
          labels?: string[];
          assignees?: string[];
        };
      }>,
      reply: FastifyReply,
    ) => {
      const client = requireOctokit(request, reply);
      if (client === undefined) return;

      const validated = CreateIssueBodySchema.safeParse(request.body);
      if (!validated.success) {
        return reply.status(400).send({ error: validated.error.message });
      }

      const { repository, title, body, labels, assignees } = validated.data;

      let parsed: { owner: string; repo: string };
      try {
        parsed = parseRepo(repository);
      } catch (err: unknown) {
        return reply.status(400).send({ error: (err as Error).message });
      }

      try {
        const createOpts: Parameters<typeof client.rest.issues.create>[0] = {
          owner: parsed.owner,
          repo: parsed.repo,
          title,
        };
        if (body !== undefined) {
          createOpts.body = body;
        }
        if (labels !== undefined && labels.length > 0) {
          createOpts.labels = labels;
        }
        if (assignees !== undefined && assignees.length > 0) {
          createOpts.assignees = assignees;
        }

        const { data } = await client.rest.issues.create(createOpts);

        fastify.log.info(
          { repository, issueNumber: data.number, title },
          'Issue created',
        );

        return reply.status(201).send({
          number: data.number,
          htmlUrl: data.html_url,
          title: data.title,
        });
      } catch (err: unknown) {
        fastify.log.error({ err, repository, title }, 'Failed to create issue');
        return reply.status(502).send({ error: 'GitHub API error', details: (err as Error).message });
      }
    },
  );

  // ---------- POST /api/engine/trigger-ci ----------
  fastify.post(
    '/api/engine/trigger-ci',
    async (
      request: FastifyRequest<{
        Body: {
          repository: string;
          branchName: string;
          workflowFile: string;
          inputs?: Record<string, string>;
        };
      }>,
      reply: FastifyReply,
    ) => {
      const client = requireOctokit(request, reply);
      if (client === undefined) return;

      const validated = TriggerCIBodySchema.safeParse(request.body);
      if (!validated.success) {
        return reply.status(400).send({ error: validated.error.message });
      }

      const { repository, branchName, workflowFile, inputs } = validated.data;

      let parsed: { owner: string; repo: string };
      try {
        parsed = parseRepo(repository);
      } catch (err: unknown) {
        return reply.status(400).send({ error: (err as Error).message });
      }

      try {
        await client.rest.actions.createWorkflowDispatch({
          owner: parsed.owner,
          repo: parsed.repo,
          workflow_id: workflowFile,
          ref: branchName,
          ...(inputs !== undefined && Object.keys(inputs).length > 0
            ? { inputs }
            : {}),
        });

        fastify.log.info(
          { repository, branchName, workflowFile },
          'CI workflow dispatched',
        );

        return reply.send({ dispatched: true, workflowFile, branch: branchName });
      } catch (err: unknown) {
        fastify.log.error(
          { err, repository, branchName, workflowFile },
          'Failed to dispatch CI workflow',
        );
        return reply.status(502).send({ error: 'GitHub API error', details: (err as Error).message });
      }
    },
  );
}
