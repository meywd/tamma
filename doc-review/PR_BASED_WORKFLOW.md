# Pull Request-Based Workflow

## Overview

All documentation changes go through **Pull Requests** (or Merge Requests on GitLab). This provides:

- ✅ **Standard Review Process** - Use existing Git workflows
- ✅ **Approval Gates** - Changes reviewed before merging
- ✅ **Audit Trail** - Every change tracked in Git
- ✅ **Rollback** - Easy to revert if needed
- ✅ **CI/CD Integration** - Auto-validate changes
- ✅ **Notifications** - Team gets notified via Git platform

## The Workflow

```
┌─────────────────────────────────────────────────────────┐
│                   User Makes Change                     │
├─────────────────────────────────────────────────────────┤
│                                                         │
│  1. User suggests edit in doc-review app               │
│  2. App creates branch: "suggestion-{id}"              │
│  3. App commits change to branch                       │
│  4. App creates PR automatically                       │
│  5. Reviewers notified by Git platform                 │
│  6. Discussion happens on PR                           │
│  7. Reviewer approves PR                               │
│  8. PR merged to main                                  │
│  9. Webhook notifies doc-review app                    │
│  10. App updates cache, change goes live!              │
│                                                         │
└─────────────────────────────────────────────────────────┘
```

---

## Implementation

### 1. Suggestion Creation

**File: `app/routes/api.suggestions.create.tsx`**

```typescript
import type { Route } from "./+types/api.suggestions.create";
import { requireAuth } from "~/lib/auth/session.server";
import { DocumentLoader } from "~/lib/docs/loader.server";
import { getDB } from "~/lib/db/client.server";
import { suggestions } from "~/lib/db/schema";

export async function action({ request, context }: Route.ActionArgs) {
  const user = await requireAuth(request, context);
  const data = await request.json();

  // Validate suggestion data
  const { docPath, lineStart, lineEnd, originalText, suggestedText, description } = data;

  // 1. Store suggestion in D1 (pending state)
  const db = getDB(context.cloudflare.env);

  const suggestion = await db.insert(suggestions).values({
    id: crypto.randomUUID(),
    docPath,
    lineStart,
    lineEnd,
    originalText,
    suggestedText,
    description,
    status: "pending",
    userId: user.id,
    createdAt: Date.now(),
    updatedAt: Date.now(),
  }).returning().get();

  // 2. Create PR automatically
  const loader = DocumentLoader.forUser(user, context.cloudflare.env);
  const pr = await loader.createSuggestionPR({
    suggestionId: suggestion.id,
    docPath,
    suggestedText,
    description,
    user,
  });

  // 3. Update suggestion with PR info
  await db.update(suggestions)
    .set({
      prNumber: pr.number,
      prUrl: pr.url,
      status: "pr_created",
    })
    .where(eq(suggestions.id, suggestion.id));

  return Response.json({
    success: true,
    suggestion: {
      ...suggestion,
      prNumber: pr.number,
      prUrl: pr.url,
    },
  });
}
```

### 2. Creating the PR

**File: `app/lib/docs/loader.server.ts`**

```typescript
export class DocumentLoader {
  // ... existing code ...

  async createSuggestionPR(params: {
    suggestionId: string;
    docPath: string;
    suggestedText: string;
    description: string;
    user: OAuthUser;
  }): Promise<GitPullRequest> {
    const { suggestionId, docPath, suggestedText, description, user } = params;

    // 1. Create unique branch name
    const branchName = `doc-suggestion-${suggestionId}`;
    const baseBranch = this.env.GIT_DEFAULT_BRANCH || "main";

    // 2. Get current file
    const currentFile = await this.git.getFile(docPath, baseBranch);

    // 3. Create new branch from base
    await this.git.createBranch(branchName, baseBranch);

    // 4. Update file on new branch
    await this.git.updateFile({
      path: docPath,
      branch: branchName,
      content: suggestedText,
      message: `docs: ${description}\n\nSuggested by @${user.username} via doc-review`,
      sha: currentFile.sha,
    });

    // 5. Create pull request
    const pr = await this.git.createPullRequest({
      title: `📝 Suggestion: ${description}`,
      description: this.formatPRDescription({
        description,
        docPath,
        suggestionId,
        user,
      }),
      sourceBranch: branchName,
      targetBranch: baseBranch,
      draft: false,
    });

    return pr;
  }

  private formatPRDescription(params: {
    description: string;
    docPath: string;
    suggestionId: string;
    user: OAuthUser;
  }): string {
    return `
## Documentation Suggestion

