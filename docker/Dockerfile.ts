# ------------------------------------------------------------------
# Dockerfile.ts  –  Multi-stage build for the Tamma TypeScript stack
#
# Build from the repo root:
#   docker build -f docker/Dockerfile.ts --target tamma-api  -t tamma-api .
#   docker build -f docker/Dockerfile.ts --target tamma-engine -t tamma-engine .
# ------------------------------------------------------------------

# ---- Stage 1: Dependencies ----
FROM node:22-alpine AS deps
RUN corepack enable && corepack prepare pnpm@latest --activate
WORKDIR /app

# Copy only lockfile + package manifests for layer caching
COPY pnpm-lock.yaml pnpm-workspace.yaml package.json ./
COPY packages/shared/package.json packages/shared/
COPY packages/platforms/package.json packages/platforms/
COPY packages/providers/package.json packages/providers/
COPY packages/orchestrator/package.json packages/orchestrator/
COPY packages/observability/package.json packages/observability/
COPY packages/api/package.json packages/api/
COPY packages/events/package.json packages/events/
COPY packages/cli/package.json packages/cli/
COPY packages/gates/package.json packages/gates/
COPY packages/intelligence/package.json packages/intelligence/
COPY packages/mcp-client/package.json packages/mcp-client/
COPY packages/scrum-master/package.json packages/scrum-master/
COPY packages/cost-monitor/package.json packages/cost-monitor/
COPY packages/workers/package.json packages/workers/

RUN pnpm install --frozen-lockfile

# ---- Stage 2: Build ----
FROM deps AS build
COPY . .
RUN pnpm --filter @tamma/cli... run build
RUN pnpm prune --prod

# ---- Stage 3a: API Server ----
FROM node:22-alpine AS tamma-api
RUN apk add --no-cache tini
RUN addgroup -g 1001 tamma && adduser -u 1001 -G tamma -s /bin/sh -D tamma
WORKDIR /app

COPY --from=build /app/node_modules ./node_modules
COPY --from=build /app/packages ./packages
COPY --from=build /app/package.json ./

USER tamma
EXPOSE 3100

RUN apk add --no-cache curl
HEALTHCHECK --interval=5s --timeout=3s --start-period=10s --retries=3 \
  CMD curl -sf http://localhost:3100/api/health || exit 1

ENTRYPOINT ["tini", "--"]
CMD ["node", "packages/api/dist/serve.js"]

# ---- Stage 3b: Engine ----
FROM node:22-alpine AS tamma-engine
RUN apk add --no-cache tini git
RUN addgroup -g 1001 tamma && adduser -u 1001 -G tamma -s /bin/sh -D tamma
WORKDIR /app

COPY --from=build /app/node_modules ./node_modules
COPY --from=build /app/packages ./packages
COPY --from=build /app/package.json ./

USER tamma

HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 \
  CMD test -f /tmp/tamma-engine-healthy || exit 1

ENTRYPOINT ["tini", "--"]
CMD ["node", "packages/cli/dist/index.js", "start", "--mode", "service"]
