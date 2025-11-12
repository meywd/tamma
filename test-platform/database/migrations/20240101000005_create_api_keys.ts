import { Knex } from 'knex';

export async function up(knex: Knex): Promise<void> {
  return knex.schema.createTable('api_keys', (table) => {
    // Primary key
    table.uuid('id').primary().defaultTo(knex.raw('gen_random_uuid()'));

    // Key information
    table.string('key_id', 100).unique().notNullable(); // Public identifier
    table.string('key_hash', 255).unique().notNullable(); // Hashed key
    table.string('key_prefix', 20).notNullable(); // First few characters for identification

    // Ownership
    table.uuid('user_id').notNullable().references('id').inTable('users').onDelete('CASCADE');
    table
      .uuid('organization_id')
      .nullable()
      .references('id')
      .inTable('organizations')
      .onDelete('CASCADE');

    // Key details
    table.string('name', 255).notNullable();
    table.text('description').nullable();
    table.enum('key_type', ['personal', 'service', 'integration']).defaultTo('personal');

    // Permissions and scopes
    table.jsonb('permissions').defaultTo('{}');
    table.jsonb('scopes').defaultTo('[]'); // Array of allowed scopes
    table.string('allowed_ips', 1000).nullable(); // Comma-separated IP addresses
    table.string('allowed_domains', 1000).nullable(); // Comma-separated domains

    // Status and lifecycle
    table.enum('status', ['active', 'inactive', 'revoked', 'expired']).defaultTo('active');
    table.timestamp('expires_at').nullable();
    table.timestamp('last_used_at').nullable();
    table.string('last_used_ip', 45).nullable();
    table.string('last_used_user_agent', 500).nullable();

    // Usage tracking
    table.integer('usage_count').defaultTo(0);
    table.timestamp('usage_reset_at').nullable();
    table.integer('usage_limit').nullable(); // Monthly usage limit

    // Security
    table.string('created_from_ip', 45).nullable();
    table.string('created_from_user_agent', 500).nullable();
    table.boolean('require_mfa').defaultTo(false);

    // Audit fields
    table.timestamp('created_at').defaultTo(knex.fn.now());
    table.timestamp('updated_at').defaultTo(knex.fn.now());
    table.uuid('created_by').nullable().references('id').inTable('users');
    table.uuid('updated_by').nullable().references('id').inTable('users');

    // Indexes
    table.index(['key_id']);
    table.index(['key_hash']);
    table.index(['user_id']);
    table.index(['organization_id']);
    table.index(['status']);
    table.index(['key_type']);
    table.index(['expires_at']);
    table.index(['last_used_at']);
    table.index(['created_at']);
  });
}

export async function down(knex: Knex): Promise<void> {
  return knex.schema.dropTableIfExists('api_keys');
}