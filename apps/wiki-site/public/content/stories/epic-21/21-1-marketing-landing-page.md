---
title: "Story 21.1: Marketing Landing Page"
sidebar:
  order: 210
---

Status: planned

## Story

As a **potential user visiting tamma.dev**,
I want to see a polished landing page with clear value proposition, feature highlights, social proof, and a call-to-action,
so that I understand what Tamma does and am compelled to sign up or try it.

## Acceptance Criteria

1. The landing page loads at `tamma.dev/` with a hero section containing headline, subheadline, animated hero visual or product screenshot, and primary CTA button ("Get Started Free" / "View on GitHub")
2. A "Features" section displays at least 6 feature cards (autonomous workflows, multi-provider AI, multi-platform Git, event sourcing audit trail, self-maintaining, quality gates) with icons and short descriptions
3. A "How It Works" section presents a 3-4 step visual flow (connect repo, assign issue, autonomous development, review & merge) with numbered steps and illustrations
4. A "Testimonials / Social Proof" section shows at least 3 testimonial cards or trust metrics (GitHub stars, repos connected, tasks completed)
5. A footer contains navigation links (Features, Pricing, Docs, Blog, GitHub, Legal), company info, and social links
6. The page achieves Lighthouse scores of 90+ on Performance, Accessibility, Best Practices, and SEO
7. The page is fully responsive (mobile, tablet, desktop breakpoints at 640px, 768px, 1024px, 1280px)
8. Dark mode is supported and respects `prefers-color-scheme` with a manual toggle
9. The site is built with Astro and deploys to Cloudflare Pages with automatic builds on push to `main`
10. Existing email signup functionality (Cloudflare KV) is preserved and integrated into the new page
11. All existing SEO metadata (Open Graph, Twitter Cards, JSON-LD structured data) is preserved or improved
12. Navigation header includes links to: Features, Pricing, Docs, Blog, Sign In (app.tamma.dev), and GitHub

## Technical Context

### Current Marketing Site

The existing site at `apps/marketing-site/` is vanilla HTML/CSS served by Cloudflare Workers with a Wrangler-based build. It already has:
- Hero section with headline, stats (3x Faster, 70%+ Tasks, Zero Vendor Lock-in), and CTA buttons
- Key Features section with 6 feature cards
- How It Works section with 4 steps
- Email signup form backed by Cloudflare KV
- SEO metadata (OG tags, Twitter Cards, JSON-LD)
- Dark mode toggle
- Responsive design

The migration to Astro preserves all of this while enabling component-based architecture, MDX content, and easier maintenance for upcoming pricing/docs/blog pages.

### Astro Project Setup

```
apps/marketing-site/       (restructured)
├── astro.config.mjs
├── package.json
├── public/
│   ├── assets/            (migrated from current public/assets/)
│   │   ├── SVG/
│   │   ├── logo.svg
│   │   └── og-image.png
│   ├── favicon.svg
│   └── robots.txt
├── src/
│   ├── components/
│   │   ├── Header.astro
│   │   ├── Footer.astro
│   │   ├── Hero.astro
│   │   ├── Features.astro
│   │   ├── HowItWorks.astro
│   │   ├── Testimonials.astro
│   │   ├── EmailSignup.astro   (interactive island)
│   │   └── ThemeToggle.astro   (interactive island)
│   ├── layouts/
│   │   └── BaseLayout.astro
│   ├── pages/
│   │   ├── index.astro
│   │   ├── pricing.astro       (placeholder for Story 21.2)
│   │   └── [...slug].astro     (catch-all for docs/blog, Story 21.3)
│   └── styles/
│       └── global.css          (migrated from current styles.css)
├── tsconfig.json
└── wrangler.toml               (updated for Cloudflare Pages)
```

### Key Dependencies

```json
{
  "dependencies": {
    "astro": "^4.x",
    "@astrojs/cloudflare": "^11.x"
  },
  "devDependencies": {
    "@astrojs/check": "^0.9.x",
    "typescript": "^5.7.x"
  }
}
```

### Email Signup (Interactive Island)

The existing Cloudflare KV email signup must be preserved. In Astro, this becomes a client-side island:

