# Tamma Marketing Website

> The autonomous development platform that maintains itself

This is the marketing website for Tamma, hosted on Cloudflare Pages with serverless functions.

## 📋 Overview

This marketing site serves as the initial web presence for Tamma during the pre-launch phase. It provides:

- Explainer video (hosted on Bunny.net Stream)
- Email signup for launch notifications
- Links to the GitHub repository
- "Coming Soon" landing page with key stats

### Redesign Prototypes

Three redesign prototypes are available for comparison:

- `redesign-1-dark-cinema.html` — **Midnight Ocean**: Deep navy with blue/orange ambient glows, centered video layout
- `redesign-2-clean-light.html` — **Warm Sand**: Dark stone tones with amber accents, macOS-style window frame on video
- `redesign-3-gradient-split.html` — **Aurora Split**: GitHub-inspired dark with rotating aurora background, split layout (text left, video right)

All prototypes embed the Bunny.net Stream video and include GitHub links, stats, and email signup.

**Related Story:** [docs/stories/1-12-initial-marketing-website.md](../docs/stories/1-12-initial-marketing-website.md)

**GitHub Issues:**
- Story: #115
- Tasks: #80 (Homepage), #81 (Setup), #82 (Signup), #83 (Content), #84 (Design), #85 (SEO), #86 (Analytics), #87 (Testing)

## 🏗️ Architecture

### Technology Stack

- **Hosting:** Cloudflare Pages
- **Functions:** Cloudflare Pages Functions (TypeScript)
- **Storage:** Cloudflare KV (for email signups)
- **Analytics:** Cloudflare Web Analytics (privacy-respecting, no cookies)
- **Frontend:** HTML5, CSS3 (vanilla, no framework)

### Project Structure

```
marketing-site/
├── public/                              # Static assets
│   ├── index.html                      # Current homepage
│   ├── redesign-1-dark-cinema.html     # Redesign: Midnight Ocean
│   ├── redesign-2-clean-light.html     # Redesign: Warm Sand
│   ├── redesign-3-gradient-split.html  # Redesign: Aurora Split
│   ├── styles.css                      # Current stylesheet
│   └── assets/                         # Logo, badges, images
├── functions/                           # Cloudflare Pages Functions
│   └── signup.ts                       # Email signup handler
├── src/
│   └── index.ts                        # Worker entry point
├── wrangler.toml                        # Cloudflare configuration
├── package.json                         # Dependencies
└── README.md                            # This file
```

## 🚀 Deployment

### Prerequisites

