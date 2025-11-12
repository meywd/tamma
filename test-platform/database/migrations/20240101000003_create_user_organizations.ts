import { Knex } from 'knex';

export async function up(knex: Knex): Promise<void> {
  return knex.schema.createTable('user_organizations', (table) => {
    // Primary key
    table.uuid('id').primary().defaultTo(knex.raw('gen_random_uuid()'));

    // Foreign keys
    table.uuid('user_id').notNullable().references('id').inTable('users').onDelete('CASCADE');
    table
      .uuid('organization_id')
      .notNullable()
      .references('id')
      .inTable('organizations')
      .onDelete('CASCADE');

    // Role and permissions within organization
    table.enum('role', ['member', 'admin', 'owner']).defaultTo('member');
    table.jsonb('permissions').defaultTo('{}');

    // Status and invitation
    table.enum('status', ['active', 'pending', 'invited', 'left']).defaultTo('pending');
    table.string('invitation_token', 255).nullable().unique();
    table.timestamp('invitation_expires_at').nullable();
    table.uuid('invited_by').nullable().references('id').inTable('users');

    // Membership details
    table.string('job_title', 255).nullable();
    table.string('department', 255).nullable();
    table.jsonb('metadata').defaultTo('{}');

    // Audit fields
    table.timestamp('joined_at').nullable();
    table.timestamp('left_at').nullable();
    table.timestamp('created_at').defaultTo(knex.fn.now());
    table.timestamp('updated_at').defaultTo(knex.fn.now());

    // Indexes
    table.index(['user_id']);
    table.index(['organization_id']);
    table.index(['status']);
    table.index(['role']);
    table.unique(['user_id', 'organization_id']); // Prevent duplicate memberships
  });
}

export async function down(knex: Knex): Promise<void> {
  return knex.schema.dropTableIfExists('user_organizations');
}