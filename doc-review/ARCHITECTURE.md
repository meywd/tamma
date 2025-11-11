# Documentation Review Site Architecture

## Overview

The Documentation Review Site is a collaborative platform for reviewing, commenting, discussing, and suggesting edits to Tamma's technical documentation. Built on Cloudflare Workers with React Router v7, it provides a fast, globally distributed experience for team members to interact with documentation.

## Technology Stack

### Frontend
- **React Router v7**: Full-stack React framework with SSR and file-based routing
- **React 18**: UI library with concurrent features
- **TypeScript 5.7+**: Type-safe development
- **Tailwind CSS**: Utility-first styling
- **react-markdown**: Markdown rendering with remark/rehype plugins
- **prism-react-renderer**: Syntax highlighting for code blocks

### Backend
- **Cloudflare Workers**: Edge compute platform (V8 isolates)
- **Cloudflare D1**: SQLite database at the edge
- **Drizzle ORM**: Type-safe database queries
- **Cloudflare KV**: Key-value store for sessions/cache
- **Cloudflare R2** (optional): Object storage for file attachments

### Authentication
- **Cloudflare Access** (Primary): Zero-trust authentication with OAuth providers
- **Auth.js** (Alternative): Flexible authentication with session management

### Build & Deploy
- **Vite 6**: Build tool and dev server
- **pnpm**: Package manager (workspace support)
- **Wrangler**: Cloudflare CLI for deployment
- **esbuild**: Fast JavaScript bundler

## System Architecture

