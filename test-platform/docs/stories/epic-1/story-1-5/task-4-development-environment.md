# Task 4: Development Environment

## Overview

Set up a comprehensive development environment with hot reloading, debugging capabilities, and developer productivity tools for the Tamma platform.

## Objectives

- Configure hot module replacement for rapid development
- Set up debugging configurations for VS Code
- Implement development scripts and utilities
- Create development database setup
- Configure testing environment with watch mode

## Implementation Steps

### Step 1: Development Scripts Configuration

Create comprehensive package.json scripts:

```json
{
  "name": "@tamma/monorepo",
  "private": true,
  "scripts": {
    "dev": "concurrently \"pnpm dev:api\" \"pnpm dev:orchestrator\" \"pnpm dev:workers\" \"pnpm dev:dashboard\"",
    "dev:api": "pnpm --filter @tamma/api dev",
    "dev:orchestrator": "pnpm --filter @tamma/orchestrator dev",
    "dev:workers": "pnpm --filter @tamma/workers dev",
    "dev:dashboard": "pnpm --filter @tamma/dashboard dev",
    "dev:cli": "pnpm --filter @tamma/cli dev",

    "build": "pnpm -r build",
    "build:watch": "pnpm -r build --watch",

    "test": "vitest run",
    "test:watch": "vitest watch",
    "test:coverage": "vitest run --coverage",
    "test:ui": "vitest --ui",

    "lint": "eslint . --ext .ts,.tsx",
    "lint:fix": "eslint . --ext .ts,.tsx --fix",
    "format": "prettier --write .",
    "format:check": "prettier --check .",

    "type-check": "tsc --noEmit",
    "type-check:watch": "tsc --noEmit --watch",

    "db:dev:up": "docker-compose -f docker-compose.dev.yml up -d postgres redis",
    "db:dev:down": "docker-compose -f docker-compose.dev.yml down",
    "db:dev:migrate": "pnpm --filter @tamma/orchestrator migrate:latest",
    "db:dev:seed": "pnpm --filter @tamma/orchestrator seed",
    "db:dev:reset": "pnpm db:dev:down && pnpm db:dev:up && sleep 5 && pnpm db:dev:migrate && pnpm db:dev:seed",

    "dev:setup": "pnpm install && pnpm db:dev:up && sleep 5 && pnpm db:dev:migrate && pnpm db:dev:seed",
    "dev:clean": "pnpm db:dev:down && docker system prune -f && rm -rf node_modules/.cache",

    "logs:dev": "docker-compose -f docker-compose.dev.yml logs -f",
    "logs:api": "docker-compose -f docker-compose.dev.yml logs -f api",
    "logs:orchestrator": "docker-compose -f docker-compose.dev.yml logs -f orchestrator",

    "debug:api": "node --inspect-brk packages/api/dist/index.js",
    "debug:orchestrator": "node --inspect-brk packages/orchestrator/dist/index.js",
    "debug:workers": "node --inspect-brk packages/workers/dist/index.js"
  },
  "devDependencies": {
    "@types/node": "^22.0.0",
    "@typescript-eslint/eslint-plugin": "^8.0.0",
    "@typescript-eslint/parser": "^8.0.0",
    "concurrently": "^8.2.2",
    "eslint": "^9.0.0",
    "eslint-config-prettier": "^9.0.0",
    "eslint-plugin-prettier": "^5.0.0",
    "prettier": "^3.0.0",
    "typescript": "^5.7.0",
    "vitest": "^3.0.0",
    "@vitest/coverage-v8": "^3.0.0",
    "@vitest/ui": "^3.0.0"
  }
}
```

### Step 2: VS Code Configuration

Set up comprehensive VS Code development environment:

