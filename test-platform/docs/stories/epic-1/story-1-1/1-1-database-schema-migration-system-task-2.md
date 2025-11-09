# Implementation Plan: Task 2 - Migration Framework Implementation

**Story**: 1.1 Database Schema Migration System  
**Task**: 2 - Migration Framework Implementation  
**Acceptance Criteria**: #2 - Migration system using modern Node.js migration library (Knex.js or similar)

## Overview

Implement a robust database migration framework using Knex.js to manage schema changes with version tracking and rollback capabilities.

## Implementation Steps

### Subtask 2.1: Set up Knex.js migration framework

**Objective**: Install and configure Knex.js for database migrations

**Steps**:

1. Install Knex.js and required dependencies

   ```bash
   npm install knex pg
   npm install -D @types/knex
   ```

2. Initialize Knex configuration

   ```bash
   npx knex init
   ```

3. Create Knex configuration file (`knexfile.ts`)

   ```typescript
   import type { Knex } from 'knex';
   import dotenv from 'dotenv';

   dotenv.config();

   const config: { [key: string]: Knex.Config } = {
     development: {
       client: 'postgresql',
       connection: {
         host: process.env.DB_HOST || 'localhost',
         port: parseInt(process.env.DB_PORT || '5432'),
         database: process.env.DB_NAME || 'test_platform',
         user: process.env.DB_USER || 'test_platform_user',
         password: process.env.DB_PASSWORD,
       },
       pool: {
         min: 2,
         max: 10,
       },
       migrations: {
         directory: './database/migrations',
         tableName: 'knex_migrations',
         schemaName: 'public',
       },
       seeds: {
         directory: './database/seeds',
       },
     },

     staging: {
       client: 'postgresql',
       connection: {
         host: process.env.DB_HOST,
         port: parseInt(process.env.DB_PORT || '5432'),
         database: process.env.DB_NAME,
         user: process.env.DB_USER,
         password: process.env.DB_PASSWORD,
         ssl: { rejectUnauthorized: false },
       },
       pool: {
         min: 2,
         max: 20,
       },
       migrations: {
         directory: './database/migrations',
         tableName: 'knex_migrations',
         schemaName: 'public',
       },
     },

     production: {
       client: 'postgresql',
       connection: {
         host: process.env.DB_HOST,
         port: parseInt(process.env.DB_PORT || '5432'),
         database: process.env.DB_NAME,
         user: process.env.DB_USER,
         password: process.env.DB_PASSWORD,
         ssl: { rejectUnauthorized: false },
       },
       pool: {
         min: 5,
         max: 30,
       },
       migrations: {
         directory: './database/migrations',
         tableName: 'knex_migrations',
         schemaName: 'public',
       },
       acquireConnectionTimeout: 60000,
       createTimeoutMillis: 30000,
       destroyTimeoutMillis: 5000,
       idleTimeoutMillis: 30000,
       reapIntervalMillis: 1000,
     },
   };

   export default config;
   ```

4. Create database connection module (`src/database/connection.ts`)

   ```typescript
   import knex from 'knex';
   import knexConfig from '../../knexfile';

   const environment = process.env.NODE_ENV || 'development';
   const config = knexConfig[environment];

   if (!config) {
     throw new Error(`No database configuration found for environment: ${environment}`);
   }

   const db = knex(config);

   // Test connection
   export async function testConnection(): Promise<boolean> {
     try {
       await db.raw('SELECT 1');
       return true;
     } catch (error) {
       console.error('Database connection failed:', error);
       return false;
     }
   }

   export default db;
   ```

**Validation**:

- Knex.js is installed and configured
- Database connection works
- Configuration loads correctly for different environments

### Subtask 2.2: Create migration runner with version tracking

**Objective**: Implement migration runner with proper version tracking and logging

**Steps**:

1. Create migration runner utility (`src/database/migration-runner.ts`)

   ```typescript
   import knex from 'knex';
   import knexConfig from '../../knexfile';
   import { logger } from '../observability/logger';

   export interface MigrationResult {
     success: boolean;
     executed: string[];
     failed?: string[];
     error?: Error;
   }

   export class MigrationRunner {
     private db: knex.Knex;

     constructor(environment?: string) {
       const env = environment || process.env.NODE_ENV || 'development';
       const config = knexConfig[env];
       if (!config) {
         throw new Error(`No database configuration for environment: ${env}`);
       }
       this.db = knex(config);
     }

     async migrateToLatest(): Promise<MigrationResult> {
       try {
         logger.info('Starting database migration to latest version');

         const [batchNo, migrations] = await this.db.migrate.latest();

         if (migrations.length === 0) {
           logger.info('No pending migrations');
           return { success: true, executed: [] };
         }

         logger.info(`Executed ${migrations.length} migrations in batch ${batchNo}`, {
           migrations,
           batchNo,
         });

         return { success: true, executed: migrations };
       } catch (error) {
         logger.error('Migration failed', { error });
         return {
           success: false,
           executed: [],
           failed: [],
           error: error as Error,
         };
       }
     }

     async getCurrentVersion(): Promise<string | null> {
       try {
         const [migration] = await this.db('knex_migrations').orderBy('id', 'desc').limit(1);

         return migration?.name || null;
       } catch (error) {
         logger.error('Failed to get current migration version', { error });
         return null;
       }
     }

     async getPendingMigrations(): Promise<string[]> {
       try {
         const completed = await this.db('knex_migrations').select('name');
         const completedNames = new Set(completed.map((m) => m.name));

         const allMigrations = await this.db.migrate.list();
         return allMigrations[0].filter((m) => !completedNames.has(m));
       } catch (error) {
         logger.error('Failed to get pending migrations', { error });
         return [];
       }
     }

     async close(): Promise<void> {
       await this.db.destroy();
     }
   }
   ```