**Document:** \`${params.docPath}\`
**Suggested by:** @${params.user.username}
**Suggestion ID:** ${params.suggestionId}

### Description

${params.description}

---

*This PR was automatically created by the [Tamma Doc Review](https://doc-review.yourcompany.com) system.*
    `.trim();
  }
}
```

### 3. PR Review & Approval

The review happens **natively on GitHub/GitLab**:

```
GitHub PR Interface:
┌────────────────────────────────────────────────────┐
│ 📝 Suggestion: Update database configuration       │
├────────────────────────────────────────────────────┤
│                                                    │
│ @john-doe wants to merge doc-suggestion-abc123    │
│ into main                                          │
│                                                    │
│ Files changed: docs/architecture.md (+3, -1)      │
│                                                    │
│ ┌─────────────────────────────────────────────┐  │
│ │ - const DB_HOST = 'localhost';              │  │
│ │ + const DB_HOST = process.env.DB_HOST;      │  │
│ └─────────────────────────────────────────────┘  │
│                                                    │
│ Reviewers: @jane-smith (approved ✅)              │
│                                                    │
│ Comments: 2                                        │
│ ├─ @jane-smith: Good catch! LGTM ✅               │
│ └─ @john-doe: Thanks for the review!              │
│                                                    │
│ [Merge pull request] [Close]                      │
└────────────────────────────────────────────────────┘
```

### 4. Webhook Handler (PR Merged)

**File: `app/routes/api.webhooks.git.tsx`**

```typescript
import type { Route } from "./+types/api.webhooks.git";
import { createOAuthService } from "~/lib/auth/oauth.server";
import { getDB } from "~/lib/db/client.server";
import { suggestions } from "~/lib/db/schema";
import { eq } from "drizzle-orm";

export async function action({ request, context }: Route.ActionArgs) {
  const provider = context.cloudflare.env.GIT_PROVIDER;
  const oauth = createOAuthService(provider, context.cloudflare.env);

  // 1. Validate webhook signature
  const signature = request.headers.get(getSignatureHeader(provider));
  const payload = await request.text();

  if (!oauth.validateWebhook(payload, signature!, context.cloudflare.env.GIT_WEBHOOK_SECRET)) {
    return Response.json({ error: "Invalid signature" }, { status: 401 });
  }

  // 2. Parse webhook event
  const eventType = request.headers.get(getEventTypeHeader(provider));
  const event = oauth.parseWebhookEvent(JSON.parse(payload), eventType!);

  // 3. Handle PR events
  if (event.type === "pull_request" && event.pullRequest) {
    await handlePREvent(event.pullRequest, context);
  }

  // 4. Handle push events (merged PRs)
  if (event.type === "push") {
    await handlePushEvent(event, context);
  }

  return Response.json({ received: true });
}

async function handlePREvent(pr: GitPullRequest, context: AppLoadContext) {
  const db = getDB(context.cloudflare.env);

  // Extract suggestion ID from branch name
  const match = pr.sourceBranch.match(/doc-suggestion-(.+)/);
  if (!match) return;

  const suggestionId = match[1];

  if (pr.state === "merged") {
    // PR was merged! Update suggestion status
    await db.update(suggestions)
      .set({
        status: "approved",
        reviewedAt: Date.now(),
        updatedAt: Date.now(),
      })
      .where(eq(suggestions.id, suggestionId));

    console.log(`Suggestion ${suggestionId} was approved and merged!`);
  } else if (pr.state === "closed") {
    // PR was closed without merging
    await db.update(suggestions)
      .set({
        status: "rejected",
        reviewedAt: Date.now(),
        updatedAt: Date.now(),
      })
      .where(eq(suggestions.id, suggestionId));

    console.log(`Suggestion ${suggestionId} was rejected.`);
  }
}

async function handlePushEvent(event: GitWebhookEvent, context: AppLoadContext) {
  // Clear cache for updated files
  const cache = context.cloudflare.env.CACHE;

  if (event.commits) {
    for (const commit of event.commits) {
      // Clear cache for modified files
      const modifiedFiles = [
        ...commit.added || [],
        ...commit.modified || [],
      ];

      for (const file of modifiedFiles) {
        if (file.startsWith("docs/") && file.endsWith(".md")) {
          await cache.delete(`file:${file}:*`);
          console.log(`Cache cleared for ${file}`);
        }
      }
    }
  }

  // Clear file list cache
  await cache.delete("file-list");
}

function getSignatureHeader(provider: string): string {
  if (provider === "github") return "X-Hub-Signature-256";
  if (provider === "gitlab") return "X-Gitlab-Token";
  if (provider === "gitea") return "X-Gitea-Signature";
  return "X-Webhook-Signature";
}

