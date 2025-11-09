# Git Provider Abstraction Layer

## Overview

A provider-agnostic abstraction that allows the documentation review system to work with **any Git platform**: GitHub, GitLab, Gitea, Forgejo, Bitbucket, Azure DevOps, or even plain Git servers.

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│           Documentation Review Application              │
├─────────────────────────────────────────────────────────┤
│                                                         │
│              IGitProvider Interface                     │
│            (Platform-agnostic API)                      │
│                                                         │
├──────────┬──────────┬──────────┬──────────┬────────────┤
│          │          │          │          │            │
│  GitHub  │  GitLab  │  Gitea   │ Bitbucket│ Azure DevOps│
│ Provider │ Provider │ Provider │ Provider │  Provider  │
│          │          │          │          │            │
└──────────┴──────────┴──────────┴──────────┴────────────┘
```

---

## Core Interface

**File: `app/lib/git/providers/interface.ts`**

```typescript
export interface IGitProvider {
  // File Operations
  getFile(path: string, ref?: string): Promise<GitFile>;
  getFileAtCommit(path: string, commitSha: string): Promise<GitFile>;
  listFiles(directory: string, ref?: string): Promise<GitTreeItem[]>;

  // History & Versioning
  getFileHistory(path: string, options?: HistoryOptions): Promise<GitCommit[]>;
  getCommit(sha: string): Promise<GitCommit>;
  compareCommits(base: string, head: string): Promise<GitComparison>;

  // Blame (Line Attribution)
  getBlame(path: string, ref?: string): Promise<GitBlameLine[]>;

  // Branches & References
  listBranches(): Promise<GitBranch[]>;
  getBranch(name: string): Promise<GitBranch>;
  createBranch(name: string, fromRef: string): Promise<GitBranch>;

  // Pull/Merge Requests
  createPullRequest(params: CreatePRParams): Promise<GitPullRequest>;
  getPullRequest(id: number | string): Promise<GitPullRequest>;
  listPullRequests(options?: PRListOptions): Promise<GitPullRequest[]>;
  updatePullRequest(id: number | string, params: UpdatePRParams): Promise<GitPullRequest>;

  // Comments (PR/MR Comments)
  addPRComment(prId: number | string, comment: string): Promise<GitComment>;
  listPRComments(prId: number | string): Promise<GitComment[]>;

  // Webhooks
  validateWebhook(payload: string, signature: string, secret: string): boolean;
  parseWebhookEvent(payload: any, eventType: string): GitWebhookEvent;

  // Authentication
  validateToken(): Promise<boolean>;
  getCurrentUser(): Promise<GitUser>;
}

// Common Types
export interface GitFile {
  path: string;
  content: string;
  sha: string;
  size: number;
  encoding?: string;
  url: string;
  commitSha?: string;
}

export interface GitTreeItem {
  path: string;
  type: "blob" | "tree";
  sha: string;
  size?: number;
  mode?: string;
}

export interface GitCommit {
  sha: string;
  message: string;
  author: GitAuthor;
  committer: GitAuthor;
  date: Date;
  url: string;
  parents?: string[];
}

export interface GitAuthor {
  name: string;
  email: string;
  date: Date;
  username?: string;
  avatarUrl?: string;
}

export interface GitBlameLine {
  lineNumber: number;
  content: string;
  commit: GitCommit;
  author: GitAuthor;
}

export interface GitBranch {
  name: string;
  sha: string;
  protected: boolean;
  default: boolean;
}

export interface GitPullRequest {
  id: number | string;
  number: number;
  title: string;
  description: string;
  state: "open" | "closed" | "merged";
  author: GitUser;
  sourceBranch: string;
  targetBranch: string;
  url: string;
  createdAt: Date;
  updatedAt: Date;
  mergedAt?: Date;
}

export interface GitComment {
  id: number | string;
  body: string;
  author: GitUser;
  createdAt: Date;
  updatedAt: Date;
  url: string;
}

