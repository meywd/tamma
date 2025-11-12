import { MigrationRunner } from '../database/migration-runner';
import { testConnection } from '../database/connection';
import chalk from 'chalk';

// Helper function to format duration
function formatDuration(ms?: number): string {
  if (!ms) return '';
  const seconds = (ms / 1000).toFixed(2);
  return `(${seconds}s)`;
}

// Helper function to print migration results
function printMigrationResult(result: any, action: string) {
  if (result.success) {
    if (result.executed.length === 0) {
      console.log(chalk.yellow(`�  No migrations to ${action}`));
    } else {
      console.log(chalk.green(` ${action} completed successfully ${formatDuration(result.duration)}`));
      console.log(chalk.cyan(`   Executed ${result.executed.length} migration(s):`));
      result.executed.forEach((migration: string) => {
        console.log(chalk.gray(`   - ${migration}`));
      });
    }
  } else {
    console.error(chalk.red(`L ${action} failed ${formatDuration(result.duration)}`));
    if (result.error) {
      console.error(chalk.red(`   Error: ${result.error.message}`));
      if (process.env.DEBUG === 'true') {
        console.error(chalk.gray(result.error.stack));
      }
    }
  }
}

/**
 * Run migrations to latest version
 */
export async function migrateLatest(options: { environment?: string; dry?: boolean }): Promise<void> {
  const environment = options.environment || process.env.NODE_ENV || 'development';
  console.log(chalk.blue(`=� Running migrations to latest version [${environment}]`));

  // Test database connection first
  const isConnected = await testConnection();
  if (!isConnected) {
    console.error(chalk.red('L Failed to connect to database'));
    process.exit(1);
  }

  const runner = new MigrationRunner(environment);

  try {
    if (options.dry) {
      const status = await runner.getStatus();
      console.log(chalk.cyan('=� Migration Status (Dry Run):'));
      console.log(chalk.gray(`   Current version: ${status.current || 'None'}`));
      console.log(chalk.gray(`   Pending migrations: ${status.pending.length}`));

      if (status.pending.length > 0) {
        console.log(chalk.cyan('   Migrations that would be executed:'));
        status.pending.forEach(migration => {
          console.log(chalk.gray(`   - ${migration}`));
        });
      }
    } else {
      const result = await runner.migrateToLatest();
      printMigrationResult(result, 'Migration');

      if (!result.success) {
        process.exit(1);
      }
    }
  } finally {
    await runner.close();
  }
}

/**
 * Rollback migrations
 */
export async function rollback(options: {
  environment?: string;
  steps?: number;
  all?: boolean;
  version?: string;
}): Promise<void> {
  const environment = options.environment || process.env.NODE_ENV || 'development';

  let action = 'Rollback';
  if (options.all) {
    action = 'Complete rollback';
  } else if (options.version) {
    action = `Rollback to version ${options.version}`;
  } else if (options.steps && options.steps > 1) {
    action = `Rollback ${options.steps} batch(es)`;
  }

  console.log(chalk.blue(`= ${action} [${environment}]`));

  // Test database connection first
  const isConnected = await testConnection();
  if (!isConnected) {
    console.error(chalk.red('L Failed to connect to database'));
    process.exit(1);
  }

  const runner = new MigrationRunner(environment);

  try {
    let result;

    if (options.all) {
      // Confirm dangerous operation
      console.log(chalk.yellow('�  WARNING: This will rollback ALL migrations!'));
      result = await runner.rollbackAll();
    } else if (options.version) {
      result = await runner.rollbackToVersion(options.version);
    } else {
      result = await runner.rollback(options.steps || 1);
    }

    printMigrationResult(result, action);

    if (!result.success) {
      process.exit(1);
    }
  } finally {
    await runner.close();
  }
}

/**
 * Show migration status
 */
