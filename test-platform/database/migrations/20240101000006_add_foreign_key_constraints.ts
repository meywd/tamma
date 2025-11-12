import { Knex } from 'knex';

export async function up(knex: Knex): Promise<void> {
  // Add foreign key constraints to organizations table for audit fields
  return knex.schema.alterTable('organizations', (table) => {
    table.foreign('created_by').references('id').inTable('users').onDelete('SET NULL');
    table.foreign('updated_by').references('id').inTable('users').onDelete('SET NULL');
  });
}

export async function down(knex: Knex): Promise<void> {
  return knex.schema.alterTable('organizations', (table) => {
    table.dropForeign(['created_by']);
    table.dropForeign(['updated_by']);
  });
}