```json
// .vscode/settings.json
{
  "typescript.preferences.importModuleSpecifier": "relative",
  "typescript.suggest.autoImports": true,
  "typescript.updateImportsOnFileMove.enabled": "always",
  "editor.formatOnSave": true,
  "editor.defaultFormatter": "esbenp.prettier-vscode",
  "editor.codeActionsOnSave": {
    "source.fixAll.eslint": "explicit",
    "source.organizeImports": "explicit"
  },
  "files.exclude": {
    "**/node_modules": true,
    "**/dist": true,
    "**/.git": true,
    "**/.DS_Store": true,
    "**/Thumbs.db": true
  },
  "search.exclude": {
    "**/node_modules": true,
    "**/dist": true,
    "**/.git": true,
    "**/coverage": true
  },
  "files.watcherExclude": {
    "**/node_modules/**": true,
    "**/dist/**": true,
    "**/.git/**": true
  },
  "terminal.integrated.defaultProfile.linux": "bash",
  "terminal.integrated.cwd": "${workspaceFolder}",
  "debug.allowBreakpointsEverywhere": true,
  "debug.inlineValues": "on",
  "debug.showBreakpointsInOverviewRuler": true,
  "testing.automaticallyOpenPeekView": "failureInVisibleDocument",
  "testing.followRunningTest": true,
  "git.autofetch": true,
  "git.enableSmartCommit": true,
  "git.confirmSync": false,
  "workbench.colorTheme": "Default Dark+",
  "workbench.iconTheme": "material-icon-theme",
  "extensions.ignoreRecommendations": false
}
```

```json
// .vscode/launch.json
{
  "version": "0.2.0",
  "configurations": [
    {
      "name": "Debug API",
      "type": "node",
      "request": "launch",
      "program": "${workspaceFolder}/packages/api/src/index.ts",
      "outFiles": ["${workspaceFolder}/packages/api/dist/**/*.js"],
      "runtimeArgs": ["-r", "ts-node/register"],
      "env": {
        "NODE_ENV": "development",
        "LOG_LEVEL": "debug"
      },
      "console": "integratedTerminal",
      "internalConsoleOptions": "neverOpen",
      "skipFiles": ["<node_internals>/**"]
    },
    {
      "name": "Debug Orchestrator",
      "type": "node",
      "request": "launch",
      "program": "${workspaceFolder}/packages/orchestrator/src/index.ts",
      "outFiles": ["${workspaceFolder}/packages/orchestrator/dist/**/*.js"],
      "runtimeArgs": ["-r", "ts-node/register"],
      "env": {
        "NODE_ENV": "development",
        "LOG_LEVEL": "debug"
      },
      "console": "integratedTerminal",
      "internalConsoleOptions": "neverOpen",
      "skipFiles": ["<node_internals>/**"]
    },
    {
      "name": "Debug Workers",
      "type": "node",
      "request": "launch",
      "program": "${workspaceFolder}/packages/workers/src/index.ts",
      "outFiles": ["${workspaceFolder}/packages/workers/dist/**/*.js"],
      "runtimeArgs": ["-r", "ts-node/register"],
      "env": {
        "NODE_ENV": "development",
        "LOG_LEVEL": "debug"
      },
      "console": "integratedTerminal",
      "internalConsoleOptions": "neverOpen",
      "skipFiles": ["<node_internals>/**"]
    },
    {
      "name": "Debug CLI",
      "type": "node",
      "request": "launch",
      "program": "${workspaceFolder}/packages/cli/src/index.ts",
      "outFiles": ["${workspaceFolder}/packages/cli/dist/**/*.js"],
      "runtimeArgs": ["-r", "ts-node/register"],
      "env": {
        "NODE_ENV": "development"
      },
      "console": "integratedTerminal",
      "internalConsoleOptions": "neverOpen",
      "skipFiles": ["<node_internals>/**"]
    },
    {
      "name": "Debug Tests",
      "type": "node",
      "request": "launch",
      "program": "${workspaceFolder}/node_modules/.bin/vitest",
      "args": ["run", "--reporter=verbose"],
      "console": "integratedTerminal",
      "internalConsoleOptions": "neverOpen",
      "env": {
        "NODE_ENV": "test"
      }
    },
    {
      "name": "Attach to Process",
      "type": "node",
      "request": "attach",
      "port": 9229,
      "restart": true,
      "localRoot": "${workspaceFolder}",
      "remoteRoot": "${workspaceFolder}",
      "skipFiles": ["<node_internals>/**"]
    }
  ],
  "compounds": [
    {
      "name": "Debug Full Stack",
      "configurations": ["Debug API", "Debug Orchestrator", "Debug Workers"],
      "stopAll": true
    }
  ]
}
```

