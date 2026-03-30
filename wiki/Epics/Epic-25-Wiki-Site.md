# Epic 25: Documentation & Wiki Site

## Overview

Build and deploy a custom documentation/wiki site for the Tamma platform using **Astro Starlight**, hosted on **Cloudflare Workers** at wiki.tamma.dev and wiki.its-done.dev. The site renders markdown content from the repository's `wiki/` and `docs/stories/` directories with collapsible sidebar navigation, full-text search, dark/light mode, and auto-deployment via GitHub Actions.

## Goals

1. Make all project documentation publicly accessible at wiki.tamma.dev and wiki.its-done.dev
2. Provide fast, searchable, mobile-responsive documentation with professional design
3. Auto-deploy when wiki/ or docs/stories/ content changes in the repository
4. Leverage Cloudflare edge caching for global performance
5. Support collapsible sidebar navigation grouped by epic
6. Include a visual roadmap timeline page

## Architecture

```
wiki.tamma.dev / wiki.its-done.dev
         |
         v
  Cloudflare Workers (edge)
         |
         v
  Astro Starlight (static site)
    - Pagefind (client-side full-text search, WASM)
    - Pre-rendered HTML (zero client JS for content pages)
    - Collapsible sidebar with epic grouping
    - Dark/light mode
         |
    Built from:
    - wiki/*.md (top-level pages: Home, Roadmap, Architecture, etc.)
    - wiki/Epics/*.md (per-epic pages)
    - docs/stories/epic-*/README.md (per-epic story READMEs)
    - docs/stories/epic-*/story-*/*.md (individual task plans)
```

## Technical Stack

- **Framework:** Astro Starlight (Astro's official documentation framework)
- **Hosting:** Cloudflare Workers (via @astrojs/cloudflare adapter)
- **Search:** Pagefind (built-in, static, WASM-based full-text search)
- **Build:** Astro + esbuild
- **Deploy:** GitHub Actions -> Wrangler deploy
- **Domains:** wiki.tamma.dev, wiki.its-done.dev (both point to same CF Workers project)

## Content Sources

| Source Directory | Content Type | Approx Files |
|-----------------|-------------|-------------|
| `wiki/` | Top-level pages (Home, Roadmap, Architecture, Epics, Stories, Contributing) | 8 |
| `wiki/Epics/` | Per-epic wiki pages (all 25 epics) | 26 |
| `docs/stories/epic-*/README.md` | Epic README files with story tables | 22 |
| `docs/stories/epic-*/story-*/*.md` | Individual story/task documents | ~420 |
| **Total** | | **~476** |

## Key Design Decisions

1. **Astro Starlight over VitePress/Docusaurus** -- Starlight is purpose-built for documentation, uses Pagefind for zero-backend search, generates static HTML with zero client JS for content pages, and has first-class Cloudflare support (Astro is now a Cloudflare portfolio company as of Feb 2026)
2. **Cloudflare Workers over CF Pages** -- The Astro Cloudflare adapter now targets Workers (not Pages), and Astro 6 has native workerd support for local dev
3. **Static pre-rendering** -- All content pages are pre-rendered at build time; only search/nav use client JS
4. **Build-time content sourcing** -- A pre-build script copies markdown from wiki/ and docs/stories/ into Starlight's content directory structure, transforming GitHub wiki links to Starlight-compatible links

## Stories

| Story | Title | Tasks | Status |
|-------|-------|-------|--------|
| 25-1 | Custom Wiki Site | 8 | Planned |

### Story 25-1 Tasks

1. Project scaffold and Astro Starlight setup
2. Content sync script (`scripts/sync-content.ts`)
3. Sidebar configuration (collapsible epic groups)
4. Custom theme and branding (Tamma purple)
5. Roadmap timeline component
6. GitHub Actions deployment workflow
7. Domain configuration (wiki.tamma.dev, wiki.its-done.dev)
8. Link validation and quality checks (Lighthouse >= 95)

## Current State

The wiki site scaffold has been created at `apps/wiki-site/` with:
- `astro.config.mjs` with Starlight integration
- `wrangler.toml` for Cloudflare Workers deployment
- `scripts/sync-content.ts` for build-time content transformation
- Custom CSS with Tamma purple branding
- Logo assets (dark/light variants)

## Dependencies

**Prerequisite Epics:** None (uses existing documentation content)

**Related Epics:**
- Epic 5 (Observability Dashboard & Docs) -- Story 5-9d covers a "full documentation website" which this epic supersedes
- Epic 21 (Marketing Site) -- Existing marketing site at tamma.dev; wiki site complements it

## References

- [Story 25-1 Implementation Plan](https://github.com/meywd/tamma/blob/main/docs/stories/epic-25/25-1-custom-wiki-site.md)
- [Epic 25 README](https://github.com/meywd/tamma/blob/main/docs/stories/epic-25/README.md)

---

_See also: [Epics Index](../Epics) | [Roadmap](../Roadmap) | [Stories](../Stories)_
