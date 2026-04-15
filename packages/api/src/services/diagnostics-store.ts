/**
 * Diagnostics Store Interface + InMemory Implementation
 *
 * Story 9-2: Diagnostics Service + API
 *
 * Defines the contract for storing/querying provider call diagnostics
 * (costs, tokens, latency, errors). Both the TS engine and Elsa workflows
 * write to the same store for a unified view of usage.
 */

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

/** A single diagnostics record in the store. */
export interface DiagnosticsRecord {
  id: string;
  accountId: string | null;
  eventType: string;
  providerName: string;
  model: string | null;
  agentType: string | null;
  projectId: string | null;
  engineId: string | null;
  taskId: string | null;
  taskType: string | null;
  inputTokens: number;
  outputTokens: number;
  latencyMs: number;
  costUsd: number;
  success: boolean;
  errorCode: string | null;
  errorMessage: string | null;
  correlationId: string | null;
  createdAt: string;
}

/** Input for inserting a diagnostics record. */
export interface DiagnosticsRecordInput {
  accountId?: string | null;
  eventType: string;
  providerName: string;
  model?: string | null;
  agentType?: string | null;
  projectId?: string | null;
  engineId?: string | null;
  taskId?: string | null;
  taskType?: string | null;
  inputTokens?: number;
  outputTokens?: number;
  latencyMs?: number;
  costUsd?: number;
  success: boolean;
  errorCode?: string | null;
  errorMessage?: string | null;
  correlationId?: string | null;
}

/** Filters for querying diagnostics. */
export interface DiagnosticsQueryOptions {
  accountId?: string | null;
  provider?: string;
  model?: string;
  from?: string;
  to?: string;
  limit?: number;
  offset?: number;
}

/** A single group in an aggregated report. */
export interface DiagnosticsReportGroup {
  key: string;
  totalCost: number;
  totalTokens: number;
  avgLatency: number;
  errorRate: number;
  count: number;
}

/** Report query options. */
export interface DiagnosticsReportOptions {
  accountId?: string | null;
  from?: string;
  to?: string;
  groupBy: 'provider' | 'model' | 'agentType';
}

/** Budget status for an account. */
export interface BudgetStatus {
  spent: number;
  limit: number;
  remaining: number;
  percentUsed: number;
}

// ---------------------------------------------------------------------------
// IDiagnosticsStore Interface
// ---------------------------------------------------------------------------

export interface IDiagnosticsStore {
  /** Insert one or more diagnostics records. Returns count of records inserted. */
  insert(records: DiagnosticsRecordInput[]): Promise<number>;

  /** Query diagnostics records with filters. */
  query(options: DiagnosticsQueryOptions): Promise<{ items: DiagnosticsRecord[]; total: number }>;

  /** Generate an aggregated report. */
  report(options: DiagnosticsReportOptions): Promise<DiagnosticsReportGroup[]>;

  /** Check budget status for an account. */
  getBudget(accountId: string, limitUsd: number): Promise<BudgetStatus>;
}

// ---------------------------------------------------------------------------
// Validation
// ---------------------------------------------------------------------------

const VALID_EVENT_TYPES = new Set([
  'tool:invoke',
  'tool:complete',
  'tool:error',
  'provider:call',
  'provider:complete',
  'provider:error',
]);

const MAX_BATCH_SIZE = 100;

function validateRecordInput(input: DiagnosticsRecordInput): void {
  if (!input.eventType || !VALID_EVENT_TYPES.has(input.eventType)) {
    throw new Error(`Invalid event type: ${input.eventType}`);
  }
  if (!input.providerName || input.providerName.length === 0) {
    throw new Error('providerName is required');
  }
  if (input.providerName.length > 128) {
    throw new Error('providerName too long (max 128)');
  }
}

// ---------------------------------------------------------------------------
// InMemoryDiagnosticsStore
// ---------------------------------------------------------------------------

let nextId = 1;

export class InMemoryDiagnosticsStore implements IDiagnosticsStore {
  private records: DiagnosticsRecord[] = [];