```json
// .vscode/extensions.json
{
  "recommendations": [
    "esbenp.prettier-vscode",
    "dbaeumer.vscode-eslint",
    "bradlc.vscode-tailwindcss",
    "ms-vscode.vscode-typescript-next",
    "formulahendry.auto-rename-tag",
    "christian-kohler.path-intellisense",
    "ms-vscode.vscode-json",
    "redhat.vscode-yaml",
    "ms-vscode-remote.remote-containers",
    "ms-vscode.vscode-docker",
    "humao.rest-client",
    "gruntfuggly.todo-tree",
    "streetsidesoftware.code-spell-checker",
    "ms-vscode.test-adapter-converter",
    "hbenl.vscode-test-explorer",
    "ms-vscode.vscode-git-graph",
    "eamodio.gitlens",
    "ms-vscode.vscode-thunder-client",
    "ms-vscode.live-server",
    "ms-vscode.js-debug",
    "ms-vscode.js-debug-nightly"
  ]
}
```

### Step 3: Development Environment Variables

Create comprehensive development configuration:

```bash
# .env.development
# Environment
NODE_ENV=development
LOG_LEVEL=debug

# Database
DATABASE_URL=postgresql://tamma_dev:dev_password@localhost:5432/tamma_dev
POSTGRES_DB=tamma_dev
POSTGRES_USER=tamma_dev
POSTGRES_PASSWORD=dev_password

# Redis
REDIS_URL=redis://:dev_password@localhost:6379
REDIS_PASSWORD=dev_password

# API Configuration
API_PORT=3000
API_HOST=localhost
CORS_ORIGIN=http://localhost:3002

# Orchestrator Configuration
ORCHESTRATOR_PORT=3001
WORKER_CONCURRENCY=2

# AI Providers (Development Keys)
ANTHROPIC_API_KEY=dev_anthropic_key
OPENAI_API_KEY=dev_openai_key
GITHUB_TOKEN=dev_github_token

# Git Platforms
GITHUB_TOKEN=dev_github_token
GITLAB_TOKEN=dev_gitlab_token

# Development Tools
ENABLE_DEBUG_LOGS=true
ENABLE_PROFILING=true
ENABLE_METRICS=true

# Testing
TEST_DATABASE_URL=postgresql://tamma_test:test_password@localhost:5432/tamma_test
TEST_REDIS_URL=redis://:test_password@localhost:6380

# Feature Flags
ENABLE_EXPERIMENTAL_FEATURES=true
ENABLE_DEBUG_ENDPOINTS=true
ENABLE_SWAGGER_UI=true
```

### Step 4: Hot Reloading Configuration

Set up development servers with hot reloading:

```typescript
// packages/api/src/dev-server.ts
import { FastifyInstance } from 'fastify';
import { watch } from 'chokidar';
import { build } from 'esbuild';
import path from 'path';

export class DevServer {
  private server: FastifyInstance;
  private watcher: any;

  constructor(server: FastifyInstance) {
    this.server = server;
  }

  async start(): Promise<void> {
    // Watch TypeScript files
    this.watcher = watch(['packages/api/src/**/*.ts', 'packages/shared/src/**/*.ts'], {
      ignored: /node_modules/,
      persistent: true,
    });

    this.watcher.on('change', async (filePath: string) => {
      console.log(`File changed: ${filePath}`);
      await this.rebuild();
    });

    this.watcher.on('add', async (filePath: string) => {
      console.log(`File added: ${filePath}`);
      await this.rebuild();
    });

    this.watcher.on('unlink', async (filePath: string) => {
      console.log(`File removed: ${filePath}`);
      await this.rebuild();
    });
  }

  private async rebuild(): Promise<void> {
    try {
      console.log('Rebuilding...');

      await build({
        entryPoints: [path.join(__dirname, 'index.ts')],
        bundle: true,
        outfile: path.join(__dirname, '../dist/index.js'),
        platform: 'node',
        target: 'node22',
        format: 'cjs',
        sourcemap: true,
        watch: false,
      });

      console.log('Rebuild complete');

      // Notify clients to reload
      this.server.io?.emit('reload');
    } catch (error) {
      console.error('Rebuild failed:', error);
    }
  }

  async stop(): Promise<void> {
    if (this.watcher) {
      await this.watcher.close();
    }
  }
}
```

```typescript
// packages/dashboard/src/development.ts
import { ViteDevServer } from 'vite';

export class DevelopmentSetup {
  private viteServer: ViteDevServer | null = null;

  async startViteServer(): Promise<ViteDevServer> {
    const { createServer } = await import('vite');

    this.viteServer = await createServer({
      configFile: path.join(__dirname, '../vite.config.ts'),
      server: {
        port: 3002,
        host: true,
        hmr: {
          port: 3003,
        },
      },
      optimizeDeps: {
        include: ['react', 'react-dom', '@tanstack/react-query'],
      },
    });

    return this.viteServer;
  }

  async stopViteServer(): Promise<void> {
    if (this.viteServer) {
      await this.viteServer.close();
      this.viteServer = null;
    }
  }

  setupHotReload(): void {
    if (process.env.NODE_ENV === 'development') {
      // Enable React Fast Refresh
      if (import.meta.hot) {
        import.meta.hot.accept();
      }
    }
  }
}
```

### Step 5: Development Database Setup

Create development database utilities:

```typescript
// packages/orchestrator/src/dev/database.ts
import { Pool } from 'pg';
import { execSync } from 'child_process';
import fs from 'fs/promises';
import path from 'path';

export class DevDatabase {
  private pool: Pool;

  constructor() {
    this.pool = new Pool({
      connectionString: process.env.DATABASE_URL,
      max: 10,
      idleTimeoutMillis: 30000,
      connectionTimeoutMillis: 2000,
    });
  }

  async setup(): Promise<void> {
    console.log('Setting up development database...');

    // Create database if it doesn't exist
    await this.createDatabase();

    // Run migrations
    await this.runMigrations();

    // Seed data
    await this.seedData();

    console.log('Development database setup complete');
  }

  private async createDatabase(): Promise<void> {
    const client = await this.pool.connect();

    try {
      await client.query('CREATE DATABASE IF NOT EXISTS tamma_dev');
      console.log('Database created or already exists');
    } catch (error) {
      console.error('Failed to create database:', error);
      throw error;
    } finally {
      client.release();
    }
  }

  private async runMigrations(): Promise<void> {
    const migrationsPath = path.join(__dirname, '../../database/migrations');
    const migrationFiles = await fs.readdir(migrationsPath);

    for (const file of migrationFiles.sort()) {
      if (file.endsWith('.sql')) {
        console.log(`Running migration: ${file}`);
        const migrationSQL = await fs.readFile(path.join(migrationsPath, file), 'utf-8');

        const client = await this.pool.connect();
        try {
          await client.query(migrationSQL);
          console.log(`Migration ${file} completed`);
        } catch (error) {
          console.error(`Migration ${file} failed:`, error);
          throw error;
        } finally {
          client.release();
        }
      }
    }
  }

  async seedData(): Promise<void> {
    const seedPath = path.join(__dirname, '../../database/seeds');
    const seedFiles = await fs.readdir(seedPath);

    for (const file of seedFiles.sort()) {
      if (file.endsWith('.sql')) {
        console.log(`Running seed: ${file}`);
        const seedSQL = await fs.readFile(path.join(seedPath, file), 'utf-8');

        const client = await this.pool.connect();
        try {
          await client.query(seedSQL);
          console.log(`Seed ${file} completed`);
        } catch (error) {
          console.error(`Seed ${file} failed:`, error);
          throw error;
        } finally {
          client.release();
        }
      }
    }
  }

  async reset(): Promise<void> {
    console.log('Resetting development database...');

    const client = await this.pool.connect();
    try {
      // Drop all tables
      await client.query(`
        DROP SCHEMA public CASCADE;
        CREATE SCHEMA public;
        GRANT ALL ON SCHEMA public TO postgres;
        GRANT ALL ON SCHEMA public TO public;
      `);

      console.log('Database reset complete');
    } catch (error) {
      console.error('Failed to reset database:', error);
      throw error;
    } finally {
      client.release();
    }
  }

  async close(): Promise<void> {
    await this.pool.end();
  }
}
```

