/**
 * PostgreSQL-backed Event Store (Story 17-3)
 *
 * Implements IEventStore against the `engine_events` table from migration 011.
 * Each method wraps its query in `withTenantContext()` so the RLS session
 * variable is set inside a transactional scope — no caller needs to manage
 * the DB tenant context manually.
 */

import { randomUUID } from 'node:crypto';
import type pg from 'pg';
import type { EngineEvent, EngineEventType, IEventStore } from '@tamma/shared';
import { withTenantContext } from './with-tenant-context.js';

interface EngineEventRow {
  id: string;
  type: string;
  timestamp: string; // bigint comes as string from pg
  tenant_id: string;
  issue_number: number | null;
  data: Record<string, unknown>;
}

export class PgEventStore implements IEventStore {
  constructor(private readonly pool: pg.Pool) {}

  async record(event: Omit<EngineEvent, 'id' | 'timestamp'>): Promise<EngineEvent> {
    const id = randomUUID();
    const timestamp = Date.now();

    return withTenantContext(this.pool, event.tenantId, async (client) => {
      const result = await client.query<EngineEventRow>(
        `INSERT INTO engine_events (id, type, timestamp, tenant_id, issue_number, data)
         VALUES ($1, $2, $3, $4, $5, $6)
         RETURNING *`,
        [
          id,
          event.type,
          timestamp,
          event.tenantId,
          event.issueNumber ?? null,
          JSON.stringify(event.data),
        ],
      );

      return this._mapRow(result.rows[0]!);
    });
  }

  async getEvents(tenantId: string, issueNumber?: number): Promise<EngineEvent[]> {
    return withTenantContext(this.pool, tenantId, async (client) => {
      let query = 'SELECT * FROM engine_events WHERE tenant_id = $1';
      const params: unknown[] = [tenantId];

      if (issueNumber !== undefined) {
        query += ' AND issue_number = $2';
        params.push(issueNumber);
      }

      query += ' ORDER BY timestamp ASC, id ASC';

      const result = await client.query<EngineEventRow>(query, params);
      return result.rows.map((row) => this._mapRow(row));
    });
  }

  async getLastEvent(tenantId: string, type: EngineEventType): Promise<EngineEvent | undefined> {
    return withTenantContext(this.pool, tenantId, async (client) => {
      const result = await client.query<EngineEventRow>(
        `SELECT * FROM engine_events
         WHERE tenant_id = $1 AND type = $2
         ORDER BY timestamp DESC, id DESC
         LIMIT 1`,
        [tenantId, type],
      );

      const row = result.rows[0];
      return row !== undefined ? this._mapRow(row) : undefined;
    });
  }

  async clear(tenantId: string): Promise<void> {
    await withTenantContext(this.pool, tenantId, async (client) => {
      await client.query('DELETE FROM engine_events WHERE tenant_id = $1', [tenantId]);
    });
  }

  /** Convert a snake_case DB row to the camelCase EngineEvent shape. */
  private _mapRow(row: EngineEventRow): EngineEvent {
    const event: EngineEvent = {
      id: row.id,
      type: row.type as EngineEvent['type'],
      timestamp: Number(row.timestamp),
      tenantId: row.tenant_id,
      data: row.data,
    };

    // Respect exactOptionalPropertyTypes: only set issueNumber when non-null
    if (row.issue_number !== null) {
      event.issueNumber = row.issue_number;
    }

    return event;
  }
}