### High-Level Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        Cloudflare Edge                          │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌──────────────────┐         ┌─────────────────┐             │
│  │  Cloudflare      │────────▶│  React Router   │             │
│  │  Access (Auth)   │         │  Worker         │             │
│  └──────────────────┘         └────────┬────────┘             │
│                                         │                       │
│                          ┌──────────────┼──────────────┐       │
│                          ▼              ▼              ▼       │
│                   ┌──────────┐   ┌──────────┐  ┌──────────┐   │
│                   │ D1 SQLite│   │ KV Store │  │ R2 Bucket│   │
│                   │ Database │   │ Sessions │  │ (optional)│   │
│                   └──────────┘   └──────────┘  └──────────┘   │
└─────────────────────────────────────────────────────────────────┘
                            │
                            ▼
                    ┌───────────────┐
                    │  Git Repo     │
                    │  (docs/*.md)  │
                    └───────────────┘
```

### Request Flow

1. **User Request** → Cloudflare Edge (nearest location)
2. **Authentication Check** → Cloudflare Access validates JWT
3. **React Router Worker** → Processes request, loads data
4. **Data Fetching** → Query D1 database, read markdown files
5. **SSR Rendering** → Generate HTML on edge
6. **Response** → Stream HTML to client
7. **Client Hydration** → React takes over for interactivity

## Data Model

### Database Schema (Cloudflare D1)

```sql
-- Users
CREATE TABLE users (
  id TEXT PRIMARY KEY,              -- UUID v7
  email TEXT UNIQUE NOT NULL,
  name TEXT NOT NULL,
  avatar_url TEXT,
  role TEXT DEFAULT 'viewer',       -- viewer, reviewer, admin
  created_at INTEGER NOT NULL,      -- Unix timestamp
  updated_at INTEGER NOT NULL
);

-- Comments (inline and document-level)
CREATE TABLE comments (
  id TEXT PRIMARY KEY,              -- UUID v7
  doc_path TEXT NOT NULL,           -- 'docs/PRD.md'
  line_number INTEGER,              -- NULL for doc-level comments
  line_content TEXT,                -- Context snapshot
  content TEXT NOT NULL,            -- Markdown-supported comment
  user_id TEXT NOT NULL,
  parent_id TEXT,                   -- For threaded replies
  resolved BOOLEAN DEFAULT FALSE,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE,
  FOREIGN KEY (parent_id) REFERENCES comments(id) ON DELETE CASCADE
);

-- Edit Suggestions
CREATE TABLE suggestions (
  id TEXT PRIMARY KEY,              -- UUID v7
  doc_path TEXT NOT NULL,
  line_start INTEGER NOT NULL,
  line_end INTEGER NOT NULL,
  original_text TEXT NOT NULL,
  suggested_text TEXT NOT NULL,
  description TEXT,                 -- Markdown-supported rationale
  status TEXT DEFAULT 'pending',    -- pending, approved, rejected
  reviewed_by TEXT,                 -- User ID of reviewer
  reviewed_at INTEGER,
  user_id TEXT NOT NULL,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE,
  FOREIGN KEY (reviewed_by) REFERENCES users(id) ON DELETE SET NULL
);

-- Discussions (document-level threads)
CREATE TABLE discussions (
  id TEXT PRIMARY KEY,              -- UUID v7
  doc_path TEXT NOT NULL,
  title TEXT NOT NULL,
  description TEXT,
  status TEXT DEFAULT 'open',       -- open, resolved, closed
  user_id TEXT NOT NULL,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE
);

-- Discussion Messages
CREATE TABLE discussion_messages (
  id TEXT PRIMARY KEY,              -- UUID v7
  discussion_id TEXT NOT NULL,
  content TEXT NOT NULL,            -- Markdown-supported
  user_id TEXT NOT NULL,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  FOREIGN KEY (discussion_id) REFERENCES discussions(id) ON DELETE CASCADE,
  FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE
);

-- Document Metadata (cached)
CREATE TABLE document_metadata (
  doc_path TEXT PRIMARY KEY,
  title TEXT NOT NULL,
  description TEXT,
  category TEXT,                    -- main, epic, story, research
  epic_id TEXT,                     -- For stories
  story_id TEXT,                    -- For tasks
  word_count INTEGER,
  line_count INTEGER,
  last_modified INTEGER NOT NULL,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL
);

-- Indexes
CREATE INDEX idx_comments_doc_path ON comments(doc_path);
CREATE INDEX idx_comments_user_id ON comments(user_id);
CREATE INDEX idx_comments_line_number ON comments(line_number);
CREATE INDEX idx_suggestions_doc_path ON suggestions(doc_path);
CREATE INDEX idx_suggestions_status ON suggestions(status);
CREATE INDEX idx_suggestions_user_id ON suggestions(user_id);
CREATE INDEX idx_discussions_doc_path ON discussions(doc_path);
CREATE INDEX idx_discussions_status ON discussions(status);
CREATE INDEX idx_discussion_messages_discussion_id ON discussion_messages(discussion_id);
CREATE INDEX idx_document_metadata_category ON document_metadata(category);
CREATE INDEX idx_document_metadata_epic_id ON document_metadata(epic_id);
```

## Application Structure

### File Organization

```
doc-review/
├── app/
│   ├── routes/                         # File-based routing
│   │   ├── _index.tsx                  # Landing page
│   │   ├── login.tsx                   # Login page (redirect to CF Access)
│   │   ├── _authenticated.tsx          # Layout with auth check
│   │   ├── _authenticated._index.tsx   # Dashboard
│   │   ├── _authenticated.docs.tsx     # Docs layout wrapper
│   │   ├── _authenticated.docs._index.tsx        # Main docs (PRD, Arch, Epics)
│   │   ├── _authenticated.docs.prd.tsx           # PRD viewer
│   │   ├── _authenticated.docs.architecture.tsx  # Architecture viewer
│   │   ├── _authenticated.docs.epics._index.tsx  # Epics listing
│   │   ├── _authenticated.docs.epics.$epicId.tsx # Epic + stories
│   │   ├── _authenticated.docs.stories.$storyId.tsx # Story + tasks
│   │   ├── _authenticated.docs.research._index.tsx  # Research listing
│   │   ├── _authenticated.docs.research.$researchId.tsx # Research doc
│   │   └── api/                        # API routes
│   │       ├── comments.ts             # Comment CRUD
│   │       ├── suggestions.ts          # Suggestion CRUD
│   │       ├── discussions.ts          # Discussion CRUD
│   │       └── documents.ts            # Document metadata
│   ├── components/
│   │   ├── markdown/
│   │   │   ├── MarkdownRenderer.tsx    # Core markdown display
│   │   │   ├── LineNumbers.tsx         # Line number gutter
│   │   │   ├── CommentIndicator.tsx    # Comment badges on lines
│   │   │   └── SyntaxHighlight.tsx     # Code block highlighting
│   │   ├── comments/
│   │   │   ├── CommentThread.tsx       # Thread of comments
│   │   │   ├── CommentForm.tsx         # New comment form
│   │   │   ├── CommentBubble.tsx       # Inline comment UI
│   │   │   └── ResolveButton.tsx       # Mark as resolved
│   │   ├── suggestions/
│   │   │   ├── SuggestionEditor.tsx    # Create suggestion
│   │   │   ├── SuggestionDiff.tsx      # Before/after view
│   │   │   ├── SuggestionCard.tsx      # Suggestion display
│   │   │   └── SuggestionReview.tsx    # Approve/reject UI
│   │   ├── discussions/
│   │   │   ├── DiscussionList.tsx      # List of discussions
│   │   │   ├── DiscussionThread.tsx    # Full thread view
│   │   │   ├── DiscussionForm.tsx      # Create discussion
│   │   │   └── MessageForm.tsx         # Reply to discussion
│   │   ├── navigation/
│   │   │   ├── Sidebar.tsx             # Main navigation sidebar
│   │   │   ├── Breadcrumbs.tsx         # Breadcrumb trail
│   │   │   ├── TableOfContents.tsx     # In-page TOC
│   │   │   └── SearchBar.tsx           # Document search
│   │   └── layout/
│   │       ├── Header.tsx              # Top header with user menu
│   │       ├── Footer.tsx              # Footer
│   │       └── DocLayout.tsx           # 3-column layout
│   ├── lib/
│   │   ├── db/
│   │   │   ├── client.server.ts        # D1 client factory
│   │   │   ├── schema.ts               # Drizzle schema
│   │   │   ├── queries.ts              # Reusable queries
│   │   │   └── migrations/             # SQL migrations
│   │   ├── auth/
│   │   │   ├── auth.server.ts          # Auth helpers
│   │   │   ├── session.server.ts       # Session management
│   │   │   └── middleware.ts           # Auth middleware
│   │   ├── markdown/
│   │   │   ├── loader.server.ts        # Load markdown from repo
│   │   │   ├── parser.server.ts        # Parse frontmatter
│   │   │   ├── renderer.tsx            # React components
│   │   │   └── plugins/                # Remark/rehype plugins
│   │   ├── utils/
│   │   │   ├── date.ts                 # Date helpers
│   │   │   ├── uuid.ts                 # UUID v7 generator
│   │   │   ├── errors.ts               # Error classes
│   │   │   └── validators.ts           # Input validation
│   │   └── types/
│   │       ├── document.ts             # Document types
│   │       ├── comment.ts              # Comment types
│   │       ├── suggestion.ts           # Suggestion types
│   │       └── user.ts                 # User types
│   ├── styles/
│   │   ├── globals.css                 # Global styles + Tailwind
│   │   ├── markdown.css                # Markdown-specific styles
│   │   └── themes/                     # Color themes
│   ├── entry.client.tsx                # Client entry point
│   └── root.tsx                        # Root layout
├── workers/
│   └── app.ts                          # Cloudflare Worker entry
├── db/
│   ├── schema.sql                      # Initial schema
│   └── migrations/                     # Migration files
├── public/
│   ├── favicon.ico
│   └── assets/
├── wrangler.toml                       # Cloudflare configuration
├── drizzle.config.ts                   # Drizzle ORM config
├── react-router.config.ts              # React Router config
├── vite.config.ts                      # Vite configuration
├── tailwind.config.ts                  # Tailwind configuration
├── tsconfig.json                       # TypeScript config
├── package.json
└── README.md
```

## Authentication & Authorization

### Cloudflare Access Integration

**Setup:**
1. Create Cloudflare Access application
2. Configure OAuth providers (GitHub, Google, Microsoft)
3. Define access policies (email domain, specific users)
4. Enable JWT validation in application

**Request Flow:**
```
User → CF Access Login → OAuth Provider → CF Access → App Worker
                                                       ↓
                                            Validate JWT Header
                                                       ↓
                                            Extract User Info
                                                       ↓
                                            Create/Update User Record
```

**JWT Validation:**
```typescript
// app/lib/auth/auth.server.ts
interface CloudflareAccessJWT {
  email: string;
  name: string;
  sub: string;  // User ID
  aud: string;  // Application ID
  exp: number;  // Expiration
}

async function validateAccessJWT(
  request: Request,
  env: Env
): Promise<User | null> {
  const token = request.headers.get('Cf-Access-Jwt-Assertion');
  if (!token) return null;

  // Verify JWT signature with Cloudflare's public keys
  const payload = await verifyJWT(token, env.CF_ACCESS_AUD);

  // Get or create user in D1
  const user = await db
    .select()
    .from(users)
    .where(eq(users.email, payload.email))
    .get();

  if (!user) {
    return await db.insert(users).values({
      id: generateUUIDv7(),
      email: payload.email,
      name: payload.name,
      avatar_url: payload.picture,
      created_at: Date.now(),
      updated_at: Date.now()
    }).returning();
  }

  return user;
}
```

### Role-Based Access Control

**Roles:**
- **Viewer**: Read-only access to all docs
- **Reviewer**: Can comment, suggest edits, create discussions
- **Admin**: Can approve/reject suggestions, manage users

**Permissions:**
```typescript
interface Permissions {
  canView: (user: User, doc: Document) => boolean;
  canComment: (user: User, doc: Document) => boolean;
  canSuggestEdits: (user: User, doc: Document) => boolean;
  canReviewSuggestions: (user: User) => boolean;
  canManageUsers: (user: User) => boolean;
}
```

## Document Management

### Document Loading Strategy

**Source:** Git repository at `/home/meywd/tamma/docs/`

**Loading Approach:**
1. **Build-time indexing**: Scan all markdown files during build
2. **Runtime reading**: Read files on-demand from filesystem
3. **Metadata caching**: Store metadata in D1 for fast queries
4. **Content caching**: Cache parsed markdown in KV (optional)

**Metadata Extraction:**
```typescript
interface DocumentMetadata {
  path: string;
  title: string;
  description: string;
  category: 'main' | 'epic' | 'story' | 'research' | 'retrospective';
  epicId?: string;
  storyId?: string;
  wordCount: number;
  lineCount: number;
  lastModified: number;
  headings: Array<{ level: number; text: string; id: string }>;
}
```

### Document Hierarchy

```typescript
interface DocStructure {
  main: [
    { id: 'prd', title: 'Product Requirements', path: 'docs/PRD.md' },
    { id: 'architecture', title: 'Architecture', path: 'docs/architecture.md' },
    { id: 'epics', title: 'Epics Overview', path: 'docs/epics.md' }
  ],
  epics: [
    {
      id: 'epic-1',
      title: 'Epic 1: Foundation & Core Infrastructure',
      techSpec: 'docs/tech-spec-epic-1.md',
      stories: [
        {
          id: '1-0',
          title: 'AI Provider Strategy Research',
          path: 'docs/stories/1-0-ai-provider-strategy-research.md',
          tasks: []
        },
        // ... more stories
      ]
    },
    // ... more epics
  ],
  research: [
    {
      id: 'ai-provider-strategy',
      title: 'AI Provider Strategy',
      path: 'docs/research/ai-provider-strategy-2024-10.md'
    }
  ],
  retrospectives: [
    {
      id: 'epic-1-retro',
      title: 'Epic 1 Retrospective',
      path: 'docs/retrospectives/epic-1-retro-2025-11-06.md'
    }
  ]
}
```

## Feature Implementation

### 1. Markdown Rendering with Line Numbers

**Component Structure:**
```tsx
<MarkdownRenderer doc={document}>
  <LineNumberGutter lines={document.lines} comments={comments} />
  <MarkdownContent>
    {/* Rendered markdown */}
  </MarkdownContent>
  <CommentPanel>
    {/* Active comments for selected line */}
  </CommentPanel>
