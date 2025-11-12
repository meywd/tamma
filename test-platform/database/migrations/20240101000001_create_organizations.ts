import { Knex } from 'knex';

export async function up(knex: Knex): Promise<void> {
  // Note: The users table needs to be created first for the foreign keys
  // This will be handled by migration ordering

  return knex.schema.createTable('organizations', (table) => {
    // Primary key
    table.uuid('id').primary().defaultTo(knex.raw('gen_random_uuid()'));

    // Organization details
    table.string('name', 255).notNullable();
    table.string('slug', 100).unique().notNullable(); // URL-friendly identifier
    table.text('description').nullable();
    table.string('domain', 255).nullable(); // For SSO integration

    // Contact information
    table.string('email', 255).nullable();
    table.string('phone', 50).nullable();
    table.string('website', 500).nullable();

    // Address
    table.string('address_line1', 255).nullable();
    table.string('address_line2', 255).nullable();
    table.string('city', 100).nullable();
    table.string('state', 100).nullable();
    table.string('country', 100).nullable();
    table.string('postal_code', 20).nullable();

    // Configuration (JSONB for flexibility)
    table.jsonb('settings').defaultTo('{}');
    table.jsonb('metadata').defaultTo('{}');

    // Status and lifecycle
    table.enum('status', ['active', 'inactive', 'suspended', 'deleted']).defaultTo('active');
    table.string('subscription_tier', 50).defaultTo('free');
    table.timestamp('subscription_expires_at').nullable();

    // Audit fields - Foreign keys will be added after users table is created
    table.timestamp('created_at').defaultTo(knex.fn.now());
    table.timestamp('updated_at').defaultTo(knex.fn.now());
    table.uuid('created_by').nullable();
    table.uuid('updated_by').nullable();

    // Indexes
    table.index(['slug']);
    table.index(['status']);
    table.index(['subscription_tier']);
    table.index(['created_at']);
  });
}

export async function down(knex: Knex): Promise<void> {
  return knex.schema.dropTableIfExists('organizations');
}