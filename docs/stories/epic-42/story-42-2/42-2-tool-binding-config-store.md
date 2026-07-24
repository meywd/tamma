# Story 42-2: Tool Binding & Config Store (two-scoping)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then check the knowledge base: `.dev/spikes/`, `.dev/bugs/`, `.dev/findings/`, `.dev/decisions/`.

## User Story

As a **user (single-user) or a tenant_admin (SaaS)**, I want to control **which tools are enabled** and
override their **per-role grant and autonomy floor**, stored under the right principal for my operating
mode, so that the tool catalog is governed by the same two-scoping ownership model as prompts — the
sole user owns it in single-user, the tenant_admin owns it in SaaS, and members can't edit it.

## Priority

P0 / Wave 1 — the persistence + resolution order that 42-3's gating reads. Ships right after 42-1.

## The gap (READ FIRST)

42-1 gives each tool a **system-default descriptor** (permission class + autonomy floor). But CLAUDE.md's
universal rule demands per-principal customization with **two** ownership models: in single-user the
sole user owns it; in SaaS the tenant_admin owns it and members don't. The Prompt Store already solves
exactly this shape — `prompt_overrides` keyed by `user_id` XOR `tenant_id`, per-mode resolution order,
member-write 403 in SaaS. There is **no equivalent for tools**: a tool's floor/enablement is currently
a hardcoded descriptor with no override layer.

## Scope

1. **`tool_bindings` table** — the tool analogue of `prompt_overrides`, same XOR-principal shape:

   ```sql
   CREATE TABLE tool_bindings (
     id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
     user_id UUID,                 -- set in single-user mode; NULL in SaaS
     tenant_id UUID,               -- set in SaaS mode; NULL in single-user
     tool_name TEXT NOT NULL,      -- matches IToolExecutor.ToolName
     enabled BOOLEAN NOT NULL DEFAULT true,
     autonomy_floor INTEGER,       -- override of descriptor floor (NULL = use system default)
     allowed_roles TEXT[],         -- AgentRole wire names permitted (NULL = descriptor/system default)
     secret_binding_name TEXT,     -- logical secret name → resolved to a SecretRef by 42-4 (NULL = tool default)
     config JSONB,                 -- tool-specific config (e.g. flag-provider base URL, HTTP host allowlist)
     created_at TIMESTAMPTZ DEFAULT now(),
     updated_at TIMESTAMPTZ DEFAULT now(),
     CONSTRAINT tool_principal_xor CHECK (
       (user_id IS NOT NULL AND tenant_id IS NULL)
       OR (user_id IS NULL AND tenant_id IS NOT NULL)),
     UNIQUE NULLS NOT DISTINCT (user_id, tenant_id, tool_name)
   );
   ```

2. **Resolution order (mirrors the Prompt Store exactly).**
   - **single-user** `(userId, toolName)`: user binding → system-default descriptor (42-1).
   - **SaaS** `(tenantId, toolName)`: tenant binding → system-default descriptor. **No per-user layer.**
   A binding overrides only the fields it sets (`autonomy_floor`, `allowed_roles`, `enabled`,
   `secret_binding_name`, `config`); unset fields fall through to the descriptor — never a full-record
   replace (same "override only what's set" semantics as prompts).

3. **RBAC (mirrors the Prompt Store).**
   | Action | single-user | SaaS |
   |---|---|---|
   | GET resolved binding | any user | any tenant member |
   | PUT/DELETE binding | any user | `tenant_owner`/`tenant_admin` only (member → 403) |
   | GET system defaults (descriptors) | any user | any member |

4. **API** (endpoint shape identical across modes; middleware picks the key by mode + caller, exactly
   like `/api/prompts`):
   ```
   GET    /api/tools                     — list catalog, resolved for the current principal
   GET    /api/tools/:toolName           — resolved binding + descriptor
   PUT    /api/tools/:toolName           — upsert binding (owner/admin only in SaaS)
   DELETE /api/tools/:toolName           — delete binding → fall back to descriptor (owner/admin only)
   GET    /api/tools/defaults            — system-default descriptors
   ```

5. **A `IToolBindingResolver`** that 42-3 calls: given `(principal, mode, toolName)` returns the
   effective `{ enabled, autonomyFloor, allowedRoles, secretBindingName, config }`. Reads the mode the
   same way the process settles it (CLAUDE.md Operating Modes) — never both keys.

## Acceptance Criteria

1. `tool_bindings` exists with the XOR constraint and `UNIQUE NULLS NOT DISTINCT` key; a test asserts a
   row with both `user_id` and `tenant_id` set (or both null) is rejected by the DB.
2. `IToolBindingResolver` returns the descriptor default when no binding exists, and the
   field-level-merged binding when one does (test: a binding overriding only `autonomy_floor` leaves
   `allowed_roles` at the descriptor value).
3. single-user resolution keys off `user_id`; SaaS off `tenant_id`; a test per mode asserts the other
   column is never consulted.
4. SaaS `member` role hits 403 on PUT/DELETE; `tenant_admin` succeeds (test). single-user any-user
   succeeds.
5. Deleting a binding falls back to the descriptor (test asserts resolved floor returns to the 42-1
   default).
6. `GET /api/tools` lists the merged catalog (DI-seeded + dynamically registered from 42-1) resolved for
   the caller.

## Events

`TOOL.BINDING_UPDATED` / `TOOL.BINDING_DELETED` DCB events (config-change audit) tagged with the
principal key and `toolName` — never the secret, never the `config` payload verbatim if it can carry a
credential (redact `config` values whose keys match the secret-field denylist). Emitted via the standard
`TammaEventEmitter` drain.

## Single-user vs SaaS

This story **is** the two-scoping model for tools — it is the whole point. It reuses the Prompt Store's
proven shape (XOR principal, per-mode resolution, member-write 403, no per-user SaaS layer) rather than
inventing a new one.

## Dependencies

- **42-1** (descriptors are the fall-through target; `tool_name`/floor semantics come from there).
- **Prompt Store** as the reference implementation to mirror (`prompt_overrides`, resolution order,
  RBAC middleware).
- **Unblocks:** 42-3 (consumes `IToolBindingResolver`), 42-4 (`secret_binding_name`).

## Risks

- **Divergence from the Prompt Store pattern.** If this reimplements resolution/RBAC subtly differently,
  the platform grows two inconsistent override models. Mitigation: extract or directly reuse the Prompt
  Store's mode-key middleware; a test asserts identical member-403 behavior.

## Estimated Effort

Medium. ~3 days (table + resolver + CRUD endpoints + RBAC, largely paralleling existing prompt code).
</content>