  async insert(inputs: DiagnosticsRecordInput[]): Promise<number> {
    if (inputs.length === 0) return 0;
    if (inputs.length > MAX_BATCH_SIZE) {
      throw new Error(`Batch size ${inputs.length} exceeds max ${MAX_BATCH_SIZE}`);
    }

    for (const input of inputs) {
      validateRecordInput(input);
    }

    const now = new Date().toISOString();
    let count = 0;

    for (const input of inputs) {
      const record: DiagnosticsRecord = {
        id: `diag-${nextId++}`,
        accountId: input.accountId ?? null,
        eventType: input.eventType,
        providerName: input.providerName,
        model: input.model ?? null,
        agentType: input.agentType ?? null,
        projectId: input.projectId ?? null,
        engineId: input.engineId ?? null,
        taskId: input.taskId ?? null,
        taskType: input.taskType ?? null,
        inputTokens: input.inputTokens ?? 0,
        outputTokens: input.outputTokens ?? 0,
        latencyMs: input.latencyMs ?? 0,
        costUsd: input.costUsd ?? 0,
        success: input.success,
        errorCode: input.errorCode ?? null,
        errorMessage: input.errorMessage ?? null,
        correlationId: input.correlationId ?? null,
        createdAt: now,
      };
      this.records.push(record);
      count++;
    }

    return count;
  }

  async query(options: DiagnosticsQueryOptions): Promise<{ items: DiagnosticsRecord[]; total: number }> {
    let filtered = [...this.records];

    if (options.accountId !== undefined) {
      filtered = filtered.filter((r) => r.accountId === options.accountId);
    }
    if (options.provider) {
      filtered = filtered.filter((r) => r.providerName === options.provider);
    }
    if (options.model) {
      filtered = filtered.filter((r) => r.model === options.model);
    }
    if (options.from) {
      const fromTime = new Date(options.from).getTime();
      filtered = filtered.filter((r) => new Date(r.createdAt).getTime() >= fromTime);
    }
    if (options.to) {
      const toTime = new Date(options.to).getTime();
      filtered = filtered.filter((r) => new Date(r.createdAt).getTime() <= toTime);
    }

    // Sort by createdAt descending
    filtered.sort((a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime());

    const total = filtered.length;
    const offset = options.offset ?? 0;
    const limit = options.limit ?? 50;
    const items = filtered.slice(offset, offset + limit);

    return { items, total };
  }

  async report(options: DiagnosticsReportOptions): Promise<DiagnosticsReportGroup[]> {
    let filtered = [...this.records];

    if (options.accountId !== undefined) {
      filtered = filtered.filter((r) => r.accountId === options.accountId);
    }
    if (options.from) {
      const fromTime = new Date(options.from).getTime();
      filtered = filtered.filter((r) => new Date(r.createdAt).getTime() >= fromTime);
    }
    if (options.to) {
      const toTime = new Date(options.to).getTime();
      filtered = filtered.filter((r) => new Date(r.createdAt).getTime() <= toTime);
    }

    // Group by the specified field
    const groups = new Map<string, DiagnosticsRecord[]>();
    for (const record of filtered) {
      let key: string;
      switch (options.groupBy) {
        case 'provider':
          key = record.providerName;
          break;
        case 'model':
          key = record.model ?? 'unknown';
          break;
        case 'agentType':
          key = record.agentType ?? 'unknown';
          break;
      }
      const existing = groups.get(key);
      if (existing) {
        existing.push(record);
      } else {
        groups.set(key, [record]);
      }
    }

    const result: DiagnosticsReportGroup[] = [];
    for (const [key, records] of groups) {
      const count = records.length;
      const totalCost = records.reduce((sum, r) => sum + r.costUsd, 0);
      const totalTokens = records.reduce((sum, r) => sum + r.inputTokens + r.outputTokens, 0);
      const avgLatency = count > 0 ? records.reduce((sum, r) => sum + r.latencyMs, 0) / count : 0;
      const errorCount = records.filter((r) => !r.success).length;
      const errorRate = count > 0 ? errorCount / count : 0;

      result.push({ key, totalCost, totalTokens, avgLatency, errorRate, count });
    }

    return result;
  }

  async getBudget(accountId: string, limitUsd: number): Promise<BudgetStatus> {
    const accountRecords = this.records.filter((r) => r.accountId === accountId);
    const spent = accountRecords.reduce((sum, r) => sum + r.costUsd, 0);
    const remaining = Math.max(0, limitUsd - spent);
    const percentUsed = limitUsd > 0 ? (spent / limitUsd) * 100 : 0;

    return { spent, limit: limitUsd, remaining, percentUsed };
  }
}