export async function status(options: { environment?: string; verbose?: boolean }): Promise<void> {
  const environment = options.environment || process.env.NODE_ENV || 'development';
  console.log(chalk.blue(`=� Migration Status [${environment}]`));

  // Test database connection first
  const isConnected = await testConnection();
  if (!isConnected) {
    console.error(chalk.red('L Failed to connect to database'));
    process.exit(1);
  }

  const runner = new MigrationRunner(environment);

  try {
    const status = await runner.getStatus();

    console.log(chalk.cyan('Current State:'));
    console.log(chalk.gray(`  Current version: ${status.current ? chalk.green(status.current) : chalk.yellow('No migrations run')}`));
    console.log(chalk.gray(`  Completed migrations: ${chalk.green(status.completed.length)}`));
    console.log(chalk.gray(`  Pending migrations: ${status.pending.length > 0 ? chalk.yellow(status.pending.length) : chalk.green('0')}`));

    if (options.verbose) {
      if (status.completed.length > 0) {
        console.log(chalk.cyan('\nCompleted Migrations:'));
        status.completed.forEach(migration => {
          console.log(chalk.green(`   ${migration}`));
        });
      }

      if (status.pending.length > 0) {
        console.log(chalk.cyan('\nPending Migrations:'));
        status.pending.forEach(migration => {
          console.log(chalk.yellow(`  � ${migration}`));
        });
      }
    } else if (status.pending.length > 0) {
      console.log(chalk.yellow('\n=� Run with --verbose to see detailed migration list'));
    }
  } finally {
    await runner.close();
  }
}

/**
 * Validate migrations
 */
export async function validate(options: { environment?: string }): Promise<void> {
  const environment = options.environment || process.env.NODE_ENV || 'development';
  console.log(chalk.blue(`= Validating Migrations [${environment}]`));

  // Test database connection first
  const isConnected = await testConnection();
  if (!isConnected) {
    console.error(chalk.red('L Failed to connect to database'));
    process.exit(1);
  }

  const runner = new MigrationRunner(environment);

  try {
    const isValid = await runner.validateMigrations();

    if (isValid) {
      console.log(chalk.green(' All migrations are valid'));
    } else {
      console.error(chalk.red('L Migration validation failed'));
      console.error(chalk.yellow('   Check the logs for details'));
      process.exit(1);
    }
  } finally {
    await runner.close();
  }
}

/**
 * Create a new migration file
 */
export async function create(name: string, options: { environment?: string }): Promise<void> {
  const environment = options.environment || process.env.NODE_ENV || 'development';

  if (!name) {
    console.error(chalk.red('L Migration name is required'));
    process.exit(1);
  }

  // Sanitize name (remove spaces, special chars except underscore)
  const sanitizedName = name.toLowerCase().replace(/[^a-z0-9_]/g, '_');

  console.log(chalk.blue(`=� Creating new migration: ${sanitizedName} [${environment}]`));

  const runner = new MigrationRunner(environment);

  try {
    const fileName = await runner.createMigration(sanitizedName);
    console.log(chalk.green(` Migration created: ${fileName}`));
    console.log(chalk.cyan('   Remember to implement the up() and down() functions'));
  } catch (error) {
    console.error(chalk.red('L Failed to create migration'));
    if (error instanceof Error) {
      console.error(chalk.red(`   Error: ${error.message}`));
    }
    process.exit(1);
  } finally {
    await runner.close();
  }
}

/**
 * Reset database (rollback all and migrate to latest)
 */
export async function reset(options: { environment?: string; force?: boolean }): Promise<void> {
  const environment = options.environment || process.env.NODE_ENV || 'development';

  if (!options.force && environment === 'production') {
    console.error(chalk.red('L Cannot reset production database without --force flag'));
    process.exit(1);
  }

  console.log(chalk.blue(`= Resetting Database [${environment}]`));
  console.log(chalk.yellow('�  WARNING: This will rollback ALL migrations and re-run them!'));

  // Test database connection first
  const isConnected = await testConnection();
  if (!isConnected) {
    console.error(chalk.red('L Failed to connect to database'));
    process.exit(1);
  }

  const runner = new MigrationRunner(environment);

  try {
    // Rollback all
    console.log(chalk.cyan('Step 1: Rolling back all migrations...'));
    const rollbackResult = await runner.rollbackAll();

    if (!rollbackResult.success) {
      console.error(chalk.red('L Rollback failed during reset'));
      if (rollbackResult.error) {
        console.error(chalk.red(`   Error: ${rollbackResult.error.message}`));
      }
      process.exit(1);
    }

    console.log(chalk.green(` Rolled back ${rollbackResult.executed.length} migration(s)`));

    // Migrate to latest
    console.log(chalk.cyan('Step 2: Running migrations to latest...'));
    const migrateResult = await runner.migrateToLatest();

    if (!migrateResult.success) {
      console.error(chalk.red('L Migration failed during reset'));
      if (migrateResult.error) {
        console.error(chalk.red(`   Error: ${migrateResult.error.message}`));
      }
      process.exit(1);
    }

    console.log(chalk.green(` Applied ${migrateResult.executed.length} migration(s)`));
    console.log(chalk.green(' Database reset completed successfully'));
  } finally {
    await runner.close();
  }
}