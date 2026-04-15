/**
 * PostgreSQL-backed Diagnostics Store
 *
 * Story 9-2: Diagnostics Service + API
 *
 * Persists provider call diagnostics to the provider_diagnostics table
 * created in migration 014. Supports query, report aggregation, and
 * budget checking.
 */

import type pg from 'pg';

import type {
  IDiagnosticsStore,
  DiagnosticsRecord,
  DiagnosticsRecordInput,
  DiagnosticsQueryOptions,
  DiagnosticsReportGroup,
  DiagnosticsReportOptions,
  BudgetStatus,
} from './diagnostics-store.js';

// ---------------------------------------------------------------------------
// Validation (shared with in-memory impl)
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
// PgDiagnosticsStore
// ---------------------------------------------------------------------------

export class PgDiagnosticsStore implements IDiagnosticsStore {
  constructor(private readonly pool: pg.Pool) {}

  async insert(inputs: DiagnosticsRecordInput[]): Promise<number> {
    if (inputs.length === 0) return 0;
    if (inputs.length > MAX_BATCH_SIZE) {
      throw new Error(`Batch size ${inputs.length} exceeds max ${MAX_BATCH_SIZE}`);
    }

    for (const input of inputs) {
      validateRecordInput(input);
    }

    let count = 0;
    for (const input of inputs) {
      const result = await this.pool.query(
        `INSERT INTO provider_diagnostics
         (account_id, event_type, provider_name, model, agent_type,
          project_id, engine_id, task_id, task_type,
          input_tokens, output_tokens, latency_ms, cost_usd,
          success, error_code, error_message, correlation_id)
         VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17)`,
        [
          input.accountId ?? null,
          input.eventType,
          input.providerName,
          input.model ?? null,
          input.agentType ?? null,
          input.projectId ?? null,
          input.engineId ?? null,
          input.taskId ?? null,
          input.taskType ?? null,
          input.inputTokens ?? 0,
          input.outputTokens ?? 0,
          input.latencyMs ?? 0,
          input.costUsd ?? 0,
          input.success,
          input.errorCode ?? null,
          input.errorMessage ?? null,
          input.correlationId ?? null,
        ],
      );
      count += result.rowCount ?? 0;
    }

    return count;
  }

  async query(options: DiagnosticsQueryOptions): Promise<{ items: DiagnosticsRecord[]; total: number }> {
    const conditions: string[] = [];
    const params: unknown[] = [];
    let paramIndex = 1;

    if (options.accountId !== undefined) {
      if (options.accountId === null) {
        conditions.push('account_id IS NULL');
      } else {
        conditions.push(`account_id = $${paramIndex++}`);
        params.push(options.accountId);
      }
    }
    if (options.provider) {
      conditions.push(`provider_name = $${paramIndex++}`);
      params.push(options.provider);
    }
    if (options.model) {
      conditions.push(`model = $${paramIndex++}`);
      params.push(options.model);
    }
    if (options.from) {
      conditions.push(`created_at >= $${paramIndex++}`);
      params.push(options.from);
    }
    if (options.to) {
      conditions.push(`created_at <= $${paramIndex++}`);
      params.push(options.to);
    }

    const whereClause = conditions.length > 0 ? `WHERE ${conditions.join(' AND ')}` : '';
    const limit = options.limit ?? 50;
    const offset = options.offset ?? 0;

    // Get total count
    const countResult = await this.pool.query<{ count: string }>(
      `SELECT COUNT(*) as count FROM provider_diagnostics ${whereClause}`,
      params,
    );
    const total = parseInt(countResult.rows[0]?.count ?? '0', 10);

    // Get items
    const itemsResult = await this.pool.query<Record<string, unknown>>(
      `SELECT * FROM provider_diagnostics ${whereClause}
       ORDER BY created_at DESC
       LIMIT $${paramIndex++} OFFSET $${paramIndex++}`,
      [...params, limit, offset],
    );

    const items = itemsResult.rows.map((row) => this._mapRow(row));
    return { items, total };
  }

