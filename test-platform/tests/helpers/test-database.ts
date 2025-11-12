import { Knex, knex } from 'knex';
import { config } from 'dotenv';
import path from 'path';

// Load test environment variables
config({ path: path.resolve(process.cwd(), '.env.test') });

/**
 * Test database configuration
 */
export const testDbConfig: Knex.Config = {
  client: 'pg',
  connection: {
    host: process.env.TEST_DB_HOST || 'localhost',
    port: parseInt(process.env.TEST_DB_PORT || '5432', 10),
    user: process.env.TEST_DB_USER || 'postgres',
    password: process.env.TEST_DB_PASSWORD || 'postgres',
    database: process.env.TEST_DB_NAME || 'test_platform_test',
  },
  pool: {
    min: 1,
    max: 5,
  },
  migrations: {
    directory: path.resolve(process.cwd(), 'database/migrations'),
    extension: 'ts',
    tableName: 'knex_migrations',
  },
  seeds: {
    directory: path.resolve(process.cwd(), 'database/seeds'),
    extension: 'ts',
  },
};

/**
 * Get test database connection
 */
export async function getTestDatabase(): Promise<Knex> {
  const db = knex(testDbConfig);

  // Verify connection
  try {
    await db.raw('SELECT 1');
  } catch (error) {
    console.error('Failed to connect to test database:', error);
    throw new Error('Test database connection failed');
  }

  return db;
}

/**
 * Clean all tables in the test database
 */
export async function cleanDatabase(db: Knex): Promise<void> {
  await db.raw(`
    DO $$ DECLARE
      r RECORD;
    BEGIN
      -- Disable all triggers
      FOR r IN (SELECT tablename FROM pg_tables WHERE schemaname = 'public') LOOP
        EXECUTE 'ALTER TABLE public.' || quote_ident(r.tablename) || ' DISABLE TRIGGER ALL';
      END LOOP;

      -- Truncate all tables
      FOR r IN (SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename != 'knex_migrations' AND tablename != 'knex_migrations_lock') LOOP
        EXECUTE 'TRUNCATE TABLE public.' || quote_ident(r.tablename) || ' CASCADE';
      END LOOP;

      -- Re-enable all triggers
      FOR r IN (SELECT tablename FROM pg_tables WHERE schemaname = 'public') LOOP
        EXECUTE 'ALTER TABLE public.' || quote_ident(r.tablename) || ' ENABLE TRIGGER ALL';
      END LOOP;
    END $$;
  `);
}

/**
 * Drop all tables in the test database
 */
export async function dropAllTables(db: Knex): Promise<void> {
  await db.raw('DROP SCHEMA public CASCADE; CREATE SCHEMA public;');
  await db.raw('GRANT ALL ON SCHEMA public TO postgres;');
  await db.raw('GRANT ALL ON SCHEMA public TO public;');
}

/**
 * Get all table names in the database
 */
export async function getTableNames(db: Knex): Promise<string[]> {
  const result = await db.raw(`
    SELECT table_name
    FROM information_schema.tables
    WHERE table_schema = 'public'
    AND table_type = 'BASE TABLE'
    ORDER BY table_name
  `);
  return result.rows.map((row: any) => row.table_name);
}

/**
 * Check if a table exists
 */
export async function tableExists(db: Knex, tableName: string): Promise<boolean> {
  const result = await db.raw(`
    SELECT EXISTS (
      SELECT FROM information_schema.tables
      WHERE table_schema = 'public'
      AND table_name = ?
    )
  `, [tableName]);
  return result.rows[0].exists;
}

/**
 * Get column information for a table
 */
export async function getTableColumns(db: Knex, tableName: string): Promise<any[]> {
  const result = await db.raw(`
    SELECT
      column_name,
      data_type,
      is_nullable,
      column_default,
      character_maximum_length
    FROM information_schema.columns
    WHERE table_schema = 'public'
    AND table_name = ?
    ORDER BY ordinal_position
  `, [tableName]);
  return result.rows;
}

/**
 * Get index information for a table
 */
export async function getTableIndexes(db: Knex, tableName: string): Promise<any[]> {
  const result = await db.raw(`
    SELECT
      indexname,
      indexdef
    FROM pg_indexes
    WHERE schemaname = 'public'
    AND tablename = ?
    ORDER BY indexname
  `, [tableName]);
  return result.rows;
}

/**
 * Get foreign key constraints for a table
 */
export async function getTableForeignKeys(db: Knex, tableName: string): Promise<any[]> {
  const result = await db.raw(`
    SELECT
      tc.constraint_name,
      kcu.column_name,
      ccu.table_name AS foreign_table_name,
      ccu.column_name AS foreign_column_name,
      rc.update_rule,
      rc.delete_rule
    FROM information_schema.table_constraints AS tc
    JOIN information_schema.key_column_usage AS kcu
      ON tc.constraint_name = kcu.constraint_name
      AND tc.table_schema = kcu.table_schema
    JOIN information_schema.constraint_column_usage AS ccu
      ON ccu.constraint_name = tc.constraint_name
      AND ccu.table_schema = tc.table_schema
    JOIN information_schema.referential_constraints AS rc
      ON rc.constraint_name = tc.constraint_name
      AND rc.constraint_schema = tc.table_schema
    WHERE tc.constraint_type = 'FOREIGN KEY'
      AND tc.table_schema = 'public'
      AND tc.table_name = ?
    ORDER BY tc.constraint_name, kcu.ordinal_position
  `, [tableName]);
  return result.rows;
}

/**
 * Execute migrations up to a specific version
 */