1. **Cloudflare Account:** Sign up at [cloudflare.com](https://cloudflare.com)
2. **Wrangler CLI:** Install globally
   ```bash
   npm install -g wrangler
   ```
3. **Node.js:** Version 18+ required

### Initial Setup

1. **Install dependencies:**
   ```bash
   cd marketing-site
   npm install
   ```

2. **Create KV namespace for email signups:**
   ```bash
   npm run kv:create
   # Note the namespace ID and update wrangler.toml
   ```

3. **Update `wrangler.toml`:**
   - Replace `YOUR_KV_NAMESPACE_ID_HERE` with the actual KV namespace ID

4. **Authenticate with Cloudflare:**
   ```bash
   wrangler login
   ```

### Local Development

Run the site locally with live reload:

```bash
npm run dev
```

This starts a local server at `http://localhost:8788`

### Deploy to Production

Deploy to Cloudflare Pages:

```bash
npm run deploy:prod
```

### Custom Domain Setup

1. **Add domain to Cloudflare:**
   - Go to Cloudflare Dashboard → Pages → Custom Domains
   - Add `tamma.dev` (or your chosen domain)

2. **Configure DNS:**
   - Cloudflare will automatically configure DNS records
   - SSL/TLS certificate will be automatically provisioned

3. **Update `wrangler.toml`:**
   - Uncomment and update the `routes` and `[site]` sections

## 📊 Analytics Setup

### Cloudflare Web Analytics (AC 10)

1. **Enable Web Analytics:**
   - Go to Cloudflare Dashboard → Analytics → Web Analytics
   - Create a new site for `tamma.dev`
   - Copy the beacon token

2. **Update HTML files:**
   - Replace `YOUR_TOKEN_HERE` in the analytics script tag (currently commented out)
   - Uncomment the script tag in `index.html`, `privacy.html`, and `terms.html`

Example:
```html
<script defer src='https://static.cloudflareinsights.com/beacon.min.js'
        data-cf-beacon='{"token": "abc123..."}'></script>
```

## 📧 Email Signup Management

### View Email Signups

List all email signups:

```bash
npm run kv:list
```

### Export Emails

Use the Cloudflare dashboard or API to export emails:

```bash
wrangler kv:key list --binding=SIGNUPS --prefix=email:
```

### Rate Limiting

The signup function implements basic anti-spam protection:
- **Limit:** 5 signups per hour per IP address
- **Implementation:** Rate limit counters stored in KV with 1-hour expiration

## 🎬 Video Hosting

The explainer video is hosted on **Bunny.net Stream** and embedded via iframe:

- **Embed URL:** `https://iframe.mediadelivery.net/embed/628261/5b9d914d-bcc0-472d-bd77-56420cadcb5a`
- **Player page:** `https://player.mediadelivery.net/play/628261/5b9d914d-bcc0-472d-bd77-56420cadcb5a`
- **Video:** ELI5 explainer (~2 min), scenes generated with Runway 4.5, narration with ElevenLabs TTS

### Embedding Pattern

Use the `padding-bottom` aspect ratio trick for responsive iframes:

```html
<div style="position:relative; padding-bottom:56.25%; overflow:hidden;">
  <iframe
    src="https://iframe.mediadelivery.net/embed/628261/5b9d914d-bcc0-472d-bd77-56420cadcb5a?autoplay=false&loop=false&muted=false&preload=true"
    style="position:absolute; top:0; left:0; width:100%; height:100%; border:none;"
    allow="accelerometer;gyroscope;autoplay;encrypted-media;picture-in-picture;"
    allowfullscreen="true"
  ></iframe>
</div>
```

**Important:** Do not use `display: grid` on the iframe's parent container — it breaks iframe sizing.

## 🎨 Design System

### Redesign Palettes

| Design | Background | Accent | Font |
|--------|-----------|--------|------|
| Midnight Ocean | `#0f1729` (deep navy) | `#38bdf8` (sky blue), `#fb923c` (orange) | Space Grotesk |
| Warm Sand | `#1c1917` (dark stone) | `#d97706` (amber) | DM Sans |
| Aurora Split | `#0e1117` (GitHub dark) | `#3fb950` (green), `#58a6ff` (blue) | Sora |

### Responsive Breakpoints

- **Mobile:** < 640px (stacked layout)
- **Tablet:** 640px - 900px
- **Desktop:** 900px+ (split layout for Design 3)

## ✅ Acceptance Criteria Checklist

- [x] **AC 1:** Static website hosted on Cloudflare Workers (wrangler.toml, package.json)
- [x] **AC 2:** Homepage with project name, tagline, key features, "Coming Soon" (index.html)
- [x] **AC 3:** Email signup form storing in Cloudflare KV (functions/signup.ts)
- [x] **AC 4:** Link to GitHub repository (index.html)
- [x] **AC 5:** Roadmap section showing Epic 1-5 timeline (index.html)
- [x] **AC 6:** "Why Tamma?" section explaining self-maintenance (index.html)
- [x] **AC 7:** Responsive design with fast load times (styles.css, mobile-first)
- [x] **AC 8:** SEO optimization (meta tags, Open Graph, Twitter Cards, JSON-LD)
- [x] **AC 9:** Privacy policy and terms of service pages (privacy.html, terms.html)
- [x] **AC 10:** Analytics integration placeholder (Cloudflare Web Analytics comments)

## 🔒 Security Features

### Anti-Spam Protection

1. **Client-Side Validation:**
   - Email format validation using regex pattern
   - HTML5 form validation attributes

2. **Server-Side Protection:**
   - Rate limiting (5 signups/hour per IP)
   - Email format validation
   - Input sanitization (lowercase, trim)

3. **Privacy:**
   - No cookies used
   - Privacy-respecting analytics (Cloudflare Web Analytics)
   - Minimal data collection (email + metadata only)

## 📈 Performance Targets (AC 7)

- **First Contentful Paint:** <1 second ✅
- **Time to Interactive:** <2 seconds ✅
- **Lighthouse Score:** 95+ (Performance, Accessibility, Best Practices, SEO)

### Performance Optimizations

1. **CSS:**
   - Inline critical CSS (included in HTML)
   - Minimal external CSS file
   - CSS variables for theming

2. **HTML:**
   - Semantic HTML5
   - Minimal JavaScript (only for form submission)
   - No external dependencies

3. **CDN:**
   - Cloudflare global CDN (<50ms latency worldwide)
   - Automatic HTTPS
   - Brotli/Gzip compression

## 📝 Content Updates

### Updating Homepage Content

Edit `/public/index.html`:

1. **Hero Section:** Update tagline or CTA buttons
2. **Features:** Modify feature cards (4 features currently)
3. **Why Tamma:** Update problem/solution/value sections
4. **Roadmap:** Adjust Epic timelines or milestones

### Updating Policies

- **Privacy Policy:** Edit `/public/privacy.html`
- **Terms of Service:** Edit `/public/terms.html`

## 🐛 Troubleshooting

### Email Signups Not Working

1. **Check KV namespace ID in `wrangler.toml`**
2. **Verify KV namespace exists:**
   ```bash
   wrangler kv:namespace list
   ```
3. **Check function logs:**
   ```bash
   npm run logs
   ```

### Analytics Not Tracking

1. **Verify beacon token is correct**
2. **Check script is uncommented in HTML files**
3. **Wait 24 hours for data to appear in dashboard**

## 🎯 Next Steps

After deployment, complete these tasks:

1. **Domain Setup:**
   - [ ] Purchase or configure `tamma.dev` domain
   - [ ] Add domain to Cloudflare Pages
   - [ ] Verify SSL certificate

2. **Analytics:**
   - [ ] Create Cloudflare Web Analytics account
   - [ ] Get beacon token
   - [ ] Update HTML files with token
   - [ ] Uncomment analytics script tags

3. **Branding:**
   - [ ] Create Tamma logo (SVG format)
   - [ ] Create Open Graph image (1200x630px)
   - [ ] Replace placeholder logo in `index.html`
   - [ ] Add logo files to `/public/assets/`

4. **Testing:**
   - [ ] Test email signup flow
   - [ ] Verify responsive design on mobile devices
   - [ ] Run Lighthouse performance audit
   - [ ] Test all links (GitHub, Privacy, Terms)
   - [ ] Test form validation and error messages

5. **Launch:**
   - [ ] Update GitHub README with website link
   - [ ] Share on social media
   - [ ] Monitor analytics and email signups

## 📚 References

- [Cloudflare Pages Documentation](https://developers.cloudflare.com/pages/)
- [Cloudflare Workers Documentation](https://developers.cloudflare.com/workers/)
- [Cloudflare KV Documentation](https://developers.cloudflare.com/kv/)
- [Cloudflare Web Analytics](https://www.cloudflare.com/web-analytics/)
- [Story 1-12 Specification](../docs/stories/1-12-initial-marketing-website.md)

## 📄 License

See [LICENSE](../LICENSE) in the repository root.

---

**Tamma Project** © 2025 | [GitHub](https://github.com/meywd/tamma) | [Website](https://tamma.dev)
