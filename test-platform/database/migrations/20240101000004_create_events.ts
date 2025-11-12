import { Knex } from 'knex';

export async function up(knex: Knex): Promise<void> {
  return knex.schema.createTable('events', (table) => {
    // Primary key - UUID v7 for time-sortable IDs
    table.uuid('id').primary().defaultTo(knex.raw('gen_random_uuid()'));

    // Event identification
    table.string('event_type', 255).notNullable(); // e.g., "USER.CREATED", "ORG.UPDATED"
    table.string('aggregate_type', 100).notNullable(); // e.g., "user", "organization"
    table.uuid('aggregate_id').notNullable(); // ID of the entity
    table.string('aggregate_version', 50).notNullable(); // Version of the aggregate

    // Event data
    table.jsonb('data').notNullable(); // Event payload
    table.jsonb('metadata').defaultTo('{}'); // Event metadata

    // DCB Pattern - Flexible tagging system
    table.jsonb('tags').defaultTo('{}'); // Flexible tags for querying

    // Context information
    table.uuid('user_id').nullable().references('id').inTable('users');
    table.uuid('organization_id').nullable().references('id').inTable('organizations');
    table.string('session_id', 255).nullable();
    table.string('correlation_id', 255).nullable(); // For tracing related events
    table.string('causation_id', 255).nullable(); // For event causality

    // System information
    table.string('source', 100).defaultTo('system'); // system, api, cli, webhook
    table.string('version', 50).nullable(); // API version or system version
    table.string('ip_address', 45).nullable();
    table.string('user_agent', 500).nullable();

    // Timestamps (DCB requires precise timing)
    table.timestamp('event_timestamp').notNullable().defaultTo(knex.fn.now());
    table.timestamp('created_at').defaultTo(knex.fn.now());

    // Processing information
    table.boolean('processed').defaultTo(false);
    table.timestamp('processed_at').nullable();
    table.integer('retry_count').defaultTo(0);
    table.string('error_message', 1000).nullable();

    // Indexes for performance
    table.index(['event_type']);
    table.index(['aggregate_type', 'aggregate_id']);
    table.index(['aggregate_id', 'aggregate_version']);
    table.index(['event_timestamp']); // Time-series queries
    table.index(['user_id']);
    table.index(['organization_id']);
    table.index(['correlation_id']);
    table.index(['causation_id']);
    table.index(['source']);
    table.index(['processed']);

    // GIN indexes for JSONB tags and data
    table.index('tags', undefined, { indexType: 'GIN' });
    table.index('data', undefined, { indexType: 'GIN' });

    // Composite indexes for common query patterns
    table.index(['aggregate_type', 'event_timestamp']);
    table.index(['organization_id', 'event_timestamp']);
    table.index(['user_id', 'event_timestamp']);
  });
}

export async function down(knex: Knex): Promise<void> {
  return knex.schema.dropTableIfExists('events');
}