export interface GitUser {
  id: number | string;
  username: string;
  name?: string;
  email?: string;
  avatarUrl?: string;
}

export interface GitComparison {
  baseCommit: string;
  headCommit: string;
  diff: string;
  filesChanged: number;
  additions: number;
  deletions: number;
  files: GitFileDiff[];
}

export interface GitFileDiff {
  path: string;
  status: "added" | "modified" | "deleted" | "renamed";
  additions: number;
  deletions: number;
  patch?: string;
}

export interface GitWebhookEvent {
  type: "push" | "pull_request" | "merge_request" | "tag";
  ref?: string;
  commits?: GitCommit[];
  pullRequest?: GitPullRequest;
}

export interface CreatePRParams {
  title: string;
  description: string;
  sourceBranch: string;
  targetBranch: string;
  draft?: boolean;
}

export interface UpdatePRParams {
  title?: string;
  description?: string;
  state?: "open" | "closed";
}

export interface HistoryOptions {
  limit?: number;
  since?: Date;
  until?: Date;
  author?: string;
}

export interface PRListOptions {
  state?: "open" | "closed" | "merged" | "all";
  limit?: number;
  author?: string;
}
```

---

## Provider Implementations

### 1. GitHub Provider

**File: `app/lib/git/providers/github.ts`**

```typescript
import type { IGitProvider, GitFile, GitCommit, GitPullRequest } from "./interface";

export class GitHubProvider implements IGitProvider {
  private baseUrl: string;

  constructor(
    private config: {
      owner: string;
      repo: string;
      token: string;
      apiUrl?: string; // For GitHub Enterprise
    }
  ) {
    this.baseUrl = config.apiUrl || "https://api.github.com";
  }

  async getFile(path: string, ref = "main"): Promise<GitFile> {
    const url = `${this.baseUrl}/repos/${this.config.owner}/${this.config.repo}/contents/${path}?ref=${ref}`;

    const response = await this.fetch(url);
    const data = await response.json();

    return {
      path: data.path,
      content: this.decodeContent(data.content, data.encoding),
      sha: data.sha,
      size: data.size,
      encoding: data.encoding,
      url: data.html_url,
    };
  }

  async getFileHistory(path: string, options: HistoryOptions = {}): Promise<GitCommit[]> {
    const params = new URLSearchParams({
      path,
      per_page: (options.limit || 50).toString(),
    });

    if (options.since) params.append("since", options.since.toISOString());
    if (options.until) params.append("until", options.until.toISOString());
    if (options.author) params.append("author", options.author);

    const url = `${this.baseUrl}/repos/${this.config.owner}/${this.config.repo}/commits?${params}`;

    const response = await this.fetch(url);
    const data = await response.json();

    return data.map((commit: any) => this.parseCommit(commit));
  }

