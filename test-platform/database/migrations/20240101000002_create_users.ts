import { Knex } from 'knex';

export async function up(knex: Knex): Promise<void> {
  return knex.schema.createTable('users', (table) => {
    // Primary key
    table.uuid('id').primary().defaultTo(knex.raw('gen_random_uuid()'));

    // Authentication fields
    table.string('email', 255).unique().notNullable();
    table.string('password_hash', 255).notNullable(); // bcrypt hash
    table.string('password_salt', 255).notNullable();
    table.string('password_reset_token', 255).nullable().unique();
    table.timestamp('password_reset_expires_at').nullable();
    table.string('email_verification_token', 255).nullable().unique();
    table.timestamp('email_verified_at').nullable();

    // Profile information
    table.string('first_name', 100).nullable();
    table.string('last_name', 100).nullable();
    table.string('username', 100).unique().nullable();
    table.text('bio').nullable();
    table.string('avatar_url', 500).nullable();

    // Preferences and settings
    table.jsonb('preferences').defaultTo('{}');
    table.jsonb('settings').defaultTo('{}');
    table.string('timezone', 50).defaultTo('UTC');
    table.string('language', 10).defaultTo('en');

    // Status and roles
    table.enum('status', ['active', 'inactive', 'suspended', 'deleted']).defaultTo('active');
    table.enum('role', ['user', 'admin', 'super_admin']).defaultTo('user');
    table.boolean('email_verified').defaultTo(false);
    table.boolean('mfa_enabled').defaultTo(false);
    table.string('mfa_secret', 255).nullable();

    // Security
    table.string('last_login_ip', 45).nullable(); // IPv6 compatible
    table.string('current_login_ip', 45).nullable();
    table.timestamp('last_login_at').nullable();
    table.timestamp('current_login_at').nullable();
    table.integer('failed_login_attempts').defaultTo(0);
    table.timestamp('locked_until').nullable();

    // Audit fields - Self-referencing foreign keys
    table.timestamp('created_at').defaultTo(knex.fn.now());
    table.timestamp('updated_at').defaultTo(knex.fn.now());
    table.uuid('created_by').nullable();
    table.uuid('updated_by').nullable();

    // Indexes
    table.index(['email']);
    table.index(['username']);
    table.index(['status']);
    table.index(['role']);
    table.index(['created_at']);
    table.index(['last_login_at']);
  });
}

export async function down(knex: Knex): Promise<void> {
  return knex.schema.dropTableIfExists('users');
}