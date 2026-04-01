---
title: "Contributing to Tamma"
sidebar:
  order: 99
---

Thank you for your interest in contributing to Tamma! This guide will help you get started.

## Table of Contents

- [Code of Conduct](#code-of-conduct)
- [How Can I Contribute?](#how-can-i-contribute)
- [Development Setup](#development-setup)
- [Repository Structure](#repository-structure)
- [Development Workflow](#development-workflow)
- [Coding Standards](#coding-standards)
- [Testing Requirements](#testing-requirements)
- [Pull Request Process](#pull-request-process)

---

## Code of Conduct

Be respectful, inclusive, and professional. We're building a platform that will autonomously maintain itself -- let's maintain a high standard of collaboration.

---

## How Can I Contribute?

### 1. Pick Up a Story

Browse the [Stories Index](/stories/) to find planned stories. Each story has detailed documentation in `docs/stories/`.

### 2. Report Bugs

Open a [GitHub issue](https://github.com/meywd/tamma/issues/new) with:
- Clear, descriptive title
- Steps to reproduce
- Expected vs actual behavior
- Environment details (Node version, OS, etc.)

### 3. Suggest Enhancements

Open a [GitHub issue](https://github.com/meywd/tamma/issues/new) with:
- Clear use case
- Proposed solution
- Alternative approaches considered

### 4. Improve Documentation

Documentation lives in `/docs` and wiki pages. Submit PRs for:
- Typo fixes
- Clarifications
- Additional examples
- Missing documentation

---

## Development Setup

### Prerequisites

- **Node.js:** 22 LTS or later
- **pnpm:** 9.x or later
- **Git:** 2.40 or later
- **PostgreSQL:** 17 or later (for orchestrator mode)
- **.NET 8.0 SDK** (for ELSA workflows development)
- **Docker & Docker Compose** (for full-stack deployment)

### Installation

```bash
# Clone the repository
git clone https://github.com/meywd/tamma.git
cd tamma

# Install dependencies
pnpm install

# Build all packages
pnpm build

# Run tests
pnpm test
```

### ELSA Workflows (C#)

```bash
cd apps/tamma-elsa
dotnet restore
dotnet build
dotnet test
```

---

## Repository Structure

```
tamma/
├── packages/                  # TypeScript monorepo packages (pnpm workspaces)
│   ├── api/                  # Fastify REST API (70+ source files)
│   ├── cli/                  # Command-line interface
│   ├── cost-monitor/         # LLM usage cost tracking
│   ├── dashboard/            # React SPA (Vite, 70+ TSX components)
│   ├── events/               # DCB event sourcing (placeholder)
│   ├── gates/                # Agent permissions system
│   ├── intelligence/         # Context, vector DB, RAG, knowledge base (94 source files)
│   ├── mcp-client/           # MCP protocol client
│   ├── observability/        # Pino structured logging
│   ├── orchestrator/         # Engine, ELSA bridge, SaaS coordinator
│   ├── platforms/            # Git platform abstraction (GitHub implemented)
│   ├── providers/            # AI provider abstraction (Claude, OpenCode, OpenRouter, Zen MCP)
│   ├── scrum-master/         # Task supervision and coordination
│   ├── shared/               # Shared types, security, telemetry, config
│   └── workers/              # Background workers (placeholder)
├── apps/                      # Standalone applications
│   ├── tamma-elsa/           # ELSA workflow engine (.NET 8, 194 C# files)
│   │   ├── src/
│   │   │   ├── Tamma.Activities/   # 70+ ELSA activity implementations
│   │   │   ├── Tamma.ElsaServer/   # Server with 20+ code-first workflows
│   │   │   ├── Tamma.Studio/       # Custom Blazor WASM studio
│   │   │   ├── Tamma.Core/         # Shared enums and models
│   │   │   ├── Tamma.Data/         # Database context and migrations
│   │   │   └── Tamma.Api/          # .NET REST API
│   │   └── tests/                   # C# test projects
│   ├── tamma-engine/         # TypeScript engine launcher
│   ├── marketing-site/       # Cloudflare Workers marketing site
│   ├── test-platform/        # AI provider benchmark platform
│   └── doc-review/           # Documentation review app
├── docker/                    # Docker Compose and Dockerfiles
│   ├── docker-compose.yml        # Base compose
│   ├── docker-compose.prod.yml   # Production overrides
│   ├── docker-compose.test.yml   # Test overrides
│   ├── Dockerfile.ts             # TypeScript services
│   ├── Dockerfile.dashboard      # Dashboard (nginx)
│   └── nginx-proxy.conf         # Reverse proxy config
├── docs/                      # All planning and specification documents
│   ├── stories/              # Story docs organized by epic (22 epic directories)
│   ├── architecture.md       # Technical architecture
│   ├── PRD.md               # Product requirements
│   └── epics.md             # Epic breakdown
├── .github/workflows/        # CI/CD pipelines
│   ├── ci.yml               # Build, lint, test
│   ├── deploy.yml           # Deploy to VPS
│   ├── docker-publish.yml   # Build and push Docker images
│   └── ...
├── .dev/                     # Development knowledge base
│   ├── spikes/              # Research and prototyping
│   ├── bugs/                # Bug reports
│   ├── findings/            # Pitfalls and best practices
│   └── decisions/           # Architecture Decision Records
└── wiki/                     # GitHub wiki source files
```

---

## Development Workflow

### 1. Read Before You Code

Before implementing anything, read these documents in order:
1. `BEFORE_YOU_CODE.md` -- Mandatory process guide
2. `.dev/README.md` -- Development knowledge base
3. `CLAUDE.md` -- Project guidelines
4. The relevant story file in `docs/stories/`

### 2. Create a Feature Branch

```bash
git checkout -b feature/issue-{number}-{short-description}
# Example: git checkout -b feature/issue-8-provider-interface
```

### 3. Implement the Feature

Follow the subtask checklist in the story documentation.

### 4. Write Tests

- **Unit tests:** All new code must have unit tests
- **Integration tests:** Required for API integrations
- **Coverage targets:** 80% line, 75% branch, 85% function

### 5. Run Tests and Linting

```bash
# Run all tests
pnpm test

# Run linting
pnpm lint

# Check types
pnpm typecheck
```

### 6. Commit Your Changes

Follow conventional commits format:

```bash
git commit -m "feat(providers): implement IAIProvider interface

- Add IAIProvider interface with core methods
- Define MessageRequest and MessageResponse types
- Add TypeScript documentation

Closes #8"
```

Commit types:
- `feat:` -- New feature
- `fix:` -- Bug fix
- `docs:` -- Documentation changes
- `test:` -- Test additions/changes
- `refactor:` -- Code refactoring
- `chore:` -- Build/tooling changes

### 7. Push and Create Pull Request

```bash
git push origin feature/issue-8-provider-interface
```

Create PR on GitHub with:
- Descriptive title
- Reference to issue (`Closes #8`)
- Summary of changes
- Testing performed

---

## Coding Standards

### TypeScript

- **Strict mode:** All packages use TypeScript strict mode (`exactOptionalPropertyTypes: true`, `noUncheckedIndexedAccess: true`)
- **Type safety:** No `any` types (use `unknown` + type guards)
- **Interfaces:** `I` prefix for interfaces (`IAIProvider`, `IAgentProvider`)
- **Naming:** PascalCase for classes/interfaces, camelCase for functions/variables, SCREAMING_SNAKE_CASE for constants
- **Async/Await:** Always use async/await, never `.then()/.catch()`
- **ESM:** All imports use `.js` extension

### C# (.NET 8)

- Follow standard C# naming conventions
- Activities inherit from ELSA base classes
- Models defined in `Models/` subdirectories within activity folders

### Code Style

- **Formatting:** Prettier with 2-space indentation (TypeScript)
- **Linting:** ESLint with recommended rules
- **Imports:** Organize imports (Node.js built-ins -> external deps -> internal packages -> relative imports)

### Error Handling

- Use `TammaError` class with error codes from `PROVIDER_ERROR_CODES`
- Pattern: `createProviderError(code, message, retryable, severity)`
- Include context in error messages
- Log errors with structured logging (Pino)
- Never expose API keys, tokens, or internal URLs in error messages

---

## Testing Requirements

### Unit Tests (Required)

Every module must have unit tests covering:
- Happy path scenarios
- Error cases
- Edge cases
- Boundary conditions

Tests use Vitest 3.x with colocated `*.test.ts` files:

```typescript
import { describe, it, expect, vi } from 'vitest';

describe('ProviderChain', () => {
  it('should try fallback providers when primary is unhealthy', async () => {
    // ...
  });

  it('should throw NO_AVAILABLE_PROVIDER when all providers exhausted', async () => {
    // ...
  });
});
```

### Integration Tests (Conditional)

Required for:
- AI provider integrations (test with real APIs)
- Git platform integrations
- Database operations
- Docker Compose stack

### Test Naming

- `*.test.ts` -- Unit tests (colocated with source)
- `*.integration.test.ts` -- Integration tests
- `*.e2e.test.ts` -- End-to-end tests

---

## Pull Request Process

### PR Checklist

Before submitting, ensure:

- [ ] Tests added and passing (`pnpm test`)
- [ ] Linting passes (`pnpm lint`)
- [ ] Type checking passes (`pnpm typecheck`)
- [ ] Documentation updated (if needed)
- [ ] Commit messages follow conventional commits
- [ ] PR description references issue number
- [ ] No secrets committed (.env, credentials, API keys)

### Review Process

1. **Automated Checks:** GitHub Actions runs tests, linting, type checking
2. **Code Review:** Maintainer reviews code for quality and design
3. **Changes Requested:** Address feedback and push updates
4. **Approval:** Maintainer approves PR
5. **Merge:** Maintainer merges PR (squash and merge)

---

## Questions?

- **General questions:** Open a [GitHub Discussion](https://github.com/meywd/tamma/discussions)
- **Bug reports:** Open a [GitHub Issue](https://github.com/meywd/tamma/issues/new)
- **Feature requests:** Open a [GitHub Issue](https://github.com/meywd/tamma/issues/new)

---

Thank you for contributing to Tamma!
