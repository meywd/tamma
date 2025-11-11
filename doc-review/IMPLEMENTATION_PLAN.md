# Implementation Plan: Documentation Review Site

## Project Goal

Build a collaborative platform where users can:
- **View** documentation in a clean, organized hierarchy
- **Comment** on specific lines of documentation
- **Suggest edits** with visual diff comparisons
- **Discuss** documentation at the document level
- **Navigate** through Main Docs → Epics → Stories → Tasks

## Phase-by-Phase Implementation

---

## Phase 1: Foundation & Authentication (Week 1-2)

### Goal
Set up Cloudflare infrastructure, database, and authentication flow.

### Tasks

#### 1.1 Cloudflare Setup (Day 1)
```bash
# Create D1 database
pnpm wrangler d1 create tamma-docs

# Copy the database_id and update wrangler.toml
# [[d1_databases]]
# database_id = "YOUR_ID_HERE"

# Create KV namespace
pnpm wrangler kv namespace create CACHE

# Copy the id and update wrangler.toml
# [[kv_namespaces]]
# id = "YOUR_KV_ID_HERE"

# Initialize database
pnpm wrangler d1 execute tamma-docs --local --file=./db/schema.sql
pnpm wrangler d1 execute tamma-docs --file=./db/schema.sql
```

#### 1.2 Database Schema & ORM (Day 2)

**File: `app/lib/db/schema.ts`**
```typescript
import { sqliteTable, text, integer } from "drizzle-orm/sqlite-core";

export const users = sqliteTable("users", {
  id: text("id").primaryKey(),
  email: text("email").notNull().unique(),
  name: text("name").notNull(),
  avatarUrl: text("avatar_url"),
  role: text("role").notNull().default("viewer"),
  createdAt: integer("created_at").notNull(),
  updatedAt: integer("updated_at").notNull(),
});

export const comments = sqliteTable("comments", {
  id: text("id").primaryKey(),
  docPath: text("doc_path").notNull(),
  lineNumber: integer("line_number"),
  lineContent: text("line_content"),
  content: text("content").notNull(),
  userId: text("user_id").notNull().references(() => users.id),
  parentId: text("parent_id").references(() => comments.id),
  resolved: integer("resolved", { mode: "boolean" }).notNull().default(false),
  createdAt: integer("created_at").notNull(),
  updatedAt: integer("updated_at").notNull(),
});

// Add other tables: suggestions, discussions, discussion_messages, document_metadata
```

**File: `app/lib/db/client.server.ts`**
```typescript
import { drizzle } from "drizzle-orm/d1";
import * as schema from "./schema";

export function getDB(env: { DB: D1Database }) {
  return drizzle(env.DB, { schema });
}
```

#### 1.3 Authentication (Day 3-4)

**Option A: Cloudflare Access (Recommended)**

1. Set up Cloudflare Access:
   - Go to Cloudflare Dashboard → Zero Trust → Access
   - Create Application → Self-hosted
   - Add OAuth providers (GitHub, Google)
   - Set access policy (email domain or specific users)
   - Copy Application Audience (AUD) tag

2. Set secret:
```bash
pnpm wrangler secret put CF_ACCESS_AUD
# Paste your AUD value
```

**File: `app/lib/auth/auth.server.ts`**
```typescript
import type { AppLoadContext } from "react-router";
import { getDB } from "../db/client.server";
import { users } from "../db/schema";
import { eq } from "drizzle-orm";

interface CloudflareAccessJWT {
  email: string;
  name: string;
  sub: string;
}

export async function requireAuth(request: Request, context: AppLoadContext) {
  const token = request.headers.get("Cf-Access-Jwt-Assertion");

  if (!token) {
    throw new Response("Unauthorized", { status: 401 });
  }

  // TODO: Verify JWT signature with Cloudflare's public keys
  const payload: CloudflareAccessJWT = parseJWT(token);

  const db = getDB(context.cloudflare.env);

  let user = await db.select().from(users).where(eq(users.email, payload.email)).get();

  if (!user) {
    user = await db.insert(users).values({
      id: crypto.randomUUID(),
      email: payload.email,
      name: payload.name,
      avatarUrl: null,
      role: "reviewer",
      createdAt: Date.now(),
      updatedAt: Date.now(),
    }).returning().get();
  }

  return user;
}

function parseJWT(token: string): CloudflareAccessJWT {
  const parts = token.split(".");
  const payload = JSON.parse(atob(parts[1]));
  return payload;
}
```