function getEventTypeHeader(provider: string): string {
  if (provider === "github") return "X-GitHub-Event";
  if (provider === "gitlab") return "X-Gitlab-Event";
  if (provider === "gitea") return "X-Gitea-Event";
  return "X-Event-Type";
}
```

### 5. Real-Time Updates in UI

**File: `app/routes/_authenticated.suggestions.$id.tsx`**

```typescript
import { useLoaderData } from "react-router";
import type { Route } from "./+types/_authenticated.suggestions.$id";
import { requireAuth } from "~/lib/auth/session.server";
import { getDB } from "~/lib/db/client.server";
import { suggestions } from "~/lib/db/schema";
import { eq } from "drizzle-orm";

export async function loader({ params, request, context }: Route.LoaderArgs) {
  const user = await requireAuth(request, context);
  const db = getDB(context.cloudflare.env);

  const suggestion = await db
    .select()
    .from(suggestions)
    .where(eq(suggestions.id, params.id))
    .get();

  if (!suggestion) {
    throw new Response("Not found", { status: 404 });
  }

  return { user, suggestion };
}

export default function SuggestionPage() {
  const { suggestion } = useLoaderData<typeof loader>();

  return (
    <div className="max-w-4xl mx-auto p-8">
      <h1 className="text-2xl font-bold mb-4">Documentation Suggestion</h1>

      <div className="bg-white shadow rounded-lg p-6 mb-6">
        <div className="flex items-center justify-between mb-4">
          <h2 className="text-xl">{suggestion.description}</h2>
          <StatusBadge status={suggestion.status} />
        </div>

        <div className="mb-4">
          <label className="text-sm text-gray-600">Document:</label>
          <p className="font-mono">{suggestion.docPath}</p>
        </div>

        <div className="mb-4">
          <label className="text-sm text-gray-600">Lines {suggestion.lineStart}-{suggestion.lineEnd}</label>
        </div>

        {/* Diff View */}
        <div className="grid grid-cols-2 gap-4 mb-4">
          <div>
            <h3 className="font-bold text-sm mb-2">Original</h3>
            <pre className="bg-red-50 p-4 rounded text-sm overflow-x-auto">
              <code>{suggestion.originalText}</code>
            </pre>
          </div>
          <div>
            <h3 className="font-bold text-sm mb-2">Suggested</h3>
            <pre className="bg-green-50 p-4 rounded text-sm overflow-x-auto">
              <code>{suggestion.suggestedText}</code>
            </pre>
          </div>
        </div>

        {/* PR Link */}
        {suggestion.prUrl && (
          <div className="mt-6 p-4 bg-blue-50 rounded">
            <p className="text-sm mb-2">
              This suggestion has been submitted as a Pull Request:
            </p>
            <a
              href={suggestion.prUrl}
              target="_blank"
              rel="noopener noreferrer"
              className="text-blue-600 hover:underline font-medium"
            >
              View PR #{suggestion.prNumber} →
            </a>
          </div>
        )}
      </div>
    </div>
  );
}

function StatusBadge({ status }: { status: string }) {
  const styles = {
    pending: "bg-yellow-100 text-yellow-800",
    pr_created: "bg-blue-100 text-blue-800",
    approved: "bg-green-100 text-green-800",
    rejected: "bg-red-100 text-red-800",
  };

  const labels = {
    pending: "⏳ Pending",
    pr_created: "🔄 Under Review",
    approved: "✅ Approved",
    rejected: "❌ Rejected",
  };

  return (
    <span className={`px-3 py-1 rounded-full text-sm font-medium ${styles[status as keyof typeof styles]}`}>
      {labels[status as keyof typeof labels]}
    </span>
  );
}
```

---

## User Experience Flow

### Making a Suggestion

```
User viewing document
     ↓
Selects text (lines 42-45)
     ↓
Clicks "Suggest Edit" button
     ↓
Modal opens with diff view:
  ┌────────────────────────────┐
  │ Suggest Edit               │
  ├────────────────────────────┤
  │ Original:                  │
  │ const DB = 'localhost';    │
  │                            │
  │ Your suggestion:           │
  │ const DB = env.DB_HOST;    │
  │                            │
  │ Description:               │
  │ Make database configurable │
  │                            │
  │ [Cancel] [Submit PR]       │
  └────────────────────────────┘
     ↓
User clicks "Submit PR"
     ↓
System creates PR automatically
     ↓
User sees success message:
  "PR #123 created! View on GitHub →"
     ↓
Reviewers get notified by GitHub
     ↓
Discussion happens on PR
     ↓
Reviewer merges PR
     ↓
Webhook updates app
     ↓
