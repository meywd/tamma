# Task 3: Docker Configuration

## Overview

Implement comprehensive Docker configuration for the Tamma platform with multi-stage builds, optimized images, and container orchestration support.

## Objectives

- Create production-ready Docker images for all services
- Implement multi-stage builds for optimal image sizes
- Configure Docker Compose for local development
- Set up container health checks and monitoring
- Implement proper networking and volume management

## Implementation Steps

### Step 1: Base Dockerfile Setup

Create optimized multi-stage Dockerfiles:

```dockerfile
# packages/cli/Dockerfile
FROM node:22-alpine AS base
WORKDIR /app
COPY package*.json ./
COPY pnpm-lock.yaml ./
RUN npm install -g pnpm@9
RUN pnpm install --frozen-lockfile

FROM base AS builder
COPY . .
RUN pnpm build

FROM base AS runtime
RUN addgroup -g 1001 -S nodejs
RUN adduser -S tamma -u 1001
COPY --from=builder /app/dist ./dist
COPY --from=builder /app/node_modules ./node_modules
COPY --from=builder /app/package.json ./package.json
USER tamma
EXPOSE 3000
CMD ["node", "dist/index.js"]
```

```dockerfile
# packages/orchestrator/Dockerfile
FROM node:22-alpine AS base
WORKDIR /app
COPY package*.json ./
COPY pnpm-lock.yaml ./
RUN npm install -g pnpm@9
RUN pnpm install --frozen-lockfile

FROM base AS builder
COPY . .
RUN pnpm build

FROM base AS runtime
RUN addgroup -g 1001 -S nodejs
RUN adduser -S tamma -u 1001
COPY --from=builder /app/dist ./dist
COPY --from=builder /app/node_modules ./node_modules
COPY --from=builder /app/package.json ./package.json
USER tamma
EXPOSE 3001
CMD ["node", "dist/index.js"]
```

### Step 2: Docker Compose Configuration

Create comprehensive Docker Compose setup:

```yaml
# docker-compose.yml
version: '3.8'

services:
  postgres:
    image: postgres:17-alpine
    container_name: tamma-postgres
    environment:
      POSTGRES_DB: tamma
      POSTGRES_USER: tamma
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
    volumes:
      - postgres_data:/var/lib/postgresql/data
      - ./database/init:/docker-entrypoint-initdb.d
    ports:
      - '5432:5432'
    healthcheck:
      test: ['CMD-SHELL', 'pg_isready -U tamma']
      interval: 10s
      timeout: 5s
      retries: 5
    networks:
      - tamma-network

  redis:
    image: redis:7-alpine
    container_name: tamma-redis
    command: redis-server --appendonly yes --requirepass ${REDIS_PASSWORD}
    volumes:
      - redis_data:/data
    ports:
      - '6379:6379'
    healthcheck:
      test: ['CMD', 'redis-cli', '--raw', 'incr', 'ping']
      interval: 10s
      timeout: 3s
      retries: 5
    networks:
      - tamma-network

  orchestrator:
    build:
      context: .
      dockerfile: packages/orchestrator/Dockerfile
    container_name: tamma-orchestrator
    environment:
      NODE_ENV: production
      DATABASE_URL: postgresql://tamma:${POSTGRES_PASSWORD}@postgres:5432/tamma
      REDIS_URL: redis://:${REDIS_PASSWORD}@redis:6379
      LOG_LEVEL: info
    depends_on:
      postgres:
        condition: service_healthy
      redis:
        condition: service_healthy
    ports:
      - '3001:3001'
    volumes:
      - ./logs:/app/logs
      - ./config:/app/config:ro
    healthcheck:
      test: ['CMD', 'wget', '--no-verbose', '--tries=1', '--spider', 'http://localhost:3001/health']
      interval: 30s
      timeout: 10s
      retries: 3
    networks:
      - tamma-network
    restart: unless-stopped

  workers:
    build:
      context: .
      dockerfile: packages/workers/Dockerfile
    container_name: tamma-workers
    environment:
      NODE_ENV: production
      DATABASE_URL: postgresql://tamma:${POSTGRES_PASSWORD}@postgres:5432/tamma
      REDIS_URL: redis://:${REDIS_PASSWORD}@redis:6379
      WORKER_CONCURRENCY: 4
    depends_on:
      postgres:
        condition: service_healthy
      redis:
        condition: service_healthy
    volumes:
      - ./logs:/app/logs
      - ./config:/app/config:ro
    networks:
      - tamma-network
    restart: unless-stopped
    deploy:
      replicas: 2

  api:
    build:
      context: .
      dockerfile: packages/api/Dockerfile
    container_name: tamma-api
    environment:
      NODE_ENV: production
      DATABASE_URL: postgresql://tamma:${POSTGRES_PASSWORD}@postgres:5432/tamma
      REDIS_URL: redis://:${REDIS_PASSWORD}@redis:6379
      API_PORT: 3000
    depends_on:
      postgres:
        condition: service_healthy
      redis:
        condition: service_healthy
    ports:
      - '3000:3000'
    volumes:
      - ./logs:/app/logs
      - ./config:/app/config:ro
    healthcheck:
      test: ['CMD', 'wget', '--no-verbose', '--tries=1', '--spider', 'http://localhost:3000/health']
      interval: 30s
      timeout: 10s
      retries: 3
    networks:
      - tamma-network
    restart: unless-stopped

  dashboard:
    build:
      context: .
      dockerfile: packages/dashboard/Dockerfile
    container_name: tamma-dashboard
    environment:
      NODE_ENV: production
      API_URL: http://api:3000
    depends_on:
      - api
    ports:
      - '3002:3000'
    networks:
      - tamma-network
    restart: unless-stopped

volumes:
  postgres_data:
    driver: local
  redis_data:
    driver: local

networks:
  tamma-network:
    driver: bridge
    ipam:
      config:
        - subnet: 172.20.0.0/16
```

### Step 3: Development Docker Compose

Create development-specific configuration:

```yaml
# docker-compose.dev.yml
version: '3.8'

services:
  postgres:
    extends:
      file: docker-compose.yml
      service: postgres
    ports:
      - '5432:5432'
    environment:
      POSTGRES_DB: tamma_dev
      POSTGRES_USER: tamma_dev
      POSTGRES_PASSWORD: dev_password

  redis:
    extends:
      file: docker-compose.yml
      service: redis
    ports:
      - '6379:6379'

  orchestrator:
    build:
      context: .
      dockerfile: packages/orchestrator/Dockerfile.dev
    volumes:
      - .:/app
      - /app/node_modules
      - ./logs:/app/logs
    environment:
      NODE_ENV: development
      DATABASE_URL: postgresql://tamma_dev:dev_password@postgres:5432/tamma_dev
      REDIS_URL: redis://:dev_password@redis:6379
      LOG_LEVEL: debug
    command: pnpm dev

  api:
    build:
      context: .
      dockerfile: packages/api/Dockerfile.dev
    volumes:
      - .:/app
      - /app/node_modules
      - ./logs:/app/logs
    environment:
      NODE_ENV: development
      DATABASE_URL: postgresql://tamma_dev:dev_password@postgres:5432/tamma_dev
      REDIS_URL: redis://:dev_password@redis:6379
      LOG_LEVEL: debug
    command: pnpm dev

  dashboard:
    build:
      context: .
      dockerfile: packages/dashboard/Dockerfile.dev
    volumes:
      - ./packages/dashboard:/app
      - /app/node_modules
    environment:
      NODE_ENV: development
      API_URL: http://localhost:3000
    command: pnpm dev
```

### Step 4: Production Docker Compose

Create production-optimized configuration:

```yaml
# docker-compose.prod.yml
version: '3.8'

services:
  postgres:
    extends:
      file: docker-compose.yml
      service: postgres
    environment:
      POSTGRES_DB: ${POSTGRES_DB}
      POSTGRES_USER: ${POSTGRES_USER}
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
    volumes:
      - postgres_data:/var/lib/postgresql/data
      - ./backups:/backups
    deploy:
      resources:
        limits:
          memory: 2G
          cpus: '1.0'
        reservations:
          memory: 1G
          cpus: '0.5'

  redis:
    extends:
      file: docker-compose.yml
      service: redis
    command: redis-server --appendonly yes --requirepass ${REDIS_PASSWORD} --maxmemory 512mb --maxmemory-policy allkeys-lru
    deploy:
      resources:
        limits:
          memory: 512M
          cpus: '0.5'

  orchestrator:
    extends:
      file: docker-compose.yml
      service: orchestrator
    environment:
      NODE_ENV: production
      DATABASE_URL: ${DATABASE_URL}
      REDIS_URL: ${REDIS_URL}
      LOG_LEVEL: ${LOG_LEVEL:-info}
      WORKER_CONCURRENCY: ${WORKER_CONCURRENCY:-4}
    deploy:
      resources:
        limits:
          memory: 1G
          cpus: '1.0'
      replicas: 2
      restart_policy:
        condition: on-failure
        delay: 5s
        max_attempts: 3

  workers:
    extends:
      file: docker-compose.yml
      service: workers
    environment:
      NODE_ENV: production
      DATABASE_URL: ${DATABASE_URL}
      REDIS_URL: ${REDIS_URL}
      WORKER_CONCURRENCY: ${WORKER_CONCURRENCY:-4}
    deploy:
      resources:
        limits:
          memory: 512M
          cpus: '0.5'
      replicas: 4

  api:
    extends:
      file: docker-compose.yml
      service: api
    environment:
      NODE_ENV: production
      DATABASE_URL: ${DATABASE_URL}
      REDIS_URL: ${REDIS_URL}
      API_PORT: 3000
    deploy:
      resources:
        limits:
          memory: 512M
          cpus: '0.5'
      replicas: 2

  nginx:
    image: nginx:alpine
    container_name: tamma-nginx
    ports:
      - '80:80'
      - '443:443'
    volumes:
      - ./nginx/nginx.conf:/etc/nginx/nginx.conf:ro
      - ./nginx/ssl:/etc/nginx/ssl:ro
      - ./logs/nginx:/var/log/nginx
    depends_on:
      - api
      - dashboard
    networks:
      - tamma-network
    restart: unless-stopped

volumes:
  postgres_data:
    driver: local
  redis_data:
    driver: local
```

### Step 5: Docker Ignore Files

Create optimized .dockerignore files:

```dockerignore
# .dockerignore
node_modules
npm-debug.log*
yarn-debug.log*
yarn-error.log*
.pnpm-debug.log*

# Runtime data
pids
*.pid
*.seed
*.pid.lock

# Coverage directory used by tools like istanbul
coverage
*.lcov

# nyc test coverage
.nyc_output

# Dependency directories
node_modules/
jspm_packages/

# TypeScript cache
*.tsbuildinfo

# Optional npm cache directory
.npm

# Optional eslint cache
.eslintcache

# Microbundle cache
.rpt2_cache/
.rts2_cache_cjs/
.rts2_cache_es/
.rts2_cache_umd/

# Optional REPL history
.node_repl_history

# Output of 'npm pack'
*.tgz

# Yarn Integrity file
.yarn-integrity

# dotenv environment variables file
.env
.env.test
.env.local
.env.production

# parcel-bundler cache (https://parceljs.org/)
.cache
.parcel-cache

# Next.js build output
.next

# Nuxt.js build / generate output
.nuxt
dist

# Gatsby files
.cache/
public

# Storybook build outputs
.out
.storybook-out

# Temporary folders
tmp/
temp/

# Logs
logs
*.log

# Runtime data
pids
*.pid
*.seed
*.pid.lock

# IDE
.vscode
.idea
*.swp
*.swo

# OS
.DS_Store
Thumbs.db

# Git
.git
.gitignore

# Documentation
docs
README.md

# Tests
**/*.test.ts
**/*.spec.ts
test/
tests/
__tests__/
```

