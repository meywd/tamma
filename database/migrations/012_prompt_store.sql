-- 012_prompt_store.sql
-- Story 27-1: Prompt Store Database Schema
--
-- Creates three tables for the prompt store:
--   - prompts:        Full role+action templates (80 system defaults)
--   - system_prompts: Role identity preambles (8 system defaults)
--   - action_prompts: Action-level default templates (10 system defaults)
--
-- System defaults use tenant_id IS NULL.
-- Tenant overrides use tenant_id = <tenant-uuid>.
-- Partial unique indexes handle the NULL vs non-NULL distinction.

-- =========================================================================
-- 1. prompts table
-- =========================================================================

CREATE TABLE IF NOT EXISTS prompts (
  id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id     UUID REFERENCES tenants(id) ON DELETE CASCADE,
  role          TEXT NOT NULL,
  action        TEXT NOT NULL,
  template      TEXT NOT NULL,
  system_prompt TEXT NOT NULL DEFAULT '',
  variables     JSONB NOT NULL DEFAULT '[]'::jsonb,
  enable_tools  BOOLEAN NOT NULL DEFAULT false,
  max_tokens    INTEGER NOT NULL DEFAULT 4096 CHECK (max_tokens > 0),
  version       INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
  created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  created_by    UUID,
  updated_by    UUID
);

-- Partial unique indexes for NULL tenant_id handling
CREATE UNIQUE INDEX IF NOT EXISTS idx_prompts_system_default
  ON prompts (role, action)
  WHERE tenant_id IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS idx_prompts_tenant_override
  ON prompts (tenant_id, role, action)
  WHERE tenant_id IS NOT NULL;

-- Lookup indexes
CREATE INDEX IF NOT EXISTS idx_prompts_tenant_id ON prompts (tenant_id);
CREATE INDEX IF NOT EXISTS idx_prompts_role_action ON prompts (role, action);

-- =========================================================================
-- 2. system_prompts table
-- =========================================================================

