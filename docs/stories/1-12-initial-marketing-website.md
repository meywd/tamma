# Story 1.12: Initial Marketing Website (Cloudflare Workers)

Status: ready-for-dev

## Story

As a **project maintainer**,
I want an initial marketing website hosted on Cloudflare Workers,
So that early adopters can learn about Tamma and sign up for updates before the full documentation site launches.

## Acceptance Criteria

1. Static website hosted on Cloudflare Workers with custom domain (tamma.dev or similar)
2. Homepage includes: project name, tagline, key features overview, "Coming Soon" message
3. Email signup form for launch notifications (stores emails in Cloudflare KV or external service)
4. Link to GitHub repository for early access
5. Roadmap section showing Epic 1-5 timeline and MVP goals
6. "Why Tamma?" section explaining self-maintenance goal and multi-provider support
7. Responsive design (mobile, tablet, desktop) with fast load times (<1 second)
8. SEO optimization (meta tags, Open Graph, Twitter Cards)
9. Privacy policy and terms of service pages (basic)
10. Analytics integration (privacy-respecting: Cloudflare Web Analytics or Plausible)

## Prerequisites

- None (foundational marketing story, can be done early in Epic 1)

## Tasks / Subtasks

- [ ] Task 1: Design and content creation (AC: 1, 2, 6)
  - [ ] Subtask 1.1: Write homepage copy (tagline, key features, CTA)
  - [ ] Subtask 1.2: Design wireframe/mockup (responsive layout)
  - [ ] Subtask 1.3: Create logo and brand assets (or use placeholder)
  - [ ] Subtask 1.4: Write "Why Tamma?" section explaining self-maintenance goal
  - [ ] Subtask 1.5: Create roadmap visualization (timeline or milestone cards)

- [ ] Task 2: Cloudflare Workers setup (AC: 1)
  - [ ] Subtask 2.1: Create Cloudflare account and set up Workers project
  - [ ] Subtask 2.2: Configure custom domain (DNS, SSL certificate)
  - [ ] Subtask 2.3: Set up Cloudflare Pages for static site hosting (alternative to Workers)
  - [ ] Subtask 2.4: Configure caching and CDN settings

- [ ] Task 3: Email signup implementation (AC: 3)
  - [ ] Subtask 3.1: Create HTML form with email input and submit button
  - [ ] Subtask 3.2: Implement serverless function to handle form submission
  - [ ] Subtask 3.3: Store emails in Cloudflare KV or integrate with Mailchimp/ConvertKit
  - [ ] Subtask 3.4: Send confirmation email to subscribers (optional)
  - [ ] Subtask 3.5: Add email validation and anti-spam protection (reCAPTCHA or hCaptcha)

- [ ] Task 4: Content pages (AC: 4, 9)
  - [ ] Subtask 4.1: Create GitHub repo link with clear CTA ("Get Early Access")
  - [ ] Subtask 4.2: Write privacy policy (template from privacy policy generator)
  - [ ] Subtask 4.3: Write terms of service (template)
  - [ ] Subtask 4.4: Add footer with links to policies and GitHub

- [ ] Task 5: Responsive design and performance (AC: 7)
  - [ ] Subtask 5.1: Implement responsive CSS (mobile-first approach)
  - [ ] Subtask 5.2: Test on mobile devices (iOS Safari, Android Chrome)
  - [ ] Subtask 5.3: Optimize images (WebP format, lazy loading)
  - [ ] Subtask 5.4: Minimize CSS/JS (inline critical CSS, defer non-critical)
  - [ ] Subtask 5.5: Test load time with Lighthouse (<1 second target)

- [ ] Task 6: SEO and social sharing (AC: 8)
  - [ ] Subtask 6.1: Add meta tags (title, description, keywords)
  - [ ] Subtask 6.2: Add Open Graph tags for Facebook/LinkedIn sharing
  - [ ] Subtask 6.3: Add Twitter Card tags for Twitter sharing
  - [ ] Subtask 6.4: Add structured data (JSON-LD for rich snippets)
  - [ ] Subtask 6.5: Create sitemap.xml and robots.txt

- [ ] Task 7: Analytics and monitoring (AC: 10)
  - [ ] Subtask 7.1: Integrate Cloudflare Web Analytics (privacy-respecting, no cookies)
  - [ ] Subtask 7.2: Set up monitoring for email signup success rate
  - [ ] Subtask 7.3: Add error tracking (Cloudflare Workers logging)

- [ ] Task 8: Testing and deployment (AC: 1-10)
  - [ ] Subtask 8.1: Test all links and email signup flow
  - [ ] Subtask 8.2: Test responsive design on multiple devices
  - [ ] Subtask 8.3: Deploy to Cloudflare Workers/Pages
  - [ ] Subtask 8.4: Test production site (DNS, SSL, load time)
  - [ ] Subtask 8.5: Announce site on GitHub README and social media

## Dev Notes

### Requirements Context Summary

**Epic 1 Foundation:** This story creates the initial web presence for Tamma before the full documentation site launches. It serves as a landing page for early adopters and captures launch notifications.

**Self-Maintenance Goal:** The marketing site explains Tamma's unique value proposition (self-maintenance capability) and builds early community interest before MVP launch.

