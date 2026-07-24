---
title: "Epic 25: Documentation & Wiki Site"
sidebar:
  order: 25
---

**Status:** Shipped (Story 25-1 delivered as a Vite + React 19 SPA on Cloudflare Workers). Live at `wiki.tamma.dev`. Single-story epic.
**Stories:** 1 (25-1) with 8 tasks
**Packages:** `apps/wiki-site` (Vite + React 19 + React Router 7 + react-markdown + Mermaid + @xyflow/react)

## Overview

Epic 25 is the public documentation site for Tamma at `wiki.tamma.dev` (and the mirror `wiki.its-done.dev`). Every epic page, every story, every workflow diagram, the roadmap, and the architecture brief are rendered from source-controlled markdown in `wiki/` and `docs/stories/`. The site is served as a Vite-built React SPA from Cloudflare Workers static assets, with a pre-build content sync step that flattens the repository's markdown into a shape the SPA can fetch.

The design decision pivoted from Astro Starlight (originally planned) to a hand-rolled Vite + React SPA during implementation. The reason: much richer content types needed interactive rendering — React Flow workflow diagrams with dagre auto-layout, Mermaid diagrams, epic-detail pages that re-parse markdown into components — which is awkward in Starlight's static MDX model. The result is a fast, search-friendly SPA that renders the same markdown the wiki is authored in, plus a handful of first-class visualisations.

## Architecture

```
 Author pushes to main
          │
          ▼
   GitHub Actions (on push to wiki/** or docs/stories/**)
          │
          ▼
   apps/wiki-site/scripts/sync-content.ts      (pre-build, Node)
     ├── reads  wiki/*.md, wiki/Epics/*.md
     ├── reads  docs/stories/epic-*/README.md
     ├── reads  docs/stories/epic-*/**/*.md
     ├── rewrites GitHub wiki-style links → /epics/:slug
     ├── rewrites GitHub repo links → local anchors
     └── writes  apps/wiki-site/public/content/{epics,stories,...}/*.md
          │
          ▼
   Vite build  (apps/wiki-site)
     ├── React 19 + React Router 7 SPA
     ├── TailwindCSS 4 (dark theme)
     ├── @xyflow/react + dagre  (WorkflowDiagram)
     ├── mermaid                (WorkflowDetailPage diagrams)
     └── react-markdown + remark-gfm + rehype-raw (all MD rendering)
          │
          ▼
   wrangler deploy
          │
          ▼
 Cloudflare Workers
   binding: ASSETS (static files from /dist)
   src/worker.ts:
     1. try env.ASSETS.fetch(request)
     2. if 404 → SPA fallback: serve /index.html
   route: wiki.tamma.dev  (custom_domain)
```

On the client, React Router dispatches routes to dedicated pages. Each page `fetch('/content/...')` for its markdown, parses it with `react-markdown`, and renders either as plain prose (generic pages) or through a specialised component that re-parses the markdown into sectioned UI (epics, stories, workflows, roadmap).

## Components

### Build-time (`apps/wiki-site/scripts/sync-content.ts`)

| Step | Behaviour |
|------|-----------|
| `cleanOutput()` | Recreates `public/content/`. |
| `extractFrontmatter()` | Pulls existing YAML FM if present. |
| `deriveTitle()` | Prefers first H1, falls back to filename. |
| Link rewriting | GitHub wiki links (`[[Epic 18]]`) → `/epics/18-user-auth`; repo links (`docs/stories/...`) → `/stories/...`. |
| `writeManifest()` | Emits `public/content/manifest.json` — flat list of every page with `{path, title, section}` (consumed by the sidebar + prev/next nav). |

### Runtime (`apps/wiki-site/src/`)

| Component | File | Responsibility |
|-----------|------|----------------|
| `App` | `App.tsx` | React Router 7 routes. |
| `worker` | `worker.ts` | Cloudflare Workers entry — tries asset, SPA-fallback to `/`. |
| `Layout` | `components/Layout.tsx` | Dark shell, sidebar, main content area, prev/next nav. |
| `Sidebar` | `components/Sidebar.tsx` | Collapsible epic groups, search box, active-route highlight. |
| `HomePage` | `components/HomePage.tsx` | Landing page with epic grid + quick links. |
| `RoadmapPage` | `components/RoadmapPage.tsx` | Visual timeline across epics. |
| `ArchitecturePage` | `components/ArchitecturePage.tsx` | Rich markdown with embedded diagrams. |
| `EpicsPage` | `components/EpicsPage.tsx` | Grid of all epics with status + progress bar. |
| `EpicDetailPage` | `components/EpicDetailPage.tsx` | Re-parses per-epic markdown into goals + deliverables + table sections + stories list + fallback sections (746 lines). |
| `StoriesPage` | `components/StoriesPage.tsx` | Flat story catalog. |
| `StoryDetailPage` | `components/StoryDetailPage.tsx` | Per-story markdown with task list + status. |
| `WorkflowsPage` | `components/WorkflowsPage.tsx` | Catalog of all 30 workflows. |
| `WorkflowDetailPage` | `components/WorkflowDetailPage.tsx` | Per-workflow page with React Flow diagram + Mermaid + Markdown (584 lines). |
| `WorkflowDiagram` | `components/WorkflowDiagram.tsx` | React Flow + dagre auto-layout (1591 lines) — draggable nodes, minimap, edge labels, colour coding by activity kind. |
| `MermaidDiagram` | `components/MermaidDiagram.tsx` | Initialises Mermaid with the dark theme, renders SVG, error fallback to `<pre>`. |
| `MarkdownPage` | `components/MarkdownPage.tsx` | Generic markdown renderer with heading anchors for ToC. |
| `InlineMarkdown` | `components/InlineMarkdown.tsx` | Short-form markdown without paragraph wrapping for table cells and tight UI. |

