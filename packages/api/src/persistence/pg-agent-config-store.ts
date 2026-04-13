/**
 * PostgreSQL-backed agent config store.
 *
 * Maps snake_case DB columns to camelCase AgentConfigRow properties.
 * Implements the resolution chain: account → system default → hardcoded.
 */

import type pg from 'pg';

import type {
  IAgentConfigStore,
  AgentConfigRow,
  AgentConfigDocument,
  ResolvedAgentConfig,
} from './agent-config-store.js';
import { HARDCODED_AGENT_CONFIG } from './agent-config-store.js';

/** PostgreSQL implementation of IAgentConfigStore. */
export class PgAgentConfigStore implements IAgentConfigStore {
  constructor(private readonly pool: pg.Pool) {}

  async resolve(accountId: string): Promise<ResolvedAgentConfig> {
    // Try account-specific first, then system default, in one query
    const result = await this.pool.query<Record<string, unknown>>(
      `SELECT *,
              CASE WHEN account_id = $1 THEN 1 ELSE 2 END AS priority
       FROM agent_configs
       WHERE account_id = $1 OR account_id IS NULL
       ORDER BY priority
       LIMIT 1`,
      [accountId],
    );

    if (result.rows.length === 0) {
      return {
        config: structuredClone(HARDCODED_AGENT_CONFIG) as AgentConfigDocument,
        source: 'hardcoded',
        version: 0,
      };
    }

    const row = this.mapRow(result.rows[0]!);
    return {
      config: row.config,
      source: row.accountId !== null ? 'account' : 'system',
      version: row.version,
    };
  }

  async getByAccountId(accountId: string | null): Promise<AgentConfigRow | null> {
    const result = accountId !== null
      ? await this.pool.query<Record<string, unknown>>(
          'SELECT * FROM agent_configs WHERE account_id = $1',
          [accountId],
        )
      : await this.pool.query<Record<string, unknown>>(
          'SELECT * FROM agent_configs WHERE account_id IS NULL',
        );

    if (result.rows.length === 0) return null;
    return this.mapRow(result.rows[0]!);
  }

  async upsert(
    accountId: string | null,
    config: AgentConfigDocument,
    userId?: string | null,
  ): Promise<AgentConfigRow> {
    const by = userId ?? null;
    const configJson = JSON.stringify(config);

    let result: pg.QueryResult<Record<string, unknown>>;

    if (accountId !== null) {
      result = await this.pool.query<Record<string, unknown>>(
        `INSERT INTO agent_configs (account_id, config, version, created_by, updated_by)
         VALUES ($1, $2::jsonb, 1, $3, $3)
         ON CONFLICT (account_id) WHERE account_id IS NOT NULL
         DO UPDATE SET
           config = $2::jsonb,
           version = agent_configs.version + 1,
           updated_at = NOW(),
           updated_by = $3
         RETURNING *`,
        [accountId, configJson, by],
      );
    } else {
      // System default (account_id IS NULL) — special conflict handling
      result = await this.pool.query<Record<string, unknown>>(
        `INSERT INTO agent_configs (account_id, config, version, created_by, updated_by)
         VALUES (NULL, $1::jsonb, 1, $2, $2)
         ON CONFLICT ((1)) WHERE account_id IS NULL
         DO UPDATE SET
           config = $1::jsonb,
           version = agent_configs.version + 1,
           updated_at = NOW(),
           updated_by = $2
         RETURNING *`,
        [configJson, by],
      );
    }

    return this.mapRow(result.rows[0]!);
  }

  async deleteByAccountId(accountId: string): Promise<boolean> {
    const result = await this.pool.query(
      'DELETE FROM agent_configs WHERE account_id = $1',
      [accountId],
    );
    return (result.rowCount ?? 0) > 0;
  }

  private mapRow(row: Record<string, unknown>): AgentConfigRow {
    return {
      id: String(row['id']),
      accountId: row['account_id'] !== null && row['account_id'] !== undefined
        ? String(row['account_id'])
        : null,
      config: row['config'] as AgentConfigDocument,
      version: Number(row['version']),
      createdAt: String(row['created_at']),
      updatedAt: String(row['updated_at']),
      createdBy: row['created_by'] !== null && row['created_by'] !== undefined
        ? String(row['created_by'])
        : null,
      updatedBy: row['updated_by'] !== null && row['updated_by'] !== undefined
        ? String(row['updated_by'])
        : null,
    };
  }
}