### Step 6: Health Check Scripts

Create comprehensive health check utilities:

```typescript
// packages/shared/src/health/health-check.ts
import { createHash } from 'crypto';
import { performance } from 'perf_hooks';

export interface HealthCheckResult {
  status: 'healthy' | 'unhealthy' | 'degraded';
  timestamp: string;
  duration: number;
  details: Record<string, unknown>;
  error?: string;
}

export interface HealthCheck {
  name: string;
  timeout: number;
  check: () => Promise<HealthCheckResult>;
}

export class HealthChecker {
  private checks: Map<string, HealthCheck> = new Map();

  register(check: HealthCheck): void {
    this.checks.set(check.name, check);
  }

  async runCheck(name: string): Promise<HealthCheckResult> {
    const check = this.checks.get(name);
    if (!check) {
      throw new Error(`Health check '${name}' not found`);
    }

    const startTime = performance.now();

    try {
      const result = await Promise.race([
        check.check(),
        new Promise<HealthCheckResult>((_, reject) =>
          setTimeout(() => reject(new Error('Health check timeout')), check.timeout)
        ),
      ]);

      return {
        ...result,
        duration: performance.now() - startTime,
        timestamp: new Date().toISOString(),
      };
    } catch (error) {
      return {
        status: 'unhealthy',
        timestamp: new Date().toISOString(),
        duration: performance.now() - startTime,
        details: {},
        error: error instanceof Error ? error.message : 'Unknown error',
      };
    }
  }

  async runAllChecks(): Promise<Record<string, HealthCheckResult>> {
    const results: Record<string, HealthCheckResult> = {};

    const promises = Array.from(this.checks.entries()).map(async ([name]) => {
      results[name] = await this.runCheck(name);
    });

    await Promise.allSettled(promises);
    return results;
  }

  getOverallStatus(
    results: Record<string, HealthCheckResult>
  ): 'healthy' | 'unhealthy' | 'degraded' {
    const statuses = Object.values(results).map((r) => r.status);

    if (statuses.every((s) => s === 'healthy')) {
      return 'healthy';
    }

    if (statuses.some((s) => s === 'unhealthy')) {
      return 'unhealthy';
    }

    return 'degraded';
  }
}

// Database health check
export const databaseHealthCheck: HealthCheck = {
  name: 'database',
  timeout: 5000,
  check: async (): Promise<HealthCheckResult> => {
    // Implementation would check database connectivity
    return {
      status: 'healthy',
      timestamp: new Date().toISOString(),
      duration: 0,
      details: { connection: 'ok' },
    };
  },
};

// Redis health check
export const redisHealthCheck: HealthCheck = {
  name: 'redis',
  timeout: 3000,
  check: async (): Promise<HealthCheckResult> => {
    // Implementation would check Redis connectivity
    return {
      status: 'healthy',
      timestamp: new Date().toISOString(),
      duration: 0,
      details: { connection: 'ok' },
    };
  },
};
```

### Step 7: Container Optimization Scripts

Create build and optimization utilities:

```bash
#!/bin/bash
# scripts/docker-build.sh

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Configuration
REGISTRY=${DOCKER_REGISTRY:-"localhost:5000"}
VERSION=${VERSION:-"latest"}
PROJECT_NAME="tamma"

echo -e "${GREEN}Building Tamma Docker images${NC}"
echo "Registry: $REGISTRY"
echo "Version: $VERSION"
echo ""

# Function to build and push image
build_and_push() {
  local service=$1
  local dockerfile=$2
  local context=${3:-"."}

  echo -e "${YELLOW}Building $service...${NC}"

  docker build \
    --file "$dockerfile" \
    --context "$context" \
    --tag "$REGISTRY/$PROJECT_NAME-$service:$VERSION" \
    --tag "$REGISTRY/$PROJECT_NAME-$service:latest" \
    .

  if [ "$PUSH_IMAGES" = "true" ]; then
    echo -e "${YELLOW}Pushing $service...${NC}"
    docker push "$REGISTRY/$PROJECT_NAME-$service:$VERSION"
    docker push "$REGISTRY/$PROJECT_NAME-$service:latest"
  fi

  echo -e "${GREEN}✓ $service completed${NC}"
  echo ""
}

# Build all services
build_and_push "api" "packages/api/Dockerfile"
build_and_push "orchestrator" "packages/orchestrator/Dockerfile"
build_and_push "workers" "packages/workers/Dockerfile"
build_and_push "dashboard" "packages/dashboard/Dockerfile"
build_and_push "cli" "packages/cli/Dockerfile"

echo -e "${GREEN}All images built successfully!${NC}"

# Show image sizes
echo ""
echo -e "${YELLOW}Image sizes:${NC}"
docker images | grep "$PROJECT_NAME-" | awk '{print $1, $2, $7}'
```

```bash
#!/bin/bash
# scripts/docker-optimize.sh

set -e

echo "Optimizing Docker images..."

# Scan for vulnerabilities
echo "Scanning for vulnerabilities..."
docker run --rm -v /var/run/docker.sock:/var/run/docker.sock \
  aquasec/trivy image --severity HIGH,CRITICAL tamma-api:latest

# Clean up unused images
echo "Cleaning up unused images..."
docker image prune -f

# Show disk usage
echo "Docker disk usage:"
docker system df

echo "Optimization complete!"
```

## Files to Create

1. **Dockerfiles**:
   - `packages/cli/Dockerfile`
   - `packages/orchestrator/Dockerfile`
   - `packages/workers/Dockerfile`
   - `packages/api/Dockerfile`
   - `packages/dashboard/Dockerfile`

2. **Docker Compose Files**:
   - `docker-compose.yml` (base configuration)
   - `docker-compose.dev.yml` (development)
   - `docker-compose.prod.yml` (production)

3. **Configuration Files**:
   - `.dockerignore` (root and package-specific)
   - `nginx/nginx.conf` (reverse proxy configuration)

4. **Scripts**:
   - `scripts/docker-build.sh` (build automation)
   - `scripts/docker-optimize.sh` (optimization utilities)

5. **Health Check Implementation**:
   - `packages/shared/src/health/health-check.ts`

## Dependencies

- Docker Engine 20.10+
- Docker Compose 2.0+
- Node.js 22 LTS (for build context)
- PostgreSQL 17 (for database services)
- Redis 7 (for caching services)

## Testing Requirements

1. **Container Testing**:
   - Test container startup and health checks
   - Verify service connectivity
   - Test volume mounting and persistence

2. **Integration Testing**:
   - Test Docker Compose orchestration
   - Verify service discovery and networking
   - Test environment variable injection

3. **Performance Testing**:
   - Measure image build times
   - Test container startup times
   - Monitor resource usage

## Security Considerations

1. **Image Security**:
   - Use minimal base images (Alpine Linux)
   - Regular security scanning with Trivy
   - Non-root user execution
   - Multi-stage builds to reduce attack surface

2. **Runtime Security**:
   - Resource limits and constraints
   - Network segmentation
   - Read-only filesystems where possible
   - Secrets management through environment variables

3. **Container Hardening**:
   - Remove unnecessary packages
   - Disable SSH access
   - Implement proper logging and monitoring
   - Regular base image updates

## Notes

- All containers should follow the 12-factor app principles
- Use health checks for all services
- Implement proper logging aggregation
- Consider using Kubernetes for production orchestration
- Regular security updates and vulnerability scanning required