2. Create CLI commands for migration management (`src/cli/migration-commands.ts`)

   ```typescript
   import { MigrationRunner } from '../database/migration-runner';
   import { logger } from '../observability/logger';

   export async function migrateCommand(target?: string): Promise<void> {
     const runner = new MigrationRunner();

     try {
       if (target === 'latest') {
         const result = await runner.migrateToLatest();
         if (result.success) {
           console.log(`✅ Migration completed. ${result.executed.length} migrations executed.`);
           process.exit(0);
         } else {
           console.error(`❌ Migration failed: ${result.error?.message}`);
           process.exit(1);
         }
       } else {
         const version = await runner.getCurrentVersion();
         console.log(`Current migration version: ${version || 'No migrations'}`);
         const pending = await runner.getPendingMigrations();
         console.log(`Pending migrations: ${pending.length}`);
         pending.forEach((m) => console.log(`  - ${m}`));
       }
     } finally {
       await runner.close();
     }
   }
   ```

**Validation**:

- Migration runner can execute migrations
- Version tracking works correctly
- Pending migrations are identified properly

### Subtask 2.3: Implement rollback functionality

**Objective**: Add ability to rollback migrations to previous versions

**Steps**:

1. Extend migration runner with rollback capabilities

   ```typescript
   // Add to MigrationRunner class
   async rollback(batchNumber?: number): Promise<MigrationResult> {
     try {
       logger.info('Starting database rollback', { batchNumber });

       const [rolledBack, migrations] = await this.db.migrate.rollback({
         directory: knexConfig[process.env.NODE_ENV || 'development']?.migrations?.directory,
       }, batchNumber);

       if (migrations.length === 0) {
         logger.info('No migrations to rollback');
         return { success: true, executed: [] };
       }

       logger.info(`Rolled back ${migrations.length} migrations`, {
         migrations,
         rolledBack,
       });

       return { success: true, executed: migrations };
     } catch (error) {
       logger.error('Rollback failed', { error });
       return {
         success: false,
         executed: [],
         failed: [],
         error: error as Error,
       };
     }
   }

   async rollbackToVersion(targetVersion: string): Promise<MigrationResult> {
     try {
       const currentVersion = await this.getCurrentVersion();
       if (!currentVersion) {
         return { success: true, executed: [] };
       }

       // Get all migration batches
       const batches = await this.db('knex_migrations')
         .select('batch')
         .distinct()
         .orderBy('batch', 'desc');

       // Rollback batch by batch until target version
       const executed: string[] = [];
       for (const batch of batches) {
         const currentBatchVersion = await this.getBatchVersion(batch.batch);
         if (currentBatchVersion === targetVersion) {
           break;
         }

         const result = await this.rollback(batch.batch);
         if (!result.success) {
           return result;
         }
         executed.push(...result.executed);
       }

       return { success: true, executed };
     } catch (error) {
       logger.error('Rollback to version failed', { error, targetVersion });
       return {
         success: false,
         executed: [],
         error: error as Error,
       };
     }
   }

   private async getBatchVersion(batchNumber: number): Promise<string | null> {
     const [migration] = await this.db('knex_migrations')
       .where('batch', batchNumber)
       .orderBy('id', 'desc')
       .limit(1);

     return migration?.name || null;
   }
   ```

2. Add rollback CLI commands

   ```typescript
   export async function rollbackCommand(batch?: string): Promise<void> {
     const runner = new MigrationRunner();

     try {
       const batchNumber = batch ? parseInt(batch) : undefined;
       const result = await runner.rollback(batchNumber);

       if (result.success) {
         console.log(`✅ Rollback completed. ${result.executed.length} migrations rolled back.`);
         process.exit(0);
       } else {
         console.error(`❌ Rollback failed: ${result.error?.message}`);
         process.exit(1);
       }
     } finally {
       await runner.close();
     }
   }
   ```

3. Create migration validation utilities

   ```typescript
   export async function validateMigrations(): Promise<boolean> {
     const runner = new MigrationRunner();

     try {
       // Check if all migrations can be rolled back
       const currentVersion = await runner.getCurrentVersion();
       if (!currentVersion) {
         return true; // No migrations to validate
       }

       // Test rollback to first migration
       const rollbackResult = await runner.rollbackToVersion('initial');
       if (!rollbackResult.success) {
         logger.error('Migration validation failed: Cannot rollback', rollbackResult.error);
         return false;
       }

       // Migrate back to latest
       const migrateResult = await runner.migrateToLatest();
       if (!migrateResult.success) {
         logger.error('Migration validation failed: Cannot re-migrate', migrateResult.error);
         return false;
       }

       logger.info('Migration validation successful');
       return true;
     } catch (error) {
       logger.error('Migration validation error', { error });
       return false;
     } finally {
       await runner.close();
     }
   }
   ```

**Validation**:

- Rollback functionality works correctly
- Can rollback to specific batch
- Can rollback to specific version
- Migration validation passes

## Files to Create

1. `knexfile.ts` - Knex configuration
2. `src/database/connection.ts` - Database connection module
3. `src/database/migration-runner.ts` - Migration runner with version tracking
4. `src/cli/migration-commands.ts` - CLI commands for migration management
5. `database/migrations/.gitkeep` - Directory for migration files

## Dependencies

- knex
- pg
- dotenv
- TypeScript types

## Testing

1. Unit tests for migration runner
2. Integration tests with test database
3. Rollback validation tests
4. CLI command tests

## Notes

- Always test rollback functionality in development
- Use descriptive migration names with timestamps
- Keep migrations reversible when possible
- Document breaking changes in migration descriptions