#### 1.4 Base Layouts (Day 5)

**File: `app/routes/_authenticated.tsx`**
```typescript
import { Outlet, useLoaderData } from "react-router";
import type { Route } from "./+types/_authenticated";
import { requireAuth } from "~/lib/auth/auth.server";

export async function loader({ request, context }: Route.LoaderArgs) {
  const user = await requireAuth(request, context);
  return { user };
}

export default function AuthenticatedLayout() {
  const { user } = useLoaderData<typeof loader>();

  return (
    <div className="min-h-screen flex">
      <aside className="w-64 bg-gray-900 text-white p-4">
        <h1 className="text-xl font-bold mb-4">Tamma Docs</h1>
        <nav>
          {/* Navigation will go here */}
        </nav>
        <div className="mt-auto pt-4 border-t border-gray-700">
          <p className="text-sm">{user.name}</p>
          <p className="text-xs text-gray-400">{user.email}</p>
        </div>
      </aside>
      <main className="flex-1">
        <Outlet />
      </main>
    </div>
  );
}
```

---

## Phase 2: Document Viewing (Week 3-4)

### Goal
Load and display markdown documentation with line numbers.

### Tasks

#### 2.1 Document Loading (Day 1-2)

**File: `app/lib/markdown/loader.server.ts`**
```typescript
import { readFile, readdir } from "fs/promises";
import { join } from "path";
import matter from "gray-matter";

const DOCS_PATH = join(process.cwd(), "../docs");

export interface Document {
  path: string;
  title: string;
  content: string;
  lines: string[];
  metadata: Record<string, unknown>;
}

export async function loadDocument(docPath: string): Promise<Document> {
  const fullPath = join(DOCS_PATH, docPath);
  const raw = await readFile(fullPath, "utf-8");

  const { data, content } = matter(raw);

  const lines = content.split("\n");

  return {
    path: docPath,
    title: data.title || extractTitle(content),
    content,
    lines,
    metadata: data,
  };
}

function extractTitle(content: string): string {
  const match = content.match(/^#\s+(.+)$/m);
  return match ? match[1] : "Untitled";
}

export async function listDocuments(): Promise<string[]> {
  // Scan docs directory and return list of .md files
  const files = await readdir(DOCS_PATH, { recursive: true });
  return files.filter((f) => f.endsWith(".md"));
}
```

#### 2.2 Document Routes (Day 3-4)

**Update: `app/routes.ts`**
```typescript
import { type RouteConfig, index, route, layout } from "@react-router/dev/routes";

export default [
  index("routes/_index.tsx"),
  layout("routes/_authenticated.tsx", [
    route("docs", "routes/_authenticated.docs._index.tsx"),
    route("docs/prd", "routes/_authenticated.docs.prd.tsx"),
    route("docs/architecture", "routes/_authenticated.docs.architecture.tsx"),
    route("docs/epics/:epicId", "routes/_authenticated.docs.epics.$epicId.tsx"),
    route("docs/stories/:storyId", "routes/_authenticated.docs.stories.$storyId.tsx"),
  ]),
] satisfies RouteConfig;
```

**File: `app/routes/_authenticated.docs.prd.tsx`**
```typescript
import { useLoaderData } from "react-router";
import type { Route } from "./+types/_authenticated.docs.prd";
import { requireAuth } from "~/lib/auth/auth.server";
import { loadDocument } from "~/lib/markdown/loader.server";
import { MarkdownRenderer } from "~/components/markdown/MarkdownRenderer";

export async function loader({ request, context }: Route.LoaderArgs) {
  const user = await requireAuth(request, context);
  const document = await loadDocument("PRD.md");

  return { user, document };
}

export default function PRDPage() {
  const { document } = useLoaderData<typeof loader>();

  return (
    <div className="max-w-4xl mx-auto p-8">
      <MarkdownRenderer document={document} />
    </div>
  );
}
```

#### 2.3 Markdown Rendering Component (Day 5)

**File: `app/components/markdown/MarkdownRenderer.tsx`**
```typescript
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import rehypeHighlight from "rehype-highlight";
import type { Document } from "~/lib/markdown/loader.server";

interface Props {
  document: Document;
}

export function MarkdownRenderer({ document }: Props) {
  return (
    <div className="markdown-content">
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        rehypePlugins={[rehypeHighlight]}
      >
        {document.content}
      </ReactMarkdown>
    </div>
  );
}
```

