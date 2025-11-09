# Tamma Documentation Review Site

A collaborative platform for reviewing, commenting, and suggesting edits to Tamma's technical documentation. Built with React Router v7 and deployed on Cloudflare Workers.

## Features

- **Authentication**: Secure login with Cloudflare Access (GitHub, Google, Microsoft OAuth)
- **Markdown Rendering**: Beautiful documentation display with syntax highlighting
- **Inline Comments**: Add comments to specific lines in documents
- **Edit Suggestions**: Propose changes with diff view for review
- **Discussions**: Document-level threaded discussions
- **Hierarchical Navigation**: Browse by main docs, epics, stories, and research
- **Search**: Full-text search across all documentation
- **Real-time Updates**: Collaborative features with live data

## Tech Stack

- **Framework**: React Router v7 (full-stack React)
- **Runtime**: Cloudflare Workers (edge deployment)
- **Database**: Cloudflare D1 (SQLite at the edge)
- **ORM**: Drizzle ORM (type-safe queries)
- **Styling**: Tailwind CSS
- **Markdown**: react-markdown + remark/rehype plugins
- **Deployment**: Cloudflare Pages

## Prerequisites

- Node.js 22+ LTS
- pnpm 9+
- Cloudflare account (free tier works)
- Wrangler CLI (installed via dependencies)

## Setup

### 1. Install Dependencies

```bash
pnpm install
```

### 2. Create Cloudflare Resources

```bash
# Create D1 database
pnpm wrangler d1 create tamma-docs

# Note the database_id and update wrangler.toml

# Create KV namespace
pnpm wrangler kv namespace create CACHE

# Note the id and update wrangler.toml
```

### 3. Update Configuration

Edit `wrangler.toml` and replace `TODO` values with actual IDs:

```toml
[[d1_databases]]
binding = "DB"
database_name = "tamma-docs"
database_id = "YOUR_DATABASE_ID"

[[kv_namespaces]]
binding = "CACHE"
id = "YOUR_KV_ID"
```

### 4. Run Database Migrations

```bash
# Local development
pnpm db:migrate:local

# Production
pnpm db:migrate
```

### 5. Configure Authentication

Set up Cloudflare Access:

1. Go to Cloudflare Dashboard → Zero Trust → Access → Applications
2. Create a new Self-hosted application
3. Configure OAuth providers (GitHub, Google, etc.)
4. Set access policies (email domain, specific users)
5. Note the Application Audience (AUD) tag
6. Add to wrangler.toml or use secrets:

```bash
pnpm wrangler secret put CF_ACCESS_AUD
```

## Development

### Start Dev Server

```bash
pnpm dev
```

Runs on `http://localhost:5173` with Cloudflare Workers runtime.

### Type Checking

```bash
pnpm typecheck
```

### Database Studio

```bash
pnpm db:studio
```

Opens Drizzle Studio for database exploration.

### Linting

```bash
pnpm lint
```

### Formatting

```bash
pnpm format
```

## Project Structure

```
doc-review/
├── app/
│   ├── routes/                  # File-based routing
│   │   ├── _index.tsx           # Landing page
│   │   ├── _authenticated.tsx   # Auth layout
│   │   └── _authenticated.docs.*.tsx  # Doc routes
│   ├── components/              # React components
│   │   ├── markdown/            # Markdown rendering
│   │   ├── comments/            # Comment features
│   │   ├── suggestions/         # Edit suggestions
│   │   ├── discussions/         # Discussion threads
│   │   └── navigation/          # Nav components
│   ├── lib/                     # Business logic
│   │   ├── db/                  # Database & ORM
│   │   ├── auth/                # Authentication
│   │   ├── markdown/            # Markdown processing
│   │   └── utils/               # Utilities
│   └── styles/                  # CSS files
├── workers/                     # Worker entry point
├── db/
│   └── migrations/              # SQL migrations
├── public/                      # Static assets
├── wrangler.toml                # Cloudflare config
├── react-router.config.ts       # React Router config
└── drizzle.config.ts            # Drizzle config
```

## Deployment

### Build for Production