CREATE TABLE IF NOT EXISTS system_prompts (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id   UUID REFERENCES tenants(id) ON DELETE CASCADE,
  role        TEXT NOT NULL,
  prompt      TEXT NOT NULL,
  version     INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
  created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  created_by  UUID,
  updated_by  UUID
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_system_prompts_system_default
  ON system_prompts (role)
  WHERE tenant_id IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS idx_system_prompts_tenant_override
  ON system_prompts (tenant_id, role)
  WHERE tenant_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_system_prompts_tenant_id ON system_prompts (tenant_id);
CREATE INDEX IF NOT EXISTS idx_system_prompts_role ON system_prompts (role);

-- =========================================================================
-- 3. action_prompts table
-- =========================================================================

CREATE TABLE IF NOT EXISTS action_prompts (
  id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id     UUID REFERENCES tenants(id) ON DELETE CASCADE,
  action        TEXT NOT NULL,
  template      TEXT NOT NULL,
  variables     JSONB NOT NULL DEFAULT '[]'::jsonb,
  enable_tools  BOOLEAN NOT NULL DEFAULT false,
  max_tokens    INTEGER NOT NULL DEFAULT 4096 CHECK (max_tokens > 0),
  version       INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
  created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  created_by    UUID,
  updated_by    UUID
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_action_prompts_system_default
  ON action_prompts (action)
  WHERE tenant_id IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS idx_action_prompts_tenant_override
  ON action_prompts (tenant_id, action)
  WHERE tenant_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_action_prompts_tenant_id ON action_prompts (tenant_id);
CREATE INDEX IF NOT EXISTS idx_action_prompts_action ON action_prompts (action);

-- =========================================================================
-- 4. Seed system_prompts (8 role preambles)
-- =========================================================================

INSERT INTO system_prompts (tenant_id, role, prompt, version) VALUES
  (NULL, 'developer', 'You are an expert software developer working on the Tamma project. You write production-quality TypeScript code that passes strict compilation, follows established conventions, and includes proper error handling. You have deep expertise in Node.js, Fastify, PostgreSQL, and event-driven architectures.', 1),
  (NULL, 'tester', 'You are a testing specialist for the Tamma project. You write thorough, maintainable tests using Vitest 3.x with colocated test files. You have expertise in unit testing, integration testing, contract testing, and mocking strategies using MSW and vi.mock.', 1),
  (NULL, 'security', 'You are a security engineer specializing in application security for TypeScript/Node.js systems. You identify vulnerabilities (OWASP Top 10), review code for injection attacks, credential leaks, and insecure configurations. You validate input sanitization, authentication flows, and authorization boundaries.', 1),
  (NULL, 'devops', 'You are a DevOps engineer specializing in CI/CD pipelines, Docker containerization, Kubernetes orchestration, and infrastructure automation. You evaluate deployment strategies, infrastructure impact, and operational concerns for the Tamma platform.', 1),
  (NULL, 'architect', 'You are a software architect specializing in distributed systems, microservices, and event-driven architectures. You review system design, interface contracts, service boundaries, and architectural patterns. You have deep knowledge of DDD, CQRS, event sourcing, and the Tamma DCB pattern.', 1),
  (NULL, 'product_owner', 'You are a product owner with expertise in agile development, user story management, and feature prioritization. You assess business value, scope decisions, and user impact. You communicate clearly with both technical and non-technical stakeholders.', 1),
  (NULL, 'senior_developer', 'You are a senior developer and technical lead on the Tamma project. You create detailed implementation plans, decompose complex tasks, and make technology decisions. You balance code quality with delivery speed and mentor other developers through your plans.', 1),
  (NULL, 'tech_writer', 'You are a technical writer who produces clear, concise documentation for developer audiences. You summarize technical findings, write issue comments, create PR descriptions, and produce changelog entries. You use precise language and avoid ambiguity.', 1)
ON CONFLICT DO NOTHING;

-- =========================================================================
-- 5. Seed action_prompts (10 action-level defaults)
-- =========================================================================
-- These are generic action templates used when no role-specific template exists.

INSERT INTO action_prompts (tenant_id, action, template, variables, enable_tools, max_tokens, version) VALUES
  (NULL, 'context-scan',
   E'Scan the codebase for a {{workItemType}} work item.\n\n## Work Item\n{{workItemJson}}\n\n## Previous Findings\n{{previousFindings}}\n\nIdentify relevant files, interfaces, dependencies, conventions, and risks. Output structured JSON findings.',
   '["workItemType","workItemJson","previousFindings"]'::jsonb, true, 4096, 1),

  (NULL, 'plan',
   E'Create an implementation plan.\n\n## Work Item\n{{workItemJson}}\n\n## Context\n{{contextFindings}}\n\n## Conventions\n{{conventions}}\n\nBreak down into discrete tasks with files, dependencies, complexity, and testing strategy. Output as JSON.',
   '["workItemJson","contextFindings","conventions"]'::jsonb, true, 8192, 1),

  (NULL, 'plan-review',
   E'Review an implementation plan.\n\n## Work Item\n{{workItemJson}}\n\n## Plan\n{{planJson}}\n\n## Conventions\n{{conventions}}\n\nCheck for missing tasks, edge cases, security, and convention compliance. Output issues and verdict as JSON.',
   '["workItemJson","planJson","conventions"]'::jsonb, false, 4096, 1),

  (NULL, 'implement',
   E'Implement code changes.\n\n## Work Item\n{{workItemJson}}\n\n## Plan\n{{planJson}}\n\n## Current Task\n{{currentTask}}\n\n## Conventions\n{{conventions}}\n\n## Code Context\n{{codeContext}}\n\nFollow strict TypeScript conventions. Output complete file implementations.',
   '["workItemJson","planJson","currentTask","conventions","codeContext"]'::jsonb, true, 16384, 1),

  (NULL, 'write-tests',
   E'Write tests for the target code.\n\n## Test Target\n{{testTarget}}\n\n## Source Code\n{{sourceCode}}\n\n## Conventions\n{{conventions}}\n\nCover happy paths, error cases, and edge cases. Use Vitest with colocated test files.',
   '["testTarget","sourceCode","conventions"]'::jsonb, true, 8192, 1),

  (NULL, 'refactor',
   E'Analyze and refactor code.\n\n## Target Code\n{{targetCode}}\n\n## Refactoring Goal\n{{refactoringGoal}}\n\n## Conventions\n{{conventions}}\n\nIdentify issues, propose changes, provide refactored code, and verification steps.',
   '["targetCode","refactoringGoal","conventions"]'::jsonb, true, 8192, 1),

  (NULL, 'code-review',
   E'Review code changes in a pull request.\n\n## PR Description\n{{prDescription}}\n\n## Diff\n{{diff}}\n\n## Conventions\n{{conventions}}\n\nCheck for bugs, security issues, convention violations, and test coverage. Output issues and summary as JSON.',
   '["prDescription","diff","conventions"]'::jsonb, false, 8192, 1),

  (NULL, 'triage',
   E'Triage an issue or alert.\n\n## Issue / Alert\n{{issueJson}}\n\n## Repository Context\n{{repoContext}}\n\nClassify type, severity, priority, owner role, and estimated effort. Output as JSON.',
   '["issueJson","repoContext"]'::jsonb, false, 2048, 1),

  (NULL, 'summarize',
   E'Summarize findings for an issue comment.\n\n## Work Item\n{{workItemJson}}\n\n## Findings\n{{findings}}\n\n## Target Audience\n{{audience}}\n\nWrite a concise summary under 500 words with key findings and action items.',
   '["workItemJson","findings","audience"]'::jsonb, false, 2048, 1),

  (NULL, 'debug',
   E'Diagnose and fix a failure.\n\n## Error Context\n{{errorContext}}\n\n## Stack Trace\n{{stackTrace}}\n\n## Relevant Code\n{{relevantCode}}\n\n## Conventions\n{{conventions}}\n\n## Recent Changes\n{{recentChanges}}\n\nIdentify root cause, provide fix, and verification steps. Output as JSON.',
   '["errorContext","stackTrace","relevantCode","conventions","recentChanges"]'::jsonb, true, 8192, 1)
ON CONFLICT DO NOTHING;

-- =========================================================================
-- 6. Seed prompts (80 role+action templates)
-- =========================================================================
-- The 80 rows are seeded from application code at startup rather than
-- in this migration, because the templates contain large multi-line strings
-- with code fences and dynamic interpolation that would be unwieldy in SQL.
-- The application calls PgPromptStore.seedDefaults() on startup, which
-- uses ON CONFLICT DO NOTHING for idempotency.
--
-- This migration creates the tables and indexes; the application seeds
-- the 80 role+action templates on first run.
-- =========================================================================
