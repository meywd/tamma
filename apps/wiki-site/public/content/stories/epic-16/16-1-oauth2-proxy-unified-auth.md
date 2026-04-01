---
title: "Story 16.1: OAuth2 Proxy Unified Authentication"
sidebar:
  order: 160
---

Status: ready-for-dev

## Story

As a **platform user**,
I want to log in once via GitHub and be authenticated across all Tamma dashboards (app.tamma.dev, elsa.tamma.dev, logs.tamma.dev),
so that I do not need separate credentials for each service and unauthorized users cannot access internal tools.

## Acceptance Criteria

1. An `oauth2-proxy` container runs in docker-compose, configured with the existing GitHub OAuth App (`GITHUB_OAUTH_CLIENT_ID` / `GITHUB_OAUTH_CLIENT_SECRET`)
2. The oauth2-proxy session cookie (`_oauth2_proxy`) is scoped to `.tamma.dev` domain, shared across all subdomains
3. Visiting `app.tamma.dev` without a valid session redirects to GitHub OAuth login via oauth2-proxy, then back to the dashboard
4. Visiting `elsa.tamma.dev` without a valid session redirects to the same GitHub OAuth flow, then back to ELSA Studio
5. Visiting `logs.tamma.dev` without a valid session redirects to the same GitHub OAuth flow, then back to OpenSearch Dashboards
6. After login on any subdomain, all other subdomains recognize the session without requiring re-login
7. ELSA Studio bypasses its own ELSA Identity login — the oauth2-proxy session is trusted as authentication
8. OpenSearch Dashboards is protected by oauth2-proxy without enabling the OpenSearch security plugin
9. The existing Tamma Dashboard GitHub OAuth flow (`/api/auth/github`) continues to work for issuing JWT tokens (used by the Tamma API for authorization), but the initial gate is oauth2-proxy
10. The `tamma_session` JWT cookie (existing) coexists with the `_oauth2_proxy` cookie — the proxy handles "who can access the page" while the JWT handles "who are you for API calls"
11. oauth2-proxy passes `X-Auth-Request-User`, `X-Auth-Request-Email`, and `X-Auth-Request-Groups` headers to upstream services
12. A `/oauth2/sign_out` endpoint clears the oauth2-proxy session and the `tamma_session` JWT cookie
13. Health check on the oauth2-proxy container passes before nginx routes traffic to it
14. All new environment variables are documented in `docker/.env.example`

## Technical Context

### How oauth2-proxy Works

oauth2-proxy is a reverse proxy that provides authentication using OAuth2 providers. It sits between nginx and the upstream service. Unauthenticated requests get redirected to the OAuth provider (GitHub). After authentication, the user gets a signed session cookie. Subsequent requests include this cookie and pass through to the upstream.

### Current Auth Flow (Before)

```
Browser --> nginx --> tamma-dashboard (serves React SPA)
                  --> tamma-api (SPA calls /api/auth/github for JWT)
```

### New Auth Flow (After)

```
Browser --> nginx --> oauth2-proxy --> tamma-dashboard
                                  --> tamma-api (still uses JWT for API auth)
                  --> oauth2-proxy --> elsa-studio (ELSA Identity bypassed)
                  --> oauth2-proxy --> opensearch-dashboards
```

### Files to Create

| File | Purpose |
|------|---------|
| `docker/oauth2-proxy.cfg` | oauth2-proxy configuration file |

### Files to Modify

| File | Change |
|------|--------|
| `docker/docker-compose.yml` | Add `oauth2-proxy` service |
| `docker/nginx-proxy.conf` | Add `auth_request` directives for all dashboard server blocks; add `logs.tamma.dev` server block; add `/oauth2/` location blocks |
| `docker/.env.example` | Add `OAUTH2_PROXY_COOKIE_SECRET`, `OAUTH2_PROXY_CLIENT_ID`, `OAUTH2_PROXY_CLIENT_SECRET` |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs` | Configure ELSA to trust proxy auth headers (skip Identity login when proxy headers present) |

## Implementation Plan

### Step 1: oauth2-proxy Configuration File

Create `docker/oauth2-proxy.cfg`:

```ini
## OAuth2 Proxy Configuration for Tamma

# Provider
provider = "github"
client_id = ""          # Set via env: OAUTH2_PROXY_CLIENT_ID
client_secret = ""      # Set via env: OAUTH2_PROXY_CLIENT_SECRET

# GitHub scopes — same as existing OAuth flow
scope = "user:email read:org"

# URLs
redirect_url = "https://app.tamma.dev/oauth2/callback"
cookie_domains = [".tamma.dev"]
whitelist_domains = [".tamma.dev"]

# Cookie settings
cookie_name = "_oauth2_proxy"
cookie_secure = true
cookie_httponly = true
cookie_samesite = "lax"
cookie_expire = "168h"    # 7 days
cookie_refresh = "1h"     # Refresh session every hour

# Session
cookie_secret = ""        # Set via env: OAUTH2_PROXY_COOKIE_SECRET (32-byte base64)

