# Contributing to Tamma

Thank you for your interest in contributing to Tamma! This guide will help you get started.

## Table of Contents

- [Code of Conduct](#code-of-conduct)
- [How Can I Contribute?](#how-can-i-contribute)
- [Development Setup](#development-setup)
- [Development Workflow](#development-workflow)
- [Coding Standards](#coding-standards)
- [Testing Requirements](#testing-requirements)
- [Pull Request Process](#pull-request-process)

---

## Code of Conduct

Be respectful, inclusive, and professional. We're building a platform that will autonomously maintain itself - let's maintain a high standard of collaboration.

---

## How Can I Contribute?

### 1. Pick Up a Task from Epic 1

Browse [Epic 1 Issues](https://github.com/meywd/tamma/issues?q=is%3Aissue+is%3Aopen+milestone%3A%22Epic+1%3A+Foundation+%26+Core+Infrastructure%22+label%3Aready-for-dev) and pick a task labeled `ready-for-dev`.

Each issue includes:
- Task description
- Subtask checklist
- Acceptance criteria
- Link to detailed story documentation

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

### Repository Structure

```
tamma/
├── packages/
│   ├── cli/              # Command-line interface
│   ├── providers/        # AI provider abstraction
│   ├── platforms/        # Git platform abstraction
│   ├── config/           # Configuration management
│   ├── orchestrator/     # Orchestrator mode
│   ├── worker/           # Worker mode
│   └── shared/           # Shared utilities
├── docs/                 # Technical documentation
│   ├── stories/          # User stories
│   ├── PRD.md           # Product requirements
│   ├── architecture.md  # Architecture doc
│   ├── epics.md         # Epic breakdown
│   └── tech-spec-*.md   # Technical specifications
├── scripts/             # Build and utility scripts
└── wiki/                # Wiki content (copy to GitHub wiki)
```

---

## Development Workflow

### 1. Assign Yourself to an Issue

Comment on the issue: "I'd like to work on this" and wait for assignment.

### 2. Create a Feature Branch

```bash
git checkout -b feature/issue-{number}-{short-description}
# Example: git checkout -b feature/issue-8-provider-interface
```

### 3. Implement the Feature

Follow the subtask checklist in the GitHub issue.

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

# Fix linting issues
pnpm lint:fix

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
- `feat:` - New feature
- `fix:` - Bug fix
- `docs:` - Documentation changes
- `test:` - Test additions/changes
- `refactor:` - Code refactoring
- `chore:` - Build/tooling changes

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

- **Strict mode:** All packages use TypeScript strict mode
- **Type safety:** No `any` types (use `unknown` + type guards)
- **Interfaces:** Prefer interfaces over types for object shapes
- **Naming:** PascalCase for classes/interfaces, camelCase for functions/variables

### Code Style

- **Formatting:** Prettier with 2-space indentation
- **Linting:** ESLint with recommended rules
- **Line length:** 100 characters max
- **Imports:** Organize imports (external → internal → relative)

### Error Handling

- Use custom error classes extending `TammaError`
- Include context in error messages
- Log errors with structured logging (Pino)

### Documentation

- **JSDoc:** Document all public APIs
- **README:** Each package has a README
- **Inline comments:** Explain "why", not "what"

---

## Testing Requirements

### Unit Tests (Required)

Every module must have unit tests covering:
- Happy path scenarios
- Error cases
- Edge cases
- Boundary conditions

Example:

```typescript
import { describe, it, expect } from 'vitest';
import { ClaudeProvider } from './claude-provider';

describe('ClaudeProvider', () => {
  it('should generate code successfully', async () => {
    const provider = new ClaudeProvider({ apiKey: 'test-key' });
    const result = await provider.generateCode({ prompt: 'Hello' });
    expect(result).toBeDefined();
  });

  it('should handle rate limit errors', async () => {
    const provider = new ClaudeProvider({ apiKey: 'test-key' });
    await expect(provider.generateCode({ prompt: 'X'.repeat(1000000) }))
      .rejects.toThrow('Rate limit exceeded');
  });
});
```

### Integration Tests (Conditional)

Required for:
- AI provider integrations (test with real APIs)
- Git platform integrations (test with real platforms)
- Database operations (test with PostgreSQL)

Use test accounts with rate limits to avoid costs.

### Test Naming

- `*.test.ts` - Unit tests
- `*.integration.test.ts` - Integration tests
- `*.spec.ts` - Behavior specifications

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

### Review Process

1. **Automated Checks:** GitHub Actions runs tests, linting, type checking
2. **Code Review:** Maintainer reviews code for quality and design
3. **Changes Requested:** Address feedback and push updates
4. **Approval:** Maintainer approves PR
5. **Merge:** Maintainer merges PR (squash and merge)

### After Merge

- Issue automatically closed (if referenced with `Closes #N`)
- CI/CD builds and tests main branch
- Documentation deployed (if changed)

---

## Questions?

- **General questions:** Open a [GitHub Discussion](https://github.com/meywd/tamma/discussions)
- **Bug reports:** Open a [GitHub Issue](https://github.com/meywd/tamma/issues/new)
- **Feature requests:** Open a [GitHub Issue](https://github.com/meywd/tamma/issues/new)

---

## Recognition

Contributors will be listed in:
- `CONTRIBUTORS.md` file
- Project README
- Release notes (for significant contributions)

Thank you for contributing to Tamma!