### Deploy (`wrangler.jsonc`)

- `name`: `tamma-wiki-site`
- `main`: `src/worker.ts`
- `assets.directory`: `./dist`, binding `ASSETS`
- `routes`: `wiki.tamma.dev` (custom_domain)
- `observability.enabled`: `true`
- `workers_dev: true`, `preview_urls: true` for staging builds

## Class diagram (page rendering)

```
                  ┌──────────────────┐
                  │      Layout      │
                  │  ┌────────────┐  │
                  │  │  Sidebar   │  │◀── manifest.json
                  │  └────────────┘  │
                  │  <Outlet/>       │
                  └────┬─────────────┘
                       │ React Router
      ┌────────────┬───┴──────────┬─────────────┬──────────────┐
      ▼            ▼              ▼             ▼              ▼
 HomePage   EpicDetailPage   StoryDetailPage   WorkflowDetail   MarkdownPage
    │            │                │                │                  │
    │            │                │                │                  │
    │     fetch('/content/epics/:slug.md')       fetch('.../md')    fetch('...')
    │            │                │                │                  │
    │            ▼                ▼                ▼                  │
    │     parseSections()   parseTasks()    parseFlowGraph()          │
    │     parseStories()    parseStatus()   ┌───────────────┐          │
    │     parseTables()     renderTree()    │WorkflowDiagram│          │
    │     renderGoals/Impl/ │                │ + dagre      │          │
    │     Tech/Stories/Fall │                │ + @xyflow    │          │
    │     back              │                └───────────────┘          │
    │     Markdown + InlineMarkdown (react-markdown + remark-gfm + rehype-raw)
    │                                        │                          │
    │                                        ▼                          │
    │                              MermaidDiagram (mermaid@11)           │
    │                                                                   │
    └──── <Markdown/> ──── react-markdown ──── remark-gfm / rehype-raw ─┘
```

## Sequence diagram — edit markdown, see it live

```
Author          Git           GH Actions       Wrangler          CF Workers        Browser
  │              │                 │                 │                 │              │
  │ edit wiki/Epics/Epic-99.md     │                 │                 │              │
  │─────────────▶│                 │                 │                 │              │
  │ commit+push  │                 │                 │                 │              │
  │─────────────▶│                 │                 │                 │              │
  │              │ on push:        │                 │                 │              │
  │              │ wiki/** touched │                 │                 │              │
  │              │────────────────▶│                 │                 │              │
  │              │                 │ npm i + build   │                 │              │
  │              │                 │ sync-content.ts │                 │              │
  │              │                 │  copies wiki/**, docs/stories/**  │              │
  │              │                 │  → public/content/  + manifest.json              │
  │              │                 │ vite build      │                 │              │
  │              │                 │ dist/ ready     │                 │              │
  │              │                 │────────────────▶│                 │              │
  │              │                 │                 │ wrangler deploy │              │
  │              │                 │                 │────────────────▶│              │
  │              │                 │                 │                 │ asset sync   │
  │                                                                                   │
  │ visitor loads wiki.tamma.dev/epics/99-foo                                          │
  │─────────────────────────────────────────────────────────────────────────────────▶│
  │                                                                       asset miss │
  │                                                                       → /index.html│
  │◀─────────────────────────────────────────────────────────────────────────────────│
  │ React Router → EpicDetailPage(slug=99-foo)                                        │
  │ fetch /content/epics/99-foo.md + /content/manifest.json                           │
  │─────────────────────────────────────────────────────────────────────────────────▶│
  │ parse + render                                                                    │
  │◀─────────────────────────────────────────────────────────────────────────────────│
```

## Use cases

