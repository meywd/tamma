---
title: "Story 14.2: Studio Docker & CI — Implementation Plan"
sidebar:
  order: 140
---

## Overview

Package the custom Tamma Studio (from Story 14.1) as a multi-stage Docker image served by nginx, with environment variable injection for the ELSA Server URL. Update docker-compose and CI workflow to build and deploy the custom image instead of the upstream `elsa-studio-v3-5`.

**Dependencies**: Story 14.1 must be complete (Tamma.Studio project exists and builds).

---

## Step-by-Step Implementation Tasks

### Step 1: Create the nginx Configuration

**File**: `apps/tamma-elsa/src/Tamma.Studio/nginx.conf`

```nginx
# Tamma Studio — nginx configuration
# Serves Blazor WASM static files with SPA routing, compression, and caching.

server {
    listen 80;
    server_name _;
    root /usr/share/nginx/html;
    index index.html;

    # -------------------------------------------------------------------------
    # SPA Routing: all non-file requests fall back to index.html
    # -------------------------------------------------------------------------
    location / {
        try_files $uri $uri/ /index.html;
    }

    # -------------------------------------------------------------------------
    # Compression: gzip for WASM, DLLs, JS, CSS, JSON, SVG
    # nginx:alpine does NOT include brotli by default — gzip only for MVP.
    # -------------------------------------------------------------------------
    gzip on;
    gzip_vary on;
    gzip_proxied any;
    gzip_comp_level 6;
    gzip_min_length 256;
    gzip_types
        application/wasm
        application/octet-stream
        application/javascript
        application/json
        text/css
        text/plain
        text/xml
        image/svg+xml;

    # -------------------------------------------------------------------------
    # Cache headers for immutable WASM/DLL/JS assets
    # -------------------------------------------------------------------------
    location ~* \.(wasm|dll)$ {
        expires 30d;
        add_header Cache-Control "public, immutable";
        gzip_static on;
    }

    location ~* \.(js|css)$ {
        expires 7d;
        add_header Cache-Control "public, immutable";
    }

    location ~* \.(png|svg|ico|woff|woff2|ttf|eot)$ {
        expires 30d;
        add_header Cache-Control "public";
    }

    # -------------------------------------------------------------------------
    # appsettings.json should NOT be cached (rewritten by entrypoint)
    # -------------------------------------------------------------------------
    location = /appsettings.json {
        expires -1;
        add_header Cache-Control "no-store, no-cache, must-revalidate";
    }

    # -------------------------------------------------------------------------
    # Security headers
    # -------------------------------------------------------------------------
    add_header X-Content-Type-Options "nosniff" always;
    add_header X-Frame-Options "SAMEORIGIN" always;
    add_header Referrer-Policy "strict-origin-when-cross-origin" always;
}
```

**Key decisions**:
- Port 80 matches the existing nginx-proxy expectation (proxy_pass to `elsa-studio:8080` in prod, but we will update docker-compose to use port 80).
- `gzip_static on` for `.wasm`/`.dll` means nginx will serve pre-compressed `.gz` files if they exist. `dotnet publish` may produce them with `<BlazorEnableCompression>true</BlazorEnableCompression>`.
- `appsettings.json` is explicitly uncached because the Docker entrypoint rewrites it at container start.

### Step 2: Create the Docker Entrypoint Script

**File**: `apps/tamma-elsa/src/Tamma.Studio/docker-entrypoint.sh`

```bash
#!/bin/sh
set -e

SETTINGS_FILE="/usr/share/nginx/html/appsettings.json"

# ---------------------------------------------------------------------------
# Inject ELSA Server URL into appsettings.json
#
# The placeholder "http://localhost:13000" is baked into the published WASM
# app. Replace it with the runtime ELSASERVER__URL env var.
# ---------------------------------------------------------------------------
if [ -n "$ELSASERVER__URL" ]; then
    echo "Injecting ElsaServer URL: $ELSASERVER__URL"
    # Use a temp file to avoid sed -i portability issues on alpine
    sed "s|http://localhost:13000|${ELSASERVER__URL}|g" "$SETTINGS_FILE" > "${SETTINGS_FILE}.tmp"
    mv "${SETTINGS_FILE}.tmp" "$SETTINGS_FILE"
else
    echo "WARNING: ELSASERVER__URL not set. Studio will try to connect to http://localhost:13000"
fi

echo "Starting nginx..."
exec "$@"
```