</MarkdownRenderer>
```

**Line Number Interaction:**
- Click line number → Open comment form
- Hover line → Show comment indicator
- Line with comments → Badge with count

### 2. Inline Comments

**Data Flow:**
```
User clicks line → CommentForm opens → Submit
                                        ↓
                              POST /api/comments
                                        ↓
                              Insert into D1
                                        ↓
                              Revalidate loader
                                        ↓
                              UI updates
```

**Comment Threading:**
- Top-level comments attached to line number
- Replies nested under parent comment
- Max nesting depth: 3 levels

### 3. Edit Suggestions

**Workflow:**
```
1. User selects text (lines 10-15)
2. Click "Suggest Edit" button
3. Open suggestion editor (split view)
   - Left: Original text (read-only)
   - Right: Editable text area
4. Add description/rationale
5. Submit suggestion
6. Reviewer sees suggestion in review queue
7. Reviewer approves/rejects
8. Status updates in UI
```

**Diff Rendering:**
- Use `diff` library to compute changes
- Highlight additions (green), deletions (red)
- Line-by-line comparison view

### 4. Discussions

**Types:**
- **Document-level**: General discussion about entire document
- **Section-level**: Discussion about specific section (future enhancement)

**UI:**
- Discussion list in sidebar
- Click discussion → Open in modal/drawer
- Threaded messages with markdown support
- Mark as resolved/closed

### 5. Navigation

**Sidebar Structure:**
```
├─ Main Documentation
│  ├─ Product Requirements (PRD)
│  ├─ Architecture
│  └─ Epics Overview
├─ Epics
│  ├─ Epic 1: Foundation
│  │  ├─ Tech Spec
│  │  └─ Stories (13)
│  │     ├─ 1-0: AI Provider Strategy
│  │     ├─ 1-1: AI Provider Interface
│  │     └─ ...
│  ├─ Epic 2: Autonomous Development
│  └─ ...
├─ Research
│  ├─ AI Provider Strategy
│  ├─ AI Provider Cost Analysis
│  └─ ...
└─ Retrospectives
   ├─ Epic 1 Retro
   └─ Epic 2 Retro
