# Git-Based Documentation Architecture

## Overview

Instead of just reading markdown files, we use **Git as the foundation** for the documentation review system. This provides native version control, branch-based workflows, and integration with existing Git platforms (GitHub, GitLab, etc.).

## Core Concept

```
Git Repository (docs/)
      ↓
Clone/Read via Git API
      ↓
Display in Web Interface
      ↓
User Interactions (comments, suggestions)
      ↓
Stored in D1 Database (metadata)
      ↓
Optional: Create PR with suggestions
```

## Architecture Options

### Option 1: Direct Git Repository Access (Recommended)

**How it works:**
- Application clones/accesses the Git repository directly
- Reads files from specific commits/branches
- Uses Git commands to get history, diffs, blame
- Stores comments/suggestions in D1 (not in Git)
- Approved suggestions can create PRs automatically

**Advantages:**
- ✅ Direct access to Git history
- ✅ No external API dependencies
- ✅ Fast local file access
- ✅ Can work offline (after clone)
- ✅ Full Git features available

**Disadvantages:**
- ❌ Requires file system access (not ideal for Cloudflare Workers)
- ❌ Need to keep repository updated
- ❌ Clone takes storage space

### Option 2: Git Platform API Integration (Best for Cloudflare)

**How it works:**
- Use GitHub/GitLab API to fetch files
- Read from main branch or specific commits
- Get file history via API
- Store comments in D1
- Create PRs via API when suggestions approved

**Advantages:**
- ✅ **Perfect for Cloudflare Workers** (no file system)
- ✅ Always up-to-date (real-time API)
- ✅ No storage needed
- ✅ Native PR/Issue integration
- ✅ Works with private repos
- ✅ User authentication via OAuth

**Disadvantages:**
- ❌ API rate limits (mitigated with caching)
- ❌ Requires network calls
- ❌ Depends on platform availability

### Option 3: Hybrid Approach

**How it works:**
- Cloudflare Worker uses GitHub API
- Cache files in R2/KV for performance
- Webhook updates when docs change
- Store metadata in D1

**Advantages:**
- ✅ Best of both worlds
- ✅ Fast (cached)
- ✅ Always in sync (webhooks)
- ✅ Resilient (cache fallback)

## Recommended: GitHub API Integration

Since you're using Cloudflare Workers, **Option 2 (GitHub API)** is the best choice.

---

## Implementation Design

### 1. GitHub Integration Layer

**File: `app/lib/git/github.server.ts`**