export async function migrateToVersion(db: Knex, version: string): Promise<void> {
  // This is a simplified version - in reality, you'd need to implement
  // logic to run migrations up to a specific version
  const migrations = await db.migrate.list(testDbConfig);
  const targetIndex = migrations[0].findIndex((m: any) => m.file.includes(version));

  if (targetIndex === -1) {
    throw new Error(`Migration version ${version} not found`);
  }

  // Run migrations up to the target version
  // This would require custom implementation as Knex doesn't support this directly
}

/**
 * Create test data factory functions
 */
export const testDataFactory = {
  /**
   * Create a test organization
   */
  async createOrganization(db: Knex, data: Partial<any> = {}): Promise<any> {
    const [org] = await db('organizations')
      .insert({
        name: data.name || 'Test Organization',
        slug: data.slug || `test-org-${Date.now()}`,
        email: data.email || 'test@organization.com',
        status: data.status || 'active',
        subscription_tier: data.subscription_tier || 'free',
        settings: data.settings || {},
        metadata: data.metadata || {},
        ...data,
      })
      .returning('*');
    return org;
  },

  /**
   * Create a test user
   */
  async createUser(db: Knex, data: Partial<any> = {}): Promise<any> {
    const [user] = await db('users')
      .insert({
        email: data.email || `test-${Date.now()}@example.com`,
        password_hash: data.password_hash || '$2b$10$test.hash',
        password_salt: data.password_salt || 'test-salt',
        first_name: data.first_name || 'Test',
        last_name: data.last_name || 'User',
        status: data.status || 'active',
        role: data.role || 'user',
        email_verified: data.email_verified !== undefined ? data.email_verified : true,
        settings: data.settings || {},
        preferences: data.preferences || {},
        ...data,
      })
      .returning('*');
    return user;
  },

  /**
   * Create a user-organization relationship
   */
  async createUserOrganization(
    db: Knex,
    userId: string,
    organizationId: string,
    data: Partial<any> = {}
  ): Promise<any> {
    const [userOrg] = await db('user_organizations')
      .insert({
        user_id: userId,
        organization_id: organizationId,
        role: data.role || 'member',
        status: data.status || 'active',
        permissions: data.permissions || {},
        metadata: data.metadata || {},
        joined_at: data.joined_at || new Date(),
        ...data,
      })
      .returning('*');
    return userOrg;
  },

  /**
   * Create an event
   */
  async createEvent(db: Knex, data: Partial<any> = {}): Promise<any> {
    const [event] = await db('events')
      .insert({
        event_type: data.event_type || 'TEST.EVENT',
        aggregate_type: data.aggregate_type || 'test',
        aggregate_id: data.aggregate_id || '00000000-0000-0000-0000-000000000000',
        aggregate_version: data.aggregate_version || '1.0.0',
        data: data.data || {},
        metadata: data.metadata || {},
        tags: data.tags || {},
        source: data.source || 'test',
        event_timestamp: data.event_timestamp || new Date(),
        processed: data.processed !== undefined ? data.processed : false,
        ...data,
      })
      .returning('*');
    return event;
  },

  /**
   * Create an API key
   */
  async createApiKey(db: Knex, userId: string, data: Partial<any> = {}): Promise<any> {
    const [apiKey] = await db('api_keys')
      .insert({
        key_id: data.key_id || `key_${Date.now()}`,
        key_hash: data.key_hash || '$2b$10$test.key.hash',
        key_prefix: data.key_prefix || 'test_',
        user_id: userId,
        organization_id: data.organization_id,
        name: data.name || 'Test API Key',
        key_type: data.key_type || 'personal',
        status: data.status || 'active',
        permissions: data.permissions || {},
        scopes: data.scopes || [],
        ...data,
      })
      .returning('*');
    return apiKey;
  },
};

/**
 * Test transaction helper
 */
export async function withTransaction<T>(
  db: Knex,
  callback: (trx: Knex.Transaction) => Promise<T>
): Promise<T> {
  return await db.transaction(callback);
}

/**
 * Wait for a condition to be true
 */
export async function waitFor(
  condition: () => Promise<boolean>,
  timeout: number = 5000,
  interval: number = 100
): Promise<void> {
  const startTime = Date.now();

  while (Date.now() - startTime < timeout) {
    if (await condition()) {
      return;
    }
    await new Promise(resolve => setTimeout(resolve, interval));
  }

  throw new Error('Timeout waiting for condition');
}

/**
 * Assert database state
 */
export const dbAssertions = {
  /**
   * Assert a record exists
   */
  async assertExists(
    db: Knex,
    table: string,
    where: Record<string, any>
  ): Promise<void> {
    const record = await db(table).where(where).first();
    if (!record) {
      throw new Error(`Record not found in ${table} with conditions: ${JSON.stringify(where)}`);
    }
  },

  /**
   * Assert a record does not exist
   */
  async assertNotExists(
    db: Knex,
    table: string,
    where: Record<string, any>
  ): Promise<void> {
    const record = await db(table).where(where).first();
    if (record) {
      throw new Error(`Record unexpectedly found in ${table} with conditions: ${JSON.stringify(where)}`);
    }
  },

  /**
   * Assert record count
   */
  async assertCount(
    db: Knex,
    table: string,
    expectedCount: number,
    where?: Record<string, any>
  ): Promise<void> {
    let query = db(table);
    if (where) {
      query = query.where(where);
    }
    const result = await query.count('* as count').first();
    const actualCount = parseInt(result.count, 10);
    if (actualCount !== expectedCount) {
      throw new Error(`Expected ${expectedCount} records in ${table}, found ${actualCount}`);
    }
  },
};