Change goes live!
```

### Tracking Suggestions

**File: `app/routes/_authenticated.my-suggestions.tsx`**

```typescript
export async function loader({ request, context }: Route.LoaderArgs) {
  const user = await requireAuth(request, context);
  const db = getDB(context.cloudflare.env);

  const mySuggestions = await db
    .select()
    .from(suggestions)
    .where(eq(suggestions.userId, user.id))
    .orderBy(desc(suggestions.createdAt))
    .all();

  return { user, suggestions: mySuggestions };
}

export default function MySuggestionsPage() {
  const { suggestions } = useLoaderData<typeof loader>();

  return (
    <div className="max-w-6xl mx-auto p-8">
      <h1 className="text-2xl font-bold mb-6">My Suggestions</h1>

      <div className="space-y-4">
        {suggestions.map((suggestion) => (
          <div key={suggestion.id} className="bg-white shadow rounded-lg p-4">
            <div className="flex items-start justify-between">
              <div className="flex-1">
                <h3 className="font-medium">{suggestion.description}</h3>
                <p className="text-sm text-gray-600 mt-1">
                  {suggestion.docPath} (lines {suggestion.lineStart}-{suggestion.lineEnd})
                </p>
                <p className="text-xs text-gray-400 mt-2">
                  {new Date(suggestion.createdAt).toLocaleDateString()}
                </p>
              </div>
              <div className="ml-4">
                <StatusBadge status={suggestion.status} />
                {suggestion.prUrl && (
                  <a
                    href={suggestion.prUrl}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="text-xs text-blue-600 hover:underline mt-2 block"
                  >
                    View PR →
                  </a>
                )}
              </div>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}
```

---

## Benefits of PR-Based Workflow

### 1. **Standard Process**
- Use existing Git review workflow
- Team already knows how it works
- No learning curve

### 2. **Native Tools**
- GitHub/GitLab UI for review
- Inline comments on diffs
- Request changes
- Approve/merge

### 3. **Notifications**
- Email notifications via Git platform
- Slack/Teams integrations
- Mobile app notifications

### 4. **CI/CD Integration**
```yaml
# .github/workflows/docs-pr.yml
name: Validate Docs PR

on:
  pull_request:
    paths:
      - 'docs/**/*.md'

jobs:
  validate:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - name: Check markdown links
        run: npm run check-links
      - name: Spell check
        run: npm run spellcheck
      - name: Validate frontmatter
        run: npm run validate-frontmatter
```

### 5. **Audit Trail**
- Every change in Git history
- Who approved what
- When changes were made
- Why (commit messages)

### 6. **Rollback**
```bash
# Easy to revert if needed
git revert <commit-sha>
```

---

## Advanced Features

### Draft PRs

```typescript
// Create as draft for work-in-progress
const pr = await this.git.createPullRequest({
  title: "WIP: Update architecture docs",
  description: "Still working on this...",
  sourceBranch: branchName,
  targetBranch: "main",
  draft: true, // 👈 Draft PR
});
```

### Auto-merge Small Changes

```typescript
// For trusted users or typo fixes
if (isTrustedUser(user) && isMinorChange(suggestion)) {
  await git.mergePullRequest(pr.number, {
    method: "squash",
    message: "Auto-merged: Minor documentation fix",
  });
}
```

### Required Approvals

```yaml
# .github/CODEOWNERS
docs/**/*.md  @tech-writers @engineering-leads
```

### PR Templates

```markdown
<!-- .github/pull_request_template.md -->

## Documentation Change

**Type:** (Bug fix / Enhancement / New content)

**Description:**
<!-- What changed and why -->

**Checklist:**
- [ ] Links are valid
- [ ] Spelling checked
- [ ] Code examples tested
- [ ] Screenshots updated (if applicable)
```

---

## Comment System Integration

Comments in the app are **separate** from PR reviews:

```
PR Comments (GitHub/GitLab):
  - Review-related discussion
  - Approve/request changes
  - Code review feedback

App Comments (D1 Database):
  - Line-by-line discussions
  - General questions
  - Clarifications
  - Not tied to specific change proposals
```

Both systems coexist:
- **PR comments** → For reviewing changes
- **App comments** → For discussing current docs

---

## Conclusion

The **PR-based workflow** provides:

✅ **Standard Git process** everyone knows
✅ **Native review tools** (GitHub/GitLab UI)
✅ **Automatic notifications** via Git platform
✅ **CI/CD integration** for validation
✅ **Complete audit trail** in Git history
✅ **Easy rollback** with Git tools
✅ **No custom approval system** needed

**Every change goes through a PR** = Quality, accountability, and traceability! 🎯