**File: `app/styles/markdown.css`**
```css
.markdown-content {
  @apply prose prose-lg max-w-none;
}

.markdown-content h1 {
  @apply text-3xl font-bold mb-4;
}

.markdown-content h2 {
  @apply text-2xl font-semibold mb-3 mt-6;
}

.markdown-content code {
  @apply bg-gray-100 px-1 py-0.5 rounded text-sm;
}

.markdown-content pre {
  @apply bg-gray-900 text-white p-4 rounded-lg overflow-x-auto;
}
```

---

## Phase 3: Comments System (Week 5-6)

### Goal
Enable inline comments on specific lines.

### Tasks

#### 3.1 Comment API Routes (Day 1-2)

**File: `app/routes/api.comments.ts`**
```typescript
import type { Route } from "./+types/api.comments";
import { requireAuth } from "~/lib/auth/auth.server";
import { getDB } from "~/lib/db/client.server";
import { comments } from "~/lib/db/schema";
import { eq, and } from "drizzle-orm";

export async function loader({ request, context }: Route.LoaderArgs) {
  await requireAuth(request, context);

  const url = new URL(request.url);
  const docPath = url.searchParams.get("docPath");
  const lineNumber = url.searchParams.get("lineNumber");

  if (!docPath) {
    return Response.json({ error: "docPath required" }, { status: 400 });
  }

  const db = getDB(context.cloudflare.env);

  let query = db.select().from(comments).where(eq(comments.docPath, docPath));

  if (lineNumber) {
    query = query.where(eq(comments.lineNumber, parseInt(lineNumber)));
  }

  const results = await query.all();

  return Response.json({ comments: results });
}

export async function action({ request, context }: Route.ActionArgs) {
  const user = await requireAuth(request, context);
  const db = getDB(context.cloudflare.env);

  const data = await request.json();

  if (request.method === "POST") {
    const newComment = await db.insert(comments).values({
      id: crypto.randomUUID(),
      docPath: data.docPath,
      lineNumber: data.lineNumber || null,
      lineContent: data.lineContent || null,
      content: data.content,
      userId: user.id,
      parentId: data.parentId || null,
      resolved: false,
      createdAt: Date.now(),
      updatedAt: Date.now(),
    }).returning().get();

    return Response.json({ comment: newComment });
  }

  // Handle PATCH and DELETE similarly

  return Response.json({ error: "Method not allowed" }, { status: 405 });
}
```

#### 3.2 Comment UI Components (Day 3-4)

**File: `app/components/comments/CommentThread.tsx`**
```typescript
import { useState } from "react";
import { CommentForm } from "./CommentForm";

interface Comment {
  id: string;
  content: string;
  userId: string;
  userName: string;
  createdAt: number;
  resolved: boolean;
}

interface Props {
  comments: Comment[];
  docPath: string;
  lineNumber?: number;
  onNewComment: (comment: Comment) => void;
}

export function CommentThread({ comments, docPath, lineNumber, onNewComment }: Props) {
  const [showForm, setShowForm] = useState(false);

  return (
    <div className="border-l-4 border-blue-500 pl-4 my-4">
      {comments.map((comment) => (
        <div key={comment.id} className="mb-3">
          <div className="flex items-start gap-2">
            <div className="w-8 h-8 bg-gray-300 rounded-full" />
            <div className="flex-1">
              <div className="text-sm font-medium">{comment.userName}</div>
              <div className="text-sm text-gray-600">{comment.content}</div>
              <div className="text-xs text-gray-400 mt-1">
                {new Date(comment.createdAt).toLocaleString()}
              </div>
            </div>
          </div>
        </div>
      ))}

      {showForm ? (
        <CommentForm
          docPath={docPath}
          lineNumber={lineNumber}
          onSubmit={(comment) => {
            onNewComment(comment);
            setShowForm(false);
          }}
          onCancel={() => setShowForm(false)}
        />
      ) : (
        <button
          onClick={() => setShowForm(true)}
          className="text-sm text-blue-600 hover:underline"
        >
          Reply
        </button>
      )}
    </div>
  );
}
```

#### 3.3 Enhanced Markdown with Line Numbers (Day 5)

