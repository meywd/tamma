# Quick Start Guide

Get the Tamma Documentation Review Site running in **under 10 minutes**.

## Prerequisites Check

```bash
node --version  # Should be 22+
pnpm --version  # Should be 9+
```

If missing:
- Node.js: https://nodejs.org/
- pnpm: `npm install -g pnpm`

## 5-Minute Setup

### 1. Install Dependencies (2 min)

```bash
cd doc-review
pnpm install
```

### 2. Login to Cloudflare (30 sec)

```bash
pnpm wrangler login
```

Opens browser to authenticate.

### 3. Create D1 Database (1 min)

```bash
pnpm wrangler d1 create tamma-docs
```

**Copy the `database_id` from output**, then:

```bash
# Edit wrangler.toml and replace database_id
nano wrangler.toml  # or use your editor
```

Update this line:
```toml
database_id = "YOUR_ID_HERE"  # Replace with copied ID
```

### 4. Create KV Namespace (30 sec)

```bash
pnpm wrangler kv namespace create CACHE
```

**Copy the `id` from output**, then update `wrangler.toml`:

```toml
id = "YOUR_KV_ID_HERE"  # Replace with copied ID
```

### 5. Initialize Database (30 sec)

```bash
pnpm wrangler d1 execute tamma-docs --local --file=./db/schema.sql
pnpm wrangler d1 execute tamma-docs --file=./db/schema.sql
```

### 6. Start Development Server (30 sec)

```bash
pnpm dev
```

Visit **http://localhost:5173** 🎉

## What You'll See

Right now, you'll see an empty app skeleton. The full implementation needs:

1. **Authentication** (Cloudflare Access setup)
2. **UI Components** (React components)
3. **API Routes** (Comment, suggestion, discussion endpoints)
4. **Markdown Rendering** (Document viewer)

## Next Steps

### Option A: Full Setup with Authentication

Follow **SETUP.md** for complete Cloudflare Access configuration.

### Option B: Start Building Features

Begin with Phase 1 implementation (see PROJECT_SUMMARY.md):

```bash
# Create root layout
touch app/root.tsx

# Create authenticated layout
touch app/routes/_authenticated.tsx

# Create index page
touch app/routes/_authenticated._index.tsx
```

### Option C: Review the Architecture

Read **ARCHITECTURE.md** to understand:
- Database schema
- Component structure
- API design
- Authentication flow
- Feature specifications

## Common Issues

### "Database not found"
- Check `wrangler.toml` has correct `database_id`

### "KV namespace not found"
- Check `wrangler.toml` has correct KV `id`

### "Port already in use"
- Kill process: `lsof -ti:5173 | xargs kill`
- Or use different port: `pnpm dev -- --port 3000`

### Type errors
- Run: `pnpm typecheck`
- Fix TypeScript errors before continuing

## Development Commands

```bash
pnpm dev              # Start dev server
pnpm build            # Build for production
pnpm preview          # Preview production build
pnpm deploy           # Deploy to Cloudflare
pnpm typecheck        # Run TypeScript checks
pnpm lint             # Run ESLint
pnpm format           # Format with Prettier
```

## Database Commands

```bash
pnpm db:migrate:local   # Apply migrations locally
pnpm db:migrate         # Apply migrations to production
pnpm db:studio          # Open Drizzle Studio

# Manual queries
pnpm wrangler d1 execute tamma-docs --local --command "SELECT * FROM users"
```

## Project Structure at a Glance

```
doc-review/
├── ARCHITECTURE.md     📘 Complete technical design
├── README.md           📗 Feature overview
├── SETUP.md            📕 Detailed setup guide
├── QUICKSTART.md       📙 This file
├── PROJECT_SUMMARY.md  📔 Project status
├── app/                🎨 React application
│   ├── routes/         🛣️  File-based routing
│   ├── components/     🧩 React components
│   ├── lib/            📚 Business logic
│   └── styles/         💅 CSS files
├── db/                 💾 Database
│   ├── schema.sql      📊 Database schema
│   └── migrations/     📝 Migration files
└── workers/            ⚙️  Worker entry point
```

## Resources

- **Architecture**: `ARCHITECTURE.md` - Design details
- **Setup**: `SETUP.md` - Step-by-step guide
- **Summary**: `PROJECT_SUMMARY.md` - Project overview
- **React Router**: https://reactrouter.com
- **Cloudflare Workers**: https://developers.cloudflare.com/workers/
- **Cloudflare D1**: https://developers.cloudflare.com/d1/

## Get Help

1. Read `ARCHITECTURE.md` for design decisions
2. Read `SETUP.md` for detailed instructions
3. Check Cloudflare docs for platform-specific issues
4. Review `PROJECT_SUMMARY.md` for implementation roadmap

---

**Ready to build? Start with SETUP.md for full configuration!** 🚀
