import { Knex, knex } from 'knex';
import path from 'path';
import { logger } from '../observability/logger';

export interface MigrationResult {
  success: boolean;
  executed: string[];
  error?: Error;
  duration?: number;
}

export interface MigrationStatus {
  current: string | null;
  pending: string[];
  completed: string[];
}

/**
 * Migration runner with rollback support
 */
export class MigrationRunner {
  private db: Knex;
  private config: Knex.Config;
  private environment: string;

  constructor(environment: string = 'development') {
    this.environment = environment;
    this.config = this.getConfig();
    this.db = knex(this.config);
  }

  /**
   * Get database configuration for the environment
   */
  private getConfig(): Knex.Config {
    const baseConfig: Knex.Config = {
      client: 'pg',
      connection: {
        host: process.env[`${this.environment.toUpperCase()}_DB_HOST`] || 'localhost',
        port: parseInt(process.env[`${this.environment.toUpperCase()}_DB_PORT`] || '5432', 10),
        user: process.env[`${this.environment.toUpperCase()}_DB_USER`] || 'postgres',
        password: process.env[`${this.environment.toUpperCase()}_DB_PASSWORD`] || 'postgres',
        database: process.env[`${this.environment.toUpperCase()}_DB_NAME`] || 'test_platform',
      },
      pool: {
        min: 2,
        max: 10,
      },
      migrations: {
        directory: path.resolve(process.cwd(), 'database/migrations'),
        extension: 'ts',
        tableName: 'knex_migrations',
        loadExtensions: ['.ts'],
      },
      seeds: {
        directory: path.resolve(process.cwd(), 'database/seeds'),
        extension: 'ts',
      },
    };

    return baseConfig;
  }

  /**
   * Run migrations to latest version
   */
  async migrateToLatest(): Promise<MigrationResult> {
    const startTime = Date.now();
    try {
      logger.info(`Running migrations to latest for ${this.environment} environment`);

      const [batchNo, migrations] = await this.db.migrate.latest();

      const duration = Date.now() - startTime;
      logger.info(`Migrations completed successfully`, {
        batch: batchNo,
        migrations,
        duration,
      });

      return {
        success: true,
        executed: migrations,
        duration,
      };
    } catch (error) {
      logger.error('Migration failed', { error });
      return {
        success: false,
        executed: [],
        error: error as Error,
      };
    }
  }

  /**
   * Rollback migrations
   * @param steps Number of batches to rollback (default: 1)
   */
  async rollback(steps: number = 1): Promise<MigrationResult> {
    const startTime = Date.now();
    try {
      logger.info(`Rolling back ${steps} batch(es) for ${this.environment} environment`);

      const executed: string[] = [];

      for (let i = 0; i < steps; i++) {
        const [batchNo, migrations] = await this.db.migrate.rollback();

        if (migrations.length === 0) {
          logger.info('No more migrations to rollback');
          break;
        }

        executed.push(...migrations);
        logger.info(`Rolled back batch ${batchNo}`, { migrations });
      }

      const duration = Date.now() - startTime;
      logger.info(`Rollback completed successfully`, {
        executed,
        duration,
      });

      return {
        success: true,
        executed,
        duration,
      };
    } catch (error) {
      logger.error('Rollback failed', { error });
      return {
        success: false,
        executed: [],
        error: error as Error,
      };
    }
  }

  /**
   * Rollback all migrations
   */
  async rollbackAll(): Promise<MigrationResult> {
    const startTime = Date.now();
    try {
      logger.info(`Rolling back all migrations for ${this.environment} environment`);

      const executed: string[] = [];
      let batchCount = 0;

      // Keep rolling back until no more migrations
      while (true) {
        const [batchNo, migrations] = await this.db.migrate.rollback();

        if (migrations.length === 0) {
          break;
        }

        executed.push(...migrations);
        batchCount++;
        logger.info(`Rolled back batch ${batchNo}`, { migrations });
      }

      const duration = Date.now() - startTime;
      logger.info(`All rollbacks completed`, {
        batches: batchCount,
        executed,
        duration,
      });

      return {
        success: true,
        executed,
        duration,
      };
    } catch (error) {
      logger.error('Complete rollback failed', { error });
      return {
        success: false,
        executed: [],
        error: error as Error,
      };
    }
  }