1. **New user lands on `wiki.tamma.dev`** — home page shows the roadmap, epic grid, and quick links to Architecture + Contributing.
2. **Reader browses epics** — `/epics` shows the full epic catalog with completion status, click-through to `/epics/:slug` renders the epic markdown with goals + stories + tables + fallback sections.
3. **Reader opens a workflow** — `/workflows/llm-call` renders the markdown and an interactive React Flow diagram auto-laid-out by dagre; draggable nodes, minimap, edge labels.
4. **Reader reads a story's tasks** — `/stories/epic-24/24-1-websocket-foundation` shows title, status, description, ACs, task breakdown.
5. **Site search (future)** — currently out of scope; sidebar search is a prefix-match over `manifest.json` titles.
6. **Link validation** — the build is a failure-visible step; if `sync-content.ts` can't resolve a link target it warns at build time (Lighthouse ≥ 95 gates the PR).
7. **Author edits epic markdown** — commits to `main`, GitHub Actions rebuilds, Wrangler deploys, the change is live on Cloudflare edge in < 60s.

## Content sources

| Source directory | Content type | Approx. files |
|-----------------|-------------|-------------|
| `wiki/` | Top-level pages (Home, Roadmap, Architecture, Epics, Stories, Contributing) | ~10 |
| `wiki/Epics/` | Per-epic pages | ~33 |
| `docs/stories/epic-*/README.md` | Epic README files with story tables | ~22 |
| `docs/stories/epic-*/story-*/*.md` | Individual story/task documents | ~420 |
| **Total** | | **~485** |

## Stories

| Story | Title | Tasks | Status |
|-------|-------|-------|--------|
| 25-1 | [Custom Wiki Site](/stories/epic-25//25-1-custom-wiki-site.md) | 8 | **Done** |

### Story 25-1 tasks (all delivered)

1. Project scaffold (`apps/wiki-site/`) and Cloudflare Worker setup.
2. Content sync script (`scripts/sync-content.ts`) with link rewriting.
3. Sidebar with collapsible epic groups driven by `manifest.json`.
4. Custom theme (Tamma dark palette) in `index.css` with Tailwind 4.
5. Roadmap timeline component (1020 lines).
6. GitHub Actions deployment workflow (on push to main).
7. Domain configuration — `wiki.tamma.dev` (and secondary `wiki.its-done.dev`).
8. Link validation + Lighthouse ≥ 95 on Performance / Accessibility / Best Practices / SEO.

## Key design decisions

1. **Vite + React SPA over Astro Starlight** — the original plan was Starlight, but the need for interactive workflow diagrams (dagre-laid-out React Flow with 30+ nodes) and live Mermaid, plus the desire to re-parse markdown into structured UI for epic pages, made a hand-rolled SPA a better fit. Starlight's static MDX would've required component islands for everything interactive anyway.
2. **Cloudflare Workers over Pages** — the Astro/Cloudflare adapter now targets Workers; we kept Workers for consistency even after swapping away from Astro. Static assets via the `ASSETS` binding.
3. **Build-time content sourcing** — `sync-content.ts` copies + rewrites markdown at build, so the runtime is a simple `fetch('/content/...')`. No server-side rendering, no KV, no content API.
4. **SPA fallback in Worker** — `worker.ts` returns `index.html` on 404 so deep-linking (`/epics/:slug`) works without pre-rendering every URL.
5. **Mermaid renders where called** — `MermaidDiagram` is used in workflow pages; epic and story pages render mermaid code fences as plain `<pre>` blocks inside prose (see `EpicDetailPage`'s technicalSections). This is why the epic-page template in this wiki folder uses ASCII diagrams rather than mermaid.
6. **No search backend** — the sidebar search is a client-side title filter; a future story may bring Pagefind or similar WASM full-text search.
7. **Two domains, one build** — `wiki.tamma.dev` (primary) and `wiki.its-done.dev` (mirror) both route to the same Worker.

## Dependencies

**Prerequisite epics**: none — the epic consumes existing documentation content.

**Related epics**:
- [Epic 5 — Observability Dashboard & Docs](Epic-5-Observability.md) — Story 5-9d originally proposed a full documentation site; Epic 25 supersedes it.
- [Epic 21 — Marketing Site & User Dashboard](Epic-21-Marketing-Dashboard.md) — marketing site at `tamma.dev`; the wiki complements it.

## Current state

- **Live**: `wiki.tamma.dev` serves the current repository docs on every push to main. Worker deployed via Wrangler, custom domain attached. Dark theme live. Roadmap + epics + stories + workflows all render.
- **Known limitations**:
  - No full-text search (title filter only).
  - Epic pages render mermaid code fences as `<pre>` blocks, not as SVG (workflow pages use SVG via `MermaidDiagram`). This motivated the ASCII-diagram style used in the 9-section epic template.
  - No versioning — always serves content from `main`.
- **Not planned**: blog / changelog section (would be better on marketing-site, see Epic 21 discussion).

## See also

- [Epic 5 — Observability Dashboard & Docs](Epic-5-Observability.md) — the admin observability surface.
- [Epic 21 — Marketing Site & User Dashboard](Epic-21-Marketing-Dashboard.md) — the public acquisition funnel.
- [Architecture](/architecture/) — the page you can read here on the wiki.
- [Roadmap](Roadmap.md) — timeline across all epics.

## Story files

[Epic 25 story on GitHub](/stories/epic-25/)

---

_Last updated: 2026-04-22_
