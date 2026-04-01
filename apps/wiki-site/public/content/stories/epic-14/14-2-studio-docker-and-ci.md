---
title: "Story 14.2: Studio Docker & CI"
sidebar:
  order: 140
---

Status: ready-for-dev

## Story

As a **DevOps engineer**,
I want the custom Tamma Studio packaged as a Docker image served by nginx with CI/CD integration for automated builds,
so that the Studio deploys alongside the ELSA Server in docker-compose with environment variable injection for server URL configuration, and new builds are automatically pushed to GHCR.

## Acceptance Criteria

1. Multi-stage Dockerfile exists: `dotnet sdk` build stage produces WASM static files, `nginx:alpine` runtime stage serves them (~30MB final image)
2. `nginx.conf` configures: SPA routing (fallback to `index.html`), gzip/Brotli compression for `.wasm`, `.dll`, `.js` files, cache headers for static assets
3. `docker-entrypoint.sh` runs `envsubst` to inject `ELSASERVER__URL` environment variable into `appsettings.json` before nginx starts
4. `docker-compose.yml` in `docker/` is updated: replace `image: elsaworkflows/elsa-studio-v3-5:latest` with `build:` pointing to the new Dockerfile
5. Studio container depends on the ELSA Server container (health check dependency)
6. `ELSASERVER__URL` is configurable via docker-compose environment variables
7. `.github/workflows/docker-publish.yml` updated: add Tamma Studio to the build matrix (name: `tamma-studio`, context, dockerfile)
8. `docker-compose.images.yml` updated: `elsa-studio: image: ghcr.io/.../tamma-studio:${TAG}`
9. Docker image builds successfully via `docker build`
10. Container starts and serves the Studio at the configured port
11. Environment variable injection works: changing `ELSASERVER__URL` changes the server the Studio connects to

## Technical Context

### Multi-Stage Dockerfile

```dockerfile
# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Tamma.Studio/Tamma.Studio.csproj ./Tamma.Studio/
RUN dotnet restore Tamma.Studio/Tamma.Studio.csproj
COPY Tamma.Studio/ ./Tamma.Studio/
RUN dotnet publish Tamma.Studio/Tamma.Studio.csproj -c Release -o /app/publish

# Runtime stage
FROM nginx:alpine
COPY --from=build /app/publish/wwwroot /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY docker-entrypoint.sh /docker-entrypoint.sh
RUN chmod +x /docker-entrypoint.sh
ENTRYPOINT ["/docker-entrypoint.sh"]
CMD ["nginx", "-g", "daemon off;"]
```

### nginx.conf

```nginx
server {
    listen 80;
    root /usr/share/nginx/html;
    index index.html;

    # SPA routing
    location / {
        try_files $uri $uri/ /index.html;
    }

    # Compression for WASM assets
    gzip on;
    gzip_types application/wasm application/octet-stream application/javascript text/css;
    gzip_min_length 1000;

    # Cache headers
    location ~* \.(wasm|dll|js|css|png|svg|ico)$ {
        expires 7d;
        add_header Cache-Control "public, immutable";
    }
}
```

### docker-entrypoint.sh

```bash
#!/bin/sh
# Inject ELSA Server URL into appsettings.json
SETTINGS_FILE="/usr/share/nginx/html/appsettings.json"
if [ -n "$ELSASERVER__URL" ]; then
    sed -i "s|http://localhost:13000|${ELSASERVER__URL}|g" "$SETTINGS_FILE"
fi
exec "$@"
```

### Files to Create

- `apps/tamma-elsa/src/Tamma.Studio/Dockerfile`
- `apps/tamma-elsa/src/Tamma.Studio/nginx.conf`
- `apps/tamma-elsa/src/Tamma.Studio/docker-entrypoint.sh`

### Files to Modify

- `docker/docker-compose.yml` — replace upstream ELSA Studio image with custom build
- `.github/workflows/docker-publish.yml` — add Tamma Studio to build matrix
- `docker/docker-compose.images.yml` (if exists) — add tamma-studio image reference