# Upstream — not used directly (nginx auth_request mode)
upstreams = ["static://202"]

# Headers to pass to upstream
set_xauthrequest = true
set_authorization_header = true
pass_access_token = true
pass_user_headers = true

# Email domains — allow all GitHub users (RBAC handled at app level)
email_domains = ["*"]

# Logging
logging_filename = ""     # Log to stdout
standard_logging = true
request_logging = true
auth_logging = true

# HTTP
http_address = "0.0.0.0:4180"

# Skip auth for health endpoints
skip_auth_routes = [
  "^/health$",
  "^/api/health$",
  "^/api/github/webhooks"
]
```

### Step 2: Docker Compose Service

Add to `docker/docker-compose.yml`:

```yaml
  # ---------------------------------------------------------------------------
  # OAuth2 Proxy (unified GitHub OAuth for all dashboards)
  # ---------------------------------------------------------------------------
  oauth2-proxy:
    image: quay.io/oauth2-proxy/oauth2-proxy:v7.7.1
    command:
      - --config=/etc/oauth2-proxy/oauth2-proxy.cfg
    environment:
      OAUTH2_PROXY_CLIENT_ID: ${GITHUB_OAUTH_CLIENT_ID}
      OAUTH2_PROXY_CLIENT_SECRET: ${GITHUB_OAUTH_CLIENT_SECRET}
      OAUTH2_PROXY_COOKIE_SECRET: ${OAUTH2_PROXY_COOKIE_SECRET:?OAUTH2_PROXY_COOKIE_SECRET is required}
    volumes:
      - ./oauth2-proxy.cfg:/etc/oauth2-proxy/oauth2-proxy.cfg:ro
    healthcheck:
      test: ["CMD", "wget", "-qO-", "http://localhost:4180/ping"]
      interval: 10s
      timeout: 5s
      retries: 3
    depends_on:
      tamma-api:
        condition: service_started
    networks:
      - tamma-net
```

### Step 3: nginx Configuration Changes

The key change is using nginx `auth_request` to delegate authentication to oauth2-proxy. Each dashboard server block gets:

1. An internal `/oauth2/` location that proxies to oauth2-proxy
2. An `auth_request /oauth2/auth` directive on the protected locations
3. Error handling to redirect 401 responses to the oauth2-proxy sign-in page

**Pattern for each server block:**

```nginx
    # oauth2-proxy endpoints
    location /oauth2/ {
        proxy_pass http://oauth2-proxy:4180;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto https;
        proxy_set_header X-Auth-Request-Redirect $request_uri;
    }

    location = /oauth2/auth {
        proxy_pass http://oauth2-proxy:4180;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto https;
        proxy_set_header Content-Length "";
        proxy_pass_request_body off;
    }
```

**For protected locations, add:**

```nginx
    location / {
        auth_request /oauth2/auth;
        auth_request_set $auth_user $upstream_http_x_auth_request_user;
        auth_request_set $auth_email $upstream_http_x_auth_request_email;
        error_page 401 = /oauth2/sign_in;

        # Pass user info to upstream
        proxy_set_header X-Auth-Request-User $auth_user;
        proxy_set_header X-Auth-Request-Email $auth_email;

        # ... existing proxy_pass directives ...
    }
```

**Add logs.tamma.dev server block** (this also addresses the routing gap from Story 15.1):

```nginx
# logs.tamma.dev — OpenSearch Dashboards (authenticated via oauth2-proxy)
server {
    listen 443 ssl;
    server_name logs.tamma.dev;

    # oauth2-proxy auth endpoints
    location /oauth2/ {
        proxy_pass http://oauth2-proxy:4180;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto https;
        proxy_set_header X-Auth-Request-Redirect $request_uri;
    }

    location = /oauth2/auth {
        proxy_pass http://oauth2-proxy:4180;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto https;
        proxy_set_header Content-Length "";
        proxy_pass_request_body off;
    }

    location / {
        auth_request /oauth2/auth;
        auth_request_set $auth_user $upstream_http_x_auth_request_user;
        auth_request_set $auth_email $upstream_http_x_auth_request_email;
        error_page 401 = /oauth2/sign_in;

        proxy_set_header X-Auth-Request-User $auth_user;
        proxy_set_header X-Auth-Request-Email $auth_email;

        proxy_pass http://opensearch-dashboards:5601;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto https;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_buffer_size 128k;
        proxy_buffers 4 256k;
        proxy_busy_buffers_size 256k;
    }
}
```

### Step 4: ELSA Studio Proxy Trust

ELSA Studio (Blazor WASM) runs client-side and calls the ELSA Server API. The ELSA Server uses its own `UseIdentity` + `UseDefaultAuthentication` (admin API key). Two approaches:

**Option A (Recommended): Proxy injects ELSA admin API key**

nginx adds the ELSA admin API key header to requests proxied to elsa-server, so the Studio operates as the admin user when the oauth2-proxy session is valid:

```nginx
    # elsa.tamma.dev — ELSA Server API (authenticated via oauth2-proxy, admin key injected)
    location /elsa/api/ {
        auth_request /oauth2/auth;
        error_page 401 = /oauth2/sign_in;

        proxy_pass http://elsa-server:5000/elsa/api/;
        proxy_set_header Authorization "ApiKey ${ELSA_ADMIN_API_KEY}";
        # ... other proxy headers ...
    }
