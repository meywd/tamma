# Deployment Guide - Tamma Marketing Site

This guide provides step-by-step instructions for deploying the Tamma marketing website to Cloudflare Pages.

## Prerequisites

- [ ] Cloudflare account (free tier is sufficient)
- [ ] Node.js 18+ installed
- [ ] Git repository access
- [ ] Domain name (optional, can use Cloudflare Pages subdomain initially)

## Step 1: Cloudflare Account Setup

1. **Create Cloudflare account:**
   - Go to [cloudflare.com](https://cloudflare.com)
   - Sign up for a free account
   - Verify your email address

2. **Install Wrangler CLI:**
   ```bash
   npm install -g wrangler
   ```

3. **Authenticate Wrangler:**
   ```bash
   wrangler login
   ```
   This will open a browser window to authorize Wrangler.

## Step 2: Create KV Namespace

The email signup functionality requires a Cloudflare KV namespace for data storage.

1. **Create production KV namespace:**
   ```bash
   cd marketing-site
   wrangler kv:namespace create SIGNUPS
   ```

   Output example:
   ```
   🌀 Creating namespace with title "tamma-marketing-site-SIGNUPS"
   ✨ Success!
   Add the following to your wrangler.toml:
   [[kv_namespaces]]
   binding = "SIGNUPS"
   id = "abc123def456..."
   ```

2. **Create preview KV namespace (for development):**
   ```bash
   wrangler kv:namespace create SIGNUPS --preview
   ```

3. **Update `wrangler.toml`:**
   - Copy the namespace IDs from the output above
   - Replace `YOUR_KV_NAMESPACE_ID_HERE` with the production ID
   - Replace `YOUR_DEV_KV_NAMESPACE_ID_HERE` with the preview ID

   Example:
   ```toml
   [[kv_namespaces]]
   binding = "SIGNUPS"
   id = "abc123def456..."  # From step 1

   [[env.development.kv_namespaces]]
   binding = "SIGNUPS"
   id = "def456abc123..."  # From step 2
   ```

## Step 3: Local Testing

Before deploying, test the site locally:

1. **Install dependencies:**
   ```bash
   npm install
   ```

2. **Start local development server:**
   ```bash
   npm run dev
   ```

3. **Test the site:**
   - Open `http://localhost:8788` in your browser
   - Test navigation (Home, Privacy, Terms)
   - Test email signup form
   - Verify responsive design (mobile, tablet, desktop)
   - Check browser console for errors

4. **Test email signup:**
   - Enter a test email address
   - Submit the form
   - Verify success message appears
   - Check KV storage:
     ```bash
     wrangler kv:key list --binding=SIGNUPS --preview
     ```

## Step 4: Deploy to Cloudflare Pages

### Option A: Deploy via CLI (Recommended for first deployment)

1. **Deploy to production:**
   ```bash
   npm run deploy:prod
   ```

2. **Verify deployment:**
   - Wrangler will output a deployment URL (e.g., `https://abc123.tamma-marketing-site.pages.dev`)
   - Visit the URL and test all functionality
   - Test email signup with the production KV namespace

### Option B: Deploy via Cloudflare Dashboard (Alternative)

1. **Go to Cloudflare Dashboard:**
   - Navigate to Pages → Create a project → Connect to Git

2. **Connect repository:**
   - Select your Git provider (GitHub, GitLab, etc.)
   - Choose the `meywd/tamma` repository
   - Select the branch to deploy (e.g., `main` or `feature/story-1-12-marketing-website`)

3. **Configure build settings:**
   - **Build command:** (leave empty)
   - **Build output directory:** `marketing-site/public`
   - **Root directory:** `marketing-site`

4. **Add environment variables:**
   - None required (KV namespace is configured in `wrangler.toml`)

5. **Deploy:**
   - Click "Save and Deploy"
   - Wait for deployment to complete (usually <1 minute)

## Step 5: Custom Domain Setup (Optional)

If you have a custom domain (e.g., `tamma.dev`):

1. **Add domain to Cloudflare Pages:**
   - Go to Pages → Your project → Custom domains
   - Click "Set up a custom domain"
   - Enter `tamma.dev`

2. **Configure DNS:**
   - Cloudflare will automatically configure DNS records
   - If your domain is not already on Cloudflare, you'll need to transfer DNS

3. **Wait for SSL certificate:**
   - Cloudflare will automatically provision a free SSL certificate
   - This usually takes 5-15 minutes

4. **Update sitemap and Open Graph URLs:**
   - In `public/sitemap.xml`: Update all URLs to use your custom domain
   - In `public/index.html`: Update `og:url` and `twitter:url` meta tags

5. **Test custom domain:**
   - Visit `https://tamma.dev`
   - Verify SSL certificate is valid (padlock icon in browser)
   - Test all functionality

## Step 6: Analytics Setup

Enable Cloudflare Web Analytics for privacy-respecting visitor tracking.

1. **Create Web Analytics site:**
   - Go to Cloudflare Dashboard → Analytics & Logs → Web Analytics
   - Click "Add a site"
   - Enter site name: `Tamma Marketing Site`
   - Enter hostname: `tamma.dev` (or your custom domain)

2. **Get beacon token:**
   - Copy the token from the snippet provided
   - Example: `{"token": "abc123def456ghi789..."}`

3. **Update HTML files:**
   - Edit `public/index.html`, `public/privacy.html`, `public/terms.html`
   - Find the commented analytics script:
     ```html
     <!-- TODO: Add Cloudflare Web Analytics beacon script -->
     ```
   - Replace with:
     ```html
     <script defer src='https://static.cloudflareinsights.com/beacon.min.js'
             data-cf-beacon='{"token": "YOUR_TOKEN_HERE"}'></script>
     ```

4. **Redeploy:**
   ```bash
   npm run deploy:prod
   ```

5. **Verify analytics:**
   - Visit your site
   - Wait 24 hours for data to appear in Cloudflare dashboard
   - Check Analytics → Web Analytics for visitor data

## Step 7: Verify Deployment

Complete this checklist to ensure everything is working:

- [ ] Homepage loads correctly (`/`)
- [ ] Privacy Policy page loads (`/privacy.html`)
- [ ] Terms of Service page loads (`/terms.html`)
- [ ] GitHub link works (opens `https://github.com/meywd/tamma`)
- [ ] Email signup form submits successfully
- [ ] Email validation works (rejects invalid emails)
- [ ] Success message appears after signup
- [ ] Rate limiting works (max 5 signups per hour per IP)
- [ ] Responsive design works on mobile
- [ ] Responsive design works on tablet
- [ ] Responsive design works on desktop
- [ ] SSL certificate is valid (HTTPS)
- [ ] Custom domain works (if configured)
- [ ] Analytics tracking is working (if configured)
- [ ] Robots.txt is accessible (`/robots.txt`)
- [ ] Sitemap is accessible (`/sitemap.xml`)
- [ ] No console errors in browser

## Step 8: Performance Testing

Run a Lighthouse audit to verify performance targets (AC 7):

1. **Open Chrome DevTools:**
   - Visit your deployed site
   - Press F12 or Cmd+Option+I (Mac)
   - Go to "Lighthouse" tab

2. **Run audit:**
   - Select "Performance", "Accessibility", "Best Practices", "SEO"
   - Click "Analyze page load"

3. **Verify scores:**
   - **Performance:** 95+ ✅
   - **Accessibility:** 95+ ✅
   - **Best Practices:** 95+ ✅
   - **SEO:** 95+ ✅

4. **Check metrics:**
   - **First Contentful Paint:** <1 second ✅
   - **Time to Interactive:** <2 seconds ✅
   - **Largest Contentful Paint:** <2.5 seconds ✅

## Step 9: Email Signup Management

### View Email Signups

List all email signups in your KV namespace:

```bash
wrangler kv:key list --binding=SIGNUPS
```

### Get a Specific Email

```bash
wrangler kv:key get "email:user@example.com" --binding=SIGNUPS
```

### Export All Emails

To export all emails for launch notifications:

```bash
# List all keys with email: prefix
wrangler kv:key list --binding=SIGNUPS --prefix="email:" > signups.txt

# Parse JSON and extract emails
# (You'll need to write a script to parse the JSON output)
```

### Delete an Email (GDPR/Privacy Request)

```bash
wrangler kv:key delete "email:user@example.com" --binding=SIGNUPS
```

## Step 10: Monitoring and Logs

### View Real-Time Logs

Monitor your site's function logs:

```bash
npm run logs
```

This will tail the logs for your Pages deployment.

### Check Signup Activity

View recent signups in the logs:

```bash
npm run logs | grep "New signup"
```

### Monitor Performance

Check Cloudflare Analytics for:
- Page views
- Unique visitors
- Geographic distribution
- Browser/device breakdown
- Performance metrics

## Troubleshooting

### Email Signups Not Working

**Symptom:** Form submits but returns an error

**Solutions:**
1. Check KV namespace ID in `wrangler.toml` is correct
2. Verify KV namespace exists:
   ```bash
   wrangler kv:namespace list
   ```
3. Check function logs for errors:
   ```bash
   npm run logs
   ```
4. Test locally first with `npm run dev`

### Site Not Updating After Deployment

**Symptom:** Changes don't appear on the live site

**Solutions:**
1. Clear Cloudflare cache:
   - Dashboard → Caching → Purge Everything
2. Hard refresh browser: Ctrl+Shift+R (Windows/Linux) or Cmd+Shift+R (Mac)
3. Verify correct branch is deployed in Pages settings

### Custom Domain Not Working

**Symptom:** Domain doesn't resolve or shows SSL error

**Solutions:**
1. Wait 15 minutes for DNS propagation
2. Check DNS records in Cloudflare Dashboard
3. Verify SSL certificate status (Pages → Custom domains)
4. Try `dig tamma.dev` to check DNS resolution

### Analytics Not Showing Data

**Symptom:** No data in Web Analytics dashboard

**Solutions:**
1. Wait 24 hours (analytics data is delayed)
2. Verify beacon token is correct
3. Check script tag is uncommented in HTML files
4. Test with browser DevTools Network tab (should see beacon requests)

## CI/CD Setup (Optional)

To enable automatic deployments on Git push:

1. **Connect Git repository:**
   - Cloudflare Pages → Settings → Builds & deployments
   - Connect your GitHub repository

2. **Configure branch deployments:**
   - **Production branch:** `main`
   - **Preview branches:** All other branches

3. **Push to deploy:**
   ```bash
   git add .
   git commit -m "Update marketing site"
   git push origin main
   ```

Cloudflare will automatically deploy on every push.

## Rollback Procedure

If a deployment has issues:

1. **Via Dashboard:**
   - Pages → Deployments
   - Find the last working deployment
   - Click "Rollback to this deployment"

2. **Via CLI:**
   - Redeploy a previous Git commit:
   ```bash
   git checkout <commit-hash>
   npm run deploy:prod
   git checkout main  # Return to main branch
   ```

## Security Best Practices

- [ ] Never commit KV namespace IDs to public repositories (use environment variables)
- [ ] Rotate KV namespaces if IDs are exposed
- [ ] Monitor signup logs for spam activity
- [ ] Keep dependencies updated (`npm update`)
- [ ] Use Cloudflare's rate limiting features if needed
- [ ] Enable WAF (Web Application Firewall) rules in Cloudflare Dashboard

## Next Steps After Deployment

1. **Update GitHub README:**
   - Add website link to main README.md
   - Add deployment badge

2. **Announce launch:**
   - Social media posts
   - GitHub discussions
   - Developer communities

3. **Monitor metrics:**
   - Email signup rate
   - Page views and engagement
   - Performance metrics
   - Error rates

4. **Iterate:**
   - A/B test CTAs
   - Update roadmap section as development progresses
   - Add testimonials (when available)
   - Create blog posts or documentation

## Support

For deployment issues:
- **Cloudflare Docs:** https://developers.cloudflare.com/pages/
- **Community Discord:** https://discord.cloudflare.com/
- **GitHub Issues:** https://github.com/meywd/tamma/issues

---

**Deployment Status:** ⚠️ Not yet deployed (awaiting KV namespace creation and domain setup)

**Last Updated:** 2025-01-01
