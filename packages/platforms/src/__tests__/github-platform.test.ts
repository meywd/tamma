import { describe, it, expect, vi, beforeEach } from 'vitest';
import { GitHubPlatform } from '../github/github-platform.js';
import {
  mockRepoResponse,
  mockRefResponse,
  mockIssueResponse,
  mockCommentResponse,
  mockPullRequestResponse,
  mockMergeResponse,
  mockCombinedStatusResponse,
  mockCheckRunsResponse,
} from './fixtures/github-responses.js';

// Mock Octokit
const mockOctokit = {
  rest: {
    repos: {
      get: vi.fn(),
      getCombinedStatusForRef: vi.fn(),
      listCommits: vi.fn(),
    },
    git: {
      getRef: vi.fn(),
      createRef: vi.fn(),
      deleteRef: vi.fn(),
    },
    issues: {
      get: vi.fn(),
      listForRepo: vi.fn(),
      listComments: vi.fn(),
      createComment: vi.fn(),
      update: vi.fn(),
      addAssignees: vi.fn(),
      addLabels: vi.fn(),
    },
    pulls: {
      create: vi.fn(),
      get: vi.fn(),
      update: vi.fn(),
      merge: vi.fn(),
      requestReviewers: vi.fn(),
    },
    checks: {
      listForRef: vi.fn(),
    },
  },
};

vi.mock('@octokit/rest', () => ({
  // Vitest 4: constructor mocks must use `function` keyword, not arrow
  Octokit: vi.fn(function () {
    return mockOctokit;
  }),
}));

vi.mock('@octokit/auth-app', () => ({
  createAppAuth: vi.fn(),
}));

describe('GitHubPlatform', () => {
  let platform: GitHubPlatform;

  beforeEach(async () => {
    vi.clearAllMocks();
    platform = new GitHubPlatform();
    await platform.initialize({ type: 'pat', token: 'test-token' });
  });

  describe('platformName', () => {
    it('should be "github"', () => {
      expect(platform.platformName).toBe('github');
    });
  });

  describe('getRepository', () => {
    it('should return mapped repository', async () => {
      mockOctokit.rest.repos.get.mockResolvedValue({ data: mockRepoResponse });
      const repo = await platform.getRepository('test-owner', 'test-repo');
      expect(repo.defaultBranch).toBe('main');
      expect(repo.owner).toBe('test-owner');
    });
  });

  describe('getBranch', () => {
    it('should return mapped branch', async () => {
      mockOctokit.rest.git.getRef.mockResolvedValue({ data: mockRefResponse });
      const branch = await platform.getBranch('owner', 'repo', 'main');
      expect(branch.name).toBe('main');
      expect(branch.sha).toBe('abc123');
    });
  });

  describe('createBranch', () => {
    it('should create branch from ref', async () => {
      mockOctokit.rest.git.getRef.mockResolvedValue({ data: mockRefResponse });
      mockOctokit.rest.git.createRef.mockResolvedValue({ data: mockRefResponse });

      const branch = await platform.createBranch(
        'owner',
        'repo',
        'feature/42-fix',
        'main',
      );

      expect(mockOctokit.rest.git.createRef).toHaveBeenCalledWith(
        expect.objectContaining({
          ref: 'refs/heads/feature/42-fix',
          sha: 'abc123',
        }),
      );
      expect(branch.name).toBe('feature/42-fix');
    });
  });

  describe('getIssue', () => {
    it('should return issue with comments', async () => {
      mockOctokit.rest.issues.get.mockResolvedValue({
        data: mockIssueResponse,
      });
      mockOctokit.rest.issues.listComments.mockResolvedValue({
        data: [mockCommentResponse],
      });

      const issue = await platform.getIssue('owner', 'repo', 42);
      expect(issue.number).toBe(42);
      expect(issue.comments).toHaveLength(1);
    });
  });

  describe('listIssues', () => {
    it('should return paginated issues filtering out PRs', async () => {
      mockOctokit.rest.issues.listForRepo.mockResolvedValue({
        data: [
          mockIssueResponse,
          { ...mockIssueResponse, number: 43, pull_request: { url: 'https://...' } },
        ],
        headers: {},
      });

      const result = await platform.listIssues('owner', 'repo', {
        labels: ['tamma'],
        direction: 'asc',
      });

      expect(result.data).toHaveLength(1);
      expect(result.data[0]!.number).toBe(42);
    });
  });

  describe('assignIssue', () => {
    it('should assign users to issue', async () => {
      mockOctokit.rest.issues.addAssignees.mockResolvedValue({
        data: { ...mockIssueResponse, assignees: [{ login: 'bot' }] },
      });

      const issue = await platform.assignIssue('owner', 'repo', 42, ['bot']);
      expect(mockOctokit.rest.issues.addAssignees).toHaveBeenCalledWith(
        expect.objectContaining({ assignees: ['bot'] }),
      );
      expect(issue.number).toBe(42);
    });
  });

  describe('createPR', () => {
    it('should create PR with labels', async () => {
      mockOctokit.rest.pulls.create.mockResolvedValue({
        data: mockPullRequestResponse,
      });
      mockOctokit.rest.issues.addLabels.mockResolvedValue({ data: [] });

      const pr = await platform.createPR('owner', 'repo', {
        title: 'Fix auth bug',
        body: 'Closes #42',
        head: 'feature/42-fix',
        base: 'main',
        labels: ['tamma-automated'],
      });

      expect(pr.number).toBe(99);
      expect(mockOctokit.rest.issues.addLabels).toHaveBeenCalled();
    });
  });

  describe('mergePR', () => {
    it('should merge with squash by default', async () => {
      mockOctokit.rest.pulls.merge.mockResolvedValue({
        data: mockMergeResponse,
      });

      const result = await platform.mergePR('owner', 'repo', 99);
      expect(result.merged).toBe(true);
      expect(mockOctokit.rest.pulls.merge).toHaveBeenCalledWith(
        expect.objectContaining({ merge_method: 'squash' }),
      );
    });
  });

  describe('getCIStatus', () => {
    it('should combine status and checks', async () => {
      mockOctokit.rest.repos.getCombinedStatusForRef.mockResolvedValue({
        data: mockCombinedStatusResponse,
      });
      mockOctokit.rest.checks.listForRef.mockResolvedValue({
        data: mockCheckRunsResponse,
      });

      const status = await platform.getCIStatus('owner', 'repo', 'abc123');
      expect(status.state).toBe('success');
      expect(status.totalCount).toBe(2);
    });
  });

  describe('listCommits', () => {
    it('should list commits', async () => {
      mockOctokit.rest.repos.listCommits.mockResolvedValue({
        data: [{
          sha: 'abc123',
          commit: { message: 'fix: something', author: { name: 'dev', date: '2024-01-01T00:00:00Z' } },
        }],
        headers: {},
      });

      const commits = await platform.listCommits('owner', 'repo');
      expect(commits).toHaveLength(1);
      expect(commits[0]!.sha).toBe('abc123');
    });
  });

  describe('error handling', () => {
    it('should throw when not initialized', async () => {
      const uninitPlatform = new GitHubPlatform();
      await expect(
        uninitPlatform.getRepository('owner', 'repo'),
      ).rejects.toThrow('not initialized');
    });
  });
});