**File: `app/components/markdown/MarkdownWithComments.tsx`**
```typescript
import { useState } from "react";
import { CommentThread } from "../comments/CommentThread";

interface Props {
  document: Document;
  comments: Comment[];
}

export function MarkdownWithComments({ document, comments }: Props) {
  const [selectedLine, setSelectedLine] = useState<number | null>(null);

  const commentsByLine = comments.reduce((acc, comment) => {
    if (comment.lineNumber) {
      if (!acc[comment.lineNumber]) acc[comment.lineNumber] = [];
      acc[comment.lineNumber].push(comment);
    }
    return acc;
  }, {} as Record<number, Comment[]>);

  return (
    <div className="flex gap-4">
      <div className="flex-1">
        {document.lines.map((line, index) => {
          const lineNumber = index + 1;
          const lineComments = commentsByLine[lineNumber] || [];

          return (
            <div key={lineNumber} className="flex group">
              <div
                className="w-12 text-right pr-4 text-gray-400 cursor-pointer hover:bg-gray-100"
                onClick={() => setSelectedLine(lineNumber)}
              >
                {lineNumber}
                {lineComments.length > 0 && (
                  <span className="ml-1 text-xs bg-blue-500 text-white rounded-full px-1">
                    {lineComments.length}
                  </span>
                )}
              </div>
              <div className="flex-1 font-mono text-sm">{line}</div>
            </div>
          );
        })}
      </div>

      {selectedLine && (
        <div className="w-80 border-l pl-4">
          <h3 className="font-bold mb-2">Line {selectedLine}</h3>
          <CommentThread
            comments={commentsByLine[selectedLine] || []}
            docPath={document.path}
            lineNumber={selectedLine}
            onNewComment={(comment) => {
              // Handle new comment
            }}
          />
        </div>
      )}
    </div>
  );
}
```

---

## Phase 4: Suggestions & Discussions (Week 7-8)

### Goal
Enable edit suggestions with diff view and document discussions.

### Tasks

#### 4.1 Suggestion API & UI (Day 1-3)

Follow similar pattern to comments:
- Create `/api/suggestions` route
- Create `SuggestionEditor` component
- Create `SuggestionDiff` component using `diff` library
- Implement approval workflow

#### 4.2 Discussion System (Day 4-5)

Follow similar pattern:
- Create `/api/discussions` route
- Create `DiscussionList` component
- Create `DiscussionThread` component

#### 4.3 Navigation & Polish (Day 6-7)

**File: `app/components/navigation/Sidebar.tsx`**
```typescript
import { Link } from "react-router";

export function Sidebar() {
  return (
    <nav className="space-y-2">
      <div>
        <h2 className="text-xs uppercase text-gray-400 mb-2">Main Docs</h2>
        <Link to="/docs/prd" className="block py-1 hover:text-blue-400">
          Product Requirements
        </Link>
        <Link to="/docs/architecture" className="block py-1 hover:text-blue-400">
          Architecture
        </Link>
      </div>

      <div>
        <h2 className="text-xs uppercase text-gray-400 mb-2 mt-4">Epics</h2>
        <Link to="/docs/epics/epic-1" className="block py-1 hover:text-blue-400">
          Epic 1: Foundation
        </Link>
        {/* Add more epics */}
      </div>
    </nav>
  );
}
```

---

## Deployment

### Production Deployment

```bash
# Build
pnpm build

# Deploy to Cloudflare
pnpm deploy
```

### Environment Setup

```bash
# Set production secrets
pnpm wrangler secret put CF_ACCESS_AUD

# Run migrations on production database
pnpm wrangler d1 migrations apply tamma-docs
```

---

## Success Metrics

After implementation, verify:

✅ Users can browse documentation hierarchy
✅ Users can add inline comments
✅ Users can suggest edits with diff view
✅ Users can create discussions
✅ Authentication works with Cloudflare Access
✅ Page load time < 200ms
✅ All features work on mobile

---

## Quick Start Guide

1. **Set up Cloudflare** (Follow Phase 1.1)
2. **Implement authentication** (Follow Phase 1.3)
3. **Load documents** (Follow Phase 2.1-2.2)
4. **Add comments** (Follow Phase 3)
5. **Add suggestions & discussions** (Follow Phase 4)
6. **Deploy** (Follow Deployment section)

---

**Next Steps**: Start with Phase 1.1 - Cloudflare Setup!
