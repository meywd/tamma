# SaaS Architecture Constraints

These MUST be reflected in every story for Epics 17-22.

## 1. Static Workflows, Per-Tenant Config
- Workflow definitions (C# WorkflowBase) are SHARED across all tenants — one codebase
- Workflow INSTANCES are per-tenant — tagged with tenantId
- Config (providers, keys, budgets, prompts, tools) is per-tenant — loaded at dispatch time
- At dispatch: resolve tenant → load tenant config → pass as workflow input → all sub-workflows inherit

## 2. Event Store Isolation
- DCB events tagged with tenantId in the tags JSONB
- All queries scoped by tenantId — no cross-tenant data access
- PostgreSQL RLS enforces isolation at the database level

## 3. Agents Run on User's GitHub Runners
- Tamma Cloud orchestrates but does NOT run code generation
- GitHub App dispatches workflow_run to user's repo
- User's repo has `.github/workflows/tamma-agent.yml`
- Agent (Claude Code) runs on user's GitHub-hosted runner
- User's code never leaves their GitHub environment
- User pays for their own GitHub Actions minutes

## 4. CLI Mode Preserved
- `tamma start` works standalone, no cloud dependency
- CLI uses LocalExecutor (agents run on user's machine)
- SaaS uses GitHubActionsExecutor (agents run on user's runners)
- Same ELSA workflow engine, different executor

## 5. Isolation Layers
- Workflow instances: tenantId tag on every instance
- Events: tenantId in DCB tags
- Logs: tenantId field in OpenSearch, filtered per tenant
- Config: per-tenant settings in DB (IProvidersConfig)
- Compute: user's own GitHub runners
- Data: user's own GitHub repos