**Notes**:
- `set -e` ensures the container fails fast on sed errors.
- Uses `sed > tmp && mv` instead of `sed -i` because BusyBox sed on alpine has different `-i` behavior.
- Logs the injected URL for debugging (does not contain secrets).

### Step 3: Create the Multi-Stage Dockerfile

**File**: `apps/tamma-elsa/src/Tamma.Studio/Dockerfile`

```dockerfile
# =============================================================================
# Tamma Studio — Multi-stage Dockerfile
#
# Build context: apps/tamma-elsa/src  (same level as Tamma.Studio/ directory)
#
# Stage 1: dotnet SDK — restore, build, publish Blazor WASM
# Stage 2: nginx:alpine — serve static files (~30-40MB final image)
# =============================================================================

# ---------------------------------------------------------------------------
# Stage 1: Build
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy only the csproj for layer caching on restore
COPY Tamma.Studio/Tamma.Studio.csproj ./Tamma.Studio/

# Restore NuGet packages (cached unless csproj changes)
RUN dotnet restore Tamma.Studio/Tamma.Studio.csproj

# Copy full source
COPY Tamma.Studio/ ./Tamma.Studio/

# Publish in Release mode — produces wwwroot/ with all WASM assets
WORKDIR /src/Tamma.Studio
RUN dotnet publish Tamma.Studio.csproj \
    -c Release \
    -o /app/publish \
    /p:BlazorEnableCompression=true

# ---------------------------------------------------------------------------
# Stage 2: Runtime (nginx serving static files)
# ---------------------------------------------------------------------------
FROM nginx:1.27-alpine AS final

# Remove default nginx site
RUN rm -rf /usr/share/nginx/html/*

# Copy published Blazor WASM assets
COPY --from=build /app/publish/wwwroot /usr/share/nginx/html

# Copy nginx configuration
COPY Tamma.Studio/nginx.conf /etc/nginx/conf.d/default.conf

# Copy entrypoint script for env var injection
COPY Tamma.Studio/docker-entrypoint.sh /docker-entrypoint.sh
RUN chmod +x /docker-entrypoint.sh

# Expose port 80 (nginx default)
EXPOSE 80

# Entrypoint: inject env vars, then start nginx
ENTRYPOINT ["/docker-entrypoint.sh"]
CMD ["nginx", "-g", "daemon off;"]
```

**Build context note**: The Dockerfile expects build context to be `apps/tamma-elsa/src` (the parent of `Tamma.Studio/`). This matches how docker-compose references it:
```yaml
build:
  context: ../apps/tamma-elsa/src
  dockerfile: Tamma.Studio/Dockerfile
```

**Why not `apps/tamma-elsa` as context?** Unlike the ElsaServer Dockerfile which needs `src/` and `workflows/`, the Studio is a standalone Blazor WASM app with no project references. Using `src` as context keeps the build context smaller.

**Image size target**: nginx:1.27-alpine base (~7MB) + WASM assets (~15-30MB) = **~22-37MB total**.

### Step 4: Verify Docker Build Locally

```bash
# From repo root
docker build \
  -t tamma-studio:local \
  -f apps/tamma-elsa/src/Tamma.Studio/Dockerfile \
  apps/tamma-elsa/src

# Check image size
docker images tamma-studio:local

# Expected: ~25-40MB
```

### Step 5: Verify Docker Run and Env Var Injection

```bash
# Run without env var — should warn and use localhost:13000
docker run --rm -d -p 14000:80 --name studio-test tamma-studio:local
docker logs studio-test
# Should show: "WARNING: ELSASERVER__URL not set..."
curl -s http://localhost:14000/appsettings.json | grep "localhost:13000"
docker stop studio-test

# Run with env var — should inject custom URL
docker run --rm -d -p 14000:80 \
  -e ELSASERVER__URL=https://elsa.tamma.dev/elsa/api \
  --name studio-test tamma-studio:local
docker logs studio-test
# Should show: "Injecting ElsaServer URL: https://elsa.tamma.dev/elsa/api"
curl -s http://localhost:14000/appsettings.json | grep "elsa.tamma.dev"
docker stop studio-test
```

### Step 6: Verify SPA Routing