```

**Search:**
- Full-text search across all documents
- Filter by category (epics, stories, research)
- Highlight matches in results

## API Design

### REST Endpoints

```typescript
// Comments
GET    /api/comments?docPath=/docs/PRD.md&lineNumber=42
POST   /api/comments
PATCH  /api/comments/:id
DELETE /api/comments/:id

// Suggestions
GET    /api/suggestions?docPath=/docs/architecture.md&status=pending
POST   /api/suggestions
PATCH  /api/suggestions/:id/review  // Approve/reject
DELETE /api/suggestions/:id

// Discussions
GET    /api/discussions?docPath=/docs/epics.md
POST   /api/discussions
PATCH  /api/discussions/:id
DELETE /api/discussions/:id

// Discussion Messages
GET    /api/discussions/:id/messages
POST   /api/discussions/:id/messages
DELETE /api/discussions/:discussionId/messages/:messageId

// Documents
GET    /api/documents                 // List all
GET    /api/documents/metadata?path=/docs/PRD.md
GET    /api/documents/content?path=/docs/PRD.md
```

### React Router Loaders

```typescript
// app/routes/_authenticated.docs.prd.tsx
export async function loader({ request, context }: LoaderFunctionArgs) {
  const user = await requireAuth(request, context);
  const docPath = 'docs/PRD.md';

  const [document, comments, suggestions, discussions] = await Promise.all([
    loadDocument(docPath, context),
    loadComments(docPath, context),
    loadSuggestions(docPath, context),
    loadDiscussions(docPath, context)
  ]);

  return json({ user, document, comments, suggestions, discussions });
}
```

## Performance Optimization

### Caching Strategy

**Cloudflare KV:**
- Parsed markdown (key: `doc:${path}:${lastModified}`)
- Document metadata (key: `meta:${path}`)
- User sessions (key: `session:${userId}`)

**TTL:**
- Documents: 1 hour (or until file changes)
- Metadata: 24 hours
- Sessions: 7 days

**Cache Invalidation:**
- On file changes (webhook from git or manual trigger)
- On suggestion approval (clears document cache)

### Code Splitting

- Route-based splitting (automatic with React Router)
- Lazy load heavy components (diff viewer, markdown editor)
- Preload adjacent routes on hover

### Database Optimization

- Indexes on frequently queried columns
- Limit comment queries to visible lines only
- Paginate discussion messages (50 per page)
- Use prepared statements for common queries

## Deployment

### Cloudflare Workers Setup

**wrangler.toml:**
```toml
name = "tamma-doc-review"
main = "workers/app.ts"
compatibility_date = "2025-01-15"
compatibility_flags = ["nodejs_compat"]