```astro
---
// EmailSignup.astro — server-rendered form shell
---
<form id="signup-form" class="signup-form" data-astro-island>
  <input type="email" name="email" placeholder="your@email.com" required />
  <button type="submit">Notify Me at Launch</button>
</form>

<script>
  // Client-side JS for form submission to Cloudflare Worker function
  document.getElementById('signup-form')?.addEventListener('submit', async (e) => {
    e.preventDefault();
    // POST to /api/signup (Cloudflare Worker function)
  });
</script>
```

The Cloudflare Worker function at `functions/api/signup.ts` handles KV writes (already exists in current site).

### Deployment

- **Build**: `astro build` outputs static HTML to `dist/`
- **Deploy**: Cloudflare Pages connects to the `apps/marketing-site/` directory with build command `pnpm build`
- **Preview**: Cloudflare Pages preview deployments on PRs
- **Custom domain**: `tamma.dev` (already configured in Cloudflare)

### Files to Create

| File | Purpose |
|------|---------|
| `apps/marketing-site/astro.config.mjs` | Astro configuration with Cloudflare adapter |
| `apps/marketing-site/src/layouts/BaseLayout.astro` | Base HTML layout with SEO metadata |
| `apps/marketing-site/src/components/Header.astro` | Navigation header |
| `apps/marketing-site/src/components/Footer.astro` | Footer with links |
| `apps/marketing-site/src/components/Hero.astro` | Hero section |
| `apps/marketing-site/src/components/Features.astro` | Feature cards grid |
| `apps/marketing-site/src/components/HowItWorks.astro` | Step-by-step flow |
| `apps/marketing-site/src/components/Testimonials.astro` | Social proof section |
| `apps/marketing-site/src/components/EmailSignup.astro` | Email signup island |
| `apps/marketing-site/src/components/ThemeToggle.astro` | Dark mode toggle island |
| `apps/marketing-site/src/pages/index.astro` | Landing page composition |
| `apps/marketing-site/src/styles/global.css` | Migrated global styles |

### Files to Modify

| File | Change |
|------|--------|
| `apps/marketing-site/package.json` | Replace Wrangler scripts with Astro build/dev; add Astro dependencies |
| `apps/marketing-site/tsconfig.json` | Update for Astro TypeScript config |
| `apps/marketing-site/wrangler.toml` | Update to point to Astro `dist/` output or switch to `pages.toml` for Cloudflare Pages |

### Files to Remove (After Migration)

| File | Reason |
|------|--------|
| `apps/marketing-site/public/index.html` | Replaced by `src/pages/index.astro` |
| `apps/marketing-site/public/styles.css` | Migrated to `src/styles/global.css` |
| `apps/marketing-site/public/styles-improved.css` | Consolidated into global.css |
| `apps/marketing-site/src/index.ts` | Cloudflare Worker entry point replaced by Pages Functions |

## Implementation Notes

- **Incremental migration**: Port existing HTML sections one-by-one into Astro components. Start with BaseLayout, then Hero, then Features, etc. Verify visual parity at each step.
- **Asset preservation**: All SVG badges, logos, and images in `public/assets/` must be preserved without changes.
- **Performance budget**: The current site is very lightweight (no JS framework). Astro's zero-JS-by-default output preserves this. Only EmailSignup and ThemeToggle need client JS.
- **Accessibility**: Preserve existing skip links, ARIA labels, semantic HTML. Add `role="navigation"` to header, `role="main"` to content.
- **Analytics**: Preserve the Cloudflare Web Analytics beacon script placeholder.
- **Sitemap**: Astro has a built-in `@astrojs/sitemap` integration. Replace the manual `scripts/generate-sitemap.ts`.

## Dependencies

- None (this is the first marketing story)

## Estimated Effort

**20 hours**

| Task | Hours |
|------|-------|
| Astro project setup + configuration | 3 |
| BaseLayout + Header + Footer components | 4 |
| Hero section migration | 3 |
| Features + HowItWorks migration | 3 |
| Testimonials section (new content) | 2 |
| EmailSignup + ThemeToggle islands | 2 |
| Responsive + dark mode verification | 2 |
| Lighthouse audit + fixes | 1 |

---

**Last Updated**: 2026-03-28