### Step 6: Development Testing Setup

Configure testing environment with watch mode:

```typescript
// vitest.config.ts
import { defineConfig } from 'vitest/config';
import { resolve } from 'path';

export default defineConfig({
  test: {
    globals: true,
    environment: 'node',
    setupFiles: ['./test/setup.ts'],
    coverage: {
      provider: 'v8',
      reporter: ['text', 'json', 'html'],
      exclude: ['node_modules/', 'dist/', 'test/', '**/*.d.ts', '**/*.config.*', '**/coverage/**'],
    },
    watch: {
      include: ['packages/*/src/**/*.ts'],
      exclude: ['node_modules/', 'dist/'],
    },
  },
  resolve: {
    alias: {
      '@': resolve(__dirname, '.'),
      '@shared': resolve(__dirname, 'packages/shared/src'),
      '@api': resolve(__dirname, 'packages/api/src'),
      '@orchestrator': resolve(__dirname, 'packages/orchestrator/src'),
      '@workers': resolve(__dirname, 'packages/workers/src'),
      '@cli': resolve(__dirname, 'packages/cli/src'),
      '@dashboard': resolve(__dirname, 'packages/dashboard/src'),
    },
  },
});
```

```typescript
// test/setup.ts
import { beforeAll, afterAll, beforeEach, afterEach } from 'vitest';
import { DevDatabase } from '../packages/orchestrator/src/dev/database';

let devDatabase: DevDatabase;

beforeAll(async () => {
  // Setup test database
  devDatabase = new DevDatabase();
  await devDatabase.setup();
});

afterAll(async () => {
  // Cleanup test database
  if (devDatabase) {
    await devDatabase.close();
  }
});

beforeEach(async () => {
  // Reset database before each test
  await devDatabase.reset();
});

afterEach(() => {
  // Cleanup after each test
});
```

### Step 7: Development Utilities

Create helpful development utilities:

```typescript
// packages/shared/src/dev/dev-utils.ts
import { performance } from 'perf_hooks';
import { createHash } from 'crypto';

export class DevUtils {
  static formatDuration(ms: number): string {
    if (ms < 1000) return `${ms}ms`;
    if (ms < 60000) return `${(ms / 1000).toFixed(2)}s`;
    return `${(ms / 60000).toFixed(2)}m`;
  }

  static async measureTime<T>(
    fn: () => Promise<T>,
    label?: string
  ): Promise<{ result: T; duration: number }> {
    const start = performance.now();
    const result = await fn();
    const duration = performance.now() - start;

    if (label) {
      console.log(`${label}: ${this.formatDuration(duration)}`);
    }

    return { result, duration };
  }

  static generateHash(data: string): string {
    return createHash('sha256').update(data).digest('hex').substring(0, 8);
  }

  static async retry<T>(
    fn: () => Promise<T>,
    maxAttempts: number = 3,
    delay: number = 1000
  ): Promise<T> {
    let lastError: Error;

    for (let attempt = 1; attempt <= maxAttempts; attempt++) {
      try {
        return await fn();
      } catch (error) {
        lastError = error as Error;

        if (attempt === maxAttempts) {
          throw lastError;
        }

        console.warn(`Attempt ${attempt} failed, retrying in ${delay}ms...`, error);
        await new Promise((resolve) => setTimeout(resolve, delay));
        delay *= 2; // Exponential backoff
      }
    }

    throw lastError!;
  }

  static formatBytes(bytes: number): string {
    const sizes = ['Bytes', 'KB', 'MB', 'GB'];
    if (bytes === 0) return '0 Bytes';
    const i = Math.floor(Math.log(bytes) / Math.log(1024));
    return Math.round((bytes / Math.pow(1024, i)) * 100) / 100 + ' ' + sizes[i];
  }

  static truncateString(str: string, maxLength: number = 50): string {
    return str.length > maxLength ? str.substring(0, maxLength) + '...' : str;
  }

  static isValidEmail(email: string): boolean {
    const emailRegex = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
    return emailRegex.test(email);
  }

  static generateId(): string {
    return Math.random().toString(36).substring(2) + Date.now().toString(36);
  }

  static sleep(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }
}

// Development logger
export class DevLogger {
  private static log(level: string, message: string, data?: any): void {
    if (process.env.NODE_ENV !== 'development') return;

    const timestamp = new Date().toISOString();
    const logEntry = {
      timestamp,
      level,
      message,
      ...(data && { data }),
    };

    console.log(`[${timestamp}] ${level.toUpperCase()}: ${message}`, data || '');
  }

  static debug(message: string, data?: any): void {
    this.log('debug', message, data);
  }

  static info(message: string, data?: any): void {
    this.log('info', message, data);
  }

  static warn(message: string, data?: any): void {
    this.log('warn', message, data);
  }

  static error(message: string, error?: Error | any): void {
    this.log('error', message, error);
  }
}
```

## Files to Create

1. **Configuration Files**:
   - `.vscode/settings.json`
   - `.vscode/launch.json`
   - `.vscode/extensions.json`
   - `.env.development`

2. **Development Scripts**:
   - `packages/api/src/dev-server.ts`
   - `packages/dashboard/src/development.ts`
   - `packages/orchestrator/src/dev/database.ts`

3. **Testing Configuration**:
   - `vitest.config.ts`
   - `test/setup.ts`

4. **Development Utilities**:
   - `packages/shared/src/dev/dev-utils.ts`

5. **Database Setup**:
   - `database/migrations/001_initial_schema.sql`
   - `database/seeds/001_dev_data.sql`

## Dependencies

- **Development Tools**:
  - `concurrently` - Run multiple scripts simultaneously
  - `chokidar` - File watching for hot reload
  - `esbuild` - Fast TypeScript compilation
  - `vite` - Frontend development server

- **Testing Tools**:
  - `vitest` - Fast test runner
  - `@vitest/coverage-v8` - Code coverage
  - `@vitest/ui` - Test UI interface

- **Database Tools**:
  - `pg` - PostgreSQL client
  - `@types/pg` - TypeScript definitions

## Testing Requirements

1. **Unit Tests**:
   - Test all development utilities
   - Test database setup and seeding
   - Test hot reload functionality

2. **Integration Tests**:
   - Test development environment startup
   - Test service communication in dev mode
   - Test database migrations

3. **Manual Testing**:
   - Verify hot reload works correctly
   - Test debugging configurations
   - Validate development scripts

## Security Considerations

1. **Development Secrets**:
   - Use development-only API keys
   - Never commit real credentials
   - Use environment-specific configurations

2. **Development Database**:
   - Isolate from production data
   - Use strong passwords even in development
   - Regular cleanup of test data

3. **Network Security**:
   - Bind development servers to localhost
   - Use CORS only for development origins
   - Disable production security features in development

## Notes

- Development environment should be easy to set up with a single command
- All development tools should be optional for production builds
- Hot reload should work across all packages
- Debugging configurations should support both local and containerized development
- Development database should be automatically reset between test runs
