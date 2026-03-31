# Epic 25: Documentation & Wiki Site

## Overview

Build and deploy a custom documentation/wiki site for the Tamma platform, hosted on Cloudflare at wiki.tamma.dev and wiki.its-done.dev. The site renders markdown content from the repository's `wiki/` and `docs/stories/` directories using Astro Starlight, with collapsible sidebar navigation, full-text search, dark/light mode, and auto-deployment via GitHub Actions.

## Goals

1. Make all project documentation publicly accessible at wiki.tamma.dev and wiki.its-done.dev
2. Provide fast, searchable, mobile-responsive documentation with professional design
3. Auto-deploy when wiki/ or docs/stories/ content changes in the repository
4. Leverage Cloudflare edge caching for global performance
5. Support collapsible sidebar navigation grouped by epic
6. Include a visual roadmap timeline page

## Stories

| Story | Title | Priority | Status |
|-------|-------|----------|--------|
| 25-1 | Custom Wiki Site | P1 | Planned |

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

## Content Sources

| Source Directory | Content Type | Approx Files |
|-----------------|-------------|-------------|
| `wiki/` | Top-level pages (Home, Roadmap, Architecture, Epics, Stories, Contributing) | 8 |
| `wiki/Epics/` | Per-epic wiki pages (all 24+ epics) | 25 |
| `docs/stories/epic-*/README.md` | Epic README files with story tables | 22 |
| `docs/stories/epic-*/story-*/*.md` | Individual story/task documents | ~420 |
| **Total** | | **~475** |

## Technical Stack

- **Framework:** Astro Starlight (Astro's official documentation framework)
- **Hosting:** Cloudflare Workers (via @astrojs/cloudflare adapter)
- **Search:** Pagefind (built-in, static, WASM-based full-text search)
- **Build:** Astro + esbuild
- **Deploy:** GitHub Actions -> Wrangler deploy
- **Domains:** wiki.tamma.dev, wiki.its-done.dev (both point to same CF Workers project)

## Dependencies

**Prerequisite Epics:** None (uses existing documentation content)

**Related Epics:**
- Epic 5 (Observability Dashboard & Docs) -- Story 5-9d covers a "full documentation website" which this epic supersedes
- Epic 21 (Marketing Site) -- Existing marketing site at tamma.dev; wiki site complements it

## Key Decisions

1. **Astro Starlight over VitePress/Docusaurus** -- Starlight is purpose-built for documentation, uses Pagefind for zero-backend search, generates static HTML with zero client JS for content pages, and has first-class Cloudflare support (Astro is now a Cloudflare portfolio company as of Feb 2026)
2. **Cloudflare Workers over CF Pages** -- The Astro Cloudflare adapter now targets Workers (not Pages), and Astro 6 has native workerd support for local dev
3. **Static pre-rendering** -- All content pages are pre-rendered at build time; only search/nav use client JS
4. **Build-time content sourcing** -- A pre-build script copies markdown from wiki/ and docs/stories/ into Starlight's content directory structure, transforming GitHub wiki links to Starlight-compatible links

---

_For detailed implementation plan, see [Story 25-1](25-1-custom-wiki-site.md)._
