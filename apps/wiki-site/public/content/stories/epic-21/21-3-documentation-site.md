---
title: "Story 21.3: Documentation Site"
sidebar:
  order: 210
---

Status: planned

## Story

As a **developer evaluating or onboarding to Tamma**,
I want comprehensive documentation covering getting started, CLI usage, API reference, and GitHub App setup,
so that I can integrate Tamma into my workflow without guesswork.

## Acceptance Criteria

1. A documentation section is accessible at `tamma.dev/docs` with a sidebar navigation, breadcrumbs, and full-text search
2. A "Getting Started" guide walks through: install CLI, connect GitHub, configure AI provider, run first autonomous workflow — in under 5 minutes of reading
3. A "CLI Reference" page documents all CLI commands (`tamma start`, `tamma server`, `tamma api`, etc.) with flags, examples, and expected output
4. An "API Reference" page documents the REST API endpoints (`/api/v1/issues`, `/api/v1/events`, etc.) with request/response schemas, authentication requirements, and curl examples
5. A "GitHub App Setup" guide explains: installing the Tamma GitHub App, configuring repository access, webhook events, and permissions required
6. A "Configuration" page documents all configuration options (`.tamma.yml`, environment variables, provider config, platform config) with annotated examples
7. Documentation pages are written in MDX and rendered by Astro's content collections with automatic table-of-contents generation
8. Previous/next navigation links appear at the bottom of each doc page
9. The documentation is searchable via client-side search (Pagefind, Fuse.js, or similar — no external service dependency)
10. Code blocks have syntax highlighting and a copy-to-clipboard button
11. The docs section is responsive and supports dark mode consistent with the rest of the marketing site
12. A version indicator shows the current documentation version (tied to the latest Tamma release tag)

## Technical Context

### Documentation Structure

```
tamma.dev/docs/
├── /docs                          Overview + quick links
├── /docs/getting-started          5-minute quickstart
├── /docs/installation             Detailed installation (npm, Docker, binary)
├── /docs/cli                      CLI command reference
│   ├── /docs/cli/start            tamma start
│   ├── /docs/cli/server           tamma server
│   └── /docs/cli/api              tamma api
├── /docs/api                      REST API reference
│   ├── /docs/api/authentication   Auth methods (JWT, API key)
│   ├── /docs/api/issues           Issues endpoints
│   ├── /docs/api/events           Events endpoints
│   └── /docs/api/webhooks         Webhook endpoints
├── /docs/github-app               GitHub App setup guide
├── /docs/configuration            Config file reference
├── /docs/providers                AI provider configuration
├── /docs/platforms                Git platform configuration
└── /docs/self-hosting             Self-hosting guide (Docker Compose)
```

### Astro Content Collections

Docs are authored as MDX files in a content collection:

```
apps/marketing-site/
├── src/
│   ├── content/
│   │   ├── config.ts              Content collection schema
│   │   └── docs/
│   │       ├── index.mdx
│   │       ├── getting-started.mdx
│   │       ├── installation.mdx
│   │       ├── cli/
│   │       │   ├── index.mdx
│   │       │   ├── start.mdx
│   │       │   ├── server.mdx
│   │       │   └── api.mdx
│   │       ├── api/
│   │       │   ├── index.mdx
│   │       │   ├── authentication.mdx
│   │       │   ├── issues.mdx
│   │       │   ├── events.mdx
│   │       │   └── webhooks.mdx
│   │       ├── github-app.mdx
│   │       ├── configuration.mdx
│   │       ├── providers.mdx
│   │       ├── platforms.mdx
│   │       └── self-hosting.mdx
│   ├── components/
│   │   ├── docs/
│   │   │   ├── DocsSidebar.astro
│   │   │   ├── DocsLayout.astro
│   │   │   ├── TableOfContents.astro
│   │   │   ├── Breadcrumbs.astro
│   │   │   ├── PrevNext.astro
│   │   │   ├── CodeBlock.astro
│   │   │   ├── SearchBar.astro    (interactive island)
│   │   │   └── CopyButton.astro   (interactive island)
│   │   └── ...
│   ├── layouts/
│   │   └── DocsLayout.astro
│   └── pages/
│       └── docs/
│           └── [...slug].astro     Dynamic route for all doc pages
```

### Content Collection Schema

