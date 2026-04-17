# -----------------------------------------------------------------------------
# @tamma/intelligence-server Dockerfile
#
# Standalone Fastify sidecar that exposes @tamma/intelligence over HTTP so the
# C# API can delegate its knowledge-base operations without a native port.
#
# Build from the repo root:
#   docker build -f packages/intelligence-server/Dockerfile \
#                -t tamma/intelligence-server .
#
# Runs as a non-root user on port 4100 by default.
# -----------------------------------------------------------------------------

# ---- Stage 1: Dependencies (pnpm workspace) -------------------------------
FROM node:22-alpine AS deps
RUN corepack enable && corepack prepare pnpm@9.15.0 --activate
WORKDIR /app

# Copy only the manifests the intelligence-server needs. The sidecar itself
# does not import @tamma/intelligence at compile time (services use narrow
# adapter interfaces) so the set of workspace packages we must install for
# runtime is small: shared, observability, providers, intelligence.
COPY pnpm-lock.yaml pnpm-workspace.yaml package.json ./
COPY packages/shared/package.json packages/shared/
COPY packages/observability/package.json packages/observability/
COPY packages/providers/package.json packages/providers/
COPY packages/intelligence/package.json packages/intelligence/
COPY packages/intelligence-server/package.json packages/intelligence-server/

RUN pnpm install --frozen-lockfile --filter @tamma/intelligence-server...

# ---- Stage 2: Build -------------------------------------------------------
FROM deps AS build
# Root tsconfig.json is extended by every package (e.g. packages/shared's
# tsconfig has `"extends": "../../tsconfig.json"`). Without it, `tsc --build`
# errors with TS5083 "Cannot read file '/app/tsconfig.json'".
COPY tsconfig.json ./
COPY packages/shared packages/shared
COPY packages/observability packages/observability
COPY packages/providers packages/providers
COPY packages/intelligence packages/intelligence
COPY packages/intelligence-server packages/intelligence-server

# Build only the intelligence-server. It references @tamma/shared via
# `references` in tsconfig, so shared is built first. @tamma/intelligence is
# NOT required for compilation (sidecar uses narrow local interfaces), so we
# skip its pre-existing strict-mode build errors.
RUN pnpm --filter @tamma/shared --filter @tamma/intelligence-server run build

RUN pnpm prune --prod --filter @tamma/intelligence-server...

# ---- Stage 3: Runtime -----------------------------------------------------
FROM node:22-alpine AS runtime
RUN apk add --no-cache tini curl \
    && addgroup -g 1001 tamma \
    && adduser -u 1001 -G tamma -s /bin/sh -D tamma
WORKDIR /app

COPY --from=build --chown=tamma:tamma /app/node_modules ./node_modules
COPY --from=build --chown=tamma:tamma /app/packages ./packages
COPY --from=build --chown=tamma:tamma /app/package.json ./
COPY --from=build --chown=tamma:tamma /app/pnpm-workspace.yaml ./

USER tamma
ENV NODE_ENV=production \
    INTELLIGENCE_PORT=4100 \
    INTELLIGENCE_HOST=0.0.0.0

EXPOSE 4100

HEALTHCHECK --interval=15s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -fsS http://localhost:4100/health || exit 1

ENTRYPOINT ["/sbin/tini", "--"]
CMD ["node", "packages/intelligence-server/dist/server.js"]