```bash
docker run --rm -d -p 14000:80 --name studio-test tamma-studio:local

# Root returns index.html
curl -sI http://localhost:14000/ | head -5
# Should return 200

# Deep path returns index.html (SPA routing)
curl -sI http://localhost:14000/workflows | head -5
# Should return 200 (not 404)

# Static file returns directly
curl -sI http://localhost:14000/appsettings.json | head -5
# Should return 200

# WASM files are gzipped
curl -sI -H "Accept-Encoding: gzip" http://localhost:14000/_framework/blazor.webassembly.js | grep -i content-encoding
# Should show: Content-Encoding: gzip

docker stop studio-test
```

### Step 7: Update docker-compose.yml

**File to modify**: `docker/docker-compose.yml`

**Replace** the existing `elsa-studio` service block:

```yaml
  # Current (REMOVE):
  elsa-studio:
    image: elsaworkflows/elsa-studio-v3-5:latest
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      # URL must be browser-accessible (Blazor WASM runs client-side)
      ELSASERVER__URL: https://elsa.tamma.dev/elsa/api
    depends_on:
      elsa-server:
        condition: service_healthy
    networks:
      - tamma-net
```

**With** (NEW):

```yaml
  # ---------------------------------------------------------------------------
  # ELSA Studio (Custom Tamma Blazor WASM UI via nginx)
  # ---------------------------------------------------------------------------
  elsa-studio:
    build:
      context: ../apps/tamma-elsa/src
      dockerfile: Tamma.Studio/Dockerfile
    environment:
      # URL must be browser-accessible (Blazor WASM runs client-side)
      ELSASERVER__URL: https://elsa.tamma.dev/elsa/api
    depends_on:
      elsa-server:
        condition: service_healthy
    networks:
      - tamma-net
```

**Changes**:
- `image:` replaced with `build:` (context + dockerfile)
- Removed `ASPNETCORE_ENVIRONMENT` (not needed; nginx serves static files, not ASP.NET)
- Kept `ELSASERVER__URL` (used by docker-entrypoint.sh)
- Kept `depends_on` and `networks`

### Step 8: Update docker-compose.override.yml (Dev)

**File to modify**: `docker/docker-compose.override.yml`

**Add** the elsa-studio port mapping:

```yaml
  elsa-studio:
    ports:
      - "14000:80"
```

Add this block after the `elsa-server` entry. This exposes the Studio on port 14000 for local development.

### Step 9: Update nginx-proxy.conf

**File to modify**: `docker/nginx-proxy.conf`

The nginx proxy currently routes `elsa.tamma.dev/` to `elsa-studio:8080`. The custom image serves on port 80.

**Change** the elsa-studio proxy_pass from port 8080 to port 80:

```nginx
    # BEFORE:
    # ELSA Studio UI (Blazor WASM on port 8080)
    location / {
        proxy_pass http://elsa-studio:8080;

    # AFTER:
    # ELSA Studio UI (Custom Tamma Blazor WASM via nginx on port 80)
    location / {
        proxy_pass http://elsa-studio:80;
```

The port 8080 was the upstream `elsa-studio-v3-5` image's default. Our custom image uses nginx on port 80.

### Step 10: Update docker-compose.prod.yml

**File to modify**: `docker/docker-compose.prod.yml`

**Add** resource limits for the custom elsa-studio service (after the `elsa-server` block):

```yaml
  elsa-studio:
    deploy:
      resources:
        limits:
          cpus: "0.25"
          memory: 128M
```

This is a static file server, so very low resources are needed. 128MB is generous for nginx serving ~30MB of static files.

### Step 11: Update CI Workflow — Build Matrix

**File to modify**: `.github/workflows/docker-publish.yml`

**Add** a new matrix entry to the `build-dotnet` job's `strategy.matrix.include` array:

```yaml
          - name: tamma-studio
            context: apps/tamma-elsa/src
            dockerfile: apps/tamma-elsa/src/Tamma.Studio/Dockerfile
```

Add this as the third entry after `tamma-api-dotnet`. The full matrix becomes:

```yaml
    strategy:
      matrix:
        include:
          - name: tamma-elsa
            context: apps/tamma-elsa
            dockerfile: apps/tamma-elsa/src/Tamma.ElsaServer/Dockerfile
          - name: tamma-api-dotnet
            context: apps/tamma-elsa/src
            dockerfile: apps/tamma-elsa/src/Tamma.Api/Dockerfile
          - name: tamma-studio
            context: apps/tamma-elsa/src
            dockerfile: apps/tamma-elsa/src/Tamma.Studio/Dockerfile
```