```bash
pnpm build
```

### Deploy to Cloudflare

```bash
pnpm deploy
```

### Preview Production Build

```bash
pnpm preview
```

## Database Schema

The application uses Cloudflare D1 (SQLite) with the following tables:

- **users**: User accounts and profiles
- **comments**: Inline comments on documentation
- **suggestions**: Edit suggestions with approval workflow
- **discussions**: Document-level discussion threads
- **discussion_messages**: Messages in discussions
- **document_metadata**: Cached document information

See `app/lib/db/schema.ts` for the complete schema.

## API Routes

### Comments
- `GET /api/comments?docPath=...&lineNumber=...`
- `POST /api/comments`
- `PATCH /api/comments/:id`
- `DELETE /api/comments/:id`

### Suggestions
- `GET /api/suggestions?docPath=...&status=...`
- `POST /api/suggestions`
- `PATCH /api/suggestions/:id/review`
- `DELETE /api/suggestions/:id`

### Discussions
- `GET /api/discussions?docPath=...`
- `POST /api/discussions`
- `PATCH /api/discussions/:id`
- `DELETE /api/discussions/:id`

## Authentication & Authorization

### Roles

- **Viewer**: Read-only access
- **Reviewer**: Can comment, suggest edits, create discussions
- **Admin**: Can approve/reject suggestions, manage users

### Cloudflare Access Integration

The application uses Cloudflare Access for authentication:

1. User visits site
2. Redirected to Cloudflare Access login
3. Authenticates via OAuth (GitHub, Google, etc.)
4. JWT token included in requests
5. Application validates JWT and creates/retrieves user

## Environment Variables

Set via Wrangler secrets or environment:

```bash
# Required
pnpm wrangler secret put CF_ACCESS_AUD

# For database migrations
export CLOUDFLARE_ACCOUNT_ID="your-account-id"
export CLOUDFLARE_DATABASE_ID="your-database-id"
export CLOUDFLARE_D1_TOKEN="your-api-token"
```

## Documentation Hierarchy

```
Main Documentation
├── Product Requirements (PRD)
├── Architecture
└── Epics Overview

Epics
├── Epic 1: Foundation
│   ├── Tech Spec
│   └── Stories (13)
├── Epic 2: Autonomous Development
│   └── Stories (11)
└── ...

Research
├── AI Provider Strategy
├── AI Provider Cost Analysis
└── ...

Retrospectives
├── Epic 1 Retro
├── Epic 2 Retro
└── ...
```

## Performance

- **Cold Start**: < 100ms (Cloudflare Workers)
- **SSR Rendering**: < 200ms p95
- **Database Queries**: < 50ms p95
- **Global CDN**: Sub-100ms latency worldwide

## Security

- HTTPS-only (enforced by Cloudflare)
- JWT signature validation
- Input sanitization (XSS prevention)
- Rate limiting on API endpoints
- CORS policies
- CSP headers

## Monitoring

- Cloudflare Analytics (requests, latency, errors)
- Custom metrics (comments, suggestions, active users)
- Error tracking (Sentry integration planned)
- Database query performance

## Cost Estimation

**Cloudflare Workers Paid Plan**: $5/month

- 10M requests/month included
- D1: 25GB storage, 5B reads, 50M writes
- KV: 1GB storage, 10M reads, 1M writes

**Expected Usage** (100 users): ~$5-10/month

## Future Enhancements

### Phase 2
- Real-time collaboration (Durable Objects + WebSockets)
- Email/Slack notifications
- Formal approval workflow
- Version history tracking
- PDF export

### Phase 3
- AI-powered suggestions (LLM integration)
- Automated summarization
- Multi-language support
- Mobile apps (iOS/Android)
- Offline mode (PWA)

## Contributing

This is an internal tool for Tamma documentation review. For questions or issues, contact the development team.

## License

MIT - See LICENSE file for details.

## Support

For issues or questions:
- Check `/doc-review/ARCHITECTURE.md` for detailed design
- Review documentation in `/docs/*`
- Contact: [Your contact info]

---

**Built with ❤️ for the Tamma Team**
