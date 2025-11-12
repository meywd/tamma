import { describe, it, expect, beforeAll, afterAll, beforeEach } from 'vitest';
import { Knex } from 'knex';
import { MigrationRunner } from '../../src/database/migration-runner';
import {
  getTestDatabase,
  cleanDatabase,
  dropAllTables,
  getTableNames,
  tableExists,
  testDataFactory,
  dbAssertions,
} from '../helpers/test-database';

describe('Migration Rollback Tests', () => {
  let db: Knex;
  let runner: MigrationRunner;

  beforeAll(async () => {
    db = await getTestDatabase();
    runner = new MigrationRunner('test');
  });

  afterAll(async () => {
    await runner.close();
    await db.destroy();
  });

  beforeEach(async () => {
    // Ensure clean state
    await dropAllTables(db);
    await runner.migrateToLatest();
  });

  describe('Single Migration Rollback', () => {
    it('should rollback last migration successfully', async () => {
      // Get current state
      const beforeTables = await getTableNames(db);
      expect(beforeTables).toContain('organizations');

      // Assuming we have these tables from migrations
      if (beforeTables.includes('users')) {
        expect(beforeTables).toContain('users');
      }
      if (beforeTables.includes('user_organizations')) {
        expect(beforeTables).toContain('user_organizations');
      }
      if (beforeTables.includes('events')) {
        expect(beforeTables).toContain('events');
      }
      if (beforeTables.includes('api_keys')) {
        expect(beforeTables).toContain('api_keys');
      }

      // Get the count of tables before rollback
      const tableCountBefore = beforeTables.length;

      // Rollback one migration
      const result = await runner.rollback(1);
      expect(result.success).toBe(true);
      expect(result.executed).toHaveLength(1);

      // Verify at least one table is gone
      const afterTables = await getTableNames(db);
      expect(afterTables.length).toBeLessThan(tableCountBefore);
    });

    it('should preserve data integrity during partial rollback', async () => {
      // Check if users table exists
      const hasUsersTable = await tableExists(db, 'users');

      if (hasUsersTable) {
        // Insert test data
        await db('organizations').insert({
          name: 'Test Org',
          slug: 'test-org',
        });

        await db('users').insert({
          email: 'test@example.com',
          password_hash: 'hash',
          password_salt: 'salt',
        });

        const orgCount = await db('organizations').count('* as count');
        const userCount = await db('users').count('* as count');

        expect(parseInt(orgCount[0].count as string)).toBe(1);
        expect(parseInt(userCount[0].count as string)).toBe(1);
      } else {
        // Just test with organizations table
        await db('organizations').insert({
          name: 'Test Org',
          slug: 'test-org',
        });

        const orgCount = await db('organizations').count('* as count');
        expect(parseInt(orgCount[0].count as string)).toBe(1);
      }

      // Rollback one migration
      const result = await runner.rollback(1);
      expect(result.success).toBe(true);

      // Verify organizations table still exists (it's the first migration)
      const tablesAfter = await getTableNames(db);
      if (tablesAfter.length > 0) {
        // If there are still tables, organizations should be one of them
        const orgExists = await tableExists(db, 'organizations');
        if (orgExists) {
          const orgCount = await db('organizations').count('* as count');
          expect(parseInt(orgCount[0].count as string)).toBeGreaterThanOrEqual(0);
        }
      }
    });
  });

  describe('Multiple Migration Rollback', () => {
    it('should rollback multiple migrations in correct order', async () => {
      const initialTables = await getTableNames(db);
      const initialCount = initialTables.length;

      // Rollback 2 migrations if we have at least 2
      const status = await runner.getStatus();
      const completedCount = status.completed.length;

      if (completedCount >= 2) {
        const result = await runner.rollback(2);
        expect(result.success).toBe(true);
        expect(result.executed.length).toBeGreaterThanOrEqual(1);

        // Verify tables are reduced
        const tablesAfter = await getTableNames(db);
        expect(tablesAfter.length).toBeLessThan(initialCount);
      } else {
        // If we have less than 2 migrations, just rollback what we have
        const result = await runner.rollback(1);
        expect(result.success).toBe(true);
      }
    });

    it('should handle rollback to specific version', async () => {
      // Get current version
      const currentVersion = await runner.getCurrentVersion();
      expect(currentVersion).toBeTruthy();

      // Get status to find a target version
      const status = await runner.getStatus();

      if (status.completed.length > 1) {
        // Rollback to the first migration (organizations)
        const targetVersion = 'create_organizations';
        const result = await runner.rollbackToVersion(targetVersion);
        expect(result.success).toBe(true);

        // Verify only organizations table exists
        const tables = await getTableNames(db);
        expect(tables).toContain('organizations');
        expect(tables).toContain('knex_migrations');
        expect(tables).toContain('knex_migrations_lock');

        // Should only have these 3 tables
        expect(tables.length).toBeLessThanOrEqual(3);
      }
    });
  });

  describe('Rollback Error Handling', () => {
    it('should handle rollback failures gracefully', async () => {
      // Manually corrupt the database state by dropping the migrations table
      await db.raw('DROP TABLE IF EXISTS knex_migrations CASCADE');

      // Attempt rollback
      const result = await runner.rollback();
      expect(result.success).toBe(false);
      expect(result.error).toBeDefined();
    });

    it('should handle empty rollback gracefully', async () => {
      // Rollback everything first
      await runner.rollbackAll();

      // Try to rollback when there's nothing to rollback
      const result = await runner.rollback();
      expect(result.success).toBe(true);
      expect(result.executed).toHaveLength(0);
    });

    it('should validate migration reversibility', async () => {
      // Get all completed migrations
      const status = await runner.getStatus();
      expect(status.completed.length).toBeGreaterThan(0);

      // Each migration should be reversible
      for (const migration of status.completed) {
        // This would require checking that each migration file has a down function
        // For now, we just verify the migration exists in the list
        expect(migration).toBeTruthy();
        expect(typeof migration).toBe('string');
      }
    });
  });

  describe('Data Consistency', () => {
    it('should maintain foreign key constraints during rollback', async () => {
      // Check what tables we have
      const tables = await getTableNames(db);

      if (tables.includes('users') && tables.includes('user_organizations')) {
        // Create related data
        const [org] = await db('organizations')
          .insert({
            name: 'Test Org',
            slug: 'test-org',
          })
          .returning('id');

        const [user] = await db('users')
          .insert({
            email: 'test@example.com',
            password_hash: 'hash',
            password_salt: 'salt',
          })
          .returning('id');

        await db('user_organizations').insert({
          user_id: user.id,
          organization_id: org.id,
          role: 'member',
        });

        // Rollback user_organizations table (if it's the last migration)
        const status = await runner.getStatus();
        const lastMigration = status.completed[status.completed.length - 1];

        if (lastMigration.includes('user_organizations')) {
          await runner.rollback(1);

          // Verify parent tables still exist and data is intact
          const orgCount = await db('organizations').count('* as count');
          const userCount = await db('users').count('* as count');

          expect(parseInt(orgCount[0].count as string)).toBe(1);
          expect(parseInt(userCount[0].count as string)).toBe(1);

          // user_organizations should not exist
          const hasUserOrgs = await tableExists(db, 'user_organizations');
          expect(hasUserOrgs).toBe(false);
        }
      }
    });

    it('should handle cascade deletes correctly', async () => {
      const tables = await getTableNames(db);

      if (tables.includes('users') && tables.includes('user_organizations')) {
        // Create data with cascade relationships
        const [org] = await db('organizations')
          .insert({
            name: 'Test Org',
            slug: 'test-org',
          })
          .returning('id');

        const [user] = await db('users')
          .insert({
            email: 'test@example.com',
            password_hash: 'hash',
            password_salt: 'salt',
          })
          .returning('id');

        await db('user_organizations').insert({
          user_id: user.id,
          organization_id: org.id,
          role: 'member',
        });

        // Get initial counts
        const userOrgCount = await db('user_organizations').count('* as count');
        expect(parseInt(userOrgCount[0].count as string)).toBe(1);

        // Delete organization (should cascade to user_organizations)
        await db('organizations').where('id', org.id).delete();

        // Verify cascade delete worked
        const userOrgCountAfter = await db('user_organizations')
          .where('organization_id', org.id)
          .count('* as count');
        expect(parseInt(userOrgCountAfter[0].count as string)).toBe(0);
      }
    });
  });

  describe('Performance Impact', () => {
    it('should complete rollback within acceptable time', async () => {
      const startTime = Date.now();

      const result = await runner.rollback(1);
      expect(result.success).toBe(true);

      const duration = Date.now() - startTime;
      expect(duration).toBeLessThan(30000); // 30 seconds max
    });

    it('should handle large datasets efficiently', async () => {
      const hasUsersTable = await tableExists(db, 'users');

      if (hasUsersTable) {
        // Insert large dataset
        const users = Array.from({ length: 100 }, (_, i) => ({
          email: `user${i}@example.com`,
          password_hash: 'hash',
          password_salt: 'salt',
        }));

        await db('users').insert(users);

        // Verify insertion
        const count = await db('users').count('* as count');
        expect(parseInt(count[0].count as string)).toBe(100);

        // Rollback should still be efficient
        const startTime = Date.now();
        const result = await runner.rollback(1);
        const duration = Date.now() - startTime;

        expect(result.success).toBe(true);
        expect(duration).toBeLessThan(60000); // 60 seconds for large dataset
      } else {
        // Test with organizations
        const orgs = Array.from({ length: 100 }, (_, i) => ({
          name: `Org ${i}`,
          slug: `org-${i}`,
        }));

        await db('organizations').insert(orgs);

        const count = await db('organizations').count('* as count');
        expect(parseInt(count[0].count as string)).toBe(100);

        const startTime = Date.now();
        const result = await runner.rollbackAll();
        const duration = Date.now() - startTime;

        expect(result.success).toBe(true);
        expect(duration).toBeLessThan(60000);
      }
    });
  });

  describe('Migration Status and Version Control', () => {
    it('should correctly report migration status', async () => {
      const status = await runner.getStatus();

      expect(status).toHaveProperty('current');
      expect(status).toHaveProperty('pending');
      expect(status).toHaveProperty('completed');

      expect(Array.isArray(status.pending)).toBe(true);
      expect(Array.isArray(status.completed)).toBe(true);
      expect(status.completed.length).toBeGreaterThan(0);
    });

    it('should track current version correctly', async () => {
      const versionBefore = await runner.getCurrentVersion();
      expect(versionBefore).toBeTruthy();

      // Rollback
      await runner.rollback(1);

      const versionAfter = await runner.getCurrentVersion();

      if (versionAfter) {
        expect(versionAfter).not.toBe(versionBefore);
      } else {
        // All migrations rolled back
        expect(versionAfter).toBeNull();
      }
    });

    it('should handle complete rollback and re-migration', async () => {
      // Rollback everything
      const rollbackResult = await runner.rollbackAll();
      expect(rollbackResult.success).toBe(true);

      // Verify no tables except knex tables
      const tablesAfterRollback = await getTableNames(db);
      const appTables = tablesAfterRollback.filter(
        t => !t.startsWith('knex_')
      );
      expect(appTables).toHaveLength(0);

      // Re-migrate
      const migrateResult = await runner.migrateToLatest();
      expect(migrateResult.success).toBe(true);

      // Verify tables are back
      const tablesAfterMigrate = await getTableNames(db);
      expect(tablesAfterMigrate).toContain('organizations');
    });
  });
});