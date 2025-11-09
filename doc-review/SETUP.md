# Setup Guide: Tamma Documentation Review Site

This guide walks you through setting up the documentation review site from scratch.

## Prerequisites

- Node.js 22+ LTS installed
- pnpm 9+ installed (`npm install -g pnpm`)
- Cloudflare account (https://dash.cloudflare.com/sign-up)
- Git installed

## Step 1: Install Dependencies

From the `doc-review` directory:

```bash
cd doc-review
pnpm install
```

This will install all required packages including:
- React Router v7
- Cloudflare Workers types
- Drizzle ORM
- React and related libraries
- Development tools

## Step 2: Cloudflare Account Setup

### 2.1 Login to Wrangler

```bash
pnpm wrangler login
```

This opens a browser window to authenticate with Cloudflare.

### 2.2 Get Your Account ID

```bash
pnpm wrangler whoami
```

Note your `Account ID` - you'll need it later.

## Step 3: Create Cloudflare Resources

### 3.1 Create D1 Database

```bash
pnpm wrangler d1 create tamma-docs
```

Output will show:
```
✅ Successfully created DB 'tamma-docs'
Created your database using D1's new storage backend.

[[d1_databases]]
binding = "DB"
database_name = "tamma-docs"
database_id = "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
```

**Copy the `database_id`** and update `wrangler.toml`:

```toml
[[d1_databases]]
binding = "DB"
database_name = "tamma-docs"
database_id = "YOUR_ACTUAL_DATABASE_ID_HERE"  # Replace this
```

### 3.2 Create KV Namespace

```bash
pnpm wrangler kv namespace create CACHE
```

Output will show:
```
 ⛅️ wrangler 3.x.x
-------------------
🌀 Creating namespace with title "tamma-doc-review-CACHE"
✨ Success!
Add the following to your configuration file:
kv_namespaces = [
  { binding = "CACHE", id = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" }
]
```

**Copy the `id`** and update `wrangler.toml`:

```toml
[[kv_namespaces]]
binding = "CACHE"
id = "YOUR_ACTUAL_KV_ID_HERE"  # Replace this
```

### 3.3 (Optional) Create R2 Bucket for Attachments

```bash
pnpm wrangler r2 bucket create tamma-attachments
```

Update `wrangler.toml` to uncomment:

```toml
[[r2_buckets]]
binding = "STORAGE"
bucket_name = "tamma-attachments"
```

## Step 4: Initialize Database

### 4.1 Run Migrations (Local)

For local development:

```bash
pnpm wrangler d1 execute tamma-docs --local --file=./db/schema.sql
```

### 4.2 Run Migrations (Production)

For production database:

```bash
pnpm wrangler d1 execute tamma-docs --file=./db/schema.sql
```

### 4.3 Verify Database

Check local database:

```bash
pnpm wrangler d1 execute tamma-docs --local --command "SELECT * FROM users"
```

Check production database:

```bash
pnpm wrangler d1 execute tamma-docs --command "SELECT * FROM users"
```

You should see the default admin user.

## Step 5: Configure Authentication

### Option A: Cloudflare Access (Recommended)

#### 5.1 Create Access Application

1. Go to https://one.dash.cloudflare.com/
2. Navigate to **Zero Trust** → **Access** → **Applications**
3. Click **Add an application** → **Self-hosted**
4. Configure:
   - **Application name**: `Tamma Doc Review`
   - **Session duration**: `24 hours`
   - **Application domain**: `doc-review.yourdomain.workers.dev` (or custom domain)
   - **Identity providers**: Enable GitHub, Google, or Microsoft

#### 5.2 Set Access Policy

Create a policy:
- **Policy name**: `Tamma Team`
- **Action**: `Allow`
- **Include**:
  - **Email ending in**: `@yourcompany.com` (or specific emails)
  - OR **Emails**: Add individual emails

#### 5.3 Get Application AUD

1. Click on your application
2. Copy the **Application Audience (AUD) Tag**
3. Set as Wrangler secret:

```bash
pnpm wrangler secret put CF_ACCESS_AUD
# Paste your AUD value when prompted
```

### Option B: Custom Auth (Advanced)

If not using Cloudflare Access, you'll need to implement custom authentication in `app/lib/auth/auth.server.ts`.

## Step 6: Development Environment

### 6.1 Set Environment Variables for Migrations

Create `.env` file (DO NOT commit this):

```bash
CLOUDFLARE_ACCOUNT_ID="your-account-id-from-step-2"
CLOUDFLARE_DATABASE_ID="your-database-id-from-step-3"
CLOUDFLARE_D1_TOKEN="your-api-token"
```

To get API token:
1. Go to https://dash.cloudflare.com/profile/api-tokens
2. Create Token → **Edit Cloudflare Workers** template
3. Add **D1 Edit** permission
4. Copy token and add to `.env`

### 6.2 Start Development Server

```bash
pnpm dev
```

Visit http://localhost:5173

The app will run with:
- Local D1 database
- Local KV storage
- Hot Module Replacement (HMR)
- Cloudflare Workers runtime simulation

## Step 7: Build and Deploy

### 7.1 Build for Production

```bash
pnpm build
```

This creates optimized bundles in `build/client` and `build/server`.

### 7.2 Preview Production Build

```bash
pnpm preview
```

Test the production build locally before deploying.

### 7.3 Deploy to Cloudflare

```bash
pnpm deploy
```

Your app will be deployed to:
- **Workers URL**: `https://tamma-doc-review.your-account.workers.dev`
- **Custom domain** (if configured)

### 7.4 Set Production Secrets

Don't forget to set secrets in production:

```bash
pnpm wrangler secret put CF_ACCESS_AUD
# Enter production AUD value
```

## Step 8: Verify Deployment

### 8.1 Check Deployment

```bash
pnpm wrangler pages deployments list
```

### 8.2 View Logs

```bash
pnpm wrangler pages deployment tail
```

### 8.3 Test Application

1. Visit your deployed URL
2. You should be redirected to Cloudflare Access login
3. Authenticate with configured provider
4. Access the documentation review interface

## Step 9: Configure Custom Domain (Optional)

### 9.1 Add Custom Domain

```bash
pnpm wrangler pages project create tamma-doc-review
```

### 9.2 Set up DNS

1. Go to Cloudflare Dashboard → Your domain → DNS
2. Add CNAME record:
   - **Name**: `docs-review` (or your choice)
   - **Target**: `tamma-doc-review.pages.dev`
   - **Proxy status**: Proxied (orange cloud)

### 9.3 Update Access Application

Update your Cloudflare Access application to use the custom domain.

## Step 10: Monitoring and Maintenance

### 10.1 View Analytics

```bash
pnpm wrangler pages deployment list
```

Or visit Cloudflare Dashboard → Pages → Your project → Analytics

### 10.2 Database Backups

```bash
# Export database
pnpm wrangler d1 export tamma-docs --output=backup.sql

# Import database
pnpm wrangler d1 execute tamma-docs --file=backup.sql
```

### 10.3 Check Logs

```bash
pnpm wrangler pages deployment tail --format=pretty
```

## Troubleshooting

### Issue: "Database not found"

**Solution**: Make sure you updated `wrangler.toml` with the correct `database_id`.

### Issue: "KV namespace not found"

**Solution**: Verify the KV `id` in `wrangler.toml` matches the created namespace.

### Issue: "Authentication failed"

**Solution**:
1. Check that `CF_ACCESS_AUD` secret is set correctly
2. Verify Cloudflare Access application is configured
3. Ensure your email is in the access policy

### Issue: "Module not found" errors

**Solution**: Run `pnpm install` to ensure all dependencies are installed.

### Issue: "Type errors" during build

**Solution**: Run `pnpm typecheck` to see detailed errors.

## Development Workflow

### Making Changes

1. Create a feature branch:
   ```bash
   git checkout -b feature/your-feature
   ```

2. Make changes and test locally:
   ```bash
   pnpm dev
   ```

3. Run type checking:
   ```bash
   pnpm typecheck
   ```

4. Format code:
   ```bash
   pnpm format
   ```

5. Commit and push:
   ```bash
   git add .
   git commit -m "Add your feature"
   git push origin feature/your-feature
   ```

6. Deploy to preview:
   ```bash
   pnpm deploy
   ```

### Database Migrations

When schema changes:

1. Update `app/lib/db/schema.ts`
2. Generate migration:
   ```bash
   pnpm db:generate
   ```
3. Review migration in `db/migrations/`
4. Apply locally:
   ```bash
   pnpm db:migrate:local
   ```
5. Test thoroughly
6. Apply to production:
   ```bash
   pnpm db:migrate
   ```

## Next Steps

Now that setup is complete, you can:

1. **Customize the UI**: Edit components in `app/components/`
2. **Add routes**: Create new files in `app/routes/`
3. **Implement features**: Follow the architecture in `ARCHITECTURE.md`
4. **Configure CI/CD**: Set up GitHub Actions for automated deployments
5. **Add team members**: Configure Cloudflare Access policies

## Resources

- [React Router v7 Docs](https://reactrouter.com)
- [Cloudflare Workers Docs](https://developers.cloudflare.com/workers/)
- [Cloudflare D1 Docs](https://developers.cloudflare.com/d1/)
- [Cloudflare Access Docs](https://developers.cloudflare.com/cloudflare-one/applications/)
- [Drizzle ORM Docs](https://orm.drizzle.team)

## Support

For questions or issues:
- Check `ARCHITECTURE.md` for design details
- Review `README.md` for feature overview
- Check Cloudflare documentation
- Contact development team

---

**Happy coding! 🚀**