```

This means: if you passed oauth2-proxy auth, you get ELSA admin access. RBAC for who can access elsa.tamma.dev at all is handled in Story 16.5.

**Option B: Custom ELSA auth handler**

Add a custom authentication handler to ELSA Server that trusts `X-Auth-Request-User` headers from oauth2-proxy. This is more complex and requires C# code changes.

Option A is simpler and sufficient for the current single-team setup.

### Step 5: Coexistence with Existing JWT Flow

The existing flow works like this:
1. User visits app.tamma.dev
2. Dashboard SPA redirects to `/api/auth/github` for JWT
3. API exchanges code for GitHub token, creates/updates user, issues JWT cookie (`tamma_session`)
4. SPA uses JWT cookie for API calls

With oauth2-proxy:
1. User visits app.tamma.dev
2. oauth2-proxy redirects to GitHub (if no `_oauth2_proxy` cookie)
3. User authenticates, gets `_oauth2_proxy` cookie, page loads
4. Dashboard SPA still calls `/api/auth/github` to get its JWT (`tamma_session` cookie)
5. Both cookies coexist — oauth2-proxy gates page access, JWT gates API authorization

The `/api/auth/github` and `/api/auth/github/callback` routes should be excluded from oauth2-proxy auth (they are API routes, not dashboard pages). Add them to `skip_auth_routes` or handle at the nginx level by not applying `auth_request` to `/api/` paths on `app.tamma.dev`.

### Step 6: Generate Cookie Secret

```bash
# Generate a 32-byte cookie secret
python3 -c 'import os,base64; print(base64.urlsafe_b64encode(os.urandom(32)).decode())'
# Add to .env as OAUTH2_PROXY_COOKIE_SECRET
```

## Logging Requirements

| Event | Level | Output | Notes |
|-------|-------|--------|-------|
| oauth2-proxy: successful authentication | INFO | stdout | Logged by oauth2-proxy natively |
| oauth2-proxy: failed authentication | WARN | stdout | Logged by oauth2-proxy natively |
| oauth2-proxy: session refresh | DEBUG | stdout | Logged by oauth2-proxy natively |
| nginx: auth_request subrequest failure | ERROR | nginx error log | Indicates oauth2-proxy is down |

### Sensitive Data Redaction

- oauth2-proxy logs email addresses by default. This is acceptable for an internal platform.
- The ELSA admin API key injected by nginx must never appear in logs. nginx does not log request headers by default, but verify `proxy_set_header Authorization` is not in the access log format.

## Testing Strategy

### Manual Verification

1. `docker compose up -d` and wait for all health checks (including oauth2-proxy) to pass
2. Open `https://app.tamma.dev` in an incognito browser — should redirect to GitHub login
3. Complete GitHub login — should redirect back to dashboard, both `_oauth2_proxy` and `tamma_session` cookies present
4. Open `https://elsa.tamma.dev` in the same browser — should load ELSA Studio without a separate login prompt
5. Open `https://logs.tamma.dev` in the same browser — should load OpenSearch Dashboards without login
6. Open `https://logs.tamma.dev` in a different incognito browser — should redirect to GitHub login
7. Visit `https://app.tamma.dev/oauth2/sign_out` — should clear session, subsequent visits require re-login
8. Verify `https://api.tamma.dev/api/github/webhooks` is accessible without oauth2-proxy auth (skip_auth_routes)

### Automated Checks

```bash
# oauth2-proxy is running
curl -sf http://oauth2-proxy:4180/ping

# Unauthenticated request returns 401 (via nginx auth_request)
curl -o /dev/null -s -w "%{http_code}" https://app.tamma.dev
# Expected: 302 (redirect to sign_in)

# Webhook endpoint bypasses auth
curl -o /dev/null -s -w "%{http_code}" https://api.tamma.dev/api/github/webhooks
# Expected: 200 or 405 (not 302/401)
```

## Dependencies

- No story dependencies (this is the foundation story for Epic 16)
- External: GitHub OAuth App (already exists), Cloudflare DNS for `logs.tamma.dev` (Story 15.1)
- Docker image: `quay.io/oauth2-proxy/oauth2-proxy:v7.7.1`

## Estimated Effort

| Task | Hours |
|------|-------|
| oauth2-proxy config file | 2 |
| Docker Compose service | 1 |
| nginx auth_request for app.tamma.dev | 2 |
| nginx auth_request for elsa.tamma.dev | 2 |
| nginx server block for logs.tamma.dev | 2 |
| ELSA admin API key injection | 1 |
| JWT coexistence verification | 2 |
| Environment variable documentation | 1 |
| Testing + troubleshooting | 3 |
| **Total** | **16 hours** |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-28 | 1.0 | Initial story creation | Architecture Team |