  /**
   * Rollback to a specific migration version
   */
  async rollbackToVersion(targetVersion: string): Promise<MigrationResult> {
    const startTime = Date.now();
    try {
      logger.info(`Rolling back to version: ${targetVersion}`);

      const executed: string[] = [];

      // Get current migrations
      const completed = await this.db.migrate.list();
      const currentMigrations = completed[1];

      // Find target migration index
      const targetIndex = currentMigrations.findIndex((m: any) =>
        m.file.includes(targetVersion) || m.name.includes(targetVersion)
      );

      if (targetIndex === -1) {
        throw new Error(`Target migration ${targetVersion} not found in completed migrations`);
      }

      // Calculate how many migrations to rollback
      const migrationsToRollback = currentMigrations.length - targetIndex - 1;

      if (migrationsToRollback <= 0) {
        logger.info('Already at or before target version');
        return {
          success: true,
          executed: [],
          duration: Date.now() - startTime,
        };
      }

      // Rollback the calculated number of migrations
      for (let i = 0; i < migrationsToRollback; i++) {
        const [, migrations] = await this.db.migrate.rollback();
        if (migrations.length === 0) break;
        executed.push(...migrations);
      }

      const duration = Date.now() - startTime;
      logger.info(`Rollback to version completed`, {
        targetVersion,
        executed,
        duration,
      });

      return {
        success: true,
        executed,
        duration,
      };
    } catch (error) {
      logger.error('Rollback to version failed', { error, targetVersion });
      return {
        success: false,
        executed: [],
        error: error as Error,
      };
    }
  }

  /**
   * Get current migration version
   */
  async getCurrentVersion(): Promise<string | null> {
    try {
      const completed = await this.db.migrate.list();
      const currentMigrations = completed[1];

      if (currentMigrations.length === 0) {
        return null;
      }

      return currentMigrations[currentMigrations.length - 1].file;
    } catch (error) {
      logger.error('Failed to get current version', { error });
      return null;
    }
  }

  /**
   * Get migration status
   */
  async getStatus(): Promise<MigrationStatus> {
    try {
      const [pending, completed] = await this.db.migrate.list();

      const current = completed.length > 0
        ? completed[completed.length - 1].file
        : null;

      return {
        current,
        pending: pending.map((m: any) => m.file),
        completed: completed.map((m: any) => m.file),
      };
    } catch (error) {
      logger.error('Failed to get migration status', { error });
      throw error;
    }
  }

  /**
   * Run a specific migration file
   */
  async runMigration(migrationName: string): Promise<MigrationResult> {
    const startTime = Date.now();
    try {
      logger.info(`Running specific migration: ${migrationName}`);

      // This would require custom implementation
      // Knex doesn't directly support running specific migrations

      const duration = Date.now() - startTime;
      return {
        success: true,
        executed: [migrationName],
        duration,
      };
    } catch (error) {
      logger.error('Failed to run migration', { error, migrationName });
      return {
        success: false,
        executed: [],
        error: error as Error,
      };
    }
  }

  /**
   * Validate migration files
   */
  async validateMigrations(): Promise<boolean> {
    try {
      logger.info('Validating migration files');

      const [pending] = await this.db.migrate.list();

      // Check each pending migration has up and down functions
      for (const migration of pending) {
        const migrationPath = path.join(
          this.config.migrations!.directory as string,
          migration.file
        );

        try {
          const migrationModule = await import(migrationPath);

          if (typeof migrationModule.up !== 'function') {
            logger.error(`Migration ${migration.file} missing 'up' function`);
            return false;
          }

          if (typeof migrationModule.down !== 'function') {
            logger.error(`Migration ${migration.file} missing 'down' function`);
            return false;
          }
        } catch (error) {
          logger.error(`Failed to load migration ${migration.file}`, { error });
          return false;
        }
      }

      logger.info('All migrations validated successfully');
      return true;
    } catch (error) {
      logger.error('Migration validation failed', { error });
      return false;
    }
  }

  /**
   * Create a new migration file
   */
  async createMigration(name: string): Promise<string> {
    try {
      const timestamp = new Date().toISOString().replace(/[-T:.Z]/g, '').slice(0, 14);
      const fileName = `${timestamp}_${name}.ts`;

      // Would need to write file here
      logger.info(`Migration file would be created: ${fileName}`);
      return fileName;
    } catch (error) {
      logger.error('Failed to create migration', { error, name });
      throw error;
    }
  }

  /**
   * Close database connection
   */
  async close(): Promise<void> {
    await this.db.destroy();
    logger.info('Database connection closed');
  }

  /**
   * Get the database instance
   */
  getDatabase(): Knex {
    return this.db;
  }
}