```typescript
// apps/marketing-site/src/content/config.ts
import { defineCollection, z } from 'astro:content';

const docs = defineCollection({
  type: 'content',
  schema: z.object({
    title: z.string(),
    description: z.string(),
    order: z.number(),                // Sort order in sidebar
    section: z.string(),              // Group in sidebar (e.g., "CLI", "API")
    draft: z.boolean().default(false),
  }),
});

export const collections = { docs };
```

### Search Implementation

Use **Pagefind** for zero-dependency static search:

```bash
# Added to build step
npx pagefind --site dist --glob "docs/**/*.html"
```

Pagefind generates a search index at build time and provides a lightweight client-side search widget (~50KB). No external search service needed.

### Files to Create

| File | Purpose |
|------|---------|
| `apps/marketing-site/src/layouts/DocsLayout.astro` | Docs-specific layout with sidebar + TOC |
| `apps/marketing-site/src/components/docs/DocsSidebar.astro` | Left sidebar navigation tree |
| `apps/marketing-site/src/components/docs/TableOfContents.astro` | Right-side TOC from heading extraction |
| `apps/marketing-site/src/components/docs/Breadcrumbs.astro` | Breadcrumb navigation |
| `apps/marketing-site/src/components/docs/PrevNext.astro` | Previous/next page navigation |
| `apps/marketing-site/src/components/docs/SearchBar.astro` | Pagefind search island |
| `apps/marketing-site/src/components/docs/CopyButton.astro` | Code block copy button island |
| `apps/marketing-site/src/pages/docs/[...slug].astro` | Dynamic route for doc pages |
| `apps/marketing-site/src/content/config.ts` | Content collection schema |
| `apps/marketing-site/src/content/docs/*.mdx` | All documentation content files (12+ files) |

### Files to Modify

| File | Change |
|------|--------|
| `apps/marketing-site/src/components/Header.astro` | Add "Docs" link to navigation |
| `apps/marketing-site/src/components/Footer.astro` | Add "Docs" link to footer nav |
| `apps/marketing-site/package.json` | Add `@astrojs/mdx`, `pagefind`, `shiki` (syntax highlighting) dependencies |
| `apps/marketing-site/astro.config.mjs` | Add MDX integration, configure Shiki theme |

### Key Dependencies

```json
{
  "dependencies": {
    "@astrojs/mdx": "^3.x"
  },
  "devDependencies": {
    "pagefind": "^1.x"
  }
}
```

Astro includes Shiki for syntax highlighting by default — no additional dependency needed.

## Implementation Notes

- **Content-first approach**: Write the MDX content before styling. The Getting Started guide and CLI Reference are the highest-value pages — start there.
- **Source from existing docs**: Much of the content already exists in `docs/architecture.md`, `docs/PRD.md`, and story files. Extract and adapt rather than writing from scratch.
- **API reference generation**: Consider generating the API reference from the Fastify route schemas (if they exist) or OpenAPI spec. For the initial version, hand-written MDX is fine.
- **Code examples**: All code blocks should use real, tested commands and snippets. Include the expected output where helpful.
- **Sidebar ordering**: Use the `order` frontmatter field to control sidebar sort order within each section. Lower numbers appear first.
- **Mobile sidebar**: On mobile, the sidebar should collapse into a hamburger menu or slide-out drawer.
- **Table of contents**: Extract from heading levels (h2, h3) on each page. Highlight the current section on scroll.
- **Edit on GitHub link**: Each doc page should include an "Edit this page on GitHub" link pointing to the MDX source file in the repository.
- **Versioning**: For v1, display the version statically in the sidebar. Full multi-version docs (v1, v2) is out of scope for this story.

## Dependencies

- **Story 21.1** (Marketing Landing Page) — provides the Astro project, base layout, header/footer, and Cloudflare Pages deployment

## Estimated Effort

**28 hours**

| Task | Hours |
|------|-------|
| DocsLayout + sidebar + TOC components | 6 |
| Breadcrumbs + PrevNext components | 2 |
| Content collection schema + dynamic routing | 3 |
| Getting Started guide (MDX content) | 3 |
| CLI Reference (MDX content) | 3 |
| API Reference (MDX content) | 3 |
| GitHub App + Configuration + Self-Hosting guides | 3 |
| Pagefind search integration | 2 |
| Code block copy button + syntax highlighting | 1 |
| Responsive + dark mode + testing | 2 |

---

**Last Updated**: 2026-03-28
