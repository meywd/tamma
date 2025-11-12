import { Knex } from 'knex';

/**
 * Migration: {MIGRATION_NAME}
 *
 * Description: Add a description of what this migration does
 *
 * Breaking Changes: None (or describe if any)
 *
 * Rollback: This migration can be safely rolled back
 */

export async function up(knex: Knex): Promise<void> {
  // TODO: Implement forward migration logic
  // Example: Create table
  /*
  await knex.schema.createTable('table_name', (table) => {
    table.uuid('id').primary().defaultTo(knex.raw('gen_random_uuid()'));
    table.string('name', 255).notNullable();
    table.text('description');
    table.boolean('is_active').defaultTo(true);
    table.timestamps(true, true); // created_at and updated_at with defaults

    // Indexes
    table.index(['name'], 'idx_table_name_name');
    table.index(['created_at'], 'idx_table_name_created_at');
  });
  */

  // Example: Alter table
  /*
  await knex.schema.alterTable('existing_table', (table) => {
    table.string('new_column', 100);
    table.dropColumn('old_column');
    table.renameColumn('old_name', 'new_name');
  });
  */

  // Example: Create index
  /*
  await knex.schema.alterTable('table_name', (table) => {
    table.index(['column1', 'column2'], 'idx_composite_name');
  });
  */

  // Example: Add foreign key
  /*
  await knex.schema.alterTable('child_table', (table) => {
    table.uuid('parent_id').references('id').inTable('parent_table')
      .onDelete('CASCADE')
      .onUpdate('CASCADE');
  });
  */

  // Example: Raw SQL
  /*
  await knex.raw(`
    CREATE OR REPLACE VIEW view_name AS
    SELECT * FROM table_name WHERE is_active = true;
  `);
  */
}

export async function down(knex: Knex): Promise<void> {
  // TODO: Implement rollback logic
  // IMPORTANT: Ensure this completely reverses the up() migration

  // Example: Drop table
  /*
  await knex.schema.dropTableIfExists('table_name');
  */

  // Example: Reverse alter table
  /*
  await knex.schema.alterTable('existing_table', (table) => {
    table.dropColumn('new_column');
    table.string('old_column', 100);
    table.renameColumn('new_name', 'old_name');
  });
  */

  // Example: Drop index
  /*
  await knex.schema.alterTable('table_name', (table) => {
    table.dropIndex(['column1', 'column2'], 'idx_composite_name');
  });
  */

  // Example: Drop foreign key
  /*
  await knex.schema.alterTable('child_table', (table) => {
    table.dropForeign(['parent_id']);
    table.dropColumn('parent_id');
  });
  */

  // Example: Drop view
  /*
  await knex.raw('DROP VIEW IF EXISTS view_name');
  */
}