  async getBlame(path: string, ref = "main"): Promise<GitBlameLine[]> {
    // GitHub doesn't have REST API for blame, use GraphQL
    const query = `
      query($owner: String!, $repo: String!, $path: String!, $ref: String!) {
        repository(owner: $owner, name: $repo) {
          object(expression: $ref) {
            ... on Commit {
              blame(path: $path) {
                ranges {
                  commit {
                    oid
                    message
                    author { name email date }
                  }
                  startingLine
                  endingLine
                }
              }
            }
          }
        }
      }
    `;

    const response = await fetch(`${this.baseUrl}/graphql`, {
      method: "POST",
      headers: {
        Authorization: `bearer ${this.config.token}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify({
        query,
        variables: {
          owner: this.config.owner,
          repo: this.config.repo,
          path,
          ref,
        },
      }),
    });

    const result = await response.json();
    const ranges = result.data.repository.object.blame.ranges;

    return this.parseBlameRanges(ranges);
  }

  async createPullRequest(params: CreatePRParams): Promise<GitPullRequest> {
    const url = `${this.baseUrl}/repos/${this.config.owner}/${this.config.repo}/pulls`;

    const response = await this.fetch(url, {
      method: "POST",
      body: JSON.stringify({
        title: params.title,
        body: params.description,
        head: params.sourceBranch,
        base: params.targetBranch,
        draft: params.draft,
      }),
    });

    const data = await response.json();
    return this.parsePullRequest(data);
  }

  validateWebhook(payload: string, signature: string, secret: string): boolean {
    const crypto = require("crypto");
    const hmac = crypto.createHmac("sha256", secret);
    const digest = `sha256=${hmac.update(payload).digest("hex")}`;
    return crypto.timingSafeEqual(Buffer.from(signature), Buffer.from(digest));
  }

  parseWebhookEvent(payload: any, eventType: string): GitWebhookEvent {
    if (eventType === "push") {
      return {
        type: "push",
        ref: payload.ref,
        commits: payload.commits.map((c: any) => this.parseCommit(c)),
      };
    } else if (eventType === "pull_request") {
      return {
        type: "pull_request",
        pullRequest: this.parsePullRequest(payload.pull_request),
      };
    }
    throw new Error(`Unknown event type: ${eventType}`);
  }

  // Helper methods
  private async fetch(url: string, options: RequestInit = {}): Promise<Response> {
    return fetch(url, {
      ...options,
      headers: {
        Authorization: `token ${this.config.token}`,
        Accept: "application/vnd.github.v3+json",
        "Content-Type": "application/json",
        ...options.headers,
      },
    });
  }

  private decodeContent(content: string, encoding: string): string {
    if (encoding === "base64") {
      return atob(content);
    }
    return content;
  }

  private parseCommit(data: any): GitCommit {
    return {
      sha: data.sha || data.oid,
      message: data.commit?.message || data.message,
      author: {
        name: data.commit?.author?.name || data.author.name,
        email: data.commit?.author?.email || data.author.email,
        date: new Date(data.commit?.author?.date || data.author.date),
      },
      committer: {
        name: data.commit?.committer?.name || data.committer.name,
        email: data.commit?.committer?.email || data.committer.email,
        date: new Date(data.commit?.committer?.date || data.committer.date),
      },
      date: new Date(data.commit?.author?.date || data.author.date),
      url: data.html_url || "",
    };
  }

  private parsePullRequest(data: any): GitPullRequest {
    return {
      id: data.id,
      number: data.number,
      title: data.title,
      description: data.body,
      state: data.state === "open" ? "open" : data.merged ? "merged" : "closed",
      author: {
        id: data.user.id,
        username: data.user.login,
        avatarUrl: data.user.avatar_url,
      },
      sourceBranch: data.head.ref,
      targetBranch: data.base.ref,
      url: data.html_url,
      createdAt: new Date(data.created_at),
      updatedAt: new Date(data.updated_at),
      mergedAt: data.merged_at ? new Date(data.merged_at) : undefined,
    };
  }

  private parseBlameRanges(ranges: any[]): GitBlameLine[] {
    const lines: GitBlameLine[] = [];
    // Implementation details...
    return lines;
  }

  // Implement remaining interface methods...
  async getFileAtCommit(path: string, commitSha: string): Promise<GitFile> {
    return this.getFile(path, commitSha);
  }

  async listFiles(directory: string, ref = "main"): Promise<GitTreeItem[]> {
    // Implementation...
    return [];
  }

  async getCommit(sha: string): Promise<GitCommit> {
    // Implementation...
    return {} as GitCommit;
  }

  async compareCommits(base: string, head: string): Promise<GitComparison> {
    // Implementation...
    return {} as GitComparison;
  }

  async listBranches(): Promise<GitBranch[]> {
    // Implementation...
    return [];
  }

  async getBranch(name: string): Promise<GitBranch> {
    // Implementation...
    return {} as GitBranch;
  }

  async createBranch(name: string, fromRef: string): Promise<GitBranch> {
    // Implementation...
    return {} as GitBranch;
  }

  async getPullRequest(id: number | string): Promise<GitPullRequest> {
    // Implementation...
    return {} as GitPullRequest;
  }

  async listPullRequests(options: PRListOptions = {}): Promise<GitPullRequest[]> {
    // Implementation...
    return [];
  }

  async updatePullRequest(id: number | string, params: UpdatePRParams): Promise<GitPullRequest> {
    // Implementation...
    return {} as GitPullRequest;
  }

  async addPRComment(prId: number | string, comment: string): Promise<GitComment> {
    // Implementation...
    return {} as GitComment;
  }

  async listPRComments(prId: number | string): Promise<GitComment[]> {
    // Implementation...
    return [];
  }

  async validateToken(): Promise<boolean> {
    // Implementation...
    return true;
  }

  async getCurrentUser(): Promise<GitUser> {
    // Implementation...
    return {} as GitUser;
  }
}
```

### 2. GitLab Provider

**File: `app/lib/git/providers/gitlab.ts`**

```typescript
import type { IGitProvider, GitFile, GitCommit } from "./interface";

export class GitLabProvider implements IGitProvider {
  private baseUrl: string;

  constructor(
    private config: {
      projectId: string; // GitLab uses project ID
      token: string;
      apiUrl?: string; // For self-hosted GitLab
    }
  ) {
    this.baseUrl = config.apiUrl || "https://gitlab.com/api/v4";
  }

  async getFile(path: string, ref = "main"): Promise<GitFile> {
    // URL encode path
    const encodedPath = encodeURIComponent(path);
    const url = `${this.baseUrl}/projects/${this.config.projectId}/repository/files/${encodedPath}?ref=${ref}`;

    const response = await this.fetch(url);
    const data = await response.json();

    return {
      path: data.file_path,
      content: this.decodeContent(data.content, data.encoding),
      sha: data.blob_id,
      size: data.size,
      encoding: data.encoding,
      url: data.file_url || "",
    };
  }

  async getFileHistory(path: string, options: HistoryOptions = {}): Promise<GitCommit[]> {
    const params = new URLSearchParams({
      path,
      per_page: (options.limit || 50).toString(),
    });

    if (options.since) params.append("since", options.since.toISOString());
    if (options.until) params.append("until", options.until.toISOString());

    const url = `${this.baseUrl}/projects/${this.config.projectId}/repository/commits?${params}`;

    const response = await this.fetch(url);
    const data = await response.json();

    return data.map((commit: any) => this.parseCommit(commit));
  }

  async getBlame(path: string, ref = "main"): Promise<GitBlameLine[]> {
    const encodedPath = encodeURIComponent(path);
    const url = `${this.baseUrl}/projects/${this.config.projectId}/repository/files/${encodedPath}/blame?ref=${ref}`;

    const response = await this.fetch(url);
    const data = await response.json();

    return data.map((blame: any, index: number) => ({
      lineNumber: index + 1,
      content: blame.lines.join("\n"),
      commit: this.parseCommit(blame.commit),
      author: {
        name: blame.commit.author_name,
        email: blame.commit.author_email,
        date: new Date(blame.commit.authored_date),
      },
    }));
  }

  async createPullRequest(params: CreatePRParams): Promise<GitPullRequest> {
    // GitLab calls them "merge requests"
    const url = `${this.baseUrl}/projects/${this.config.projectId}/merge_requests`;

    const response = await this.fetch(url, {
      method: "POST",
      body: JSON.stringify({
        title: params.title,
        description: params.description,
        source_branch: params.sourceBranch,
        target_branch: params.targetBranch,
        draft: params.draft,
      }),
    });

    const data = await response.json();
    return this.parseMergeRequest(data);
  }

  validateWebhook(payload: string, signature: string, secret: string): boolean {
    // GitLab uses X-Gitlab-Token header
    return signature === secret;
  }

  parseWebhookEvent(payload: any, eventType: string): GitWebhookEvent {
    if (payload.object_kind === "push") {
      return {
        type: "push",
        ref: payload.ref,
        commits: payload.commits.map((c: any) => this.parseCommit(c)),
      };
    } else if (payload.object_kind === "merge_request") {
      return {
        type: "pull_request",
        pullRequest: this.parseMergeRequest(payload.object_attributes),
      };
    }
    throw new Error(`Unknown event type: ${eventType}`);
  }

  // Helper methods
  private async fetch(url: string, options: RequestInit = {}): Promise<Response> {
    return fetch(url, {
      ...options,
      headers: {
        "PRIVATE-TOKEN": this.config.token,
        "Content-Type": "application/json",
        ...options.headers,
      },
    });
  }

  private decodeContent(content: string, encoding: string): string {
    if (encoding === "base64") {
      return atob(content);
    }
    return content;
  }

  private parseCommit(data: any): GitCommit {
    return {
      sha: data.id || data.sha,
      message: data.message,
      author: {
        name: data.author_name,
        email: data.author_email,
        date: new Date(data.authored_date || data.timestamp),
      },
      committer: {
        name: data.committer_name || data.author_name,
        email: data.committer_email || data.author_email,
        date: new Date(data.committed_date || data.timestamp),
      },
      date: new Date(data.authored_date || data.timestamp),
      url: data.web_url || "",
    };
  }

  private parseMergeRequest(data: any): GitPullRequest {
    return {
      id: data.id,
      number: data.iid,
      title: data.title,
      description: data.description,
      state: data.state === "opened" ? "open" : data.state === "merged" ? "merged" : "closed",
      author: {
        id: data.author.id,
        username: data.author.username,
        name: data.author.name,
        avatarUrl: data.author.avatar_url,
      },
      sourceBranch: data.source_branch,
      targetBranch: data.target_branch,
      url: data.web_url,
      createdAt: new Date(data.created_at),
      updatedAt: new Date(data.updated_at),
      mergedAt: data.merged_at ? new Date(data.merged_at) : undefined,
    };
  }

  // Implement remaining interface methods...
  // (Similar to GitHub but with GitLab API specifics)
}
```

### 3. Gitea/Forgejo Provider

**File: `app/lib/git/providers/gitea.ts`**

```typescript
import type { IGitProvider } from "./interface";

export class GiteaProvider implements IGitProvider {
  // Gitea API is very similar to GitHub
  // Forgejo is a Gitea fork, so this works for both

  private baseUrl: string;

  constructor(
    private config: {
      owner: string;
      repo: string;
      token: string;
      apiUrl: string; // Required for self-hosted
    }
  ) {
    this.baseUrl = `${config.apiUrl}/api/v1`;
  }

  // Implementation is nearly identical to GitHub
  // Just different base URL and minor API differences
}
```

---

## Provider Factory

**File: `app/lib/git/providers/factory.ts`**

```typescript
import type { IGitProvider } from "./interface";
import { GitHubProvider } from "./github";
import { GitLabProvider } from "./gitlab";
import { GiteaProvider } from "./gitea";

export type GitProviderType = "github" | "gitlab" | "gitea" | "forgejo" | "bitbucket" | "azure-devops";

export interface GitProviderConfig {
  type: GitProviderType;
  token: string;

  // GitHub/Gitea/Forgejo
  owner?: string;
  repo?: string;

  // GitLab
  projectId?: string;

  // Self-hosted instances
  apiUrl?: string;
}

export function createGitProvider(config: GitProviderConfig): IGitProvider {
  switch (config.type) {
    case "github":
      if (!config.owner || !config.repo) {
        throw new Error("GitHub requires owner and repo");
      }
      return new GitHubProvider({
        owner: config.owner,
        repo: config.repo,
        token: config.token,
        apiUrl: config.apiUrl,
      });

    case "gitlab":
      if (!config.projectId) {
        throw new Error("GitLab requires projectId");
      }
      return new GitLabProvider({
        projectId: config.projectId,
        token: config.token,
        apiUrl: config.apiUrl,
      });

    case "gitea":
    case "forgejo":
      if (!config.owner || !config.repo || !config.apiUrl) {
        throw new Error("Gitea/Forgejo requires owner, repo, and apiUrl");
      }
      return new GiteaProvider({
        owner: config.owner,
        repo: config.repo,
        token: config.token,
        apiUrl: config.apiUrl,
      });

    default:
      throw new Error(`Unsupported provider type: ${config.type}`);
  }
}
```

---

## Usage in Application

**File: `app/lib/docs/loader.server.ts`**

```typescript
import { createGitProvider } from "../git/providers/factory";
import type { IGitProvider } from "../git/providers/interface";

export class DocumentLoader {
  private git: IGitProvider;

  constructor(config: GitProviderConfig) {
    this.git = createGitProvider(config);
  }

  async loadDocument(path: string, ref?: string): Promise<Document> {
    const file = await this.git.getFile(path, ref);

    // Same logic regardless of provider!
    return {
      path: file.path,
      sha: file.sha,
      content: file.content,
      lines: file.content.split("\n"),
      url: file.url,
    };
  }

  async getDocumentHistory(path: string): Promise<DocumentVersion[]> {
    const commits = await this.git.getFileHistory(path);

    return commits.map((commit) => ({
      sha: commit.sha,
      message: commit.message,
      author: commit.author.name,
      date: commit.date,
      url: commit.url,
    }));
  }

  async createSuggestionPR(suggestion: SuggestionData): Promise<PullRequest> {
    // Works with any provider!
    return await this.git.createPullRequest({
      title: suggestion.title,
      description: suggestion.description,
      sourceBranch: suggestion.branch,
      targetBranch: "main",
    });
  }
}
```

---

## Configuration

**File: `wrangler.jsonc`**

```jsonc
{
  "vars": {
    "GIT_PROVIDER": "github",          // or "gitlab", "gitea", etc.
    "GIT_OWNER": "meywd",              // For GitHub/Gitea
    "GIT_REPO": "tamma",               // For GitHub/Gitea
    "GIT_PROJECT_ID": "",              // For GitLab
    "GIT_API_URL": "",                 // For self-hosted instances
    "GIT_DEFAULT_BRANCH": "main",
    "DOCS_PATH": "docs"
  }
}
```

**Secrets:**
```bash
pnpm wrangler secret put GIT_TOKEN
pnpm wrangler secret put GIT_WEBHOOK_SECRET
```

---

## Benefits of Abstraction

### 1. **Provider Independence**
- Switch providers without changing application code
- Support multiple repos from different platforms
- Easy migration between platforms

### 2. **Consistent API**
- Same interface regardless of provider
- Predictable behavior across platforms
- Easier testing and maintenance

### 3. **Future-Proof**
- Add new providers without breaking changes
- Support enterprise Git solutions
- Handle platform-specific features gracefully

### 4. **Self-Hosted Support**
- Works with GitHub Enterprise
- Works with self-hosted GitLab
- Works with Gitea/Forgejo instances

---

## Provider Comparison

| Feature | GitHub | GitLab | Gitea | Forgejo | Bitbucket |
|---------|--------|--------|-------|---------|-----------|
| REST API | ✅ | ✅ | ✅ | ✅ | ✅ |
| GraphQL | ✅ | ✅ | ❌ | ❌ | ❌ |
| Blame API | GraphQL | REST | REST | REST | REST |
| Webhooks | ✅ | ✅ | ✅ | ✅ | ✅ |
| OAuth | ✅ | ✅ | ✅ | ✅ | ✅ |
| Self-Hosted | Enterprise | ✅ | ✅ | ✅ | Server |

---

## Next Steps

1. **Implement Core Interface** - Complete `IGitProvider`
2. **GitHub Provider** - Primary implementation (most users)
3. **GitLab Provider** - Second most common
4. **Gitea/Forgejo** - For self-hosted users
5. **Add Caching** - Wrap providers with cache layer
6. **Add Webhooks** - Listen for repo changes

---

## Conclusion

This **provider-agnostic abstraction** allows your documentation review system to work with **any Git platform** while maintaining a single, clean codebase. Users can choose their preferred platform without any code changes!

**Key Achievement**: Write once, run on **any Git platform**! 🎯