**Cloudflare Workers Rationale:**
- **Fast**: Global CDN with <50ms latency worldwide
- **Free**: Generous free tier (100,000 requests/day)
- **Simple**: No server management, deploy via CLI or GitHub Actions
- **Secure**: Automatic HTTPS, DDoS protection, no cold starts

### Project Structure Notes

**Site Structure:**
```
cloudflare-site/
├── public/
│   ├── index.html          # Homepage
│   ├── privacy.html        # Privacy policy
│   ├── terms.html          # Terms of service
│   ├── styles.css          # Stylesheet
│   └── assets/
│       ├── logo.svg        # Tamma logo
│       └── og-image.png    # Open Graph image
├── functions/
│   └── signup.ts           # Email signup handler
├── wrangler.toml           # Cloudflare Workers config
└── package.json
```

**Technology Stack:**
- HTML5, CSS3 (no framework needed for simple site)
- Cloudflare Workers for serverless functions
- Cloudflare KV for email storage (or Mailchimp API)
- Cloudflare Pages for static hosting

### Content Guidelines

**Homepage Sections:**
1. **Hero**: Tamma logo + tagline + CTA
   - Tagline: "The autonomous development platform that maintains itself"
   - CTA: "Get Early Access" (links to GitHub) + "Notify Me at Launch" (email signup)

2. **Key Features** (3-4 cards):
   - 🤖 "Autonomous Development Loop" - 70%+ completion rate without human intervention
   - 🔀 "Multi-Provider Flexibility" - 8 AI providers, 7 Git platforms, no vendor lock-in
   - 🔒 "Production-Ready Quality" - Mandatory escalation, comprehensive testing, never gets stuck
   - 🛠️ "Self-Maintenance" - Tamma maintains its own codebase (MVP validation goal)

3. **Why Tamma?**
   - Problem: Development teams waste 40-60% of time on repetitive toil
   - Solution: Autonomous development with quality gates and transparency
   - Unique Value: Self-maintenance capability proves production-readiness

4. **Roadmap** (timeline):
   - Epic 1: Foundation & Core Infrastructure (Weeks 0-2)
   - Epic 2: Autonomous Development Loop (Weeks 2-4)
   - Epic 3: Quality Gates & Intelligence (Weeks 4-6)
   - Epic 4: Event Sourcing & Audit Trail (Weeks 6-8)
   - Epic 5: Observability & Production Readiness (Weeks 8-10)
   - Alpha Launch: MVP Self-Maintenance Validation (Week 10)

5. **Coming Soon**
   - "Tamma is currently in active development. Sign up below to be notified when we launch."

6. **Footer**
   - Links: GitHub, Privacy Policy, Terms of Service
   - Copyright: © 2025 Tamma Project
   - Social: GitHub stars badge

### Design Considerations

**Color Scheme** (example - can be adjusted):
- Primary: Blue (#2563eb) - Trust, technology
- Accent: Green (#10b981) - Success, automation
- Background: Light gray (#f9fafb) or white
- Text: Dark gray (#1f2937)

**Typography**:
- Headings: Inter or similar sans-serif
- Body: System font stack for performance

**Performance Targets**:
- First Contentful Paint: <1 second
- Time to Interactive: <2 seconds
- Lighthouse Score: 95+ (Performance, Accessibility, Best Practices, SEO)

### Email Signup Implementation

**Option A: Cloudflare KV (Simple)**
```typescript
// functions/signup.ts
export async function onRequestPost(context) {
  const { request, env } = context;
  const { email } = await request.json();

  // Validate email
  if (!isValidEmail(email)) {
    return new Response('Invalid email', { status: 400 });
  }

  // Store in KV
  await env.SIGNUPS.put(email, Date.now().toString());

  return new Response('Success', { status: 200 });
}
```

**Option B: Mailchimp API (Full-featured)**
```typescript
// Integrate with Mailchimp API for automatic email campaigns
```

### SEO Meta Tags Example

```html
<meta name="description" content="Tamma is an autonomous development platform that maintains itself. 70%+ completion rate with 8 AI providers and 7 Git platforms.">
<meta property="og:title" content="Tamma - Autonomous Development Platform">
<meta property="og:description" content="Self-maintaining autonomous development with multi-provider flexibility">
<meta property="og:image" content="https://tamma.dev/assets/og-image.png">
<meta property="og:url" content="https://tamma.dev">
<meta name="twitter:card" content="summary_large_image">
<meta name="twitter:title" content="Tamma - Autonomous Development Platform">
<meta name="twitter:description" content="Self-maintaining autonomous development with multi-provider flexibility">
<meta name="twitter:image" content="https://tamma.dev/assets/og-image.png">
```

### References

- [Cloudflare Workers Documentation](https://developers.cloudflare.com/workers/)
- [Cloudflare Pages Documentation](https://developers.cloudflare.com/pages/)
- [Source: docs/PRD.md#Goals](F:\Code\Repos\Tamma\docs\PRD.md#Goals)
- [Source: docs/PRD.md#Background-Context](F:\Code\Repos\Tamma\docs\PRD.md#Background-Context)

## Change Log

| Date | Version | Changes | Author |
|------|---------|----------|--------|
| 2025-10-29 | 1.0.0 | Initial story creation for marketing website | Bob (Scrum Master) |

## Dev Agent Record

### Context Reference

- docs/stories/1-12-initial-marketing-website.context.xml

### Agent Model Used

Claude-3.5-Sonnet

### Debug Log References

### Completion Notes List

### File List
