import { Knex } from 'knex';

/**
 * Initial migration to set up the database schema
 * Creates core system tables and sets up database extensions
 */

export async function up(knex: Knex): Promise<void> {
  // Enable UUID extension for PostgreSQL
  await knex.raw('CREATE EXTENSION IF NOT EXISTS "uuid-ossp"');

  // Create system metadata table for tracking various system information
  await knex.schema.createTable('system_metadata', (table) => {
    table.uuid('id').primary().defaultTo(knex.raw('uuid_generate_v4()'));
    table.string('key', 255).notNullable().unique();
    table.jsonb('value').notNullable();
    table.text('description');
    table.timestamps(true, true);

    // Indexes
    table.index(['key'], 'idx_system_metadata_key');
    table.index(['created_at'], 'idx_system_metadata_created_at');
  });

  // Insert initial system metadata
  await knex('system_metadata').insert([
    {
      key: 'database_version',
      value: JSON.stringify({ version: '1.0.0', migrated_at: new Date().toISOString() }),
      description: 'Current database schema version',
    },
    {
      key: 'system_initialized',
      value: JSON.stringify({ initialized: true, timestamp: new Date().toISOString() }),
      description: 'System initialization status',
    },
  ]);

  // Create audit log table for tracking database changes
  await knex.schema.createTable('audit_log', (table) => {
    table.uuid('id').primary().defaultTo(knex.raw('uuid_generate_v4()'));
    table.string('table_name', 255).notNullable();
    table.string('operation', 50).notNullable(); // INSERT, UPDATE, DELETE
    table.uuid('record_id');
    table.jsonb('old_values');
    table.jsonb('new_values');
    table.string('user_id', 255);
    table.string('session_id', 255);
    table.string('ip_address', 45);
    table.text('user_agent');
    table.timestamp('created_at', { useTz: true }).defaultTo(knex.fn.now());

    // Indexes
    table.index(['table_name', 'operation'], 'idx_audit_log_table_operation');
    table.index(['record_id'], 'idx_audit_log_record_id');
    table.index(['user_id'], 'idx_audit_log_user_id');
    table.index(['created_at'], 'idx_audit_log_created_at');
  });

  // Create health check table for monitoring
  await knex.schema.createTable('health_checks', (table) => {
    table.uuid('id').primary().defaultTo(knex.raw('uuid_generate_v4()'));
    table.string('service_name', 255).notNullable();
    table.string('check_type', 100).notNullable(); // database, api, external_service
    table.string('status', 50).notNullable(); // healthy, degraded, unhealthy
    table.jsonb('details');
    table.integer('response_time_ms');
    table.timestamp('checked_at', { useTz: true }).defaultTo(knex.fn.now());

    // Indexes
    table.index(['service_name', 'check_type'], 'idx_health_checks_service_type');
    table.index(['status'], 'idx_health_checks_status');
    table.index(['checked_at'], 'idx_health_checks_checked_at');
  });

  // Add comments to tables
  await knex.raw('COMMENT ON TABLE system_metadata IS ?', ['Stores system-wide configuration and metadata']);
  await knex.raw('COMMENT ON TABLE audit_log IS ?', ['Tracks all database changes for audit purposes']);
  await knex.raw('COMMENT ON TABLE health_checks IS ?', ['Records health check results for monitoring']);
}

export async function down(knex: Knex): Promise<void> {
  // Drop tables in reverse order to avoid foreign key constraints
  await knex.schema.dropTableIfExists('health_checks');
  await knex.schema.dropTableIfExists('audit_log');
  await knex.schema.dropTableIfExists('system_metadata');

  // Note: We don't drop the uuid-ossp extension as it might be used by other schemas
  // If you need to drop it, uncomment the following line:
  // await knex.raw('DROP EXTENSION IF EXISTS "uuid-ossp"');
}