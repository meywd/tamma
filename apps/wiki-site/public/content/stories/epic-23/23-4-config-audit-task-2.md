---
title: "Task 2: Configuration Audit Frontend Components"
sidebar:
  order: 230
---

**Story:** 23-4-configuration-audit
**Epic:** 23

## Task Description

Build the ConfigAuditPage and all child components: ConfigSourcesTable, ConfigKeyInventory, MissingConfigAlerts, ConfigDiffView, ProviderValidationPanel, PlatformValidationPanel, EnvVarCompleteness, ConfigChangeTimeline, and RestoreConfirmDialog.

## Acceptance Criteria

- Config sources table with priority order and status
- Searchable config key inventory with values, sources, validation status
- Red alert banner for missing/invalid required configuration
- Side-by-side diff view of current vs default configuration
- Provider connectivity test with latency result
- Platform connectivity test with repository list
- Environment variable checklist
- Config change timeline with restore capability (owner-only)

## Implementation Details

### Technical Requirements

- [ ] Replace placeholder `packages/dashboard/src/pages/monitoring/ConfigAuditPage.tsx`:
  - MonitoringLayout with title "Configuration Audit"
  - Tab navigation: Overview, Inventory, Diff, Environment, Validation, History

- [ ] Create `packages/dashboard/src/hooks/monitoring/useConfigAudit.ts`:
  ```typescript
  export interface UseConfigAuditResult {
    inventory: ConfigKeyEntry[];
    sources: ConfigSource[];
    diff: ConfigDiffEntry[];
    envVars: EnvVarStatus[];
    missing: ConfigKeyEntry[];
    history: ConfigChangeEntry[];
    loading: boolean;
    error: string | null;
    validateProvider: (provider: string) => Promise<ValidationResult>;
    validatePlatform: () => Promise<ValidationResult>;
    restoreConfig: (entryId: string) => Promise<void>;
    refresh: () => Promise<void>;
  }
  ```

- [ ] Create `packages/dashboard/src/components/monitoring/config/ConfigSourcesTable.tsx`:
  - DataTable: columns Name, Type, Location, Status, Last Modified, Priority
  - Sorted by priority ascending (highest first)
  - Status badges: active (green), inactive (gray)

- [ ] Create `packages/dashboard/src/components/monitoring/config/ConfigKeyInventory.tsx`:
  - DataTable with search bar (filters key names)
  - Columns: Key, Value (redacted), Source, Default Value, Validation, Type
  - Validation column: green check, red X, or yellow triangle with message tooltip
  - Secret values show `****xxxx` pattern
  - Clicking a row expands to show full details

- [ ] Create `packages/dashboard/src/components/monitoring/config/MissingConfigAlerts.tsx`:
  - Red banner at top of page listing all missing/invalid required keys
  - Each alert: key name, expected format, "Fix" link that scrolls to inventory row
  - Dismissible (per session only, reappears on refresh)

- [ ] Create `packages/dashboard/src/components/monitoring/config/ConfigDiffView.tsx`:
  - Toggle "Compare to Defaults" mode
  - Side-by-side: left = current, right = defaults
  - Green lines = additions, yellow = modifications, red = deletions
  - Only non-default values highlighted
  - Uses `<pre>` blocks with JSON formatting

- [ ] Create `packages/dashboard/src/components/monitoring/config/ProviderValidationPanel.tsx`:
  - Card per configured provider: name, model, API key presence
  - "Test Connection" button triggering POST /validate-provider
  - Result: success (green check + latency), failure (red X + error message)
  - Model list display if returned
  - Last validated timestamp
  - Loading spinner during validation

- [ ] Create `packages/dashboard/src/components/monitoring/config/PlatformValidationPanel.tsx`:
  - Similar to provider but for git platform
  - Shows auth mode, token status
  - "Test Connection" lists accessible repositories
  - Rate limit remaining from response

- [ ] Create `packages/dashboard/src/components/monitoring/config/EnvVarCompleteness.tsx`:
  - Grouped list: AI Provider keys, Infrastructure, Security, Server
  - Each var: name, set (green check / red X), source, redacted value
  - Referenced but missing vars highlighted in red

- [ ] Create `packages/dashboard/src/components/monitoring/config/ConfigChangeTimeline.tsx`:
  - Vertical timeline: newest first
  - Each entry: timestamp, user, key, old->new (redacted), source
  - "Restore" button (owner-only) opens RestoreConfirmDialog

- [ ] Create `packages/dashboard/src/components/monitoring/config/RestoreConfirmDialog.tsx`:
  - Modal: "Restore configuration to state from {timestamp}?"
  - Shows what will change
  - Confirm/Cancel buttons

- [ ] Create `packages/dashboard/src/services/monitoring/config-api-client.ts`

### Files to Create

- CREATE `packages/dashboard/src/components/monitoring/config/ConfigSourcesTable.tsx`
- CREATE `packages/dashboard/src/components/monitoring/config/ConfigKeyInventory.tsx`
- CREATE `packages/dashboard/src/components/monitoring/config/MissingConfigAlerts.tsx`
- CREATE `packages/dashboard/src/components/monitoring/config/ConfigDiffView.tsx`
- CREATE `packages/dashboard/src/components/monitoring/config/ProviderValidationPanel.tsx`
- CREATE `packages/dashboard/src/components/monitoring/config/PlatformValidationPanel.tsx`
- CREATE `packages/dashboard/src/components/monitoring/config/EnvVarCompleteness.tsx`
- CREATE `packages/dashboard/src/components/monitoring/config/ConfigChangeTimeline.tsx`
- CREATE `packages/dashboard/src/components/monitoring/config/RestoreConfirmDialog.tsx`
- CREATE `packages/dashboard/src/hooks/monitoring/useConfigAudit.ts`
- CREATE `packages/dashboard/src/services/monitoring/config-api-client.ts`

### Files to Modify

- MODIFY `packages/dashboard/src/pages/monitoring/ConfigAuditPage.tsx` -- replace placeholder

### Dependencies

- Story 23-12: MonitoringLayout, DataTable, StatusBadge, EmptyState, ErrorBanner
- Task 1: Config audit API endpoints

## Testing Strategy

### Unit Tests

- [ ] ConfigKeyInventory: renders all keys with correct redaction
- [ ] ConfigKeyInventory: search filters key names
- [ ] MissingConfigAlerts: renders alert for each missing key
- [ ] MissingConfigAlerts: "Fix" link scrolls to correct row
- [ ] ConfigDiffView: shows additions in green, modifications in yellow
- [ ] ProviderValidationPanel: "Test Connection" calls validate endpoint
- [ ] ProviderValidationPanel: shows success/failure result
- [ ] EnvVarCompleteness: shows set/unset status correctly
- [ ] ConfigChangeTimeline: renders entries in reverse chronological order
- [ ] ConfigChangeTimeline: restore button only shown for owner
- [ ] RestoreConfirmDialog: confirm triggers restore API call
- [ ] useConfigAudit: fetches all endpoints on mount

## Completion Checklist

- [ ] All 9 child components created
- [ ] Tab navigation between sections
- [ ] Secret redaction in all value displays
- [ ] Diff view with color-coded changes
- [ ] Validation panels with live testing
- [ ] Change history with restore capability
- [ ] Tests written and passing
- [ ] TypeScript strict mode compiles