[observability]
enabled = true

[[d1_databases]]
binding = "DB"
database_name = "tamma-docs"
database_id = "<CREATED_DB_ID>"

[[kv_namespaces]]
binding = "CACHE"
id = "<CREATED_KV_ID>"

[[r2_buckets]]
binding = "STORAGE"
bucket_name = "tamma-attachments"

[vars]
CF_ACCESS_AUD = "<ACCESS_APPLICATION_ID>"
REPO_PATH = "/home/meywd/tamma/docs"
```

### Build & Deploy Commands

```bash
# Install dependencies
pnpm install

# Run database migrations
pnpm wrangler d1 migrations apply tamma-docs

# Build for production
pnpm build

# Deploy to Cloudflare
pnpm wrangler deploy

# View logs
pnpm wrangler tail
```

### CI/CD Integration

**GitHub Actions Workflow:**
```yaml
name: Deploy Doc Review

on:
  push:
    branches: [main]
    paths: ['doc-review/**']

jobs:
  deploy:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: pnpm/action-setup@v2
      - uses: actions/setup-node@v4
        with:
          node-version: 22
          cache: 'pnpm'
      - run: pnpm install
      - run: pnpm build
      - uses: cloudflare/wrangler-action@v3
        with:
          apiToken: ${{ secrets.CLOUDFLARE_API_TOKEN }}
          workingDirectory: doc-review