  async report(options: DiagnosticsReportOptions): Promise<DiagnosticsReportGroup[]> {
    const conditions: string[] = [];
    const params: unknown[] = [];
    let paramIndex = 1;

    if (options.accountId !== undefined) {
      if (options.accountId === null) {
        conditions.push('account_id IS NULL');
      } else {
        conditions.push(`account_id = $${paramIndex++}`);
        params.push(options.accountId);
      }
    }
    if (options.from) {
      conditions.push(`created_at >= $${paramIndex++}`);
      params.push(options.from);
    }
    if (options.to) {
      conditions.push(`created_at <= $${paramIndex++}`);
      params.push(options.to);
    }

    const whereClause = conditions.length > 0 ? `WHERE ${conditions.join(' AND ')}` : '';

    let groupColumn: string;
    switch (options.groupBy) {
      case 'provider':
        groupColumn = 'provider_name';
        break;
      case 'model':
        groupColumn = 'COALESCE(model, \'unknown\')';
        break;
      case 'agentType':
        groupColumn = 'COALESCE(agent_type, \'unknown\')';
        break;
    }

    const result = await this.pool.query<Record<string, unknown>>(
      `SELECT
         ${groupColumn} as key,
         SUM(cost_usd)::NUMERIC(12,6) as total_cost,
         SUM(input_tokens + output_tokens) as total_tokens,
         AVG(latency_ms)::NUMERIC(10,2) as avg_latency,
         CASE WHEN COUNT(*) > 0
           THEN (COUNT(*) FILTER (WHERE NOT success))::NUMERIC / COUNT(*)
           ELSE 0 END as error_rate,
         COUNT(*) as count
       FROM provider_diagnostics
       ${whereClause}
       GROUP BY ${groupColumn}
       ORDER BY count DESC`,
      params,
    );

    return result.rows.map((row) => ({
      key: String(row['key']),
      totalCost: parseFloat(String(row['total_cost'] ?? '0')),
      totalTokens: parseInt(String(row['total_tokens'] ?? '0'), 10),
      avgLatency: parseFloat(String(row['avg_latency'] ?? '0')),
      errorRate: parseFloat(String(row['error_rate'] ?? '0')),
      count: parseInt(String(row['count'] ?? '0'), 10),
    }));
  }

  async getBudget(accountId: string, limitUsd: number): Promise<BudgetStatus> {
    const result = await this.pool.query<{ total_spent: string }>(
      `SELECT COALESCE(SUM(cost_usd), 0)::NUMERIC(12,6) as total_spent
       FROM provider_diagnostics
       WHERE account_id = $1`,
      [accountId],
    );

    const spent = parseFloat(result.rows[0]?.total_spent ?? '0');
    const remaining = Math.max(0, limitUsd - spent);
    const percentUsed = limitUsd > 0 ? (spent / limitUsd) * 100 : 0;

    return { spent, limit: limitUsd, remaining, percentUsed };
  }

  private _mapRow(row: Record<string, unknown>): DiagnosticsRecord {
    return {
      id: String(row['id']),
      accountId: row['account_id'] !== null ? String(row['account_id']) : null,
      eventType: String(row['event_type']),
      providerName: String(row['provider_name']),
      model: row['model'] !== null ? String(row['model']) : null,
      agentType: row['agent_type'] !== null ? String(row['agent_type']) : null,
      projectId: row['project_id'] !== null ? String(row['project_id']) : null,
      engineId: row['engine_id'] !== null ? String(row['engine_id']) : null,
      taskId: row['task_id'] !== null ? String(row['task_id']) : null,
      taskType: row['task_type'] !== null ? String(row['task_type']) : null,
      inputTokens: Number(row['input_tokens'] ?? 0),
      outputTokens: Number(row['output_tokens'] ?? 0),
      latencyMs: Number(row['latency_ms'] ?? 0),
      costUsd: parseFloat(String(row['cost_usd'] ?? '0')),
      success: Boolean(row['success']),
      errorCode: row['error_code'] !== null ? String(row['error_code']) : null,
      errorMessage: row['error_message'] !== null ? String(row['error_message']) : null,
      correlationId: row['correlation_id'] !== null ? String(row['correlation_id']) : null,
      createdAt: String(row['created_at']),
    };
  }
}