### Step 12: Update CI Workflow — docker-compose.images.yml

**In the deploy job**, the `Create docker-compose override for GHCR images` step generates `docker-compose.images.yml`.

**Add** the `elsa-studio` service entry to the YAML heredoc:

```yaml
            elsa-studio:
              image: ghcr.io/${OWNER}/tamma-studio:${IMAGE_TAG}
              build: !reset null
```

The full YAML block after the change:

```yaml
          services:
            tamma-api:
              image: ghcr.io/${OWNER}/tamma-api:${IMAGE_TAG}
              build: !reset null
            tamma-engine:
              image: ghcr.io/${OWNER}/tamma-engine:${IMAGE_TAG}
              build: !reset null
            tamma-dashboard:
              image: ghcr.io/${OWNER}/tamma-dashboard:${IMAGE_TAG}
              build: !reset null
            elsa-server:
              image: ghcr.io/${OWNER}/tamma-elsa:${IMAGE_TAG}
              build: !reset null
            tamma-api-dotnet:
              image: ghcr.io/${OWNER}/tamma-api-dotnet:${IMAGE_TAG}
              build: !reset null
            elsa-studio:
              image: ghcr.io/${OWNER}/tamma-studio:${IMAGE_TAG}
              build: !reset null
```

### Step 13: Verify docker-compose Build

```bash
cd docker
docker compose build elsa-studio
# Should build successfully using the new Dockerfile
```

### Step 14: Verify Full Stack with docker-compose

```bash
cd docker
docker compose up -d postgres rabbitmq
# Wait for healthy
docker compose up -d elsa-server
# Wait for healthy
docker compose up -d elsa-studio

# Check Studio is running
curl -sI http://localhost:14000/
# Should return 200

# Check env var injection
docker compose exec elsa-studio cat /usr/share/nginx/html/appsettings.json
# Should show the injected ELSASERVER__URL
```

---

## Files to Create

| # | Path | Description |
|---|------|-------------|
| 1 | `apps/tamma-elsa/src/Tamma.Studio/Dockerfile` | Multi-stage build: dotnet SDK -> nginx:alpine |
| 2 | `apps/tamma-elsa/src/Tamma.Studio/nginx.conf` | SPA routing, gzip, cache headers |
| 3 | `apps/tamma-elsa/src/Tamma.Studio/docker-entrypoint.sh` | Env var injection into appsettings.json |

## Files to Modify

| # | Path | Change |
|---|------|--------|
| 1 | `docker/docker-compose.yml` | Replace `image: elsaworkflows/elsa-studio-v3-5:latest` with `build:` block |
| 2 | `docker/docker-compose.override.yml` | Add `elsa-studio` port mapping (`14000:80`) |
| 3 | `docker/docker-compose.prod.yml` | Add `elsa-studio` resource limits |
| 4 | `docker/nginx-proxy.conf` | Change `elsa-studio:8080` to `elsa-studio:80` |
| 5 | `.github/workflows/docker-publish.yml` | Add `tamma-studio` to build-dotnet matrix + images.yml |

---

## Docker Build Verification Steps

These are the commands to run (in order) to fully verify the Docker integration:

```bash
# 1. Build the image standalone
docker build \
  -t tamma-studio:test \
  -f apps/tamma-elsa/src/Tamma.Studio/Dockerfile \
  apps/tamma-elsa/src

# 2. Check image size (should be < 50MB)
docker images tamma-studio:test --format "{{.Size}}"

# 3. Run container and verify nginx starts
docker run --rm -d -p 14000:80 -e ELSASERVER__URL=http://test:5000 --name studio-verify tamma-studio:test

# 4. Verify entrypoint injection
docker exec studio-verify cat /usr/share/nginx/html/appsettings.json
# Must contain "http://test:5000", NOT "http://localhost:13000"

# 5. Verify SPA routing
curl -sI http://localhost:14000/workflows
# Must return HTTP 200 (not 404)

# 6. Verify gzip on WASM
curl -sI -H "Accept-Encoding: gzip" http://localhost:14000/_framework/blazor.webassembly.js
# Must include Content-Encoding: gzip

# 7. Verify cache headers on WASM
curl -sI http://localhost:14000/_framework/blazor.webassembly.js | grep -i cache-control
# Must include "immutable"

# 8. Verify appsettings.json is NOT cached
curl -sI http://localhost:14000/appsettings.json | grep -i cache-control
# Must include "no-store" or "no-cache"

# 9. Cleanup
docker stop studio-verify

# 10. Verify docker-compose build
cd docker
docker compose build elsa-studio
```