```

## Security Considerations

### Input Validation
- Sanitize all user inputs (comments, suggestions)
- Validate markdown for XSS attempts
- Limit file path access (no directory traversal)
- Rate limit API endpoints

### Authentication
- JWT signature validation on every request
- Short token expiration (1 hour)
- Automatic session renewal
- Logout endpoint clears session

### Data Protection
- Comments/suggestions encrypted at rest (D1 auto-encrypts)
- HTTPS only (enforced by Cloudflare)
- CORS policies for API endpoints
- CSP headers to prevent XSS

### Access Control
- Role-based permissions enforced on every action
- Document-level access control (future: private epics)
- Audit log for sensitive actions (future enhancement)

## Testing Strategy

### Unit Tests (Vitest)
- Markdown parser
- Comment threading logic
- Suggestion diff generation
- Auth helpers
- Database queries

### Integration Tests
- API endpoints with test D1 database
- Authentication flow
- Comment creation/deletion
- Suggestion approval workflow

### E2E Tests (Playwright)
- User login flow
- Document navigation
- Create/reply to comments
- Submit edit suggestions
- Resolve discussions

### Performance Tests
- Lighthouse scores (target: 90+ on all metrics)
- Load testing with Artillery (1000 concurrent users)
- Database query performance (< 50ms p95)

## Monitoring & Observability

### Cloudflare Analytics
- Request volume and latency
- Error rates (4xx, 5xx)
- Geographic distribution
- Cache hit rates

### Custom Metrics
- Comments created/resolved per day
- Suggestions pending/approved/rejected
- Active users per week
- Popular documents (most viewed)

### Error Tracking
- Sentry integration for error logging
- Cloudflare Workers logging
- Database query slow log

### Alerts
- Error rate > 1%
- Response time p95 > 1000ms
- D1 database storage > 80%
- KV storage > 80%

## Future Enhancements

### Phase 2 Features
- **Real-time collaboration**: Multiple users editing simultaneously (Durable Objects + WebSockets)
- **Notification system**: Email/Slack notifications for comments/mentions
- **Review workflow**: Formal approval process for documentation changes
- **Version history**: Track changes to documents over time
- **Export to PDF**: Generate PDF versions of documentation
- **Search improvements**: Semantic search with embeddings

### Phase 3 Features
- **AI-powered suggestions**: Use LLM to suggest improvements
- **Automated summarization**: Generate summaries for long documents
- **Translation**: Multi-language support
- **Mobile app**: Native iOS/Android apps
- **Offline mode**: PWA with offline reading

## Cost Estimation

### Cloudflare Workers (Paid Plan - $5/month)
- 10M requests/month included
- D1: 25GB storage, 5B reads, 50M writes
- KV: 1GB storage, 10M reads, 1M writes
- R2: 10GB storage, 1M Class A operations

### Expected Usage (100 users)
- Requests: ~500K/month (well under limit)
- D1 reads: ~1M/month (0.02% of limit)
- D1 writes: ~100K/month (0.2% of limit)
- KV reads: ~200K/month (2% of limit)

**Total Monthly Cost: ~$5-10/month**

## Development Workflow

### Local Development
```bash
# Start dev server with Workers runtime
pnpm dev

# Run type checking
pnpm typecheck

# Run tests
pnpm test

# Run linter
pnpm lint

# Format code
pnpm format
```

### Database Development
```bash
# Create migration
pnpm wrangler d1 migrations create tamma-docs <migration_name>

# Apply migrations (local)
pnpm wrangler d1 migrations apply tamma-docs --local

# Apply migrations (production)
pnpm wrangler d1 migrations apply tamma-docs

# Query database (local)
pnpm wrangler d1 execute tamma-docs --local --command "SELECT * FROM users"
```

## Conclusion

This architecture provides a robust foundation for collaborative documentation review. Key strengths:

- **Performance**: Edge deployment with SSR for fast global access
- **Scalability**: Cloudflare's infrastructure handles traffic spikes
- **Developer Experience**: Modern tooling (React Router v7, TypeScript, Drizzle)
- **Cost-Effective**: Generous free tier, predictable pricing
- **Type-Safe**: End-to-end TypeScript with strict mode
- **Maintainable**: Clear separation of concerns, well-documented

The system is designed to grow with the team's needs while maintaining simplicity and performance.
