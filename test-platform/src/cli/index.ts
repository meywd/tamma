#!/usr/bin/env node

import { Command } from 'commander';
import dotenv from 'dotenv';
import chalk from 'chalk';
import {
  migrateLatest,
  rollback,
  status,
  validate,
  create,
  reset,
} from './migration-commands';

// Load environment variables
dotenv.config();

// Create CLI program
const program = new Command();

program
  .name('test-platform-cli')
  .description('Test Platform CLI - Database Migration Management')
  .version('1.0.0');

// Migrate command
program
  .command('migrate [target]')
  .description('Run database migrations')
  .option('-e, --env <environment>', 'Environment (development/test/staging/production)', process.env.NODE_ENV || 'development')
  .option('-d, --dry', 'Dry run - show what would be executed without making changes')
  .action(async (target, options) => {
    try {
      if (target === 'latest' || !target) {
        await migrateLatest({
          environment: options.env,
          dry: options.dry,
        });
      } else {
        console.error(chalk.red(`Unknown migration target: ${target}`));
        console.log(chalk.yellow('Available targets: latest'));
        process.exit(1);
      }
    } catch (error) {
      console.error(chalk.red('Migration failed:'), error);
      process.exit(1);
    }
  });

// Rollback command
program
  .command('rollback')
  .description('Rollback database migrations')
  .option('-e, --env <environment>', 'Environment (development/test/staging/production)', process.env.NODE_ENV || 'development')
  .option('-s, --steps <number>', 'Number of batches to rollback', '1')
  .option('-a, --all', 'Rollback all migrations')
  .option('-v, --version <version>', 'Rollback to specific version')
  .action(async (options) => {
    try {
      await rollback({
        environment: options.env,
        steps: parseInt(options.steps),
        all: options.all,
        version: options.version,
      });
    } catch (error) {
      console.error(chalk.red('Rollback failed:'), error);
      process.exit(1);
    }
  });

// Status command
program
  .command('status')
  .description('Show migration status')
  .option('-e, --env <environment>', 'Environment (development/test/staging/production)', process.env.NODE_ENV || 'development')
  .option('-v, --verbose', 'Show detailed migration list')
  .action(async (options) => {
    try {
      await status({
        environment: options.env,
        verbose: options.verbose,
      });
    } catch (error) {
      console.error(chalk.red('Status check failed:'), error);
      process.exit(1);
    }
  });

// Validate command
program
  .command('validate')
  .description('Validate migration files')
  .option('-e, --env <environment>', 'Environment (development/test/staging/production)', process.env.NODE_ENV || 'development')
  .action(async (options) => {
    try {
      await validate({
        environment: options.env,
      });
    } catch (error) {
      console.error(chalk.red('Validation failed:'), error);
      process.exit(1);
    }
  });

// Create command
program
  .command('create <name>')
  .description('Create a new migration file')
  .option('-e, --env <environment>', 'Environment (development/test/staging/production)', process.env.NODE_ENV || 'development')
  .action(async (name, options) => {
    try {
      await create(name, {
        environment: options.env,
      });
    } catch (error) {
      console.error(chalk.red('Migration creation failed:'), error);
      process.exit(1);
    }
  });

// Reset command
program
  .command('reset')
  .description('Reset database (rollback all and re-run migrations)')
  .option('-e, --env <environment>', 'Environment (development/test/staging/production)', process.env.NODE_ENV || 'development')
  .option('-f, --force', 'Force reset even in production')
  .action(async (options) => {
    try {
      await reset({
        environment: options.env,
        force: options.force,
      });
    } catch (error) {
      console.error(chalk.red('Reset failed:'), error);
      process.exit(1);
    }
  });

// Help command customization
program.on('--help', () => {
  console.log('');
  console.log('Examples:');
  console.log('  $ test-platform-cli migrate latest          # Run all pending migrations');
  console.log('  $ test-platform-cli rollback                # Rollback last batch');
  console.log('  $ test-platform-cli rollback --steps 3      # Rollback 3 batches');
  console.log('  $ test-platform-cli rollback --all          # Rollback all migrations');
  console.log('  $ test-platform-cli status                  # Show migration status');
  console.log('  $ test-platform-cli status --verbose        # Show detailed status');
  console.log('  $ test-platform-cli create add_users_table  # Create new migration');
  console.log('  $ test-platform-cli validate                # Validate migration files');
  console.log('  $ test-platform-cli reset                   # Reset database');
  console.log('');
  console.log('Environment Variables:');
  console.log('  NODE_ENV         - Environment (development/test/staging/production)');
  console.log('  DB_HOST          - Database host');
  console.log('  DB_PORT          - Database port');
  console.log('  DB_NAME          - Database name');
  console.log('  DB_USER          - Database user');
  console.log('  DB_PASSWORD      - Database password');
});

// Parse command line arguments
program.parse(process.argv);

// Show help if no command provided
if (!process.argv.slice(2).length) {
  program.outputHelp();
}