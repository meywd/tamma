# Quick Start Guide - Tamma Marketing Site

Get the marketing site running locally in under 5 minutes.

## Prerequisites

- Node.js 18+ installed
- Cloudflare account (free tier)

## 1. Install Dependencies

```bash
cd marketing-site
npm install
```

## 2. Create KV Namespace

```bash
# Create production KV namespace
wrangler kv:namespace create SIGNUPS

# Copy the namespace ID from output
# Example output:
# [[kv_namespaces]]
# binding = "SIGNUPS"
# id = "abc123def456..."
```

## 3. Update Configuration

Edit `wrangler.toml` and replace `YOUR_KV_NAMESPACE_ID_HERE` with the ID from step 2.

## 4. Authenticate with Cloudflare

```bash
wrangler login
```

This will open a browser window. Click "Allow" to authorize Wrangler.

## 5. Run Locally

```bash
npm run dev
```

Open http://localhost:8788 in your browser.

## 6. Test Email Signup

1. Fill in the email form with a test email
2. Click "Notify Me"
3. Verify you see a success message
4. Check KV storage:
   ```bash
   wrangler kv:key list --binding=SIGNUPS --preview
   ```

## 7. Deploy to Production

```bash
npm run deploy:prod
```

Your site will be deployed to a URL like:
`https://abc123.tamma-marketing-site.pages.dev`

## Next Steps

- **Configure custom domain:** See [DEPLOYMENT.md](./DEPLOYMENT.md#step-5-custom-domain-setup-optional)
- **Add analytics:** See [DEPLOYMENT.md](./DEPLOYMENT.md#step-6-analytics-setup)
- **Create logo:** Replace placeholder in `public/index.html`
- **Run performance audit:** Use Chrome Lighthouse

## Troubleshooting

### "Error: No namespace found for binding SIGNUPS"

**Solution:** Update `wrangler.toml` with your actual KV namespace ID from step 2.

### Email signup returns error

**Solution:** Check that you're using the preview KV namespace in dev mode:
```bash
wrangler kv:namespace create SIGNUPS --preview
# Update wrangler.toml [env.development.kv_namespaces] section
```

### Port 8788 already in use

**Solution:** Kill the existing process or use a different port:
```bash
wrangler pages dev public --port 8789
```

## Common Commands

```bash
# Development
npm run dev                # Start local server

# Deployment
npm run deploy             # Deploy to preview
npm run deploy:prod        # Deploy to production

# KV Management
npm run kv:list            # List all email signups
npm run logs               # View deployment logs

# Testing
npm run validate           # Run validation checks
```

## File Structure

```
marketing-site/
├── public/              # Static files (deployed)
│   ├── index.html      # Homepage
│   ├── privacy.html    # Privacy policy
│   ├── terms.html      # Terms of service
│   ├── styles.css      # Stylesheet
│   ├── robots.txt      # SEO
│   └── sitemap.xml     # SEO
├── functions/          # Serverless functions
│   └── signup.ts       # Email signup handler
├── wrangler.toml       # Cloudflare config
└── package.json        # Dependencies
```

## Support

- **Full Documentation:** [README.md](./README.md)
- **Deployment Guide:** [DEPLOYMENT.md](./DEPLOYMENT.md)
- **Implementation Details:** [IMPLEMENTATION_SUMMARY.md](./IMPLEMENTATION_SUMMARY.md)
- **Cloudflare Docs:** https://developers.cloudflare.com/pages/
- **GitHub Issues:** https://github.com/meywd/tamma/issues

---

**Ready to deploy?** Follow the [full deployment guide](./DEPLOYMENT.md) for production setup.