---

## Risks and Edge Cases

### 1. Build Context Path Mismatch

The Dockerfile expects `apps/tamma-elsa/src` as build context. If docker-compose specifies a different context, COPY commands will fail.

**Mitigation**: The `docker-compose.yml` sets `context: ../apps/tamma-elsa/src` (relative to `docker/`). Verify this resolves correctly.

### 2. Port Change Breaking nginx-proxy

Changing from port 8080 to 80 in `nginx-proxy.conf` must be done atomically with the docker-compose change. If the proxy config is deployed before the new Studio image, requests will fail.

**Mitigation**: Deploy both changes in the same commit/PR. The CI workflow deploys all files via rsync before starting containers.

### 3. dotnet publish Produces Different Output Structure

Different Blazor WASM SDK versions produce different publish output structures. The Dockerfile expects `wwwroot/` at `/app/publish/wwwroot`. If the SDK puts files elsewhere, the COPY will miss assets.

**Mitigation**: The `dotnet publish` step uses `-o /app/publish`. For Blazor WASM, the SDK always outputs to `<output>/wwwroot/`. Verify after Step 4.

### 4. WASM Files Not Compressed

If `BlazorEnableCompression` does not produce `.gz` files, nginx's `gzip_static` will not find pre-compressed files and will compress on-the-fly (slower for large `.wasm` files).

**Mitigation**: On-the-fly gzip is acceptable. Pre-compressed files are a nice-to-have optimization. Check `ls /app/publish/wwwroot/_framework/*.gz` in the build stage to verify.

### 5. ELSASERVER__URL Must Be Browser-Accessible

The injected URL is used by Blazor WASM running in the user's browser, NOT by the container itself. The URL must be publicly accessible (e.g., `https://elsa.tamma.dev/elsa/api`), not an internal Docker hostname like `http://elsa-server:5000`.

**Mitigation**: The docker-compose.yml sets `ELSASERVER__URL: https://elsa.tamma.dev/elsa/api` which is the public Cloudflare-fronted URL. For local dev, override in docker-compose.override.yml or set explicitly.

### 6. CI Workflow YAML Syntax

Adding matrix entries to the GitHub Actions workflow requires careful YAML indentation. A syntax error will break all builds.

**Mitigation**: Validate with `yq` or `python -c "import yaml; yaml.safe_load(open('.github/workflows/docker-publish.yml'))"` before committing.

### 7. Concurrent Deployment of Old and New

During the transition, the VPS may briefly run the old `elsa-studio-v3-5` image. The CI deploy step does `docker compose down` then `docker compose up`, so there is no overlap. But if the old image is cached and the new one fails to pull, the old image may be used.

**Mitigation**: The `docker-compose.images.yml` override forces the specific image tag. If the new image is not available, the pull step will fail and the deploy will abort before starting containers.

---

## Verification Checklist

- [ ] Dockerfile builds successfully (`docker build` exits 0)
- [ ] Image size < 50MB
- [ ] Container starts and nginx serves on port 80
- [ ] `ELSASERVER__URL` env var is injected into `appsettings.json`
- [ ] Missing `ELSASERVER__URL` logs a warning (does not crash)
- [ ] SPA routing: `/workflows` returns `index.html` (not 404)
- [ ] gzip: `.wasm` files served with `Content-Encoding: gzip`
- [ ] Cache: `.wasm`/`.dll` have `Cache-Control: public, immutable`
- [ ] Cache: `appsettings.json` has `Cache-Control: no-store`
- [ ] `docker-compose.yml` change is backward compatible
- [ ] `docker-compose build elsa-studio` succeeds
- [ ] `docker-compose up` starts Studio connected to ELSA Server
- [ ] nginx-proxy correctly routes `elsa.tamma.dev` to Studio on port 80
- [ ] CI workflow YAML is syntactically valid
- [ ] CI build-dotnet matrix includes tamma-studio
- [ ] docker-compose.images.yml includes elsa-studio image reference
