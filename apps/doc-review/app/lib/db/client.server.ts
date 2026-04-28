import { drizzle } from 'drizzle-orm/d1';
import type { DrizzleD1Database } from 'drizzle-orm/d1';
import * as schema from './schema';

// Database type that includes both Drizzle ORM and underlying D1 database
export type Database = DrizzleD1Database<typeof schema> & {
  $client: D1Database;
};

export function getDb(env: { DB?: D1Database }): Database {
  if (!env?.DB) {
    throw new Error('Database binding "DB" is not configured.');
  }

  return drizzle(env.DB, { schema });
}

export function hasDatabase(env: { DB?: D1Database }): env is { DB: D1Database } {
  return Boolean(env?.DB);
}
