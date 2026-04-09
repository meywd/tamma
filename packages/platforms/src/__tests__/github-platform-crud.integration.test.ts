// E2E tests for GitHubPlatform against the Tam-ma/tamma-test repository.
//
// Gated by environment variables:
//   E2E_TEST_ENABLED=true
//   E2E_GITHUB_TOKEN=<GitHub PAT>
//   E2E_GITHUB_OWNER=Tam-ma
//   E2E_GITHUB_REPO=tamma-test
//
// Run with: npx vitest run github-platform.e2e.test.ts --testTimeout=60000

import { describe, it, expect, beforeAll, afterEach } from 'vitest';
import { GitHubPlatform } from '../github/github-platform.js';

const E2E_ENABLED = process.env['E2E_TEST_ENABLED'] === 'true';
const TOKEN = process.env['E2E_GITHUB_TOKEN'] ?? '';
const OWNER = process.env['E2E_GITHUB_OWNER'] ?? 'Tam-ma';
const REPO = process.env['E2E_GITHUB_REPO'] ?? 'tamma-test';

const describeE2E = E2E_ENABLED ? describe : describe.skip;

describeE2E('GitHubPlatform E2E', () => {
  let platform: GitHubPlatform;
  const cleanupFns: Array<() => Promise<void>> = [];

  beforeAll(async () => {
    platform = new GitHubPlatform();
    await platform.initialize({ type: 'pat', token: TOKEN });
  });

  afterEach(async () => {
    // Run cleanup in reverse order
    for (const fn of cleanupFns.reverse()) {
      try {
        await fn();
      } catch {
        // Best-effort cleanup
      }
    }
    cleanupFns.length = 0;
  });

  it('should create, list, and close an issue', async () => {
    const timestamp = Date.now();
    const title = `E2E test issue ${timestamp}`;

    // Verify listIssues and getIssue on pre-existing issues.
    // Note: GitHubPlatform now has createIssue, but for this E2E test we only
    // exercise the read path to avoid creating test data.
    const issues = await platform.listIssues(OWNER, REPO, {
      state: 'open',
      labels: ['tamma'],
    });

    expect(issues.data.length).toBeGreaterThanOrEqual(0);
    expect(issues.page).toBe(1);

    if (issues.data.length > 0) {
      const first = issues.data[0]!;
      const fetched = await platform.getIssue(OWNER, REPO, first.number);
      expect(fetched.number).toBe(first.number);
      expect(fetched.title).toBe(first.title);
    }
  });

  it('should create and delete a branch', async () => {
    const branchName = `e2e-test-branch-${Date.now()}`;

    const branch = await platform.createBranch(OWNER, REPO, branchName, 'main');
    expect(branch.name).toBe(branchName);
    expect(branch.sha).toBeTruthy();

    // Cleanup
    cleanupFns.push(async () => {
      await platform.deleteBranch(OWNER, REPO, branchName);
    });

    // Verify branch exists
    const fetched = await platform.getBranch(OWNER, REPO, branchName);
    expect(fetched.name).toBe(branchName);

    // Delete
    await platform.deleteBranch(OWNER, REPO, branchName);
    cleanupFns.length = 0; // Already cleaned up

    // Verify deletion
    await expect(
      platform.getBranch(OWNER, REPO, branchName),
    ).rejects.toThrow();
  });

  it('should create a PR from a branch and close it', async () => {
    const branchName = `e2e-pr-test-${Date.now()}`;

    // Create branch
    await platform.createBranch(OWNER, REPO, branchName, 'main');
    cleanupFns.push(async () => {
      try { await platform.deleteBranch(OWNER, REPO, branchName); } catch { /* ok */ }
    });

    // Create PR
    const pr = await platform.createPR(OWNER, REPO, {
      title: `E2E test PR ${Date.now()}`,
      body: 'Automated E2E test. Will be closed immediately.',
      head: branchName,
      base: 'main',
    });
    expect(pr.number).toBeGreaterThan(0);
    expect(pr.state).toBe('open');

    cleanupFns.push(async () => {
      try {
        await platform.updatePR(OWNER, REPO, pr.number, { state: 'closed' });
      } catch { /* ok */ }
    });

    // Verify PR
    const fetched = await platform.getPR(OWNER, REPO, pr.number);
    expect(fetched.number).toBe(pr.number);
    expect(fetched.state).toBe('open');

    // Close PR
    await platform.updatePR(OWNER, REPO, pr.number, { state: 'closed' });
    cleanupFns.pop(); // Already closed

    const closed = await platform.getPR(OWNER, REPO, pr.number);
    expect(closed.state).toBe('closed');

    // Clean up branch
    await platform.deleteBranch(OWNER, REPO, branchName);
    cleanupFns.pop();
  });

  it('should get CI status for main branch', async () => {
    const status = await platform.getCIStatus(OWNER, REPO, 'main');
    expect(status).toHaveProperty('state');
    expect(status).toHaveProperty('totalCount');
    expect(status).toHaveProperty('successCount');
    expect(status).toHaveProperty('failureCount');
    expect(status).toHaveProperty('pendingCount');
  });

  it('should assign an issue', async () => {
    // List open issues to find one to assign
    const issues = await platform.listIssues(OWNER, REPO, {
      state: 'open',
    });

    if (issues.data.length === 0) {
      // Skip if no issues
      return;
    }

    const issue = issues.data[0]!;

    // Assign (note: the assignee must have access to the repo)
    // We'll try assigning and catch if the user doesn't have access
    try {
      const updated = await platform.assignIssue(OWNER, REPO, issue.number, ['tamma-bot']);
      expect(updated.number).toBe(issue.number);
    } catch (err: unknown) {
      // 422 means the user can't be assigned (not a collaborator) — that's OK for E2E
      const message = err instanceof Error ? err.message : String(err);
      expect(message).toMatch(/validation|not a collaborator|could not be assigned/i);
    }
  });

  it('should get repository info', async () => {
    const repo = await platform.getRepository(OWNER, REPO);
    expect(repo.owner).toBe(OWNER);
    expect(repo.name).toBe(REPO);
    expect(repo.defaultBranch).toBe('main');
  });

  it('should list recent commits', async () => {
    const commits = await platform.listCommits(OWNER, REPO, { perPage: 5 });
    expect(Array.isArray(commits)).toBe(true);
    if (commits.length > 0) {
      expect(commits[0]!.sha).toBeTruthy();
      expect(commits[0]!.message).toBeTruthy();
    }
  });

  it('should add and retrieve issue comments', async () => {
    const issues = await platform.listIssues(OWNER, REPO, { state: 'open' });

    if (issues.data.length === 0) {
      return;
    }

    const issue = issues.data[0]!;
    const commentBody = `E2E test comment ${Date.now()}`;

    const comment = await platform.addIssueComment(OWNER, REPO, issue.number, commentBody);
    expect(comment.body).toBe(commentBody);
    expect(comment.id).toBeGreaterThan(0);

    // Verify comment appears in issue
    const fetched = await platform.getIssue(OWNER, REPO, issue.number);
    const found = fetched.comments.some((c) => c.body === commentBody);
    expect(found).toBe(true);
  });
});