### docker-compose Integration

```yaml
elsa-studio:
  build:
    context: ../apps/tamma-elsa/src
    dockerfile: Tamma.Studio/Dockerfile
  environment:
    - ELSASERVER__URL=http://elsa-server:13000
  ports:
    - "14000:80"
  depends_on:
    elsa-server:
      condition: service_healthy
```

## Implementation Notes

1. Build the Docker image locally first: `docker build -t tamma-studio -f apps/tamma-elsa/src/Tamma.Studio/Dockerfile apps/tamma-elsa/src/`. Verify the image builds and runs.
2. Test env var injection: `docker run -e ELSASERVER__URL=http://custom-server:13000 -p 14000:80 tamma-studio`. Inspect `/usr/share/nginx/html/appsettings.json` inside the container to verify the URL was replaced.
3. Brotli compression: nginx:alpine may not include the Brotli module by default. Gzip is sufficient for MVP. Brotli can be added later via a custom nginx build.
4. The `docker-compose.yml` change should be backward compatible: developers who already use docker-compose will get the new Studio after a `docker-compose build`.
5. CI integration: the build matrix entry should match the pattern used by existing services (check the current `docker-publish.yml` for the matrix structure).
6. Image size target: under 50MB. The nginx:alpine base is ~5MB, WASM assets are 15-30MB.

## Testing Strategy

- **Docker build**: `docker build` succeeds, produces image under 50MB
- **Container start**: Container starts and nginx serves the app on port 80
- **Env var injection**: `ELSASERVER__URL` changes the server URL in `appsettings.json`
- **SPA routing**: Direct navigation to `/workflows` returns `index.html` (not 404)
- **Compression**: `.wasm` files are served with `Content-Encoding: gzip`
- **docker-compose**: `docker-compose up --build` starts the Studio connected to the ELSA Server
- **CI verification**: The GitHub workflow file is syntactically valid and the matrix entry is correctly formatted

## Dependencies

- **Story 14.1** (Studio Blazor WASM Scaffold) — the Studio project must exist before it can be Dockerized

## Estimated Effort

2-3 days

## Logging Requirements

### Existing Coverage

The story has **no logging requirements** specified. This is a Docker/CI story with no application code, but the `docker-entrypoint.sh` and nginx access logs need consideration.

### Required Additions

This story produces infrastructure (Dockerfile, nginx, entrypoint script) rather than C# code. Logging is shell-level and nginx-level.

| Event | Level | Output | Notes |
|-------|-------|--------|-------|
| Entrypoint: env var injection performed | stdout | `ELSASERVER__URL` value injected (print the URL, not any secret) | `echo` in `docker-entrypoint.sh` confirming the URL was replaced |
| Entrypoint: env var not set, using default | stdout | Warning that `ELSASERVER__URL` was not set | Helps debug "Studio can't connect to server" issues |
| nginx access log | nginx default | Standard combined format | `access_log /var/log/nginx/access.log;` — leave default nginx logging enabled |
| nginx error log | nginx default | Standard error format | `error_log /var/log/nginx/error.log warn;` — set to warn level to catch 404s and upstream errors |

### Sensitive Data Redaction

- The `ELSASERVER__URL` is not a secret (it is a URL). Safe to print in entrypoint.
- nginx access logs may contain query parameters — ensure no API keys are passed as query params to the Studio.

### Correlation IDs

- Not applicable for this story (no workflow correlation in a static file server).

### Note on Log Priority

This story has **low logging priority**. The entrypoint `echo` and nginx access/error logs are sufficient for debugging container startup and connectivity issues.

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-28 | 1.0 | Initial story creation from `.dev/plans/elsa-studio-customization.md` Phases 3+4 | Architecture Team |
| 2026-03-28 | 1.1 | Added Logging Requirements section | Logging Audit |