```typescript
interface GitHubConfig {
  owner: string;      // "meywd"
  repo: string;       // "tamma"
  branch: string;     // "main"
  token: string;      // GitHub PAT or OAuth token
}

export class GitHubClient {
  constructor(private config: GitHubConfig) {}

  /**
   * Get file content from repository
   */
  async getFile(path: string, ref?: string): Promise<GitFile> {
    const url = `https://api.github.com/repos/${this.config.owner}/${this.config.repo}/contents/${path}`;
    const params = ref ? `?ref=${ref}` : `?ref=${this.config.branch}`;

    const response = await fetch(url + params, {
      headers: {
        Authorization: `token ${this.config.token}`,
        Accept: "application/vnd.github.v3+json",
      },
    });

    const data = await response.json();

    return {
      path: data.path,
      content: atob(data.content), // Base64 decode
      sha: data.sha,
      size: data.size,
      url: data.html_url,
    };
  }

  /**
   * List all markdown files in docs directory
   */
  async listDocs(path = "docs"): Promise<GitTreeItem[]> {
    const url = `https://api.github.com/repos/${this.config.owner}/${this.config.repo}/git/trees/${this.config.branch}?recursive=1`;

    const response = await fetch(url, {
      headers: {
        Authorization: `token ${this.config.token}`,
        Accept: "application/vnd.github.v3+json",
      },
    });

    const data = await response.json();

    return data.tree
      .filter((item: any) =>
        item.path.startsWith(path) &&
        item.path.endsWith(".md") &&
        item.type === "blob"
      )
      .map((item: any) => ({
        path: item.path,
        sha: item.sha,
        size: item.size,
      }));
  }

  /**
   * Get file history (commits)
   */
  async getFileHistory(path: string, limit = 50): Promise<GitCommit[]> {
    const url = `https://api.github.com/repos/${this.config.owner}/${this.config.repo}/commits`;
    const params = `?path=${path}&per_page=${limit}`;

    const response = await fetch(url + params, {
      headers: {
        Authorization: `token ${this.config.token}`,
        Accept: "application/vnd.github.v3+json",
      },
    });

    const data = await response.json();

    return data.map((commit: any) => ({
      sha: commit.sha,
      message: commit.commit.message,
      author: {
        name: commit.commit.author.name,
        email: commit.commit.author.email,
        date: commit.commit.author.date,
      },
      url: commit.html_url,
    }));
  }

  /**
   * Get diff between two commits
   */
  async getFileDiff(path: string, baseRef: string, headRef: string): Promise<string> {
    const url = `https://api.github.com/repos/${this.config.owner}/${this.config.repo}/compare/${baseRef}...${headRef}`;

    const response = await fetch(url, {
      headers: {
        Authorization: `token ${this.config.token}`,
        Accept: "application/vnd.github.v3.diff",
      },
    });

    return await response.text();
  }

  /**
   * Get git blame for file (who wrote each line)
   */
  async getBlame(path: string, ref?: string): Promise<GitBlameLine[]> {
    // Use GitHub GraphQL API for blame
    const query = `
      query($owner: String!, $repo: String!, $path: String!) {
        repository(owner: $owner, name: $repo) {
          object(expression: "${ref || this.config.branch}:${path}") {
            ... on Blob {
              blame {
                ranges {
                  commit {
                    oid
                    message
                    author {
                      name
                      email
                      date
                    }
                  }
                  startingLine
                  endingLine
                  age
                }
              }
            }
          }
        }
      }
    `;

    const response = await fetch("https://api.github.com/graphql", {
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
        },
      }),
    });

    const data = await response.json();
    return data.data.repository.object.blame.ranges;
  }

  /**
   * Create a pull request with suggested changes
   */
  async createPRFromSuggestion(suggestion: {
    title: string;
    description: string;
    filePath: string;
    baseRef: string;
    changes: string; // New content
  }): Promise<{ prNumber: number; url: string }> {
    // 1. Create a new branch
    const branchName = `doc-suggestion-${Date.now()}`;
    const baseSha = await this.getRefSha(suggestion.baseRef);

    await this.createRef(`refs/heads/${branchName}`, baseSha);

    // 2. Update file on new branch
    const currentFile = await this.getFile(suggestion.filePath, suggestion.baseRef);

    await this.updateFile({
      path: suggestion.filePath,
      message: suggestion.title,
      content: suggestion.changes,
      branch: branchName,
      sha: currentFile.sha,
    });

    // 3. Create pull request
    const prUrl = `https://api.github.com/repos/${this.config.owner}/${this.config.repo}/pulls`;

    const response = await fetch(prUrl, {
      method: "POST",
      headers: {
        Authorization: `token ${this.config.token}`,
        Accept: "application/vnd.github.v3+json",
        "Content-Type": "application/json",
      },
      body: JSON.stringify({
        title: suggestion.title,
        body: suggestion.description,
        head: branchName,
        base: suggestion.baseRef,
      }),
    });

    const pr = await response.json();

    return {
      prNumber: pr.number,
      url: pr.html_url,
    };
  }

  private async getRefSha(ref: string): Promise<string> {
    const url = `https://api.github.com/repos/${this.config.owner}/${this.config.repo}/git/ref/heads/${ref}`;
    const response = await fetch(url, {
      headers: { Authorization: `token ${this.config.token}` },
    });
    const data = await response.json();
    return data.object.sha;
  }

  private async createRef(ref: string, sha: string): Promise<void> {
    const url = `https://api.github.com/repos/${this.config.owner}/${this.config.repo}/git/refs`;
    await fetch(url, {
      method: "POST",
      headers: {
        Authorization: `token ${this.config.token}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify({ ref, sha }),
    });
  }

  private async updateFile(params: {
    path: string;
    message: string;
    content: string;
    branch: string;
    sha: string;
  }): Promise<void> {
    const url = `https://api.github.com/repos/${this.config.owner}/${this.config.repo}/contents/${params.path}`;
    await fetch(url, {
      method: "PUT",
      headers: {
        Authorization: `token ${this.config.token}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify({
        message: params.message,
        content: btoa(params.content), // Base64 encode
        branch: params.branch,
        sha: params.sha,
      }),
    });
  }
}
```

### 2. Caching Layer (R2/KV)

**File: `app/lib/git/cache.server.ts`**

```typescript
export class GitCache {
  constructor(
    private kv: KVNamespace,
    private ttl = 3600 // 1 hour
  ) {}

  async getFile(path: string, sha: string): Promise<string | null> {
    const key = `file:${path}:${sha}`;
    return await this.kv.get(key);
  }

  async setFile(path: string, sha: string, content: string): Promise<void> {
    const key = `file:${path}:${sha}`;
    await this.kv.put(key, content, { expirationTtl: this.ttl });
  }

  async getFileList(): Promise<GitTreeItem[] | null> {
    const cached = await this.kv.get("file-list", "json");
    return cached as GitTreeItem[] | null;
  }

  async setFileList(items: GitTreeItem[]): Promise<void> {
    await this.kv.put("file-list", JSON.stringify(items), {
      expirationTtl: 600, // 10 minutes
    });
  }
}
```

### 3. Document Loader with Git Integration

**File: `app/lib/docs/loader.server.ts`**

```typescript
import { GitHubClient } from "../git/github.server";
import { GitCache } from "../git/cache.server";
import matter from "gray-matter";

export class DocumentLoader {
  constructor(
    private github: GitHubClient,
    private cache: GitCache
  ) {}

  async loadDocument(path: string, ref?: string): Promise<Document> {
    // Try cache first
    const gitFile = await this.github.getFile(path, ref);

    let content = await this.cache.getFile(path, gitFile.sha);

    if (!content) {
      content = gitFile.content;
      await this.cache.setFile(path, gitFile.sha, content);
    }

    const { data, content: markdown } = matter(content);

    return {
      path,
      sha: gitFile.sha,
      title: data.title || this.extractTitle(markdown),
      content: markdown,
      lines: markdown.split("\n"),
      metadata: data,
      gitUrl: gitFile.url,
    };
  }

  async loadDocumentHistory(path: string): Promise<DocumentVersion[]> {
    const commits = await this.github.getFileHistory(path);

    return commits.map((commit) => ({
      sha: commit.sha,
      message: commit.message,
      author: commit.author,
      date: new Date(commit.author.date),
      url: commit.url,
    }));
  }

  async loadDocumentAtVersion(path: string, sha: string): Promise<Document> {
    return this.loadDocument(path, sha);
  }

  private extractTitle(content: string): string {
    const match = content.match(/^#\s+(.+)$/m);
    return match ? match[1] : "Untitled";
  }
}
```

### 4. Webhook Handler for Updates

**File: `app/routes/api.webhooks.github.ts`**

```typescript
import type { Route } from "./+types/api.webhooks.github";
import crypto from "crypto";

export async function action({ request, context }: Route.ActionArgs) {
  // Verify webhook signature
  const signature = request.headers.get("X-Hub-Signature-256");
  const payload = await request.text();

  if (!verifySignature(payload, signature, context.cloudflare.env.GITHUB_WEBHOOK_SECRET)) {
    return Response.json({ error: "Invalid signature" }, { status: 401 });
  }

  const event = JSON.parse(payload);

  // Handle push event
  if (event.ref === "refs/heads/main") {
    // Clear cache for updated files
    const modifiedFiles = event.commits.flatMap((c: any) => [
      ...c.added,
      ...c.modified,
    ]);

    const cache = new GitCache(context.cloudflare.env.CACHE);

    for (const file of modifiedFiles) {
      if (file.startsWith("docs/") && file.endsWith(".md")) {
        // Clear cache - will be re-fetched on next request
        await context.cloudflare.env.CACHE.delete(`file:${file}:*`);
      }
    }

    // Also clear file list cache
    await context.cloudflare.env.CACHE.delete("file-list");
  }

  return Response.json({ received: true });
}

function verifySignature(payload: string, signature: string | null, secret: string): boolean {
  if (!signature) return false;

  const hmac = crypto.createHmac("sha256", secret);
  const digest = `sha256=${hmac.update(payload).digest("hex")}`;

  return crypto.timingSafeEqual(Buffer.from(signature), Buffer.from(digest));
}
```

### 5. Enhanced Features with Git

#### Line-by-Line Blame (Who Wrote What)

```typescript
async function getLineAuthors(path: string): Promise<Map<number, Author>> {
  const blame = await github.getBlame(path);

  const lineAuthors = new Map<number, Author>();

  for (const range of blame) {
    for (let line = range.startingLine; line <= range.endingLine; line++) {
      lineAuthors.set(line, {
        name: range.commit.author.name,
        email: range.commit.author.email,
        date: range.commit.author.date,
        commitSha: range.commit.oid,
      });
    }
  }

  return lineAuthors;
}
```

#### Time-Travel View

```typescript
// View document as it was at specific date
async function getDocumentAtDate(path: string, date: Date): Promise<Document> {
  const history = await github.getFileHistory(path);

  const commit = history.find((c) => new Date(c.author.date) <= date);

  if (!commit) {
    throw new Error("No version found for that date");
  }

  return loader.loadDocumentAtVersion(path, commit.sha);
}
```

#### Visual Diff Between Versions

```typescript
// Compare two versions
async function compareVersions(
  path: string,
  oldSha: string,
  newSha: string
): Promise<DiffResult> {
  const diff = await github.getFileDiff(path, oldSha, newSha);

  return parseDiff(diff);
}
```

---

## New Workflow with Git Integration

### User Suggests Edit

1. **User selects text** in document
2. **Makes changes** in suggestion editor
3. **Submits suggestion** → Stored in D1 with status "pending"
4. **Reviewer approves** → Status changes to "approved"
5. **System creates PR** automatically via GitHub API
6. **Maintainer merges PR** → Changes go live
7. **Webhook updates cache** → App shows latest version

### Comment on Line with Git Blame

When viewing a line:
```
Line 42: const DB_CONFIG = { host: 'localhost' };
         ↓
[Comment] 💬 "Should this be configurable?"
         ↓
[Blame] 👤 Written by: John Doe (john@example.com)
         📅 Last modified: 2024-10-15
         🔗 Commit: abc123 "Update database config"
```

---

## Benefits of Git-Based Approach

### 1. **Native Version Control**
- See who changed what and when
- Rollback to any previous version
- Track document evolution over time

### 2. **Standard Workflows**
- Use existing PR review process
- Integrate with GitHub/GitLab issues
- Leverage existing permissions

### 3. **Audit Trail**
- Every change tracked in Git
- Comments stored separately in D1
- Full history always available

### 4. **Collaboration**
- Multiple users can suggest changes
- Changes reviewed before merging
- Conflicts handled by Git

### 5. **Integration**
- Works with existing Git tools
- CI/CD can validate changes
- Webhooks keep system in sync

---

## Configuration

**Environment Variables:**

```jsonc
// wrangler.jsonc
{
  "vars": {
    "GITHUB_OWNER": "meywd",
    "GITHUB_REPO": "tamma",
    "GITHUB_BRANCH": "main",
    "DOCS_PATH": "docs"
  }
}
```

**Secrets (use `wrangler secret put`):**
```bash
pnpm wrangler secret put GITHUB_TOKEN
pnpm wrangler secret put GITHUB_WEBHOOK_SECRET
```

---

## API Rate Limits & Mitigation

GitHub API limits:
- **Authenticated**: 5,000 requests/hour
- **Unauthenticated**: 60 requests/hour

**Mitigation strategies:**
1. **Aggressive caching** (KV) - Cache files for 1 hour
2. **Conditional requests** - Use ETags
3. **Batch operations** - Fetch multiple files at once
4. **GraphQL API** - More efficient for complex queries
5. **Webhook updates** - Don't poll, react to changes

---

## Implementation Priority

### Phase 1: Basic Git Integration
✅ GitHub client for reading files
✅ Caching layer (KV)
✅ Document loader with Git SHA tracking

### Phase 2: Enhanced Features
✅ File history viewing
✅ Git blame (line authors)
✅ Diff view between versions

### Phase 3: Write Operations
✅ Create PRs from suggestions
✅ Webhook handler for updates
✅ Auto-sync on merge

---

## Conclusion

Using **Git as the foundation** gives you:
- 📜 **Full version history** natively
- 🔄 **Standard PR workflow** for approvals
- 👥 **Git blame** for line-level attribution
- 🔀 **Branch-based reviews** (optional)
- ⏮️ **Time-travel** viewing
- 🎯 **Single source of truth** (Git repo)

This is **production-ready, scalable, and leverages existing Git infrastructure** you already have!

**Next Steps**: Implement `GitHubClient` class and integrate with document routes.
